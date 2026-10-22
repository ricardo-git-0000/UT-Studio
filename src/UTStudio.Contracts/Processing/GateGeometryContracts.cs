using System.Collections.ObjectModel;
using UTStudio.Contracts.Inspection;
using UTStudio.Domain.Processing;

namespace UTStudio.Contracts.Processing;

public enum GateGeometrySourceKind { Unknown, Committed, Preview }
public enum GateGeometrySourceValidationStatus { Unknown, Current, Superseded, Closed }
public enum GateGeometryEvaluationStatus
{
    Unknown, Completed, CapacityExceeded, NotAvailable, Stale, Cancelled, Closed, Faulted
}

public static class GateThresholdScale
{
    public static double ToSignalCounts(GateSignalDomain domain, double normalized)
    {
        if (!double.IsFinite(normalized) || normalized is < 0 or > 1)
            throw new ArgumentOutOfRangeException(nameof(normalized));
        return domain switch
        {
            GateSignalDomain.RawSigned => -32768d + 65535d * normalized,
            GateSignalDomain.Positive => 32767d * normalized,
            GateSignalDomain.NegativeMagnitude or GateSignalDomain.Absolute => 32768d * normalized,
            _ => throw new ArgumentOutOfRangeException(nameof(domain))
        };
    }
}

public readonly record struct GatePreviewIdentity
{
    public GatePreviewIdentity(Guid value, ulong generation)
    {
        if (value == Guid.Empty) throw new ArgumentException("A Preview identity is required.", nameof(value));
        if (generation == 0) throw new ArgumentOutOfRangeException(nameof(generation));
        Value = value; Generation = generation;
    }
    public Guid Value { get; }
    public ulong Generation { get; }
    public bool IsValid => Value != Guid.Empty && Generation != 0;
}

public sealed class GateGeometrySourceIdentity
{
    private GateGeometrySourceIdentity(GateGeometrySourceKind kind, GatePlanAuthorityKey authority,
        GatePlanRevision baseRevision, GatePlanRevision candidateRevision, GatePlanSignature signature, GateAuthorityGeneration authorityGeneration,
        GateAuthorityPublicationSequence publicationSequence, GatePreviewIdentity preview)
    {
        Kind = kind; Authority = authority; BaseRevision = baseRevision; CandidateRevision = candidateRevision; Signature = signature;
        AuthorityGeneration = authorityGeneration; PublicationSequence = publicationSequence; Preview = preview;
    }

    public GateGeometrySourceKind Kind { get; }
    public GatePlanAuthorityKey Authority { get; }
    public GatePlanRevision BaseRevision { get; }
    /// <summary>The compiled revision. For committed sources it equals BaseRevision; Preview remains non-authoritative.</summary>
    public GatePlanRevision CandidateRevision { get; }
    public GatePlanRevision Revision => CandidateRevision;
    public GatePlanSignature Signature { get; }
    public GateAuthorityGeneration AuthorityGeneration { get; }
    public GateAuthorityPublicationSequence PublicationSequence { get; }
    public GatePreviewIdentity Preview { get; }

    public static GateGeometrySourceIdentity Committed(GatePlanAuthoritySnapshot authority)
    {
        ArgumentNullException.ThrowIfNull(authority);
        return new(GateGeometrySourceKind.Committed, authority.Authority, authority.Committed.Revision,
            authority.Committed.Revision,
            authority.Committed.Signature, authority.Generation, authority.PublicationSequence, default);
    }

    public static GateGeometrySourceIdentity PreviewSource(GatePlanAuthorityKey authority,
        GatePlanRevision baseRevision, GatePlanRevision candidateRevision,
        GatePlanSignature previewSignature, GatePreviewIdentity preview)
    {
        if (!authority.IsValid || !baseRevision.IsValid || !candidateRevision.IsValid ||
            baseRevision.Value == uint.MaxValue || candidateRevision.Value != baseRevision.Value + 1 ||
            previewSignature is null || !preview.IsValid)
            throw new ArgumentException("A valid Preview source identity is required.");
        return new(GateGeometrySourceKind.Preview, authority, baseRevision, candidateRevision, previewSignature,
            default, default, preview);
    }
}

