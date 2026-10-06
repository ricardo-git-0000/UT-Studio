using UTStudio.Contracts.Inspection;
using UTStudio.Domain.Acquisition;
using UTStudio.Domain.Inspection;

namespace UTStudio.Inspection.Core;

public sealed record InMemoryInspectionStoreOptions
{
    public InMemoryInspectionStoreOptions(long sampleCapacityBudgetBytes, int maximumEntries,
        int targetChunkBytes = 4 * 1024 * 1024)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(sampleCapacityBudgetBytes, 2);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumEntries, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(targetChunkBytes, 2);
        SampleCapacityBudgetBytes = sampleCapacityBudgetBytes;
        MaximumEntries = maximumEntries;
        TargetChunkBytes = targetChunkBytes;
    }
    public long SampleCapacityBudgetBytes { get; }
    public int MaximumEntries { get; }
    public int TargetChunkBytes { get; }
}

/// <summary>Authoritative, append-only phase-1 store. It has no durable backing.</summary>
public sealed class InMemoryInspectionDataStore : IInspectionDataStore
{
    private readonly object _gate = new();
    private readonly InspectionId _inspectionId;
    private readonly AcquisitionRunId _runId;
    private readonly InMemoryInspectionStoreOptions _options;
    private readonly SemaphoreSlim _writer = new(1, 1);
    private readonly Dictionary<TraceIdentity, Entry> _entries = [];
    private readonly Dictionary<UtStreamIdentity, SortedDictionary<ulong, Entry>> _streams = [];
    private readonly List<Chunk> _chunks = [];
    private Chunk? _activeChunk;
    private bool _accepting = true, _leasesAllowed = true, _closing, _closed;
    private int _admittedWriters, _maximumWriters, _activeLeases, _maximumLeases;
    private long _logicalBytes, _reservedSampleBytes, _ownedSampleBytes, _maximumOwnedSampleBytes;
    private ulong _revision, _cancelledAppends, _preAdmissionRejections, _duplicates, _conflicts,
        _capacityRejections;
    private TaskCompletionSource? _writersDrained, _leasesDrained;
    private Task? _disposeTask;

    public InMemoryInspectionDataStore(InspectionId inspectionId, AcquisitionRunId runId,
        InMemoryInspectionStoreOptions options)
    {
        if (!inspectionId.IsValid) { throw new ArgumentException("A valid inspection identifier is required.", nameof(inspectionId)); }
        if (!runId.IsValid) { throw new ArgumentException("A valid run identifier is required.", nameof(runId)); }
        ArgumentNullException.ThrowIfNull(options);
        _inspectionId = inspectionId;
        _runId = runId;
        _options = options;
    }

    public InspectionRevision Revision { get { lock (_gate) { return new(_revision); } } }

    public StoreDiagnostics Diagnostics
    {
        get
        {
            lock (_gate)
            {
                return new(_logicalBytes, _reservedSampleBytes, _ownedSampleBytes,
                    _maximumOwnedSampleBytes, _options.SampleCapacityBudgetBytes, _options.MaximumEntries,
                    _entries.Count, _admittedWriters, _maximumWriters,
                    _closing ? _admittedWriters : 0, _activeLeases, _maximumLeases,
                    _revision, _cancelledAppends, _preAdmissionRejections,
                    _duplicates, _conflicts, _capacityRejections,
                    _activeChunk is null ? 0 : 1, _chunks.Count(static c => c.Sealed), _closed);
            }
        }
    }

    internal InspectionStoreTestHooks? TestHooks { get; set; }

