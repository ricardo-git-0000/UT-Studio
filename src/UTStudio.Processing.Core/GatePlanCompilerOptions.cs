namespace UTStudio.Processing.Core;

public sealed class GatePlanCompilerOptions
{
    public const int DefaultMaximumGateCount = 8;
    public const int AbsoluteMaximumGateCount = 16;
    public GatePlanCompilerOptions(int maximumGateCount = DefaultMaximumGateCount)
    {
        if (maximumGateCount is < 1 or > AbsoluteMaximumGateCount)
        { throw new ArgumentOutOfRangeException(nameof(maximumGateCount)); }
        MaximumGateCount = maximumGateCount;
    }
    public int MaximumGateCount { get; }
}
