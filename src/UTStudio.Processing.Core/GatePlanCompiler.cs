using System.Security.Cryptography;
using System.Text;
using UTStudio.Contracts.Processing;
using UTStudio.Domain.Processing;

namespace UTStudio.Processing.Core;

public sealed class GatePlanCompiler
{
    private readonly GatePlanCompilerOptions _options;
    public GatePlanCompiler(GatePlanCompilerOptions? options = null) =>
        _options = options ?? new GatePlanCompilerOptions();

    public GatePlanCompilationResult Compile(GatePlan? plan, CancellationToken cancellationToken = default)
    {
        try
        {
            if (cancellationToken.IsCancellationRequested) { return Cancelled(); }
            List<GateCompilationError> errors = [];
            if (plan is null)
            { return Failed(GatePlanCompilationStatus.InvalidPlan, GateCompilationErrorCode.InvalidIdentity, "A gate plan is required."); }

            ValidateHeader(plan, errors);
            if (plan.Gates.Count == 0)
            { errors.Add(Error(GateCompilationErrorCode.EmptyPlan, "A plan must contain at least one gate.")); }
            if (plan.Gates.Count > _options.MaximumGateCount || plan.Gates.Count > GatePlanCompilerOptions.AbsoluteMaximumGateCount)
            { errors.Add(Error(GateCompilationErrorCode.CapacityExceeded, $"The plan contains {plan.Gates.Count} gates; the configured maximum is {_options.MaximumGateCount}.")); }

            Dictionary<GateId, int> indices = new();
            for (int i = 0; i < plan.Gates.Count; i++)
            {
                if (cancellationToken.IsCancellationRequested) { return Cancelled(); }
                GateDefinition? gate = plan.Gates[i];
                if (gate is null)
                { errors.Add(Error(GateCompilationErrorCode.InvalidIdentity, $"Gate at declared index {i} is null.")); continue; }
                if (!gate.Id.IsValid)
                { errors.Add(Error(GateCompilationErrorCode.InvalidIdentity, $"Gate at declared index {i} has an invalid identifier.")); }
                else if (!indices.TryAdd(gate.Id, i))
                { errors.Add(Error(GateCompilationErrorCode.DuplicateGate, $"Gate '{gate.Id}' is declared more than once.", gate.Id)); }
                ValidateGate(gate, errors);
            }

            int[] dependencies = Enumerable.Repeat(-1, plan.Gates.Count).ToArray();
            for (int i = 0; i < plan.Gates.Count; i++)
            {
                GateDefinition? gate = plan.Gates[i];
                if (gate is null || gate.Window.StartReference.Kind != GateTimeReferenceKind.GateEvent) { continue; }
                GateId dependency = gate.Window.StartReference.DependencyGateId;
                if (dependency == gate.Id)
                { errors.Add(Error(GateCompilationErrorCode.SelfDependency, $"Gate '{gate.Id}' cannot depend on itself.", gate.Id, dependency)); }
                else if (!indices.TryGetValue(dependency, out int dependencyIndex))
                { errors.Add(Error(GateCompilationErrorCode.MissingDependency, $"Gate '{gate.Id}' depends on missing gate '{dependency}'.", gate.Id, dependency)); }
                else { dependencies[i] = dependencyIndex; }
            }

            if (errors.Count != 0) { return FailFor(errors); }
            int[] order = TopologicalOrder(dependencies);
            if (order.Length != plan.Gates.Count)
            {
                foreach (GateId[] cycle in FindCycles(plan, dependencies))
                {
                    errors.Add(new(GateCompilationErrorCode.CycleDetected,
                        $"Gate dependency cycle detected: {string.Join(" -> ", cycle)}.",
                        cycle[0], cycle.Length > 1 ? cycle[1] : null, cycle));
                }
                return GatePlanCompilationResult.Failed(GatePlanCompilationStatus.CycleDetected, errors);
            }

            if (cancellationToken.IsCancellationRequested) { return Cancelled(); }
            GatePlanSignature signature = CreateSignature(plan);
            CompiledGate[] compiled = new CompiledGate[plan.Gates.Count];
            for (int i = 0; i < compiled.Length; i++) { compiled[i] = new(plan.Gates[i], i, dependencies[i]); }
            if (cancellationToken.IsCancellationRequested) { return Cancelled(); }
            var published = new CompiledGatePlan(plan, compiled, order, signature);
            return GatePlanCompilationResult.Compiled(published);
        }
        catch (OperationCanceledException) { return Cancelled(); }
        catch (Exception error)
        {
            return Failed(GatePlanCompilationStatus.Faulted, GateCompilationErrorCode.InternalFailure,
                $"Gate plan compilation failed: {error.GetType().Name}.");
        }
    }

