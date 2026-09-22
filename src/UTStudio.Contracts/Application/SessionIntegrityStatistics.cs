namespace UTStudio.Contracts.Application;

/// <summary>Per-run acquisition integrity counters captured in a session snapshot.</summary>
public sealed record SessionIntegrityStatistics(
    ulong AcquisitionMissingFrames,
    ulong DuplicateFramesRejected,
    ulong OutOfOrderFramesRejected,
    ulong TimestampRegressionsRejected,
    ulong InvalidAcquisitionIndexFramesRejected)
{
    public static SessionIntegrityStatistics Empty { get; } = new(0, 0, 0, 0, 0);
}
