using System.Collections.ObjectModel;
using UTStudio.Domain.Acquisition;

namespace UTStudio.Contracts.Application;

/// <summary>Immutable lifecycle snapshot. Contains metadata and counters, never sample memory.</summary>
/// <remarks>Counters are captured at lifecycle transitions; this stage has no periodic telemetry publisher.</remarks>
public sealed class SessionSnapshot
{
    public SessionSnapshot(ulong version, SessionPhase phase, UtSourceId sourceId,
        AcquisitionRunId? runId, long receivedFrames, long releasedFrames, ulong? lastSequence,
        ConventionalUtFrameMetadata? lastMetadata, UtSourceError? primaryError,
        IEnumerable<UtSourceError> cleanupErrors, long additionalErrorCount, UtSourceError? visualError = null,
        bool canStart = false, bool canStop = false)
    {
        if (!Enum.IsDefined(phase)) { throw new ArgumentOutOfRangeException(nameof(phase)); }
        if (!sourceId.IsValid) { throw new ArgumentException("Invalid source identifier.", nameof(sourceId)); }
        if (runId is { IsValid: false }) { throw new ArgumentException("Invalid run identifier.", nameof(runId)); }
        ArgumentOutOfRangeException.ThrowIfNegative(receivedFrames);
        ArgumentOutOfRangeException.ThrowIfNegative(releasedFrames);
        ArgumentOutOfRangeException.ThrowIfNegative(additionalErrorCount);
        ArgumentNullException.ThrowIfNull(cleanupErrors);
        var errors = cleanupErrors.ToArray();
        if (errors.Any(error => error is null)) { throw new ArgumentException("Null cleanup error.", nameof(cleanupErrors)); }
        Version = version;
        Phase = phase;
        SourceId = sourceId;
        RunId = runId;
        ReceivedFrames = receivedFrames;
        ReleasedFrames = releasedFrames;
        LastSequence = lastSequence;
        LastMetadata = lastMetadata;
        PrimaryError = primaryError;
        CleanupErrors = Array.AsReadOnly(errors);
        AdditionalErrorCount = additionalErrorCount;
        VisualError = visualError;
        CanStart = canStart;
        CanStop = canStop;
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
    /// <summary>A failure in the optional visual branch; it does not imply an acquisition failure.</summary>
    public UtSourceError? VisualError { get; }
    /// <summary>Admission decided by the session; Faulted alone does not prove resources are released.</summary>
    public bool CanStart { get; }
    public bool CanStop { get; }
}