    private static void ValidateHeader(GatePlan plan, List<GateCompilationError> errors)
    {
        if (!plan.Id.IsValid) { errors.Add(Error(GateCompilationErrorCode.InvalidIdentity, "GatePlanId is invalid.")); }
        if (!plan.Revision.IsValid) { errors.Add(Error(GateCompilationErrorCode.InvalidRevision, "GatePlanRevision is invalid.")); }
        if (plan.SchemaVersion.Value != GatePlanCompilerOptions.CurrentSchemaVersion)
        { errors.Add(Error(GateCompilationErrorCode.InvalidVersion, $"GateSchemaVersion must be {GatePlanCompilerOptions.CurrentSchemaVersion}.")); }
        if (plan.AlgorithmVersion.Value != GatePlanCompilerOptions.CurrentAlgorithmVersion)
        { errors.Add(Error(GateCompilationErrorCode.InvalidVersion, $"GateAlgorithmVersion must be {GatePlanCompilerOptions.CurrentAlgorithmVersion}.")); }
    }

    private static void ValidateGate(GateDefinition gate, List<GateCompilationError> errors)
    {
        if (!gate.Revision.IsValid) { errors.Add(Error(GateCompilationErrorCode.InvalidRevision, "GateRevision is invalid.", gate.Id)); }
        if (!Enum.IsDefined(gate.SignalDomain) || gate.SignalDomain == GateSignalDomain.Unknown)
        { errors.Add(Error(GateCompilationErrorCode.UnsupportedCombination, "Signal metadata is unknown.", gate.Id)); }
        ValidateWindow(gate, errors);
        ValidateDetector(gate, errors);
        if (gate.RequestedMeasures.Count == 0 || gate.RequestedMeasures.Any(x => !Enum.IsDefined(x) || x == GateMeasureKind.Unknown) ||
            gate.RequestedMeasures.Distinct().Count() != gate.RequestedMeasures.Count)
        { errors.Add(Error(GateCompilationErrorCode.InvalidMeasure, "Requested measures must be nonempty, known, and unique.", gate.Id)); }
    }

    private static void ValidateWindow(GateDefinition gate, List<GateCompilationError> errors)
    {
        GateWindowDefinition window = gate.Window;
        if (!Enum.IsDefined(window.StartReference.Kind) || window.StartReference.Kind == GateTimeReferenceKind.Unknown ||
            (window.StartReference.Kind == GateTimeReferenceKind.TraceOrigin && window.StartReference.DependencyGateId.IsValid) ||
            (window.StartReference.Kind == GateTimeReferenceKind.GateEvent && !window.StartReference.DependencyGateId.IsValid))
        { errors.Add(Error(GateCompilationErrorCode.UnsupportedCombination, "The time reference is invalid.", gate.Id)); }
        if (!double.IsFinite(window.DurationSeconds) || window.DurationSeconds <= 0)
        { errors.Add(Error(GateCompilationErrorCode.InvalidDuration, "Gate duration must be positive.", gate.Id)); return; }
        if (!double.IsFinite(window.StartOffsetSeconds) || !double.IsFinite(window.StartOffsetSeconds + window.DurationSeconds))
        { errors.Add(Error(GateCompilationErrorCode.TemporalOverflow, "Gate start offset plus duration is not finite.", gate.Id)); }
    }

