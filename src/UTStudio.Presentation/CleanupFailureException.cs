namespace UTStudio.Presentation;

public sealed record CleanupStageFailure(string Stage, Exception Error);

/// <summary>Preserves the first cleanup failure and every later failure in execution order.</summary>
public sealed class CleanupFailureException : Exception
{
    public CleanupFailureException(string message, Exception primaryError, IReadOnlyList<Exception> secondaryErrors)
        : this(message, new("cleanup", primaryError), secondaryErrors.Select(error => new CleanupStageFailure("cleanup", error)).ToArray())
    { }

    public CleanupFailureException(string message, CleanupStageFailure primaryFailure,
        IReadOnlyList<CleanupStageFailure> secondaryFailures)
        : base(message, primaryFailure.Error)
    {
        PrimaryFailure = primaryFailure ?? throw new ArgumentNullException(nameof(primaryFailure));
        SecondaryFailures = Array.AsReadOnly((secondaryFailures ??
            throw new ArgumentNullException(nameof(secondaryFailures))).ToArray());
    }

    public CleanupStageFailure PrimaryFailure { get; }
    public IReadOnlyList<CleanupStageFailure> SecondaryFailures { get; }
    public Exception PrimaryError => PrimaryFailure.Error;
    public IReadOnlyList<Exception> SecondaryErrors => SecondaryFailures.Select(failure => failure.Error).ToArray();
}
