using UTStudio.Contracts.Processing;
using UTStudio.Domain.Acquisition;
using UTStudio.Domain.Inspection;
using UTStudio.Domain.Processing;

namespace UTStudio.Processing.Core;

internal enum GateCandidateReservationStatus
{ Reserved, AlreadyRequested, RunSuperseded, TokenSuperseded, CapacityExceeded, Closed }

internal sealed record GateCandidateReservation(
    Guid Id,
    InspectionId InspectionId,
    AcquisitionRunId RunId,
    GateRunGeneration RunGeneration,
    GatePlanId PlanId,
    GatePlanRevision PlanRevision,
    GateReplacementToken Token,
    GateReplacementGeneration Generation,
    DerivedViewId CandidateViewId,
    ActiveDerivedViewDescriptor? Predecessor,
    GateReprocessingAttemptId AttemptId)
{
    internal bool IsValid => Id != Guid.Empty && InspectionId.IsValid && RunId.IsValid && RunGeneration.IsValid &&
        PlanId.IsValid && PlanRevision.IsValid && Token.IsValid && Generation.IsValid && CandidateViewId.IsValid &&
        AttemptId.IsValid;
}

internal readonly record struct GateCandidateReservationResult(
    GateCandidateReservationStatus Status, GateCandidateReservation? Reservation)
{
    internal bool IsReserved => Status == GateCandidateReservationStatus.Reserved && Reservation is not null;
}

internal interface IGateReplacementStore
{
    GateCandidateReservationResult ReserveCandidate(InspectionId inspectionId, AcquisitionRunId runId,
        GateRunGeneration runGeneration, GatePlanId planId, GatePlanRevision planRevision,
        GateReprocessingAttemptId attemptId);
    GateStoreOnlyCandidateDescriptor MaterializeCandidate(GateCandidateReservation reservation,
        GateDerivedCoverageManifest manifest);
    void ReleaseReservation(GateCandidateReservation reservation);
    GateCandidateActivationResult ActivateCandidate(GateCandidateActivationRequest request);
    GateCandidateAbandonResult AbandonCandidate(GateStoreOnlyCandidateDescriptor candidate);
    GateCandidateReclaimResult ReclaimCandidate(GateStoreOnlyCandidateDescriptor candidate);
    GateDerivedMutationResult ReclaimPreviousView();
    ValueTask<GateDerivedMutationResult> CompletePreviousViewRetirementAsync();
}

internal interface IGateReplacementStoreLifecycle : IGateReplacementStore, IAsyncDisposable;

internal sealed class InMemoryGateReplacementStore(InMemoryGateDerivedStore store) : IGateReplacementStoreLifecycle
{
    public GateCandidateReservationResult ReserveCandidate(InspectionId inspectionId, AcquisitionRunId runId,
        GateRunGeneration runGeneration, GatePlanId planId, GatePlanRevision planRevision,
        GateReprocessingAttemptId attemptId) =>
        store.ReserveCandidate(inspectionId, runId, runGeneration, planId, planRevision, attemptId);
    public GateStoreOnlyCandidateDescriptor MaterializeCandidate(GateCandidateReservation reservation,
        GateDerivedCoverageManifest manifest) => store.MaterializeCandidate(reservation, manifest);
    public void ReleaseReservation(GateCandidateReservation reservation) => store.ReleaseCandidateReservation(reservation);
    public GateCandidateActivationResult ActivateCandidate(GateCandidateActivationRequest request) =>
        store.ActivateCandidate(request);
    public GateCandidateAbandonResult AbandonCandidate(GateStoreOnlyCandidateDescriptor candidate) =>
        store.AbandonCandidate(candidate);
    public GateCandidateReclaimResult ReclaimCandidate(GateStoreOnlyCandidateDescriptor candidate) =>
        store.ReclaimCandidate(candidate);
    public GateDerivedMutationResult ReclaimPreviousView() =>
        store.ReclaimInvalidatedAsync(CancellationToken.None).AsTask().GetAwaiter().GetResult();
    public ValueTask<GateDerivedMutationResult> CompletePreviousViewRetirementAsync() =>
        store.CompletePreviousViewRetirementAsync();
    public ValueTask DisposeAsync() => store.DisposeAsync();
}

