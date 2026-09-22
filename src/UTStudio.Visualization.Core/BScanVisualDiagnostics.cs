namespace UTStudio.Visualization.Core;

public enum BScanVisualCloseState { Open, Closing, Closed }

public sealed record BScanVisualBuildDurationStatistics(
    long Count, TimeSpan P50, TimeSpan P95, TimeSpan P99, TimeSpan Maximum);

public sealed record BScanVisualDiagnostics(
    ulong ColumnsIndexed,
    ulong RasterRegionsBuilt,
    ulong FullRefreshesBuilt,
    ulong IncrementalUpdatesBuilt,
    ulong VisualBuildsSuperseded,
    ulong VisualUpdatesPublished,
    ulong VisualUpdatesCoalesced,
    ulong ObserverCallbacksStarted,
    ulong ObserverCallbacksCompleted,
    ulong ObserverErrors,
    int ActiveRasterOwners,
    int MaximumRasterOwners,
    long VisualBytesLogical,
    long VisualBytesRented,
    long MaximumVisualBytesRented,
    BScanVisualBuildDurationStatistics BuildDuration,
    BScanVisualCloseState CloseState,
    int ActiveBuilds,
    string? LastVisualError);
