using UTStudio.Contracts.Inspection;
using UTStudio.Contracts.Processing;
using UTStudio.Domain.Acquisition;
using UTStudio.Domain.Inspection;
using UTStudio.Domain.Processing;
using UTStudio.Inspection.Core;
using UTStudio.Processing.Core;

namespace UTStudio.Core.Tests.Processing;

[TestClass]
public sealed class GateLiveReprocessingDifferentialTests
{
    [TestMethod]
    public async Task AllDetectorDomainQualityAndOutcomePairsAreContractuallyIdentical()
    {
        int comparisons = 0;
        foreach (GateDetectorKind detector in Enum.GetValues<GateDetectorKind>().Where(x => x != GateDetectorKind.Unknown))
            foreach (GateSignalDomain domain in Enum.GetValues<GateSignalDomain>().Where(x => x != GateSignalDomain.Unknown))
            {
                if (detector == GateDetectorKind.AbsoluteMaximum && domain != GateSignalDomain.RawSigned) continue;
                foreach (bool alternate in new[] { false, true })
                    foreach (UtTraceQuality quality in new[] { UtTraceQuality.Good, UtTraceQuality.Degraded, UtTraceQuality.Invalid })
                    {
                        (short[] samples, double? threshold, GateHysteresis? hysteresis,
                            GateLocalExtremaParameters? local) = Case(detector, domain, alternate);
                        GateDefinition gate = GateEvaluationTestData.Gate("A", samples.Length, detector: detector,
                            domain: domain, threshold: threshold, hysteresis: hysteresis, local: local,
                            offsetSeconds: alternate ? .25e-6 : 0,
                            measures: Enum.GetValues<GateMeasureKind>().Where(x => x != GateMeasureKind.Unknown));
                        await AssertLiveEqualsReprocessingAsync(GateEvaluationTestData.Compile(gate), samples, quality,
                            $"{detector}/{domain}/{quality}/{alternate}");
                        comparisons++;
                    }
            }
        Assert.AreEqual(198, comparisons);
    }

    [TestMethod]
    public async Task IndependentChainAndFanOutPlansAreContractuallyIdentical()
    {
        short[] samples = [0, 5, 1, 8, 0, 4, 0, 9, 0];
        GateDefinition a = GateEvaluationTestData.Gate("A", 3, offsetSeconds: .25e-6,
            measures: [GateMeasureKind.Status]);
        GateDefinition bIndependent = GateEvaluationTestData.Gate("B", samples.Length,
            detector: GateDetectorKind.FirstLocalMaximum, local: new(1, 1, 2));
        GateDefinition cIndependent = GateEvaluationTestData.Gate("C", samples.Length,
            detector: GateDetectorKind.LastRisingCrossing, threshold: .5);
        GateDefinition bDependent = GateEvaluationTestData.Gate("B", 3, dependency: "A",
            offsetSeconds: .25e-6, measures: [GateMeasureKind.Status]);
        GateDefinition cFromB = GateEvaluationTestData.Gate("C", 3, dependency: "B", offsetSeconds: .25e-6);
        GateDefinition cFromA = GateEvaluationTestData.Gate("C", 5, dependency: "A", offsetSeconds: .25e-6);

        CompiledGatePlan[] plans =
        [
            GateEvaluationTestData.Compile(a, bIndependent, cIndependent),
            GateEvaluationTestData.Compile(a, bDependent, cFromB),
            GateEvaluationTestData.Compile(a, bDependent, cFromA)
        ];
        string[] names = ["independent", "A-to-B-to-C", "A-to-B-and-C"];
        for (int i = 0; i < plans.Length; i++)
            await AssertLiveEqualsReprocessingAsync(plans[i], samples, UtTraceQuality.Good, names[i]);
    }