    private static void ValidateDetector(GateDefinition gate, List<GateCompilationError> errors)
    {
        GateDetectorDefinition detector = gate.Detector;
        if (!Enum.IsDefined(detector.Kind) || detector.Kind == GateDetectorKind.Unknown)
        { errors.Add(Error(GateCompilationErrorCode.UnsupportedCombination, "Detector is unknown.", gate.Id)); return; }

        bool crossing = detector.Kind is GateDetectorKind.FirstRisingCrossing or GateDetectorKind.LastRisingCrossing or
            GateDetectorKind.FirstFallingCrossing or GateDetectorKind.LastFallingCrossing;
        bool local = detector.Kind is GateDetectorKind.FirstLocalMaximum or GateDetectorKind.LastLocalMaximum;

        if (detector.Threshold is { } threshold && !UnitInterval(threshold.NormalizedValue))
        { errors.Add(Error(GateCompilationErrorCode.InvalidThreshold, "Threshold must be finite and in [0,1].", gate.Id)); }
        if (crossing && detector.Threshold is null && detector.Hysteresis is null)
        { errors.Add(Error(GateCompilationErrorCode.UnsupportedCombination, "Crossing detectors require a threshold or hysteresis.", gate.Id)); }
        if (crossing && detector.Threshold is not null && detector.Hysteresis is not null)
        { errors.Add(Error(GateCompilationErrorCode.UnsupportedCombination, "Crossing detectors use either a threshold or hysteresis, not both.", gate.Id)); }
        if (detector.Hysteresis is { } hysteresis && (!UnitInterval(hysteresis.LowNormalized) ||
            !UnitInterval(hysteresis.HighNormalized) || hysteresis.LowNormalized > hysteresis.HighNormalized))
        { errors.Add(Error(GateCompilationErrorCode.InvalidHysteresis, "Hysteresis must satisfy 0 <= low <= high <= 1.", gate.Id)); }
        if (!crossing && detector.Hysteresis is not null)
        { errors.Add(Error(GateCompilationErrorCode.UnsupportedCombination, "Hysteresis is supported only by crossing detectors.", gate.Id)); }

        if (local)
        {
            GateLocalExtremaParameters? value = detector.LocalExtrema;
            if (value is null || value.NeighborhoodRadiusSamples < 1 || value.MinimumDistanceSamples < 1 ||
                !double.IsFinite(value.MinimumProminenceCounts) || value.MinimumProminenceCounts < 0 ||
                value.PlateauPolicy != GatePlateauPolicy.LowerCenter)
            { errors.Add(Error(GateCompilationErrorCode.InvalidLocalExtremaParameters, "Local maxima require radius and distance >= 1, finite nonnegative prominence counts, and LowerCenter plateaus.", gate.Id)); }
        }
        else if (detector.LocalExtrema is not null)
        { errors.Add(Error(GateCompilationErrorCode.UnsupportedCombination, "Local extrema parameters are supported only by local-maximum detectors.", gate.Id)); }

    }

    private static bool UnitInterval(double value) => double.IsFinite(value) && value is >= 0 and <= 1;

    private static int[] TopologicalOrder(int[] dependencies)
    {
        int[] indegree = dependencies.Select(x => x < 0 ? 0 : 1).ToArray();
        List<int> order = [];
        while (order.Count < dependencies.Length)
        {
            int ready = -1;
            for (int i = 0; i < indegree.Length; i++)
            { if (indegree[i] == 0 && !order.Contains(i)) { ready = i; break; } }
            if (ready < 0) { break; }
            order.Add(ready);
            for (int i = 0; i < dependencies.Length; i++) { if (dependencies[i] == ready) { indegree[i]--; } }
        }
        return order.ToArray();
    }

    private static IEnumerable<GateId[]> FindCycles(GatePlan plan, int[] dependencies)
    {
        bool[] visited = new bool[dependencies.Length];
        for (int start = 0; start < dependencies.Length; start++)
        {
            if (visited[start]) { continue; }
            Dictionary<int, int> positions = [];
            List<int> path = [];
            int current = start;
            while (current >= 0 && !visited[current])
            {
                if (positions.TryGetValue(current, out int cycleStart))
                {
                    int[] cycle = path.Skip(cycleStart).ToArray();
                    foreach (int item in cycle) { visited[item] = true; }
                    yield return cycle.Select(i => plan.Gates[i].Id).Append(plan.Gates[cycle[0]].Id).ToArray();
                    break;
                }
                positions[current] = path.Count; path.Add(current); current = dependencies[current];
            }
            foreach (int item in path) { visited[item] = true; }
        }
    }

