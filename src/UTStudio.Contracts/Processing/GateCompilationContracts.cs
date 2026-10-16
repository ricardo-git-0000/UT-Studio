using System.Collections.ObjectModel;
using UTStudio.Domain.Processing;

namespace UTStudio.Contracts.Processing;

public sealed class GateCompilationError
{
    private readonly ReadOnlyCollection<GateId> _cyclePath;
    public GateCompilationError(GateCompilationErrorCode code, string message, GateId? gateId = null,
        GateId? dependencyGateId = null, IEnumerable<GateId>? cyclePath = null)
    {
        if (!Enum.IsDefined(code) || code == GateCompilationErrorCode.Unknown) { throw new ArgumentOutOfRangeException(nameof(code)); }
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        Code = code; Message = message; GateId = gateId; DependencyGateId = dependencyGateId;
        _cyclePath = Array.AsReadOnly((cyclePath ?? []).ToArray());
    }
    public GateCompilationErrorCode Code { get; }
    public GateId? GateId { get; }
    public GateId? DependencyGateId { get; }
    public IReadOnlyList<GateId> CyclePath => _cyclePath;
    public string Message { get; }
}

public sealed class GatePlanSignature : IEquatable<GatePlanSignature>
{
    private readonly byte[] _bytes;
    internal GatePlanSignature(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length != 32) { throw new ArgumentException("A gate plan signature must contain 32 bytes.", nameof(bytes)); }
        _bytes = bytes.ToArray();
    }
    public ReadOnlyMemory<byte> Bytes => _bytes.ToArray();
    public string Hex => Convert.ToHexString(_bytes);
    public bool Equals(GatePlanSignature? other) => other is not null && _bytes.AsSpan().SequenceEqual(other._bytes);
    public override bool Equals(object? obj) => Equals(obj as GatePlanSignature);
    public override int GetHashCode() => BitConverter.ToInt32(_bytes, 0);
    public override string ToString() => Hex;
}

public sealed class CompiledGate
{
    private readonly ReadOnlyCollection<GateMeasureKind> _measures;
    internal CompiledGate(GateDefinition definition, int declaredIndex, int dependencyIndex)
    {
        ArgumentNullException.ThrowIfNull(definition);
        Definition = definition; DeclaredIndex = declaredIndex; DependencyIndex = dependencyIndex;
        _measures = Array.AsReadOnly(definition.RequestedMeasures.ToArray());
    }
    public GateDefinition Definition { get; }
    public int DeclaredIndex { get; }
    public int DependencyIndex { get; }
    public IReadOnlyList<GateMeasureKind> RequestedMeasures => _measures;
}

public sealed class CompiledGatePlan
{
    private readonly ReadOnlyCollection<CompiledGate> _declared;
    private readonly ReadOnlyCollection<int> _topologicalOrder;
    internal CompiledGatePlan(GatePlan source, IEnumerable<CompiledGate> declaredGates,
        IEnumerable<int> topologicalOrder, GatePlanSignature signature)
    {
        ArgumentNullException.ThrowIfNull(source); ArgumentNullException.ThrowIfNull(declaredGates);
        ArgumentNullException.ThrowIfNull(topologicalOrder); ArgumentNullException.ThrowIfNull(signature);
        Source = source; Signature = signature;
        _declared = Array.AsReadOnly(declaredGates.ToArray());
        _topologicalOrder = Array.AsReadOnly(topologicalOrder.ToArray());
    }
    public GatePlan Source { get; }
    public IReadOnlyList<CompiledGate> DeclaredGates => _declared;
    public IReadOnlyList<int> TopologicalOrder => _topologicalOrder;
    public GatePlanSignature Signature { get; }
}

public sealed class GatePlanCompilationResult
{
    private readonly ReadOnlyCollection<GateCompilationError> _errors;
    private GatePlanCompilationResult(GatePlanCompilationStatus status, CompiledGatePlan? plan,
        IEnumerable<GateCompilationError> errors)
    { Status = status; CompiledPlan = plan; _errors = Array.AsReadOnly(errors.ToArray()); }
    public GatePlanCompilationStatus Status { get; }
    public CompiledGatePlan? CompiledPlan { get; }
    public IReadOnlyList<GateCompilationError> Errors => _errors;
    public bool IsCompiled => Status == GatePlanCompilationStatus.Compiled && CompiledPlan is not null;
    internal static GatePlanCompilationResult Compiled(CompiledGatePlan plan) =>
        new(GatePlanCompilationStatus.Compiled, plan ?? throw new ArgumentNullException(nameof(plan)), []);
    public static GatePlanCompilationResult Failed(GatePlanCompilationStatus status,
        IEnumerable<GateCompilationError> errors)
    {
        if (!Enum.IsDefined(status) || status is GatePlanCompilationStatus.Unknown or GatePlanCompilationStatus.Compiled)
        { throw new ArgumentOutOfRangeException(nameof(status)); }
        ArgumentNullException.ThrowIfNull(errors);
        GateCompilationError[] captured = errors.ToArray();
        if (captured.Length == 0) { throw new ArgumentException("A failed compilation requires an error.", nameof(errors)); }
        return new(status, null, captured);
    }
}
