using UTStudio.Contracts.Processing;

namespace UTStudio.Presentation;

/// <summary>
/// Bounded owner of the single stable Preview checkpoint. Replacing the slot does not invalidate immutable
/// references already borrowed by the bounded geometry runner; their identity makes late results stale.
/// </summary>
internal sealed class GatePreviewWorkspace : IGatePreviewSourceGuard
{
    private readonly object _gate = new();
    private readonly Guid _identity = Guid.NewGuid();
    private ulong _generation;
    private GatePreviewArtifact? _stable;
    private GatePreviewArtifact? _candidate;

    internal GatePreviewIdentity ReserveIdentity()
    {
        lock (_gate)
        {
            if (_generation == ulong.MaxValue) throw new InvalidOperationException("Preview generation is exhausted.");
            _generation++;
            return new(_identity, _generation);
        }
    }

    internal GatePreviewArtifact Install(GatePlanPreviewCandidate candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        var source = GateGeometrySourceIdentity.PreviewSource(candidate.Draft.Authority,
            candidate.BaseRevision, candidate.CandidateRevision, candidate.Signature,
            candidate.PreviewIdentity);
        lock (_gate) return _candidate = new(candidate, source, this);
    }

    internal GatePreviewArtifact? Stable { get { lock (_gate) return _stable; } }
    internal GatePreviewArtifact? Candidate { get { lock (_gate) return _candidate; } }
    internal bool IsCurrentCandidate(GatePreviewArtifact artifact) { lock (_gate) return ReferenceEquals(_candidate, artifact); }
    internal bool Promote(GatePreviewArtifact artifact)
    {
        lock (_gate)
        {
            if (!ReferenceEquals(_candidate, artifact)) return false;
            _stable = artifact; _candidate = null; return true;
        }
    }
    internal void ClearCandidate() { lock (_gate) _candidate = null; }
    internal void Clear() { lock (_gate) { _stable = null; _candidate = null; } }
    public GateGeometrySourceValidationStatus ValidatePreview(GateGeometrySourceIdentity source)
    {
        ArgumentNullException.ThrowIfNull(source);
        lock (_gate)
        {
            GatePreviewArtifact? current = _candidate ?? _stable;
            return source.Kind == GateGeometrySourceKind.Preview && current is not null &&
                current.Source.Authority == source.Authority && current.Source.BaseRevision == source.BaseRevision &&
                current.Source.CandidateRevision == source.CandidateRevision && current.Source.Preview == source.Preview &&
                current.Source.Signature.Equals(source.Signature)
                ? GateGeometrySourceValidationStatus.Current : GateGeometrySourceValidationStatus.Superseded;
        }
    }
}

internal sealed record GatePreviewArtifact(GatePlanPreviewCandidate Candidate, GateGeometrySourceIdentity Source,
    IGatePreviewSourceGuard PreviewGuard)
{
    internal GatePreviewIdentity Identity => Candidate.PreviewIdentity;
    internal GatePlanDraft Checkpoint => Candidate.Draft;
    internal CompiledGatePlan CompiledPlan => Candidate.CompiledPlan;
}

internal sealed record GateCommittedArtifact(GateGeometrySourceIdentity Source, CompiledGatePlan CompiledPlan);

internal readonly record struct GateEditorApplyTicket(Guid Value, GatePreviewArtifact Artifact,
    ulong EditorGeneration)
{
    internal bool IsValid => Value != Guid.Empty;
}
