using System.Collections.ObjectModel;
using UTStudio.Domain.Inspection;
using UTStudio.Domain.Processing;

namespace UTStudio.Contracts.Processing;

public enum GatePlanEditIntentKind { Unknown, Preview, Cancel, Apply }
public enum GatePlanEditOutcome
{
    Unknown, Previewed, Cancelled, Superseded, Closed, Accepted, ValidationFailed, Conflict,
    CapacityExceeded, RevisionExhausted, CancelledBeforeCommit, FaultedBeforeCommit
}
public enum GatePlanCapacityResource { Unknown, LiveEpochs, LedgerRevisions, LedgerBytes, DerivedStore }
public enum GatePlanEditorState
{
    Unknown, Committed, Editing, Previewed, ValidationFailed, Stale, Cancelled, Closed
}

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

public sealed class GatePlanAuthoritySnapshot
{
    public GatePlanAuthoritySnapshot(CommittedGatePlanSnapshot committed, GateAuthorityGeneration generation,
        GateAuthorityPublicationSequence publicationSequence = default)
    {
        Committed = committed ?? throw new ArgumentNullException(nameof(committed));
        if (!generation.IsValid) throw new ArgumentException("A valid authority generation is required.", nameof(generation));
        Generation = generation;
        PublicationSequence = publicationSequence.IsValid ? publicationSequence : new(generation.Value);
    }
    public GatePlanAuthorityKey Authority => Committed.AuthorityKey;
    public CommittedGatePlanSnapshot Committed { get; }
    public GateAuthorityGeneration Generation { get; }
    public GateAuthorityPublicationSequence PublicationSequence { get; }
}

public readonly record struct GateAuthorityPublicationSequence
{
    public GateAuthorityPublicationSequence(ulong value)
    {
        if (value == 0) throw new ArgumentOutOfRangeException(nameof(value));
        Value = value;
    }
    public ulong Value { get; }
    public bool IsValid => Value != 0;
}

/// <summary>
/// Instantaneous, resource-free observation of committed authority. It reserves nothing and cannot prevent later
/// changes. PublicationSequence defines its logical order; consumers must discard work after observing a newer one.
/// </summary>
public readonly record struct GatePlanAuthorityReadStamp
{
    public GatePlanAuthorityReadStamp(GatePlanAuthorityKey authority, GatePlanAuthoritySnapshot snapshot,
        GateAuthorityGeneration generation, GateAuthorityPublicationSequence publicationSequence)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (!authority.IsValid || snapshot.Authority != authority || !generation.IsValid ||
            snapshot.Generation != generation || !publicationSequence.IsValid ||
            snapshot.PublicationSequence != publicationSequence)
            throw new ArgumentException("The read stamp identity is inconsistent.");
        Authority = authority; Snapshot = snapshot; Generation = generation; PublicationSequence = publicationSequence;
    }
    public GatePlanAuthorityKey Authority { get; }
    public GatePlanAuthoritySnapshot Snapshot { get; }
    public GateAuthorityGeneration Generation { get; }
    public GateAuthorityPublicationSequence PublicationSequence { get; }
    public bool IsValid => Authority.IsValid && Snapshot is not null && Generation.IsValid && PublicationSequence.IsValid;
}

public enum GatePlanAuthorityReadStampStatus { Unknown, Acquired, NotFound, GenerationChanged, Closed }

public sealed class GatePlanAuthorityReadStampResult
{
    public GatePlanAuthorityReadStampResult(GatePlanAuthorityReadStampStatus status,
        GatePlanAuthoritySnapshot? current = null, GatePlanAuthorityReadStamp stamp = default)
    {
        if (!Enum.IsDefined(status) || status == GatePlanAuthorityReadStampStatus.Unknown)
            throw new ArgumentOutOfRangeException(nameof(status));
        if ((status == GatePlanAuthorityReadStampStatus.Acquired) != stamp.IsValid ||
            status == GatePlanAuthorityReadStampStatus.Acquired && current != stamp.Snapshot ||
            status is GatePlanAuthorityReadStampStatus.GenerationChanged && current is null ||
            status is GatePlanAuthorityReadStampStatus.NotFound or GatePlanAuthorityReadStampStatus.Closed && current is not null)
            throw new ArgumentException("Read-stamp result payload contradicts its status.");
        Status = status; Current = current; Stamp = stamp;
    }
    public GatePlanAuthorityReadStampStatus Status { get; }
    public GatePlanAuthoritySnapshot? Current { get; }
    public GatePlanAuthorityReadStamp Stamp { get; }
}

public enum GatePlanRecoveryInstallStatus
{
    Unknown, Installed, CapabilityDisposed, OwnerClosingOrDisposed, OwnerSealed,
    IdentityNotBootstrapped, RevisionNotIncreasing, GenerationExhausted
}

