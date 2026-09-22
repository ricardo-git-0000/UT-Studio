namespace UTStudio.Visualization.Core;

public sealed record BScanAcceptDurationStatistics(
    long Count,
    TimeSpan P50,
    TimeSpan P95,
    TimeSpan P99,
    TimeSpan Maximum);

public sealed record BScanDiagnostics(
    ulong BScanColumnsAccepted,
    ulong BScanColumnsRejectedPinnedSlot,
    ulong BScanColumnsRejectedConfiguration,
    ulong BScanColumnsRejectedClosed,
    ulong BScanColumnsEvicted,
    ulong BScanIndexWorkSuperseded,
    int ActiveReadLeases,
    int MaximumReadLeases,
    ulong BScanAcceptBudgetExceeded,
    BScanAcceptDurationStatistics AcceptDuration,
    int BlocksRented,
    int BlocksReturned,
    long LogicalBytes,
    long RentedBytes,
    long VisualLogicalBytes,
    int EffectiveCapacity,
    string? WorkerError,
    IReadOnlyList<string> CleanupErrors,
    bool IsClosed);