    private static GatePlanSignature CreateSignature(GatePlan plan)
    {
        using MemoryStream stream = new();
        using (BinaryWriter writer = new(stream, new UTF8Encoding(false, true), true))
        {
            Write(writer, "UTSTUDIO-GATE-PLAN"); writer.Write((byte)2);
            Write(writer, plan.Id.Value); writer.Write(plan.Revision.Value);
            writer.Write(plan.SchemaVersion.Value); writer.Write(plan.AlgorithmVersion.Value);
            writer.Write(plan.Gates.Count);
            foreach (GateDefinition gate in plan.Gates)
            {
                Write(writer, gate.Id.Value); writer.Write(gate.Revision.Value);
                writer.Write((int)gate.Window.StartReference.Kind);
                Write(writer, gate.Window.StartReference.DependencyGateId.Value);
                writer.Write(CanonicalDoubleBits(gate.Window.StartOffsetSeconds));
                writer.Write(CanonicalDoubleBits(gate.Window.DurationSeconds));
                writer.Write((int)gate.SignalDomain); writer.Write((int)gate.Detector.Kind);
                WriteOptional(writer, gate.Detector.Threshold?.NormalizedValue);
                WriteOptional(writer, gate.Detector.Hysteresis?.LowNormalized);
                WriteOptional(writer, gate.Detector.Hysteresis?.HighNormalized);
                writer.Write(gate.Detector.LocalExtrema is not null);
                if (gate.Detector.LocalExtrema is { } local)
                {
                    writer.Write(local.NeighborhoodRadiusSamples);
                    writer.Write(CanonicalDoubleBits(local.MinimumProminenceCounts));
                    writer.Write(local.MinimumDistanceSamples); writer.Write((int)local.PlateauPolicy);
                }
                writer.Write(gate.RequestedMeasures.Count);
                foreach (GateMeasureKind measure in gate.RequestedMeasures) { writer.Write((int)measure); }
            }
        }
        return new(SHA256.HashData(stream.GetBuffer().AsSpan(0, checked((int)stream.Length))));
    }

    private static void Write(BinaryWriter writer, string value)
    { byte[] bytes = Encoding.UTF8.GetBytes(value); writer.Write(bytes.Length); writer.Write(bytes); }
    private static void WriteOptional(BinaryWriter writer, double? value)
    { writer.Write(value.HasValue); if (value.HasValue) { writer.Write(CanonicalDoubleBits(value.Value)); } }
    private static long CanonicalDoubleBits(double value) => BitConverter.DoubleToInt64Bits(value == 0d ? 0d : value);

    private static GatePlanCompilationResult FailFor(List<GateCompilationError> errors)
    {
        GatePlanCompilationStatus status = errors.Any(x => x.Code == GateCompilationErrorCode.DuplicateGate) ? GatePlanCompilationStatus.DuplicateGate :
            errors.Any(x => x.Code == GateCompilationErrorCode.MissingDependency) ? GatePlanCompilationStatus.MissingDependency :
            errors.Any(x => x.Code == GateCompilationErrorCode.SelfDependency) ? GatePlanCompilationStatus.SelfDependency :
            errors.Any(x => x.Code == GateCompilationErrorCode.CapacityExceeded) ? GatePlanCompilationStatus.CapacityExceeded :
            errors.Any(x => x.Code is GateCompilationErrorCode.UnsupportedCombination or GateCompilationErrorCode.InvalidHysteresis or
                GateCompilationErrorCode.InvalidThreshold or GateCompilationErrorCode.InvalidLocalExtremaParameters) ? GatePlanCompilationStatus.UnsupportedCombination :
            GatePlanCompilationStatus.InvalidPlan;
        return GatePlanCompilationResult.Failed(status, errors);
    }

    private static GateCompilationError Error(GateCompilationErrorCode code, string message,
        GateId? gate = null, GateId? dependency = null) => new(code, message, gate, dependency);
    private static GatePlanCompilationResult Failed(GatePlanCompilationStatus status,
        GateCompilationErrorCode code, string message) => GatePlanCompilationResult.Failed(status, [Error(code, message)]);
    private static GatePlanCompilationResult Cancelled() => Failed(GatePlanCompilationStatus.Cancelled,
        GateCompilationErrorCode.Cancelled, "Gate plan compilation was cancelled before publication.");
}
