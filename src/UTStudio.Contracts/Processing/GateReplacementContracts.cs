using UTStudio.Domain.Acquisition;
using UTStudio.Domain.Inspection;
using UTStudio.Domain.Processing;

namespace UTStudio.Contracts.Processing;

public enum GateCandidateOwnershipState
{
    Unknown, Reserved, Materializing, Available, Activating, Activated, Abandoning,
    LogicallyAbandoned, Reclaimed
}
public enum GateCandidateActivationStatus
{
    Unknown, Activated, TokenSuperseded, RunSuperseded, PredecessorMismatch, CandidateMismatch,
    CandidateNotAvailable, AlreadyActivated, CandidateAbandoned, Disposed
}
public enum GateCandidateAbandonStatus
{
    Unknown, LogicallyAbandoned, AlreadyAbandoned, AlreadyActivated, CandidateNotAvailable, Disposed
}
public enum GateCandidateReclaimStatus
{ Unknown, Reclaimed, AlreadyReclaimed, Busy, AlreadyActivated, CandidateNotAvailable, Disposed }

public readonly record struct GateReplacementToken
{
    public GateReplacementToken(ulong value)
    {
        if (value == 0) throw new ArgumentOutOfRangeException(nameof(value));
        Value = value;
    }
    public ulong Value { get; }
    public bool IsValid => Value > 0;
}

public sealed class GateStoreOnlyCandidateDescriptor : IEquatable<GateStoreOnlyCandidateDescriptor>
{
    public GateStoreOnlyCandidateDescriptor(GateDerivedCoverageManifest manifest, DerivedViewId candidateViewId,
        GateReplacementToken replacementToken, GateReplacementGeneration generation,
        GateCandidateOwnershipState ownershipState)
    {
        Manifest = manifest ?? throw new ArgumentNullException(nameof(manifest));
        if (!candidateViewId.IsValid || !replacementToken.IsValid || !generation.IsValid ||
            !manifest.CapturedStoreRevision.IsValid)
            throw new ArgumentException("Valid candidate identity, token, and generation are required.");
        if (!Enum.IsDefined(ownershipState) || ownershipState == GateCandidateOwnershipState.Unknown)
            throw new ArgumentOutOfRangeException(nameof(ownershipState));
        CandidateViewId = candidateViewId; ReplacementToken = replacementToken;
        Generation = generation; OwnershipState = ownershipState;
    }
    public GateDerivedCoverageManifest Manifest { get; }
    public DerivedViewId CandidateViewId { get; }
    public GateReplacementToken ReplacementToken { get; }
    public GateReplacementGeneration Generation { get; }
    public GateCandidateOwnershipState OwnershipState { get; }
    public bool Equals(GateStoreOnlyCandidateDescriptor? other) => other is not null &&
        CandidateViewId == other.CandidateViewId && ReplacementToken == other.ReplacementToken &&
        Generation == other.Generation && OwnershipState == other.OwnershipState &&
        Manifest.Equals(other.Manifest);
    public override bool Equals(object? obj) => Equals(obj as GateStoreOnlyCandidateDescriptor);
    public override int GetHashCode()
    {
        HashCode hash = new(); hash.Add(CandidateViewId); hash.Add(ReplacementToken); hash.Add(Generation);
        hash.Add(OwnershipState); hash.Add(Manifest); return hash.ToHashCode();
    }
}

