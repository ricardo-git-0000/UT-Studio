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

public enum InspectionGapCause
{
    Unknown,
    SourceSequenceDiscontinuity,
    SourceReportedLoss,
    TransportLoss,
    CorruptFrame
}

/// <summary>An inclusive Sequence interval confirmed by the later observed frame.</summary>
public readonly record struct GapRecord
{
    public GapRecord(UtStreamIdentity stream, ulong firstMissingSequence, ulong lastMissingSequence,
        ulong previousAdmittedSequence, ulong subsequentObservedSequence, InspectionGapCause cause,
        InspectionRevision confirmedRevision = default)
    {
        if (!stream.IsValid) { throw new ArgumentException("A valid stream is required.", nameof(stream)); }
        if (!Enum.IsDefined(cause) || cause == InspectionGapCause.Unknown)
        { throw new ArgumentOutOfRangeException(nameof(cause)); }
        if (firstMissingSequence > lastMissingSequence)
        { throw new ArgumentException("The inclusive missing range cannot be empty or inverted."); }
        if (previousAdmittedSequence == ulong.MaxValue || firstMissingSequence <= previousAdmittedSequence)
        { throw new ArgumentException("The missing range must follow the admitted sequence."); }
        if (lastMissingSequence == ulong.MaxValue || subsequentObservedSequence != lastMissingSequence + 1)
        { throw new ArgumentException("The observed sequence must immediately follow the missing range."); }
        Stream = stream;
        FirstMissingSequence = firstMissingSequence;
        LastMissingSequence = lastMissingSequence;
        PreviousAdmittedSequence = previousAdmittedSequence;
        SubsequentObservedSequence = subsequentObservedSequence;
        Cause = cause;
        ConfirmedRevision = confirmedRevision;
    }

    public UtStreamIdentity Stream { get; }
    public ulong FirstMissingSequence { get; }
    public ulong LastMissingSequence { get; }
    public ulong PreviousAdmittedSequence { get; }
    public ulong SubsequentObservedSequence { get; }
    public InspectionGapCause Cause { get; }
    public InspectionRevision ConfirmedRevision { get; init; }
    public bool IsValid => Stream.IsValid && Cause != InspectionGapCause.Unknown &&
        FirstMissingSequence <= LastMissingSequence && PreviousAdmittedSequence != ulong.MaxValue &&
        FirstMissingSequence > PreviousAdmittedSequence && LastMissingSequence != ulong.MaxValue &&
        SubsequentObservedSequence == LastMissingSequence + 1;
    public override string ToString() =>
        $"stream={Stream};missing={FirstMissingSequence}..{LastMissingSequence};previous={PreviousAdmittedSequence};subsequent={SubsequentObservedSequence};cause={Cause};revision={ConfirmedRevision.Value}";
}

public readonly record struct InspectionCommitInput
{
    public InspectionCommitInput(InspectionTraceInput trace, IReadOnlyList<GapRecord>? gaps = null)
    {
        if (trace.Descriptor is null) { throw new ArgumentException("A trace is required.", nameof(trace)); }
        Trace = trace;
        Gaps = Array.AsReadOnly((gaps ?? []).ToArray());
        if (Gaps.Count > 1)
        { throw new ArgumentException("A single trace can confirm at most one contiguous Sequence gap.", nameof(gaps)); }
        if (Gaps.Any(gap => !gap.IsValid || gap.Stream != trace.Descriptor.Identity.Stream ||
            gap.SubsequentObservedSequence != trace.Descriptor.Sequence ||
            gap.ConfirmedRevision != InspectionRevision.Initial))
        { throw new ArgumentException("Every unconfirmed gap must be valid and belong to the trace stream.", nameof(gaps)); }
    }
    public InspectionTraceInput Trace { get; }
    public IReadOnlyList<GapRecord> Gaps { get; }
}

public enum InspectionRunState { Unknown, Open, Sealing, Sealed, Faulted }

public readonly record struct InspectionRunSealResult(AcquisitionRunId RunId, InspectionRevision FinalRevision,
    ulong? MinimumAcquisitionIndex, ulong? MaximumAcquisitionIndex, ulong? MinimumSequence,
    ulong? MaximumSequence, int TraceCount, int GapCount);

public enum InspectionStreamStateReadStatus
{
    Unknown = 0,
    Available = 1,
    NotAvailable = 2,
    Closed = 3,
    Cancelled = 4,
    Faulted = 5
}

public enum InspectionStreamStateErrorCode
{
    Unknown = 0,
    InspectionNotFound = 1,
    RunNotFound = 2,
    StreamNotFound = 3,
    StoreClosed = 4,
    InternalFailure = 5
}

