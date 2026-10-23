namespace UTStudio.Processing.Core;

public sealed class GatePlanCompilerOptions
{
    public const int DefaultMaximumGateCount = 8;
    public const int AbsoluteMaximumGateCount = 16;
    public const ushort CurrentSchemaVersion = 2;
    public const ushort CurrentAlgorithmVersion = 3;
    public GatePlanCompilerOptions(int maximumGateCount = DefaultMaximumGateCount)
    {
        if (maximumGateCount is < 1 or > AbsoluteMaximumGateCount)
        { throw new ArgumentOutOfRangeException(nameof(maximumGateCount)); }
        MaximumGateCount = maximumGateCount;
    }
    public int MaximumGateCount { get; }
}
