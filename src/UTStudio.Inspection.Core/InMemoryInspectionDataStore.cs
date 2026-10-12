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

/// <summary>Authoritative, append-only in-memory store. It has no durable backing.</summary>
public sealed class InMemoryInspectionDataStore : IInspectionDataStore
{
    private readonly object _gate = new();
    private readonly InspectionId _inspectionId;
    private readonly AcquisitionRunId? _runId;
    private readonly InMemoryInspectionStoreOptions _options;
    private readonly IInspectionSampleMaterializer _sampleMaterializer;
    private readonly IInspectionOwnerReleaser _ownerReleaser;
    private readonly IInspectionTraceIndex _index;
    private IInspectionSealResultFactory _sealResultFactory = DefaultInspectionSealResultFactory.Instance;
    private readonly SemaphoreSlim _writer = new(1, 1);
    private readonly SemaphoreSlim _streamStateAdmission = new(1, 1);
    private readonly Dictionary<GapKey, GapRecord> _gaps = [];
    private readonly Dictionary<UtStreamIdentity, int> _gapCountsByStream = [];
    private readonly Dictionary<AcquisitionRunId, RunState> _runs = [];
    private readonly List<Chunk> _chunks = [];
    private Chunk? _activeChunk;
    private bool _accepting = true, _leasesAllowed = true, _closing, _closed;
    private int _admittedWriters, _maximumWriters, _activeLeases, _maximumLeases;
    private long _logicalBytes, _reservedSampleBytes, _ownedSampleBytes, _maximumOwnedSampleBytes;
    private ulong _revision, _committedTraces, _cancelledAppends, _preAdmissionRejections, _duplicates, _conflicts,
        _capacityRejections;
    private long _streamStateReadsCompleted, _streamStateReadFaults;
    private string? _lastStreamStateReadFaultType;
    private TaskCompletionSource? _writersDrained, _leasesDrained;
    private Task? _disposeTask;

    public InMemoryInspectionDataStore(InspectionId inspectionId, AcquisitionRunId runId,
        InMemoryInspectionStoreOptions options)
        : this(inspectionId, options)
    {
        if (!runId.IsValid) { throw new ArgumentException("A valid run identifier is required.", nameof(runId)); }
        _runId = runId;
    }

    internal InMemoryInspectionDataStore(InspectionId inspectionId, AcquisitionRunId runId,
        InMemoryInspectionStoreOptions options, IInspectionSampleMaterializer sampleMaterializer,
        IInspectionOwnerReleaser ownerReleaser)
        : this(inspectionId, options, sampleMaterializer, ownerReleaser)
    {
        if (!runId.IsValid) { throw new ArgumentException("A valid run identifier is required.", nameof(runId)); }
        _runId = runId;
    }

    internal InMemoryInspectionDataStore(InspectionId inspectionId, AcquisitionRunId runId,
        InMemoryInspectionStoreOptions options, IInspectionSampleMaterializer sampleMaterializer,
        IInspectionOwnerReleaser ownerReleaser, IInspectionTraceIndex index)
        : this(inspectionId, options, sampleMaterializer, ownerReleaser, index)
    {
        if (!runId.IsValid) { throw new ArgumentException("A valid run identifier is required.", nameof(runId)); }
        _runId = runId;
    }

    /// <summary>Creates one inspection-wide store that accepts successive, non-overlapping runs.</summary>
    public InMemoryInspectionDataStore(InspectionId inspectionId, InMemoryInspectionStoreOptions options)
        : this(inspectionId, options, DefaultInspectionSampleMaterializer.Instance,
            DefaultInspectionOwnerReleaser.Instance)
    {
    }

    internal InMemoryInspectionDataStore(InspectionId inspectionId, InMemoryInspectionStoreOptions options,
        IInspectionSampleMaterializer sampleMaterializer, IInspectionOwnerReleaser ownerReleaser)
        : this(inspectionId, options, sampleMaterializer, ownerReleaser, new DefaultInspectionTraceIndex())
    {
    }

    internal InMemoryInspectionDataStore(InspectionId inspectionId, InMemoryInspectionStoreOptions options,
        IInspectionSampleMaterializer sampleMaterializer, IInspectionOwnerReleaser ownerReleaser,
        IInspectionTraceIndex index)
    {
        if (!inspectionId.IsValid) { throw new ArgumentException("A valid inspection identifier is required.", nameof(inspectionId)); }
        ArgumentNullException.ThrowIfNull(options);
        _inspectionId = inspectionId;
        _options = options;
        _sampleMaterializer = sampleMaterializer ?? throw new ArgumentNullException(nameof(sampleMaterializer));
        _ownerReleaser = ownerReleaser ?? throw new ArgumentNullException(nameof(ownerReleaser));
        _index = index ?? throw new ArgumentNullException(nameof(index));
    }

    public InspectionRevision Revision { get { lock (_gate) { return new(_revision); } } }

