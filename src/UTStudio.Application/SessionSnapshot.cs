using System.Collections.ObjectModel;
using UTStudio.Domain.Acquisition;

namespace UTStudio.Application;

/// <summary>Immutable lifecycle snapshot. Contains metadata and counters, never sample memory.</summary>
/// <remarks>Counters are captured at lifecycle transitions; this stage has no periodic telemetry publisher.</remarks>
public sealed class SessionSnapshot
{
    internal SessionSnapshot(ulong version, SessionPhase phase, UtSourceId sourceId,
        AcquisitionRunId? runId, long receivedFrames, long releasedFrames, ulong? lastSequence,
        ConventionalUtFrameMetadata? lastMetadata, UtSourceError? primaryError,
        IEnumerable<UtSourceError> cleanupErrors, long additionalErrorCount)
    {
        Version = version;
        Phase = phase;
        SourceId = sourceId;
        RunId = runId;
        ReceivedFrames = receivedFrames;
        ReleasedFrames = releasedFrames;
        LastSequence = lastSequence;
        LastMetadata = lastMetadata;
        PrimaryError = primaryError;
        CleanupErrors = Array.AsReadOnly(cleanupErrors.ToArray());
        AdditionalErrorCount = additionalErrorCount;
    }

    public ulong Version { get; }
    public SessionPhase Phase { get; }
    public UtSourceId SourceId { get; }
    public AcquisitionRunId? RunId { get; }
    public long ReceivedFrames { get; }
    public long ReleasedFrames { get; }
    public ulong? LastSequence { get; }
    public ConventionalUtFrameMetadata? LastMetadata { get; }
    public UtSourceError? PrimaryError { get; }
    public ReadOnlyCollection<UtSourceError> CleanupErrors { get; }
    public long AdditionalErrorCount { get; }
}
