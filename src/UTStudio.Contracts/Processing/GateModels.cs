using System.Collections.ObjectModel;
using UTStudio.Domain.Processing;

namespace UTStudio.Contracts.Processing;

public sealed class GateTimeReference
{
    private GateTimeReference(GateTimeReferenceKind kind, GateId dependency)
    { Kind = kind; DependencyGateId = dependency; }

    public GateTimeReferenceKind Kind { get; }
    public GateId DependencyGateId { get; }
    public static GateTimeReference TraceOrigin() => new(GateTimeReferenceKind.TraceOrigin, default);
    public static GateTimeReference GateEvent(GateId dependency)
    {
        if (!dependency.IsValid) { throw new ArgumentException("A valid dependency gate identifier is required.", nameof(dependency)); }
        return new(GateTimeReferenceKind.GateEvent, dependency);
    }
}

public sealed class GateWindowDefinition
{
    public GateWindowDefinition(GateTimeReference startReference, double startOffsetSeconds, double durationSeconds)
    {
        ArgumentNullException.ThrowIfNull(startReference);
        StartReference = startReference;
        StartOffsetSeconds = startOffsetSeconds;
        DurationSeconds = durationSeconds;
    }
    public GateTimeReference StartReference { get; }
    public double StartOffsetSeconds { get; }
    public double DurationSeconds { get; }
}

public sealed class GateThreshold
{
    public GateThreshold(double normalizedValue)
    { NormalizedValue = normalizedValue; }
    public double NormalizedValue { get; }
}

public sealed class GateHysteresis
{
    public GateHysteresis(double lowNormalized, double highNormalized)
    { LowNormalized = lowNormalized; HighNormalized = highNormalized; }
    public double LowNormalized { get; }
    public double HighNormalized { get; }
}

public sealed class GateLocalExtremaParameters
{
    public GateLocalExtremaParameters(int neighborhoodRadiusSamples, double minimumProminenceCounts,
        int minimumDistanceSamples, GatePlateauPolicy plateauPolicy = GatePlateauPolicy.LowerCenter)
    {
        NeighborhoodRadiusSamples = neighborhoodRadiusSamples;
        MinimumProminenceCounts = minimumProminenceCounts;
        MinimumDistanceSamples = minimumDistanceSamples;
        PlateauPolicy = plateauPolicy;
    }
    public int NeighborhoodRadiusSamples { get; }
    public double MinimumProminenceCounts { get; }
    public int MinimumDistanceSamples { get; }
    public GatePlateauPolicy PlateauPolicy { get; }
}

public sealed class GateDetectorDefinition
{
    public GateDetectorDefinition(GateDetectorKind kind, GateThreshold? threshold = null,
        GateHysteresis? hysteresis = null, GateLocalExtremaParameters? localExtrema = null)
    { Kind = kind; Threshold = threshold; Hysteresis = hysteresis; LocalExtrema = localExtrema; }
    public GateDetectorKind Kind { get; }
    public GateThreshold? Threshold { get; }
    public GateHysteresis? Hysteresis { get; }
    public GateLocalExtremaParameters? LocalExtrema { get; }
}

public sealed class GateDefinition
{
    private readonly ReadOnlyCollection<GateMeasureKind> _measures;
    public GateDefinition(GateId id, GateRevision revision, GateWindowDefinition window,
        GateSignalDomain signalDomain, GateDetectorDefinition detector,
        IEnumerable<GateMeasureKind> requestedMeasures, GateAcceptedTraceQuality acceptedTraceQuality)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(detector);
        ArgumentNullException.ThrowIfNull(requestedMeasures);
        Id = id;
        Revision = revision;
        Window = window;
        SignalDomain = signalDomain;
        Detector = detector;
        _measures = Array.AsReadOnly(requestedMeasures.ToArray());
        AcceptedTraceQuality = acceptedTraceQuality;
    }
    public GateId Id { get; }
    public GateRevision Revision { get; }
    public GateWindowDefinition Window { get; }
    public GateSignalDomain SignalDomain { get; }
    public GateDetectorDefinition Detector { get; }
    public IReadOnlyList<GateMeasureKind> RequestedMeasures => _measures;
    public GateAcceptedTraceQuality AcceptedTraceQuality { get; }
}

