using System.Collections.ObjectModel;
using UTStudio.Domain.Inspection;
using UTStudio.Domain.Processing;

namespace UTStudio.Contracts.Processing;

public enum GatePlanEditIntentKind { Unknown, Preview, Cancel, Apply }
public enum GatePlanEditOutcome
{
    Unknown, Previewed, Cancelled, Accepted, ValidationFailed, Conflict, CapacityExceeded,
    RevisionExhausted, CancelledBeforeCommit, FaultedBeforeCommit
}
public enum GatePlanCapacityResource { Unknown, LiveEpochs, LedgerRevisions, LedgerBytes, DerivedStore }

public readonly record struct GatePlanAuthorityKey
{
    public GatePlanAuthorityKey(InspectionId inspectionId, GatePlanId planId)
    {
        if (!inspectionId.IsValid || !planId.IsValid) throw new ArgumentException("Valid authority identities are required.");
        InspectionId = inspectionId; PlanId = planId;
    }
    public InspectionId InspectionId { get; }
    public GatePlanId PlanId { get; }
    public bool IsValid => InspectionId.IsValid && PlanId.IsValid;
}

public sealed class CommittedGatePlanSnapshot : IEquatable<CommittedGatePlanSnapshot>
{
    public CommittedGatePlanSnapshot(InspectionId inspectionId, CompiledGatePlan compiledPlan)
    {
        if (!inspectionId.IsValid) throw new ArgumentException("A valid inspection is required.", nameof(inspectionId));
        CompiledPlan = compiledPlan ?? throw new ArgumentNullException(nameof(compiledPlan));
        if (!compiledPlan.Source.Id.IsValid || !compiledPlan.Source.Revision.IsValid)
            throw new ArgumentException("The compiled plan identity is invalid.", nameof(compiledPlan));
        InspectionId = inspectionId;
    }
    public InspectionId InspectionId { get; }
    public GatePlanAuthorityKey AuthorityKey => new(InspectionId, Plan.Id);
    public GatePlan Plan => CompiledPlan.Source;
    public GatePlanRevision Revision => Plan.Revision;
    public GatePlanSignature Signature => CompiledPlan.Signature;
    public CompiledGatePlan CompiledPlan { get; }
    public bool Equals(CommittedGatePlanSnapshot? other) => other is not null &&
        InspectionId == other.InspectionId && Plan.Id == other.Plan.Id && Revision == other.Revision && Signature.Equals(other.Signature);
    public override bool Equals(object? obj) => Equals(obj as CommittedGatePlanSnapshot);
    public override int GetHashCode() => HashCode.Combine(InspectionId, Plan.Id, Revision, Signature);
}

public sealed class GatePlanDraft
{
    private readonly ReadOnlyCollection<GateDefinition> _gates;
    public GatePlanDraft(GatePlanAuthorityKey authority, GatePlanRevision baseRevision,
        GateSchemaVersion schemaVersion, GateAlgorithmVersion algorithmVersion, IEnumerable<GateDefinition> gates)
    {
        ArgumentNullException.ThrowIfNull(gates); GateDefinition[] captured = gates.ToArray();
        if (!authority.IsValid || !baseRevision.IsValid || !schemaVersion.IsValid || !algorithmVersion.IsValid ||
            captured.Length == 0 || captured.Any(x => x is null)) throw new ArgumentException("The draft is invalid.");
        Authority = authority; BaseRevision = baseRevision; SchemaVersion = schemaVersion;
        AlgorithmVersion = algorithmVersion; _gates = Array.AsReadOnly(captured);
    }
    public GatePlanAuthorityKey Authority { get; }
    public GatePlanRevision BaseRevision { get; }
    public GateSchemaVersion SchemaVersion { get; }
    public GateAlgorithmVersion AlgorithmVersion { get; }
    public IReadOnlyList<GateDefinition> Gates => _gates;
}

