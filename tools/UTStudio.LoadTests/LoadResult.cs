using UTStudio.Domain.Acquisition;
using UTStudio.Acquisition.Simulator;

namespace UTStudio.LoadTests;

internal sealed record LoadResult(
    long Produced,
    long Consumed,
    long Released,
    int OutstandingBuffers,
    int MaximumOutstandingBuffers,
    long VisualReceived,
    long VisualPublished,
    long VisualReplaced,
    long VisualDropped,
    long VisualObserverErrors,
    double ThroughputFramesPerSecond,
    PercentileSnapshot? VisualDeliveryLatencyMicroseconds,
    PercentileSnapshot? ProjectionMicroseconds,
    long ManagedBytes,
    long WorkingSetBytes,
    long? MaximumWorkingSetBytes,
    int Gen0,
    int Gen1,
    int Gen2,
    double CpuPercent,
    double? MaximumCpuPercent,
    bool FramesReleasedBarrier,
    TimeSpan StopDuration,
    IReadOnlyList<UtSourceError> Errors,
    bool Cancelled)
{
    internal long? ManagedBytesAtActiveStart { get; init; }
    internal long? ManagedBytesAtActiveEnd { get; init; }
    internal long? WorkingSetBytesAtActiveStart { get; init; }
    internal long? WorkingSetBytesAtActiveEnd { get; init; }
    internal CampaignOutcome Outcome { get; init; } = CampaignOutcome.Completed;
    internal CampaignOutcome PrimaryOutcome { get; init; } = CampaignOutcome.Completed;
    internal Task<CleanupReport>? PendingCleanup { get; init; }
    internal bool CleanupSucceeded { get; init; }
    internal CounterSnapshot Counters { get; init; }
    internal RateWindow ActiveWindow { get; init; }
    internal long DrainedDuringStop { get; init; }
    internal long ConsumedAfterActiveBeforeStop { get; init; }
    internal DemandSnapshot Demand { get; init; }
    internal double? TargetRate { get; init; }
    internal double? GridRate { get; init; }
    internal string SourceDescription { get; init; } = "unspecified";
    internal long? CorrelationMisses { get; init; }
    internal long? CorrelationOverwrites { get; init; }
    internal ResourceSample[]? ResourceSamples { get; init; }
    internal long? TotalResourceSamples { get; init; }
    internal double StartupCpuSeconds { get; init; }
    internal double CleanupCpuSeconds { get; init; }
}

internal enum CampaignOutcome { Completed, Cancelled, NoProgress, FunctionalFailure, CleanupTimeout }
internal sealed record CleanupReport(bool Barrier, int Outstanding, int MaximumOutstanding, IReadOnlyList<UtSourceError> Errors);

internal static class FunctionalCriteria
{
    internal static IReadOnlyList<string> Evaluate(LoadResult result)
    {
        List<string> failures = [];
        if (result.Produced != result.Consumed || result.Consumed != result.Released)
        { failures.Add($"Acquisition balance failed: produced={result.Produced}, consumed={result.Consumed}, released={result.Released}."); }
        if (result.OutstandingBuffers != 0) { failures.Add($"Outstanding buffers: {result.OutstandingBuffers}."); }
        if (result.VisualPublished + result.VisualReplaced > result.VisualReceived)
        { failures.Add("Visual counters are inconsistent."); }
        if (result.VisualObserverErrors != 0) { failures.Add($"Visual observer errors: {result.VisualObserverErrors}."); }
        if (!result.FramesReleasedBarrier) { failures.Add("AllFramesReleased was not confirmed."); }
        if (result.Errors.Count != 0) { failures.Add($"Observed errors: {result.Errors.Count}."); }
        var counts = result.Counters;
        if (counts.Generated != counts.Accepted + counts.Untransferred)
        { failures.Add("Generated != accepted + untransferred."); }
        if (counts.Rented != counts.Returned) { failures.Add("Sample rent/return balance failed."); }
        return failures;
    }

    internal static int ExitCode(LoadResult result)
    {
        if (result.Outcome == CampaignOutcome.CleanupTimeout) { return 5; }
        if (Evaluate(result).Count != 0 || result.Outcome == CampaignOutcome.FunctionalFailure) { return 2; }
        if (result.Outcome == CampaignOutcome.Cancelled) { return 3; }
        if (result.Outcome == CampaignOutcome.NoProgress || result.Consumed == 0) { return 4; }
        return 0;
    }
}
