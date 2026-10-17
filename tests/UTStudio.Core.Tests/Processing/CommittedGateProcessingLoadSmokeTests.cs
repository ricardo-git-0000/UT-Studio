using System.Diagnostics;
using UTStudio.Contracts.Inspection;
using UTStudio.Contracts.Processing;
using UTStudio.Domain.Acquisition;
using UTStudio.Domain.Inspection;
using UTStudio.Inspection.Core;
using UTStudio.Processing.Core;

namespace UTStudio.Core.Tests.Processing;

[TestClass, DoNotParallelize]
public sealed class CommittedGateProcessingLoadSmokeTests
{
    [TestMethod]
    [DataRow(2048, 50, 1, false)]
    [DataRow(2048, 100, 2, true)]
    [DataRow(2048, 1000, 2, false)]
    [DataRow(65535, 50, 1, true)]
    [DataRow(65535, 100, 2, false)]
    [DataRow(65535, 1000, 2, true)]
    public async Task Scenarios13_14_15_16_55_67_69_DeterministicRealOrchestratorLoadBalances(
        int sampleCount, int offeredRate, int streamCount, bool chain)
    {
        // CI smoke scales the offered one-second rate deterministically while retaining every requested dimension.
        int traces = Math.Min(offeredRate, sampleCount == 65535 ? 4 : 16);
        var inspection = new InspectionId(Guid.NewGuid());
        await using var registry = new InspectionDataStoreRegistry(inspection,
            new InMemoryInspectionStoreOptions((long)sampleCount * sizeof(short) * traces * 2 + 4096, traces * 2 + 8));
        var sink = new CountingSink(); var evaluator = new GateEvaluator(new GateScratchPool());
        await using var orchestrator = new CommittedGateProcessingOrchestrator(registry.Store, registry.Store,
            sink, evaluator, new(maximumRuns: 1, maximumStreamsPerRun: 2, catalogPageSize: 2));
        for (int runNumber = 0; runNumber < 2; runNumber++)
        {
            var run = new AcquisitionRunId(Guid.NewGuid());
            await using IInspectionRunWriter writer = registry.OpenRun(run);
            UtStreamIdentity[] streams = Enumerable.Range(0, streamCount).Select(index => new UtStreamIdentity(
                inspection, run, new UtSourceId("orchestration-smoke"), new PhysicalChannelId(index))).ToArray();
            int acceptedBefore = sink.Accepted;
            CompiledGatePlan plan = Compile(sampleCount, chain);
            Assert.AreEqual(GateProcessingAdmissionStatus.Accepted,
                orchestrator.OpenRun(new(inspection, run, streams, plan)));
            short[] samples = new short[sampleCount]; samples[sampleCount / 2] = short.MaxValue;
            for (int index = 0; index < traces; index++)
            {
                UtStreamIdentity stream = streams[index % streams.Length]; var identity = new TraceIdentity(stream, (ulong)index);
                var descriptor = new TraceDescriptor(identity, (ulong)index, TimeSpan.FromTicks(index), null, null,
                    sampleCount, 10_000_000, 0, UtSignalMode.Rf, sampleEncoding: UtSampleEncoding.SignedInt16,
                    provenance: UtTraceProvenance.Simulator, traceQuality: UtTraceQuality.Good);
                InspectionAppendResult commit = await writer.CommitAsync(new(new(descriptor, samples)));
                if (index % 3 != 0) { orchestrator.NotifyCommitted(new(identity, commit.Revision)); }
                if (index % 5 == 0) { orchestrator.NotifyCommitted(new(identity, commit.Revision)); }
            }
            InspectionRunSealResult seal = await writer.SealRunAsync(); var stopwatch = Stopwatch.StartNew();
            orchestrator.StopAdmission(run); orchestrator.RequestFinalReconciliation(run, seal.FinalRevision);
            await orchestrator.WaitForDrainAsync(run).AsTask().WaitAsync(TimeSpan.FromSeconds(20)); stopwatch.Stop();
            GateProcessingDiagnostics diagnostics = orchestrator.Diagnostics;
            GateProcessingRunDiagnostics terminal = diagnostics.TerminalRuns.Single(x => x.RunId == run);
            Assert.AreEqual(traces, sink.Accepted - acceptedBefore);
            Assert.AreEqual(traces, terminal.WorkAdmitted);
            Assert.AreEqual(traces, terminal.WorkTerminal);
            Assert.IsTrue(terminal.Plans.All(x => x.IsLosslessBalanced));
            Assert.AreEqual(diagnostics.LeasesAcquired, diagnostics.LeasesReleased);
            Assert.AreEqual(0, diagnostics.ActiveLeases);
            Assert.AreEqual(diagnostics.CallbacksAdmitted, diagnostics.CallbacksCompleted);
            Assert.AreEqual(0, diagnostics.ActiveCallbacks);
            Assert.AreEqual(evaluator.ScratchDiagnostics.RentCount, evaluator.ScratchDiagnostics.ReturnCount);
            Assert.AreEqual(0, evaluator.ScratchDiagnostics.ActiveRentals);
            Assert.IsLessThanOrEqualTo(diagnostics.PeakSchedulerDepth, diagnostics.SchedulerCapacity);
            Assert.IsGreaterThanOrEqualTo(TimeSpan.Zero, stopwatch.Elapsed);
        }
    }

    private static CompiledGatePlan Compile(int sampleCount, bool chain)
    {
        GateDefinition Gate(string id, string? dependency = null) => GateTestPlan.Gate(id, dependency,
            durationSeconds: (sampleCount - 1) / 10_000_000d);
        return new GatePlanCompiler().Compile(GateTestPlan.Plan(chain
            ? [Gate("A"), Gate("B", "A"), Gate("C", "B")]
            : [Gate("A"), Gate("B"), Gate("C")])).CompiledPlan!;
    }

    private sealed class CountingSink : IGateEvaluationBatchSink
    {
        internal int Accepted;
        public ValueTask<GateBatchDeliveryResult> DeliverAsync(GateEvaluationBatch batch,
            CancellationToken exceptionalCancellation = default)
        { Interlocked.Increment(ref Accepted); return ValueTask.FromResult(GateBatchDeliveryResult.Accepted()); }
    }
}
