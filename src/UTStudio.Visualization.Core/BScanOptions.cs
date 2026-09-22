namespace UTStudio.Visualization.Core;

/// <summary>Bounded storage and indexing options for one live B-Scan run.</summary>
public sealed class BScanOptions
{
    public const long DefaultTraceMemoryBudgetBytes = 32L * 1024 * 1024;

    public BScanOptions(
        long traceMemoryBudgetBytes = DefaultTraceMemoryBudgetBytes,
        long visualMemoryBudgetBytes = 16L * 1024 * 1024,
        int maximumColumns = 1024,
        int minimumColumns = 128,
        int indexBucketSamples = 64,
        TimeSpan? acceptDiagnosticBudget = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(traceMemoryBudgetBytes, sizeof(short));
        ArgumentOutOfRangeException.ThrowIfLessThan(visualMemoryBudgetBytes, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumColumns, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maximumColumns, 1024);
        ArgumentOutOfRangeException.ThrowIfLessThan(minimumColumns, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(minimumColumns, maximumColumns);
        ArgumentOutOfRangeException.ThrowIfLessThan(indexBucketSamples, 1);
        var budget = acceptDiagnosticBudget ?? TimeSpan.FromMilliseconds(1);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(budget, TimeSpan.Zero);
        TraceMemoryBudgetBytes = traceMemoryBudgetBytes;
        VisualMemoryBudgetBytes = visualMemoryBudgetBytes;
        MaximumColumns = maximumColumns;
        MinimumColumns = minimumColumns;
        IndexBucketSamples = indexBucketSamples;
        AcceptDiagnosticBudget = budget;
    }

    public long TraceMemoryBudgetBytes { get; }
    public long VisualMemoryBudgetBytes { get; }
    public int MaximumColumns { get; }
    public int MinimumColumns { get; }
    public int IndexBucketSamples { get; }
    public TimeSpan AcceptDiagnosticBudget { get; }
}
