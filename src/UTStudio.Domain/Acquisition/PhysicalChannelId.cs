namespace UTStudio.Domain.Acquisition;

/// <summary>A nonnegative physical channel identifier, not a channel count. Default is channel zero.</summary>
public readonly record struct PhysicalChannelId
{
    public PhysicalChannelId(int value)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(value);
        Value = value;
    }

    public int Value { get; }
}
