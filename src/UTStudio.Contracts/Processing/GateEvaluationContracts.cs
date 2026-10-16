using System.Collections.ObjectModel;
using UTStudio.Contracts.Inspection;
using UTStudio.Domain.Processing;

namespace UTStudio.Contracts.Processing;

public sealed class ConfirmedGateTraceDescriptor
{
    public ConfirmedGateTraceDescriptor(TraceDescriptor descriptor, TraceIdentity confirmedIdentity,
        InspectionRevision sourceInspectionRevision, UtTraceQuality effectiveQuality)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        if (!confirmedIdentity.IsValid || descriptor.Identity != confirmedIdentity)
        { throw new ArgumentException("The confirmed identity must be valid and match the trace descriptor.", nameof(confirmedIdentity)); }
        if (sourceInspectionRevision == InspectionRevision.Initial)
        { throw new ArgumentException("A confirmed trace requires a non-initial source revision.", nameof(sourceInspectionRevision)); }
        if (effectiveQuality is not (UtTraceQuality.Good or UtTraceQuality.Degraded or UtTraceQuality.Invalid))
        { throw new ArgumentOutOfRangeException(nameof(effectiveQuality)); }
        if (descriptor.TraceQuality != effectiveQuality)
        { throw new ArgumentException("Effective quality must match the original descriptor quality.", nameof(effectiveQuality)); }
        if (descriptor.SampleEncoding != UtSampleEncoding.SignedInt16)
        { throw new ArgumentException("Gate evaluation requires confirmed SignedInt16 samples.", nameof(descriptor)); }
        Descriptor = descriptor;
        ConfirmedIdentity = confirmedIdentity;
        SourceInspectionRevision = sourceInspectionRevision;
        EffectiveQuality = effectiveQuality;
    }

    public TraceDescriptor Descriptor { get; }
    public TraceIdentity ConfirmedIdentity { get; }
    public InspectionRevision SourceInspectionRevision { get; }
    public UtTraceQuality EffectiveQuality { get; }
}

public sealed class GateEvaluationError
{
    public GateEvaluationError(GateEvaluationErrorCode code, string message)
    {
        if (!Enum.IsDefined(code) || code == GateEvaluationErrorCode.Unknown) { throw new ArgumentOutOfRangeException(nameof(code)); }
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        Code = code; Message = message;
    }
    public GateEvaluationErrorCode Code { get; }
    public string Message { get; }
}

public sealed class GateDetectorMetrics : IEquatable<GateDetectorMetrics>
{
    internal GateDetectorMetrics(double? prominenceCounts, double? thresholdMarginCounts)
    {
        ProminenceCounts = Canonical(prominenceCounts);
        ThresholdMarginCounts = Canonical(thresholdMarginCounts);
    }
    public double? ProminenceCounts { get; }
    public double? ThresholdMarginCounts { get; }
    public bool Equals(GateDetectorMetrics? other) => other is not null &&
        Bits(ProminenceCounts) == Bits(other.ProminenceCounts) && Bits(ThresholdMarginCounts) == Bits(other.ThresholdMarginCounts);
    public override bool Equals(object? obj) => Equals(obj as GateDetectorMetrics);
    public override int GetHashCode() => HashCode.Combine(Bits(ProminenceCounts), Bits(ThresholdMarginCounts));
    private static double? Canonical(double? value) => value == 0d ? 0d : value;
    private static long? Bits(double? value) => value.HasValue ? BitConverter.DoubleToInt64Bits(value.Value) : null;
}

