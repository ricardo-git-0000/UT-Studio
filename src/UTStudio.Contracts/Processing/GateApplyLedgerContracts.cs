using System.Collections.ObjectModel;
using UTStudio.Domain.Acquisition;
using UTStudio.Domain.Inspection;
using UTStudio.Domain.Processing;

namespace UTStudio.Contracts.Processing;

public enum GateApplyCommitState
{ Unknown, Preparing, Accepted, ValidationFailed, Conflict, CapacityExceeded, CancelledBeforeCommit, FaultedBeforeCommit }
public enum GateLiveEpochState
{ Unknown, Dormant, Registered, Active, Drained, NotApplicable, Superseded, Cancelled, Faulted }
public enum GateHistoricalAdmissionState
{ Unknown, Dormant, Deferred, Admitting, Admitted, NotApplicable, Superseded, Cancelled, Rejected, Faulted }
public enum GateHistoricalExecutionState
{ Unknown, Dormant, Queued, Running, Completed, NotApplicable, Superseded, Cancelled, Faulted }
public enum GateCandidateCoverageState
{ Unknown, Dormant, Building, Complete, NotApplicable, Superseded, Cancelled, Faulted }
public enum GateActivationState
{ Unknown, Dormant, Eligible, Activating, Activated, NotApplicable, Superseded, CasRejected, RunSuperseded, Abandoned, Faulted }
public enum GateCandidateRetirementState
{ Unknown, Dormant, Required, Abandoning, LogicallyAbandoned, Reclaimed, NotApplicable, Activated }
public enum GatePreviousViewRetirementState
{ Unknown, Dormant, Required, Retiring, Retired, NotApplicable, DeferredByLeases, Faulted }
public enum GateLedgerStage
{ Unknown, Commit, LiveEpoch, HistoricalAdmission, HistoricalExecution, CandidateCoverage, Activation, CandidateRetirement, PreviousViewRetirement, Lifecycle }
public enum GateLedgerCauseKind
{
    Unknown, ValidationFailed, RevisionConflict, CapacityExceeded, Cancelled, Faulted, Superseded,
    Rejected, TokenSuperseded, PredecessorMismatch, CandidateMismatch, CandidateNotAvailable,
    AlreadyActivated, Disposed, RunSuperseded, CandidateAbandoned, ReclaimBusy, DeferredByLeases
}

public enum GateApplyLedgerTransitionStatus
{ Unknown, Valid, IdentityMismatch, RevisionMismatch, IllegalStageTransition, TerminalReopened, OwnershipMismatch, CauseRegression }
public enum GateApplyLedgerLifecycleState { Unknown, Active, Disposing, Disposed }
[Flags]
public enum GateApplyLedgerPendingResource { Unknown = 0, None = Unknown, Observer = 1, Lease = 2, Tombstone = 4 }

public readonly record struct GateLedgerCause
{
    public GateLedgerCause(GateLedgerStage stage, GateLedgerCauseKind kind,
        GatePlanCapacityResource? capacityResource = null)
    {
        if (!Enum.IsDefined(stage) || stage == GateLedgerStage.Unknown) throw new ArgumentOutOfRangeException(nameof(stage));
        if (!Enum.IsDefined(kind) || kind == GateLedgerCauseKind.Unknown) throw new ArgumentOutOfRangeException(nameof(kind));
        if ((kind == GateLedgerCauseKind.CapacityExceeded) != capacityResource.HasValue)
            throw new ArgumentException("Only capacity failures carry a capacity resource.", nameof(capacityResource));
        if (capacityResource is { } resource && (!Enum.IsDefined(resource) || resource == GatePlanCapacityResource.Unknown))
            throw new ArgumentOutOfRangeException(nameof(capacityResource));
        bool compatible = stage switch
        {
            GateLedgerStage.Commit => kind is GateLedgerCauseKind.ValidationFailed or GateLedgerCauseKind.RevisionConflict or
                GateLedgerCauseKind.CapacityExceeded or GateLedgerCauseKind.Cancelled or GateLedgerCauseKind.Faulted,
            GateLedgerStage.LiveEpoch => kind is GateLedgerCauseKind.Superseded or GateLedgerCauseKind.Cancelled or GateLedgerCauseKind.Faulted,
            GateLedgerStage.HistoricalAdmission => kind is GateLedgerCauseKind.Superseded or GateLedgerCauseKind.Cancelled or
                GateLedgerCauseKind.Rejected or GateLedgerCauseKind.Faulted,
            GateLedgerStage.HistoricalExecution or GateLedgerStage.CandidateCoverage => kind is
                GateLedgerCauseKind.Superseded or GateLedgerCauseKind.Cancelled or GateLedgerCauseKind.Faulted,
            GateLedgerStage.Activation => kind is GateLedgerCauseKind.Superseded or GateLedgerCauseKind.TokenSuperseded or
                GateLedgerCauseKind.PredecessorMismatch or GateLedgerCauseKind.CandidateMismatch or
                GateLedgerCauseKind.CandidateNotAvailable or GateLedgerCauseKind.AlreadyActivated or
                GateLedgerCauseKind.RunSuperseded or
                GateLedgerCauseKind.CandidateAbandoned or GateLedgerCauseKind.Faulted,
            GateLedgerStage.CandidateRetirement => kind == GateLedgerCauseKind.ReclaimBusy,
            GateLedgerStage.PreviousViewRetirement => kind is GateLedgerCauseKind.DeferredByLeases or GateLedgerCauseKind.Faulted,
            GateLedgerStage.Lifecycle => kind == GateLedgerCauseKind.Disposed,
            _ => false
        };
        if (!compatible) throw new ArgumentException("The cause kind is invalid for the ledger stage.", nameof(kind));
        Stage = stage; Kind = kind; CapacityResource = capacityResource;
    }
    public GateLedgerStage Stage { get; }
    public GateLedgerCauseKind Kind { get; }
    public GatePlanCapacityResource? CapacityResource { get; }
}

