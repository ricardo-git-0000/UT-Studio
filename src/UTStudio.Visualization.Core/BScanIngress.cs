using UTStudio.Contracts.Presentation;
using UTStudio.Domain.Acquisition;

namespace UTStudio.Visualization.Core;

/// <summary>Bounded live B-Scan ingress, index worker and exact-trace provider.</summary>
public sealed class BScanIngress : IConventionalFrameSink, IBScanTraceProvider, IAsyncDisposable
{
    private const int DurationWindow = 1024;
    private readonly object _gate = new();
    private readonly BScanOptions _options;
    private readonly IShortArrayPool _arrayPool;
    private readonly ITraceCopyOwnerPool _copyPool;
    private readonly BoundedBScanVisualBufferPool _visualBuffers;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _copyQuota = new(2, 2);
    private readonly long[] _durations = new long[DurationWindow];
    private readonly List<string> _cleanupErrors = [];
    private BScanTraceStore? _store;
    private ConventionalUtFrameMetadata? _metadata;
    private SemaphoreSlim? _wake;
    private CancellationTokenSource? _runCancellation;
    private CancellationTokenSource _lifetimeCancellation = new();
    private Task? _worker;
    private Task? _closeTask;
    private Task? _drainTask;
    private Task? _disposeTask;
    private bool _open;
    private bool _disposed;
    private ulong _rejectedConfiguration, _rejectedClosed, _budgetExceeded;
    private long _durationCount, _durationMaximum;
    private string? _workerError;
    private int _providerOperations;
    private TaskCompletionSource? _providerDrain;
    private long _visualEpoch;
    private BScanIngressVisualState? _visualState;

    internal Action? BeforeIndexBatch { get; set; }
    internal Action<BScanTraceIdentity>? AfterIndexCommit { get; set; }
    internal Func<Exception?>? WorkerFailure { get; set; }
    internal Action? AfterWorkerFailure { get; set; }
    internal Action? BeforeProviderCopy { get; set; }
    internal Action? AfterProviderCopy { get; set; }
    internal event Action? VisualStateChanged;

    public BScanIngress(BScanOptions? options = null, TimeProvider? timeProvider = null)
        : this(options, timeProvider, SharedShortArrayPool.Instance, null, null) { }

    internal BScanIngress(BScanOptions? options, TimeProvider? timeProvider,
        IShortArrayPool arrayPool, ITraceCopyOwnerPool? copyPool,
        BoundedBScanVisualBufferPool? visualBuffers = null)
    {
        _options = options ?? new();
        _timeProvider = timeProvider ?? TimeProvider.System;
        _arrayPool = arrayPool ?? throw new ArgumentNullException(nameof(arrayPool));
        _visualBuffers = visualBuffers ?? new(_options.VisualMemoryBudgetBytes);
        _copyPool = copyPool ?? new BoundedTraceCopyOwnerPool(_visualBuffers);
    }

    internal BoundedBScanVisualBufferPool VisualBuffers => _visualBuffers;

