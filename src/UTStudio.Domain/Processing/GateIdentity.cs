using System.Globalization;
using System.Text;

namespace UTStudio.Domain.Processing;

public readonly record struct GateId
{
    public const int MaximumUtf8Bytes = 128;
    private readonly string? _value;
    public GateId(string value) { Validate(value, MaximumUtf8Bytes); _value = value; }
    public string Value => _value ?? string.Empty;
    public bool IsValid => !string.IsNullOrWhiteSpace(_value);
    public override int GetHashCode() => StableHash.Ordinal(Value);
    public override string ToString() => IsValid ? Value : "-";
    private static void Validate(string value, int maximum) { ArgumentException.ThrowIfNullOrWhiteSpace(value); if (Encoding.UTF8.GetByteCount(value) > maximum) throw new ArgumentOutOfRangeException(nameof(value)); }
}

public readonly record struct GatePlanId
{
    public const int MaximumUtf8Bytes = 128;
    private readonly string? _value;
    public GatePlanId(string value) { ArgumentException.ThrowIfNullOrWhiteSpace(value); if (Encoding.UTF8.GetByteCount(value) > MaximumUtf8Bytes) throw new ArgumentOutOfRangeException(nameof(value)); _value = value; }
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

public readonly record struct DerivedStoreRevision
{
    public DerivedStoreRevision(ulong value) => Value = value;
    public ulong Value { get; }
    public bool IsValid => Value > 0;
    public static DerivedStoreRevision Initial => default;
    public override string ToString() => IsValid ? Value.ToString(CultureInfo.InvariantCulture) : "-";
}

public readonly record struct DerivedViewId
{
    public const int MaximumUtf8Bytes = 128;
    private readonly string? _value;
    public DerivedViewId(string value) { ArgumentException.ThrowIfNullOrWhiteSpace(value); if (Encoding.UTF8.GetByteCount(value) > MaximumUtf8Bytes) throw new ArgumentOutOfRangeException(nameof(value)); _value = value; }
    public string Value => _value ?? string.Empty;
    public bool IsValid => !string.IsNullOrWhiteSpace(_value);
    public override int GetHashCode() => StableHash.Ordinal(Value);
    public override string ToString() => IsValid ? Value : "-";
}

public readonly record struct GateReprocessingJobId
{
    public GateReprocessingJobId(Guid value) { if (value == Guid.Empty) throw new ArgumentException("A non-empty job identifier is required.", nameof(value)); Value = value; }
    public Guid Value { get; }
    public bool IsValid => Value != Guid.Empty;
    public override string ToString() => IsValid ? Value.ToString("D") : "-";
}

public readonly record struct GateReprocessingAttemptId
{
    public GateReprocessingAttemptId(Guid value) { if (value == Guid.Empty) throw new ArgumentException("A non-empty attempt identifier is required.", nameof(value)); Value = value; }
    public Guid Value { get; }
    public bool IsValid => Value != Guid.Empty;
    public override string ToString() => IsValid ? Value.ToString("D") : "-";
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
