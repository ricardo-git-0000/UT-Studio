using UTStudio.Contracts.Processing;

namespace UTStudio.Presentation;

/// <summary>
/// Bounded owner of the single stable Preview checkpoint. Replacing the slot does not invalidate immutable
/// references already borrowed by the bounded geometry runner; their identity makes late results stale.
/// </summary>
internal sealed class GatePreviewWorkspace
{
    private readonly Guid _identity = Guid.NewGuid();
    private ulong _generation;
    private GatePreviewArtifact? _stable;
    private GatePreviewArtifact? _candidate;

    internal GatePreviewArtifact Install(GatePlanDraft draft, CompiledGatePlan compiledPlan)
    {
        ArgumentNullException.ThrowIfNull(draft); ArgumentNullException.ThrowIfNull(compiledPlan);
        if (_generation == ulong.MaxValue) throw new InvalidOperationException("Preview generation is exhausted.");
        _generation++;
        var identity = new GatePreviewIdentity(_identity, _generation);
        var source = GateGeometrySourceIdentity.PreviewSource(draft.Authority, draft.BaseRevision,
            compiledPlan.Signature, identity);
        return _candidate = new(identity, source, draft, compiledPlan);
    }

    internal GatePreviewArtifact? Stable => _stable;
    internal GatePreviewArtifact? Candidate => _candidate;
    internal bool IsCurrentCandidate(GatePreviewArtifact artifact) => ReferenceEquals(_candidate, artifact);
    internal bool Promote(GatePreviewArtifact artifact)
    {
        if (!ReferenceEquals(_candidate, artifact)) return false;
        _stable = artifact; _candidate = null; return true;
    }
    internal void ClearCandidate() => _candidate = null;
    internal void Clear() { _stable = null; _candidate = null; }
}

internal sealed record GatePreviewArtifact(GatePreviewIdentity Identity, GateGeometrySourceIdentity Source,
    GatePlanDraft Checkpoint, CompiledGatePlan CompiledPlan);