public readonly record struct GateApplyLedgerTransitionResult
{
    private GateApplyLedgerTransitionResult(GateApplyLedgerTransitionStatus status, GateLedgerStage? stage)
    { Status = status; Stage = stage; }
    public GateApplyLedgerTransitionStatus Status { get; }
    public GateLedgerStage? Stage { get; }

    public static GateApplyLedgerTransitionResult Validate(GateApplyLedgerSnapshot previous,
        GateApplyLedgerSnapshot next)
    {
        ArgumentNullException.ThrowIfNull(previous); ArgumentNullException.ThrowIfNull(next);
        if (previous.InspectionId != next.InspectionId || previous.RunId != next.RunId ||
            previous.RunGeneration != next.RunGeneration || previous.PlanId != next.PlanId)
            return new(GateApplyLedgerTransitionStatus.IdentityMismatch, null);
        if (previous.PlanRevision != next.PlanRevision)
            return new(GateApplyLedgerTransitionStatus.RevisionMismatch, null);
        if (!LifecycleCanAdvance(previous.Lifecycle, next.Lifecycle))
            return new(previous.Lifecycle == GateApplyLedgerLifecycleState.Disposed ?
                GateApplyLedgerTransitionStatus.TerminalReopened : GateApplyLedgerTransitionStatus.IllegalStageTransition,
                GateLedgerStage.Lifecycle);
        if (next.Lifecycle != GateApplyLedgerLifecycleState.Active && StartsMaterialWork(previous, next))
            return new(GateApplyLedgerTransitionStatus.IllegalStageTransition, GateLedgerStage.Lifecycle);
        if (previous.Lifecycle == GateApplyLedgerLifecycleState.Disposing &&
            (next.PendingResources & ~previous.PendingResources) != 0)
            return new(GateApplyLedgerTransitionStatus.IllegalStageTransition, GateLedgerStage.Lifecycle);
        if (next.Lifecycle == GateApplyLedgerLifecycleState.Disposed && !MaterialStateEquals(previous, next))
            return new(GateApplyLedgerTransitionStatus.IllegalStageTransition, GateLedgerStage.Lifecycle);
        (GateLedgerStage Stage, bool Valid, bool Reopened)[] transitions =
        [
            Check(GateLedgerStage.Commit, previous.Commit, next.Commit, CommitEdges),
            Check(GateLedgerStage.LiveEpoch, previous.LiveEpoch, next.LiveEpoch, LiveEdges),
            Check(GateLedgerStage.HistoricalAdmission, previous.HistoricalAdmission, next.HistoricalAdmission, AdmissionEdges),
            Check(GateLedgerStage.HistoricalExecution, previous.HistoricalExecution, next.HistoricalExecution, ExecutionEdges),
            Check(GateLedgerStage.CandidateCoverage, previous.CandidateCoverage, next.CandidateCoverage, CoverageEdges),
            Check(GateLedgerStage.Activation, previous.Activation, next.Activation, ActivationEdges),
            Check(GateLedgerStage.CandidateRetirement, previous.CandidateRetirement, next.CandidateRetirement, RetirementEdges),
            Check(GateLedgerStage.PreviousViewRetirement, previous.PreviousViewRetirement, next.PreviousViewRetirement, PreviousEdges)
        ];
        (GateLedgerStage Stage, bool Valid, bool Reopened) invalid = transitions.FirstOrDefault(x => !x.Valid);
        if (invalid.Stage != GateLedgerStage.Unknown)
            return new(invalid.Reopened ? GateApplyLedgerTransitionStatus.TerminalReopened :
                GateApplyLedgerTransitionStatus.IllegalStageTransition, invalid.Stage);
        if (!OwnershipCanAdvance(previous.CandidateOwnership, next.CandidateOwnership))
            return new(GateApplyLedgerTransitionStatus.OwnershipMismatch, GateLedgerStage.CandidateRetirement);
        Dictionary<GateLedgerStage, GateLedgerCause> before = Causes(previous);
        Dictionary<GateLedgerStage, GateLedgerCause> after = Causes(next);
        if (previous.PrimaryCause is { } previousPrimary && next.PrimaryCause != previousPrimary ||
            before.Any(x => !after.TryGetValue(x.Key, out GateLedgerCause cause) || cause != x.Value))
            return new(GateApplyLedgerTransitionStatus.CauseRegression, null);
        return new(GateApplyLedgerTransitionStatus.Valid, null);
    }

    private static (GateLedgerStage, bool, bool) Check<T>(GateLedgerStage stage, T previous, T next,
        Func<T, T, bool> edges) where T : struct, Enum
    {
        if (EqualityComparer<T>.Default.Equals(previous, next)) return (stage, true, false);
        bool terminal = IsTerminalState(stage, previous.ToString());
        return (stage, edges(previous, next), terminal);
    }
    private static bool IsTerminalState(GateLedgerStage stage, string state) => stage switch
    {
        GateLedgerStage.Commit => state is not nameof(GateApplyCommitState.Preparing),
        GateLedgerStage.LiveEpoch => state is nameof(GateLiveEpochState.Drained) or nameof(GateLiveEpochState.NotApplicable) or
            nameof(GateLiveEpochState.Superseded) or nameof(GateLiveEpochState.Cancelled) or nameof(GateLiveEpochState.Faulted),
        GateLedgerStage.HistoricalAdmission => state is nameof(GateHistoricalAdmissionState.Admitted) or
            nameof(GateHistoricalAdmissionState.NotApplicable) or nameof(GateHistoricalAdmissionState.Superseded) or
            nameof(GateHistoricalAdmissionState.Cancelled) or nameof(GateHistoricalAdmissionState.Rejected) or nameof(GateHistoricalAdmissionState.Faulted),
        GateLedgerStage.HistoricalExecution => state is nameof(GateHistoricalExecutionState.Completed) or
            nameof(GateHistoricalExecutionState.NotApplicable) or nameof(GateHistoricalExecutionState.Superseded) or
            nameof(GateHistoricalExecutionState.Cancelled) or nameof(GateHistoricalExecutionState.Faulted),
        GateLedgerStage.CandidateCoverage => state is nameof(GateCandidateCoverageState.Complete) or
            nameof(GateCandidateCoverageState.NotApplicable) or nameof(GateCandidateCoverageState.Superseded) or
            nameof(GateCandidateCoverageState.Cancelled) or nameof(GateCandidateCoverageState.Faulted),
        GateLedgerStage.Activation => state is nameof(GateActivationState.Activated) or nameof(GateActivationState.NotApplicable) or
            nameof(GateActivationState.Superseded) or nameof(GateActivationState.CasRejected) or
            nameof(GateActivationState.RunSuperseded) or nameof(GateActivationState.Abandoned) or nameof(GateActivationState.Faulted),
        GateLedgerStage.CandidateRetirement => state is nameof(GateCandidateRetirementState.Reclaimed) or
            nameof(GateCandidateRetirementState.NotApplicable) or nameof(GateCandidateRetirementState.Activated),
        GateLedgerStage.PreviousViewRetirement => state is nameof(GatePreviousViewRetirementState.Retired) or
            nameof(GatePreviousViewRetirementState.NotApplicable) or nameof(GatePreviousViewRetirementState.DeferredByLeases) or
            nameof(GatePreviousViewRetirementState.Faulted),
        _ => false
    };
    private static bool CommitEdges(GateApplyCommitState from, GateApplyCommitState to) =>
        from == GateApplyCommitState.Preparing && to != GateApplyCommitState.Unknown;
    private static bool LiveEdges(GateLiveEpochState from, GateLiveEpochState to) => from switch
    {
        GateLiveEpochState.Dormant => to is GateLiveEpochState.Registered or GateLiveEpochState.NotApplicable or
            GateLiveEpochState.Superseded or GateLiveEpochState.Cancelled or GateLiveEpochState.Faulted,
        GateLiveEpochState.Registered => to is GateLiveEpochState.Active or GateLiveEpochState.Superseded or
            GateLiveEpochState.Cancelled or GateLiveEpochState.Faulted,
        GateLiveEpochState.Active => to is GateLiveEpochState.Drained or GateLiveEpochState.Superseded or
            GateLiveEpochState.Cancelled or GateLiveEpochState.Faulted,
        _ => false
    };
    private static bool AdmissionEdges(GateHistoricalAdmissionState from, GateHistoricalAdmissionState to) => from switch
    {
        GateHistoricalAdmissionState.Dormant => to is GateHistoricalAdmissionState.Deferred or
            GateHistoricalAdmissionState.NotApplicable or GateHistoricalAdmissionState.Superseded or
            GateHistoricalAdmissionState.Cancelled or GateHistoricalAdmissionState.Rejected or GateHistoricalAdmissionState.Faulted,
        GateHistoricalAdmissionState.Deferred => to is GateHistoricalAdmissionState.Admitting or
            GateHistoricalAdmissionState.Superseded or GateHistoricalAdmissionState.Cancelled or
            GateHistoricalAdmissionState.Rejected or GateHistoricalAdmissionState.Faulted,
        GateHistoricalAdmissionState.Admitting => to is GateHistoricalAdmissionState.Admitted or
            GateHistoricalAdmissionState.Superseded or GateHistoricalAdmissionState.Cancelled or
            GateHistoricalAdmissionState.Rejected or GateHistoricalAdmissionState.Faulted,
        _ => false
    };
    private static bool ExecutionEdges(GateHistoricalExecutionState from, GateHistoricalExecutionState to) => from switch
    {
        GateHistoricalExecutionState.Dormant => to is GateHistoricalExecutionState.Queued or
            GateHistoricalExecutionState.NotApplicable or GateHistoricalExecutionState.Superseded or
            GateHistoricalExecutionState.Cancelled or GateHistoricalExecutionState.Faulted,
        GateHistoricalExecutionState.Queued => to is GateHistoricalExecutionState.Running or
            GateHistoricalExecutionState.Superseded or GateHistoricalExecutionState.Cancelled or GateHistoricalExecutionState.Faulted,
        GateHistoricalExecutionState.Running => to is GateHistoricalExecutionState.Completed or
            GateHistoricalExecutionState.Superseded or GateHistoricalExecutionState.Cancelled or GateHistoricalExecutionState.Faulted,
        _ => false
    };
    private static bool CoverageEdges(GateCandidateCoverageState from, GateCandidateCoverageState to) => from switch
    {
        GateCandidateCoverageState.Dormant => to is GateCandidateCoverageState.Building or
            GateCandidateCoverageState.NotApplicable or GateCandidateCoverageState.Superseded or
            GateCandidateCoverageState.Cancelled or GateCandidateCoverageState.Faulted,
        GateCandidateCoverageState.Building => to is GateCandidateCoverageState.Complete or
            GateCandidateCoverageState.Superseded or GateCandidateCoverageState.Cancelled or GateCandidateCoverageState.Faulted,
        _ => false
    };
    private static bool ActivationEdges(GateActivationState from, GateActivationState to) => from switch
    {
        GateActivationState.Dormant => to is GateActivationState.Eligible or GateActivationState.NotApplicable or
            GateActivationState.Superseded or GateActivationState.CasRejected or GateActivationState.RunSuperseded or
            GateActivationState.Abandoned or GateActivationState.Faulted,
        GateActivationState.Eligible => to is GateActivationState.Activating or GateActivationState.CasRejected or
            GateActivationState.Superseded or GateActivationState.RunSuperseded or GateActivationState.Abandoned or GateActivationState.Faulted,
        GateActivationState.Activating => to is GateActivationState.Activated or GateActivationState.CasRejected or
            GateActivationState.Superseded or GateActivationState.RunSuperseded or GateActivationState.Abandoned or GateActivationState.Faulted,
        _ => false
    };
    private static bool RetirementEdges(GateCandidateRetirementState from, GateCandidateRetirementState to) => from switch
    {
        GateCandidateRetirementState.Dormant => to is GateCandidateRetirementState.Required or
            GateCandidateRetirementState.NotApplicable or GateCandidateRetirementState.Activated,
        GateCandidateRetirementState.Required => to == GateCandidateRetirementState.Abandoning,
        GateCandidateRetirementState.Abandoning => to == GateCandidateRetirementState.LogicallyAbandoned,
        GateCandidateRetirementState.LogicallyAbandoned => to == GateCandidateRetirementState.Reclaimed,
        _ => false
    };
    private static bool PreviousEdges(GatePreviousViewRetirementState from, GatePreviousViewRetirementState to) => from switch
    {
        GatePreviousViewRetirementState.Dormant => to is GatePreviousViewRetirementState.Required or
            GatePreviousViewRetirementState.NotApplicable or GatePreviousViewRetirementState.DeferredByLeases or
            GatePreviousViewRetirementState.Faulted,
        GatePreviousViewRetirementState.Required => to is GatePreviousViewRetirementState.Retiring or
            GatePreviousViewRetirementState.DeferredByLeases or GatePreviousViewRetirementState.Faulted,
        GatePreviousViewRetirementState.Retiring => to is GatePreviousViewRetirementState.Retired or
            GatePreviousViewRetirementState.DeferredByLeases or GatePreviousViewRetirementState.Faulted,
        _ => false
    };
    private static bool OwnershipCanAdvance(GateCandidateOwnershipState? from, GateCandidateOwnershipState? to) =>
        from == to || from switch
        {
            null => to == GateCandidateOwnershipState.Reserved,
            GateCandidateOwnershipState.Reserved => to is GateCandidateOwnershipState.Materializing or GateCandidateOwnershipState.Abandoning,
            GateCandidateOwnershipState.Materializing => to is GateCandidateOwnershipState.Available or GateCandidateOwnershipState.Abandoning,
            GateCandidateOwnershipState.Available => to is GateCandidateOwnershipState.Activating or GateCandidateOwnershipState.Abandoning,
            GateCandidateOwnershipState.Activating => to is GateCandidateOwnershipState.Available or
                GateCandidateOwnershipState.Activated or GateCandidateOwnershipState.Abandoning,
            GateCandidateOwnershipState.Abandoning => to == GateCandidateOwnershipState.LogicallyAbandoned,
            GateCandidateOwnershipState.LogicallyAbandoned => to == GateCandidateOwnershipState.Reclaimed,
            _ => false
        };
    private static bool LifecycleCanAdvance(GateApplyLedgerLifecycleState from, GateApplyLedgerLifecycleState to) =>
        from == to || from == GateApplyLedgerLifecycleState.Active && to == GateApplyLedgerLifecycleState.Disposing ||
        from == GateApplyLedgerLifecycleState.Disposing && to == GateApplyLedgerLifecycleState.Disposed;
    private static bool StartsMaterialWork(GateApplyLedgerSnapshot previous, GateApplyLedgerSnapshot next) =>
        previous.LiveEpoch == GateLiveEpochState.Dormant && next.LiveEpoch == GateLiveEpochState.Registered ||
        previous.HistoricalAdmission == GateHistoricalAdmissionState.Dormant && next.HistoricalAdmission is
            GateHistoricalAdmissionState.Deferred or GateHistoricalAdmissionState.Admitting or GateHistoricalAdmissionState.Admitted ||
        previous.HistoricalExecution == GateHistoricalExecutionState.Dormant && next.HistoricalExecution is
            GateHistoricalExecutionState.Queued or GateHistoricalExecutionState.Running ||
        previous.CandidateCoverage == GateCandidateCoverageState.Dormant && next.CandidateCoverage == GateCandidateCoverageState.Building ||
        previous.Activation == GateActivationState.Dormant && next.Activation == GateActivationState.Eligible ||
        previous.Activation == GateActivationState.Eligible && next.Activation == GateActivationState.Activating ||
        previous.CandidateOwnership is null && next.CandidateOwnership == GateCandidateOwnershipState.Reserved;
    private static bool MaterialStateEquals(GateApplyLedgerSnapshot left, GateApplyLedgerSnapshot right) =>
        left.Commit == right.Commit && left.LiveEpoch == right.LiveEpoch &&
        left.HistoricalAdmission == right.HistoricalAdmission && left.HistoricalExecution == right.HistoricalExecution &&
        left.CandidateCoverage == right.CandidateCoverage && left.Activation == right.Activation &&
        left.CandidateRetirement == right.CandidateRetirement &&
        left.PreviousViewRetirement == right.PreviousViewRetirement && left.CandidateOwnership == right.CandidateOwnership;
    private static Dictionary<GateLedgerStage, GateLedgerCause> Causes(GateApplyLedgerSnapshot snapshot) =>
        (snapshot.PrimaryCause is { } primary ? new[] { primary }.Concat(snapshot.SecondaryCauses) : snapshot.SecondaryCauses)
        .ToDictionary(x => x.Stage);
}

