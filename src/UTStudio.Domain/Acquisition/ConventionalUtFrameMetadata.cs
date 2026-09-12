namespace UTStudio.Domain.Acquisition;

/// <summary>Immutable metadata shared by all frames in an execution.</summary>
public sealed class ConventionalUtFrameMetadata
{
    public ConventionalUtFrameMetadata(
        UtSourceId sourceId,
        AcquisitionRunId runId,
        ConventionalAcquisitionConfiguration configuration,
        DateTimeOffset runStartedAtUtc)
    {
        if (!sourceId.IsValid)
        {
            throw new ArgumentException("A valid source identifier is required.", nameof(sourceId));
        }

        if (!runId.IsValid)
        {
            throw new ArgumentException("A valid execution identifier is required.", nameof(runId));
        }

        ArgumentNullException.ThrowIfNull(configuration);
        SourceId = sourceId;
        RunId = runId;
        Configuration = configuration;
        RunStartedAtUtc = runStartedAtUtc.ToUniversalTime();
    }

    public UtSourceId SourceId { get; }
    public AcquisitionRunId RunId { get; }
    public ConventionalAcquisitionConfiguration Configuration { get; }

    /// <summary>UTC-normalized civil origin for correlation, not ordering or elapsed time.</summary>
    public DateTimeOffset RunStartedAtUtc { get; }
}
