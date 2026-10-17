using BenchmarkDotNet.Attributes;
using UTStudio.Contracts.Inspection;
using UTStudio.Contracts.Processing;
using UTStudio.Domain.Acquisition;
using UTStudio.Domain.Inspection;
using UTStudio.Domain.Processing;
using UTStudio.Processing.Core;

namespace UTStudio.Benchmarks;

/// <summary>Diagnostic Phase 4 smoke only; it does not establish a production baseline.</summary>
[MemoryDiagnoser]
public class GateDerivedStoreBenchmarks
{
    private CompiledGatePlan _plan = null!;
    private GateEvaluator _evaluator = null!;
    private UtStreamIdentity _stream;

    [Params(1, 64)]
    public int BatchCount { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        var gate = new GateDefinition(new GateId("A"), new GateRevision(1),
            new GateWindowDefinition(GateTimeReference.TraceOrigin(), 0, 3d / 1_000_000),
            GateSignalDomain.Absolute, new GateDetectorDefinition(GateDetectorKind.GlobalMaximum),
            [GateMeasureKind.Amplitude, GateMeasureKind.Status, GateMeasureKind.Quality]);
        _plan = new GatePlanCompiler().Compile(new GatePlan(new GatePlanId("derived-benchmark"),
            new GatePlanRevision(1), new GateSchemaVersion(GatePlanCompilerOptions.CurrentSchemaVersion),
            new GateAlgorithmVersion(GatePlanCompilerOptions.CurrentAlgorithmVersion), [gate])).CompiledPlan!;
        _evaluator = new GateEvaluator(new GateScratchPool());
        _stream = new(new InspectionId(Guid.NewGuid()), new AcquisitionRunId(Guid.NewGuid()),
            new UtSourceId("derived-benchmark"), new PhysicalChannelId(0));
    }

    [Benchmark]
    public async Task<ulong> CommitBatches()
    {
        await using var store = new InMemoryGateDerivedStore(new(BatchCount, 1024L * BatchCount));
        for (int index = 0; index < BatchCount; index++)
        {
            var identity = new TraceIdentity(_stream, (ulong)index);
            var descriptor = new TraceDescriptor(identity, (ulong)index, TimeSpan.Zero, null, null,
                4, 1_000_000, 0, UtSignalMode.Rf, sampleEncoding: UtSampleEncoding.SignedInt16,
                provenance: UtTraceProvenance.Simulator, traceQuality: UtTraceQuality.Good);
            var trace = new ConfirmedGateTraceDescriptor(descriptor, identity,
                new InspectionRevision((ulong)index + 1), UtTraceQuality.Good);
            GateEvaluationBatch batch = _evaluator.Evaluate(_plan, trace, [1, 4, 2, 0]).Batch!;
            GateDerivedMutationResult result = await store.CommitAsync(batch);
            if (result.Status != GateDerivedMutationStatus.Committed) throw new InvalidOperationException(result.Status.ToString());
        }
        return store.Revision.Value;
    }
}