public sealed class GateApplyLedgerSnapshot : IEquatable<GateApplyLedgerSnapshot>
{
    private readonly ReadOnlyCollection<GateLedgerCause> _secondaryCauses;
    private GateApplyLedgerSnapshot(InspectionId inspectionId, AcquisitionRunId runId, GateRunGeneration runGeneration,
        GatePlanId planId, GatePlanRevision planRevision, GateApplyCommitState commit, GateLiveEpochState liveEpoch,
        GateHistoricalAdmissionState historicalAdmission, GateHistoricalExecutionState historicalExecution,
        GateCandidateCoverageState candidateCoverage, GateActivationState activation,
        GateCandidateRetirementState candidateRetirement, GatePreviousViewRetirementState previousViewRetirement,
        GateCandidateOwnershipState? candidateOwnership, GateLedgerCause? primaryCause, GateLedgerCause[] secondaryCauses,
        GateApplyLedgerLifecycleState lifecycle, GateApplyLedgerPendingResource pendingResources)
    {
        InspectionId = inspectionId; RunId = runId; RunGeneration = runGeneration; PlanId = planId;
        PlanRevision = planRevision; Commit = commit; LiveEpoch = liveEpoch; HistoricalAdmission = historicalAdmission;
        HistoricalExecution = historicalExecution; CandidateCoverage = candidateCoverage; Activation = activation;
        CandidateRetirement = candidateRetirement; PreviousViewRetirement = previousViewRetirement;
        CandidateOwnership = candidateOwnership; PrimaryCause = primaryCause; _secondaryCauses = Array.AsReadOnly(secondaryCauses);
        Lifecycle = lifecycle; PendingResources = pendingResources;
    }
    public InspectionId InspectionId { get; }
    public AcquisitionRunId RunId { get; }
    public GateRunGeneration RunGeneration { get; }
    public GatePlanId PlanId { get; }
    public GatePlanRevision PlanRevision { get; }
    public GateApplyCommitState Commit { get; }
    public GateLiveEpochState LiveEpoch { get; }
    public GateHistoricalAdmissionState HistoricalAdmission { get; }
    public GateHistoricalExecutionState HistoricalExecution { get; }
    public GateCandidateCoverageState CandidateCoverage { get; }
    public GateActivationState Activation { get; }
    public GateCandidateRetirementState CandidateRetirement { get; }
    public GatePreviousViewRetirementState PreviousViewRetirement { get; }
    public GateCandidateOwnershipState? CandidateOwnership { get; }
    public GateLedgerCause? PrimaryCause { get; }
    public IReadOnlyList<GateLedgerCause> SecondaryCauses => _secondaryCauses;
    public GateApplyLedgerLifecycleState Lifecycle { get; }
    public GateApplyLedgerPendingResource PendingResources { get; }

