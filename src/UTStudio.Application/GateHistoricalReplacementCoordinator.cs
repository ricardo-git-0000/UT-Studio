using UTStudio.Contracts.Processing;
using UTStudio.Contracts.Inspection;
using UTStudio.Domain.Acquisition;
using UTStudio.Domain.Inspection;
using UTStudio.Domain.Processing;
using UTStudio.Processing.Core;

namespace UTStudio.Application;

internal enum GateHistoricalReplacementSubmissionStatus
{
    Admitted, AlreadyRequested, Cancelled, CapacityExceeded, Closed, Superseded, Rejected, Faulted
}

internal sealed record GateHistoricalReplacementTerminal(
    GateReprocessingProgressSnapshot Progress,
    GateCandidateActivationResult? Activation,
    GateCandidateAbandonResult? Abandonment,
    GateCandidateReclaimResult? Reclaim,
    GateDerivedMutationResult? PreviousViewReclaim,
    Exception? ObserverError);

internal sealed class GateHistoricalReplacementSubmission
{
    internal GateHistoricalReplacementSubmission(GateHistoricalReplacementSubmissionStatus status,
        Task<GateHistoricalReplacementTerminal>? completion = null)
    { Status = status; Completion = completion; }
    internal GateHistoricalReplacementSubmissionStatus Status { get; }
    internal Task<GateHistoricalReplacementTerminal>? Completion { get; }
    internal bool IsAdmitted => Status == GateHistoricalReplacementSubmissionStatus.Admitted;
    internal Task<GateHistoricalReplacementTerminal> WaitAsync(CancellationToken cancellationToken = default) =>
        Completion is null ? throw new InvalidOperationException("Historical work was not admitted.") :
            Completion.WaitAsync(cancellationToken);
}

/// <summary>Owns one historical replacement from reservation through terminal reconciliation.</summary>
internal sealed class GateHistoricalReplacementCoordinator
{
    private readonly IOwnedGateReprocessingScheduler _scheduler;
    private readonly IGateReplacementStore _store;
    private readonly InMemoryGatePlanPreparedCommitOwner _authority;

    internal GateHistoricalReplacementCoordinator(IOwnedGateReprocessingScheduler scheduler,
        IGateReplacementStore store, InMemoryGatePlanPreparedCommitOwner authority)
    {
        _scheduler = scheduler ?? throw new ArgumentNullException(nameof(scheduler));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _authority = authority ?? throw new ArgumentNullException(nameof(authority));
    }