public readonly record struct GateGeometryEvaluationRequest
{
    public GateGeometryEvaluationRequest(GateGeometrySourceIdentity source, TraceIdentity traceIdentity,
        InspectionRevision expectedInspectionRevision, IGatePreviewSourceGuard? previewGuard = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!source.Authority.IsValid || !traceIdentity.IsValid || expectedInspectionRevision == InspectionRevision.Initial)
            throw new ArgumentException("A valid source, trace, and committed inspection revision are required.");
        if (source.Authority.InspectionId != traceIdentity.Stream.InspectionId)
            throw new ArgumentException("The source and trace must belong to the same inspection.");
        if ((source.Kind == GateGeometrySourceKind.Preview) != (previewGuard is not null))
            throw new ArgumentException("Preview geometry requires exactly one borrowed Preview guard.", nameof(previewGuard));
        Source = source; TraceIdentity = traceIdentity; ExpectedInspectionRevision = expectedInspectionRevision;
        PreviewGuard = previewGuard;
    }
    public GateGeometrySourceIdentity Source { get; }
    public TraceIdentity TraceIdentity { get; }
    public InspectionRevision ExpectedInspectionRevision { get; }
    public IGatePreviewSourceGuard? PreviewGuard { get; }
}

public interface IGateGeometrySourceGuard
{
    GateGeometrySourceValidationStatus Validate(GateGeometrySourceIdentity source);
}

public interface IGatePreviewSourceGuard
{
    GateGeometrySourceValidationStatus ValidatePreview(GateGeometrySourceIdentity source);
}

public interface IGateGeometryEvaluator
{
    ValueTask<GateGeometryEvaluationResult> EvaluateAsync(GateGeometryEvaluationRequest request,
        CompiledGatePlan compiledPlan, CancellationToken cancellationToken = default);
}

public sealed class GateAnchorSnapshot
{
    public GateAnchorSnapshot(GateId gateId, GateRevision gateRevision,
        GateTimeReferenceKind referenceKind, GateId dependencyGateId, GateResultStatus status,
        GateId? causalGateId, GateResultStatus? causalStatus, double? detectionTimeSeconds)
    {
        if (!gateId.IsValid || !gateRevision.IsValid || referenceKind != GateTimeReferenceKind.GateEvent ||
            !dependencyGateId.IsValid || dependencyGateId != gateId || !Enum.IsDefined(status) ||
            status == GateResultStatus.Unknown || causalGateId is { IsValid: false } ||
            causalStatus is GateResultStatus.Unknown || detectionTimeSeconds is { } time && !double.IsFinite(time))
            throw new ArgumentException("A valid exact gate anchor is required.");
        GateId = gateId; GateRevision = gateRevision; ReferenceKind = referenceKind;
        DependencyGateId = dependencyGateId; Status = status; CausalGateId = causalGateId;
        CausalStatus = causalStatus; DetectionTimeSeconds = detectionTimeSeconds;
    }
    public GateId GateId { get; }
    public GateRevision GateRevision { get; }
    public GateTimeReferenceKind ReferenceKind { get; }
    public GateId DependencyGateId { get; }
    public GateResultStatus Status { get; }
    public GateId? CausalGateId { get; }
    public GateResultStatus? CausalStatus { get; }
    public double? DetectionTimeSeconds { get; }
}