    public static GateApplyLedgerSnapshot Create(InspectionId inspectionId, AcquisitionRunId runId,
        GateRunGeneration runGeneration, GatePlanId planId, GatePlanRevision planRevision,
        GateApplyCommitState commit, GateLiveEpochState liveEpoch, GateHistoricalAdmissionState historicalAdmission,
        GateHistoricalExecutionState historicalExecution, GateCandidateCoverageState candidateCoverage,
        GateActivationState activation, GateCandidateRetirementState candidateRetirement,
        GatePreviousViewRetirementState previousViewRetirement, GateCandidateOwnershipState? candidateOwnership = null,
        GateLedgerCause? primaryCause = null,
        IEnumerable<GateLedgerCause>? secondaryCauses = null,
        GateApplyLedgerLifecycleState lifecycle = GateApplyLedgerLifecycleState.Active,
        GateApplyLedgerPendingResource pendingResources = GateApplyLedgerPendingResource.None)
    {
        if (!inspectionId.IsValid || !runId.IsValid || !runGeneration.IsValid || !planId.IsValid || !planRevision.IsValid)
            throw new ArgumentException("Valid ledger identity is required.");
        ValidateEnum(commit, nameof(commit)); ValidateEnum(liveEpoch, nameof(liveEpoch));
        ValidateEnum(historicalAdmission, nameof(historicalAdmission)); ValidateEnum(historicalExecution, nameof(historicalExecution));
        ValidateEnum(candidateCoverage, nameof(candidateCoverage)); ValidateEnum(activation, nameof(activation));
        ValidateEnum(candidateRetirement, nameof(candidateRetirement)); ValidateEnum(previousViewRetirement, nameof(previousViewRetirement));
        ValidateEnum(lifecycle, nameof(lifecycle));
        const GateApplyLedgerPendingResource knownResources = GateApplyLedgerPendingResource.Observer |
            GateApplyLedgerPendingResource.Lease | GateApplyLedgerPendingResource.Tombstone;
        if ((pendingResources & ~knownResources) != 0) throw new ArgumentOutOfRangeException(nameof(pendingResources));
        GateLedgerCause[] captured = (secondaryCauses ?? []).OrderBy(x => x.Stage).ToArray();
        if (primaryCause is null && captured.Length != 0)
            throw new ArgumentException("Secondary causes require a primary cause.", nameof(secondaryCauses));
        GateLedgerCause[] all = primaryCause is { } primary ? [primary, .. captured] : captured;
        if (all.Any(x => x.Stage == GateLedgerStage.Unknown || x.Kind == GateLedgerCauseKind.Unknown) ||
            all.Select(x => x.Stage).Distinct().Count() != all.Length)
            throw new ArgumentException("Ledger causes must be valid and unique by stage.", nameof(secondaryCauses));
        ValidateCombination(commit, liveEpoch, historicalAdmission, historicalExecution, candidateCoverage,
            activation, candidateRetirement, previousViewRetirement, candidateOwnership, lifecycle, pendingResources);
        ValidateCauses(commit, liveEpoch, historicalAdmission, historicalExecution, candidateCoverage,
            activation, candidateRetirement, previousViewRetirement, all);
        RequireCause(GateLedgerStage.Lifecycle,
            lifecycle is GateApplyLedgerLifecycleState.Disposing or GateApplyLedgerLifecycleState.Disposed ?
                GateLedgerCauseKind.Disposed : null, all);
        return new(inspectionId, runId, runGeneration, planId, planRevision, commit, liveEpoch,
            historicalAdmission, historicalExecution, candidateCoverage, activation, candidateRetirement,
            previousViewRetirement, candidateOwnership, primaryCause, captured, lifecycle, pendingResources);
    }