public sealed partial class InMemoryGateDerivedStore
{
    private const int MaximumReplacementCandidates = 64;
    private readonly Dictionary<Guid, CandidateState> _replacementCandidates = [];
    private readonly Dictionary<(InspectionId, AcquisitionRunId, GatePlanId, GatePlanRevision), Guid>
        _candidateRevisionClaims = [];
    private TaskCompletionSource? _replacementRetirementReady;

    internal GateCandidateReservationResult ReserveCandidate(InspectionId inspectionId, AcquisitionRunId runId,
        GateRunGeneration runGeneration, GatePlanId planId, GatePlanRevision planRevision,
        GateReprocessingAttemptId attemptId)
    {
        lock (_gate)
        {
            if (_admissionClosed || _closed)
                return new(GateCandidateReservationStatus.Closed, null);
            if (!_currentRun.IsValid || _currentRun.RunId != runId || _currentRun.InspectionId != inspectionId ||
                _currentRun.Generation != runGeneration)
                return new(GateCandidateReservationStatus.RunSuperseded, null);
            var claim = (inspectionId, runId, planId, planRevision);
            if (_candidateRevisionClaims.ContainsKey(claim))
                return new(GateCandidateReservationStatus.AlreadyRequested, null);
            var desiredKey = new DesiredReplacementKey(inspectionId, planId);
            if (!_desiredReplacements.TryGetValue(desiredKey, out InstalledDesiredReplacement? desired) ||
                desired.RunId != runId || desired.RunGeneration != runGeneration || desired.PlanRevision != planRevision)
                return new(GateCandidateReservationStatus.TokenSuperseded, null);
            if (_replacementCandidates.Count >= MaximumReplacementCandidates)
                return new(GateCandidateReservationStatus.CapacityExceeded, null);
            Guid id = Guid.NewGuid();
            var reservation = new GateCandidateReservation(id, inspectionId, runId, runGeneration, planId,
                planRevision, desired.Token, desired.Generation, new($"candidate-{id:N}"), desired.Predecessor, attemptId);
            _replacementCandidates.EnsureCapacity(_replacementCandidates.Count + 1);
            _candidateRevisionClaims.EnsureCapacity(_candidateRevisionClaims.Count + 1);
            _replacementCandidates.Add(id, new(reservation));
            _candidateRevisionClaims.Add(claim, id);
            return new(GateCandidateReservationStatus.Reserved, reservation);
        }
    }

    internal GateStoreOnlyCandidateDescriptor MaterializeCandidate(GateCandidateReservation reservation,
        GateDerivedCoverageManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(reservation); ArgumentNullException.ThrowIfNull(manifest);
        lock (_gate)
        {
            if (!_replacementCandidates.TryGetValue(reservation.Id, out CandidateState? state) ||
                state.Reservation != reservation || state.Ownership != GateCandidateOwnershipState.Reserved)
                throw new InvalidOperationException("The candidate reservation is not materializable.");
            if (manifest.InspectionId != reservation.InspectionId || manifest.RunId != reservation.RunId ||
                manifest.PlanId != reservation.PlanId || manifest.PlanRevision != reservation.PlanRevision)
                throw new InvalidOperationException("The terminal manifest does not match the candidate reservation.");
            if (!reservation.CandidateViewId.IsValid || !reservation.Token.IsValid ||
                !reservation.Generation.IsValid || !manifest.CapturedStoreRevision.IsValid)
                throw new InvalidOperationException($"Candidate materialization identity is invalid: " +
                    $"view={reservation.CandidateViewId.IsValid}, token={reservation.Token.IsValid}, " +
                    $"generation={reservation.Generation.IsValid}, revision={manifest.CapturedStoreRevision.IsValid}.");
            state.Ownership = GateCandidateOwnershipState.Materializing;
            var descriptor = new GateStoreOnlyCandidateDescriptor(manifest, reservation.CandidateViewId,
                reservation.Token, reservation.Generation, GateCandidateOwnershipState.Available);
            state.Descriptor = descriptor; state.Ownership = GateCandidateOwnershipState.Available;
            return descriptor;
        }
    }

