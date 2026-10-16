using BenchmarkDotNet.Attributes;
using UTStudio.Contracts.Inspection;
using UTStudio.Contracts.Processing;
using UTStudio.Domain.Acquisition;
using UTStudio.Domain.Inspection;
using UTStudio.Domain.Processing;
using UTStudio.Processing.Core;

namespace UTStudio.Benchmarks;

[MemoryDiagnoser]
public class GateProcessingBenchmarks
{
    [Params(2048, 65535)] public int SampleCount { get; set; }
    private const double Rate = 10_000_000;
    private short[] _samples = null!;
    private short[] _crossingSamples = null!;
    private short[] _localSamples = null!;
    private short[] _adversarialSamples = null!;
    private ConfirmedGateTraceDescriptor _trace = null!;
    private GateEvaluator _evaluator = null!;
    private CompiledGatePlan _independent = null!;
    private CompiledGatePlan _chain = null!;
    private CompiledGatePlan _fanOut = null!;
    private CompiledGatePlan _crossings = null!;
    private CompiledGatePlan _locals = null!;
    private CompiledGatePlan _adversarialLocals = null!;

    [GlobalSetup]
    public void Setup()
    {
        _samples = new short[SampleCount];
        _samples[0] = short.MaxValue;
        _crossingSamples = new short[SampleCount];
        for (int i = 0; i < _crossingSamples.Length; i++) { _crossingSamples[i] = (short)(i % 2 == 0 ? -20_000 : 20_000); }
        _localSamples = new short[SampleCount];
        _localSamples[SampleCount / 2] = short.MaxValue;
        _adversarialSamples = new short[SampleCount];
        for (int i = 0; i < _adversarialSamples.Length; i++)
        { _adversarialSamples[i] = (short)(i % 4 is 1 or 2 ? 20_000 - (i % 1024) : 0); }
        var identity = new TraceIdentity(new UtStreamIdentity(
            new InspectionId(Guid.Parse("10000000-0000-0000-0000-000000000001")),
            new AcquisitionRunId(Guid.Parse("20000000-0000-0000-0000-000000000002")),
            new UtSourceId("gate-benchmark"), new PhysicalChannelId(0)), 0);
        var descriptor = new TraceDescriptor(identity, 0, TimeSpan.Zero, null, null, SampleCount, Rate, 0,
            UtSignalMode.Rf, sampleEncoding: UtSampleEncoding.SignedInt16,
            provenance: UtTraceProvenance.Simulator, traceQuality: UtTraceQuality.Good);
        _trace = new(descriptor, identity, new InspectionRevision(1), UtTraceQuality.Good);
        _evaluator = new();
        _independent = Compile(Gate("A"), Gate("B"), Gate("C"));
        _chain = Compile(Gate("A"), Gate("B", "A"), Gate("C", "B"));
        _fanOut = Compile(Gate("A"), Gate("B", "A"), Gate("C", "A"));
        _crossings = Compile(Crossing("A"), Crossing("B"), Crossing("C"));
        _locals = Compile(Local("A"), Local("B"), Local("C"));
        _adversarialLocals = Compile(Local("A"), Local("B"), Local("C"));
    }

    [Benchmark] public GateEvaluationStatus ThreeIndependent() => Evaluate(_independent);
    [Benchmark] public GateEvaluationStatus Chain() => Evaluate(_chain);
    [Benchmark] public GateEvaluationStatus FanOut() => Evaluate(_fanOut);
    [Benchmark] public GateEvaluationStatus Crossings() => Evaluate(_crossings, _crossingSamples);
    [Benchmark] public GateEvaluationStatus LocalMaxima() => Evaluate(_locals, _localSamples);
    [Benchmark] public GateEvaluationStatus AdversarialLocalMaxima() => Evaluate(_adversarialLocals, _adversarialSamples);

    [IterationCleanup]
    public void VerifyScratchBalance()
    {
        GateScratchDiagnostics diagnostics = _evaluator.ScratchDiagnostics;
        if (diagnostics.ActiveRentals != 0 || diagnostics.RentCount != diagnostics.ReturnCount ||
            diagnostics.RentedElements != diagnostics.ReturnedElements)
        { throw new InvalidOperationException("Scratch ownership is not balanced."); }
    }

    private GateEvaluationStatus Evaluate(CompiledGatePlan plan) => Evaluate(plan, _samples);
    private GateEvaluationStatus Evaluate(CompiledGatePlan plan, short[] samples) =>
        _evaluator.Evaluate(plan, _trace, samples).Status;
    private GateDefinition Gate(string id, string? dependency = null) => Definition(id, dependency,
        GateDetectorKind.GlobalMaximum, new GateDetectorDefinition(GateDetectorKind.GlobalMaximum));
    private GateDefinition Crossing(string id) => Definition(id, null, GateDetectorKind.FirstRisingCrossing,
        new GateDetectorDefinition(GateDetectorKind.FirstRisingCrossing, new GateThreshold(.5)));
    private GateDefinition Local(string id) => Definition(id, null, GateDetectorKind.FirstLocalMaximum,
        new GateDetectorDefinition(GateDetectorKind.FirstLocalMaximum, localExtrema: new(1, 1, 1)));
    private GateDefinition Definition(string id, string? dependency, GateDetectorKind _, GateDetectorDefinition detector) =>
        new(new GateId(id), new GateRevision(1), new GateWindowDefinition(dependency is null
            ? GateTimeReference.TraceOrigin() : GateTimeReference.GateEvent(new GateId(dependency)), 0, SampleCount / Rate),
            GateSignalDomain.RawSigned, detector, [GateMeasureKind.Amplitude, GateMeasureKind.Status]);
    private static CompiledGatePlan Compile(params GateDefinition[] gates) => new GatePlanCompiler(new(16)).Compile(
        new GatePlan(new GatePlanId("benchmark"), new GatePlanRevision(1),
            new GateSchemaVersion(GatePlanCompilerOptions.CurrentSchemaVersion),
            new GateAlgorithmVersion(GatePlanCompilerOptions.CurrentAlgorithmVersion), gates)).CompiledPlan!;
}