    internal async ValueTask<GateHistoricalReplacementSubmission> TryAdmitAsync(
        GatePlanAuthorityKey authority, GatePlanApplyRunContext run, InspectionRevision sourceFinalRevision,
        IReadOnlyList<GateReprocessingStreamSelection> selection, int blockSize,
        CancellationToken cancellationToken = default)
    {
        if (!authority.IsValid || !run.IsValid || authority.InspectionId != run.InspectionId ||
            sourceFinalRevision == InspectionRevision.Initial || selection is null || selection.Count == 0)
            throw new ArgumentException("Historical replacement identity is invalid.");
        GatePlanAuthorityInstalledState installed = _authority.ReadInstalled(authority) ??
            throw new InvalidOperationException("The committed gate-plan authority is unavailable.");
        CompiledGatePlan plan = installed.Authority.Committed.CompiledPlan;
        var attempt = new GateReprocessingAttemptId(Guid.NewGuid());
        GateCandidateReservationResult reserved = _store.ReserveCandidate(run.InspectionId, run.RunId,
            run.RunGeneration, authority.PlanId, plan.Source.Revision, attempt);
        if (!reserved.IsReserved)
        {
            if (reserved.Status != GateCandidateReservationStatus.AlreadyRequested)
                FinishAdmissionFailure(authority, plan.Source.Revision, reserved.Status);
            return new(Map(reserved.Status));
        }

        GateCandidateReservation reservation = reserved.Reservation!;
        if (!Advance(authority, plan.Source.Revision, current => Copy(current,
            admission: GateHistoricalAdmissionState.Admitting,
            pending: GateApplyLedgerPendingResource.Observer)))
        {
            _store.ReleaseReservation(reservation);
            return new(GateHistoricalReplacementSubmissionStatus.Closed);
        }
        var owner = new ObserverOwner(this, authority, reservation);
        Task<GateHistoricalReplacementTerminal> observation = owner.ObserveAsync();
        var descriptor = new GateReprocessingJobDescriptor(new(Guid.NewGuid()), attempt,
            run.InspectionId, run.RunId, sourceFinalRevision, selection, plan, blockSize,
            GateReprocessingPublicationPolicy.StoreOnly);
        GateReprocessingAdmissionResult admission;
        try
        {
            admission = await _scheduler.AdmitOwnedAsync(descriptor, owner, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            owner.RejectBeforeAdmission();
            if (!owner.WasBound)
            {
                _store.ReleaseReservation(reservation);
                FinishAdmissionFailure(authority, plan.Source.Revision, GateCandidateReservationStatus.Closed,
                    GateHistoricalAdmissionState.Faulted);
            }
            throw;
        }
        if (admission.Status != GateReprocessingAdmissionStatus.Admitted)
        {
            owner.RejectBeforeAdmission();
            _store.ReleaseReservation(reservation);
            FinishAdmissionFailure(authority, plan.Source.Revision, GateCandidateReservationStatus.Closed,
                admission.Status == GateReprocessingAdmissionStatus.Cancelled ?
                    GateHistoricalAdmissionState.Cancelled : GateHistoricalAdmissionState.Rejected);
            return new(Map(admission.Status));
        }
        return new(GateHistoricalReplacementSubmissionStatus.Admitted, observation);
    }

    private bool Advance(GatePlanAuthorityKey authority, GatePlanRevision revision,
        Func<GateApplyLedgerSnapshot, GateApplyLedgerSnapshot> transition) =>
        _authority.TryAdvanceLedger(authority, revision, transition);

    private void FinishAdmissionFailure(GatePlanAuthorityKey authority, GatePlanRevision revision,
        GateCandidateReservationStatus status, GateHistoricalAdmissionState? forced = null)
    {
        GateHistoricalAdmissionState terminal = forced ?? (status switch
        {
            GateCandidateReservationStatus.RunSuperseded or GateCandidateReservationStatus.TokenSuperseded =>
                GateHistoricalAdmissionState.Superseded,
            GateCandidateReservationStatus.Closed => GateHistoricalAdmissionState.Rejected,
            _ => GateHistoricalAdmissionState.Rejected
        });
        GateLedgerCauseKind kind = terminal switch
        {
            GateHistoricalAdmissionState.Cancelled => GateLedgerCauseKind.Cancelled,
            GateHistoricalAdmissionState.Superseded => GateLedgerCauseKind.Superseded,
            GateHistoricalAdmissionState.Faulted => GateLedgerCauseKind.Faulted,
            _ => GateLedgerCauseKind.Rejected
        };
        Advance(authority, revision, current => Copy(current, admission: terminal,
            execution: GateHistoricalExecutionState.NotApplicable,
            coverage: GateCandidateCoverageState.NotApplicable, activation: GateActivationState.NotApplicable,
            retirement: GateCandidateRetirementState.NotApplicable,
            previous: GatePreviousViewRetirementState.NotApplicable,
            primary: new(GateLedgerStage.HistoricalAdmission, kind),
            pending: GateApplyLedgerPendingResource.None));
    }

    private static GateHistoricalReplacementSubmissionStatus Map(GateCandidateReservationStatus status) => status switch
    {
        GateCandidateReservationStatus.AlreadyRequested => GateHistoricalReplacementSubmissionStatus.AlreadyRequested,
        GateCandidateReservationStatus.CapacityExceeded => GateHistoricalReplacementSubmissionStatus.CapacityExceeded,
        GateCandidateReservationStatus.Closed => GateHistoricalReplacementSubmissionStatus.Closed,
        GateCandidateReservationStatus.RunSuperseded or GateCandidateReservationStatus.TokenSuperseded =>
            GateHistoricalReplacementSubmissionStatus.Superseded,
        _ => GateHistoricalReplacementSubmissionStatus.Faulted
    };

    private static GateHistoricalReplacementSubmissionStatus Map(GateReprocessingAdmissionStatus status) => status switch
    {
        GateReprocessingAdmissionStatus.Cancelled => GateHistoricalReplacementSubmissionStatus.Cancelled,
        GateReprocessingAdmissionStatus.CapacityExceeded => GateHistoricalReplacementSubmissionStatus.CapacityExceeded,
        GateReprocessingAdmissionStatus.Closed => GateHistoricalReplacementSubmissionStatus.Closed,
        GateReprocessingAdmissionStatus.Rejected => GateHistoricalReplacementSubmissionStatus.Rejected,
        _ => GateHistoricalReplacementSubmissionStatus.Faulted
    };

    private static GateApplyLedgerSnapshot Copy(GateApplyLedgerSnapshot source,
        GateHistoricalAdmissionState? admission = null, GateHistoricalExecutionState? execution = null,
        GateCandidateCoverageState? coverage = null, GateActivationState? activation = null,
        GateCandidateRetirementState? retirement = null, GatePreviousViewRetirementState? previous = null,
        GateCandidateOwnershipState? ownership = null, bool preserveOwnership = true,
        GateLedgerCause? primary = null, IEnumerable<GateLedgerCause>? secondary = null,
        GateApplyLedgerPendingResource? pending = null) => GateApplyLedgerSnapshot.Create(
            source.InspectionId, source.RunId, source.RunGeneration, source.PlanId, source.PlanRevision,
            source.Commit, source.LiveEpoch, admission ?? source.HistoricalAdmission,
            execution ?? source.HistoricalExecution, coverage ?? source.CandidateCoverage,
            activation ?? source.Activation, retirement ?? source.CandidateRetirement,
            previous ?? source.PreviousViewRetirement,
            preserveOwnership ? ownership ?? source.CandidateOwnership : ownership,
            primary ?? source.PrimaryCause, secondary ?? source.SecondaryCauses,
            source.Lifecycle, pending ?? source.PendingResources);

    private sealed class ObserverOwner : IGateReprocessingAdmissionOwner
    {
        private readonly GateHistoricalReplacementCoordinator _owner;
        private readonly GatePlanAuthorityKey _authority;
        private readonly GateCandidateReservation _reservation;
        private readonly TaskCompletionSource<IGateReprocessingJob> _job =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly object _transitionGate = new();
        private bool _admittedRecorded;
        private GateStoreOnlyCandidateDescriptor? _candidate;
        internal bool WasBound => _job.Task.IsCompletedSuccessfully;

        internal ObserverOwner(GateHistoricalReplacementCoordinator owner, GatePlanAuthorityKey authority,
            GateCandidateReservation reservation)
        { _owner = owner; _authority = authority; _reservation = reservation; }

        public void Bind(IGateReprocessingJob job)
        {
            if (!_job.TrySetResult(job)) throw new ApplicationCommitInvariantException("Historical owner was bound twice.");
        }

        public GateStoreOnlyCandidateDescriptor MaterializeCandidate(GateDerivedCoverageManifest manifest)
        {
            EnsureAdmitted();
            _owner.Advance(_authority, _reservation.PlanRevision, current => Copy(current,
                execution: GateHistoricalExecutionState.Running,
                coverage: GateCandidateCoverageState.Building,
                ownership: GateCandidateOwnershipState.Materializing));
            _candidate = _owner._store.MaterializeCandidate(_reservation, manifest);
            return _candidate;
        }

        internal void RejectBeforeAdmission() => _job.TrySetCanceled();

        internal async Task<GateHistoricalReplacementTerminal> ObserveAsync()
        {
            IGateReprocessingJob job = await _job.Task.ConfigureAwait(false);
            EnsureAdmitted();
            GateReprocessingProgressSnapshot progress = await job.Completion.ConfigureAwait(false);
            GateCandidateActivationResult? activation = null;
            GateCandidateAbandonResult? abandon = null;
            GateCandidateReclaimResult? reclaim = null;
            GateDerivedMutationResult? previous = null;
            Exception? observerError = null;
            try
            {
                if (progress.Lifecycle == GateReprocessingLifecycle.Completed &&
                    progress.StoreOnlyCandidate is { } candidate)
                {
                    if (_candidate is null || !_candidate.Equals(candidate))
                        throw new ApplicationCommitInvariantException("The terminal candidate receipt changed identity.");
                    _owner.Advance(_authority, _reservation.PlanRevision, current => Copy(current,
                        execution: GateHistoricalExecutionState.Completed,
                        coverage: GateCandidateCoverageState.Complete,
                        activation: GateActivationState.Eligible,
                        ownership: GateCandidateOwnershipState.Available));
                    _owner.Advance(_authority, _reservation.PlanRevision, current => Copy(current,
                        activation: GateActivationState.Activating,
                        ownership: GateCandidateOwnershipState.Activating));
                    activation = _owner._store.ActivateCandidate(new(candidate, _reservation.Predecessor,
                        _reservation.RunId, _reservation.RunGeneration));
                    if (activation.IsSuccess)
                    {
                        _owner.Advance(_authority, _reservation.PlanRevision, current => Copy(current,
                            activation: GateActivationState.Activated,
                            retirement: GateCandidateRetirementState.Activated,
                            previous: _reservation.Predecessor is null ? GatePreviousViewRetirementState.NotApplicable :
                                GatePreviousViewRetirementState.Required,
                            ownership: GateCandidateOwnershipState.Activated));
                        if (_reservation.Predecessor is not null)
                        {
                            _owner.Advance(_authority, _reservation.PlanRevision, current => Copy(current,
                                previous: GatePreviousViewRetirementState.Retiring));
                            previous = _owner._store.ReclaimPreviousView();
                            GatePreviousViewRetirementState state = previous.Value.Status == GateDerivedMutationStatus.Reclaimed ?
                                GatePreviousViewRetirementState.Retired : GatePreviousViewRetirementState.DeferredByLeases;
                            GateLedgerCause? cause = state == GatePreviousViewRetirementState.DeferredByLeases ?
                                new(GateLedgerStage.PreviousViewRetirement, GateLedgerCauseKind.DeferredByLeases) : null;
                            _owner.Advance(_authority, _reservation.PlanRevision, current => Copy(current,
                                previous: state, primary: cause,
                                pending: state == GatePreviousViewRetirementState.DeferredByLeases ?
                                    GateApplyLedgerPendingResource.Observer |
                                    GateApplyLedgerPendingResource.Tombstone : GateApplyLedgerPendingResource.None));
                            if (state == GatePreviousViewRetirementState.DeferredByLeases)
                            {
                                previous = await _owner._store.CompletePreviousViewRetirementAsync()
                                    .ConfigureAwait(false);
                                if (previous.Value.Status == GateDerivedMutationStatus.Reclaimed)
                                    _owner.Advance(_authority, _reservation.PlanRevision, current => Copy(current,
                                        previous: GatePreviousViewRetirementState.Retired,
                                        pending: GateApplyLedgerPendingResource.None));
                            }
                        }
                        else _owner.Advance(_authority, _reservation.PlanRevision,
                            current => Copy(current, pending: GateApplyLedgerPendingResource.None));
                    }
                    else
                    {
                        (abandon, reclaim) = Retire(candidate, activation.Status);
                    }
                }
                else if (_candidate is { } materialized)
                {
                    (abandon, reclaim) = Retire(materialized, GateCandidateActivationStatus.CandidateNotAvailable);
                }
                else
                {
                    _owner._store.ReleaseReservation(_reservation);
                    FinishUnmaterialized(progress);
                }
            }
            catch (Exception error)
            {
                observerError = error;
                try
                {
                    if (activation?.IsSuccess != true && _candidate is { } orphan)
                    {
                        abandon = _owner._store.AbandonCandidate(orphan);
                        if (abandon.Status is GateCandidateAbandonStatus.LogicallyAbandoned or
                            GateCandidateAbandonStatus.AlreadyAbandoned)
                            reclaim = _owner._store.ReclaimCandidate(abandon.Candidate);
                    }
                    else if (_candidate is null)
                        _owner._store.ReleaseReservation(_reservation);
                }
                catch (Exception cleanup)
                { observerError = new AggregateException(error, cleanup); }
            }
            return new(progress, activation, abandon, reclaim, previous, observerError);
        }

        private void EnsureAdmitted()
        {
            lock (_transitionGate)
            {
                if (_admittedRecorded) return;
                if (!_owner.Advance(_authority, _reservation.PlanRevision, current => Copy(current,
                    admission: GateHistoricalAdmissionState.Admitted,
                    execution: GateHistoricalExecutionState.Queued,
                    ownership: GateCandidateOwnershipState.Reserved)))
                    throw new ApplicationCommitInvariantException("The admitted historical owner lost its ledger.");
                _admittedRecorded = true;
            }
        }

        private (GateCandidateAbandonResult, GateCandidateReclaimResult) Retire(
            GateStoreOnlyCandidateDescriptor candidate, GateCandidateActivationStatus status)
        {
            GateActivationState terminal = status switch
            {
                GateCandidateActivationStatus.RunSuperseded => GateActivationState.RunSuperseded,
                GateCandidateActivationStatus.TokenSuperseded => GateActivationState.Superseded,
                GateCandidateActivationStatus.Disposed => GateActivationState.Faulted,
                _ => GateActivationState.CasRejected
            };
            GateLedgerCauseKind causeKind = status == GateCandidateActivationStatus.RunSuperseded ?
                GateLedgerCauseKind.RunSuperseded : status == GateCandidateActivationStatus.TokenSuperseded ?
                GateLedgerCauseKind.TokenSuperseded : status == GateCandidateActivationStatus.PredecessorMismatch ?
                GateLedgerCauseKind.PredecessorMismatch : status == GateCandidateActivationStatus.CandidateMismatch ?
                GateLedgerCauseKind.CandidateMismatch : status == GateCandidateActivationStatus.CandidateAbandoned ?
                GateLedgerCauseKind.CandidateAbandoned : status == GateCandidateActivationStatus.AlreadyActivated ?
                GateLedgerCauseKind.AlreadyActivated : status == GateCandidateActivationStatus.Disposed ?
                GateLedgerCauseKind.Faulted : GateLedgerCauseKind.CandidateNotAvailable;
            _owner.Advance(_authority, _reservation.PlanRevision, current => Copy(current,
                activation: terminal, retirement: GateCandidateRetirementState.Required,
                ownership: GateCandidateOwnershipState.Available,
                primary: new(GateLedgerStage.Activation, causeKind)));
            _owner.Advance(_authority, _reservation.PlanRevision, current => Copy(current,
                retirement: GateCandidateRetirementState.Abandoning,
                ownership: GateCandidateOwnershipState.Abandoning));
            GateCandidateAbandonResult abandoned = _owner._store.AbandonCandidate(candidate);
            if (abandoned.Status == GateCandidateAbandonStatus.Disposed)
            {
                GateLedgerCause disposed = new(GateLedgerStage.CandidateRetirement, GateLedgerCauseKind.Disposed);
                _owner.Advance(_authority, _reservation.PlanRevision, current => Copy(current,
                    retirement: GateCandidateRetirementState.LogicallyAbandoned,
                    ownership: GateCandidateOwnershipState.LogicallyAbandoned,
                    secondary: AppendCause(current, disposed),
                    pending: GateApplyLedgerPendingResource.Observer));
                _owner.Advance(_authority, _reservation.PlanRevision, current => Copy(current,
                    retirement: GateCandidateRetirementState.Reclaimed,
                    ownership: GateCandidateOwnershipState.Reclaimed,
                    pending: GateApplyLedgerPendingResource.None));
                GateCandidateReclaimResult disposedReclaim = _owner._store.ReclaimCandidate(abandoned.Candidate);
                return (abandoned, disposedReclaim);
            }
            if (abandoned.Status is not (GateCandidateAbandonStatus.LogicallyAbandoned or
                GateCandidateAbandonStatus.AlreadyAbandoned))
                throw new ApplicationCommitInvariantException(
                    $"Candidate abandonment returned {abandoned.Status}.");
            _owner.Advance(_authority, _reservation.PlanRevision, current => Copy(current,
                retirement: GateCandidateRetirementState.LogicallyAbandoned,
                ownership: GateCandidateOwnershipState.LogicallyAbandoned));
            GateCandidateReclaimResult reclaimed = _owner._store.ReclaimCandidate(abandoned.Candidate);
            if (reclaimed.Status is GateCandidateReclaimStatus.Reclaimed or GateCandidateReclaimStatus.AlreadyReclaimed)
                _owner.Advance(_authority, _reservation.PlanRevision, current => Copy(current,
                    retirement: GateCandidateRetirementState.Reclaimed,
                    ownership: GateCandidateOwnershipState.Reclaimed,
                    pending: GateApplyLedgerPendingResource.None));
            else if (reclaimed.Status != GateCandidateReclaimStatus.Busy)
                throw new ApplicationCommitInvariantException($"Candidate reclaim returned {reclaimed.Status}.");
            return (abandoned, reclaimed);
        }

        private static IEnumerable<GateLedgerCause> AppendCause(GateApplyLedgerSnapshot current,
            GateLedgerCause cause) => current.SecondaryCauses.Any(item => item.Stage == cause.Stage) ?
            current.SecondaryCauses : [.. current.SecondaryCauses, cause];

        private void FinishUnmaterialized(GateReprocessingProgressSnapshot progress)
        {
            GateHistoricalExecutionState execution = progress.Lifecycle == GateReprocessingLifecycle.Cancelled ?
                GateHistoricalExecutionState.Cancelled : GateHistoricalExecutionState.Faulted;
            GateLedgerCauseKind kind = execution == GateHistoricalExecutionState.Cancelled ?
                GateLedgerCauseKind.Cancelled : GateLedgerCauseKind.Faulted;
            _owner.Advance(_authority, _reservation.PlanRevision, current => Copy(current,
                execution: execution, coverage: execution == GateHistoricalExecutionState.Cancelled ?
                    GateCandidateCoverageState.Cancelled : GateCandidateCoverageState.Faulted,
                activation: GateActivationState.Abandoned,
                retirement: GateCandidateRetirementState.Required,
                ownership: GateCandidateOwnershipState.Reserved,
                primary: new(GateLedgerStage.HistoricalExecution, kind),
                secondary: [new(GateLedgerStage.CandidateCoverage, kind),
                    new(GateLedgerStage.Activation, GateLedgerCauseKind.CandidateAbandoned)]));
            _owner.Advance(_authority, _reservation.PlanRevision, current => Copy(current,
                retirement: GateCandidateRetirementState.Abandoning,
                ownership: GateCandidateOwnershipState.Abandoning));
            _owner.Advance(_authority, _reservation.PlanRevision, current => Copy(current,
                retirement: GateCandidateRetirementState.LogicallyAbandoned,
                ownership: GateCandidateOwnershipState.LogicallyAbandoned));
            _owner.Advance(_authority, _reservation.PlanRevision, current => Copy(current,
                retirement: GateCandidateRetirementState.Reclaimed,
                ownership: GateCandidateOwnershipState.Reclaimed,
                pending: GateApplyLedgerPendingResource.None));
        }
    }
}
