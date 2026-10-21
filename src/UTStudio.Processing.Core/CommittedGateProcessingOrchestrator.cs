using System.Buffers;
using UTStudio.Contracts.Inspection;
using UTStudio.Contracts.Processing;
using UTStudio.Domain.Acquisition;
using UTStudio.Domain.Inspection;

namespace UTStudio.Processing.Core;

public sealed record GateProcessingOptions
{
    public GateProcessingOptions(int maximumRuns = 2, int maximumStreamsPerRun = 8,
        int maximumPlanEpochsPerRun = 8, int catalogPageSize = 256, int maximumTerminalRunSummaries = 8,
        int maximumEligibleTracesPerPlanEpoch = 65_536)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumRuns, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumStreamsPerRun, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumPlanEpochsPerRun, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(catalogPageSize, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(catalogPageSize, 4096);
        ArgumentOutOfRangeException.ThrowIfNegative(maximumTerminalRunSummaries);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumEligibleTracesPerPlanEpoch, 1);
        MaximumRuns = maximumRuns; MaximumStreamsPerRun = maximumStreamsPerRun;
        MaximumPlanEpochsPerRun = maximumPlanEpochsPerRun; CatalogPageSize = catalogPageSize;
        MaximumTerminalRunSummaries = maximumTerminalRunSummaries;
        MaximumEligibleTracesPerPlanEpoch = maximumEligibleTracesPerPlanEpoch;
    }
    public int MaximumRuns { get; }
    public int MaximumStreamsPerRun { get; }
    public int MaximumPlanEpochsPerRun { get; }
    public int CatalogPageSize { get; }
    public int MaximumTerminalRunSummaries { get; }
    public int MaximumEligibleTracesPerPlanEpoch { get; }
    public int MaximumMetadataUnits => checked(MaximumRuns * (MaximumStreamsPerRun + MaximumPlanEpochsPerRun));
}

internal readonly record struct GatePlanEpochTerminalTombstone(
    GatePreparedReservationId Id,
    GatePreparedReservationOwner Owner,
    GatePreparedOperationGeneration Generation,
    GatePreparedReservationState State,
    AcquisitionRunId RunId)
{
    internal GatePreparedReservation Snapshot() => new(Id, Owner, Generation, State);
}