public sealed class GateCandidateActivationRequest
{
    public GateCandidateActivationRequest(GateStoreOnlyCandidateDescriptor candidate,
        ActiveDerivedViewDescriptor? expectedPredecessor, AcquisitionRunId expectedRunId,
        GateRunGeneration expectedRunGeneration)
    {
        Candidate = candidate ?? throw new ArgumentNullException(nameof(candidate));
        if (!expectedRunId.IsValid || !expectedRunGeneration.IsValid) throw new ArgumentException("Expected run identity is invalid.");
        if (candidate.Manifest.RunId != expectedRunId)
            throw new ArgumentException("Candidate and expected run identities are incompatible.");
        if (candidate.OwnershipState != GateCandidateOwnershipState.Available)
            throw new ArgumentException("Only an available candidate can be requested for activation.", nameof(candidate));
        if (expectedPredecessor is not null && expectedPredecessor.Manifest.InspectionId != candidate.Manifest.InspectionId)
            throw new ArgumentException("Predecessor and candidate inspections are incompatible.", nameof(expectedPredecessor));
        if (expectedPredecessor?.ViewId == candidate.CandidateViewId)
            throw new ArgumentException("Predecessor and candidate views must differ.", nameof(expectedPredecessor));
        ExpectedPredecessor = expectedPredecessor; ExpectedRunId = expectedRunId; ExpectedRunGeneration = expectedRunGeneration;
    }
    public GateStoreOnlyCandidateDescriptor Candidate { get; }
    public ActiveDerivedViewDescriptor? ExpectedPredecessor { get; }
    public AcquisitionRunId ExpectedRunId { get; }
    public GateRunGeneration ExpectedRunGeneration { get; }
}

public enum GateObservedPredecessorKind { Unknown, None, Present }

public sealed class GateReplacementCasObservedState
{
    public GateReplacementCasObservedState(GateApplyLedgerLifecycleState lifecycle, InspectionId inspectionId,
        AcquisitionRunId runId, GatePlanId planId, GatePlanRevision planRevision,
        GateRunGeneration runGeneration, GateReplacementToken? desiredToken,
        GateReplacementGeneration? desiredGeneration, GateObservedPredecessorKind predecessorKind,
        ActiveDerivedViewDescriptor? predecessor, GateStoreOnlyCandidateDescriptor? candidate,
        GateCandidateOwnershipState? ownership, bool hasActivationCapacity,
        ActiveDerivedViewDescriptor? activeView)
    {
        if (!Enum.IsDefined(lifecycle) || lifecycle == GateApplyLedgerLifecycleState.Unknown)
            throw new ArgumentOutOfRangeException(nameof(lifecycle));
        if (!inspectionId.IsValid || !runId.IsValid || !planId.IsValid || !planRevision.IsValid ||
            !runGeneration.IsValid || desiredToken is { IsValid: false } ||
            desiredGeneration is { IsValid: false } || desiredToken.HasValue != desiredGeneration.HasValue)
            throw new ArgumentException("Observed desired token and generation must be valid and present together.");
        if (!Enum.IsDefined(predecessorKind) || predecessorKind == GateObservedPredecessorKind.Unknown ||
            (predecessorKind == GateObservedPredecessorKind.None) != (predecessor is null))
            throw new ArgumentException("Observed predecessor presence must be explicit.", nameof(predecessor));
        if (ownership is { } material && (!Enum.IsDefined(material) || material == GateCandidateOwnershipState.Unknown))
            throw new ArgumentOutOfRangeException(nameof(ownership));
        if (candidate is null && ownership is not (null or GateCandidateOwnershipState.Reclaimed) ||
            candidate is not null && (ownership is null or GateCandidateOwnershipState.Reclaimed ||
                candidate.OwnershipState != ownership))
            throw new ArgumentException("Candidate presence and material ownership must agree.", nameof(ownership));
        if (candidate is not null && (candidate.Manifest.InspectionId != inspectionId ||
            candidate.Manifest.PlanId != planId || candidate.Manifest.PlanRevision != planRevision))
            throw new ArgumentException("Observed candidate coordinates contradict the atomic CAS snapshot.", nameof(candidate));
        if (predecessor is not null && predecessor.Manifest.InspectionId != inspectionId)
            throw new ArgumentException("Observed predecessor coordinates contradict the atomic CAS snapshot.", nameof(predecessor));
        if (activeView is not null && (activeView.Manifest.InspectionId != inspectionId ||
            activeView.Manifest.RunId != runId))
            throw new ArgumentException("Active-view coordinates contradict the atomic CAS snapshot.", nameof(activeView));
        if (lifecycle == GateApplyLedgerLifecycleState.Disposed && ownership is not
            (null or GateCandidateOwnershipState.Activated or GateCandidateOwnershipState.Reclaimed))
            throw new ArgumentException("Disposed lifecycle cannot retain nonterminal candidate ownership.", nameof(ownership));
        Lifecycle = lifecycle; InspectionId = inspectionId; RunId = runId; PlanId = planId; PlanRevision = planRevision;
        RunGeneration = runGeneration; DesiredToken = desiredToken;
        DesiredGeneration = desiredGeneration; PredecessorKind = predecessorKind; Predecessor = predecessor;
        Candidate = candidate; Ownership = ownership; HasActivationCapacity = hasActivationCapacity; ActiveView = activeView;
    }
    public GateApplyLedgerLifecycleState Lifecycle { get; }
    public InspectionId InspectionId { get; }
    public AcquisitionRunId RunId { get; }
    public GatePlanId PlanId { get; }
    public GatePlanRevision PlanRevision { get; }
    public GateRunGeneration RunGeneration { get; }
    public GateReplacementToken? DesiredToken { get; }
    public GateReplacementGeneration? DesiredGeneration { get; }
    public GateObservedPredecessorKind PredecessorKind { get; }
    public ActiveDerivedViewDescriptor? Predecessor { get; }
    public GateStoreOnlyCandidateDescriptor? Candidate { get; }
    public GateCandidateOwnershipState? Ownership { get; }
    public bool HasActivationCapacity { get; }
    public ActiveDerivedViewDescriptor? ActiveView { get; }
}

