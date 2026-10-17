using BenchmarkDotNet.Attributes;
using UTStudio.Contracts.Inspection;
using UTStudio.Contracts.Processing;
using UTStudio.Domain.Acquisition;
using UTStudio.Domain.Inspection;
using UTStudio.Domain.Processing;
using UTStudio.Inspection.Core;
using UTStudio.Processing.Core;

namespace UTStudio.Benchmarks;

[MemoryDiagnoser]
public class GateOrchestrationBenchmarks
{
    public IEnumerable<object[]> Scenarios =>
    [
        [2048, 50, 1, false], [2048, 100, 4, true], [2048, 1000, 4, false],
        [65535, 50, 1, true], [65535, 100, 4, false], [65535, 1000, 4, true]
    ];

    [ParamsSource(nameof(Scenarios))]
    public object[] Scenario { get; set; } = null!;

    [Benchmark]
    public async Task<ulong> CommitReconcileEvaluateDrain()
    {
        int sampleCount = (int)Scenario[0]; int offered = (int)Scenario[1];
        int streamCount = (int)Scenario[2]; bool chain = (bool)Scenario[3];
        var inspection = new InspectionId(Guid.NewGuid()); var run = new AcquisitionRunId(Guid.NewGuid());
        long budget = checked((long)sampleCount * sizeof(short) * offered + 4096);
        await using var registry = new InspectionDataStoreRegistry(inspection,
            new InMemoryInspectionStoreOptions(budget, offered + 8));
        await using IInspectionRunWriter writer = registry.OpenRun(run);
        UtStreamIdentity[] streams = Enumerable.Range(0, streamCount).Select(index => new UtStreamIdentity(
            inspection, run, new UtSourceId("orchestration-benchmark"), new PhysicalChannelId(index))).ToArray();
        var evaluator = new GateEvaluator(new GateScratchPool()); var sink = new CountingSink();
        await using var orchestrator = new CommittedGateProcessingOrchestrator(registry.Store, registry.Store,
            sink, evaluator, new(catalogPageSize: 16));
        CompiledGatePlan plan = Compile(sampleCount, chain);
        if (orchestrator.OpenRun(new(inspection, run, streams, plan)) != GateProcessingAdmissionStatus.Accepted)
        { throw new InvalidOperationException("Benchmark run was not admitted."); }
        short[] samples = new short[sampleCount]; samples[sampleCount / 2] = short.MaxValue;
        for (int index = 0; index < offered; index++)
        {
            UtStreamIdentity stream = streams[index % streams.Length]; var identity = new TraceIdentity(stream, (ulong)index);
            var descriptor = new TraceDescriptor(identity, (ulong)index, TimeSpan.FromTicks(index), null, null,
                sampleCount, 10_000_000, 0, UtSignalMode.Rf, sampleEncoding: UtSampleEncoding.SignedInt16,
                provenance: UtTraceProvenance.Simulator, traceQuality: UtTraceQuality.Good);
            InspectionAppendResult commit = await writer.CommitAsync(new(new(descriptor, samples)));
            if (index % 7 != 0) { orchestrator.NotifyCommitted(new(identity, commit.Revision)); }
            if (index % 11 == 0) { orchestrator.NotifyCommitted(new(identity, commit.Revision)); }
        }
        InspectionRunSealResult seal = await writer.SealRunAsync();
        orchestrator.StopAdmission(run); orchestrator.RequestFinalReconciliation(run, seal.FinalRevision);
        await orchestrator.WaitForDrainAsync(run);
        GateProcessingDiagnostics diagnostics = orchestrator.Diagnostics;
        GateProcessingRunDiagnostics terminal = diagnostics.TerminalRuns.Single();
        if (!terminal.Plans.All(x => x.IsLosslessBalanced) || sink.Accepted != offered ||
            diagnostics.LeasesAcquired != diagnostics.LeasesReleased || evaluator.ScratchDiagnostics.ActiveRentals != 0)
        { throw new InvalidOperationException("Orchestration benchmark ended unbalanced."); }
        return seal.FinalRevision.Value;
    }

    private static CompiledGatePlan Compile(int sampleCount, bool chain)
    {
        GateDefinition Gate(string id, string? dependency = null) => new(new GateId(id), new GateRevision(1),
            new GateWindowDefinition(dependency is null ? GateTimeReference.TraceOrigin() :
                GateTimeReference.GateEvent(new GateId(dependency)), 0, (sampleCount - 1) / 10_000_000d),
            GateSignalDomain.Absolute, new GateDetectorDefinition(GateDetectorKind.GlobalMaximum),
            [GateMeasureKind.Amplitude, GateMeasureKind.Status]);
        GateDefinition[] gates = chain ? [Gate("A"), Gate("B", "A"), Gate("C", "B")] : [Gate("A"), Gate("B"), Gate("C")];
        return new GatePlanCompiler().Compile(new GatePlan(new GatePlanId(chain ? "chain" : "independent"),
            new GatePlanRevision(1), new GateSchemaVersion(GatePlanCompilerOptions.CurrentSchemaVersion),
            new GateAlgorithmVersion(GatePlanCompilerOptions.CurrentAlgorithmVersion), gates)).CompiledPlan!;
    }

    private sealed class CountingSink : IGateEvaluationBatchSink
    {
        internal int Accepted;
        public ValueTask<GateBatchDeliveryResult> DeliverAsync(GateEvaluationBatch batch,
            CancellationToken exceptionalCancellation = default)
        { Interlocked.Increment(ref Accepted); return ValueTask.FromResult(GateBatchDeliveryResult.Accepted()); }
    }
}