    internal GateCandidateActivationResult ActivateCandidate(GateCandidateActivationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        lock (_gate)
        {
            CandidateState? state = _replacementCandidates.Values.SingleOrDefault(x =>
                x.Reservation.CandidateViewId == request.Candidate.CandidateViewId);
            InstalledDesiredReplacement? desired = _desiredReplacements.GetValueOrDefault(
                new(request.Candidate.Manifest.InspectionId, request.Candidate.Manifest.PlanId));
            GateStoreOnlyCandidateDescriptor? observedCandidate = state?.Snapshot();
            GateRunGeneration observedGeneration = _currentRun.IsValid ? _currentRun.Generation : request.ExpectedRunGeneration;
            AcquisitionRunId observedRun = _currentRun.IsValid ? _currentRun.RunId : request.ExpectedRunId;
            ActiveDerivedViewDescriptor? predecessor = desired?.Predecessor;
            GateDerivedBatchKey[] membership = MatchingKeysUnderLock(request.Candidate.Manifest);
            long total = 0;
            bool coverage = CoverageMatches(request.Candidate.Manifest, membership);
            bool capacity = false;
            if (coverage && _revision != ulong.MaxValue && request.Candidate.Manifest.CapturedStoreRevision.Value <= _revision)
            {
                try
                {
                    long manifestSize = GateDerivedLogicalSize.Manifest(request.Candidate.Manifest);
                    long proposedViewSize = checked(64 + System.Text.Encoding.UTF8.GetByteCount(request.Candidate.CandidateViewId.Value));
                    total = checked(manifestSize + proposedViewSize + GateDerivedLogicalSize.Membership(membership.Length));
                    capacity = total <= _options.LogicalPayloadBudgetBytes - _logicalBytes;
                }
                catch (OverflowException) { capacity = false; }
            }
            var observed = new GateReplacementCasObservedState(
                _closed ? GateApplyLedgerLifecycleState.Disposed : GateApplyLedgerLifecycleState.Active,
                request.Candidate.Manifest.InspectionId, observedRun, request.Candidate.Manifest.PlanId,
                request.Candidate.Manifest.PlanRevision, observedGeneration, desired?.Token, desired?.Generation,
                predecessor is null ? GateObservedPredecessorKind.None : GateObservedPredecessorKind.Present,
                predecessor, observedCandidate, state?.Ownership, capacity,
                _activeVisible ? _active?.Descriptor : null);
            GateCandidateActivationResult classified = GateCandidateActivationResult.Classify(request, observed);
            if (classified.Status != GateCandidateActivationStatus.Activated) return classified;
            if (state is null || state.Descriptor is null)
                throw new InvalidOperationException("The classified candidate is unavailable.");
            if (!coverage)
                return GateCandidateActivationResult.Classify(request, new GateReplacementCasObservedState(
                    GateApplyLedgerLifecycleState.Active, request.Candidate.Manifest.InspectionId, observedRun,
                    request.Candidate.Manifest.PlanId, request.Candidate.Manifest.PlanRevision, observedGeneration,
                    desired?.Token, desired?.Generation,
                    predecessor is null ? GateObservedPredecessorKind.None : GateObservedPredecessorKind.Present,
                    predecessor, state.Snapshot(), GateCandidateOwnershipState.Materializing, false,
                    _activeVisible ? _active?.Descriptor : null));
            ulong next = checked(_revision + 1); ulong order = checked(_activationOrder + 1);
            long viewSize = checked(64 + System.Text.Encoding.UTF8.GetByteCount(request.Candidate.CandidateViewId.Value));
            var active = new ActiveDerivedViewDescriptor(request.Candidate.CandidateViewId, new(next),
                request.Candidate.Manifest, order);
            var stored = new StoredView(active, total, viewSize, new(membership));
            var stagedViews = new Dictionary<DerivedViewId, StoredView>(_views) { [active.ViewId] = stored };
            var stagedIds = new HashSet<DerivedViewId>(_usedViewIds) { active.ViewId };
            var stagedInvalidated = new HashSet<DerivedViewId>(_invalidated);
            if (predecessor is not null && _views.ContainsKey(predecessor.ViewId))
                stagedInvalidated.Add(predecessor.ViewId);
            long nextLogicalBytes = checked(_logicalBytes + total);
            state.Ownership = GateCandidateOwnershipState.Activating;
            _views = stagedViews; _usedViewIds = stagedIds; _invalidated = stagedInvalidated;
            _active = stored; _activeVisible = CanExposeActiveViewUnderLock(active);
            _logicalBytes = nextLogicalBytes; _revision = next; _activationOrder = order; _activations++;
            state.Ownership = GateCandidateOwnershipState.Activated;
            return GateCandidateActivationResult.Classify(request, new GateReplacementCasObservedState(
                GateApplyLedgerLifecycleState.Active, request.Candidate.Manifest.InspectionId, observedRun,
                request.Candidate.Manifest.PlanId, request.Candidate.Manifest.PlanRevision, observedGeneration,
                desired?.Token, desired?.Generation,
                predecessor is null ? GateObservedPredecessorKind.None : GateObservedPredecessorKind.Present,
                predecessor, state.Snapshot(), GateCandidateOwnershipState.Activated, true, active));
        }
    }