public sealed class GateCandidateActivationResult
{
    private GateCandidateActivationResult(GateCandidateActivationStatus status,
        GateStoreOnlyCandidateDescriptor requestedCandidate, GateStoreOnlyCandidateDescriptor? observedCandidate,
        ActiveDerivedViewDescriptor? activeView, GateObservedPredecessorKind observedPredecessorKind,
        ActiveDerivedViewDescriptor? observedPredecessor, ActiveDerivedViewDescriptor? expectedPredecessor)
    {
        Status = status; RequestedCandidate = requestedCandidate; ObservedCandidate = observedCandidate; ActiveView = activeView;
        ObservedPredecessorKind = observedPredecessorKind; ObservedPredecessor = observedPredecessor;
        ExpectedPredecessor = expectedPredecessor;
    }
    public GateCandidateActivationStatus Status { get; }
    public GateStoreOnlyCandidateDescriptor RequestedCandidate { get; }
    public GateStoreOnlyCandidateDescriptor? ObservedCandidate { get; }
    public ActiveDerivedViewDescriptor? ActiveView { get; }
    public GateObservedPredecessorKind ObservedPredecessorKind { get; }
    public ActiveDerivedViewDescriptor? ObservedPredecessor { get; }
    public ActiveDerivedViewDescriptor? ExpectedPredecessor { get; }
    public bool IsSuccess => Status is GateCandidateActivationStatus.Activated or
        GateCandidateActivationStatus.AlreadyActivated;