    public bool Equals(GateApplyLedgerSnapshot? other) => other is not null && InspectionId == other.InspectionId &&
        RunId == other.RunId && RunGeneration == other.RunGeneration && PlanId == other.PlanId &&
        PlanRevision == other.PlanRevision && Commit == other.Commit && LiveEpoch == other.LiveEpoch &&
        HistoricalAdmission == other.HistoricalAdmission && HistoricalExecution == other.HistoricalExecution &&
        CandidateCoverage == other.CandidateCoverage && Activation == other.Activation &&
        CandidateRetirement == other.CandidateRetirement && PreviousViewRetirement == other.PreviousViewRetirement &&
        CandidateOwnership == other.CandidateOwnership && PrimaryCause == other.PrimaryCause &&
        Lifecycle == other.Lifecycle && PendingResources == other.PendingResources &&
        SecondaryCauses.SequenceEqual(other.SecondaryCauses);
    public override bool Equals(object? obj) => Equals(obj as GateApplyLedgerSnapshot);
    public override int GetHashCode()
    {
        HashCode hash = new(); hash.Add(InspectionId); hash.Add(RunId); hash.Add(RunGeneration); hash.Add(PlanId);
        hash.Add(PlanRevision); hash.Add(Commit); hash.Add(LiveEpoch); hash.Add(HistoricalAdmission);
        hash.Add(HistoricalExecution); hash.Add(CandidateCoverage); hash.Add(Activation); hash.Add(CandidateRetirement);
        hash.Add(PreviousViewRetirement); hash.Add(CandidateOwnership); hash.Add(PrimaryCause);
        hash.Add(Lifecycle); hash.Add(PendingResources);
        foreach (GateLedgerCause cause in SecondaryCauses) hash.Add(cause); return hash.ToHashCode();
    }

