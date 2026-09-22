namespace UTStudio.Domain.Acquisition;

/// <summary>Physical unit of an optional spatial acquisition coordinate.</summary>
public enum ScanSpatialUnit
{
    Metres = 1,
    Millimetres = 2
}

/// <summary>A stable physical axis identifier; it is not a channel, element, beam or focal law.</summary>
public readonly record struct ScanAxisId
{
    private readonly string? _value;

    public ScanAxisId(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        _value = value;
    }

    public string Value => _value ?? string.Empty;
    public bool IsValid => !string.IsNullOrWhiteSpace(_value);
    public override string ToString() => Value;
}

/// <summary>Minimal physical coordinate carried atomically with one acquired frame.</summary>
public readonly record struct ScanSpatialCoordinate
{
    public ScanSpatialCoordinate(double value, ScanSpatialUnit unit, ScanAxisId axisId)
    {
        if (!double.IsFinite(value)) { throw new ArgumentOutOfRangeException(nameof(value)); }
        if (!Enum.IsDefined(unit)) { throw new ArgumentOutOfRangeException(nameof(unit)); }
        if (!axisId.IsValid) { throw new ArgumentException("A valid spatial axis is required.", nameof(axisId)); }
        Value = value;
        Unit = unit;
        AxisId = axisId;
    }

    public double Value { get; }
    public ScanSpatialUnit Unit { get; }
    public ScanAxisId AxisId { get; }
    public bool IsValid => double.IsFinite(Value) && Enum.IsDefined(Unit) && AxisId.IsValid;
}