    private static async Task AssertLiveEqualsReprocessingAsync(CompiledGatePlan plan, short[] samples,
        UtTraceQuality quality, string context)
    {
        var inspection = new InspectionId(Guid.NewGuid());
        var run = new AcquisitionRunId(Guid.NewGuid());
        var stream = new UtStreamIdentity(inspection, run, new UtSourceId("differential"), new PhysicalChannelId(0));
        var identity = new TraceIdentity(stream, 0);
        await using var rf = new InMemoryInspectionDataStore(inspection,
            new((long)Math.Max(samples.Length, 1) * sizeof(short) + 16_384, 16));
        IInspectionRunWriter writer = await rf.OpenRunAsync(run);
        var sink = new CapturingSink();
        await using var live = new CommittedGateProcessingOrchestrator(rf, rf, sink,
            new GateEvaluator(new GateScratchPool()));
        Assert.AreEqual(GateProcessingAdmissionStatus.Accepted,
            live.OpenRun(new(inspection, run, [stream], plan)), context);
        var descriptor = new TraceDescriptor(identity, sequence: 99, TimeSpan.FromTicks(17),
            DateTimeOffset.UnixEpoch.AddTicks(23), null, samples.Length, 1_000_000, .125e-6,
            UtSignalMode.Rf, sampleEncoding: UtSampleEncoding.SignedInt16,
            provenance: UtTraceProvenance.Simulator, traceQuality: quality);
        InspectionAppendResult commit = await writer.CommitAsync(new(new(descriptor, samples)));
        Assert.AreEqual(InspectionAppendStatus.Committed, commit.Status, context);
        Assert.AreEqual(GateProcessingAdmissionStatus.Accepted,
            live.NotifyCommitted(new(identity, commit.Revision)), context);
        GateEvaluationBatch liveBatch = await sink.Next.Task.WaitAsync(TimeSpan.FromSeconds(5));
        InspectionRunSealResult seal = await writer.SealRunAsync();
        live.StopAdmission(run); live.RequestFinalReconciliation(run, seal.FinalRevision);
        await live.WaitForDrainAsync(run).AsTask().WaitAsync(TimeSpan.FromSeconds(5));

        await using var derived = new InMemoryGateDerivedStore(new(16, 2_000_000));
        await using var scheduler = new GateReprocessingScheduler(rf, derived,
            new(maximumWorkers: 1, maximumAdmittedJobs: 1), new GateScratchPool(1, 300_000));
        var request = new GateReprocessingJobDescriptor(new(Guid.NewGuid()), new(Guid.NewGuid()), inspection, run,
            seal.FinalRevision, [new GateReprocessingStreamSelection(stream, [new(0, 0)])], plan, 1,
            GateReprocessingPublicationPolicy.ActivateOnComplete, new DerivedViewId("differential"), 1);
        IGateReprocessingJob job = (await scheduler.AdmitAsync(request)).Job!;
        GateReprocessingProgressSnapshot terminal = await job.Completion;
        Assert.AreEqual(GateReprocessingLifecycle.Completed, terminal.Lifecycle, context + ":" + terminal.PrimaryError);
        using IGateDerivedReadLease view = derived.OpenActiveView();
        GateEvaluationBatch? historicalBatch = view.ReadExact(Key(liveBatch));
        Assert.IsNotNull(historicalBatch, context);
        AssertBatchEqual(liveBatch, historicalBatch, context);
        Assert.AreEqual(GateDerivedStreamCoverage.ComputeIdentityDigest([identity]),
            derived.ActiveView!.Manifest.Streams.Single().IdentityDigest, context);
    }

    private static void AssertBatchEqual(GateEvaluationBatch expected, GateEvaluationBatch actual, string context)
    {
        Assert.AreEqual(expected.Trace.ConfirmedIdentity, actual.Trace.ConfirmedIdentity, context);
        Assert.AreEqual(expected.Trace.SourceInspectionRevision, actual.Trace.SourceInspectionRevision, context);
        Assert.AreEqual(expected.Trace.EffectiveQuality, actual.Trace.EffectiveQuality, context);
        Assert.AreEqual(expected.Trace.Descriptor, actual.Trace.Descriptor, context);
        Assert.AreEqual(expected.ReproducibilitySignature.Hex, actual.ReproducibilitySignature.Hex, context);
        Assert.HasCount(expected.Entries.Count, actual.Entries, context);
        for (int i = 0; i < expected.Entries.Count; i++) AssertEntryEqual(expected.Entries[i], actual.Entries[i], context);
        Assert.AreEqual(Key(expected), Key(actual), context);
    }

