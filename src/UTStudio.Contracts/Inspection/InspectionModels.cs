using UTStudio.Domain.Acquisition;
using UTStudio.Domain.Inspection;

namespace UTStudio.Contracts.Inspection;

public readonly record struct UtStreamIdentity
{
    public UtStreamIdentity(InspectionId inspectionId, AcquisitionRunId acquisitionRunId,
        UtSourceId sourceId, PhysicalChannelId physicalChannelId, BeamId? beamId = null,
        FocalLawId? focalLawId = null)
    {
        if (!inspectionId.IsValid) { throw new ArgumentException("A valid inspection identifier is required.", nameof(inspectionId)); }
        if (!acquisitionRunId.IsValid) { throw new ArgumentException("A valid run identifier is required.", nameof(acquisitionRunId)); }
        if (!sourceId.IsValid) { throw new ArgumentException("A valid source identifier is required.", nameof(sourceId)); }
        if (beamId is { IsValid: false }) { throw new ArgumentException("A valid beam identifier is required when present.", nameof(beamId)); }
        if (focalLawId is { IsValid: false }) { throw new ArgumentException("A valid focal-law identifier is required when present.", nameof(focalLawId)); }
        InspectionId = inspectionId;
        AcquisitionRunId = acquisitionRunId;
        SourceId = sourceId;
        PhysicalChannelId = physicalChannelId;
        BeamId = beamId;
        FocalLawId = focalLawId;
    }

    public InspectionId InspectionId { get; }
    public AcquisitionRunId AcquisitionRunId { get; }
    public UtSourceId SourceId { get; }
    public PhysicalChannelId PhysicalChannelId { get; }
    public BeamId? BeamId { get; }
    public FocalLawId? FocalLawId { get; }
    public bool IsValid => InspectionId.IsValid && AcquisitionRunId.IsValid && SourceId.IsValid &&
        BeamId is not { IsValid: false } && FocalLawId is not { IsValid: false };
    public override string ToString() => $"inspection={InspectionId};run={AcquisitionRunId};source={SourceId};channel={PhysicalChannelId.Value};beam={BeamId?.ToString() ?? "-"};law={FocalLawId?.ToString() ?? "-"}";
}

public readonly record struct TraceIdentity
{
    public TraceIdentity(UtStreamIdentity stream, ulong acquisitionIndex)
    {
        if (!stream.IsValid) { throw new ArgumentException("A valid stream identity is required.", nameof(stream)); }
        Stream = stream;
        AcquisitionIndex = acquisitionIndex;
    }

    public UtStreamIdentity Stream { get; }
    public ulong AcquisitionIndex { get; }
    public bool IsValid => Stream.IsValid;
    public override string ToString() => $"{Stream};index={AcquisitionIndex}";
}

public readonly record struct InspectionRevision
{
    public InspectionRevision(ulong value) => Value = value;
    public ulong Value { get; }
    public static InspectionRevision Initial => default;
}

public enum UtClockQuality { Unknown, Synchronized, Estimated, Unreliable }
public enum UtSampleEncoding { Unknown, SignedInt16 }
public enum UtTraceProvenance { Unknown, Simulator, Hardware, FileReplay, Imported }
public enum UtTraceQuality { Unknown, Good, Degraded, Invalid }

public sealed record TraceDescriptor
{
    public TraceDescriptor(TraceIdentity identity, ulong sequence, TimeSpan elapsedSinceRunStart,
        DateTimeOffset? timestampUtc, ScanSpatialCoordinate? spatialCoordinate, int sampleCount,
        double sampleRateHz, double firstSampleOffsetSeconds, UtSignalMode signalMode,
        UtClockQuality clockQuality = UtClockQuality.Unknown,
        UtSampleEncoding sampleEncoding = UtSampleEncoding.SignedInt16,
        UtTraceProvenance provenance = UtTraceProvenance.Unknown,
        UtTraceQuality traceQuality = UtTraceQuality.Unknown)
    {
        if (!identity.IsValid) { throw new ArgumentException("A valid trace identity is required.", nameof(identity)); }
        if (elapsedSinceRunStart < TimeSpan.Zero) { throw new ArgumentOutOfRangeException(nameof(elapsedSinceRunStart)); }
        if (spatialCoordinate is { IsValid: false }) { throw new ArgumentException("The spatial coordinate is invalid.", nameof(spatialCoordinate)); }
        ArgumentOutOfRangeException.ThrowIfLessThan(sampleCount, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(sampleCount, ConventionalAcquisitionConfiguration.MaximumSampleCount);
        if (!double.IsFinite(sampleRateHz) || sampleRateHz <= 0 || sampleRateHz > ConventionalAcquisitionConfiguration.MaximumSampleRateHz)
        { throw new ArgumentOutOfRangeException(nameof(sampleRateHz)); }
        if (!double.IsFinite(firstSampleOffsetSeconds)) { throw new ArgumentOutOfRangeException(nameof(firstSampleOffsetSeconds)); }
        if (signalMode != UtSignalMode.Rf) { throw new ArgumentOutOfRangeException(nameof(signalMode), "Only bipolar RF is supported in phase 1."); }
        if (!Enum.IsDefined(clockQuality)) { throw new ArgumentOutOfRangeException(nameof(clockQuality)); }
        if (!Enum.IsDefined(sampleEncoding)) { throw new ArgumentOutOfRangeException(nameof(sampleEncoding)); }
        if (!Enum.IsDefined(provenance)) { throw new ArgumentOutOfRangeException(nameof(provenance)); }
        if (!Enum.IsDefined(traceQuality)) { throw new ArgumentOutOfRangeException(nameof(traceQuality)); }
        Identity = identity;
        Sequence = sequence;
        ElapsedSinceRunStart = elapsedSinceRunStart;
        TimestampUtc = timestampUtc?.ToUniversalTime();
        SpatialCoordinate = spatialCoordinate;
        SampleCount = sampleCount;
        SampleRateHz = sampleRateHz;
        FirstSampleOffsetSeconds = firstSampleOffsetSeconds;
        SignalMode = signalMode;
        ClockQuality = clockQuality;
        SampleEncoding = sampleEncoding;
        Provenance = provenance;
        TraceQuality = traceQuality;
    }

    public TraceIdentity Identity { get; }
    public ulong Sequence { get; }
    public TimeSpan ElapsedSinceRunStart { get; }
    public DateTimeOffset? TimestampUtc { get; }
    public ScanSpatialCoordinate? SpatialCoordinate { get; }
    public int SampleCount { get; }
    public double SampleRateHz { get; }
    public double FirstSampleOffsetSeconds { get; }
    public UtSignalMode SignalMode { get; }
    public UtClockQuality ClockQuality { get; }
    public UtSampleEncoding SampleEncoding { get; }
    public UtTraceProvenance Provenance { get; }
    public UtTraceQuality TraceQuality { get; }

    public override string ToString() =>
        $"trace={Identity};sequence={Sequence};sampleCount={SampleCount};clock={ClockQuality};encoding={SampleEncoding};provenance={Provenance};quality={TraceQuality}";
}

public readonly record struct InspectionTraceInput
{
    public InspectionTraceInput(TraceDescriptor descriptor, ReadOnlyMemory<short> samples)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        if (samples.Length != descriptor.SampleCount) { throw new ArgumentException("The sample memory length must equal SampleCount.", nameof(samples)); }
        Descriptor = descriptor;
        Samples = samples;
    }