    internal void ReleaseCandidateReservation(GateCandidateReservation reservation)
    {
        ArgumentNullException.ThrowIfNull(reservation);
        lock (_gate)
        {
            if (!_replacementCandidates.TryGetValue(reservation.Id, out CandidateState? state) ||
                state.Reservation != reservation) return;
            if (state.Ownership != GateCandidateOwnershipState.Reserved) return;
            _replacementCandidates.Remove(reservation.Id);
        }
    }

    internal GateCandidateAbandonResult AbandonCandidate(GateStoreOnlyCandidateDescriptor candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        lock (_gate)
        {
            CandidateState? state = _replacementCandidates.Values.SingleOrDefault(x =>
                x.Reservation.CandidateViewId == candidate.CandidateViewId);
            if (_closed) return GateCandidateAbandonResult.Create(GateCandidateAbandonStatus.Disposed,
                state is null ? WithOwnership(candidate, GateCandidateOwnershipState.Reclaimed) :
                state.Ownership == GateCandidateOwnershipState.Activated ? state.Snapshot() :
                WithOwnership(candidate, GateCandidateOwnershipState.Reclaimed),
                GateApplyLedgerLifecycleState.Disposed);
            if (state is null) return GateCandidateAbandonResult.Create(GateCandidateAbandonStatus.CandidateNotAvailable, candidate);
            if (state.Ownership == GateCandidateOwnershipState.Activated)
                return GateCandidateAbandonResult.Create(GateCandidateAbandonStatus.AlreadyActivated, state.Snapshot());
            if (state.Ownership is GateCandidateOwnershipState.LogicallyAbandoned or GateCandidateOwnershipState.Reclaimed)
                return GateCandidateAbandonResult.Create(GateCandidateAbandonStatus.AlreadyAbandoned, state.Snapshot());
            state.Ownership = GateCandidateOwnershipState.Abandoning;
            state.Ownership = GateCandidateOwnershipState.LogicallyAbandoned;
            return GateCandidateAbandonResult.Create(GateCandidateAbandonStatus.LogicallyAbandoned, state.Snapshot());
        }
    }

    internal GateCandidateReclaimResult ReclaimCandidate(GateStoreOnlyCandidateDescriptor candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        lock (_gate)
        {
            CandidateState? state = _replacementCandidates.Values.SingleOrDefault(x =>
                x.Reservation.CandidateViewId == candidate.CandidateViewId);
            if (_closed) return GateCandidateReclaimResult.Create(GateCandidateReclaimStatus.Disposed,
                state is null ? WithOwnership(candidate, GateCandidateOwnershipState.Reclaimed) :
                state.Ownership == GateCandidateOwnershipState.Activated ? state.Snapshot() :
                WithOwnership(candidate, GateCandidateOwnershipState.Reclaimed),
                GateApplyLedgerLifecycleState.Disposed);
            if (state is null)
                return GateCandidateReclaimResult.Create(GateCandidateReclaimStatus.AlreadyReclaimed,
                    WithOwnership(candidate, GateCandidateOwnershipState.Reclaimed));
            if (state.Ownership == GateCandidateOwnershipState.Activated)
                return GateCandidateReclaimResult.Create(GateCandidateReclaimStatus.AlreadyActivated, state.Snapshot());
            if (state.Ownership != GateCandidateOwnershipState.LogicallyAbandoned)
                return GateCandidateReclaimResult.Create(GateCandidateReclaimStatus.CandidateNotAvailable, state.Snapshot());
            state.Ownership = GateCandidateOwnershipState.Reclaimed;
            GateStoreOnlyCandidateDescriptor reclaimed = state.Snapshot();
            _replacementCandidates.Remove(state.Reservation.Id);
            return GateCandidateReclaimResult.Create(GateCandidateReclaimStatus.Reclaimed, reclaimed);
        }
    }