public sealed class GateEvaluationEntry : IEquatable<GateEvaluationEntry>
{
    internal GateEvaluationEntry(TraceIdentity traceIdentity, InspectionRevision sourceInspectionRevision,
        GatePlanId planId, GatePlanRevision planRevision, GatePlanSignature planSignature,
        GateSchemaVersion schemaVersion, GateAlgorithmVersion algorithmVersion,
        GateId gateId, GateRevision gateRevision, GateResultStatus status,
        GateDetectionEventKind eventKind, int? sampleIndex, double? fractionalSamplePosition,
        double? detectionTimeSeconds, double? amplitudeCounts, short? signedRfAmplitudeCounts,
        double? timeOfFlightSeconds, double? offsetWithinWindowSeconds, GateResultQuality quality,
        GateSignalDomain signalDomain, UtTraceProvenance provenance, GateId? causalGateId,
        GateResultStatus? causalStatus, GateDiagnosticCode diagnosticCode, GateDetectorMetrics metrics)
    {
        TraceIdentity = traceIdentity; SourceInspectionRevision = sourceInspectionRevision;
        PlanId = planId; PlanRevision = planRevision; PlanSignature = planSignature;
        SchemaVersion = schemaVersion; AlgorithmVersion = algorithmVersion;
        GateId = gateId; GateRevision = gateRevision; Status = status; EventKind = eventKind;
        SampleIndex = sampleIndex; FractionalSamplePosition = Canonical(fractionalSamplePosition);
        DetectionTimeSeconds = Canonical(detectionTimeSeconds); AmplitudeCounts = Canonical(amplitudeCounts);
        SignedRfAmplitudeCounts = signedRfAmplitudeCounts; TimeOfFlightSeconds = Canonical(timeOfFlightSeconds);
        OffsetWithinWindowSeconds = Canonical(offsetWithinWindowSeconds); Quality = quality;
        SignalDomain = signalDomain; Provenance = provenance; CausalGateId = causalGateId; CausalStatus = causalStatus;
        DiagnosticCode = diagnosticCode; Metrics = metrics;
    }

    public TraceIdentity TraceIdentity { get; }
    public InspectionRevision SourceInspectionRevision { get; }
    public GatePlanId PlanId { get; }
    public GatePlanRevision PlanRevision { get; }
    public GatePlanSignature PlanSignature { get; }
    public GateSchemaVersion SchemaVersion { get; }
    public GateAlgorithmVersion AlgorithmVersion { get; }
    public GateId GateId { get; }
    public GateRevision GateRevision { get; }
    public GateResultStatus Status { get; }
    public GateDetectionEventKind EventKind { get; }
    public int? SampleIndex { get; }
    public double? FractionalSamplePosition { get; }
    public double? DetectionTimeSeconds { get; }
    public double? AmplitudeCounts { get; }
    public GateAmplitudeUnit AmplitudeUnit => GateAmplitudeUnit.DigitalCounts;
    public short? SignedRfAmplitudeCounts { get; }
    public double? TimeOfFlightSeconds { get; }
    public double? OffsetWithinWindowSeconds { get; }
    public GateResultQuality Quality { get; }
    public GateSignalDomain SignalDomain { get; }
    public UtTraceProvenance Provenance { get; }
    public GateId? CausalGateId { get; }
    public GateResultStatus? CausalStatus { get; }
    public GateDiagnosticCode DiagnosticCode { get; }
    public GateDetectorMetrics Metrics { get; }

    public bool Equals(GateEvaluationEntry? other) => other is not null &&
        TraceIdentity == other.TraceIdentity && SourceInspectionRevision == other.SourceInspectionRevision &&
        PlanId == other.PlanId && PlanRevision == other.PlanRevision && PlanSignature.Equals(other.PlanSignature) &&
        SchemaVersion == other.SchemaVersion && AlgorithmVersion == other.AlgorithmVersion &&
        GateId == other.GateId && GateRevision == other.GateRevision && Status == other.Status &&
        EventKind == other.EventKind && SampleIndex == other.SampleIndex && Bits(FractionalSamplePosition) == Bits(other.FractionalSamplePosition) &&
        Bits(DetectionTimeSeconds) == Bits(other.DetectionTimeSeconds) && Bits(AmplitudeCounts) == Bits(other.AmplitudeCounts) &&
        SignedRfAmplitudeCounts == other.SignedRfAmplitudeCounts && Bits(TimeOfFlightSeconds) == Bits(other.TimeOfFlightSeconds) &&
        Bits(OffsetWithinWindowSeconds) == Bits(other.OffsetWithinWindowSeconds) && Quality == other.Quality &&
        SignalDomain == other.SignalDomain && Provenance == other.Provenance && CausalGateId == other.CausalGateId &&
        CausalStatus == other.CausalStatus && DiagnosticCode == other.DiagnosticCode && Metrics.Equals(other.Metrics);
    public override bool Equals(object? obj) => Equals(obj as GateEvaluationEntry);
    public override int GetHashCode() => HashCode.Combine(GateId, GateRevision, Status, SampleIndex);
    private static double? Canonical(double? value) => value == 0d ? 0d : value;
    private static long? Bits(double? value) => value.HasValue ? BitConverter.DoubleToInt64Bits(value.Value) : null;
}

