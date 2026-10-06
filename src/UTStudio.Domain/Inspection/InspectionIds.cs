namespace UTStudio.Domain.Inspection;

public readonly record struct InspectionId
{
    public InspectionId(Guid value)
    {
        if (value == Guid.Empty) { throw new ArgumentException("An inspection identifier cannot be empty.", nameof(value)); }
        Value = value;
    }

    public Guid Value { get; }
    public bool IsValid => Value != Guid.Empty;
    public override string ToString() => Value.ToString("D");
}

/// <summary>A nonnegative beam identifier. Zero is valid; absence is represented by null.</summary>
public readonly record struct BeamId
{
    private readonly long _encodedValue;

    public BeamId(int value)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(value);
        _encodedValue = (long)value + 1;
    }

    public int Value => IsValid
        ? checked((int)(_encodedValue - 1))
        : throw new InvalidOperationException("The beam identifier is not initialized.");
    public bool IsValid => _encodedValue != 0;
    public override string ToString() => IsValid
        ? Value.ToString(System.Globalization.CultureInfo.InvariantCulture)
        : "-";
}

/// <summary>A nonnegative focal-law identifier. Zero is valid; absence is represented by null.</summary>
public readonly record struct FocalLawId
{
    private readonly long _encodedValue;

    public FocalLawId(int value)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(value);
        _encodedValue = (long)value + 1;
    }

    public int Value => IsValid
        ? checked((int)(_encodedValue - 1))
        : throw new InvalidOperationException("The focal-law identifier is not initialized.");
    public bool IsValid => _encodedValue != 0;
    public override string ToString() => IsValid
        ? Value.ToString(System.Globalization.CultureInfo.InvariantCulture)
        : "-";
}