    public static GateCandidateActivationResult Classify(GateCandidateActivationRequest request,
        GateReplacementCasObservedState observed)
    {
        ArgumentNullException.ThrowIfNull(request); ArgumentNullException.ThrowIfNull(observed);
        GateCandidateActivationStatus status;
        if (observed.Lifecycle is GateApplyLedgerLifecycleState.Disposing or GateApplyLedgerLifecycleState.Disposed)
            status = GateCandidateActivationStatus.Disposed;
        else if (observed.RunId != request.ExpectedRunId || observed.RunGeneration != request.ExpectedRunGeneration)
            status = GateCandidateActivationStatus.RunSuperseded;
        else if (observed.DesiredToken != request.Candidate.ReplacementToken ||
                 observed.DesiredGeneration != request.Candidate.Generation)
            status = GateCandidateActivationStatus.TokenSuperseded;
        else if (observed.Ownership == GateCandidateOwnershipState.Activated &&
                 observed.Candidate is not null && SameCandidateDescriptor(request.Candidate, observed.Candidate) &&
                 IsRequestedActive(request.Candidate, observed.ActiveView))
            status = GateCandidateActivationStatus.AlreadyActivated;
        else if (!SamePredecessor(request.ExpectedPredecessor, observed.PredecessorKind, observed.Predecessor))
            status = GateCandidateActivationStatus.PredecessorMismatch;
        else if (observed.Ownership != GateCandidateOwnershipState.Reclaimed &&
                 (observed.Candidate is null || !SameCandidateDescriptor(request.Candidate, observed.Candidate)))
            status = GateCandidateActivationStatus.CandidateMismatch;
        else status = observed.Ownership switch
        {
            GateCandidateOwnershipState.Activated => GateCandidateActivationStatus.CandidateMismatch,
            GateCandidateOwnershipState.Abandoning or GateCandidateOwnershipState.LogicallyAbandoned or
                GateCandidateOwnershipState.Reclaimed => GateCandidateActivationStatus.CandidateAbandoned,
            GateCandidateOwnershipState.Reserved or GateCandidateOwnershipState.Materializing or
                GateCandidateOwnershipState.Activating => GateCandidateActivationStatus.CandidateNotAvailable,
            GateCandidateOwnershipState.Available when !observed.HasActivationCapacity =>
                GateCandidateActivationStatus.CandidateNotAvailable,
            GateCandidateOwnershipState.Available => GateCandidateActivationStatus.Activated,
            null => GateCandidateActivationStatus.CandidateMismatch,
            _ => GateCandidateActivationStatus.CandidateMismatch
        };
        ActiveDerivedViewDescriptor? active = status == GateCandidateActivationStatus.AlreadyActivated ?
            observed.ActiveView : null;
        return new(status, request.Candidate, observed.Candidate, active,
            observed.PredecessorKind, observed.Predecessor, request.ExpectedPredecessor);
    }
    private static bool SameCandidateDescriptor(GateStoreOnlyCandidateDescriptor left,
        GateStoreOnlyCandidateDescriptor right) => left.CandidateViewId == right.CandidateViewId &&
        left.ReplacementToken == right.ReplacementToken && left.Generation == right.Generation &&
        left.Manifest.Equals(right.Manifest);
    private static bool SamePredecessor(ActiveDerivedViewDescriptor? expected,
        GateObservedPredecessorKind kind, ActiveDerivedViewDescriptor? observed) =>
        expected is null ? kind == GateObservedPredecessorKind.None :
        kind == GateObservedPredecessorKind.Present && expected.Equals(observed);
    private static bool IsRequestedActive(GateStoreOnlyCandidateDescriptor requested,
        ActiveDerivedViewDescriptor? active) => active is not null && active.ViewId == requested.CandidateViewId &&
        active.Manifest.Equals(requested.Manifest);
}