    public StoreDiagnostics Diagnostics
    {
        get
        {
            lock (_gate)
            {
                var runs = _runs.Values.Select(static run => run.Diagnostics()).ToArray();
                return new(_logicalBytes, _reservedSampleBytes, _ownedSampleBytes,
                    _maximumOwnedSampleBytes, _options.SampleCapacityBudgetBytes, _options.MaximumEntries,
                    _index.Count, _admittedWriters, _maximumWriters,
                    _closing ? _admittedWriters : 0, _activeLeases, _maximumLeases,
                    _committedTraces, _cancelledAppends, _preAdmissionRejections,
                    _duplicates, _conflicts, _capacityRejections,
                    _activeChunk is null ? 0 : 1, _chunks.Count(static c => c.Sealed), _closed,
                    runs.Count(static run => run.State == InspectionRunState.Open),
                    runs.Count(static run => run.State == InspectionRunState.Sealing),
                    runs.Count(static run => run.State == InspectionRunState.Sealed),
                    runs.Count(static run => run.State == InspectionRunState.Open), _gaps.Count,
                    runs.Count(static run => run.State == InspectionRunState.Sealing), Array.AsReadOnly(runs));
            }
        }
    }

    internal InspectionStoreTestHooks? TestHooks { get; set; }
    internal IInspectionSealResultFactory SealResultFactory
    {
        get => _sealResultFactory;
        set => _sealResultFactory = value ?? throw new ArgumentNullException(nameof(value));
    }

