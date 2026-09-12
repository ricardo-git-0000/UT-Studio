namespace UTStudio.Domain.Acquisition;

/// <summary>A stable, ordinal, case-sensitive logical source identifier.</summary>
/// <remarks>The default value is invalid and must be rejected at aggregate boundaries.</remarks>
public readonly record struct UtSourceId
{
    private readonly string? _value;

    public UtSourceId(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        _value = value;
    }

    public string Value => _value ?? string.Empty;
    public bool IsValid => !string.IsNullOrWhiteSpace(_value);
    public override string ToString() => Value;
}