    private static void ValidateCombination(GateApplyCommitState commit, GateLiveEpochState liveEpoch,
        GateHistoricalAdmissionState admission, GateHistoricalExecutionState execution,
        GateCandidateCoverageState coverage, GateActivationState activation,
        GateCandidateRetirementState candidateRetirement, GatePreviousViewRetirementState previousRetirement,
        GateCandidateOwnershipState? candidateOwnership, GateApplyLedgerLifecycleState lifecycle,
        GateApplyLedgerPendingResource pendingResources)
    {
        if (commit != GateApplyCommitState.Accepted)
        {
            if (liveEpoch != GateLiveEpochState.Dormant || admission != GateHistoricalAdmissionState.Dormant ||
                execution != GateHistoricalExecutionState.Dormant || coverage != GateCandidateCoverageState.Dormant ||
                activation != GateActivationState.Dormant || candidateRetirement != GateCandidateRetirementState.Dormant ||
                previousRetirement != GatePreviousViewRetirementState.Dormant)
                throw new ArgumentException("Effects must remain dormant before an accepted commit.");
            if (candidateOwnership is not null)
                throw new ArgumentException("A candidate cannot exist before an accepted commit.", nameof(candidateOwnership));
            if (lifecycle == GateApplyLedgerLifecycleState.Disposed && pendingResources != GateApplyLedgerPendingResource.None)
                throw new ArgumentException("Disposed lifecycle cannot retain pending resources.", nameof(pendingResources));
            return;
        }
        if (execution is GateHistoricalExecutionState.Queued or GateHistoricalExecutionState.Running or GateHistoricalExecutionState.Completed &&
            admission != GateHistoricalAdmissionState.Admitted)
            throw new ArgumentException("Historical execution requires admission.");
        if (admission is GateHistoricalAdmissionState.NotApplicable or GateHistoricalAdmissionState.Superseded or
            GateHistoricalAdmissionState.Cancelled or GateHistoricalAdmissionState.Rejected or GateHistoricalAdmissionState.Faulted &&
            execution is not (GateHistoricalExecutionState.Dormant or GateHistoricalExecutionState.NotApplicable or
                GateHistoricalExecutionState.Superseded or GateHistoricalExecutionState.Cancelled or GateHistoricalExecutionState.Faulted))
            throw new ArgumentException("Historical execution contradicts terminal admission.");
        if (coverage == GateCandidateCoverageState.Building && execution != GateHistoricalExecutionState.Running ||
            coverage == GateCandidateCoverageState.Complete && execution != GateHistoricalExecutionState.Completed)
            throw new ArgumentException("Candidate coverage contradicts historical execution.");
        if (activation is GateActivationState.Eligible or GateActivationState.Activating or GateActivationState.Activated &&
            coverage != GateCandidateCoverageState.Complete)
            throw new ArgumentException("Activation requires complete candidate coverage.");
        bool candidateRetiring = candidateRetirement is GateCandidateRetirementState.Required or
            GateCandidateRetirementState.Abandoning or GateCandidateRetirementState.LogicallyAbandoned or GateCandidateRetirementState.Reclaimed;
        bool unsuccessfulActivation = activation is GateActivationState.Abandoned or GateActivationState.CasRejected or
            GateActivationState.Superseded or GateActivationState.RunSuperseded or GateActivationState.Faulted;
        if (candidateRetiring && !unsuccessfulActivation)
            throw new ArgumentException("Candidate retirement requires unsuccessful terminal activation.");
        if (activation == GateActivationState.Abandoned && !candidateRetiring)
            throw new ArgumentException("Terminal abandonment requires candidate retirement.");
        if (candidateRetirement == GateCandidateRetirementState.Activated && activation != GateActivationState.Activated)
            throw new ArgumentException("Candidate retirement Activated requires successful activation.");
        bool previousRetiring = previousRetirement is GatePreviousViewRetirementState.Required or
            GatePreviousViewRetirementState.Retiring or GatePreviousViewRetirementState.Retired or
            GatePreviousViewRetirementState.DeferredByLeases;
        if (previousRetiring && activation != GateActivationState.Activated)
            throw new ArgumentException("Previous-view retirement requires successful activation.");
        if (activation == GateActivationState.Activated && candidateRetirement is not
            (GateCandidateRetirementState.Activated or GateCandidateRetirementState.NotApplicable))
            throw new ArgumentException("Successful activation closes candidate retirement as activated or not applicable.");
        if (candidateOwnership is { } ownership && (!Enum.IsDefined(ownership) || ownership == GateCandidateOwnershipState.Unknown))
            throw new ArgumentOutOfRangeException(nameof(candidateOwnership));
        GateCandidateOwnershipState? requiredOwnership = DeriveCandidateOwnership(admission, execution, coverage,
            activation, candidateRetirement);
        if (candidateOwnership != requiredOwnership)
            throw new ArgumentException("Candidate ownership contradicts the ledger state.", nameof(candidateOwnership));
        if (lifecycle == GateApplyLedgerLifecycleState.Disposed)
        {
            if (pendingResources != GateApplyLedgerPendingResource.None)
                throw new ArgumentException("Disposed lifecycle cannot retain observers, leases, or tombstones.", nameof(pendingResources));
            if (candidateOwnership is not (null or GateCandidateOwnershipState.Activated or GateCandidateOwnershipState.Reclaimed) ||
                liveEpoch is GateLiveEpochState.Registered or GateLiveEpochState.Active ||
                admission is GateHistoricalAdmissionState.Deferred or GateHistoricalAdmissionState.Admitting ||
                execution is GateHistoricalExecutionState.Queued or GateHistoricalExecutionState.Running ||
                coverage == GateCandidateCoverageState.Building ||
                activation is GateActivationState.Eligible or GateActivationState.Activating ||
                candidateRetirement is GateCandidateRetirementState.Required or GateCandidateRetirementState.Abandoning or
                    GateCandidateRetirementState.LogicallyAbandoned ||
                previousRetirement is GatePreviousViewRetirementState.Required or GatePreviousViewRetirementState.Retiring or
                    GatePreviousViewRetirementState.DeferredByLeases)
                throw new ArgumentException("Disposed lifecycle requires material cleanup to be terminal.", nameof(lifecycle));
        }
    }

