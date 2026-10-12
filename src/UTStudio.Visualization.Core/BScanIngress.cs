using UTStudio.Contracts.Inspection;
using UTStudio.Domain.Acquisition;

namespace UTStudio.Visualization.Core;

/// <summary>
/// Sample-free B-Scan range projector. Commit notifications only coalesce work; authoritative
/// state and samples are always reacquired from the inspection providers.
/// </summary>
public sealed class BScanIngress : ICommittedTraceLifecycleSink, IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly IInspectionRangeProvider _ranges;
    private readonly IInspectionStreamStateProvider _states;
    private readonly BScanOptions _options;
    private readonly BoundedBScanVisualBufferPool _visualBuffers;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _reconcileWake = new(0, 1);
    private readonly Task _reconcileWorker;
    private CancellationTokenSource? _runCancellation;
    private long _runEpoch;
    private bool _reconcilePending;
    private bool _reconcileScheduled;
    private int _activeOperations;
    private TaskCompletionSource _drained = CompletedDrain();
    private readonly TaskCompletionSource _disposeDrained =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private BScanRunProjectionPhase _phase = BScanRunProjectionPhase.Closed;
    private InspectionRevision? _requiredFinalRevision;
    private Task? _disposeTask;
    private AcquisitionRunId? _runId;
    private UtStreamIdentity? _stream;
    private InspectionRevision _targetRevision;
    private readonly Dictionary<TraceIdentity, ulong> _identityRevisions = [];
    private bool _disposed;
    private long _epoch;
    private int _activeLeases;
    private int _maximumLeases;
    private ulong _rangeReads;
    private string? _workerError;
    private BScanIngressVisualState? _visualState;

    internal event Action? VisualStateChanged;
    internal Action? BeforeReconcileWorkerExit { get; set; }
    internal Action? BeforeVisualStateCommit { get; set; }

    public BScanIngress(IInspectionRangeProvider ranges, IInspectionStreamStateProvider states,
        BScanOptions? options = null, TimeProvider? timeProvider = null)
        : this(ranges, states, options,
            new BoundedBScanVisualBufferPool((options ?? new BScanOptions()).VisualMemoryBudgetBytes))
    { }

    internal BScanIngress(IInspectionRangeProvider ranges, IInspectionStreamStateProvider states,
        BScanOptions? options, BoundedBScanVisualBufferPool visualBuffers)
    {
        _ranges = ranges ?? throw new ArgumentNullException(nameof(ranges));
        _states = states ?? throw new ArgumentNullException(nameof(states));
        _options = options ?? new();
        _visualBuffers = visualBuffers ?? throw new ArgumentNullException(nameof(visualBuffers));
        _reconcileWorker = ReconcileWorkerAsync();
    }

    public void OpenRun(AcquisitionRunId runId)
    {
        if (!runId.IsValid) { throw new ArgumentException("A valid run is required.", nameof(runId)); }
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_phase is not (BScanRunProjectionPhase.Closed or BScanRunProjectionPhase.Frozen))
            { throw new InvalidOperationException("A B-Scan run is already open or draining."); }
            if (_activeOperations != 0)
            { throw new InvalidOperationException("The previous B-Scan run is still closing."); }
            // The stable drain completes in the worker's finally block after no admitted
            // operation remains.
            _runId = runId;
            _runCancellation?.Dispose();
            _runCancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            _runEpoch++;
            _stream = null;
            _targetRevision = InspectionRevision.Initial;
            _requiredFinalRevision = null;
            _identityRevisions.Clear();
            _visualState = null;
            _workerError = null;
            _reconcilePending = false;
            _reconcileScheduled = false;
            _drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _phase = BScanRunProjectionPhase.Open;
        }
        NotifyVisualStateChanged();
    }

    public void NotifyCommitted(TraceIdentity identity, InspectionRevision revision)
    {
        if (!identity.IsValid) { throw new ArgumentException("A valid identity is required.", nameof(identity)); }
        if (revision == InspectionRevision.Initial)
        { throw new ArgumentException("A committed revision is required.", nameof(revision)); }
        lock (_gate)
        {
            if (_phase != BScanRunProjectionPhase.Open || _disposed ||
                _runId != identity.Stream.AcquisitionRunId) { return; }
            if (_stream is { } existing && existing != identity.Stream)
            { throw new InvalidOperationException("One B-Scan projection cannot combine inspection streams."); }
            _stream = identity.Stream;
            if (revision.Value > _targetRevision.Value) { _targetRevision = revision; }
            _reconcilePending = true;
            ScheduleReconcileUnderLock();
        }
    }

    public void CloseRun(AcquisitionRunId runId)
    {
        StopAdmission(runId);
    }

    public ValueTask WaitForCloseAsync(AcquisitionRunId runId)
    {
        return WaitForDrainAsync(runId);
    }

    public void RequestFinalReconciliation(AcquisitionRunId runId, InspectionRevision finalRevision)
    {
        lock (_gate)
        {
            if (_runId != runId || _phase != BScanRunProjectionPhase.Open) { return; }
            _requiredFinalRevision = finalRevision;
            if (finalRevision.Value > _targetRevision.Value) { _targetRevision = finalRevision; }
            if (_stream is null)
            {
                _reconcilePending = false;
                return;
            }
            _reconcilePending = true;
            ScheduleReconcileUnderLock();
        }
    }

    public void StopAdmission(AcquisitionRunId runId)
    {
        bool changed = false;
        lock (_gate)
        {
            if (_runId != runId || _phase != BScanRunProjectionPhase.Open) { return; }
            _phase = BScanRunProjectionPhase.AdmissionClosed;
            if (_visualState is { } current)
            { _visualState = current with { Phase = _phase }; changed = true; }
            TryCompleteDrainUnderLock();
        }
        if (changed) { NotifyVisualStateChanged(); }
    }

    public ValueTask WaitForDrainAsync(AcquisitionRunId runId)
    {
        bool changed = false;
        lock (_gate)
        {
            if (_runId != runId) { return ValueTask.CompletedTask; }
            if (_phase == BScanRunProjectionPhase.Open)
            { throw new InvalidOperationException("B-Scan admission must be stopped before draining."); }
            if (_phase == BScanRunProjectionPhase.AdmissionClosed)
            {
                _phase = BScanRunProjectionPhase.Draining;
                if (_visualState is { } current)
                { _visualState = current with { Phase = _phase }; changed = true; }
            }
            TryCompleteDrainUnderLock();
            ValueTask drain = new(_drained.Task);
            if (changed)
            {
                ThreadPool.QueueUserWorkItem(static owner =>
                ((BScanIngress)owner!).NotifyVisualStateChanged(), this);
            }
            return drain;
        }
    }

    public void FreezeRun(AcquisitionRunId runId)
    {
        bool changed = false;
        lock (_gate)
        {
            if (_runId != runId) { return; }
            if (_phase != BScanRunProjectionPhase.Draining)
            { throw new InvalidOperationException("B-Scan can only freeze after entering the draining phase."); }
            if (!_drained.Task.IsCompletedSuccessfully)
            { throw new InvalidOperationException("B-Scan cannot freeze before the drain barrier completes."); }
            _phase = BScanRunProjectionPhase.Frozen;
            if (_visualState is { } current)
            { _visualState = current with { Phase = _phase }; changed = true; }
        }
        if (changed) { NotifyVisualStateChanged(); }
    }

    private void ScheduleReconcileUnderLock()
    {
        if (_disposed || _stream is null || !_reconcilePending || _reconcileScheduled ||
            _runCancellation is null || _phase is not (BScanRunProjectionPhase.Open or
                BScanRunProjectionPhase.AdmissionClosed or BScanRunProjectionPhase.Draining)) { return; }
        _reconcileScheduled = true;
        _activeOperations++;
        _reconcileWake.Release();
    }

    private async Task ReconcileWorkerAsync()
    {
        try
        {
            while (true)
            {
                await _reconcileWake.WaitAsync(_lifetime.Token).ConfigureAwait(false);
                while (true)
                {
                    CancellationToken token;
                    bool mayBecomeIdle;
                    lock (_gate)
                    {
                        if (_disposed) { return; }
                        mayBecomeIdle = !_reconcilePending;
                        token = _runCancellation?.Token ?? _lifetime.Token;
                        if (!mayBecomeIdle) { _reconcilePending = false; }
                    }
                    if (mayBecomeIdle)
                    {
                        BeforeReconcileWorkerExit?.Invoke();
                        lock (_gate)
                        {
                            if (_reconcilePending) { continue; }
                            _reconcileScheduled = false;
                            _activeOperations--;
                            TryCompleteDrainUnderLock();
                            break;
                        }
                    }
                    await ReconcileAsync(token).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        finally
        {
            lock (_gate)
            {
                if (_reconcileScheduled)
                {
                    _reconcileScheduled = false;
                    _activeOperations--;
                }
                TryCompleteDrainUnderLock();
            }
        }
    }

    private async Task ReconcileAsync(CancellationToken token)
    {
        try
        {
            while (true)
            {
                UtStreamIdentity stream;
                InspectionRevision target;
                lock (_gate)
                {
                    if (_disposed || _stream is not { } current ||
                        _phase is not (BScanRunProjectionPhase.Open or
                            BScanRunProjectionPhase.AdmissionClosed or BScanRunProjectionPhase.Draining))
                    { return; }
                    stream = current;
                    target = _targetRevision;
                }
                InspectionStreamStateReadResult state = await _states.ReadStreamStateAsync(stream, token)
                    .ConfigureAwait(false);
                if (!state.IsAvailable || state.Snapshot is not { } snapshot) { return; }
                if (snapshot.CommittedTraceCount == 0 || snapshot.MinimumCommittedAcquisitionIndex is null ||
                    snapshot.MaximumCommittedAcquisitionIndex is null) { return; }

                ulong last = snapshot.MaximumCommittedAcquisitionIndex.Value;
                ulong distance = last - snapshot.MinimumCommittedAcquisitionIndex.Value;
                ulong span = distance >= (ulong)_options.MaximumColumns - 1
                    ? (ulong)_options.MaximumColumns : distance + 1;
                ulong first = last - span + 1;
                ConventionalUtFrameMetadata? metadata = null;
                var identities = new List<TraceIdentity>((int)span);
                ulong cursor = first;
                ulong remaining = span;
                bool revisionRegressed = false;
                while (remaining != 0)
                {
                    int count = (int)Math.Min(remaining, (ulong)InspectionRangeBatchSize);
                    using InspectionRangeReadResult range = await _ranges.ReadRangeAsync(
                        new(stream, cursor, count), token).ConfigureAwait(false);
                    // Reconciliation is bounded by the stream-state watermarks. Committed identities
                    // are append-only and AcquisitionIndex is strictly increasing, so a later page
                    // revision cannot change this captured prefix. Requiring equality would starve
                    // whenever acquisition advances between pages; raster projection remains stricter.
                    if (range.Revision.Value < snapshot.Revision.Value)
                    { revisionRegressed = true; break; }
                    foreach (InspectionRangeItem available in range.Items.Where(static item =>
                        item.Status == InspectionReadStatus.Available && item.Lease is not null))
                    {
                        metadata ??= CreateMetadata(available.Lease!.Descriptor);
                        identities.Add(available.Identity);
                    }
                    remaining -= (ulong)count;
                    if (remaining != 0) { cursor = checked(cursor + (ulong)count); }
                }
                if (revisionRegressed) { continue; }
                if (metadata is null) { return; }
                var storeState = new BScanStoreVisualState(snapshot.Revision.Value, 0,
                    new(snapshot.MinimumCommittedAcquisitionIndex.Value, last), snapshot.RunState,
                    snapshot.FinalRevision?.Value, snapshot.AreWatermarksFinal,
                    snapshot.CommittedTraceCount, snapshot.SequenceGapCount);
                BeforeVisualStateCommit?.Invoke();
                bool reconcileAgain;
                lock (_gate)
                {
                    if (_disposed || _stream != stream) { return; }
                    var residentIdentities = identities.ToHashSet();
                    foreach (TraceIdentity retired in _identityRevisions.Keys
                        .Where(identity => !residentIdentities.Contains(identity)).ToArray())
                    { _identityRevisions.Remove(retired); }
                    foreach (TraceIdentity identity in identities)
                    { _identityRevisions.TryAdd(identity, snapshot.Revision.Value); }
                    _visualState = new(checked(++_epoch), _runEpoch, metadata, stream, storeState, _phase);
                    reconcileAgain = _targetRevision.Value > snapshot.Revision.Value ||
                        target.Value > snapshot.Revision.Value;
                }
                NotifyVisualStateChanged();
                if (!reconcileAgain) { return; }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception error)
        {
            lock (_gate) { _workerError ??= Message(error); }
            NotifyVisualStateChanged();
        }
    }

    private const int InspectionRangeBatchSize = 256;

    internal async ValueTask<BScanPageTraversalResult> VisitVisualPagesAsync(BScanProjectionTicket ticket,
        BScanAcquisitionRange range,
        Action<InspectionRangeReadResult> visit, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(visit);
        var statuses = new HashSet<InspectionReadStatus>();
        ulong distance = range.Last - range.First;
        ulong span = distance >= (ulong)_options.MaximumColumns - 1
            ? (ulong)_options.MaximumColumns : distance + 1;
        ulong cursor = range.First;
        ulong remaining = span;
        int pages = 0;
        int availableTotal = 0;
        while (remaining != 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int count = (int)Math.Min(remaining, (ulong)InspectionRangeBatchSize);
            InspectionRangeReadResult? result = null;
            int pageLeases = 0;
            try
            {
                result = await _ranges.ReadRangeAsync(new(ticket.Stream, cursor, count), cancellationToken)
                    .ConfigureAwait(false);
                Interlocked.Increment(ref _rangeReads);
                pages++;
                statuses.Add(result.Status);
                foreach (InspectionRangeItem item in result.Items) { statuses.Add(item.Status); }
                if (result.Revision.Value != ticket.AuthoritativeRevision)
                { return new(statuses, null, true, pages, availableTotal); }
                if (result.Status is InspectionReadStatus.Closed or InspectionReadStatus.Cancelled or
                    InspectionReadStatus.Faulted or InspectionReadStatus.OutOfRange)
                { return new(statuses, result.Status, false, pages, availableTotal); }
                pageLeases = result.Items.Count(static item =>
                    item.Status == InspectionReadStatus.Available && item.Lease is not null);
                int active = Interlocked.Add(ref _activeLeases, pageLeases);
                UpdateMaximum(ref _maximumLeases, active);
                availableTotal = checked(availableTotal + pageLeases);
                visit(result);
            }
            finally
            {
                try { result?.Dispose(); }
                finally
                {
                    if (pageLeases != 0) { Interlocked.Add(ref _activeLeases, -pageLeases); }
                }
            }
            remaining -= (ulong)count;
            if (remaining != 0) { cursor = checked(cursor + (ulong)count); }
        }
        return new(statuses, null, false, pages, availableTotal);
    }

    internal ulong GetVisualRevision(TraceIdentity identity)
    { lock (_gate) { return _identityRevisions.GetValueOrDefault(identity); } }

    internal bool TryGetDirtyRange(UtStreamIdentity stream, BScanAcquisitionRange represented,
        ulong displayedRevision, out BScanAcquisitionRange dirty)
    {
        ulong first = ulong.MaxValue;
        ulong last = 0;
        lock (_gate)
        {
            foreach ((TraceIdentity identity, ulong revision) in _identityRevisions)
            {
                ulong index = identity.AcquisitionIndex;
                if (identity.Stream != stream || revision <= displayedRevision ||
                    index < represented.First || index > represented.Last) { continue; }
                first = Math.Min(first, index);
                last = Math.Max(last, index);
            }
        }
        if (first == ulong.MaxValue) { dirty = default; return false; }
        dirty = new(first, last);
        return true;
    }

    private static ConventionalUtFrameMetadata CreateMetadata(TraceDescriptor descriptor)
    {
        var configuration = new ConventionalAcquisitionConfiguration(descriptor.Identity.Stream.PhysicalChannelId,
            descriptor.SampleCount, descriptor.SampleRateHz, descriptor.FirstSampleOffsetSeconds,
            descriptor.SignalMode);
        DateTimeOffset started = (descriptor.TimestampUtc ?? DateTimeOffset.UnixEpoch + descriptor.ElapsedSinceRunStart) -
            descriptor.ElapsedSinceRunStart;
        return new(descriptor.Identity.Stream.SourceId, descriptor.Identity.Stream.AcquisitionRunId,
            configuration, started);
    }

    internal BoundedBScanVisualBufferPool VisualBuffers => _visualBuffers;
    internal BScanOptions Options => _options;
    internal CancellationToken RunCancellationToken
    { get { lock (_gate) { return _runCancellation?.Token ?? _lifetime.Token; } } }
    internal BScanIngressVisualState? CurrentVisualState => Volatile.Read(ref _visualState);
    internal bool TryBeginProjection(out BScanIngressVisualState? state,
        out BScanProjectionOperation? operation)
    {
        lock (_gate)
        {
            state = _visualState;
            if (_disposed || state is null || _phase is not (BScanRunProjectionPhase.Open or
                BScanRunProjectionPhase.Frozen))
            { operation = null; return false; }
            _activeOperations++;
            operation = new(this, state.RunGeneration);
            return true;
        }
    }

    internal bool IsTicketCurrent(BScanProjectionTicket ticket)
    {
        lock (_gate)
        {
            if (_disposed || _phase is not (BScanRunProjectionPhase.Open or BScanRunProjectionPhase.Frozen) ||
                _runEpoch != ticket.RunGeneration || _visualState is not { } current ||
                _phase != ticket.Phase || current.StreamIdentity != ticket.Stream ||
                current.StoreState.RunState != ticket.RunState ||
                current.StoreState.FinalRevision != ticket.FinalRevision ||
                current.StoreState.AreWatermarksFinal != ticket.AreWatermarksFinal)
            { return false; }
            if (current.Epoch == ticket.StateEpoch &&
                _targetRevision.Value == ticket.AuthoritativeRevision &&
                current.StoreState.StoreRevision == ticket.AuthoritativeRevision &&
                current.StoreState.ResidentRange == ticket.ResidentRange)
            { return true; }
            if (!ticket.FollowLiveWindow || current.StoreState.StoreRevision < ticket.AuthoritativeRevision ||
                current.StoreState.ResidentRange is not { } resident)
            { return false; }
            decimal firstValue = decimal.Floor(ticket.SpatialViewport.OriginAcquisitionIndex +
                ticket.SpatialViewport.MinimumOffset);
            decimal lastValue = decimal.Ceiling(ticket.SpatialViewport.OriginAcquisitionIndex +
                ticket.SpatialViewport.MaximumOffset) - 1;
            ulong first = firstValue <= 0 ? 0 : firstValue >= ulong.MaxValue
                ? ulong.MaxValue : (ulong)firstValue;
            ulong last = lastValue <= 0 ? 0 : lastValue >= ulong.MaxValue
                ? ulong.MaxValue : (ulong)lastValue;
            ulong representedFirst = Math.Max(first, ticket.ResidentRange.First);
            ulong representedLast = Math.Min(last, ticket.ResidentRange.Last);
            return representedFirst <= representedLast && representedFirst >= resident.First &&
                representedLast <= resident.Last;
        }
    }

    internal void EndProjection(long runGeneration)
    {
        lock (_gate)
        {
            if (_runEpoch != runGeneration)
            { throw new InvalidOperationException("B-Scan projection completed against a different run generation."); }
            if (_activeOperations <= 0) { throw new InvalidOperationException("B-Scan operation ledger underflow."); }
            _activeOperations--;
            if (_disposed && _activeOperations == 0) { _disposeDrained.TrySetResult(); }
            TryCompleteDrainUnderLock();
        }
    }

    private void TryCompleteDrainUnderLock()
    {
        if (_phase is not (BScanRunProjectionPhase.AdmissionClosed or BScanRunProjectionPhase.Draining) ||
            _activeOperations != 0 ||
            _reconcilePending) { return; }
        if (_stream is not null && _requiredFinalRevision is { } required &&
            (_visualState?.StoreState.StoreRevision ?? 0) != required.Value)
        {
            _drained.TrySetException(new InvalidOperationException(
                $"B-Scan did not reconcile final revision {required.Value}."));
            return;
        }
        if (_workerError is { } error)
        { _drained.TrySetException(new InvalidOperationException(error)); return; }
        _drained.TrySetResult();
    }

    private static TaskCompletionSource CompletedDrain()
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        completion.SetResult();
        return completion;
    }
    public BScanDiagnostics Diagnostics => new(_rangeReads, 0, 0, 0, 0, 0,
        Volatile.Read(ref _activeLeases), Volatile.Read(ref _maximumLeases), 0,
        new(0, TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero), 0, 0, 0, 0,
        0, _options.MaximumColumns, _workerError, [], _disposed);

    internal void Stop()
    {
        lock (_gate)
        {
            if (_disposed) { return; }
            _disposed = true;
            _phase = BScanRunProjectionPhase.Disposed;
            _lifetime.Cancel();
            if (_activeOperations == 0) { _disposeDrained.TrySetResult(); }
        }
    }

    internal Task WaitForDrainAsync(CancellationToken cancellationToken = default)
    {
        Stop();
        Task drain;
        lock (_gate) { drain = Task.WhenAll(_reconcileWorker, _disposeDrained.Task); }
        return cancellationToken.CanBeCanceled ? drain.WaitAsync(cancellationToken) : drain;
    }

    public ValueTask DisposeAsync()
    {
        lock (_gate) { return new(_disposeTask ??= DisposeCoreAsync()); }
    }

    private async Task DisposeCoreAsync()
    {
        Stop();
        await WaitForDrainAsync().ConfigureAwait(false);
        await _visualBuffers.DisposeAsync().ConfigureAwait(false);
        _reconcileWake.Dispose();
        _runCancellation?.Dispose();
        _lifetime.Dispose();
    }

    private void NotifyVisualStateChanged()
    {
        foreach (Action observer in VisualStateChanged?.GetInvocationList() ?? [])
        { try { observer(); } catch { } }
    }

    private static void UpdateMaximum(ref int target, int value)
    {
        int observed;
        while (value > (observed = Volatile.Read(ref target)) &&
            Interlocked.CompareExchange(ref target, value, observed) != observed) { }
    }

    private static string Message(Exception error) =>
        string.IsNullOrWhiteSpace(error.Message) ? error.GetType().Name : error.Message;
}

internal sealed record BScanIngressVisualState(
    long Epoch,
    long RunGeneration,
    ConventionalUtFrameMetadata Metadata,
    UtStreamIdentity StreamIdentity,
    BScanStoreVisualState StoreState,
    BScanRunProjectionPhase Phase)
{
    internal bool IsOpen => Phase == BScanRunProjectionPhase.Open;
    internal bool IsProjectable => Phase is BScanRunProjectionPhase.Open or BScanRunProjectionPhase.Frozen;
    internal BScanStreamIdentity Stream => new(Metadata.SourceId, Metadata.RunId,
        Metadata.Configuration.PhysicalChannelId);
}

internal enum BScanRunProjectionPhase { Closed, Open, AdmissionClosed, Draining, Frozen, Disposed }

internal sealed class BScanProjectionOperation(BScanIngress owner, long runGeneration) : IDisposable
{
    private BScanIngress? _owner = owner;
    public void Dispose() => Interlocked.Exchange(ref _owner, null)?.EndProjection(runGeneration);
}