public enum InspectionRunFaultCode
{
    Unknown = 0,
    SealFailed = 1
}

/// <summary>
/// Immutable, sample-free state of one inspection stream captured at a single store revision.
/// Committed-index watermarks are sparse bounds: they neither assert density nor describe Sequence gaps.
/// </summary>
public sealed class InspectionStreamStateSnapshot : IEquatable<InspectionStreamStateSnapshot>
{
    public InspectionStreamStateSnapshot(InspectionId inspectionId, AcquisitionRunId acquisitionRunId,
        UtStreamIdentity stream, InspectionRunState runState, InspectionRevision revision,
        InspectionRevision? finalRevision, ulong? minimumCommittedAcquisitionIndex,
        ulong? maximumCommittedAcquisitionIndex, int committedTraceCount, int sequenceGapCount,
        bool areWatermarksFinal, InspectionRunFaultCode? runFaultCode = null)
    {
        if (!inspectionId.IsValid) { throw new ArgumentException("A valid inspection identifier is required.", nameof(inspectionId)); }
        if (!acquisitionRunId.IsValid) { throw new ArgumentException("A valid run identifier is required.", nameof(acquisitionRunId)); }
        if (!stream.IsValid) { throw new ArgumentException("A valid stream is required.", nameof(stream)); }
        if (stream.InspectionId != inspectionId || stream.AcquisitionRunId != acquisitionRunId)
        { throw new ArgumentException("The inspection, run and stream identifiers must agree.", nameof(stream)); }
        if (!Enum.IsDefined(runState) || runState == InspectionRunState.Unknown)
        { throw new ArgumentOutOfRangeException(nameof(runState)); }
        if (revision == InspectionRevision.Initial)
        { throw new ArgumentException("An available stream state requires a captured revision.", nameof(revision)); }
        ArgumentOutOfRangeException.ThrowIfNegative(committedTraceCount);
        ArgumentOutOfRangeException.ThrowIfNegative(sequenceGapCount);
        if (minimumCommittedAcquisitionIndex.HasValue != maximumCommittedAcquisitionIndex.HasValue)
        { throw new ArgumentException("Committed acquisition-index watermarks must both be present or absent."); }
        if (minimumCommittedAcquisitionIndex > maximumCommittedAcquisitionIndex)
        { throw new ArgumentException("The minimum committed acquisition index cannot exceed the maximum."); }
        if ((committedTraceCount == 0) != !minimumCommittedAcquisitionIndex.HasValue)
        { throw new ArgumentException("Only a stream without committed traces can omit both watermarks.", nameof(committedTraceCount)); }

        bool sealedRun = runState == InspectionRunState.Sealed;
        if (sealedRun != finalRevision.HasValue || sealedRun != areWatermarksFinal)
        { throw new ArgumentException("Only a confirmed sealed run has a final revision and final watermarks."); }
        if (finalRevision is { } final &&
            (final == InspectionRevision.Initial || final.Value > revision.Value))
        { throw new ArgumentException("The final revision must be non-initial and no later than the captured revision.", nameof(finalRevision)); }
        if (runState == InspectionRunState.Faulted)
        {
            if (runFaultCode is null || !Enum.IsDefined(runFaultCode.Value) ||
                runFaultCode == InspectionRunFaultCode.Unknown)
            { throw new ArgumentException("A faulted run requires a known bounded fault code.", nameof(runFaultCode)); }
        }
        else if (runFaultCode is not null)
        { throw new ArgumentException("Only a faulted run can expose a run fault code.", nameof(runFaultCode)); }

        InspectionId = inspectionId;
        AcquisitionRunId = acquisitionRunId;
        Stream = stream;
        RunState = runState;
        Revision = revision;
        FinalRevision = finalRevision;
        MinimumCommittedAcquisitionIndex = minimumCommittedAcquisitionIndex;
        MaximumCommittedAcquisitionIndex = maximumCommittedAcquisitionIndex;
        CommittedTraceCount = committedTraceCount;
        SequenceGapCount = sequenceGapCount;
        AreWatermarksFinal = areWatermarksFinal;
        RunFaultCode = runFaultCode;
    }

    public InspectionId InspectionId { get; }
    public AcquisitionRunId AcquisitionRunId { get; }
    public UtStreamIdentity Stream { get; }
    public InspectionRunState RunState { get; }
    public InspectionRevision Revision { get; }
    public InspectionRevision? FinalRevision { get; }
    public ulong? MinimumCommittedAcquisitionIndex { get; }
    public ulong? MaximumCommittedAcquisitionIndex { get; }
    public int CommittedTraceCount { get; }
    public int SequenceGapCount { get; }
    public bool AreWatermarksFinal { get; }
    public InspectionRunFaultCode? RunFaultCode { get; }