public sealed class GateEvaluationSignature : IEquatable<GateEvaluationSignature>
{
    private readonly byte[] _bytes;
    internal GateEvaluationSignature(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length != 32) { throw new ArgumentException("An evaluation signature must contain 32 bytes.", nameof(bytes)); }
        _bytes = bytes.ToArray();
    }
    public ReadOnlyMemory<byte> Bytes => _bytes.ToArray();
    public string Hex => Convert.ToHexString(_bytes);
    public bool Equals(GateEvaluationSignature? other) => other is not null && _bytes.AsSpan().SequenceEqual(other._bytes);
    public override bool Equals(object? obj) => Equals(obj as GateEvaluationSignature);
    public override int GetHashCode() => BitConverter.ToInt32(_bytes, 0);
    public override string ToString() => Hex;
}

public sealed class GateEvaluationBatch : IEquatable<GateEvaluationBatch>
{
    private readonly ReadOnlyCollection<GateEvaluationEntry> _entries;
    internal GateEvaluationBatch(ConfirmedGateTraceDescriptor trace, GateEvaluationSignature signature,
        IEnumerable<GateEvaluationEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(trace); ArgumentNullException.ThrowIfNull(signature);
        ArgumentNullException.ThrowIfNull(entries); Trace = trace; ReproducibilitySignature = signature;
        _entries = Array.AsReadOnly(entries.ToArray());
    }
    public const ushort ProcessingContractVersion = 3;
    public ConfirmedGateTraceDescriptor Trace { get; }
    public GateEvaluationSignature ReproducibilitySignature { get; }
    public IReadOnlyList<GateEvaluationEntry> Entries => _entries;
    public bool Equals(GateEvaluationBatch? other) => other is not null &&
        ReproducibilitySignature.Equals(other.ReproducibilitySignature) && _entries.SequenceEqual(other._entries);
    public override bool Equals(object? obj) => Equals(obj as GateEvaluationBatch);
    public override int GetHashCode() => _entries.Count;
}

public sealed class GateEvaluationResult
{
    private readonly ReadOnlyCollection<GateEvaluationError> _secondaryErrors;
    private GateEvaluationResult(GateEvaluationStatus status, GateEvaluationBatch? batch,
        GateEvaluationError? primaryError, IEnumerable<GateEvaluationError>? secondaryErrors = null)
    {
        Status = status; Batch = batch; PrimaryError = primaryError;
        _secondaryErrors = Array.AsReadOnly((secondaryErrors ?? []).ToArray());
    }
    public GateEvaluationStatus Status { get; }
    public GateEvaluationBatch? Batch { get; }
    public GateEvaluationError? PrimaryError { get; }
    public GateEvaluationError? Error => PrimaryError;
    public IReadOnlyList<GateEvaluationError> SecondaryErrors => _secondaryErrors;
    public bool IsCompleted => Status == GateEvaluationStatus.Completed && Batch is not null;
    internal static GateEvaluationResult Completed(GateEvaluationBatch batch) => new(GateEvaluationStatus.Completed, batch, null);
    internal static GateEvaluationResult Cancelled(GateEvaluationBatch batch) => new(GateEvaluationStatus.Cancelled, batch, null);
    internal static GateEvaluationResult Failed(GateEvaluationErrorCode code, string message) =>
        new(GateEvaluationStatus.Failed, null, new GateEvaluationError(code, message));
    internal static GateEvaluationResult Failed(GateEvaluationError primary,
        IEnumerable<GateEvaluationError> secondaryErrors) =>
        new(GateEvaluationStatus.Failed, null, primary, secondaryErrors);
}

public sealed class GateScratchDiagnostics
{
    public GateScratchDiagnostics(long rentCount, long returnCount, long rejectionCount, int activeRentals,
        int peakActiveRentals, long rentedElements, long returnedElements, long retainedCapacity)
    {
        RentCount = rentCount; ReturnCount = returnCount; RejectionCount = rejectionCount;
        ActiveRentals = activeRentals; PeakActiveRentals = peakActiveRentals;
        RentedElements = rentedElements; ReturnedElements = returnedElements;
        RetainedCapacity = retainedCapacity;
    }
    public long RentCount { get; }
    public long ReturnCount { get; }
    public long RejectionCount { get; }
    public long RejectedRentals => RejectionCount;
    public int ActiveRentals { get; }
    public int PeakActiveRentals { get; }
    public long RentedElements { get; }
    public long ReturnedElements { get; }
    public long RetainedCapacity { get; }
}