public sealed class GatePlan
{
    private readonly ReadOnlyCollection<GateDefinition> _gates;
    public GatePlan(GatePlanId id, GatePlanRevision revision, GateSchemaVersion schemaVersion,
        GateAlgorithmVersion algorithmVersion, IEnumerable<GateDefinition> gates)
    {
        ArgumentNullException.ThrowIfNull(gates);
        Id = id; Revision = revision; SchemaVersion = schemaVersion; AlgorithmVersion = algorithmVersion;
        _gates = Array.AsReadOnly(gates.ToArray());
    }
    public GatePlanId Id { get; }
    public GatePlanRevision Revision { get; }
    public GateSchemaVersion SchemaVersion { get; }
    public GateAlgorithmVersion AlgorithmVersion { get; }
    public IReadOnlyList<GateDefinition> Gates => _gates;
}

public sealed class GateFutureResultDescriptor
{
    private readonly ReadOnlyCollection<GateMeasureKind> _measures;
    public GateFutureResultDescriptor(GatePlanId planId, GatePlanRevision planRevision,
        GateSchemaVersion schemaVersion, GateAlgorithmVersion algorithmVersion,
        GateId gateId, GateRevision gateRevision, GateSignalDomain signalDomain,
        IEnumerable<GateMeasureKind> measures, GateAcceptedTraceQuality acceptedTraceQuality,
        GateAmplitudeUnit amplitudeUnit, GateTimeBasis timeBasis, GateTofReferenceKind tofReference,
        GateId? tofReferenceGateId = null)
    {
        ArgumentNullException.ThrowIfNull(measures);
        if (!planId.IsValid || !planRevision.IsValid || !schemaVersion.IsValid || !algorithmVersion.IsValid ||
            !gateId.IsValid || !gateRevision.IsValid)
        { throw new ArgumentException("Valid plan and gate identities, revisions, and versions are required."); }
        if (!Enum.IsDefined(signalDomain) || signalDomain == GateSignalDomain.Unknown) { throw new ArgumentOutOfRangeException(nameof(signalDomain)); }
        if (!Enum.IsDefined(acceptedTraceQuality) || acceptedTraceQuality == GateAcceptedTraceQuality.Unknown) { throw new ArgumentOutOfRangeException(nameof(acceptedTraceQuality)); }
        if (!Enum.IsDefined(amplitudeUnit) || amplitudeUnit == GateAmplitudeUnit.Unknown) { throw new ArgumentOutOfRangeException(nameof(amplitudeUnit)); }
        if (!Enum.IsDefined(timeBasis) || timeBasis == GateTimeBasis.Unknown) { throw new ArgumentOutOfRangeException(nameof(timeBasis)); }
        if (!Enum.IsDefined(tofReference) || tofReference == GateTofReferenceKind.Unknown) { throw new ArgumentOutOfRangeException(nameof(tofReference)); }
        bool validTofReference = tofReference switch
        {
            GateTofReferenceKind.TraceOrigin => tofReferenceGateId is null,
            GateTofReferenceKind.DependencyGateEvent => tofReferenceGateId is { IsValid: true } causal && causal != gateId,
            _ => false
        };
        if (!validTofReference)
        { throw new ArgumentException("TOF reference and causal gate identifier are inconsistent.", nameof(tofReferenceGateId)); }
        GateMeasureKind[] captured = measures.ToArray();
        if (captured.Length == 0 || captured.Any(x => !Enum.IsDefined(x) || x == GateMeasureKind.Unknown) ||
            captured.Distinct().Count() != captured.Length)
        { throw new ArgumentException("Measures must be nonempty, known, and unique.", nameof(measures)); }
        PlanId = planId; PlanRevision = planRevision; SchemaVersion = schemaVersion; AlgorithmVersion = algorithmVersion;
        GateId = gateId; GateRevision = gateRevision; SignalDomain = signalDomain;
        AcceptedTraceQuality = acceptedTraceQuality; AmplitudeUnit = amplitudeUnit;
        TimeBasis = timeBasis; TofReference = tofReference; TofReferenceGateId = tofReferenceGateId;
        _measures = Array.AsReadOnly(captured);
    }
    public GatePlanId PlanId { get; }
    public GatePlanRevision PlanRevision { get; }
    public GateSchemaVersion SchemaVersion { get; }
    public GateAlgorithmVersion AlgorithmVersion { get; }
    public GateId GateId { get; }
    public GateRevision GateRevision { get; }
    public GateSignalDomain SignalDomain { get; }
    public IReadOnlyList<GateMeasureKind> Measures => _measures;
    public GateAcceptedTraceQuality AcceptedTraceQuality { get; }
    public GateAmplitudeUnit AmplitudeUnit { get; }
    public GateTimeBasis TimeBasis { get; }
    public GateTofReferenceKind TofReference { get; }
    public GateId? TofReferenceGateId { get; }
}
