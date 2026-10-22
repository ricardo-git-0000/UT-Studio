using UTStudio.Processing.Core;
using UTStudio.Contracts.Processing;

namespace UTStudio.Application;

/// <summary>
/// Independent inspection-scoped limits. Each owner accounts for its own resource; the values are never
/// combined into a synthetic budget. Defaults preserve the pre-B5 capacities.
/// </summary>
internal sealed record GateHeadlessInspectionOptions
{
    internal GateHeadlessInspectionOptions(int maximumRetainedLedgers = 8,
        long maximumEstimatedLedgerBytes = 64 * 1024, int maximumHistoricalObservers = 64,
        int maximumReplacementCandidates = 64, int maximumHistoricalJobs = 64,
        int maximumHistoricalWorkers = 1, long historicalSampleBufferBytes = 8 * 1024 * 1024,
        int maximumLiveEpochsPerRun = 8)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumRetainedLedgers, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumEstimatedLedgerBytes, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumHistoricalObservers, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumReplacementCandidates, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumHistoricalJobs, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumHistoricalWorkers, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(historicalSampleBufferBytes, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumLiveEpochsPerRun, 1);
        MaximumRetainedLedgers = maximumRetainedLedgers;
        MaximumEstimatedLedgerBytes = maximumEstimatedLedgerBytes;
        MaximumHistoricalObservers = maximumHistoricalObservers;
        MaximumReplacementCandidates = maximumReplacementCandidates;
        MaximumHistoricalJobs = maximumHistoricalJobs;
        MaximumHistoricalWorkers = maximumHistoricalWorkers;
        HistoricalSampleBufferBytes = historicalSampleBufferBytes;
        MaximumLiveEpochsPerRun = maximumLiveEpochsPerRun;
    }

    internal int MaximumRetainedLedgers { get; }
    internal long MaximumEstimatedLedgerBytes { get; }
    internal int MaximumHistoricalObservers { get; }
    internal int MaximumReplacementCandidates { get; }
    internal int MaximumHistoricalJobs { get; }
    internal int MaximumHistoricalWorkers { get; }
    internal long HistoricalSampleBufferBytes { get; }
    internal int MaximumLiveEpochsPerRun { get; }

    internal GatePlanAuthorityPreparedCommitOptions AuthorityOptions() =>
        new(MaximumRetainedLedgers, MaximumEstimatedLedgerBytes);

    internal GateReprocessingSchedulerOptions SchedulerOptions() =>
        new(MaximumHistoricalWorkers, MaximumHistoricalJobs, HistoricalSampleBufferBytes);
}