    public async ValueTask<InspectionAppendResult> AppendAsync(InspectionTraceInput trace,
        CancellationToken cancellationToken = default)
    {
        ValidateBoundary(trace);
        if (!TryAdmitWriter()) { return Result(InspectionAppendStatus.Closed, false); }
        try
        {
            TestHooks?.AfterWriterAdmitted?.Invoke();
            try { await _writer.WaitAsync(cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            { RecordCancellation(); return Result(InspectionAppendStatus.Cancelled, true); }
            try
            {
                if (cancellationToken.IsCancellationRequested)
                { RecordCancellation(); return Result(InspectionAppendStatus.Cancelled, true); }
                if (_entries.TryGetValue(trace.Descriptor.Identity, out Entry? existing))
                {
                    bool duplicate = DescriptorEquals(existing.Descriptor, trace.Descriptor) &&
                        existing.Samples.ReadOnlySpan.SequenceEqual(trace.Samples.Span);
                    lock (_gate) { if (duplicate) { _duplicates++; } else { _conflicts++; } }
                    return Result(duplicate ? InspectionAppendStatus.Duplicate : InspectionAppendStatus.Conflict, true);
                }

                int bytes;
                try { bytes = checked(trace.Descriptor.SampleCount * sizeof(short)); }
                catch (OverflowException) { lock (_gate) { _capacityRejections++; } return Result(InspectionAppendStatus.CapacityExceeded, true); }

                lock (_gate)
                {
                    if (_entries.Count >= _options.MaximumEntries ||
                        bytes > _options.SampleCapacityBudgetBytes - _ownedSampleBytes - _reservedSampleBytes)
                    { _capacityRejections++; return ResultUnderLock(InspectionAppendStatus.CapacityExceeded, true); }
                    _reservedSampleBytes = checked(_reservedSampleBytes + bytes);
                }

                short[]? copy = null;
                bool materialized = false, committed = false;
                try
                {
                    TestHooks?.BeforeCopy?.Invoke();
                    copy = TestHooks?.Allocate?.Invoke(trace.Descriptor.SampleCount) ?? new short[trace.Descriptor.SampleCount];
                    if (copy.Length != trace.Descriptor.SampleCount) { throw new InvalidOperationException("Allocator returned an unexpected capacity."); }
                    lock (_gate)
                    {
                        _reservedSampleBytes -= bytes;
                        _ownedSampleBytes = checked(_ownedSampleBytes + bytes);
                        _maximumOwnedSampleBytes = Math.Max(_maximumOwnedSampleBytes, _ownedSampleBytes);
                        materialized = true;
                    }
                    if (TestHooks?.Copy is { } copyAction) { copyAction(trace.Samples, copy); }
                    else { trace.Samples.Span.CopyTo(copy); }
                    TestHooks?.AfterCopy?.Invoke();
                    cancellationToken.ThrowIfCancellationRequested();
                    TestHooks?.BeforeCommit?.Invoke();

                    lock (_gate)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var owner = new ImmutableShortMemory(copy);
                        Entry? entry = null;
                        Chunk? priorActive = _activeChunk;
                        int priorChunkCount = _chunks.Count;
                        bool primaryAdded = false, streamCreated = false, streamAdded = false,
                            chunkAdded = false, logicalAdded = false;
                        try
                        {
                            EnsureChunkUnderLock(trace.Descriptor, bytes);
                            entry = new Entry(trace.Descriptor, owner, _activeChunk!);
                            if (!_streams.TryGetValue(trace.Descriptor.Identity.Stream, out var stream))
                            { stream = []; }
                            _entries.Add(trace.Descriptor.Identity, entry); primaryAdded = true;
                            TestHooks?.AfterPrimaryIndex?.Invoke();
                            if (!_streams.ContainsKey(trace.Descriptor.Identity.Stream))
                            { _streams.Add(trace.Descriptor.Identity.Stream, stream); streamCreated = true; }
                            stream.Add(trace.Descriptor.Identity.AcquisitionIndex, entry); streamAdded = true;
                            TestHooks?.AfterStreamIndex?.Invoke();
                            _activeChunk!.Entries.Add(entry); chunkAdded = true;
                            _activeChunk.UsedBytes = checked(_activeChunk.UsedBytes + bytes);
                            _logicalBytes = checked(_logicalBytes + bytes); logicalAdded = true;
                            _revision = checked(_revision + 1);
                            committed = true; // linearization point: all indexes and revision are visible together.
                        }
                        catch
                        {
                            if (logicalAdded) { _logicalBytes -= bytes; }
                            if (chunkAdded) { _activeChunk!.Entries.Remove(entry!); _activeChunk.UsedBytes -= bytes; }
                            if (_streams.TryGetValue(trace.Descriptor.Identity.Stream, out var rollbackStream))
                            {
                                if (streamAdded) { rollbackStream.Remove(trace.Descriptor.Identity.AcquisitionIndex); }
                                if (streamCreated && rollbackStream.Count == 0) { _streams.Remove(trace.Descriptor.Identity.Stream); }
                            }
                            if (primaryAdded) { _entries.Remove(trace.Descriptor.Identity); }
                            if (_chunks.Count > priorChunkCount && _activeChunk is { Entries.Count: 0 })
                            { _chunks.Remove(_activeChunk); _activeChunk = priorActive; if (priorActive is not null) { priorActive.Sealed = false; } }
                            ((IDisposable)owner).Dispose();
                            throw;
                        }
                    }
                    TestHooks?.AfterCommit?.Invoke();
                    return Result(InspectionAppendStatus.Committed, true);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested && !committed)
                { RecordCancellation(); return Result(InspectionAppendStatus.Cancelled, true); }
                catch (Exception error) when (!committed && error is not ArgumentException)
                { return new(InspectionAppendStatus.Faulted, Revision, true, "append-failed"); }
                catch (Exception) when (committed)
                { return Result(InspectionAppendStatus.Committed, true); }
                finally
                {
                    if (!committed)
                    {
                        lock (_gate)
                        {
                            if (materialized) { _ownedSampleBytes -= bytes; }
                            else { _reservedSampleBytes -= bytes; }
                        }
                    }
                }
            }
            finally { _writer.Release(); }
        }
        finally { ReleaseWriterAdmission(); }
    }

    public ValueTask<InspectionTraceReadResult> ReadAsync(TraceIdentity identity,
        CancellationToken cancellationToken = default)
    {
        if (!identity.IsValid) { throw new ArgumentException("A valid identity is required.", nameof(identity)); }
        if (cancellationToken.IsCancellationRequested)
        { return ValueTask.FromResult(new InspectionTraceReadResult(InspectionReadStatus.Cancelled, Revision)); }
        lock (_gate)
        {
            if (!_leasesAllowed) { return ValueTask.FromResult(new InspectionTraceReadResult(InspectionReadStatus.Closed, new(_revision))); }
            if (!_entries.TryGetValue(identity, out Entry? entry))
            { return ValueTask.FromResult(new InspectionTraceReadResult(InspectionReadStatus.NotAvailable, new(_revision))); }
            return ValueTask.FromResult(new InspectionTraceReadResult(InspectionReadStatus.Available,
                new(_revision), CreateLeaseUnderLock(entry)));
        }
    }

    public ValueTask<InspectionRangeReadResult> ReadRangeAsync(InspectionRangeQuery query,
        CancellationToken cancellationToken = default)
    {
        if (!query.IsValid) { throw new ArgumentException("A valid bounded range query is required.", nameof(query)); }
        if (cancellationToken.IsCancellationRequested)
        { return ValueTask.FromResult(new InspectionRangeReadResult(InspectionReadStatus.Cancelled, Revision, [])); }
        lock (_gate)
        {
            var revision = new InspectionRevision(_revision);
            if (!_leasesAllowed) { return ValueTask.FromResult(new InspectionRangeReadResult(InspectionReadStatus.Closed, revision, [])); }
            var items = new List<InspectionRangeItem>(query.MaximumCount);
            var acquired = new List<Lease>();
            try
            {
                _streams.TryGetValue(query.Stream, out var stream);
                for (int offset = 0; offset < query.MaximumCount; offset++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    TestHooks?.BeforeRangeLease?.Invoke(offset);
                    ulong index = checked(query.StartAcquisitionIndex + (ulong)offset);
                    if (stream is not null && stream.TryGetValue(index, out Entry? entry))
                    {
                        Lease? unownedLease = CreateLeaseUnderLock(entry);
                        bool transferred = false;
                        try
                        {
                            TestHooks?.AfterRangeLeaseCreated?.Invoke(offset);
                            acquired.Add(unownedLease);
                            transferred = true;
                        }
                        finally
                        {
                            if (!transferred) { unownedLease.Dispose(); }
                        }
                        Lease lease = unownedLease;
                        items.Add(new(entry.Descriptor.Identity, InspectionReadStatus.Available, lease));
                    }
                    else
                    { items.Add(new(new TraceIdentity(query.Stream, index), InspectionReadStatus.NotAvailable)); }
                }
                return ValueTask.FromResult(new InspectionRangeReadResult(
                    items.Any(static item => item.Status == InspectionReadStatus.Available)
                        ? InspectionReadStatus.Available : InspectionReadStatus.NotAvailable,
                    revision, items));
            }
            catch (Exception error)
            {
                var cleanupErrors = new List<Exception>();
                for (int index = acquired.Count - 1; index >= 0; index--)
                {
                    try { TestHooks?.BeforeRangeRollbackDispose?.Invoke(index); }
                    catch (Exception cleanupError) { cleanupErrors.Add(cleanupError); }
                    finally { acquired[index].Dispose(); }
                }
                if (error is OperationCanceledException && cleanupErrors.Count == 0)
                { return ValueTask.FromResult(new InspectionRangeReadResult(InspectionReadStatus.Cancelled, revision, [])); }
                throw new InspectionRangeAcquisitionException(error, cleanupErrors);
            }
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (_gate) { _disposeTask ??= DisposeCoreAsync(); return new(_disposeTask); }
    }

    private async Task DisposeCoreAsync()
    {
        Task writers;
        lock (_gate)
        {
            _accepting = false;
            _closing = true;
            writers = _admittedWriters == 0 ? Task.CompletedTask : (_writersDrained ??= NewSignal()).Task;
        }
        await writers.ConfigureAwait(false);
        lock (_gate)
        {
            SealActiveChunkUnderLock();
            _leasesAllowed = false;
        }
        Task leases;
        lock (_gate) { leases = _activeLeases == 0 ? Task.CompletedTask : (_leasesDrained ??= NewSignal()).Task; }
        await leases.ConfigureAwait(false);
        try { TestHooks?.BeforeCleanup?.Invoke(); }
        catch (Exception error)
        { throw new InspectionStoreCloseException("cleanup-failed", [], error); }
        lock (_gate)
        {
            foreach (Entry entry in _entries.Values) { ((IDisposable)entry.Samples).Dispose(); }
            _entries.Clear(); _streams.Clear(); _chunks.Clear(); _activeChunk = null;
            _logicalBytes = 0; _reservedSampleBytes = 0; _ownedSampleBytes = 0;
            _closing = false; _closed = true;
        }
    }

    private bool TryAdmitWriter()
    {
        lock (_gate)
        {
            if (!_accepting) { _preAdmissionRejections++; return false; }
            _admittedWriters++;
            _maximumWriters = Math.Max(_maximumWriters, _admittedWriters);
            return true;
        }
    }

    private void ReleaseWriterAdmission()
    {
        TaskCompletionSource? signal = null;
        lock (_gate) { _admittedWriters--; if (_admittedWriters == 0) { signal = _writersDrained; } }
        signal?.TrySetResult();
    }

    private void ValidateBoundary(InspectionTraceInput trace)
    {
        if (trace.Descriptor is null) { throw new ArgumentException("A descriptor is required.", nameof(trace)); }
        TraceIdentity identity = trace.Descriptor.Identity;
        if (identity.Stream.InspectionId != _inspectionId || identity.Stream.AcquisitionRunId != _runId)
        { throw new ArgumentException("The trace does not belong to this store inspection/run.", nameof(trace)); }
        if (trace.Samples.Length != trace.Descriptor.SampleCount)
        { throw new ArgumentException("The sample memory length must equal SampleCount.", nameof(trace)); }
    }

    private void EnsureChunkUnderLock(TraceDescriptor descriptor, int bytes)
    {
        bool incompatible = _activeChunk is not null && !_activeChunk.IsCompatible(descriptor);
        bool full = _activeChunk is not null && bytes > _options.TargetChunkBytes - _activeChunk.UsedBytes;
        if (_activeChunk is null || incompatible || full)
        {
            var candidate = new Chunk(bytes > _options.TargetChunkBytes, descriptor);
            TestHooks?.BeforeChunkAdd?.Invoke();
            _chunks.Add(candidate); // no state changes if allocation/list growth fails.
            if (_activeChunk is not null) { _activeChunk.Sealed = true; }
            _activeChunk = candidate;
        }
    }

    private void SealActiveChunkUnderLock()
    {
        if (_activeChunk is null) { return; }
        _activeChunk.Sealed = true;
        _activeChunk = null;
    }

    private Lease CreateLeaseUnderLock(Entry entry)
    {
        var lease = new Lease(this, entry);
        _activeLeases++;
        _maximumLeases = Math.Max(_maximumLeases, _activeLeases);
        return lease;
    }

    private void ReleaseLease()
    {
        TaskCompletionSource? signal = null;
        lock (_gate) { _activeLeases--; if (_activeLeases == 0) { signal = _leasesDrained; } }
        signal?.TrySetResult();
    }

    private InspectionAppendResult Result(InspectionAppendStatus status, bool wasAccepted)
    { lock (_gate) { return ResultUnderLock(status, wasAccepted); } }
    private InspectionAppendResult ResultUnderLock(InspectionAppendStatus status, bool wasAccepted) =>
        new(status, new(_revision), wasAccepted);
    private void RecordCancellation() { lock (_gate) { _cancelledAppends++; } }
    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static bool DescriptorEquals(TraceDescriptor left, TraceDescriptor right) =>
        left.Identity == right.Identity && left.Sequence == right.Sequence &&
        left.ElapsedSinceRunStart == right.ElapsedSinceRunStart && left.TimestampUtc == right.TimestampUtc &&
        left.SpatialCoordinate == right.SpatialCoordinate && left.SampleCount == right.SampleCount &&
        left.SampleRateHz.Equals(right.SampleRateHz) &&
        left.FirstSampleOffsetSeconds.Equals(right.FirstSampleOffsetSeconds) &&
        left.SignalMode == right.SignalMode && left.ClockQuality == right.ClockQuality &&
        left.SampleEncoding == right.SampleEncoding && left.Provenance == right.Provenance &&
        left.TraceQuality == right.TraceQuality;

    private sealed class Entry(TraceDescriptor descriptor, ImmutableShortMemory samples, Chunk chunk)
    { internal TraceDescriptor Descriptor { get; } = descriptor; internal ImmutableShortMemory Samples { get; } = samples; internal Chunk Chunk { get; } = chunk; }

    private sealed class Chunk(bool dedicated, TraceDescriptor descriptor)
    {
        internal bool Dedicated { get; } = dedicated;
        internal UtStreamIdentity Stream { get; } = descriptor.Identity.Stream;
        internal int SampleCount { get; } = descriptor.SampleCount;
        internal double SampleRateHz { get; } = descriptor.SampleRateHz;
        internal double Offset { get; } = descriptor.FirstSampleOffsetSeconds;
        internal UtSignalMode Mode { get; } = descriptor.SignalMode;
        internal List<Entry> Entries { get; } = [];
        internal int UsedBytes { get; set; }
        internal bool Sealed { get; set; }
        internal bool IsCompatible(TraceDescriptor value) => !Dedicated && Stream == value.Identity.Stream &&
            SampleCount == value.SampleCount && SampleRateHz.Equals(value.SampleRateHz) &&
            Offset.Equals(value.FirstSampleOffsetSeconds) && Mode == value.SignalMode &&
            descriptor.SampleEncoding == value.SampleEncoding;
    }

    private sealed class Lease(InMemoryInspectionDataStore owner, Entry entry) : IInspectionTraceLease
    {
        private readonly object _leaseGate = new();
        private InMemoryInspectionDataStore? _owner = owner;
        public TraceDescriptor Descriptor { get { lock (_leaseGate) { EnsureAlive(); return entry.Descriptor; } } }
        public IReadOnlyUtSamples Samples { get { lock (_leaseGate) { EnsureAlive(); return new SampleView(this); } } }
        public void Dispose()
        {
            InMemoryInspectionDataStore? released;
            lock (_leaseGate) { released = _owner; _owner = null; }
            released?.ReleaseLease();
        }
        private void EnsureAlive() { if (_owner is null) { throw new ObjectDisposedException(nameof(Lease)); } }
        private T Read<T>(Func<ImmutableShortMemory, T> read)
        { lock (_leaseGate) { EnsureAlive(); _owner!.TestHooks?.BeforeLeaseRead?.Invoke(); return read(entry.Samples); } }
        private void Copy(Span<short> destination)
        { lock (_leaseGate) { EnsureAlive(); _owner!.TestHooks?.BeforeLeaseRead?.Invoke(); entry.Samples.CopyTo(destination); } }
        private sealed class SampleView(Lease lease) : IReadOnlyUtSamples
        {
            public int Count => lease.Read(static value => value.Count);
            public short this[int index] => lease.Read(value => value[index]);
            public void CopyTo(Span<short> destination) => lease.Copy(destination);
        }
    }

    private sealed class ImmutableShortMemory(short[] array) : IDisposable
    {
        private short[]? _array = array;
        internal int Count => (_array ?? throw new ObjectDisposedException(nameof(ImmutableShortMemory))).Length;
        internal short this[int index] => (_array ?? throw new ObjectDisposedException(nameof(ImmutableShortMemory)))[index];
        internal ReadOnlySpan<short> ReadOnlySpan => (_array ?? throw new ObjectDisposedException(nameof(ImmutableShortMemory))).AsSpan();
        internal void CopyTo(Span<short> destination)
        {
            if (destination.Length < Count) { throw new ArgumentException("Destination is too short.", nameof(destination)); }
            ReadOnlySpan.CopyTo(destination);
        }
        public void Dispose() => _array = null;
    }
}

internal sealed class InspectionStoreTestHooks
{
    internal Func<int, short[]>? Allocate { get; init; }
    internal Action<ReadOnlyMemory<short>, short[]>? Copy { get; init; }
    internal Action? BeforeCopy { get; init; }
    internal Action? AfterWriterAdmitted { get; init; }
    internal Action? AfterCopy { get; init; }
    internal Action? BeforeCommit { get; init; }
    internal Action? BeforeChunkAdd { get; init; }
    internal Action? AfterPrimaryIndex { get; init; }
    internal Action? AfterStreamIndex { get; init; }
    internal Action? AfterCommit { get; init; }
    internal Action? BeforeCleanup { get; init; }
    internal Action<int>? BeforeRangeLease { get; init; }
    internal Action<int>? AfterRangeLeaseCreated { get; init; }
    internal Action<int>? BeforeRangeRollbackDispose { get; init; }
    internal Action? BeforeLeaseRead { get; init; }
}