/// <summary>Bounded, sample-free post-commit reconciler. One worker preserves revision order.</summary>
public sealed class CommittedGateProcessingOrchestrator : ICommittedGateProcessingIngress,
    IPreparedGatePlanEpochPort, IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly IInspectionChangeProvider _catalog;
    private readonly IUtTraceProvider _traces;
    private readonly IGateEvaluationBatchSink _sink;
    private readonly GateEvaluator _evaluator;
    private readonly GateProcessingOptions _options;
    private readonly Dictionary<AcquisitionRunId, RunState> _runs = [];
    private readonly Dictionary<GatePreparedReservationId, PreparedEpoch> _preparedEpochs = [];
    private readonly GatePlanEpochTerminalTombstone[] _terminalEpochTombstones;
    private readonly Queue<GateProcessingRunDiagnostics> _terminalRuns = [];
    private readonly SemaphoreSlim _wake = new(0, 1);
    private readonly CancellationTokenSource _exceptionalCancellation = new();
    private readonly Task _worker;
    private Task? _disposeTask;
    private bool _disposing;
    private GateProcessingLifecycleState _state = GateProcessingLifecycleState.Created;
    private long _signals, _coalesced, _duplicates, _late, _admitted, _rejected, _saturations,
        _observed, _reconciled, _available, _notAvailable, _gap, _outOfRange, _cancelled, _closed, _faulted,
        _leasesAcquired, _leasesReleased, _evalStarted, _evalCompleted, _evalCancelled, _evalFailed,
        _batchesAccepted, _batchesRejected, _batchesFailed, _callbacksAdmitted, _callbacksCompleted,
        _drainedRuns, _failedRuns;
    private int _activeLeases, _activeWorkers, _activeCallbacks;
    private int _peakSchedulerDepth;
    private int _terminalEpochTombstoneCount;
    private GateProcessingError? _primaryError;
    private readonly List<GateProcessingError> _secondaryErrors = [];

    public CommittedGateProcessingOrchestrator(IInspectionChangeProvider catalog, IUtTraceProvider traces,
        IGateEvaluationBatchSink sink, GateEvaluator evaluator, GateProcessingOptions? options = null)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _traces = traces ?? throw new ArgumentNullException(nameof(traces));
        _sink = sink ?? throw new ArgumentNullException(nameof(sink));
        _evaluator = evaluator ?? throw new ArgumentNullException(nameof(evaluator));
        _options = options ?? new();
        _terminalEpochTombstones = new GatePlanEpochTerminalTombstone[_options.MaximumRuns];
        _state = GateProcessingLifecycleState.Running;
        _worker = WorkerLoopAsync();
    }

    public GateProcessingAdmissionStatus OpenRun(GateProcessingRunRegistration registration)
    {
        ArgumentNullException.ThrowIfNull(registration);
        lock (_gate)
        {
            if (_disposing) { _rejected++; return GateProcessingAdmissionStatus.Closed; }
            if (_runs.ContainsKey(registration.RunId)) { _duplicates++; return GateProcessingAdmissionStatus.Duplicate; }
            if (_runs.Count >= _options.MaximumRuns || registration.Streams.Count > _options.MaximumStreamsPerRun)
            { _rejected++; _saturations++; return GateProcessingAdmissionStatus.Rejected; }
            _runs.Add(registration.RunId, new(registration, _options.MaximumPlanEpochsPerRun));
            _peakSchedulerDepth = Math.Max(_peakSchedulerDepth, _runs.Count);
            Pulse();
            return GateProcessingAdmissionStatus.Accepted;
        }
    }

    public GateProcessingAdmissionStatus ActivatePlan(AcquisitionRunId runId, CompiledGatePlan plan,
        InspectionRevision effectiveFromInclusive)
    {
        ArgumentNullException.ThrowIfNull(plan);
        lock (_gate)
        {
            GatePreparedReservationOwner owner = new(Guid.NewGuid());
            var generation = new GatePreparedOperationGeneration(1);
            InspectionId inspectionId = _runs.TryGetValue(runId, out RunState? run)
                ? run.Registration.InspectionId
                : default;
            GatePlanEpochPrepareResult prepared = PrepareEpochUnderGate(inspectionId, runId, plan,
                effectiveFromInclusive, owner, generation);
            if (!prepared.IsPrepared)
            {
                _rejected++;
                if (prepared.Status is GatePlanEpochPrepareStatus.InvalidBoundary or
                    GatePlanEpochPrepareStatus.DuplicatePlan)
                    _saturations++;
                return GateProcessingAdmissionStatus.Rejected;
            }
            GatePreparedReservationResult committed = ConsumeEpochUnderGate(prepared.Reservation,
                owner, generation, GatePreparedReservationOperation.Commit);
            if (committed.Status == GatePreparedReservationResultStatus.Committed)
                return GateProcessingAdmissionStatus.Accepted;
            _rejected++;
            return GateProcessingAdmissionStatus.Rejected;
        }
    }

    public GatePlanEpochPrepareResult PrepareEpoch(InspectionId inspectionId, AcquisitionRunId runId,
        CompiledGatePlan plan, InspectionRevision effectiveFromInclusive,
        GatePreparedReservationOwner owner, GatePreparedOperationGeneration generation)
    {
        lock (_gate)
            return PrepareEpochUnderGate(inspectionId, runId, plan, effectiveFromInclusive, owner, generation);
    }

    private GatePlanEpochPrepareResult PrepareEpochUnderGate(InspectionId inspectionId, AcquisitionRunId runId,
        CompiledGatePlan plan, InspectionRevision effectiveFromInclusive,
        GatePreparedReservationOwner owner, GatePreparedOperationGeneration generation)
    {
        if (!inspectionId.IsValid || !runId.IsValid || !owner.IsValid || !generation.IsValid)
            return GatePlanEpochPrepareResult.Rejected(GatePlanEpochPrepareStatus.InvalidIdentity);
        if (plan is null || !plan.Source.Id.IsValid || !plan.Source.Revision.IsValid)
            return GatePlanEpochPrepareResult.Rejected(GatePlanEpochPrepareStatus.InvalidPlan);
        if (effectiveFromInclusive == InspectionRevision.Initial)
            return GatePlanEpochPrepareResult.Rejected(GatePlanEpochPrepareStatus.InvalidBoundary);

        if (_disposing)
            return GatePlanEpochPrepareResult.Rejected(GatePlanEpochPrepareStatus.Disposed);
        if (!_runs.TryGetValue(runId, out RunState? run))
            return GatePlanEpochPrepareResult.Rejected(GatePlanEpochPrepareStatus.RunNotFound);
        if (run.Registration.InspectionId != inspectionId)
            return GatePlanEpochPrepareResult.Rejected(GatePlanEpochPrepareStatus.InvalidIdentity);
        if (run.AdmissionClosed || run.FinalRevision is not null || run.Failed)
            return GatePlanEpochPrepareResult.Rejected(GatePlanEpochPrepareStatus.RunClosed);
        if (run.PreparedEpoch is not null)
        {
            _saturations++;
            return GatePlanEpochPrepareResult.Rejected(GatePlanEpochPrepareStatus.ProvisionalCapacityExceeded);
        }
        if (run.Epochs.Count >= run.MaximumEpochs)
        {
            _saturations++;
            return GatePlanEpochPrepareResult.Rejected(GatePlanEpochPrepareStatus.LiveEpochLimitReached);
        }
        if (effectiveFromInclusive.Value <= run.Epochs[^1].From.Value ||
            effectiveFromInclusive.Value <= run.Target.Value)
            return GatePlanEpochPrepareResult.Rejected(GatePlanEpochPrepareStatus.InvalidBoundary);
        if (run.Epochs.Any(x => x.Plan.Source.Id == plan.Source.Id &&
            x.Plan.Source.Revision == plan.Source.Revision && x.Plan.Signature == plan.Signature))
            return GatePlanEpochPrepareResult.Rejected(GatePlanEpochPrepareStatus.DuplicatePlan);

        run.Epochs.EnsureCapacity(run.Epochs.Count + 1);
        _preparedEpochs.EnsureCapacity(_preparedEpochs.Count + 1);
        var epoch = new PlanEpoch(effectiveFromInclusive, plan, new());
        var id = new GatePreparedReservationId(Guid.NewGuid());
        var prepared = new PreparedEpoch(id, owner, generation, run, epoch);
        _preparedEpochs.Add(id, prepared);
        run.PreparedEpoch = prepared;
        RemoveTerminalTombstone(runId);
        return GatePlanEpochPrepareResult.Prepared(prepared.Snapshot());
    }

    public GatePreparedReservationResult CommitEpoch(GatePreparedReservation reservation,
        GatePreparedReservationOwner owner, GatePreparedOperationGeneration generation)
    {
        lock (_gate)
            return ConsumeEpochUnderGate(reservation, owner, generation, GatePreparedReservationOperation.Commit);
    }

    public GatePreparedReservationResult AbortEpoch(GatePreparedReservation reservation,
        GatePreparedReservationOwner owner, GatePreparedOperationGeneration generation)
    {
        lock (_gate)
            return ConsumeEpochUnderGate(reservation, owner, generation, GatePreparedReservationOperation.Abort);
    }

    private GatePreparedReservationResult ConsumeEpochUnderGate(GatePreparedReservation reservation,
        GatePreparedReservationOwner owner, GatePreparedOperationGeneration generation,
        GatePreparedReservationOperation operation)
    {
        if (!reservation.IsValid)
            return GatePreparedReservationResult.Classify(default, owner, generation, operation);
        if (!_preparedEpochs.TryGetValue(reservation.Id, out PreparedEpoch? prepared))
        {
            GatePreparedReservation terminal = FindTerminalTombstone(reservation.Id);
            if (!terminal.IsValid)
                return GatePreparedReservationResult.Classify(default, owner, generation, operation);
            GatePreparedReservationResult replay = GatePreparedReservationResult.Classify(
                terminal, owner, generation, operation);
            if (replay.Status == GatePreparedReservationResultStatus.OwnerOrGenerationMismatch)
                return replay;
            if (reservation.Owner != terminal.Owner || reservation.Generation != terminal.Generation)
                return GatePreparedReservationResult.Classify(
                    terminal, reservation.Owner, reservation.Generation, operation);
            return replay;
        }
        GatePreparedReservation current = prepared.Snapshot();
        GatePreparedReservationResult classified = GatePreparedReservationResult.Classify(
            current, owner, generation, operation);
        if (classified.Status == GatePreparedReservationResultStatus.OwnerOrGenerationMismatch)
            return classified;
        if (reservation.Owner != current.Owner || reservation.Generation != current.Generation)
            return GatePreparedReservationResult.Classify(current,
                reservation.Owner, reservation.Generation, operation);
        if (classified.Status == GatePreparedReservationResultStatus.Committed)
        {
            prepared.Run.Epochs.Add(prepared.Epoch);
            prepared.Run.PreparedEpoch = null;
            _preparedEpochs.Remove(prepared.Id);
            StoreTerminalTombstone(prepared, GatePreparedReservationState.Committed);
        }
        else if (classified.Status == GatePreparedReservationResultStatus.Aborted)
        {
            prepared.Run.PreparedEpoch = null;
            _preparedEpochs.Remove(prepared.Id);
            StoreTerminalTombstone(prepared, GatePreparedReservationState.Aborted);
        }
        return classified;
    }

    public GateProcessingAdmissionStatus NotifyCommitted(GateProcessingSignal signal)
    {
        if (!signal.IsValid) { throw new ArgumentException("A valid sample-free signal is required.", nameof(signal)); }
        lock (_gate)
        {
            _signals++;
            if (_disposing) { _rejected++; return GateProcessingAdmissionStatus.Closed; }
            if (!_runs.TryGetValue(signal.TraceIdentity.Stream.AcquisitionRunId, out RunState? run) ||
                !run.Streams.Contains(signal.TraceIdentity.Stream))
            { _rejected++; return GateProcessingAdmissionStatus.Rejected; }
            if (run.AdmissionClosed || run.FinalRevision is not null) { _late++; return GateProcessingAdmissionStatus.Late; }
            if (signal.Revision.Value <= run.Cursor.Value) { _duplicates++; return GateProcessingAdmissionStatus.Duplicate; }
            GateProcessingAdmissionStatus status = signal.Revision.Value <= run.Target.Value
                ? GateProcessingAdmissionStatus.Coalesced : GateProcessingAdmissionStatus.Accepted;
            if (status == GateProcessingAdmissionStatus.Coalesced) { _coalesced++; }
            else { run.Target = signal.Revision; _observed = Math.Max(_observed, checked((long)signal.Revision.Value)); }
            run.Observed = new(Math.Max(run.Observed.Value, signal.Revision.Value));
            InspectionRevision watermark = run.Watermarks[signal.TraceIdentity.Stream];
            if (signal.Revision.Value > watermark.Value)
            { run.Watermarks[signal.TraceIdentity.Stream] = signal.Revision; }
            Pulse();
            return status;
        }
    }

    public void RequestFinalReconciliation(AcquisitionRunId runId, InspectionRevision finalRevision)
    {
        lock (_gate)
        {
            if (!_runs.TryGetValue(runId, out RunState? run)) { throw new InvalidOperationException("The processing run is not registered."); }
            if (run.FinalRevision is { } existing)
            {
                if (existing != finalRevision)
                { throw new InvalidOperationException("FinalRevision is immutable once published."); }
                return;
            }
            if (finalRevision.Value < run.Cursor.Value ||
                finalRevision.Value < run.Target.Value)
            { throw new ArgumentException("FinalRevision cannot precede admitted or reconciled work.", nameof(finalRevision)); }
            AbortPreparedEpoch(run);
            run.FinalRevision = finalRevision; run.Target = finalRevision;
            run.Observed = new(Math.Max(run.Observed.Value, finalRevision.Value));
            run.Lifecycle = GateProcessingLifecycleState.FinalReconciling;
            _state = GateProcessingLifecycleState.FinalReconciling;
            Pulse();
        }
    }

    /// <summary>Reactivates a cursor suspended by provisional NotAvailable, without polling.</summary>
    public void NotifyProviderProgress(AcquisitionRunId runId)
    {
        lock (_gate) { if (_runs.ContainsKey(runId) && !_disposing) { Pulse(); } }
    }

    public void StopAdmission(AcquisitionRunId runId)
    {
        lock (_gate)
        {
            if (!_runs.TryGetValue(runId, out RunState? run)) { return; }
            AbortPreparedEpoch(run);
            run.AdmissionClosed = true;
            run.Lifecycle = GateProcessingLifecycleState.AdmissionClosed;
            _state = GateProcessingLifecycleState.AdmissionClosed;
            Pulse();
        }
    }

    public ValueTask WaitForDrainAsync(AcquisitionRunId runId)
    {
        lock (_gate)
        {
            if (!_runs.TryGetValue(runId, out RunState? run)) { return ValueTask.CompletedTask; }
            TryCompleteDrain(run);
            return new(AwaitDrainAndRetireAsync(runId, run));
        }
    }

    private async Task AwaitDrainAndRetireAsync(AcquisitionRunId runId, RunState run)
    {
        try { await run.Drained.Task.ConfigureAwait(false); }
        finally
        {
            lock (_gate)
            {
                if (_runs.TryGetValue(runId, out RunState? current) && ReferenceEquals(current, run))
                {
                    GateProcessingRunDiagnostics terminal = SnapshotRun(run);
                    _runs.Remove(runId);
                    if (_options.MaximumTerminalRunSummaries > 0)
                    {
                        while (_terminalRuns.Count >= _options.MaximumTerminalRunSummaries) { _terminalRuns.Dequeue(); }
                        _terminalRuns.Enqueue(terminal);
                    }
                }
            }
        }
    }

    public GateProcessingDiagnostics Diagnostics
    {
        get
        {
            lock (_gate)
            {
                return new(SnapshotLifecycle(), _signals, _coalesced, _duplicates, _late, _admitted, _rejected,
                    _saturations, _observed, _reconciled, _available, _notAvailable, _gap, _outOfRange,
                    _cancelled, _closed, _faulted, _leasesAcquired, _leasesReleased, _activeLeases,
                    _evalStarted, _evalCompleted, _evalCancelled, _evalFailed, _batchesAccepted,
                    _batchesRejected, _batchesFailed, _callbacksAdmitted, _callbacksCompleted,
                    _activeWorkers, _activeCallbacks,
                    _runs.Values.Count(x => x.Cursor.Value < x.Target.Value), _peakSchedulerDepth, _options.MaximumRuns,
                    _runs.Values.Count(x => !x.AdmissionClosed), _drainedRuns, _failedRuns,
                    Array.AsReadOnly(_runs.Values.Select(SnapshotRun).ToArray()),
                    Array.AsReadOnly(_terminalRuns.ToArray()),
                    _primaryError, Array.AsReadOnly(_secondaryErrors.ToArray()),
                    _terminalEpochTombstoneCount, _terminalEpochTombstones.Length);
            }
        }
    }

    private async Task WorkerLoopAsync()
    {
        try
        {
            while (true)
            {
                await _wake.WaitAsync(_exceptionalCancellation.Token).ConfigureAwait(false);
                RunState[] runs;
                lock (_gate) { if (_disposing) { return; } runs = _runs.Values.ToArray(); }
                foreach (RunState run in runs) { await ReconcileAsync(run).ConfigureAwait(false); }
            }
        }
        catch (OperationCanceledException) when (_exceptionalCancellation.IsCancellationRequested) { }
        catch (Exception error)
        {
            lock (_gate)
            {
                RecordError("processing.worker", error); _state = GateProcessingLifecycleState.Faulted;
                foreach (RunState run in _runs.Values) { FailRun(run, error); }
            }
        }
    }

    private async Task ReconcileAsync(RunState run)
    {
        lock (_gate)
        {
            if (run.Failed) { return; }
            _activeWorkers++; run.ActiveWorkers++; run.Lifecycle = GateProcessingLifecycleState.Running;
        }
        try
        {
            while (true)
            {
                InspectionRevision cursor, target;
                lock (_gate) { cursor = run.Cursor; target = run.Target; }
                if (cursor.Value >= target.Value) { lock (_gate) { TryCompleteDrain(run); } return; }
                var query = new InspectionChangeQuery(run.Registration.InspectionId, run.Registration.RunId,
                    cursor, target, _options.CatalogPageSize);
                InspectionChangePage page = await _catalog.ReadChangesAsync(query, _exceptionalCancellation.Token).ConfigureAwait(false);
                ValidatePage(run, cursor, target, page);
                foreach (InspectionCommittedChange change in page.Changes)
                {
                    if (change.Kind == InspectionCommittedChangeKind.Trace &&
                        !await ProcessTraceAsync(run, change).ConfigureAwait(false))
                    { return; }
                    lock (_gate) { run.Cursor = change.Revision; run.PendingRevision = default; run.PendingEpoch = null; _reconciled++; }
                }
                lock (_gate) { if (!page.HasMore) { run.Cursor = page.ScannedThroughRevision; } }
                if (!page.HasMore) { return; }
            }
        }
        catch (Exception error)
        {
            lock (_gate)
            {
                RecordError("processing.reconcile", error); FailRun(run, error);
            }
        }
        finally { lock (_gate) { _activeWorkers--; run.ActiveWorkers--; TryCompleteDrain(run); } }
    }

    private async Task<bool> ProcessTraceAsync(RunState run, InspectionCommittedChange change)
    {
        PlanEpoch? epoch;
        lock (_gate)
        {
            epoch = run.Epochs.LastOrDefault(x => x.From.Value <= change.Revision.Value);
            if (epoch is null) { _rejected++; return true; }
            if (run.PendingRevision != change.Revision)
            {
                if (epoch.EligibleIdentities.Count == _options.MaximumEligibleTracesPerPlanEpoch)
                {
                    _saturations++;
                    FailRun(run, new InvalidOperationException("The bounded terminal coverage ledger is full."));
                    return false;
                }
                run.PendingRevision = change.Revision; run.PendingEpoch = epoch;
                epoch.Balance.ConfirmedEligible++; run.WorkAdmitted++; _admitted++;
                epoch.EligibleIdentities.Add(change.TraceIdentity!.Value);
            }
            else { epoch = run.PendingEpoch ?? epoch; }
        }
        InspectionTraceReadResult read = await _traces.ReadAsync(change.TraceIdentity!.Value,
            _exceptionalCancellation.Token).ConfigureAwait(false);
        if (read.Status != InspectionReadStatus.Available)
        {
            lock (_gate) { CountRead(read.Status); }
            // A committed catalog entry may briefly precede provider visibility. Keep the
            // cursor at the entry and wait for the next coalesced progress/final signal.
            if (read.Status == InspectionReadStatus.NotAvailable)
            {
                return false;
            }
            lock (_gate)
            {
                CompleteFailed(run, epoch);
                FailRun(run, new InvalidOperationException($"A catalogued trace read ended as {read.Status}."));
            }
            return false;
        }
        IInspectionTraceLease lease = read.Lease!;
        lock (_gate) { _available++; _leasesAcquired++; _activeLeases++; run.ActiveLeases++; }
        Exception? primary = null;
        bool evaluationCompleted = false;
        try
        {
            if (read.Revision != change.Revision || lease.Descriptor.Identity != change.TraceIdentity.Value)
            { throw new InvalidOperationException("The provider lease does not match the catalogued commit."); }
            var confirmed = new ConfirmedGateTraceDescriptor(lease.Descriptor,
                change.TraceIdentity.Value, change.Revision, lease.Descriptor.TraceQuality);
            lock (_gate) { _evalStarted++; }
            GateEvaluationResult result = EvaluateLease(epoch.Plan, confirmed, lease);
            if (result.Status == GateEvaluationStatus.Cancelled)
            { lock (_gate) { _evalCancelled++; CompleteFailed(run, epoch); } return true; }
            if (result.Status != GateEvaluationStatus.Completed || result.Batch is null)
            { lock (_gate) { _evalFailed++; CompleteFailed(run, epoch); } return true; }
            lock (_gate) { _evalCompleted++; _callbacksAdmitted++; _activeCallbacks++; run.ActiveCallbacks++; }
            evaluationCompleted = true;
            try
            {
                GateBatchDeliveryResult delivery = await _sink.DeliverAsync(result.Batch,
                    _exceptionalCancellation.Token).ConfigureAwait(false);
                lock (_gate)
                {
                    if (run.Failed)
                    { _batchesFailed++; CompleteFailed(run, epoch); }
                    else if (delivery.Status is GateBatchDeliveryStatus.Accepted or GateBatchDeliveryStatus.Duplicate)
                    {
                        _batchesAccepted++; epoch.Balance.EvaluatedAndDelivered++; run.WorkTerminal++;
                    }
                    else
                    {
                        if (delivery.Status == GateBatchDeliveryStatus.Rejected) { _batchesRejected++; }
                        else { _batchesFailed++; }
                        CompleteFailed(run, epoch);
                    }
                }
            }
            finally { lock (_gate) { _activeCallbacks--; run.ActiveCallbacks--; _callbacksCompleted++; } }
        }
        catch (Exception error)
        {
            primary = error;
            lock (_gate)
            {
                if (evaluationCompleted) { _batchesFailed++; }
                else { _evalFailed++; }
                CompleteFailed(run, epoch);
                RecordError("processing.unit", error);
            }
        }
        finally
        {
            try { lease.Dispose(); }
            catch (Exception cleanup)
            {
                lock (_gate)
                {
                    RecordError(primary is null ? "processing.lease" : "processing.lease.cleanup", cleanup, primary is not null);
                    FailRun(run, cleanup);
                }
            }
            finally
            {
                lock (_gate) { _leasesReleased++; _activeLeases--; run.ActiveLeases--; }
            }
        }
        return true;
    }

    private GateEvaluationResult EvaluateLease(CompiledGatePlan plan, ConfirmedGateTraceDescriptor trace,
        IInspectionTraceLease lease)
    {
        if (trace.EffectiveQuality == UtTraceQuality.Invalid)
        { return _evaluator.Evaluate(plan, trace, ReadOnlySpan<short>.Empty, _exceptionalCancellation.Token); }
        int count = lease.Samples.Count;
        short[] buffer = ArrayPool<short>.Shared.Rent(count);
        try
        {
            lease.Samples.CopyTo(buffer.AsSpan(0, count));
            return _evaluator.Evaluate(plan, trace, buffer.AsSpan(0, count), _exceptionalCancellation.Token);
        }
        finally { ArrayPool<short>.Shared.Return(buffer); }
    }

    private void CountRead(InspectionReadStatus status)
    {
        switch (status)
        {
            case InspectionReadStatus.NotAvailable: _notAvailable++; break;
            case InspectionReadStatus.Gap: _gap++; break;
            case InspectionReadStatus.OutOfRange: _outOfRange++; break;
            case InspectionReadStatus.Cancelled: _cancelled++; break;
            case InspectionReadStatus.Closed: _closed++; break;
            case InspectionReadStatus.Faulted: _faulted++; break;
        }
    }

    private void TryCompleteDrain(RunState run)
    {
        if (run.Failed)
        {
            if (run.ActiveWorkers == 0 && run.ActiveLeases == 0 && run.ActiveCallbacks == 0)
            {
                if (run.PendingEpoch is { } pending && run.WorkTerminal < run.WorkAdmitted)
                { CompleteFailed(run, pending); }
                run.Drained.TrySetException(run.Failure ?? new InvalidOperationException("Processing run failed."));
            }
            return;
        }
        if (!run.AdmissionClosed || run.FinalRevision is not { } final || run.Cursor.Value < final.Value ||
            run.PreparedEpoch is not null ||
            run.ActiveWorkers != 0 || run.ActiveLeases != 0 || run.ActiveCallbacks != 0 ||
            run.WorkAdmitted != run.WorkTerminal) { return; }
        run.Lifecycle = GateProcessingLifecycleState.Stopped;
        if (!run.TerminalCoveragePublished && _sink is IGateProcessingTerminalCoverageSink terminal)
        {
            GateProcessingTerminalCoverage[] coverage = run.Epochs.Select(epoch =>
            {
                GateDerivedStreamCoverage[] streams = run.Registration.Streams.Select(stream =>
                {
                    TraceIdentity[] identities = epoch.EligibleIdentities.Where(x => x.Stream == stream).ToArray();
                    return new GateDerivedStreamCoverage(stream,
                        identities.Length == 0 ? null : identities.Min(x => x.AcquisitionIndex),
                        identities.Length == 0 ? null : identities.Max(x => x.AcquisitionIndex),
                        identities.Length, GateDerivedStreamCoverage.ComputeIdentityDigest(identities));
                }).ToArray();
                return new GateProcessingTerminalCoverage(run.Registration.InspectionId, run.Registration.RunId,
                    final, epoch.Plan.Source.Id, epoch.Plan.Source.Revision, epoch.Plan.Signature.Hex,
                    epoch.Plan.Source.SchemaVersion, epoch.Plan.Source.AlgorithmVersion, streams);
            }).ToArray();
            GateProcessingTerminalCoverageStatus terminalStatus = terminal.ConfirmTerminalCoverage(coverage);
            if (terminalStatus is not (GateProcessingTerminalCoverageStatus.Accepted or
                GateProcessingTerminalCoverageStatus.Duplicate))
            {
                var error = new InvalidOperationException($"Terminal coverage ended as {terminalStatus}.");
                run.Failed = true; run.Lifecycle = GateProcessingLifecycleState.Faulted; run.Failure = error;
                run.LastError = new GateProcessingError("processing.terminal-coverage", terminalStatus.ToString());
                _failedRuns++; run.Drained.TrySetException(error); return;
            }
            run.TerminalCoveragePublished = true;
        }
        if (run.Drained.TrySetResult()) { _drainedRuns++; _state = GateProcessingLifecycleState.Stopped; }
    }

    private void FailRun(RunState run, Exception error)
    {
        if (run.Failed) { return; }
        if (run.ActiveWorkers == 0 && run.PendingEpoch is { } pending && run.WorkTerminal < run.WorkAdmitted)
        { CompleteFailed(run, pending); }
        run.Failed = true; run.Lifecycle = GateProcessingLifecycleState.Faulted;
        run.Failure = error;
        run.LastError = new GateProcessingError("processing.run", error.GetType().Name);
        _failedRuns++; TryCompleteDrain(run);
    }

    private static void CompleteFailed(RunState run, PlanEpoch epoch)
    {
        if (run.WorkTerminal >= run.WorkAdmitted) { return; }
        epoch.Balance.TerminallyFailed++; run.WorkTerminal++;
    }

    private GateProcessingLifecycleState SnapshotLifecycle()
    {
        if (_disposing || _state == GateProcessingLifecycleState.Closed) { return _state; }
        if (_runs.Count == 0) { return _state; }
        if (_runs.Values.Any(x => x.Lifecycle == GateProcessingLifecycleState.Running))
        { return GateProcessingLifecycleState.Running; }
        if (_runs.Values.Any(x => x.Lifecycle is GateProcessingLifecycleState.FinalReconciling or GateProcessingLifecycleState.Draining))
        { return GateProcessingLifecycleState.FinalReconciling; }
        if (_runs.Values.Any(x => x.Lifecycle == GateProcessingLifecycleState.Created))
        { return GateProcessingLifecycleState.Created; }
        if (_runs.Values.Any(x => x.Lifecycle == GateProcessingLifecycleState.AdmissionClosed))
        { return GateProcessingLifecycleState.AdmissionClosed; }
        if (_runs.Values.All(x => x.Lifecycle == GateProcessingLifecycleState.Faulted))
        { return GateProcessingLifecycleState.Faulted; }
        return GateProcessingLifecycleState.Stopped;
    }

    private GateProcessingRunDiagnostics SnapshotRun(RunState run)
    {
        InspectionRevision activeAt = run.PendingRevision != InspectionRevision.Initial
            ? run.PendingRevision
            : run.Target;
        PlanEpoch active = run.PendingEpoch ?? run.Epochs.Last(x => x.From.Value <= activeAt.Value);
        return new(run.Registration.RunId, run.Lifecycle, active.Plan.Source.Revision, active.Plan.Signature,
            run.Target, run.Observed, run.Cursor, run.FinalRevision,
            run.Drained.Task.IsCompletedSuccessfully,
            run.Watermarks.Select(x => new GateProcessingStreamWatermark(x.Key, x.Value)).ToArray(),
            run.Epochs.Select(x => new GateProcessingPlanBalance(x.Plan.Source.Revision, x.Plan.Signature,
                x.Balance.ConfirmedEligible, x.Balance.EvaluatedAndDelivered,
                x.Balance.ExplicitlyRejectedBeforeAdmission, x.Balance.TerminallyFailed,
                x.Balance.ExplicitlyUnprocessedByPlanTransition)).ToArray(),
            run.WorkAdmitted, run.WorkTerminal, run.ActiveWorkers, run.ActiveCallbacks, run.ActiveLeases,
            epochAdmissionOpen: !run.AdmissionClosed && run.FinalRevision is null && !run.Failed && !_disposing,
            hasPreparedEpoch: run.PreparedEpoch is not null,
            provisionalEpochSlots: run.PreparedEpoch is null ? 0 : 1,
            preparedEpochPins: run.PreparedEpoch is null ? 0 : 1,
            terminalReservationTombstones: HasTerminalTombstone(run.Registration.RunId) ? 1 : 0,
            structuralEpochSlots: run.Epochs.Count, maximumEpochSlots: run.MaximumEpochs,
            run.LastError);
    }

    private static void ValidatePage(RunState run, InspectionRevision cursor, InspectionRevision target,
        InspectionChangePage page)
    {
        if (page.ScannedThroughRevision.Value < cursor.Value || page.ScannedThroughRevision.Value > target.Value)
        { throw new InvalidOperationException("The change page reported an invalid scanned revision."); }
        ulong previous = cursor.Value;
        foreach (InspectionCommittedChange change in page.Changes)
        {
            if (change.RunId != run.Registration.RunId || !run.Streams.Contains(change.Stream) ||
                change.Revision.Value <= previous || change.Revision.Value > page.ScannedThroughRevision.Value)
            { throw new InvalidOperationException("The change page is unordered or outside its registered run and range."); }
            previous = change.Revision.Value;
        }
        if (page.HasMore && (page.Changes.Count == 0 || page.ScannedThroughRevision != page.Changes[^1].Revision))
        { throw new InvalidOperationException("A continued change page must end at its last returned change."); }
    }

    private void Pulse() { if (_wake.CurrentCount == 0) { _wake.Release(); } }
    private void RecordError(string code, Exception error, bool secondary = false)
    {
        var value = new GateProcessingError(code, error.GetType().Name);
        if (!secondary && _primaryError is null) { _primaryError = value; }
        else if (_secondaryErrors.Count < 8) { _secondaryErrors.Add(value); }
    }

    public ValueTask DisposeAsync()
    {
        TaskCompletionSource? completion = null;
        Task task;
        lock (_gate)
        {
            if (_disposeTask is null)
            {
                completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
                _disposeTask = completion.Task; _disposing = true;
                _state = GateProcessingLifecycleState.Disposing;
                foreach (RunState run in _runs.Values)
                {
                    AbortPreparedEpoch(run);
                    FailRun(run, new OperationCanceledException("Processing was disposed exceptionally."));
                }
            }
            task = _disposeTask;
        }
        if (completion is not null)
        {
            try { _exceptionalCancellation.Cancel(); }
            catch (Exception error) { completion.TrySetException(error); return new(task); }
            _ = CompleteDisposeAsync(completion);
        }
        return new(task);
    }

    private async Task CompleteDisposeAsync(TaskCompletionSource completion)
    {
        try
        {
            await _worker.ConfigureAwait(false);
            Exception? cleanupFailure = null;
            lock (_gate)
            {
                if (_activeWorkers != 0 || _activeCallbacks != 0 || _activeLeases != 0)
                {
                    _state = GateProcessingLifecycleState.Faulted;
                    cleanupFailure = new InvalidOperationException("Processing cleanup could not be confirmed.");
                }
                else { _state = GateProcessingLifecycleState.Closed; }
                _preparedEpochs.Clear();
                _runs.Clear();
                _terminalRuns.Clear();
                _primaryError = null;
                _secondaryErrors.Clear();
            }
            _exceptionalCancellation.Dispose(); _wake.Dispose();
            if (cleanupFailure is null) { completion.TrySetResult(); }
            else { completion.TrySetException(cleanupFailure); }
        }
        catch (Exception error) { completion.TrySetException(error); }
    }

    private sealed class RunState(GateProcessingRunRegistration registration, int maximumEpochs)
    {
        internal GateProcessingRunRegistration Registration { get; } = registration;
        internal HashSet<UtStreamIdentity> Streams { get; } = [.. registration.Streams];
        internal Dictionary<UtStreamIdentity, InspectionRevision> Watermarks { get; } =
            registration.Streams.ToDictionary(x => x, _ => InspectionRevision.Initial);
        internal List<PlanEpoch> Epochs { get; } = [new(InspectionRevision.Initial, registration.Plan, new())];
        internal PreparedEpoch? PreparedEpoch;
        internal int MaximumEpochs { get; } = maximumEpochs;
        internal InspectionRevision Cursor, Target, Observed;
        internal InspectionRevision PendingRevision;
        internal PlanEpoch? PendingEpoch;
        internal bool TerminalCoveragePublished;
        internal InspectionRevision? FinalRevision;
        internal bool AdmissionClosed, Failed;
        internal Exception? Failure;
        internal int ActiveWorkers, ActiveCallbacks, ActiveLeases;
        internal long WorkAdmitted, WorkTerminal;
        internal GateProcessingLifecycleState Lifecycle = GateProcessingLifecycleState.Running;
        internal GateProcessingError? LastError;
        internal TaskCompletionSource Drained { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
    private sealed record PlanEpoch(InspectionRevision From, CompiledGatePlan Plan, MutablePlanBalance Balance)
    {
        internal HashSet<TraceIdentity> EligibleIdentities { get; } = [];
    }

    private void AbortPreparedEpoch(RunState run)
    {
        PreparedEpoch? prepared = run.PreparedEpoch;
        if (prepared is null) return;
        run.PreparedEpoch = null;
        _preparedEpochs.Remove(prepared.Id);
        StoreTerminalTombstone(prepared, GatePreparedReservationState.Aborted);
    }

    private void StoreTerminalTombstone(PreparedEpoch prepared, GatePreparedReservationState state)
    {
        AcquisitionRunId runId = prepared.Run.Registration.RunId;
        RemoveTerminalTombstone(runId);
        if (_terminalEpochTombstoneCount == _terminalEpochTombstones.Length)
        {
            Array.Copy(_terminalEpochTombstones, 1, _terminalEpochTombstones, 0,
                _terminalEpochTombstones.Length - 1);
            _terminalEpochTombstoneCount--;
        }
        _terminalEpochTombstones[_terminalEpochTombstoneCount++] =
            new(prepared.Id, prepared.Owner, prepared.Generation, state, runId);
    }

    private void RemoveTerminalTombstone(AcquisitionRunId runId)
    {
        for (int index = 0; index < _terminalEpochTombstoneCount; index++)
        {
            if (_terminalEpochTombstones[index].RunId != runId) continue;
            Array.Copy(_terminalEpochTombstones, index + 1, _terminalEpochTombstones, index,
                _terminalEpochTombstoneCount - index - 1);
            _terminalEpochTombstones[_terminalEpochTombstoneCount - 1] = default;
            _terminalEpochTombstoneCount--;
            return;
        }
    }

    private GatePreparedReservation FindTerminalTombstone(GatePreparedReservationId id)
    {
        for (int index = 0; index < _terminalEpochTombstoneCount; index++)
            if (_terminalEpochTombstones[index].Id == id)
                return _terminalEpochTombstones[index].Snapshot();
        return default;
    }

    private bool HasTerminalTombstone(AcquisitionRunId runId)
    {
        for (int index = 0; index < _terminalEpochTombstoneCount; index++)
            if (_terminalEpochTombstones[index].RunId == runId) return true;
        return false;
    }

    private sealed class PreparedEpoch(GatePreparedReservationId id, GatePreparedReservationOwner owner,
        GatePreparedOperationGeneration generation, RunState run, PlanEpoch epoch)
    {
        internal GatePreparedReservationId Id { get; } = id;
        internal GatePreparedReservationOwner Owner { get; } = owner;
        internal GatePreparedOperationGeneration Generation { get; } = generation;
        internal RunState Run { get; } = run;
        internal PlanEpoch Epoch { get; } = epoch;
        internal GatePreparedReservation Snapshot() =>
            new(Id, Owner, Generation, GatePreparedReservationState.Prepared);
    }
    private sealed class MutablePlanBalance
    {
        internal long ConfirmedEligible { get; set; }
        internal long EvaluatedAndDelivered { get; set; }
        internal long ExplicitlyRejectedBeforeAdmission { get; set; }
        internal long TerminallyFailed { get; set; }
        internal long ExplicitlyUnprocessedByPlanTransition { get; set; }
    }
}
