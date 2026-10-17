using UTStudio.Domain.Acquisition;
using UTStudio.Domain.Inspection;

namespace UTStudio.Contracts.Inspection;

public enum InspectionCommittedChangeKind { Unknown, Trace, GapOnly }

public readonly record struct InspectionCommittedChange
{
    public InspectionCommittedChange(InspectionRevision revision, AcquisitionRunId runId,
        UtStreamIdentity stream, InspectionCommittedChangeKind kind, TraceIdentity? traceIdentity = null)
    {
        if (revision == InspectionRevision.Initial) { throw new ArgumentException("A committed change requires a revision.", nameof(revision)); }
        if (!runId.IsValid || !stream.IsValid || stream.AcquisitionRunId != runId)
        { throw new ArgumentException("Run and stream must be valid and agree."); }
        if (!Enum.IsDefined(kind) || kind == InspectionCommittedChangeKind.Unknown)
        { throw new ArgumentOutOfRangeException(nameof(kind)); }
        if ((kind == InspectionCommittedChangeKind.Trace) != traceIdentity.HasValue ||
            traceIdentity is { } trace && trace.Stream != stream)
        { throw new ArgumentException("Only a trace change has a matching trace identity.", nameof(traceIdentity)); }
        Revision = revision; RunId = runId; Stream = stream; Kind = kind; TraceIdentity = traceIdentity;
    }
    public InspectionRevision Revision { get; }
    public AcquisitionRunId RunId { get; }
    public UtStreamIdentity Stream { get; }
    public InspectionCommittedChangeKind Kind { get; }
    public TraceIdentity? TraceIdentity { get; }
}

public readonly record struct InspectionChangeQuery
{
    public InspectionChangeQuery(InspectionId inspectionId, AcquisitionRunId runId,
        InspectionRevision afterExclusive, InspectionRevision throughInclusive, int maximumCount)
    {
        if (!inspectionId.IsValid || !runId.IsValid) { throw new ArgumentException("Inspection and run must be valid."); }
        if (throughInclusive.Value < afterExclusive.Value) { throw new ArgumentException("The revision range is inverted."); }
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumCount, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maximumCount, 4096);
        InspectionId = inspectionId; RunId = runId; AfterExclusive = afterExclusive;
        ThroughInclusive = throughInclusive; MaximumCount = maximumCount;
    }
    public InspectionId InspectionId { get; }
    public AcquisitionRunId RunId { get; }
    public InspectionRevision AfterExclusive { get; }
    public InspectionRevision ThroughInclusive { get; }
    public int MaximumCount { get; }
}

public sealed record InspectionChangePage(InspectionRevision ScannedThroughRevision,
    IReadOnlyList<InspectionCommittedChange> Changes, bool HasMore);

public interface IInspectionChangeProvider
{
    ValueTask<InspectionChangePage> ReadChangesAsync(InspectionChangeQuery query,
        CancellationToken cancellationToken = default);
}