    public bool Equals(InspectionStreamStateSnapshot? other) => other is not null &&
        InspectionId == other.InspectionId && AcquisitionRunId == other.AcquisitionRunId &&
        Stream == other.Stream && RunState == other.RunState && Revision == other.Revision &&
        FinalRevision == other.FinalRevision &&
        MinimumCommittedAcquisitionIndex == other.MinimumCommittedAcquisitionIndex &&
        MaximumCommittedAcquisitionIndex == other.MaximumCommittedAcquisitionIndex &&
        CommittedTraceCount == other.CommittedTraceCount && SequenceGapCount == other.SequenceGapCount &&
        AreWatermarksFinal == other.AreWatermarksFinal && RunFaultCode == other.RunFaultCode;
    public override bool Equals(object? obj) => Equals(obj as InspectionStreamStateSnapshot);
    public override int GetHashCode() => HashCode.Combine(
        HashCode.Combine(InspectionId, AcquisitionRunId, Stream, RunState, Revision, FinalRevision,
            MinimumCommittedAcquisitionIndex, MaximumCommittedAcquisitionIndex),
        CommittedTraceCount, SequenceGapCount, AreWatermarksFinal, RunFaultCode);
}

public readonly record struct InspectionStreamStateReadResult
{
    private InspectionStreamStateReadResult(InspectionStreamStateReadStatus status,
        InspectionRevision revision, InspectionStreamStateSnapshot? snapshot,
        InspectionStreamStateErrorCode? errorCode)
    {
        Status = status;
        Revision = revision;
        Snapshot = snapshot;
        ErrorCode = errorCode;
    }

    public InspectionStreamStateReadStatus Status { get; }
    public InspectionRevision Revision { get; }
    public InspectionStreamStateSnapshot? Snapshot { get; }
    public InspectionStreamStateErrorCode? ErrorCode { get; }
    public bool IsAvailable => Status == InspectionStreamStateReadStatus.Available && Snapshot is not null;

    public static InspectionStreamStateReadResult Available(InspectionStreamStateSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return new(InspectionStreamStateReadStatus.Available, snapshot.Revision, snapshot, null);
    }

    public static InspectionStreamStateReadResult NotAvailable(InspectionRevision revision,
        InspectionStreamStateErrorCode errorCode)
    {
        if (errorCode is not (InspectionStreamStateErrorCode.InspectionNotFound or
            InspectionStreamStateErrorCode.RunNotFound or InspectionStreamStateErrorCode.StreamNotFound))
        { throw new ArgumentOutOfRangeException(nameof(errorCode)); }
        return new(InspectionStreamStateReadStatus.NotAvailable, revision, null, errorCode);
    }

    public static InspectionStreamStateReadResult Closed(InspectionRevision revision)
    {
        return new(InspectionStreamStateReadStatus.Closed, revision, null,
            InspectionStreamStateErrorCode.StoreClosed);
    }

    public static InspectionStreamStateReadResult Cancelled() =>
        new(InspectionStreamStateReadStatus.Cancelled, InspectionRevision.Initial, null, null);

    public static InspectionStreamStateReadResult Faulted() =>
        new(InspectionStreamStateReadStatus.Faulted, InspectionRevision.Initial, null,
            InspectionStreamStateErrorCode.InternalFailure);
}

public readonly record struct RunStoreDiagnostics(AcquisitionRunId RunId, InspectionRunState State,
    int ActiveWriters, int PendingOperations, int CommittedTraces, int ConfirmedGaps,
    ulong DuplicateCount, ulong ConflictCount, ulong CancellationCount, ulong RejectionCount,
    InspectionRevision? FinalRevision, ulong? MinimumAcquisitionIndex, ulong? MaximumAcquisitionIndex,
    ulong? MinimumSequence, ulong? MaximumSequence);

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
/// An absent index is NotAvailable while the run is open or when it lies inside confirmed watermarks.
/// A sealed run returns OutOfRange outside its final committed acquisition-index watermarks.
/// </summary>
public enum InspectionReadStatus
{
    Unknown = 0,
    Available = 1,
    NotAvailable = 2,
    Gap = 3,
    OutOfRange = 4,
    Cancelled = 5,
    Closed = 6,
    Faulted = 7
}

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
    ulong CapacityRejections, int ActiveChunks, int SealedChunks, bool IsClosed,
    int OpenRuns = 0, int SealingRuns = 0, int SealedRuns = 0, int ActiveRunWriters = 0,
    int ConfirmedGaps = 0, int PendingSealOperations = 0,
    IReadOnlyList<RunStoreDiagnostics>? Runs = null)
{
    public bool Durable => false;
}