public sealed class GatePlanRecoveryInstallResult
{
    public GatePlanRecoveryInstallResult(GatePlanRecoveryInstallStatus status, GatePlanAuthoritySnapshot? installed = null)
    {
        if (!Enum.IsDefined(status) || status == GatePlanRecoveryInstallStatus.Unknown ||
            (status == GatePlanRecoveryInstallStatus.Installed) != (installed is not null))
            throw new ArgumentException("Recovery result payload contradicts its status.");
        Status = status; Installed = installed;
    }
    public GatePlanRecoveryInstallStatus Status { get; }
    public GatePlanAuthoritySnapshot? Installed { get; }
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
        IEnumerable<GateCompilationError>? draftErrors = null,
        GatePlanEditorState state = GatePlanEditorState.Unknown)
    {
        Committed = committed ?? throw new ArgumentNullException(nameof(committed));
        if (draft is not null && draft.Authority != committed.AuthorityKey)
            throw new ArgumentException("Draft and committed authority must match.", nameof(draft));
        GateCompilationError[] captured = CaptureErrors(draftErrors, nameof(draftErrors));
        if (draft is null && captured.Length != 0) throw new ArgumentException("Draft errors require a draft.", nameof(draftErrors));
        GatePlanEditorState resolved = state == GatePlanEditorState.Unknown ?
            draft is null ? GatePlanEditorState.Committed : captured.Length == 0 ?
                GatePlanEditorState.Editing : GatePlanEditorState.ValidationFailed : state;
        if (!Enum.IsDefined(resolved) || resolved == GatePlanEditorState.Unknown)
            throw new ArgumentOutOfRangeException(nameof(state));
        bool needsDraft = resolved is GatePlanEditorState.Editing or GatePlanEditorState.Previewed or
            GatePlanEditorState.ValidationFailed or GatePlanEditorState.Stale;
        if (needsDraft != (draft is not null) ||
            resolved == GatePlanEditorState.ValidationFailed != (captured.Length != 0))
            throw new ArgumentException("The editorial state contradicts draft or diagnostics.", nameof(state));
        Draft = draft; _draftErrors = Array.AsReadOnly(captured); State = resolved;
    }
    public CommittedGatePlanSnapshot Committed { get; }
    public GatePlanRevision CurrentRevision => Committed.Revision;
    public GatePlanDraft? Draft { get; }
    public IReadOnlyList<GateCompilationError> DraftErrors => _draftErrors;
    public GatePlanEditorState State { get; }
    internal static GateCompilationError[] CaptureErrors(IEnumerable<GateCompilationError>? errors, string name)
    {
        GateCompilationError[] captured = (errors ?? []).ToArray();
        if (captured.Any(x => x is null)) throw new ArgumentException("Errors cannot contain null elements.", name);
        return captured;
    }
}

public interface IGatePlanAuthorityReader
{
    GatePlanAuthoritySnapshot Read(GatePlanAuthorityKey authority);
    IObservable<GatePlanAuthoritySnapshot> Observe(GatePlanAuthorityKey authority);
    GatePlanAuthorityReadStampResult TryReadStamp(GatePlanAuthorityKey authority,
        GateAuthorityGeneration expectedGeneration);
}

public interface IGatePlanPreviewCompiler
{
    GatePlanCompilationResult Compile(GatePlanDraft draft);
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
    {
        RequireCurrentDraft(snapshot);
        if (snapshot.State != GatePlanEditorState.Previewed || snapshot.DraftErrors.Count != 0)
            throw new ArgumentException("Previewed requires an error-free Previewed snapshot.", nameof(snapshot));
        return new(GatePlanEditOutcome.Previewed, snapshot, snapshot.CurrentRevision);
    }
    public static GatePlanEditResult Cancelled(GatePlanEditorSnapshot snapshot)
    {
        RequireCommittedOnly(snapshot);
        if (snapshot.State != GatePlanEditorState.Cancelled)
            throw new ArgumentException("Cancelled requires a Cancelled snapshot.", nameof(snapshot));
        return new(GatePlanEditOutcome.Cancelled, snapshot, snapshot.CurrentRevision);
    }
    public static GatePlanEditResult Superseded(GatePlanEditorSnapshot snapshot, GatePlanRevision expectedRevision)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (!expectedRevision.IsValid) throw new ArgumentException("A valid expected revision is required.", nameof(expectedRevision));
        return new(GatePlanEditOutcome.Superseded, snapshot, expectedRevision);
    }
    public static GatePlanEditResult Closed(GatePlanEditorSnapshot snapshot, GatePlanRevision expectedRevision)
    {
        RequireCommittedOnly(snapshot);
        if (snapshot.State != GatePlanEditorState.Closed || !expectedRevision.IsValid)
            throw new ArgumentException("Closed requires a closed snapshot and valid expected revision.");
        return new(GatePlanEditOutcome.Closed, snapshot, expectedRevision);
    }
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
        RequireCurrentDraft(snapshot); GateCompilationError[] captured = GatePlanEditorSnapshot.CaptureErrors(validationErrors, nameof(validationErrors));
        if (captured.Length == 0 || snapshot.State != GatePlanEditorState.ValidationFailed ||
            !snapshot.DraftErrors.SequenceEqual(captured))
            throw new ArgumentException("Snapshot and result diagnostics must describe the same validation failure.", nameof(validationErrors));
        return new(GatePlanEditOutcome.ValidationFailed, snapshot, snapshot.CurrentRevision, validationErrors: captured);
    }
    public static GatePlanEditResult Conflict(GatePlanRevision expectedRevision, GatePlanEditorSnapshot currentSnapshot)
    {
        ArgumentNullException.ThrowIfNull(currentSnapshot);
        if (!expectedRevision.IsValid || expectedRevision == currentSnapshot.CurrentRevision ||
            currentSnapshot.State != GatePlanEditorState.Stale ||
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