    internal async ValueTask<GateDerivedMutationResult> CompletePreviousViewRetirementAsync()
    {
        Task ready;
        lock (_gate)
        {
            if (_closed)
                return ResultUnderLock(GateDerivedMutationStatus.Reclaimed);
            if (_admissionClosed)
                ready = _disposeTask ?? throw new InvalidOperationException("Store close has no completion owner.");
            else if (!HasInvalidatedLeaseBlockersUnderLock())
                ready = Task.CompletedTask;
            else
            {
                _replacementRetirementReady ??=
                    new(TaskCreationOptions.RunContinuationsAsynchronously);
                ready = _replacementRetirementReady.Task;
            }
        }
        await ready.ConfigureAwait(false);
        Task? closing;
        lock (_gate)
        {
            if (_closed) return ResultUnderLock(GateDerivedMutationStatus.Reclaimed);
            closing = _admissionClosed ? _disposeTask : null;
        }
        if (closing is not null)
        {
            await closing.ConfigureAwait(false);
            return Result(GateDerivedMutationStatus.Reclaimed);
        }
        GateDerivedMutationResult result = await ReclaimInvalidatedAsync(CancellationToken.None).ConfigureAwait(false);
        if (result.Status == GateDerivedMutationStatus.NotFound)
            return new(GateDerivedMutationStatus.Reclaimed, result.Revision, wasAccepted: false);
        if (result.Status != GateDerivedMutationStatus.Closed) return result;
        lock (_gate) closing = _disposeTask;
        if (closing is null) return result;
        await closing.ConfigureAwait(false);
        return Result(GateDerivedMutationStatus.Reclaimed);
    }

    private void SignalReplacementRetirementIfReadyUnderLock()
    {
        if (_replacementRetirementReady is not null && !HasInvalidatedLeaseBlockersUnderLock())
        {
            TaskCompletionSource ready = _replacementRetirementReady;
            _replacementRetirementReady = null;
            ready.TrySetResult();
        }
    }

    private static GateStoreOnlyCandidateDescriptor WithOwnership(GateStoreOnlyCandidateDescriptor descriptor,
        GateCandidateOwnershipState ownership) => new(descriptor.Manifest, descriptor.CandidateViewId,
            descriptor.ReplacementToken, descriptor.Generation, ownership);

    private void RetireReplacementClaimsForRunUnderLock(AcquisitionRunId runId)
    {
        foreach (var key in _candidateRevisionClaims.Keys.Where(key => key.Item2 == runId).ToArray())
            _candidateRevisionClaims.Remove(key);
    }

    private void ClearReplacementStateUnderLock()
    {
        _replacementCandidates.Clear(); _candidateRevisionClaims.Clear();
        _replacementRetirementReady?.TrySetResult(); _replacementRetirementReady = null;
    }

    private sealed class CandidateState(GateCandidateReservation reservation)
    {
        internal GateCandidateReservation Reservation { get; } = reservation;
        internal GateCandidateOwnershipState Ownership { get; set; } = GateCandidateOwnershipState.Reserved;
        internal GateStoreOnlyCandidateDescriptor? Descriptor { get; set; }
        internal GateStoreOnlyCandidateDescriptor Snapshot() => Descriptor is null ?
            throw new InvalidOperationException("The candidate has no materialized descriptor.") :
            WithOwnership(Descriptor, Ownership);
    }
}