public sealed class GatePlanEditIntent
{
    public GatePlanEditIntent(GatePlanEditIntentKind kind, GatePlanAuthorityKey authority,
        GatePlanRevision expectedRevision, GatePlanDraft? draft = null)
    {
        if (!Enum.IsDefined(kind) || kind == GatePlanEditIntentKind.Unknown) throw new ArgumentOutOfRangeException(nameof(kind));
        if (!authority.IsValid || !expectedRevision.IsValid) throw new ArgumentException("Valid authority and revision are required.");
        bool needsDraft = kind is GatePlanEditIntentKind.Preview or GatePlanEditIntentKind.Apply;
        if (needsDraft != (draft is not null) || draft is not null &&
            (draft.Authority != authority || draft.BaseRevision != expectedRevision))
            throw new ArgumentException("The intent draft is inconsistent with its authority and expected revision.", nameof(draft));
        Kind = kind; Authority = authority; ExpectedRevision = expectedRevision; Draft = draft;
    }
    public GatePlanEditIntentKind Kind { get; }
    public GatePlanAuthorityKey Authority { get; }
    public GatePlanRevision ExpectedRevision { get; }
    public GatePlanDraft? Draft { get; }
}

public sealed class GatePlanEditorSnapshot
{
    private readonly ReadOnlyCollection<GateCompilationError> _draftErrors;
    public GatePlanEditorSnapshot(CommittedGatePlanSnapshot committed, GatePlanDraft? draft = null,
        IEnumerable<GateCompilationError>? draftErrors = null)
    {
        Committed = committed ?? throw new ArgumentNullException(nameof(committed));
        if (draft is not null && draft.Authority != committed.AuthorityKey)
            throw new ArgumentException("Draft and committed authority must match.", nameof(draft));
        GateCompilationError[] captured = CaptureErrors(draftErrors, nameof(draftErrors));
        if (draft is null && captured.Length != 0) throw new ArgumentException("Draft errors require a draft.", nameof(draftErrors));
        Draft = draft; _draftErrors = Array.AsReadOnly(captured);
    }
    public CommittedGatePlanSnapshot Committed { get; }
    public GatePlanRevision CurrentRevision => Committed.Revision;
    public GatePlanDraft? Draft { get; }
    public IReadOnlyList<GateCompilationError> DraftErrors => _draftErrors;
    internal static GateCompilationError[] CaptureErrors(IEnumerable<GateCompilationError>? errors, string name)
    {
        GateCompilationError[] captured = (errors ?? []).ToArray();
        if (captured.Any(x => x is null)) throw new ArgumentException("Errors cannot contain null elements.", name);
        return captured;
    }
}

public sealed class GatePlanEditResult
{
    private readonly ReadOnlyCollection<GateCompilationError> _validationErrors;
    private GatePlanEditResult(GatePlanEditOutcome outcome, GatePlanEditorSnapshot snapshot,
        GatePlanRevision expectedRevision, GatePlanRevision? resultingRevision = null,
        IEnumerable<GateCompilationError>? validationErrors = null, GatePlanCapacityResource? capacityResource = null)
    {
        Outcome = outcome; Snapshot = snapshot; ExpectedRevision = expectedRevision; ResultingRevision = resultingRevision;
        CapacityResource = capacityResource;
        _validationErrors = Array.AsReadOnly(GatePlanEditorSnapshot.CaptureErrors(validationErrors, nameof(validationErrors)));
    }
    public GatePlanEditOutcome Outcome { get; }
    public GatePlanEditorSnapshot Snapshot { get; }
    public GatePlanRevision ExpectedRevision { get; }
    public GatePlanRevision CurrentRevision => Snapshot.CurrentRevision;
    public GatePlanRevision? ResultingRevision { get; }
    public IReadOnlyList<GateCompilationError> ValidationErrors => _validationErrors;
    public GatePlanCapacityResource? CapacityResource { get; }

