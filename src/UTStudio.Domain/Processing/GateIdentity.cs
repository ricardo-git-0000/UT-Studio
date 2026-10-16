using System.Globalization;
using System.Text;

namespace UTStudio.Domain.Processing;

public readonly record struct GateId
{
    private readonly string? _value;
    public GateId(string value) { ArgumentException.ThrowIfNullOrWhiteSpace(value); _value = value; }
    public string Value => _value ?? string.Empty;
    public bool IsValid => !string.IsNullOrWhiteSpace(_value);
    public override int GetHashCode() => StableHash.Ordinal(Value);
    public override string ToString() => IsValid ? Value : "-";
}

public readonly record struct GatePlanId
{
    private readonly string? _value;
    public GatePlanId(string value) { ArgumentException.ThrowIfNullOrWhiteSpace(value); _value = value; }
    public string Value => _value ?? string.Empty;
    public bool IsValid => !string.IsNullOrWhiteSpace(_value);
    public override int GetHashCode() => StableHash.Ordinal(Value);
    public override string ToString() => IsValid ? Value : "-";
}

public readonly record struct GateRevision
{
    public GateRevision(uint value) { if (value == 0) { throw new ArgumentOutOfRangeException(nameof(value)); } Value = value; }
    public uint Value { get; }
    public bool IsValid => Value > 0;
    public override string ToString() => IsValid ? Value.ToString(CultureInfo.InvariantCulture) : "-";
}

public readonly record struct GatePlanRevision
{
    public GatePlanRevision(uint value) { if (value == 0) { throw new ArgumentOutOfRangeException(nameof(value)); } Value = value; }
    public uint Value { get; }
    public bool IsValid => Value > 0;
    public override string ToString() => IsValid ? Value.ToString(CultureInfo.InvariantCulture) : "-";
}

public readonly record struct GateSchemaVersion
{
    public GateSchemaVersion(ushort value) { if (value == 0) { throw new ArgumentOutOfRangeException(nameof(value)); } Value = value; }
    public ushort Value { get; }
    public bool IsValid => Value > 0;
    public override string ToString() => IsValid ? Value.ToString(CultureInfo.InvariantCulture) : "-";
}

public readonly record struct GateAlgorithmVersion
{
    public GateAlgorithmVersion(ushort value) { if (value == 0) { throw new ArgumentOutOfRangeException(nameof(value)); } Value = value; }
    public ushort Value { get; }
    public bool IsValid => Value > 0;
    public override string ToString() => IsValid ? Value.ToString(CultureInfo.InvariantCulture) : "-";
}

internal static class StableHash
{
    internal static int Ordinal(string value)
    {
        const uint offset = 2166136261;
        const uint prime = 16777619;
        uint hash = offset;
        foreach (byte item in Encoding.UTF8.GetBytes(value)) { hash = (hash ^ item) * prime; }
        return unchecked((int)hash);
    }
}