    public TraceDescriptor Descriptor { get; }
    public ReadOnlyMemory<short> Samples { get; }
}

public enum InspectionAppendStatus { Unknown, Committed, Duplicate, Conflict, CapacityExceeded, Closed, Cancelled, Faulted }
public readonly record struct InspectionAppendResult
{
    public InspectionAppendResult(InspectionAppendStatus status, InspectionRevision revision,
        bool wasAccepted, string? errorCode = null)
    {
        if (!Enum.IsDefined(status) || status == InspectionAppendStatus.Unknown)
        { throw new ArgumentOutOfRangeException(nameof(status)); }
        if ((status == InspectionAppendStatus.Closed) == wasAccepted)
        {
            throw new ArgumentException("Closed is the only terminal status for an append that was not accepted.",
                nameof(wasAccepted));
        }
        if (status == InspectionAppendStatus.Committed && revision == InspectionRevision.Initial)
        { throw new ArgumentException("A committed append requires a non-initial revision.", nameof(revision)); }
        Status = status;
        Revision = revision;
        WasAccepted = wasAccepted;
        ErrorCode = errorCode;
    }

    public InspectionAppendStatus Status { get; }
    public InspectionRevision Revision { get; }
    public bool WasAccepted { get; }
    public string? ErrorCode { get; }
    public bool IsCommitted => Status == InspectionAppendStatus.Committed && WasAccepted &&
        Revision != InspectionRevision.Initial;
    public bool Durable => false;
}

/// <summary>
/// Phase 1 returns NotAvailable for an absent index because it has no authoritative closed-range
/// watermark. Gap and OutOfRange are reserved for later producers that can prove those states.
/// </summary>
public enum InspectionReadStatus { Available, NotAvailable, Gap, OutOfRange, Cancelled, Closed, Faulted }

/// <summary>
/// A forward-only range expressed as a start index and positive count. An inverted range is not
/// representable; construction rejects nonpositive counts and arithmetic overflow.
/// </summary>
public readonly record struct InspectionRangeQuery
{
    public InspectionRangeQuery(UtStreamIdentity stream, ulong startAcquisitionIndex, int maximumCount)
    {
        if (!stream.IsValid) { throw new ArgumentException("A valid stream is required.", nameof(stream)); }
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumCount, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maximumCount, 4096);
        _ = checked(startAcquisitionIndex + (ulong)(maximumCount - 1));
        Stream = stream;
        StartAcquisitionIndex = startAcquisitionIndex;
        MaximumCount = maximumCount;
    }
    public UtStreamIdentity Stream { get; }
    public ulong StartAcquisitionIndex { get; }
    public int MaximumCount { get; }
    public bool IsValid => Stream.IsValid && MaximumCount is >= 1 and <= 4096 &&
        StartAcquisitionIndex <= ulong.MaxValue - (ulong)(MaximumCount - 1);
}

public readonly record struct StoreDiagnostics(long LogicalSampleBytes, long ReservedSampleCapacityBytes,
    long OwnedSampleCapacityBytes, long MaximumOwnedSampleCapacityBytes, long SampleCapacityBudgetBytes,
    int MaximumEntries, int CommittedEntries, int ActiveWriters, int MaximumActiveWriters,
    int WritersPendingDuringClose, int ActiveLeases, int MaximumActiveLeases, ulong CommittedTraces,
    ulong CancelledAppends, ulong PreAdmissionClosedRejections, ulong DuplicateCount, ulong ConflictCount,
    ulong CapacityRejections, int ActiveChunks, int SealedChunks, bool IsClosed)
{
    public bool Durable => false;
}