public sealed class GateGeometryItemSnapshot
{
    public GateGeometryItemSnapshot(GateId gateId, GateRevision gateRevision,
        GateSignalDomain signalDomain, GateResultStatus evaluationStatus, GateAnchorSnapshot? anchor,
        double? startSeconds, double? endSeconds, double? thresholdNormalized)
    {
        if (!gateId.IsValid || !gateRevision.IsValid || !Enum.IsDefined(signalDomain) ||
            signalDomain == GateSignalDomain.Unknown || !Enum.IsDefined(evaluationStatus) ||
            evaluationStatus == GateResultStatus.Unknown || (startSeconds.HasValue != endSeconds.HasValue) ||
            startSeconds is { } start && !double.IsFinite(start) ||
            endSeconds is { } end && (!double.IsFinite(end) || end <= startSeconds!.Value) ||
            thresholdNormalized is { } threshold && (!double.IsFinite(threshold) || threshold is < 0 or > 1))
            throw new ArgumentException("A valid immutable gate geometry item is required.");
        GateId = gateId; GateRevision = gateRevision; SignalDomain = signalDomain;
        EvaluationStatus = evaluationStatus; Anchor = anchor; StartSeconds = startSeconds;
        EndSeconds = endSeconds; ThresholdNormalized = thresholdNormalized;
    }
    public GateId GateId { get; }
    public GateRevision GateRevision { get; }
    public GateSignalDomain SignalDomain { get; }
    public GateResultStatus EvaluationStatus { get; }
    public GateAnchorSnapshot? Anchor { get; }
    public double? StartSeconds { get; }
    public double? EndSeconds { get; }
    public double? ThresholdNormalized { get; }
}

public sealed class GateGeometrySnapshot
{
    private readonly ReadOnlyCollection<GateGeometryItemSnapshot> _gates;
    public GateGeometrySnapshot(GateGeometrySourceIdentity source, TraceIdentity traceIdentity,
        InspectionRevision inspectionRevision, IEnumerable<GateGeometryItemSnapshot> gates)
    {
        ArgumentNullException.ThrowIfNull(source); ArgumentNullException.ThrowIfNull(gates);
        GateGeometryItemSnapshot[] captured = gates.ToArray();
        if (!traceIdentity.IsValid || source.Authority.InspectionId != traceIdentity.Stream.InspectionId ||
            inspectionRevision == InspectionRevision.Initial || captured.Length == 0 ||
            captured.Any(item => item is null || !item.GateId.IsValid || !item.GateRevision.IsValid ||
                !Enum.IsDefined(item.SignalDomain) || item.SignalDomain == GateSignalDomain.Unknown ||
                !Enum.IsDefined(item.EvaluationStatus) || item.EvaluationStatus == GateResultStatus.Unknown) ||
            captured.Select(item => item.GateId).Distinct().Count() != captured.Length)
            throw new ArgumentException("Valid immutable gate geometry is required.");
        Source = source; TraceIdentity = traceIdentity; InspectionRevision = inspectionRevision;
        _gates = Array.AsReadOnly(captured);
    }
    public GateGeometrySourceIdentity Source { get; }
    public TraceIdentity TraceIdentity { get; }
    public InspectionRevision InspectionRevision { get; }
    public IReadOnlyList<GateGeometryItemSnapshot> Gates => _gates;
}

public sealed class GateGeometryEvaluationResult
{
    public GateGeometryEvaluationResult(GateGeometryEvaluationStatus status, GateGeometryEvaluationRequest request,
        GateGeometrySnapshot? snapshot = null, string? errorCode = null)
    {
        if (!Enum.IsDefined(status) || status == GateGeometryEvaluationStatus.Unknown)
            throw new ArgumentOutOfRangeException(nameof(status));
        if ((status == GateGeometryEvaluationStatus.Completed) != (snapshot is not null))
            throw new ArgumentException("Only a completed result carries geometry.", nameof(snapshot));
        if (status == GateGeometryEvaluationStatus.Faulted && string.IsNullOrWhiteSpace(errorCode))
            throw new ArgumentException("A faulted result requires an error code.", nameof(errorCode));
        if (status != GateGeometryEvaluationStatus.Faulted && errorCode is not null)
            throw new ArgumentException("Only a faulted result carries an error code.", nameof(errorCode));
        Status = status; Request = request; Snapshot = snapshot; ErrorCode = errorCode;
    }
    public GateGeometryEvaluationStatus Status { get; }
    public GateGeometryEvaluationRequest Request { get; }
    public GateGeometrySnapshot? Snapshot { get; }
    public string? ErrorCode { get; }
}