public sealed class GateCandidateAbandonResult
{
    private GateCandidateAbandonResult(GateCandidateAbandonStatus status, GateStoreOnlyCandidateDescriptor candidate,
        GateApplyLedgerLifecycleState lifecycle)
    {
        Status = status; Candidate = candidate; Lifecycle = lifecycle;
    }
    public GateCandidateAbandonStatus Status { get; }
    public GateStoreOnlyCandidateDescriptor Candidate { get; }
    public GateApplyLedgerLifecycleState Lifecycle { get; }
    public static GateCandidateAbandonResult Create(GateCandidateAbandonStatus status,
        GateStoreOnlyCandidateDescriptor candidate,
        GateApplyLedgerLifecycleState lifecycle = GateApplyLedgerLifecycleState.Active)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        if (!Enum.IsDefined(lifecycle) || lifecycle == GateApplyLedgerLifecycleState.Unknown)
            throw new ArgumentOutOfRangeException(nameof(lifecycle));
        if (!Enum.IsDefined(status) || status == GateCandidateAbandonStatus.Unknown) throw new ArgumentOutOfRangeException(nameof(status));
        bool coherent = status switch
        {
            GateCandidateAbandonStatus.LogicallyAbandoned =>
                candidate.OwnershipState == GateCandidateOwnershipState.LogicallyAbandoned,
            GateCandidateAbandonStatus.AlreadyAbandoned => candidate.OwnershipState is
                GateCandidateOwnershipState.LogicallyAbandoned or GateCandidateOwnershipState.Reclaimed,
            GateCandidateAbandonStatus.AlreadyActivated => candidate.OwnershipState == GateCandidateOwnershipState.Activated,
            GateCandidateAbandonStatus.Disposed => lifecycle == GateApplyLedgerLifecycleState.Disposed &&
                candidate.OwnershipState is GateCandidateOwnershipState.Activated or GateCandidateOwnershipState.Reclaimed,
            GateCandidateAbandonStatus.CandidateNotAvailable => candidate.OwnershipState is
                GateCandidateOwnershipState.Reserved or GateCandidateOwnershipState.Materializing or
                GateCandidateOwnershipState.Activating or GateCandidateOwnershipState.Abandoning,
            _ => false
        };
        if (status != GateCandidateAbandonStatus.Disposed && lifecycle == GateApplyLedgerLifecycleState.Disposed)
            coherent = false;
        if (!coherent) throw new ArgumentException("The abandonment result contradicts candidate ownership.", nameof(candidate));
        return new(status, candidate, lifecycle);
    }
}

public sealed class GateCandidateReclaimResult
{
    private GateCandidateReclaimResult(GateCandidateReclaimStatus status, GateStoreOnlyCandidateDescriptor candidate,
        GateApplyLedgerLifecycleState lifecycle)
    {
        Status = status; Candidate = candidate; Lifecycle = lifecycle;
    }
    public GateCandidateReclaimStatus Status { get; }
    public GateStoreOnlyCandidateDescriptor Candidate { get; }
    public GateApplyLedgerLifecycleState Lifecycle { get; }
    public static GateCandidateReclaimResult Create(GateCandidateReclaimStatus status,
        GateStoreOnlyCandidateDescriptor candidate,
        GateApplyLedgerLifecycleState lifecycle = GateApplyLedgerLifecycleState.Active)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        if (!Enum.IsDefined(lifecycle) || lifecycle == GateApplyLedgerLifecycleState.Unknown)
            throw new ArgumentOutOfRangeException(nameof(lifecycle));
        if (!Enum.IsDefined(status) || status == GateCandidateReclaimStatus.Unknown) throw new ArgumentOutOfRangeException(nameof(status));
        bool coherent = status switch
        {
            GateCandidateReclaimStatus.Reclaimed or GateCandidateReclaimStatus.AlreadyReclaimed =>
                candidate.OwnershipState == GateCandidateOwnershipState.Reclaimed,
            GateCandidateReclaimStatus.Busy => candidate.OwnershipState == GateCandidateOwnershipState.LogicallyAbandoned,
            GateCandidateReclaimStatus.AlreadyActivated => candidate.OwnershipState == GateCandidateOwnershipState.Activated,
            GateCandidateReclaimStatus.CandidateNotAvailable => candidate.OwnershipState is
                GateCandidateOwnershipState.Reserved or GateCandidateOwnershipState.Materializing or
                GateCandidateOwnershipState.Available or GateCandidateOwnershipState.Activating or
                GateCandidateOwnershipState.Abandoning,
            GateCandidateReclaimStatus.Disposed => lifecycle == GateApplyLedgerLifecycleState.Disposed &&
                candidate.OwnershipState is GateCandidateOwnershipState.Activated or GateCandidateOwnershipState.Reclaimed,
            _ => false
        };
        if (status != GateCandidateReclaimStatus.Disposed && lifecycle == GateApplyLedgerLifecycleState.Disposed)
            coherent = false;
        if (!coherent) throw new ArgumentException("The reclaim result contradicts candidate ownership.", nameof(candidate));
        return new(status, candidate, lifecycle);
    }
}
