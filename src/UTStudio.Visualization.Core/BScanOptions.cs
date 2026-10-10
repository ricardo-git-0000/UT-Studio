namespace UTStudio.Visualization.Core;

/// <summary>Bounded projection options for one live B-Scan run.</summary>
public sealed class BScanOptions
{
    public const long MaximumVisualMemoryBudgetBytes = checked(16L * 1024 * 1024);

    public BScanOptions(
        long visualMemoryBudgetBytes = MaximumVisualMemoryBudgetBytes,
        int maximumColumns = 1024,
        int maximumRasterConsumers = 2,
        int maximumRasterOwnersPerConsumer = 2)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(visualMemoryBudgetBytes, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(
            visualMemoryBudgetBytes, MaximumVisualMemoryBudgetBytes);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumColumns, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maximumColumns, 1024);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumRasterConsumers, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumRasterOwnersPerConsumer, 1);
        VisualMemoryBudgetBytes = visualMemoryBudgetBytes;
        MaximumColumns = maximumColumns;
        MaximumRasterConsumers = maximumRasterConsumers;
        MaximumRasterOwnersPerConsumer = maximumRasterOwnersPerConsumer;
    }

    public long VisualMemoryBudgetBytes { get; }
    public int MaximumColumns { get; }
    public int MaximumRasterConsumers { get; }
    public int MaximumRasterOwnersPerConsumer { get; }
}