    private static GateCandidateOwnershipState? DeriveCandidateOwnership(
        GateHistoricalAdmissionState admission, GateHistoricalExecutionState execution,
        GateCandidateCoverageState coverage, GateActivationState activation,
        GateCandidateRetirementState candidateRetirement)
    {
        GateCandidateOwnershipState? retirementOwnership = candidateRetirement switch
        {
            GateCandidateRetirementState.Abandoning => GateCandidateOwnershipState.Abandoning,
            GateCandidateRetirementState.LogicallyAbandoned => GateCandidateOwnershipState.LogicallyAbandoned,
            GateCandidateRetirementState.Reclaimed => GateCandidateOwnershipState.Reclaimed,
            GateCandidateRetirementState.Activated => GateCandidateOwnershipState.Activated,
            _ => null
        };
        if (retirementOwnership is not null) return retirementOwnership;
        GateCandidateOwnershipState? activationOwnership = activation switch
        {
            GateActivationState.Eligible or GateActivationState.CasRejected => GateCandidateOwnershipState.Available,
            GateActivationState.Activating => GateCandidateOwnershipState.Activating,
            GateActivationState.Activated => GateCandidateOwnershipState.Activated,
            _ => null
        };
        if (activationOwnership is not null) return activationOwnership;
        if (coverage == GateCandidateCoverageState.Complete) return GateCandidateOwnershipState.Available;
        if (coverage == GateCandidateCoverageState.Building) return GateCandidateOwnershipState.Materializing;
        if (coverage is GateCandidateCoverageState.Superseded or GateCandidateCoverageState.Cancelled or
            GateCandidateCoverageState.Faulted && execution is GateHistoricalExecutionState.Running or
            GateHistoricalExecutionState.Completed)
            return GateCandidateOwnershipState.Materializing;
        if (execution is GateHistoricalExecutionState.Queued or GateHistoricalExecutionState.Running ||
            admission == GateHistoricalAdmissionState.Admitted)
            return GateCandidateOwnershipState.Reserved;
        return null;
    }