    public void OpenRun(ConventionalUtFrameMetadata metadata)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        BScanTraceStore? previousStore;
        Task? previousClose;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_open || _closeTask is { IsCompleted: false })
            {
                throw new InvalidOperationException("The previous B-Scan run is still active or closing.");
            }
            previousStore = _store;
            previousClose = _closeTask;
        }
        previousClose?.GetAwaiter().GetResult();
        previousStore?.CloseAsync().GetAwaiter().GetResult();
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var store = new BScanTraceStore(metadata.Configuration.SampleCount, _options, _arrayPool,
                _visualBuffers);
            var wake = new SemaphoreSlim(0, 1);
            var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCancellation.Token);
            _metadata = metadata;
            _store = store;
            _wake = wake;
            _runCancellation = cancellation;
            _open = true;
            _closeTask = null;
            _workerError = null;
            _cleanupErrors.Clear();
            _rejectedConfiguration = 0;
            _rejectedClosed = 0;
            _budgetExceeded = 0;
            _durationCount = 0;
            _durationMaximum = 0;
            Array.Clear(_durations);
            _worker = Task.Run(() => RunIndexWorkerAsync(store, wake, cancellation.Token));
            Volatile.Write(ref _visualState, new(checked(++_visualEpoch), metadata, store,
                store.CurrentVisualState, true));
        }
        NotifyVisualStateChanged();
    }

    public void Accept(ConventionalUtFrameMetadata metadata, ulong sequence, ulong acquisitionIndex,
        TimeSpan elapsedSinceRunStart, ScanSpatialCoordinate? spatialCoordinate, ReadOnlySpan<short> samples)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        long started = _timeProvider.GetTimestamp();
        try
        {
            BScanTraceStore? store;
            SemaphoreSlim? wake;
            lock (_gate)
            {
                store = _open ? _store : null;
                wake = _wake;
                if (store is null)
                {
                    _rejectedClosed++;
                    return;
                }
                if (!IsCompatible(_metadata!, metadata) || samples.Length != metadata.Configuration.SampleCount ||
                    spatialCoordinate is { IsValid: false })
                {
                    _rejectedConfiguration++;
                    return;
                }
            }

            var reservation = store.BeginWrite(samples.Length);
            if (reservation.Kind != StoreWriteKind.Accepted)
            {
                lock (_gate)
                {
                    if (reservation.Kind == StoreWriteKind.Configuration) { _rejectedConfiguration++; }
                    else if (reservation.Kind == StoreWriteKind.Closed) { _rejectedClosed++; }
                }
                return;
            }

            try
            {
                samples.CopyTo(reservation.Destination.Span);
                var identity = new BScanTraceIdentity(metadata.SourceId, metadata.RunId,
                    metadata.Configuration.PhysicalChannelId, sequence);
                var descriptor = new BScanTraceDescriptor(metadata, identity, acquisitionIndex, elapsedSinceRunStart,
                    metadata.Configuration.SampleCount, metadata.Configuration.SampleRateHz,
                    metadata.Configuration.FirstSampleOffsetSeconds, spatialCoordinate, 0, 0);
                StoreWriteResult result = reservation.Commit(descriptor);
                if (result.Kind == StoreWriteKind.Accepted)
                {
                    try { wake!.Release(); }
                    catch (SemaphoreFullException) { }
                }
                else if (result.Kind == StoreWriteKind.Closed)
                {
                    lock (_gate) { _rejectedClosed++; }
                }
            }
            catch
            {
                reservation.Abort();
                throw;
            }
        }
        finally
        {
            RecordDuration(_timeProvider.GetElapsedTime(started, _timeProvider.GetTimestamp()));
        }
    }

    public void CloseRun(AcquisitionRunId runId)
    {
        bool changed = false;
        if (!runId.IsValid) { throw new ArgumentException("Invalid run identifier.", nameof(runId)); }
        lock (_gate)
        {
            if (!_open || _metadata?.RunId != runId) { return; }
            _open = false;
            _store!.StopAccepting();
            _closeTask ??= FreezeCoreAsync(_store, _worker!, _wake!, _runCancellation!);
            Volatile.Write(ref _visualState, new(checked(++_visualEpoch), _metadata, _store,
                _store.CurrentVisualState, true));
            changed = true;
        }
        if (changed) { NotifyVisualStateChanged(); }
    }

    public ValueTask WaitForCloseAsync(AcquisitionRunId runId)
    {
        lock (_gate)
        {
            if (_metadata?.RunId != runId) { return ValueTask.CompletedTask; }
            return new(_closeTask ?? Task.CompletedTask);
        }
    }

    public async ValueTask<BScanTraceResult> GetTraceAsync(
        BScanTraceIdentity identity, CancellationToken cancellationToken = default)
    {
        if (!identity.IsValid) { return BScanTraceResult.NotAvailable(); }
        CancellationToken lifetime;
        BScanTraceStore? store;
        lock (_gate)
        {
            if (_disposed || _store is null) { return BScanTraceResult.NotAvailable(); }
            _providerOperations++;
            lifetime = _open ? _runCancellation!.Token : _lifetimeCancellation.Token;
            store = _store;
        }

        bool quota = false;
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime);
            CancellationToken token = linked.Token;
            await _copyQuota.WaitAsync(token).ConfigureAwait(false);
            quota = true;
            token.ThrowIfCancellationRequested();
            using BScanTraceStore.BScanTraceLease? lease = store.TryAcquire(identity);
            if (lease is null)
            {
                _copyQuota.Release();
                quota = false;
                return store.GetResidence(identity) == BScanResidence.Evicted
                    ? BScanTraceResult.Evicted() : BScanTraceResult.NotAvailable();
            }

            token.ThrowIfCancellationRequested();
            var owner = _copyPool.Rent(lease.Descriptor.SampleCount);
            try
            {
                BeforeProviderCopy?.Invoke();
                Memory<short> destination = owner.Memory[..lease.Descriptor.SampleCount];
                const int chunkSize = 4096;
                for (int offset = 0; offset < destination.Length; offset += chunkSize)
                {
                    token.ThrowIfCancellationRequested();
                    int count = Math.Min(chunkSize, destination.Length - offset);
                    lease.Samples.Span.Slice(offset, count).CopyTo(destination.Span.Slice(offset, count));
                }
                AfterProviderCopy?.Invoke();
                token.ThrowIfCancellationRequested();
                var snapshot = new BScanTraceSnapshot(lease.Descriptor, owner, () => _copyQuota.Release());
                quota = false;
                return BScanTraceResult.Available(snapshot);
            }
            catch
            {
                owner.Dispose();
                throw;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested || lifetime.IsCancellationRequested)
        {
            return BScanTraceResult.Cancelled();
        }
        catch (Exception error)
        {
            return BScanTraceResult.Faulted(error);
        }
        finally
        {
            if (quota) { _copyQuota.Release(); }
            EndProviderOperation();
        }
    }

    public bool TryResolveIdentity(AcquisitionRunId runId, ulong acquisitionIndex,
        out BScanTraceIdentity identity)
    {
        lock (_gate)
        {
            if (_disposed || _metadata?.RunId != runId || _store is null)
            { identity = default; return false; }
            return _store.TryResolveIdentity(acquisitionIndex, out identity);
        }
    }

    public BScanDiagnostics Diagnostics
    {
        get
        {
            lock (_gate)
            {
                StoreStatistics statistics = _store?.Statistics ?? default;
                return new(statistics.Accepted, statistics.RejectedPinned, _rejectedConfiguration,
                    _rejectedClosed, statistics.Evicted, statistics.Superseded, statistics.ActiveLeases,
                    statistics.MaximumLeases, _budgetExceeded, GetDurations(), statistics.BlocksRented,
                    statistics.BlocksReturned, statistics.LogicalBytes, statistics.RentedBytes,
                    statistics.VisualLogicalBytes, statistics.Capacity, _workerError,
                    [.. _cleanupErrors], statistics.Closed);
            }
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            return new(_disposeTask ??= DisposeCoreAsync());
        }
    }

    internal void Stop()
    {
        bool notify = false;
        lock (_gate)
        {
            if (_disposed) { return; }
            _disposed = true;
            _lifetimeCancellation.Cancel();
            if (_open)
            {
                _open = false;
                _store!.StopAccepting();
                _closeTask ??= FreezeCoreAsync(_store, _worker!, _wake!, _runCancellation!);
            }
            if (_store is not null && _metadata is not null)
            {
                Volatile.Write(ref _visualState, new(checked(++_visualEpoch), _metadata, _store,
                    _store.CurrentVisualState, false));
                notify = true;
            }
            Task closeAndReturn = CloseAndReturnStoreAsync(_closeTask ?? Task.CompletedTask, _store);
            _drainTask ??= DrainCoreAsync(
                closeAndReturn,
                _providerOperations == 0 ? Task.CompletedTask :
                    (_providerDrain ??= new(TaskCreationOptions.RunContinuationsAsynchronously)).Task);
        }
        if (notify) { NotifyVisualStateChanged(); }
    }

    internal Task WaitForDrainAsync(CancellationToken cancellationToken = default)
    {
        Stop();
        Task drain;
        lock (_gate) { drain = _drainTask!; }
        return cancellationToken.CanBeCanceled ? drain.WaitAsync(cancellationToken) : drain;
    }

    private async Task DisposeCoreAsync()
    {
        Stop();
        await WaitForDrainAsync().ConfigureAwait(false);
        await _visualBuffers.DisposeAsync().ConfigureAwait(false);
        _lifetimeCancellation.Dispose();
    }

    private static async Task DrainCoreAsync(Task close, Task providers)
    {
        List<Exception> failures = [];
        try { await close.ConfigureAwait(false); }
        catch (Exception error) { AddFailures(failures, error); }
        try { await providers.ConfigureAwait(false); }
        catch (Exception error) { AddFailures(failures, error); }
        if (failures.Count != 0) { throw new AggregateException("B-Scan ingress drain failed.", failures); }
    }

    private async Task FreezeCoreAsync(BScanTraceStore store, Task worker, SemaphoreSlim wake,
        CancellationTokenSource cancellation)
    {
        List<Exception> failures = [];
        try
        {
            await store.WaitForWritersAsync().ConfigureAwait(false);
            try { wake.Release(); } catch (SemaphoreFullException) { }
        }
        catch (Exception error) { failures.Add(error); }
        try { await worker.ConfigureAwait(false); }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested ||
            _lifetimeCancellation.IsCancellationRequested)
        { }
        catch (Exception error)
        {
            lock (_gate) { _workerError ??= Message(error); }
            failures.Add(error);
        }
        try
        {
            wake.Dispose();
            cancellation.Dispose();
        }
        catch (Exception error) { failures.Add(error); }
        if (failures.Count != 0) { throw new AggregateException("B-Scan run cleanup failed.", failures); }
    }

    private async Task CloseAndReturnStoreAsync(Task frozen, BScanTraceStore? store)
    {
        List<Exception> failures = [];
        try { await frozen.ConfigureAwait(false); }
        catch (Exception error) { AddFailures(failures, error); }
        if (store is not null)
        {
            try { await store.CloseAsync().ConfigureAwait(false); }
            catch (Exception error)
            {
                AddFailures(failures, error);
                lock (_gate)
                {
                    IEnumerable<Exception> errors = error is AggregateException aggregate
                        ? aggregate.Flatten().InnerExceptions : [error];
                    foreach (Exception cleanupError in errors)
                    {
                        if (_cleanupErrors.Count == 8) { break; }
                        _cleanupErrors.Add(Message(cleanupError));
                    }
                }
            }
        }
        if (failures.Count != 0) { throw new AggregateException("B-Scan store cleanup failed.", failures); }
    }

    private static void AddFailures(List<Exception> failures, Exception error)
    {
        if (error is AggregateException aggregate) { failures.AddRange(aggregate.Flatten().InnerExceptions); }
        else { failures.Add(error); }
    }

    private async Task IndexLoopAsync(BScanTraceStore store, SemaphoreSlim wake, CancellationToken token)
    {
        using IBScanVisualBufferOwner<BScanIndexedMinMax> bucketsOwner =
            _visualBuffers.Rent<BScanIndexedMinMax>(store.BucketCount,
                BScanVisualBufferKind.DetachedIndex);
        BScanIndexedMinMax[] buckets = bucketsOwner.Array;
        var candidates = new BScanIndexCandidate[store.Capacity];
        while (true)
        {
            await wake.WaitAsync(token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            BeforeIndexBatch?.Invoke();
            if (WorkerFailure?.Invoke() is { } failure) { throw failure; }
            int candidateCount = store.CaptureIndexCandidates(candidates);
            for (int candidateIndex = 0; candidateIndex < candidateCount; candidateIndex++)
            {
                (BScanTraceIdentity identity, ulong generation) = candidates[candidateIndex];
                token.ThrowIfCancellationRequested();
                using BScanTraceStore.BScanTraceLease? lease = store.TryAcquire(identity, generation);
                if (lease is null)
                {
                    if (!token.IsCancellationRequested) { store.RecordSuperseded(); }
                    continue;
                }
                BuildIndexedIndex(lease.Samples.Span, _options.IndexBucketSamples, buckets);
                if (store.CommitIndex(identity, generation, buckets))
                {
                    PublishVisualState(store);
                    AfterIndexCommit?.Invoke(identity);
                    NotifyVisualStateChanged();
                }
            }
            if (!store.IsAccepting) { return; }
        }
    }

    private async Task RunIndexWorkerAsync(BScanTraceStore store, SemaphoreSlim wake, CancellationToken token)
    {
        try { await IndexLoopAsync(store, wake, token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception error)
        {
            lock (_gate) { _workerError ??= Message(error); }
            AfterWorkerFailure?.Invoke();
            throw;
        }
    }

    internal static void BuildIndex(ReadOnlySpan<short> samples, int bucketSamples, Span<BScanMinMax> destination)
    {
        int required = checked((samples.Length + bucketSamples - 1) / bucketSamples);
        if (destination.Length < required) { throw new ArgumentException("Index destination is too short.", nameof(destination)); }
        for (int bucket = 0; bucket < required; bucket++)
        {
            int start = checked(bucket * bucketSamples);
            ReadOnlySpan<short> source = samples.Slice(start, Math.Min(bucketSamples, samples.Length - start));
            short minimum = source[0], maximum = source[0];
            for (int index = 1; index < source.Length; index++)
            {
                if (source[index] < minimum) { minimum = source[index]; }
                if (source[index] > maximum) { maximum = source[index]; }
            }
            destination[bucket] = new(minimum, maximum);
        }
    }

    private static void BuildIndexedIndex(ReadOnlySpan<short> samples, int bucketSamples,
        Span<BScanIndexedMinMax> destination)
    {
        int required = checked((samples.Length + bucketSamples - 1) / bucketSamples);
        if (destination.Length < required) { throw new ArgumentException("Index destination is too short.", nameof(destination)); }
        for (int bucket = 0; bucket < required; bucket++)
        {
            int start = checked(bucket * bucketSamples);
            ReadOnlySpan<short> source = samples.Slice(start, Math.Min(bucketSamples, samples.Length - start));
            short minimum = source[0], maximum = source[0];
            int minimumOffset = start, maximumOffset = start;
            for (int index = 1; index < source.Length; index++)
            {
                if (source[index] < minimum) { minimum = source[index]; minimumOffset = start + index; }
                if (source[index] > maximum) { maximum = source[index]; maximumOffset = start + index; }
            }
            destination[bucket] = new(minimum, checked((ushort)minimumOffset),
                maximum, checked((ushort)maximumOffset));
        }
    }

    private void NotifyVisualStateChanged()
    {
        Delegate[] observers = VisualStateChanged?.GetInvocationList() ?? [];
        foreach (Action observer in observers)
        {
            try { observer(); }
            catch { /* Visual observers cannot terminate the indexing worker. */ }
        }
    }

    internal BScanMinMax[]? CopyIndexForDiagnostics(BScanTraceIdentity identity) => _store?.CopyIndex(identity);
    internal int PendingWorkerSignals => _wake?.CurrentCount ?? 0;
    internal int VisualCapacity { get { lock (_gate) { return _store?.Capacity ?? 0; } } }
    internal long VisualBytesReserved { get { lock (_gate) { return _store?.VisualLogicalBytes ?? 0; } } }
    internal BScanOptions Options => _options;
    internal ConventionalUtFrameMetadata? CurrentMetadata { get { lock (_gate) { return _disposed ? null : _metadata; } } }
    internal BScanIngressVisualState? CurrentVisualState => Volatile.Read(ref _visualState);
    internal int CaptureVisualLeases(Span<BScanTraceStore.BScanTraceLease?> destination,
        out BScanIngressVisualState? visualState, out BScanStoreVisualState state)
    {
        visualState = Volatile.Read(ref _visualState);
        if (visualState is not { IsOpen: true } current)
        { state = null!; return 0; }
        return current.Store.CaptureVisualLeases(current.StoreState, destination, out state);
    }

    private void PublishVisualState(BScanTraceStore store)
    {
        lock (_gate)
        {
            if (_disposed || !ReferenceEquals(_store, store) || _metadata is null) { return; }
            Volatile.Write(ref _visualState, new(checked(++_visualEpoch), _metadata, store,
                store.CurrentVisualState, _open));
        }
    }

    private static bool IsCompatible(ConventionalUtFrameMetadata expected, ConventionalUtFrameMetadata actual) =>
        expected.SourceId == actual.SourceId && expected.RunId == actual.RunId &&
        expected.Configuration.PhysicalChannelId == actual.Configuration.PhysicalChannelId &&
        expected.Configuration.SampleCount == actual.Configuration.SampleCount &&
        expected.Configuration.SampleRateHz.Equals(actual.Configuration.SampleRateHz) &&
        expected.Configuration.FirstSampleOffsetSeconds.Equals(actual.Configuration.FirstSampleOffsetSeconds) &&
        expected.Configuration.SignalMode == actual.Configuration.SignalMode &&
        expected.RunStartedAtUtc == actual.RunStartedAtUtc;

    private void EndProviderOperation()
    {
        TaskCompletionSource? drain = null;
        lock (_gate)
        {
            _providerOperations--;
            if (_providerOperations == 0) { drain = _providerDrain; }
        }
        drain?.TrySetResult();
    }

    private void RecordDuration(TimeSpan elapsed)
    {
        long ticks = Math.Max(0, elapsed.Ticks);
        lock (_gate)
        {
            long index = _durationCount++;
            _durations[index % DurationWindow] = ticks;
            _durationMaximum = Math.Max(_durationMaximum, ticks);
            if (elapsed > _options.AcceptDiagnosticBudget) { _budgetExceeded++; }
        }
    }

    private BScanAcceptDurationStatistics GetDurations()
    {
        int count = (int)Math.Min(_durationCount, DurationWindow);
        if (count == 0) { return new(0, TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero); }
        long[] values = _durations[..count].ToArray();
        Array.Sort(values);
        return new(_durationCount, TimeSpan.FromTicks(Percentile(values, 50)),
            TimeSpan.FromTicks(Percentile(values, 95)), TimeSpan.FromTicks(Percentile(values, 99)),
            TimeSpan.FromTicks(_durationMaximum));
    }

    private static long Percentile(long[] values, int percentile) =>
        values[Math.Max(0, (int)Math.Ceiling(values.Length * percentile / 100d) - 1)];

    private static string Message(Exception error) =>
        string.IsNullOrWhiteSpace(error.Message) ? error.GetType().Name : error.Message;
}

internal sealed record BScanIngressVisualState(
    long Epoch,
    ConventionalUtFrameMetadata Metadata,
    BScanTraceStore Store,
    BScanStoreVisualState StoreState,
    bool IsOpen)
{
    internal BScanStreamIdentity Stream => new(Metadata.SourceId, Metadata.RunId,
        Metadata.Configuration.PhysicalChannelId);
}