    private static void AssertEntryEqual(GateEvaluationEntry expected, GateEvaluationEntry actual, string context)
    {
        Assert.AreEqual(expected.TraceIdentity, actual.TraceIdentity, context);
        Assert.AreEqual(expected.SourceInspectionRevision, actual.SourceInspectionRevision, context);
        Assert.AreEqual(expected.PlanId, actual.PlanId, context); Assert.AreEqual(expected.PlanRevision, actual.PlanRevision, context);
        Assert.AreEqual(expected.PlanSignature, actual.PlanSignature, context); Assert.AreEqual(expected.SchemaVersion, actual.SchemaVersion, context);
        Assert.AreEqual(expected.AlgorithmVersion, actual.AlgorithmVersion, context); Assert.AreEqual(expected.GateId, actual.GateId, context);
        Assert.AreEqual(expected.GateRevision, actual.GateRevision, context);
        CollectionAssert.AreEqual(expected.RequestedMeasures.ToArray(), actual.RequestedMeasures.ToArray(), context);
        Assert.AreEqual(expected.Status, actual.Status, context); Assert.AreEqual(expected.EventKind, actual.EventKind, context);
        Assert.AreEqual(expected.SampleIndex, actual.SampleIndex, context); Assert.AreEqual(expected.FractionalSamplePosition, actual.FractionalSamplePosition, context);
        Assert.AreEqual(expected.DetectionTimeSeconds, actual.DetectionTimeSeconds, context); Assert.AreEqual(expected.AmplitudeCounts, actual.AmplitudeCounts, context);
        Assert.AreEqual(expected.AmplitudeUnit, actual.AmplitudeUnit, context); Assert.AreEqual(expected.SignedRfAmplitudeCounts, actual.SignedRfAmplitudeCounts, context);
        Assert.AreEqual(expected.TimeOfFlightSeconds, actual.TimeOfFlightSeconds, context); Assert.AreEqual(expected.OffsetWithinWindowSeconds, actual.OffsetWithinWindowSeconds, context);
        Assert.AreEqual(expected.Quality, actual.Quality, context); Assert.AreEqual(expected.SignalDomain, actual.SignalDomain, context);
        Assert.AreEqual(expected.Provenance, actual.Provenance, context); Assert.AreEqual(expected.CausalGateId, actual.CausalGateId, context);
        Assert.AreEqual(expected.CausalStatus, actual.CausalStatus, context); Assert.AreEqual(expected.DiagnosticCode, actual.DiagnosticCode, context);
        Assert.AreEqual(expected.Metrics.ProminenceCounts, actual.Metrics.ProminenceCounts, context);
        Assert.AreEqual(expected.Metrics.ThresholdMarginCounts, actual.Metrics.ThresholdMarginCounts, context);
    }

    private static GateDerivedBatchKey Key(GateEvaluationBatch batch)
    {
        GateEvaluationEntry entry = batch.Entries[0];
        return new(batch.Trace.ConfirmedIdentity, batch.Trace.SourceInspectionRevision, entry.PlanId,
            entry.PlanRevision, entry.PlanSignature.Hex, entry.SchemaVersion, entry.AlgorithmVersion);
    }

    private static (short[] Samples, double? Threshold, GateHysteresis? Hysteresis,
        GateLocalExtremaParameters? Local) Case(GateDetectorKind detector, GateSignalDomain domain, bool alternate)
    {
        short high = domain == GateSignalDomain.NegativeMagnitude ? (short)-30_000 : (short)30_000;
        short low = domain == GateSignalDomain.RawSigned ? (short)-30_000 : (short)0;
        return detector switch
        {
            GateDetectorKind.GlobalMaximum or GateDetectorKind.GlobalMinimum or GateDetectorKind.AbsoluteMaximum =>
                (alternate ? [short.MinValue, 0, short.MaxValue, short.MaxValue] : [low, high, low],
                    alternate ? 1d : 0d, null, null),
            GateDetectorKind.FirstRisingCrossing or GateDetectorKind.LastRisingCrossing =>
                (alternate ? [low, low, low, low] : [low, high, low, high], null,
                    alternate ? new(.25, .75) : new(0, .5), null),
            GateDetectorKind.FirstFallingCrossing or GateDetectorKind.LastFallingCrossing =>
                (alternate ? [high, high, high, high] : [high, low, high, low], null,
                    alternate ? new(.25, .75) : new(0, .5), null),
            _ => (alternate ? [low, low, low, low, low] : [low, high, high, low, high, low],
                .5, null, alternate ? new(1, 1, 3) : new(1, 1, 2))
        };
    }

    private sealed class CapturingSink : IGateEvaluationBatchSink
    {
        internal TaskCompletionSource<GateEvaluationBatch> Next { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ValueTask<GateBatchDeliveryResult> DeliverAsync(GateEvaluationBatch batch,
            CancellationToken exceptionalCancellation = default)
        { Next.TrySetResult(batch); return ValueTask.FromResult(GateBatchDeliveryResult.Accepted()); }
    }
}