    public ValueTask<IInspectionRunWriter> OpenRunAsync(AcquisitionRunId runId,
        CancellationToken cancellationToken = default)
    {
        if (!runId.IsValid) { throw new ArgumentException("A valid run identifier is required.", nameof(runId)); }
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(!_accepting, this);
            if (_runs.Values.Any(static run => run.State != InspectionRunState.Sealed))
            { throw new InvalidOperationException("Another run is open or sealing."); }
            if (_runs.ContainsKey(runId))
            { throw new InvalidOperationException("The run is already open, sealing, or sealed."); }
            var writer = new RunWriter(this, runId);
            _runs.Add(runId, new RunState(runId, writer));
            return ValueTask.FromResult<IInspectionRunWriter>(writer);
        }
    }

    public ValueTask<IReadOnlyList<GapRecord>> ReadGapsAsync(UtStreamIdentity stream, int maximumCount,
        CancellationToken cancellationToken = default)
    {
        if (!stream.IsValid) { throw new ArgumentException("A valid stream is required.", nameof(stream)); }
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumCount, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maximumCount, 4096);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(!_leasesAllowed, this);
            IReadOnlyList<GapRecord> result = Array.AsReadOnly(_gaps.Values
                .Where(gap => gap.Stream == stream).OrderBy(gap => gap.FirstMissingSequence)
                .Take(maximumCount).ToArray());
            return ValueTask.FromResult(result);
        }
    }

    public async ValueTask<InspectionAppendResult> AppendAsync(InspectionTraceInput trace,
        CancellationToken cancellationToken = default)
    {
        ValidateBoundary(trace);
        if (_runId is null) { return Result(InspectionAppendStatus.Closed, false); }
        return await CommitCoreAsync(new InspectionCommitInput(trace), null, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<InspectionAppendResult> CommitCoreAsync(InspectionCommitInput commit,
        RunWriter? runWriter, CancellationToken cancellationToken)
    {
        InspectionTraceInput trace = commit.Trace;
        ValidateBoundary(trace);
        if (runWriter is not null && !TryAdmitRunOperation(runWriter))
        { return Result(InspectionAppendStatus.Closed, false); }
        if (!TryAdmitWriter())
        {
            if (runWriter is not null) { ReleaseRunOperation(runWriter); }
            return Result(InspectionAppendStatus.Closed, false);
        }
        try
        {
            TestHooks?.AfterWriterAdmitted?.Invoke();
            try { await _writer.WaitAsync(cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            { RecordCancellation(runWriter); return Result(InspectionAppendStatus.Cancelled, true); }
            try
            {
                if (cancellationToken.IsCancellationRequested)
                { RecordCancellation(runWriter); return Result(InspectionAppendStatus.Cancelled, true); }
                if (_index.TryGetPrimary(trace.Descriptor.Identity, out Entry? existing))
                {
                    bool duplicate = DescriptorEquals(existing!.Descriptor, trace.Descriptor) &&
                        existing.Samples.ReadOnlySpan.SequenceEqual(trace.Samples.Span) &&
                        existing.Gaps.Count == commit.Gaps.Count &&
                        commit.Gaps.All(gap => existing.Gaps.Contains(GapKey.From(gap)) &&
                            _gaps.TryGetValue(GapKey.From(gap), out GapRecord stored) &&
                            GapEquivalent(stored, gap));
                    lock (_gate)
                    {
                        if (duplicate) { _duplicates++; if (runWriter is not null) { _runs[runWriter.RunId].Duplicates++; } }
                        else { _conflicts++; if (runWriter is not null) { _runs[runWriter.RunId].Conflicts++; } }
                    }
                    return Result(duplicate ? InspectionAppendStatus.Duplicate : InspectionAppendStatus.Conflict, true);
                }

                int bytes;
                try { bytes = checked(trace.Descriptor.SampleCount * sizeof(short)); }
                catch (OverflowException) { lock (_gate) { _capacityRejections++; } return Result(InspectionAppendStatus.CapacityExceeded, true); }

                lock (_gate)
                {
                    if (!HasEntryCapacityUnderLock(checked(1 + commit.Gaps.Count)) ||
                        bytes > _options.SampleCapacityBudgetBytes - _ownedSampleBytes - _reservedSampleBytes)
                    {
                        _capacityRejections++;
                        if (runWriter is not null) { _runs[runWriter.RunId].Rejections++; }
                        return ResultUnderLock(InspectionAppendStatus.CapacityExceeded, true);
                    }
                    _reservedSampleBytes = checked(_reservedSampleBytes + bytes);
                }
                short[]? copy = null;
                bool materialized = false, committed = false;
                try
                {
                    TestHooks?.AfterCapacityReserved?.Invoke();
                    TestHooks?.BeforeCopy?.Invoke();
                    copy = TestHooks?.Allocate?.Invoke(trace.Descriptor.SampleCount) ??
                        _sampleMaterializer.Materialize(trace.Samples, trace.Descriptor.SampleCount);
                    if (copy.Length != trace.Descriptor.SampleCount) { throw new InvalidOperationException("Allocator returned an unexpected capacity."); }
                    lock (_gate)
                    {
                        _reservedSampleBytes -= bytes;
                        _ownedSampleBytes = checked(_ownedSampleBytes + bytes);
                        _maximumOwnedSampleBytes = Math.Max(_maximumOwnedSampleBytes, _ownedSampleBytes);
                        materialized = true;
                    }
                    if (TestHooks?.Copy is { } copyAction) { copyAction(trace.Samples, copy); }
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
                        var addedGaps = new List<GapKey>();
                        try
                        {
                            EnsureChunkUnderLock(trace.Descriptor, bytes);
                            entry = new Entry(trace.Descriptor, owner, _activeChunk!,
                                commit.Gaps.Select(GapKey.From).ToArray());
                            TestHooks?.AfterTracePrepared?.Invoke();
                            for (int gapIndex = 0; gapIndex < commit.Gaps.Count; gapIndex++)
                            {
                                GapRecord gap = commit.Gaps[gapIndex];
                                var key = GapKey.From(gap);
                                if (_gaps.TryGetValue(key, out GapRecord stored))
                                {
                                    throw new GapConflictException();
                                }
                                _gaps.Add(key, gap with { ConfirmedRevision = new(_revision + 1) });
                                IncrementGapCountUnderLock(gap.Stream);
                                addedGaps.Add(key);
                                TestHooks?.AfterGapPrepared?.Invoke(gapIndex);
                            }
                            if (!_index.TryGetStream(trace.Descriptor.Identity.Stream, out var stream))
                            { stream = []; }
                            _index.AddPrimary(trace.Descriptor.Identity, entry); primaryAdded = true;
                            TestHooks?.AfterPrimaryIndex?.Invoke();
                            if (!_index.ContainsStream(trace.Descriptor.Identity.Stream))
                            { _index.AddStream(trace.Descriptor.Identity.Stream, stream!); streamCreated = true; }
                            stream!.Add(trace.Descriptor.Identity.AcquisitionIndex, entry); streamAdded = true;
                            TestHooks?.AfterStreamIndex?.Invoke();
                            _activeChunk!.Entries.Add(entry); chunkAdded = true;
                            _activeChunk.UsedBytes = checked(_activeChunk.UsedBytes + bytes);
                            _logicalBytes = checked(_logicalBytes + bytes); logicalAdded = true;
                            TestHooks?.BeforeRevisionPublished?.Invoke();
                            _revision = checked(_revision + 1);
                            if (runWriter is not null)
                            { _runs[runWriter.RunId].RecordCommit(trace.Descriptor, addedGaps.Count, new(_revision)); }
                            _committedTraces = checked(_committedTraces + 1);
                            committed = true; // linearization point: all indexes and revision are visible together.
                        }
                        catch (Exception primary)
                        {
                            if (logicalAdded) { _logicalBytes -= bytes; }
                            if (chunkAdded) { _activeChunk!.Entries.Remove(entry!); _activeChunk.UsedBytes -= bytes; }
                            if (_index.TryGetStream(trace.Descriptor.Identity.Stream, out var rollbackStream))
                            {
                                if (streamAdded) { rollbackStream!.Remove(trace.Descriptor.Identity.AcquisitionIndex); }
                                if (streamCreated && rollbackStream!.Count == 0) { _index.RemoveStream(trace.Descriptor.Identity.Stream); }
                            }
                            if (primaryAdded) { _index.RemovePrimary(trace.Descriptor.Identity); }
                            foreach (GapKey gap in addedGaps)
                            {
                                _gaps.Remove(gap);
                                DecrementGapCountUnderLock(gap.Stream);
                            }
                            if (_chunks.Count > priorChunkCount && _activeChunk is { Entries.Count: 0 })
                            { _chunks.Remove(_activeChunk); _activeChunk = priorActive; if (priorActive is not null) { priorActive.Sealed = false; } }
                            try { _ownerReleaser.Release(owner); }
                            catch (Exception releaseFailure)
                            {
                                try { owner.Dispose(); }
                                catch (Exception fallbackFailure)
                                { throw new AggregateException(primary, releaseFailure, fallbackFailure); }
                                throw new AggregateException(primary, releaseFailure);
                            }
                            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(primary).Throw();
                            throw;
                        }
                    }
                    TestHooks?.AfterCommit?.Invoke();
                    return Result(InspectionAppendStatus.Committed, true);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested && !committed)
                { RecordCancellation(runWriter); return Result(InspectionAppendStatus.Cancelled, true); }
                catch (GapConflictException) when (!committed)
                {
                    lock (_gate)
                    {
                        _conflicts++;
                        if (runWriter is not null) { _runs[runWriter.RunId].Conflicts++; }
                    }
                    return Result(InspectionAppendStatus.Conflict, true);
                }
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
        finally
        {
            ReleaseWriterAdmission();
            if (runWriter is not null) { ReleaseRunOperation(runWriter); }
        }
    }

    private async ValueTask<InspectionAppendResult> CommitGapCoreAsync(GapRecord gap,
        RunWriter runWriter, CancellationToken cancellationToken)
    {
        if (!gap.IsValid || gap.Stream.InspectionId != _inspectionId ||
            gap.Stream.AcquisitionRunId != runWriter.RunId)
        { throw new ArgumentException("The gap does not belong to this store inspection/run.", nameof(gap)); }
        if (gap.ConfirmedRevision != InspectionRevision.Initial)
        { throw new ArgumentException("A new gap cannot already be confirmed.", nameof(gap)); }
        if (!TryAdmitRunOperation(runWriter)) { return Result(InspectionAppendStatus.Closed, false); }
        if (!TryAdmitWriter())
        {
            ReleaseRunOperation(runWriter);
            return Result(InspectionAppendStatus.Closed, false);
        }
        try
        {
            TestHooks?.AfterWriterAdmitted?.Invoke();
            try { await _writer.WaitAsync(cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            { RecordCancellation(runWriter); return Result(InspectionAppendStatus.Cancelled, true); }
            try
            {
                bool committed = false;
                if (cancellationToken.IsCancellationRequested)
                { RecordCancellation(runWriter); return Result(InspectionAppendStatus.Cancelled, true); }
                var key = GapKey.From(gap);
                lock (_gate)
                {
                    if (_gaps.TryGetValue(key, out GapRecord existing))
                    {
                        bool duplicate = GapEquivalent(existing, gap);
                        if (duplicate) { _duplicates++; _runs[runWriter.RunId].Duplicates++; }
                        else { _conflicts++; _runs[runWriter.RunId].Conflicts++; }
                        return ResultUnderLock(duplicate ? InspectionAppendStatus.Duplicate : InspectionAppendStatus.Conflict, true);
                    }
                    if (!HasEntryCapacityUnderLock(1))
                    {
                        _capacityRejections++;
                        _runs[runWriter.RunId].Rejections++;
                        return ResultUnderLock(InspectionAppendStatus.CapacityExceeded, true);
                    }
                    cancellationToken.ThrowIfCancellationRequested();
                    TestHooks?.BeforeGapOnlyCommit?.Invoke();
                    cancellationToken.ThrowIfCancellationRequested();
                    InspectionRevision revision = new(checked(_revision + 1));
                    _gaps.Add(key, gap with { ConfirmedRevision = revision });
                    IncrementGapCountUnderLock(gap.Stream);
                    try
                    {
                        TestHooks?.BeforeRevisionPublished?.Invoke();
                        _revision = revision.Value;
                        _runs[runWriter.RunId].RecordGap();
                        committed = true;
                    }
                    catch
                    {
                        _gaps.Remove(key);
                        DecrementGapCountUnderLock(gap.Stream);
                        throw;
                    }
                    try { TestHooks?.AfterCommit?.Invoke(); }
                    catch when (committed) { }
                    return ResultUnderLock(InspectionAppendStatus.Committed, true);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            { RecordCancellation(runWriter); return Result(InspectionAppendStatus.Cancelled, true); }
            catch (Exception) { return new(InspectionAppendStatus.Faulted, Revision, true, "gap-commit-failed"); }
            finally { _writer.Release(); }
        }
        finally
        {
            ReleaseWriterAdmission();
            ReleaseRunOperation(runWriter);
        }
    }

    public ValueTask<InspectionTraceReadResult> ReadAsync(TraceIdentity identity,
        CancellationToken cancellationToken = default)
    {
        if (!identity.IsValid) { throw new ArgumentException("A valid identity is required.", nameof(identity)); }
        if (cancellationToken.IsCancellationRequested)
        { return ValueTask.FromResult(InspectionTraceReadResult.Unavailable(InspectionReadStatus.Cancelled, Revision)); }
        lock (_gate)
        {
            if (!_leasesAllowed) { return ValueTask.FromResult(InspectionTraceReadResult.Unavailable(InspectionReadStatus.Closed, new(_revision))); }
            if (!_index.TryGetPrimary(identity, out Entry? entry))
            {
                InspectionReadStatus missing = MissingStatusUnderLock(identity.Stream,
                    identity.AcquisitionIndex);
                return ValueTask.FromResult(InspectionTraceReadResult.Unavailable(missing, new(_revision)));
            }
            return ValueTask.FromResult(InspectionTraceReadResult.Available(
                new(_revision), CreateLeaseUnderLock(entry!)));
        }
    }

    public ValueTask<InspectionRangeReadResult> ReadRangeAsync(InspectionRangeQuery query,
        CancellationToken cancellationToken = default)
    {
        if (!query.IsValid) { throw new ArgumentException("A valid bounded range query is required.", nameof(query)); }
        if (cancellationToken.IsCancellationRequested)
        { return ValueTask.FromResult(InspectionRangeReadResult.Terminal(InspectionReadStatus.Cancelled, Revision)); }
        lock (_gate)
        {
            var revision = new InspectionRevision(_revision);
            if (!_leasesAllowed) { return ValueTask.FromResult(InspectionRangeReadResult.Terminal(InspectionReadStatus.Closed, revision)); }
            var items = new List<InspectionRangeItem>(query.MaximumCount);
            var acquired = new List<Lease>();
            try
            {
                _index.TryGetStream(query.Stream, out var stream);
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
                        items.Add(InspectionRangeItem.Available(lease));
                    }
                    else
                    {
                        items.Add(InspectionRangeItem.Unavailable(new TraceIdentity(query.Stream, index),
                            MissingStatusUnderLock(query.Stream, index)));
                    }
                }
                return ValueTask.FromResult(InspectionRangeReadResult.Page(revision, items));
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
                { return ValueTask.FromResult(InspectionRangeReadResult.Terminal(InspectionReadStatus.Cancelled, revision)); }
                throw new InspectionRangeAcquisitionException(error, cleanupErrors);
            }
        }
    }

    public async ValueTask<InspectionStreamStateReadResult> ReadStreamStateAsync(UtStreamIdentity stream,
        CancellationToken cancellationToken = default)
    {
        if (!stream.IsValid) { throw new ArgumentException("A valid stream is required.", nameof(stream)); }
        if (cancellationToken.IsCancellationRequested)
        { return InspectionStreamStateReadResult.Cancelled(); }
        bool admitted = false;
        try
        {
            await _streamStateAdmission.WaitAsync(cancellationToken).ConfigureAwait(false);
            admitted = true;
            TestHooks?.AfterStreamStateAdmission?.Invoke();
            await Task.Yield();
            lock (_gate)
            {
                // Linearization point: cancellation before this check never captures state.
                if (cancellationToken.IsCancellationRequested)
                { return InspectionStreamStateReadResult.Cancelled(); }
                var revision = new InspectionRevision(_revision);
                if (!_leasesAllowed)
                { return CompleteStreamStateReadUnderLock(InspectionStreamStateReadResult.Closed(revision)); }
                if (stream.InspectionId != _inspectionId)
                {
                    return CompleteStreamStateReadUnderLock(InspectionStreamStateReadResult.NotAvailable(revision,
                    InspectionStreamStateErrorCode.InspectionNotFound));
                }
                if (!_runs.TryGetValue(stream.AcquisitionRunId, out RunState? run))
                {
                    return CompleteStreamStateReadUnderLock(InspectionStreamStateReadResult.NotAvailable(revision,
                    InspectionStreamStateErrorCode.RunNotFound));
                }

                _index.TryGetStream(stream, out SortedDictionary<ulong, Entry>? entries);
                int gapCount = _gapCountsByStream.GetValueOrDefault(stream);
                if (entries is null && gapCount == 0)
                {
                    return CompleteStreamStateReadUnderLock(InspectionStreamStateReadResult.NotAvailable(revision,
                    InspectionStreamStateErrorCode.StreamNotFound));
                }
                ulong? minimum = entries is { Count: > 0 } ? entries.First().Key : null;
                ulong? maximum = entries is { Count: > 0 } ? entries.Last().Key : null;
                bool final = run.State == InspectionRunState.Sealed && run.FinalRevision is not null;
                var snapshot = new InspectionStreamStateSnapshot(stream.InspectionId,
                    stream.AcquisitionRunId, stream, run.State, revision,
                    final ? run.FinalRevision : null, minimum, maximum, entries?.Count ?? 0,
                    gapCount, final, run.State == InspectionRunState.Faulted ? run.FaultCode : null);
                return CompleteStreamStateReadUnderLock(InspectionStreamStateReadResult.Available(snapshot));
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        { return InspectionStreamStateReadResult.Cancelled(); }
        catch (Exception error)
        {
            lock (_gate)
            {
                _streamStateReadFaults++;
                _lastStreamStateReadFaultType = error.GetType().Name;
            }
            return InspectionStreamStateReadResult.Faulted();
        }
        finally { if (admitted) { _streamStateAdmission.Release(); } }
    }

    internal (long Completed, long Faulted, string? LastFaultType) StreamStateReadDiagnostics
    {
        get
        {
            lock (_gate)
            {
                return (_streamStateReadsCompleted, _streamStateReadFaults,
            _lastStreamStateReadFaultType);
            }
        }
    }

    private InspectionStreamStateReadResult CompleteStreamStateReadUnderLock(
        InspectionStreamStateReadResult result)
    {
        _streamStateReadsCompleted++;
        return result;
    }

    public ValueTask DisposeAsync()
    {
        lock (_gate) { _disposeTask ??= DisposeCoreAsync(); return new(_disposeTask); }
    }

    private bool TryAdmitRunOperation(RunWriter writer)
    {
        lock (_gate)
        {
            if (!_runs.TryGetValue(writer.RunId, out RunState? run) ||
                !ReferenceEquals(run.Writer, writer) || run.State != InspectionRunState.Open)
            {
                if (run is not null) { run.Rejections++; }
                return false;
            }
            run.PendingOperations++;
            return true;
        }
    }

    private void ReleaseRunOperation(RunWriter writer)
    {
        TaskCompletionSource? signal = null;
        lock (_gate)
        {
            if (!_runs.TryGetValue(writer.RunId, out RunState? run)) { return; }
            run.PendingOperations--;
            if (run.PendingOperations == 0) { signal = run.OperationsDrained; }
        }
        signal?.TrySetResult();
    }

    private ValueTask<InspectionRunSealResult> SealRunAsync(RunWriter writer,
        CancellationToken cancellationToken)
    {
        Task<InspectionRunSealResult> task;
        lock (_gate)
        {
            if (!_runs.TryGetValue(writer.RunId, out RunState? run) || !ReferenceEquals(run.Writer, writer))
            { throw new InvalidOperationException("The writer does not belong to this run."); }
            run.SealTask ??= SealRunCoreAsync(run);
            task = run.SealTask;
        }
        return new(task.WaitAsync(cancellationToken));
    }

    private async Task<InspectionRunSealResult> SealRunCoreAsync(RunState run)
    {
        try
        {
            Task pending;
            lock (_gate)
            {
                TestHooks?.BeforeSealing?.Invoke();
                run.State = InspectionRunState.Sealing;
                pending = run.PendingOperations == 0 ? Task.CompletedTask :
                    (run.OperationsDrained ??= NewSignal()).Task;
            }
            await pending.ConfigureAwait(false);
            lock (_gate)
            {
                TestHooks?.BeforeSealWatermarksPublished?.Invoke();
                if (_activeChunk?.Stream.AcquisitionRunId == run.RunId) { SealActiveChunkUnderLock(); }
                var revision = new InspectionRevision(_revision);
                InspectionRunSealResult result = _sealResultFactory.Create(run.RunId, revision,
                    run.MinimumIndex, run.MaximumIndex, run.MinimumSequence, run.MaximumSequence,
                    run.TraceCount, run.GapCount);
                run.FinalRevision = revision;
                run.State = InspectionRunState.Sealed;
                TestHooks?.AfterSealPublished?.Invoke();
                return result;
            }
        }
        catch
        {
            lock (_gate)
            {
                if (run.State != InspectionRunState.Sealed)
                {
                    run.State = InspectionRunState.Faulted;
                    run.FaultCode = InspectionRunFaultCode.SealFailed;
                }
            }
            throw;
        }
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
        Task[] seals;
        lock (_gate)
        {
            seals = _runs.Values.Where(static run => run.State != InspectionRunState.Sealed)
                .Select(run => run.SealTask ??= SealRunCoreAsync(run)).ToArray();
        }
        Exception? sealFailure = null;
        try { await Task.WhenAll(seals).ConfigureAwait(false); }
        catch (Exception error) { sealFailure = error; }
        lock (_gate)
        {
            SealActiveChunkUnderLock();
            _leasesAllowed = false;
            TestHooks?.AfterLeasesClosed?.Invoke();
        }
        Task leases;
        lock (_gate) { leases = _activeLeases == 0 ? Task.CompletedTask : (_leasesDrained ??= NewSignal()).Task; }
        await leases.ConfigureAwait(false);
        var cleanupFailures = new List<Exception>();
        try { TestHooks?.BeforeCleanup?.Invoke(); }
        catch (Exception error) { cleanupFailures.Add(error); }
        lock (_gate)
        {
            foreach (Entry entry in _index.Values)
            {
                try { _ownerReleaser.Release(entry.Samples); }
                catch (Exception error)
                {
                    cleanupFailures.Add(error);
                    try { entry.Samples.Dispose(); }
                    catch (Exception fallbackError) { cleanupFailures.Add(fallbackError); }
                }
            }
            _index.Clear(); _gaps.Clear(); _gapCountsByStream.Clear(); _chunks.Clear(); _activeChunk = null;
            _logicalBytes = 0; _reservedSampleBytes = 0; _ownedSampleBytes = 0;
            _closing = false; _closed = true;
        }
        if (sealFailure is not null || cleanupFailures.Count != 0)
        {
            string primary = sealFailure is not null ? "run-seal-failed" : "cleanup-failed";
            string[] secondary = sealFailure is not null && cleanupFailures.Count != 0 ? ["cleanup-failed"] : [];
            Exception inner = sealFailure is not null && cleanupFailures.Count != 0
                ? new AggregateException([sealFailure, .. cleanupFailures])
                : sealFailure ?? (cleanupFailures.Count == 1 ? cleanupFailures[0] : new AggregateException(cleanupFailures));
            throw new InspectionStoreCloseException(primary, secondary, inner);
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

    private bool HasEntryCapacityUnderLock(int additionalEntries)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(additionalEntries);
        int remaining = _options.MaximumEntries;
        if (_index.Count > remaining) { return false; }
        remaining -= _index.Count;
        if (_gaps.Count > remaining) { return false; }
        remaining -= _gaps.Count;
        return additionalEntries <= remaining;
    }

    private InspectionReadStatus MissingStatusUnderLock(UtStreamIdentity stream, ulong acquisitionIndex)
    {
        if (!_runs.TryGetValue(stream.AcquisitionRunId, out RunState? run) ||
            run.State != InspectionRunState.Sealed)
        { return InspectionReadStatus.NotAvailable; }
        if (!_index.TryGetStream(stream, out SortedDictionary<ulong, Entry>? entries) ||
            entries is not { Count: > 0 })
        {
            return _gapCountsByStream.ContainsKey(stream)
                ? InspectionReadStatus.OutOfRange : InspectionReadStatus.NotAvailable;
        }
        if (acquisitionIndex < entries.First().Key || acquisitionIndex > entries.Last().Key)
        { return InspectionReadStatus.OutOfRange; }
        return InspectionReadStatus.NotAvailable;
    }

    private void IncrementGapCountUnderLock(UtStreamIdentity stream) =>
        _gapCountsByStream[stream] = checked(_gapCountsByStream.GetValueOrDefault(stream) + 1);

    private void DecrementGapCountUnderLock(UtStreamIdentity stream)
    {
        int remaining = _gapCountsByStream[stream] - 1;
        if (remaining == 0) { _gapCountsByStream.Remove(stream); }
        else { _gapCountsByStream[stream] = remaining; }
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
        if (identity.Stream.InspectionId != _inspectionId ||
            (_runId is { } requiredRun && identity.Stream.AcquisitionRunId != requiredRun))
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
    private void RecordCancellation(RunWriter? writer)
    {
        lock (_gate)
        {
            _cancelledAppends++;
            if (writer is not null && _runs.TryGetValue(writer.RunId, out RunState? run)) { run.Cancellations++; }
        }
    }
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

    private static bool GapEquivalent(GapRecord left, GapRecord right) =>
        left.Stream == right.Stream && left.FirstMissingSequence == right.FirstMissingSequence &&
        left.LastMissingSequence == right.LastMissingSequence &&
        left.PreviousAdmittedSequence == right.PreviousAdmittedSequence &&
        left.SubsequentObservedSequence == right.SubsequentObservedSequence && left.Cause == right.Cause;

    internal readonly record struct GapKey(UtStreamIdentity Stream, ulong FirstMissingSequence,
        ulong LastMissingSequence)
    {
        internal static GapKey From(GapRecord gap) => new(gap.Stream, gap.FirstMissingSequence, gap.LastMissingSequence);
    }

    private sealed class GapConflictException : Exception;

    private sealed class RunWriter(InMemoryInspectionDataStore owner, AcquisitionRunId runId) : IInspectionRunWriter
    {
        public AcquisitionRunId RunId { get; } = runId;
        public InspectionRunState State
        {
            get { lock (owner._gate) { return owner._runs.TryGetValue(RunId, out RunState? run) ? run.State : InspectionRunState.Unknown; } }
        }
        public ValueTask<InspectionAppendResult> CommitAsync(InspectionCommitInput commit,
            CancellationToken cancellationToken = default)
        {
            if (commit.Trace.Descriptor.Identity.Stream.AcquisitionRunId != RunId)
            { throw new ArgumentException("The commit belongs to a different run.", nameof(commit)); }
            return owner.CommitCoreAsync(commit, this, cancellationToken);
        }
        public ValueTask<InspectionAppendResult> CommitGapAsync(GapRecord gap,
            CancellationToken cancellationToken = default) =>
            owner.CommitGapCoreAsync(gap, this, cancellationToken);
        public ValueTask<InspectionRunSealResult> SealRunAsync(CancellationToken cancellationToken = default) =>
            owner.SealRunAsync(this, cancellationToken);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class RunState(AcquisitionRunId runId, RunWriter writer)
    {
        internal AcquisitionRunId RunId { get; } = runId;
        internal RunWriter Writer { get; } = writer;
        internal InspectionRunState State = InspectionRunState.Open;
        internal int PendingOperations, TraceCount, GapCount;
        internal ulong Duplicates { get; set; }
        internal ulong Conflicts { get; set; }
        internal ulong Cancellations { get; set; }
        internal ulong Rejections { get; set; }
        internal InspectionRevision? FinalRevision;
        internal InspectionRunFaultCode? FaultCode;
        internal ulong? MinimumIndex, MaximumIndex, MinimumSequence, MaximumSequence;
        internal TaskCompletionSource? OperationsDrained;
        internal Task<InspectionRunSealResult>? SealTask;
        internal void RecordCommit(TraceDescriptor descriptor, int gapCount, InspectionRevision revision)
        {
            TraceCount++; GapCount += gapCount;
            MinimumIndex = MinimumIndex is null ? descriptor.Identity.AcquisitionIndex : Math.Min(MinimumIndex.Value, descriptor.Identity.AcquisitionIndex);
            MaximumIndex = MaximumIndex is null ? descriptor.Identity.AcquisitionIndex : Math.Max(MaximumIndex.Value, descriptor.Identity.AcquisitionIndex);
            MinimumSequence = MinimumSequence is null ? descriptor.Sequence : Math.Min(MinimumSequence.Value, descriptor.Sequence);
            MaximumSequence = MaximumSequence is null ? descriptor.Sequence : Math.Max(MaximumSequence.Value, descriptor.Sequence);
        }
        internal void RecordGap() => GapCount++;
        internal RunStoreDiagnostics Diagnostics() => new(RunId, State,
            State == InspectionRunState.Open ? 1 : 0, PendingOperations, TraceCount, GapCount,
            Duplicates, Conflicts, Cancellations, Rejections, FinalRevision,
            MinimumIndex, MaximumIndex, MinimumSequence, MaximumSequence);
    }

    internal sealed class Entry(TraceDescriptor descriptor, ImmutableShortMemory samples, Chunk chunk,
        IReadOnlyList<GapKey> gaps)
    {
        internal TraceDescriptor Descriptor { get; } = descriptor;
        internal ImmutableShortMemory Samples { get; } = samples;
        internal Chunk Chunk { get; } = chunk;
        internal IReadOnlyList<GapKey> Gaps { get; } = gaps;
    }

    internal sealed class Chunk(bool dedicated, TraceDescriptor descriptor)
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

    internal sealed class ImmutableShortMemory(short[] array) : IDisposable
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

internal interface IInspectionTraceIndex
{
    int Count { get; }
    IEnumerable<InMemoryInspectionDataStore.Entry> Values { get; }
    bool TryGetPrimary(TraceIdentity identity, out InMemoryInspectionDataStore.Entry? entry);
    void AddPrimary(TraceIdentity identity, InMemoryInspectionDataStore.Entry entry);
    void RemovePrimary(TraceIdentity identity);
    bool TryGetStream(UtStreamIdentity stream,
        out SortedDictionary<ulong, InMemoryInspectionDataStore.Entry>? entries);
    bool ContainsStream(UtStreamIdentity stream);
    void AddStream(UtStreamIdentity stream, SortedDictionary<ulong, InMemoryInspectionDataStore.Entry> entries);
    void RemoveStream(UtStreamIdentity stream);
    void Clear();
}

internal interface IInspectionSealResultFactory
{
    InspectionRunSealResult Create(AcquisitionRunId runId, InspectionRevision finalRevision,
        ulong? minimumAcquisitionIndex, ulong? maximumAcquisitionIndex, ulong? minimumSequence,
        ulong? maximumSequence, int traceCount, int gapCount);
}

internal sealed class DefaultInspectionSealResultFactory : IInspectionSealResultFactory
{
    internal static DefaultInspectionSealResultFactory Instance { get; } = new();
    public InspectionRunSealResult Create(AcquisitionRunId runId, InspectionRevision finalRevision,
        ulong? minimumAcquisitionIndex, ulong? maximumAcquisitionIndex, ulong? minimumSequence,
        ulong? maximumSequence, int traceCount, int gapCount) =>
        new(runId, finalRevision, minimumAcquisitionIndex, maximumAcquisitionIndex,
            minimumSequence, maximumSequence, traceCount, gapCount);
}

internal sealed class DefaultInspectionTraceIndex : IInspectionTraceIndex
{
    private readonly Dictionary<TraceIdentity, InMemoryInspectionDataStore.Entry> _primary = [];
    private readonly Dictionary<UtStreamIdentity,
        SortedDictionary<ulong, InMemoryInspectionDataStore.Entry>> _streams = [];
    public int Count => _primary.Count;
    public IEnumerable<InMemoryInspectionDataStore.Entry> Values => _primary.Values;
    public bool TryGetPrimary(TraceIdentity identity, out InMemoryInspectionDataStore.Entry? entry) =>
        _primary.TryGetValue(identity, out entry);
    public void AddPrimary(TraceIdentity identity, InMemoryInspectionDataStore.Entry entry) =>
        _primary.Add(identity, entry);
    public void RemovePrimary(TraceIdentity identity) => _primary.Remove(identity);
    public bool TryGetStream(UtStreamIdentity stream,
        out SortedDictionary<ulong, InMemoryInspectionDataStore.Entry>? entries) =>
        _streams.TryGetValue(stream, out entries);
    public bool ContainsStream(UtStreamIdentity stream) => _streams.ContainsKey(stream);
    public void AddStream(UtStreamIdentity stream,
        SortedDictionary<ulong, InMemoryInspectionDataStore.Entry> entries) => _streams.Add(stream, entries);
    public void RemoveStream(UtStreamIdentity stream) => _streams.Remove(stream);
    public void Clear() { _primary.Clear(); _streams.Clear(); }
}

internal interface IInspectionSampleMaterializer
{
    short[] Materialize(ReadOnlyMemory<short> source, int expectedLength);
}

internal sealed class DefaultInspectionSampleMaterializer : IInspectionSampleMaterializer
{
    internal static DefaultInspectionSampleMaterializer Instance { get; } = new();
    public short[] Materialize(ReadOnlyMemory<short> source, int expectedLength)
    {
        var result = new short[expectedLength];
        source.Span.CopyTo(result);
        return result;
    }
}

internal interface IInspectionOwnerReleaser
{
    void Release(IDisposable owner);
}

internal sealed class DefaultInspectionOwnerReleaser : IInspectionOwnerReleaser
{
    internal static DefaultInspectionOwnerReleaser Instance { get; } = new();
    public void Release(IDisposable owner) => owner.Dispose();
}

internal sealed class InspectionStoreTestHooks
{
    internal Func<int, short[]>? Allocate { get; init; }
    internal Action<ReadOnlyMemory<short>, short[]>? Copy { get; init; }
    internal Action? BeforeCopy { get; init; }
    internal Action? AfterCapacityReserved { get; init; }
    internal Action? AfterWriterAdmitted { get; init; }
    internal Action? AfterCopy { get; init; }
    internal Action? BeforeCommit { get; init; }
    internal Action? AfterTracePrepared { get; init; }
    internal Action<int>? AfterGapPrepared { get; init; }
    internal Action? BeforeGapOnlyCommit { get; init; }
    internal Action? BeforeChunkAdd { get; init; }
    internal Action? AfterPrimaryIndex { get; init; }
    internal Action? AfterStreamIndex { get; init; }
    internal Action? BeforeRevisionPublished { get; init; }
    internal Action? AfterStreamStateAdmission { get; init; }
    internal Action? AfterLeasesClosed { get; init; }
    internal Action? AfterCommit { get; init; }
    internal Action? BeforeSealing { get; init; }
    internal Action? BeforeSealWatermarksPublished { get; init; }
    internal Action? AfterSealPublished { get; init; }
    internal Action? BeforeCleanup { get; init; }
    internal Action<int>? BeforeRangeLease { get; init; }
    internal Action<int>? AfterRangeLeaseCreated { get; init; }
    internal Action<int>? BeforeRangeRollbackDispose { get; init; }
    internal Action? BeforeLeaseRead { get; init; }
}