    public static GatePlanEditResult Previewed(GatePlanEditorSnapshot snapshot)
    { RequireCurrentDraft(snapshot); return new(GatePlanEditOutcome.Previewed, snapshot, snapshot.CurrentRevision); }
    public static GatePlanEditResult Cancelled(GatePlanEditorSnapshot snapshot)
    { RequireCommittedOnly(snapshot); return new(GatePlanEditOutcome.Cancelled, snapshot, snapshot.CurrentRevision); }
    public static GatePlanEditResult Accepted(GatePlanEditorSnapshot previousSnapshot,
        GatePlanEditorSnapshot resultingSnapshot)
    {
        RequireValidCurrentDraft(previousSnapshot); RequireCommittedOnly(resultingSnapshot);
        GatePlanRevision expectedRevision = previousSnapshot.CurrentRevision;
        if (previousSnapshot.Committed.AuthorityKey != resultingSnapshot.Committed.AuthorityKey ||
            expectedRevision.Value == uint.MaxValue || resultingSnapshot.CurrentRevision.Value != expectedRevision.Value + 1)
            throw new ArgumentException("Accepted must publish exactly the next committed revision.", nameof(resultingSnapshot));
        return new(GatePlanEditOutcome.Accepted, resultingSnapshot, expectedRevision, resultingSnapshot.CurrentRevision);
    }
    public static GatePlanEditResult ValidationFailed(GatePlanEditorSnapshot snapshot,
        IEnumerable<GateCompilationError> validationErrors)
    {
        RequireValidCurrentDraft(snapshot); GateCompilationError[] captured = GatePlanEditorSnapshot.CaptureErrors(validationErrors, nameof(validationErrors));
        if (captured.Length == 0) throw new ArgumentException("Validation failure requires errors.", nameof(validationErrors));
        return new(GatePlanEditOutcome.ValidationFailed, snapshot, snapshot.CurrentRevision, validationErrors: captured);
    }
    public static GatePlanEditResult Conflict(GatePlanRevision expectedRevision, GatePlanEditorSnapshot currentSnapshot)
    {
        ArgumentNullException.ThrowIfNull(currentSnapshot);
        if (!expectedRevision.IsValid || expectedRevision == currentSnapshot.CurrentRevision ||
            currentSnapshot.Draft is not { } draft || draft.BaseRevision != expectedRevision)
            throw new ArgumentException("Conflict requires distinct expected and current revisions and a matching stale draft.");
        return new(GatePlanEditOutcome.Conflict, currentSnapshot, expectedRevision);
    }
    public static GatePlanEditResult CapacityExceeded(GatePlanEditorSnapshot snapshot, GatePlanCapacityResource resource)
    {
        RequireValidCurrentDraft(snapshot);
        if (!Enum.IsDefined(resource) || resource == GatePlanCapacityResource.Unknown) throw new ArgumentOutOfRangeException(nameof(resource));
        return new(GatePlanEditOutcome.CapacityExceeded, snapshot, snapshot.CurrentRevision, capacityResource: resource);
    }
    public static GatePlanEditResult RevisionExhausted(GatePlanEditorSnapshot snapshot)
    {
        RequireValidCurrentDraft(snapshot);
        if (snapshot.CurrentRevision.Value != uint.MaxValue) throw new ArgumentException("Revision exhaustion requires the maximum current revision.", nameof(snapshot));
        return new(GatePlanEditOutcome.RevisionExhausted, snapshot, snapshot.CurrentRevision);
    }
    public static GatePlanEditResult CancelledBeforeCommit(GatePlanEditorSnapshot snapshot)
    { RequireValidCurrentDraft(snapshot); return new(GatePlanEditOutcome.CancelledBeforeCommit, snapshot, snapshot.CurrentRevision); }
    public static GatePlanEditResult FaultedBeforeCommit(GatePlanEditorSnapshot snapshot)
    { RequireValidCurrentDraft(snapshot); return new(GatePlanEditOutcome.FaultedBeforeCommit, snapshot, snapshot.CurrentRevision); }

    private static void RequireCurrentDraft(GatePlanEditorSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (snapshot.Draft is null || snapshot.Draft.BaseRevision != snapshot.CurrentRevision)
            throw new ArgumentException("A current draft is required.", nameof(snapshot));
    }
    private static void RequireCommittedOnly(GatePlanEditorSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (snapshot.Draft is not null || snapshot.DraftErrors.Count != 0)
            throw new ArgumentException("A committed-only snapshot is required.", nameof(snapshot));
    }
    private static void RequireValidCurrentDraft(GatePlanEditorSnapshot snapshot)
    {
        RequireCurrentDraft(snapshot);
        if (snapshot.DraftErrors.Count != 0)
            throw new ArgumentException("Apply outcomes require a draft without preview diagnostics.", nameof(snapshot));
    }
}