    private static void ValidateCauses(GateApplyCommitState commit, GateLiveEpochState live,
        GateHistoricalAdmissionState admission, GateHistoricalExecutionState execution,
        GateCandidateCoverageState coverage, GateActivationState activation,
        GateCandidateRetirementState candidateRetirement, GatePreviousViewRetirementState previousRetirement,
        IReadOnlyList<GateLedgerCause> causes)
    {
        RequireCause(GateLedgerStage.Commit, commit switch
        {
            GateApplyCommitState.ValidationFailed => GateLedgerCauseKind.ValidationFailed,
            GateApplyCommitState.Conflict => GateLedgerCauseKind.RevisionConflict,
            GateApplyCommitState.CapacityExceeded => GateLedgerCauseKind.CapacityExceeded,
            GateApplyCommitState.CancelledBeforeCommit => GateLedgerCauseKind.Cancelled,
            GateApplyCommitState.FaultedBeforeCommit => GateLedgerCauseKind.Faulted,
            _ => null
        }, causes);
        RequireTerminalCause(GateLedgerStage.LiveEpoch, live, causes);
        RequireTerminalCause(GateLedgerStage.HistoricalAdmission, admission, causes);
        RequireTerminalCause(GateLedgerStage.HistoricalExecution, execution, causes);
        RequireTerminalCause(GateLedgerStage.CandidateCoverage, coverage, causes);
        RequireTerminalCause(GateLedgerStage.Activation, activation, causes);
        RequireTerminalCause(GateLedgerStage.CandidateRetirement, candidateRetirement, causes);
        RequireTerminalCause(GateLedgerStage.PreviousViewRetirement, previousRetirement, causes);
    }

    private static void RequireTerminalCause<T>(GateLedgerStage stage, T state, IReadOnlyList<GateLedgerCause> causes)
        where T : struct, Enum
    {
        string name = state.ToString(); GateLedgerCauseKind? expected = name switch
        {
            "Superseded" => GateLedgerCauseKind.Superseded,
            "Cancelled" => GateLedgerCauseKind.Cancelled,
            "Faulted" => GateLedgerCauseKind.Faulted,
            "Rejected" => GateLedgerCauseKind.Rejected,
            "RunSuperseded" => GateLedgerCauseKind.RunSuperseded,
            "Abandoned" => GateLedgerCauseKind.CandidateAbandoned,
            "DeferredByLeases" => GateLedgerCauseKind.DeferredByLeases,
            _ => null
        };
        if (stage == GateLedgerStage.CandidateRetirement && name is nameof(GateCandidateRetirementState.LogicallyAbandoned) or
            nameof(GateCandidateRetirementState.Reclaimed))
        {
            GateLedgerCause? retirement = causes.Where(x => x.Stage == stage).Cast<GateLedgerCause?>().SingleOrDefault();
            if (retirement is not null && retirement.Value.Kind != GateLedgerCauseKind.ReclaimBusy)
                throw new ArgumentException("Ledger cause is incompatible with candidate retirement.", nameof(causes));
            return;
        }
        if (stage == GateLedgerStage.Activation && name == nameof(GateActivationState.CasRejected))
        {
            GateLedgerCause? rejection = causes.Where(x => x.Stage == stage).Cast<GateLedgerCause?>().SingleOrDefault();
            GateLedgerCauseKind[] allowed = [GateLedgerCauseKind.TokenSuperseded,
                GateLedgerCauseKind.PredecessorMismatch, GateLedgerCauseKind.CandidateMismatch,
                GateLedgerCauseKind.CandidateNotAvailable, GateLedgerCauseKind.Disposed];
            if (rejection is null || !allowed.Contains(rejection.Value.Kind))
                throw new ArgumentException("CAS rejection requires its concrete typed cause.", nameof(causes));
            return;
        }
        RequireCause(stage, expected, causes);
    }
    private static void RequireCause(GateLedgerStage stage, GateLedgerCauseKind? expected,
        IReadOnlyList<GateLedgerCause> causes)
    {
        GateLedgerCause? actual = causes.Where(x => x.Stage == stage).Cast<GateLedgerCause?>().SingleOrDefault();
        if (expected is null ? actual is not null : actual is null || actual.Value.Kind != expected)
            throw new ArgumentException("Ledger cause is incompatible with its stage state.", nameof(causes));
    }
    private static void ValidateEnum<T>(T value, string name) where T : struct, Enum
    {
        if (!Enum.IsDefined(value) || Convert.ToInt32(value) == 0) throw new ArgumentOutOfRangeException(name);
    }
}
