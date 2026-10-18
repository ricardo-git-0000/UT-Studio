using UTStudio.Contracts.Inspection;
using UTStudio.Contracts.Processing;
using UTStudio.Domain.Acquisition;
using UTStudio.Domain.Inspection;
using UTStudio.Domain.Processing;
using UTStudio.Inspection.Core;
using UTStudio.Processing.Core;

namespace UTStudio.Core.Tests.Processing;

[TestClass]
public sealed class GateReprocessingLoadSmokeTests
{
    public static IEnumerable<object[]> PairwiseScenarios => PairwiseRows.Select(row => new object[] { row });

    [TestMethod]
    public void PairwiseTableCoversEveryValuePairAcrossAllEightDimensions()
    {
        object[][] rows = PairwiseRows.Select(ToValues).ToArray();
        for (int left = 0; left < rows[0].Length; left++)
            for (int right = left + 1; right < rows[0].Length; right++)
            {
                object[] leftValues = rows.Select(x => x[left]).Distinct().ToArray();
                object[] rightValues = rows.Select(x => x[right]).Distinct().ToArray();
                foreach (object leftValue in leftValues)
                    foreach (object rightValue in rightValues)
                        Assert.IsTrue(rows.Any(x => Equals(x[left], leftValue) && Equals(x[right], rightValue)),
                            $"Missing pair dimensions {left}/{right}: {leftValue}/{rightValue}.");
            }
        Assert.HasCount(21, PairwiseRows);
    }

    [TestMethod]
    public async Task PairwiseTableExecutesProductivePipelineAndPublishesStructuredResults()
    {
        var results = new List<LoadScenarioResult>(PairwiseRows.Length);
        foreach (LoadScenario scenario in PairwiseRows)
        {
            LoadScenarioResult result = await RunPairwiseScenarioAsync(scenario);
            results.Add(result);
            Console.WriteLine(result);
            Assert.AreEqual(scenario.Id, result.Id, scenario.ToString());
            Assert.AreEqual(scenario, result.Dimensions, scenario.ToString());
            Assert.AreEqual(result.ExpectedIdentities, result.Terminal.SelectedTraceCount, scenario.ToString());
            Assert.AreEqual(0, result.Terminal.ActiveWorkers, scenario.ToString());
            Assert.AreEqual(0, result.Terminal.ActiveCallbacks, scenario.ToString());
            Assert.AreEqual(0, result.Terminal.ActiveLeases, scenario.ToString());
            Assert.AreEqual(result.Terminal.LeasesAcquired, result.Terminal.LeasesReleased, scenario.ToString());
            Assert.AreEqual(result.Terminal.BufferRents, result.Terminal.BufferReturns, scenario.ToString());
            Assert.AreEqual(result.Terminal.ScratchRents, result.Terminal.ScratchReturns, scenario.ToString());
            Assert.IsTrue(result.Terminal.CleanupConfirmed, scenario.ToString());
            Assert.AreEqual(0, result.StoreDiagnostics.AdmittedWriters, scenario.ToString());
            Assert.AreEqual(0, result.StoreDiagnostics.ReservedBatchCount, scenario.ToString());
            Assert.AreEqual(0, result.StoreDiagnostics.ReservedCoverageCount, scenario.ToString());
            Assert.AreEqual(0, result.StoreDiagnostics.ReservedActivationCount, scenario.ToString());
            Assert.AreEqual(scenario.Live, result.LiveCompleted, scenario.ToString());
            Assert.AreEqual(scenario.Policy == GateReprocessingPublicationPolicy.ActivateOnComplete &&
                result.Terminal.Lifecycle == GateReprocessingLifecycle.Completed,
                result.ActiveViewPresent, scenario.ToString());
        }
        Assert.HasCount(21, results);
        CollectionAssert.AreEquivalent(Enumerable.Range(1, 21).ToArray(), results.Select(x => x.Id).ToArray());
    }

    [TestMethod]
    [DataRow(1)]
    [DataRow(2)]
    [DataRow(3)]
    [DataRow(4)]
    [DataRow(5)]
    [DataRow(6)]
    [DataRow(7)]
    [DataRow(8)]
    [DataRow(9)]
    [DataRow(10)]
    [DataRow(11)]
    [DataRow(12)]
    [DataRow(13)]
    [DataRow(14)]
    [DataRow(15)]
    [DataRow(16)]
    [DataRow(17)]
    [DataRow(18)]
    [DataRow(19)]
    [DataRow(20)]
    [DataRow(21)]
    public async Task PairwiseProductiveRow(int id) =>
        _ = await RunPairwiseScenarioAsync(PairwiseRows.Single(row => row.Id == id));

    [TestMethod]
    public async Task CriticalPairwiseInteractionsHaveExplicitProductiveAssertions()
    {
        LoadScenarioResult providerLive = await RunPairwiseScenarioAsync(PairwiseRows.Single(x => x.Id == 3));
        Assert.IsTrue(providerLive.Dimensions.SlowProvider && providerLive.Dimensions.Live);
        Assert.IsTrue(providerLive.LiveCompleted);
        Assert.AreEqual(GateReprocessingLifecycle.Completed, providerLive.Terminal.Lifecycle);

        LoadScenarioResult storeLive = await RunPairwiseScenarioAsync(PairwiseRows.Single(x => x.Id == 1));
        Assert.IsTrue(storeLive.Dimensions.SlowStore && storeLive.Dimensions.Live);
        Assert.IsTrue(storeLive.LiveCompleted);

        LoadScenarioResult cancelStore = await RunPairwiseScenarioAsync(PairwiseRows.Single(x => x.Id == 11));
        Assert.AreEqual(LoadTermination.CancelAfterAdmission, cancelStore.Dimensions.Termination);
        Assert.AreEqual(1L, cancelStore.Terminal.CommitProgress!.Admitted);
        Assert.AreEqual(1L, cancelStore.Terminal.CommitProgress.Completed);

        LoadScenarioResult stopRead = await RunPairwiseScenarioAsync(PairwiseRows.Single(x => x.Id == 13));
        Assert.AreEqual(0L, stopRead.Terminal.EvaluatedTraceCount);
        Assert.AreEqual(0L, stopRead.Terminal.CommitProgress!.Attempts);

        LoadScenarioResult stopRfCopy = await RunPairwiseScenarioAsync(PairwiseRows.Single(x => x.Id == 17));
        Assert.IsTrue(stopRfCopy.LiveCompleted);
        Assert.IsTrue(stopRfCopy.RfCopyBoundaryReached);
        Assert.IsFalse(stopRfCopy.EvaluationBoundaryReached);
        Assert.AreEqual("rf-copy", stopRfCopy.TerminalStage);
        Assert.AreEqual(0L, stopRfCopy.Terminal.ScratchRents);
        Assert.AreEqual(0, stopRfCopy.Terminal.PeakCallbacks);
        Assert.AreEqual(0L, stopRfCopy.Terminal.EvaluatedTraceCount);
        Assert.AreEqual(0L, stopRfCopy.Terminal.EvaluatedNotPublishedCount);

        LoadScenarioResult stopCommit = await RunPairwiseScenarioAsync(PairwiseRows.Single(x => x.Id == 19));
        Assert.AreEqual(1L, stopCommit.Terminal.CommitProgress!.Admitted);
        Assert.AreEqual(1L, stopCommit.Terminal.CommitProgress.Completed);

        LoadScenarioResult activateStreams = await RunPairwiseScenarioAsync(PairwiseRows.Single(x => x.Id == 3));
        Assert.HasCount(4, activateStreams.Terminal.StreamProgress);
        Assert.IsTrue(activateStreams.ActiveViewPresent);
        Assert.AreEqual(1L, activateStreams.Terminal.ActivationProgress!.Succeeded);

        LoadScenarioResult duplicateBlocks = await RunPairwiseScenarioAsync(PairwiseRows.Single(x => x.Id == 4));
        Assert.IsGreaterThan(1, duplicateBlocks.Terminal.BlocksTotal);
        Assert.AreEqual(duplicateBlocks.ExpectedIdentities, duplicateBlocks.Terminal.ReusedBatchCount);
        Assert.AreEqual(0L, duplicateBlocks.Terminal.NewlyCommittedBatchCount);
    }

    [TestMethod]
    public async Task RepresentativeSampleStreamBlockAndPublicationMatrixBalancesExactly()
    {
        int scenarios = 0;
        foreach (int sampleCount in new[] { 2_048, 65_535 })
            foreach (int streamCount in new[] { 1, 4 })
                foreach (int blockSize in new[] { 1, 64, 256 })
                    foreach (GateReprocessingPublicationPolicy policy in new[]
                        { GateReprocessingPublicationPolicy.StoreOnly, GateReprocessingPublicationPolicy.ActivateOnComplete })
                    {
                        await RunScenarioAsync(sampleCount, streamCount, blockSize, policy);
                        scenarios++;
                    }
        Assert.AreEqual(24, scenarios);
    }

    [TestMethod]
    public async Task DuplicateAttemptReusesEveryBatchAndBalancesExactly()
    {
        (GateReprocessingProgressSnapshot first, GateReprocessingProgressSnapshot second) =
            await RunDuplicateScenarioAsync();
        Assert.AreEqual(16L, first.NewlyCommittedBatchCount);
        Assert.AreEqual(0L, second.NewlyCommittedBatchCount);
        Assert.AreEqual(16L, second.ReusedBatchCount);
    }

    private static async Task RunScenarioAsync(int sampleCount, int streamCount, int blockSize,
        GateReprocessingPublicationPolicy policy)
    {
        const int tracesPerStream = 8;
        var inspection = new InspectionId(Guid.NewGuid()); var run = new AcquisitionRunId(Guid.NewGuid());
        UtStreamIdentity[] streams = Enumerable.Range(0, streamCount).Select(index => new UtStreamIdentity(
            inspection, run, new UtSourceId($"load-{index}"), new PhysicalChannelId(index))).ToArray();
        long sampleBytes = checked((long)sampleCount * sizeof(short) * tracesPerStream * streamCount);
        await using var rf = new InMemoryInspectionDataStore(inspection,
            new(sampleBytes + 65_536, tracesPerStream * streamCount + 8));
        IInspectionRunWriter writer = await rf.OpenRunAsync(run);
        short[] samples = new short[sampleCount]; samples[sampleCount / 2] = short.MaxValue;
        foreach (UtStreamIdentity stream in streams)
            for (ulong index = 0; index < tracesPerStream; index++)
            {
                var identity = new TraceIdentity(stream, index);
                var descriptor = new TraceDescriptor(identity, index + 1_000, TimeSpan.Zero, null, null,
                    sampleCount, 100_000_000, 0, UtSignalMode.Rf, provenance: UtTraceProvenance.Simulator,
                    traceQuality: UtTraceQuality.Good);
                Assert.AreEqual(InspectionAppendStatus.Committed,
                    (await writer.CommitAsync(new(new(descriptor, samples)))).Status);
            }
        InspectionRevision revision = (await writer.SealRunAsync()).FinalRevision;
        CompiledGatePlan plan = GateEvaluationTestData.Compile(GateEvaluationTestData.Gate("A", sampleCount,
            rate: 100_000_000, domain: GateSignalDomain.Absolute));
        await using var derived = new InMemoryGateDerivedStore(new(128, 32_000_000));
        await using var scheduler = new GateReprocessingScheduler(rf, derived,
            new(maximumWorkers: 2, maximumAdmittedJobs: 2, sampleBufferBudgetBytes: 262_144),
            new GateScratchPool(2, 600_000));
        var request = new GateReprocessingJobDescriptor(new(Guid.NewGuid()), new(Guid.NewGuid()), inspection, run,
            revision, streams.Select(stream => new GateReprocessingStreamSelection(stream, [new(0, 7)])),
            plan, blockSize, policy,
            policy == GateReprocessingPublicationPolicy.ActivateOnComplete ? new DerivedViewId("load-view") : null,
            snapshotPageSize: 4);
        GateReprocessingProgressSnapshot terminal = await (await scheduler.AdmitAsync(request)).Job!.Completion;
        Assert.AreEqual(GateReprocessingLifecycle.Completed, terminal.Lifecycle, terminal.PrimaryError);
        Assert.AreEqual((long)tracesPerStream * streamCount, terminal.SelectedTraceCount);
        Assert.AreEqual(terminal.SelectedTraceCount, terminal.EvaluatedTraceCount);
        Assert.IsTrue(terminal.CleanupConfirmed); Assert.AreEqual(terminal.LeasesAcquired, terminal.LeasesReleased);
        Assert.AreEqual(terminal.BufferRents, terminal.BufferReturns); Assert.AreEqual(terminal.ScratchRents, terminal.ScratchReturns);
        Assert.AreEqual(0, derived.Diagnostics.AdmittedWriters); Assert.AreEqual(0, derived.Diagnostics.ReservedBatchCount);
        Assert.AreEqual(0, derived.Diagnostics.ReservedCoverageCount); Assert.AreEqual(0, derived.Diagnostics.ReservedActivationCount);
    }

    private static async Task<LoadScenarioResult> RunPairwiseScenarioAsync(LoadScenario scenario)
    {
        const int tracesPerStream = 2;
        var inspection = new InspectionId(Guid.NewGuid());
        var run = new AcquisitionRunId(Guid.NewGuid());
        UtStreamIdentity[] streams = Enumerable.Range(0, scenario.StreamCount).Select(index =>
            new UtStreamIdentity(inspection, run, new UtSourceId($"pairwise-{scenario.Id}-{index}"),
                new PhysicalChannelId(index))).ToArray();
        long bytes = checked((long)scenario.SampleCount * sizeof(short) * tracesPerStream * scenario.StreamCount);
        await using var rf = new InMemoryInspectionDataStore(inspection,
            new(bytes + 1_000_000, tracesPerStream * scenario.StreamCount + 16));
        IInspectionRunWriter writer = await rf.OpenRunAsync(run);
        short[] samples = new short[scenario.SampleCount];
        samples[scenario.SampleCount / 2] = short.MaxValue;
        foreach (UtStreamIdentity stream in streams)
            for (ulong index = 0; index < tracesPerStream; index++)
            {
                var identity = new TraceIdentity(stream, index);
                var descriptor = new TraceDescriptor(identity, index, TimeSpan.Zero, null, null,
                    scenario.SampleCount, 100_000_000, 0, UtSignalMode.Rf,
                    provenance: UtTraceProvenance.Simulator, traceQuality: UtTraceQuality.Good);
                Assert.AreEqual(InspectionAppendStatus.Committed,
                    (await writer.CommitAsync(new(new(descriptor, samples)))).Status);
            }
        InspectionRevision revision = (await writer.SealRunAsync()).FinalRevision;
        CompiledGatePlan plan = GateEvaluationTestData.Compile(GateEvaluationTestData.Gate("A",
            scenario.SampleCount, rate: 100_000_000, domain: GateSignalDomain.Absolute));
        await using var derived = new InMemoryGateDerivedStore(new(128, 32_000_000));
        var providerGate = new ProductiveGate();
        var storeGate = new ProductiveGate();
        var rfCopyGate = new ProductiveGate();
        bool storeConfigured = scenario.SlowStore || scenario.Termination is LoadTermination.CancelBeforeAdmission or
            LoadTermination.CancelAfterAdmission or LoadTermination.StopDuringAdmittedCommit;
        bool providerConfigured = scenario.SlowProvider || scenario.Termination == LoadTermination.StopDuringRead ||
            (scenario.Live && !storeConfigured && scenario.Termination != LoadTermination.StopDuringRfCopy);
        IInspectionSealedRunSnapshotProvider provider = scenario.Termination == LoadTermination.StopDuringRfCopy
            ? new CopyBlockingSnapshotProvider(rf, rfCopyGate)
            : providerConfigured ? new BlockingSnapshotProvider(rf, providerGate) : rf;
        IGateDerivedStore schedulerStore = storeConfigured
            ? new GateControlledStore(derived, storeGate,
                scenario.Termination is LoadTermination.CancelAfterAdmission or LoadTermination.StopDuringAdmittedCommit)
            : derived;
        await using var scheduler = new GateReprocessingScheduler(provider, schedulerStore,
            new(maximumWorkers: 2, maximumAdmittedJobs: 2, sampleBufferBudgetBytes: 262_144),
            new GateScratchPool(2, 600_000));


        var request = new GateReprocessingJobDescriptor(new(Guid.NewGuid()), new(Guid.NewGuid()), inspection, run,
            revision, streams.Select(stream => new GateReprocessingStreamSelection(stream, [new(0, 1)])),
            plan, scenario.BlockSize, scenario.Policy,
            scenario.Policy == GateReprocessingPublicationPolicy.ActivateOnComplete
                ? new DerivedViewId($"pairwise-view-{scenario.Id}") : null, snapshotPageSize: 1);
        IGateReprocessingJob job = (await scheduler.AdmitAsync(request)).Job!;
        bool liveCompleted = false;
        Task? stop = null;
        bool providerReached = false, storeReached = false, rfCopyReached = false;
        GateReprocessingProgressSnapshot terminal;
        try
        {
            if (providerConfigured && scenario.Termination != LoadTermination.StopDuringRfCopy)
            {
                await providerGate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                providerReached = true;
                if (scenario.Live) liveCompleted = await RunLiveProbeAsync(rf, inspection, plan, scenario.Id);
                if (scenario.Termination == LoadTermination.StopDuringRead)
                    stop = scheduler.StopAsync();
                providerGate.Release();
            }
            if (scenario.Termination != LoadTermination.StopDuringRead &&
                scenario.Termination != LoadTermination.StopDuringRfCopy && storeConfigured)
            {
                await storeGate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                storeReached = true;
                if (scenario.Live && !liveCompleted) liveCompleted = await RunLiveProbeAsync(rf, inspection, plan, scenario.Id);
                if (scenario.Termination is LoadTermination.CancelBeforeAdmission or LoadTermination.CancelAfterAdmission)
                    job.Cancel();
                else if (scenario.Termination == LoadTermination.StopDuringAdmittedCommit)
                    stop = scheduler.StopAsync();
                storeGate.Release();
            }
            if (scenario.Termination == LoadTermination.StopDuringRfCopy)
            {
                await rfCopyGate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                rfCopyReached = true;
                if (scenario.Live) liveCompleted = await RunLiveProbeAsync(rf, inspection, plan, scenario.Id);
                stop = scheduler.StopAsync();
                rfCopyGate.Release();
            }
            if (scenario.Live && !liveCompleted)
            {
                liveCompleted = await RunLiveProbeAsync(rf, inspection, plan, scenario.Id);
            }
            terminal = await job.Completion.WaitAsync(TimeSpan.FromSeconds(10));
            if (stop is not null) await stop.WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally
        {
            providerGate.Release(); storeGate.Release(); rfCopyGate.Release();
        }

        if (scenario.Termination == LoadTermination.Duplicate)
        {
            var replay = new GateReprocessingJobDescriptor(new(Guid.NewGuid()), new(Guid.NewGuid()), inspection, run,
                revision, request.Selection, plan, scenario.BlockSize, scenario.Policy,
                scenario.Policy == GateReprocessingPublicationPolicy.ActivateOnComplete
                    ? new DerivedViewId($"pairwise-replay-{scenario.Id}") : null, snapshotPageSize: 1);
            terminal = await (await scheduler.AdmitAsync(replay)).Job!.Completion.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.AreEqual(0L, terminal.NewlyCommittedBatchCount, scenario.ToString());
            Assert.AreEqual((long)tracesPerStream * scenario.StreamCount, terminal.ReusedBatchCount, scenario.ToString());
        }

        GateReprocessingLifecycle expectedLifecycle = scenario.Termination is LoadTermination.Normal or LoadTermination.Duplicate
            ? GateReprocessingLifecycle.Completed : GateReprocessingLifecycle.Cancelled;
        Assert.AreEqual(expectedLifecycle, terminal.Lifecycle, $"{scenario}: {terminal.PrimaryError}");
        long expected = (long)tracesPerStream * scenario.StreamCount;
        if (expectedLifecycle == GateReprocessingLifecycle.Completed)
        {
            Assert.AreEqual(expected, terminal.IdentitiesVisited, scenario.ToString());
            Assert.AreEqual(expected, terminal.ReadTraceCount, scenario.ToString());
            Assert.AreEqual(expected, terminal.EvaluatedTraceCount, scenario.ToString());
        }
        GateDerivedStoreDiagnostics diagnostics = derived.Diagnostics;
        Assert.AreEqual(expectedLifecycle == GateReprocessingLifecycle.Completed ? 1 : 0,
            diagnostics.TerminalEvidenceCount, scenario.ToString());
        Assert.AreEqual((ulong)diagnostics.BatchCount + (ulong)diagnostics.Activations,
            diagnostics.Revision.Value, scenario.ToString());
        if (scenario.Termination == LoadTermination.Normal)
            Assert.AreEqual(expected, terminal.NewlyCommittedBatchCount, scenario.ToString());
        if (scenario.Termination == LoadTermination.Duplicate)
            Assert.AreEqual(expected, terminal.ReusedBatchCount, scenario.ToString());
        var result = new LoadScenarioResult(scenario.Id, scenario, expected, terminal, diagnostics, liveCompleted,
            derived.ActiveView is not null,
            scenario.Termination == LoadTermination.StopDuringRfCopy ? "rf-copy" : ActualTerminalStage(terminal),
            providerReached, storeReached, rfCopyReached, terminal.PeakCallbacks > 0);
        AssertExpected(result, ExpectedOutcomes.Single(item => item.Id == scenario.Id));
        return result;
    }

    private static async Task<(GateReprocessingProgressSnapshot First, GateReprocessingProgressSnapshot Second)>
        RunDuplicateScenarioAsync()
    {
        var inspection = new InspectionId(Guid.NewGuid()); var run = new AcquisitionRunId(Guid.NewGuid());
        var stream = new UtStreamIdentity(inspection, run, new UtSourceId("duplicate-load"), new PhysicalChannelId(0));
        await using var rf = new InMemoryInspectionDataStore(inspection, new(2_000_000, 32));
        IInspectionRunWriter writer = await rf.OpenRunAsync(run);
        for (ulong index = 0; index < 16; index++)
        {
            var identity = new TraceIdentity(stream, index);
            var descriptor = new TraceDescriptor(identity, index, TimeSpan.Zero, null, null, 2_048,
                100_000_000, 0, UtSignalMode.Rf, traceQuality: UtTraceQuality.Good);
            Assert.AreEqual(InspectionAppendStatus.Committed,
                (await writer.CommitAsync(new(new(descriptor, new short[2_048])))).Status);
        }
        InspectionRevision revision = (await writer.SealRunAsync()).FinalRevision;
        CompiledGatePlan plan = GateEvaluationTestData.Compile(GateEvaluationTestData.Gate("A", 2_048,
            rate: 100_000_000));
        await using var derived = new InMemoryGateDerivedStore(new(64, 4_000_000));
        GateReprocessingProgressSnapshot first = await RunAsync(); GateReprocessingProgressSnapshot second = await RunAsync();
        return (first, second);

        async Task<GateReprocessingProgressSnapshot> RunAsync()
        {
            await using var scheduler = new GateReprocessingScheduler(rf, derived);
            var request = new GateReprocessingJobDescriptor(new(Guid.NewGuid()), new(Guid.NewGuid()), inspection,
                run, revision, [new GateReprocessingStreamSelection(stream, [new(0, 15)])], plan, 64,
                GateReprocessingPublicationPolicy.StoreOnly, snapshotPageSize: 8);
            GateReprocessingProgressSnapshot result = await (await scheduler.AdmitAsync(request)).Job!.Completion;
            Assert.AreEqual(GateReprocessingLifecycle.Completed, result.Lifecycle, result.PrimaryError);
            Assert.IsTrue(result.CleanupConfirmed); return result;
        }
    }

    private static readonly LoadScenario[] PairwiseRows =
    [
        new(1, 65_535, 1, 1, GateReprocessingPublicationPolicy.StoreOnly, false, true, true, LoadTermination.Normal),
        new(2, 2_048, 1, 64, GateReprocessingPublicationPolicy.ActivateOnComplete, true, false, false, LoadTermination.Normal),
        new(3, 65_535, 4, 256, GateReprocessingPublicationPolicy.ActivateOnComplete, true, true, true, LoadTermination.Normal),
        new(4, 65_535, 4, 1, GateReprocessingPublicationPolicy.StoreOnly, false, false, false, LoadTermination.Duplicate),
        new(5, 2_048, 4, 64, GateReprocessingPublicationPolicy.ActivateOnComplete, false, false, true, LoadTermination.Duplicate),
        new(6, 2_048, 1, 256, GateReprocessingPublicationPolicy.ActivateOnComplete, true, true, true, LoadTermination.Duplicate),
        new(7, 65_535, 4, 1, GateReprocessingPublicationPolicy.ActivateOnComplete, true, false, false, LoadTermination.CancelBeforeAdmission),
        new(8, 2_048, 1, 64, GateReprocessingPublicationPolicy.ActivateOnComplete, false, true, true, LoadTermination.CancelBeforeAdmission),
        new(9, 2_048, 4, 256, GateReprocessingPublicationPolicy.StoreOnly, true, true, false, LoadTermination.CancelBeforeAdmission),
        new(10, 2_048, 1, 1, GateReprocessingPublicationPolicy.ActivateOnComplete, false, false, false, LoadTermination.CancelAfterAdmission),
        new(11, 65_535, 4, 64, GateReprocessingPublicationPolicy.StoreOnly, false, true, false, LoadTermination.CancelAfterAdmission),
        new(12, 65_535, 4, 256, GateReprocessingPublicationPolicy.StoreOnly, true, false, true, LoadTermination.CancelAfterAdmission),
        new(13, 65_535, 4, 1, GateReprocessingPublicationPolicy.ActivateOnComplete, true, false, false, LoadTermination.StopDuringRead),
        new(14, 65_535, 1, 64, GateReprocessingPublicationPolicy.ActivateOnComplete, false, true, false, LoadTermination.StopDuringRead),
        new(15, 2_048, 1, 256, GateReprocessingPublicationPolicy.StoreOnly, true, false, true, LoadTermination.StopDuringRead),
        new(16, 65_535, 1, 1, GateReprocessingPublicationPolicy.ActivateOnComplete, true, false, false, LoadTermination.StopDuringRfCopy),
        new(17, 65_535, 4, 64, GateReprocessingPublicationPolicy.StoreOnly, false, true, true, LoadTermination.StopDuringRfCopy),
        new(18, 2_048, 1, 256, GateReprocessingPublicationPolicy.ActivateOnComplete, false, false, false, LoadTermination.StopDuringRfCopy),
        new(19, 65_535, 4, 1, GateReprocessingPublicationPolicy.ActivateOnComplete, false, true, true, LoadTermination.StopDuringAdmittedCommit),
        new(20, 65_535, 1, 64, GateReprocessingPublicationPolicy.ActivateOnComplete, true, true, false, LoadTermination.StopDuringAdmittedCommit),
        new(21, 2_048, 4, 256, GateReprocessingPublicationPolicy.StoreOnly, true, false, false, LoadTermination.StopDuringAdmittedCommit)
    ];

    private static readonly PairwiseExpectation[] ExpectedOutcomes =
    [
        new(1,"complete",false,true,false,GateReprocessingLifecycle.Completed,GateReprocessingPublicationPolicy.StoreOnly,false,2,0,true),
        new(2,"complete",true,false,false,GateReprocessingLifecycle.Completed,GateReprocessingPublicationPolicy.ActivateOnComplete,true,2,0,true),
        new(3,"complete",true,true,false,GateReprocessingLifecycle.Completed,GateReprocessingPublicationPolicy.ActivateOnComplete,true,8,0,true),
        new(4,"complete",false,false,false,GateReprocessingLifecycle.Completed,GateReprocessingPublicationPolicy.StoreOnly,false,0,8,true),
        new(5,"complete",true,false,false,GateReprocessingLifecycle.Completed,GateReprocessingPublicationPolicy.ActivateOnComplete,true,0,8,true),
        new(6,"complete",true,true,false,GateReprocessingLifecycle.Completed,GateReprocessingPublicationPolicy.ActivateOnComplete,true,0,2,true),
        new(7,"commit-before-admission",true,true,false,GateReprocessingLifecycle.Cancelled,GateReprocessingPublicationPolicy.ActivateOnComplete,false,0,0,true),
        new(8,"commit-before-admission",false,true,false,GateReprocessingLifecycle.Cancelled,GateReprocessingPublicationPolicy.ActivateOnComplete,false,0,0,true),
        new(9,"commit-before-admission",true,true,false,GateReprocessingLifecycle.Cancelled,GateReprocessingPublicationPolicy.StoreOnly,false,0,0,true),
        new(10,"commit-admitted",false,true,false,GateReprocessingLifecycle.Cancelled,GateReprocessingPublicationPolicy.ActivateOnComplete,false,1,0,true),
        new(11,"commit-admitted",false,true,false,GateReprocessingLifecycle.Cancelled,GateReprocessingPublicationPolicy.StoreOnly,false,1,0,true),
        new(12,"commit-admitted",true,true,false,GateReprocessingLifecycle.Cancelled,GateReprocessingPublicationPolicy.StoreOnly,false,1,0,true),
        new(13,"read",true,false,false,GateReprocessingLifecycle.Cancelled,GateReprocessingPublicationPolicy.ActivateOnComplete,false,0,0,false),
        new(14,"read",true,false,false,GateReprocessingLifecycle.Cancelled,GateReprocessingPublicationPolicy.ActivateOnComplete,false,0,0,false),
        new(15,"read",true,false,false,GateReprocessingLifecycle.Cancelled,GateReprocessingPublicationPolicy.StoreOnly,false,0,0,false),
        new(16,"rf-copy",false,false,true,GateReprocessingLifecycle.Cancelled,GateReprocessingPublicationPolicy.ActivateOnComplete,false,0,0,false),
        new(17,"rf-copy",false,false,true,GateReprocessingLifecycle.Cancelled,GateReprocessingPublicationPolicy.StoreOnly,false,0,0,false),
        new(18,"rf-copy",false,false,true,GateReprocessingLifecycle.Cancelled,GateReprocessingPublicationPolicy.ActivateOnComplete,false,0,0,false),
        new(19,"commit-admitted",false,true,false,GateReprocessingLifecycle.Cancelled,GateReprocessingPublicationPolicy.ActivateOnComplete,false,1,0,true),
        new(20,"commit-admitted",true,true,false,GateReprocessingLifecycle.Cancelled,GateReprocessingPublicationPolicy.ActivateOnComplete,false,1,0,true),
        new(21,"commit-admitted",true,true,false,GateReprocessingLifecycle.Cancelled,GateReprocessingPublicationPolicy.StoreOnly,false,1,0,true)
    ];

    private static void AssertExpected(LoadScenarioResult actual, PairwiseExpectation expected)
    {
        string message = $"{actual.Dimensions}; expected={expected}; observed=" +
            $"{actual.TerminalStage}/{actual.ProviderBoundaryReached}/{actual.StoreBoundaryReached}/{actual.RfCopyBoundaryReached}";
        Assert.AreEqual(expected.Stage, actual.TerminalStage, message);
        Assert.AreEqual(expected.Provider, actual.ProviderBoundaryReached, message);
        Assert.AreEqual(expected.Store, actual.StoreBoundaryReached, message);
        Assert.AreEqual(expected.RfCopy, actual.RfCopyBoundaryReached, message);
        Assert.AreEqual(expected.Evaluation, actual.EvaluationBoundaryReached, message);
        Assert.AreEqual(expected.Lifecycle, actual.Terminal.Lifecycle, message);
        Assert.AreEqual(expected.Policy, actual.Dimensions.Policy, message);
        Assert.AreEqual(actual.Dimensions.Policy == GateReprocessingPublicationPolicy.ActivateOnComplete &&
            expected.View, actual.ActiveViewPresent, message);
        Assert.AreEqual(expected.New, actual.Terminal.NewlyCommittedBatchCount, message);
        Assert.AreEqual(expected.Duplicate, actual.Terminal.ReusedBatchCount, message);
        Assert.IsTrue(actual.Terminal.CleanupConfirmed, message);
    }

    private static object[] ToValues(LoadScenario value) =>
    [value.SampleCount, value.StreamCount, value.BlockSize, value.Policy,
        value.SlowProvider, value.SlowStore, value.Live, value.Termination];

    private static string ActualTerminalStage(GateReprocessingProgressSnapshot terminal)
    {
        if (terminal.Lifecycle == GateReprocessingLifecycle.Completed) return "complete";
        if (terminal.CommitProgress?.Admitted > 0) return "commit-admitted";
        if (terminal.CommitProgress?.RejectedBeforeAdmission > 0) return "commit-before-admission";
        if (terminal.EvaluatedNotPublishedCount > 0) return "evaluation";
        if (terminal.ReadTraceCount == 0) return "read";
        return "cancelled";
    }

    private static async Task<bool> RunLiveProbeAsync(InMemoryInspectionDataStore rf, InspectionId inspection,
        CompiledGatePlan plan, int scenarioId)
    {
        var run = new AcquisitionRunId(Guid.NewGuid());
        var stream = new UtStreamIdentity(inspection, run, new UtSourceId($"pairwise-live-{scenarioId}"),
            new PhysicalChannelId(100 + scenarioId));
        IInspectionRunWriter writer = await rf.OpenRunAsync(run);
        var sink = new LiveCapturingSink();
        var scratch = new GateScratchPool(1, 300_000);
        await using var live = new CommittedGateProcessingOrchestrator(rf, rf, sink, new GateEvaluator(scratch));
        Assert.AreEqual(GateProcessingAdmissionStatus.Accepted,
            live.OpenRun(new(inspection, run, [stream], plan)));
        var identity = new TraceIdentity(stream, 0);
        var descriptor = new TraceDescriptor(identity, 1, TimeSpan.Zero, null, null, 4,
            100_000_000, 0, UtSignalMode.Rf, traceQuality: UtTraceQuality.Good);
        InspectionAppendResult committed = await writer.CommitAsync(
            new(new(descriptor, new short[] { 0, 7, 2, 0 })));
        Assert.AreEqual(InspectionAppendStatus.Committed, committed.Status);
        Assert.AreEqual(GateProcessingAdmissionStatus.Accepted,
            live.NotifyCommitted(new(identity, committed.Revision)));
        await sink.Delivered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        InspectionRevision revision = (await writer.SealRunAsync()).FinalRevision;
        live.StopAdmission(run);
        live.RequestFinalReconciliation(run, revision);
        await live.WaitForDrainAsync(run).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.AreEqual(0, live.Diagnostics.ActiveLeases);
        Assert.AreEqual(0, live.Diagnostics.ActiveCallbacks);
        Assert.AreEqual(live.Diagnostics.LeasesAcquired, live.Diagnostics.LeasesReleased);
        Assert.AreEqual(0, scratch.Snapshot.ActiveRentals);
        return true;
    }

    private sealed class LiveCapturingSink : IGateEvaluationBatchSink
    {
        internal TaskCompletionSource Delivered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ValueTask<GateBatchDeliveryResult> DeliverAsync(GateEvaluationBatch batch,
            CancellationToken exceptionalCancellation = default)
        { Delivered.TrySetResult(); return ValueTask.FromResult(GateBatchDeliveryResult.Accepted()); }
    }

    private sealed class ProductiveGate
    {
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _blocked;
        internal TaskCompletionSource Entered => _entered;
        internal void Block()
        {
            if (Interlocked.Exchange(ref _blocked, 1) != 0) return;
            _entered.TrySetResult();
            _release.Task.GetAwaiter().GetResult();
        }
        internal async ValueTask WaitAsync(CancellationToken token)
        {
            if (Interlocked.Exchange(ref _blocked, 1) != 0) return;
            _entered.TrySetResult();
            await _release.Task.WaitAsync(token);
        }
        internal void Signal() => _entered.TrySetResult();
        internal void Release() => _release.TrySetResult();
    }

    private sealed class BlockingSnapshotProvider(IInspectionSealedRunSnapshotProvider inner, ProductiveGate gate) :
        IInspectionSealedRunSnapshotProvider
    {
        public async ValueTask<InspectionSealedRunSnapshotOpenResult> OpenSealedRunSnapshotAsync(
            InspectionSealedRunSnapshotRequest request, CancellationToken cancellationToken = default)
        {
            InspectionSealedRunSnapshotOpenResult opened = await inner.OpenSealedRunSnapshotAsync(request,
                cancellationToken);
            return opened.Status == InspectionSealedRunSnapshotOpenStatus.Available && opened.Snapshot is not null
                ? InspectionSealedRunSnapshotOpenResult.Available(new BlockingSnapshot(opened.Snapshot, gate))
                : opened;
        }

        private sealed class BlockingSnapshot(IInspectionSealedRunSnapshot inner, ProductiveGate gate) :
            IInspectionSealedRunSnapshot
        {
            public ushort ContractVersion => inner.ContractVersion;
            public InspectionId InspectionId => inner.InspectionId;
            public AcquisitionRunId RunId => inner.RunId;
            public InspectionRevision SourceFinalRevision => inner.SourceFinalRevision;
            public InspectionRunState RunState => inner.RunState;
            public int MaximumPageSize => inner.MaximumPageSize;
            public IReadOnlyList<InspectionSealedStreamSnapshot> Streams => inner.Streams;
            public InspectionSealedRunEnumerationDiagnostics EnumerationDiagnostics => inner.EnumerationDiagnostics;
            public long CountIdentities(InspectionSealedRunRangeQuery query) => inner.CountIdentities(query);
            public InspectionSealedRunIdentityPage ReadIdentities(UtStreamIdentity stream,
                InspectionSealedRunCursor? cursor = null) => inner.ReadIdentities(stream, cursor);
            public InspectionSealedRunIdentityPage ReadIdentities(InspectionSealedRunRangeQuery query,
                InspectionSealedRunCursor? cursor = null) => inner.ReadIdentities(query, cursor);
            public async ValueTask<InspectionTraceReadResult> ReadAsync(TraceIdentity identity,
                CancellationToken cancellationToken = default)
            { await gate.WaitAsync(cancellationToken); return await inner.ReadAsync(identity, cancellationToken); }
            public void Dispose() => inner.Dispose();
        }
    }

    private sealed class CopyBlockingSnapshotProvider(IInspectionSealedRunSnapshotProvider inner, ProductiveGate gate) :
        IInspectionSealedRunSnapshotProvider
    {
        public async ValueTask<InspectionSealedRunSnapshotOpenResult> OpenSealedRunSnapshotAsync(
            InspectionSealedRunSnapshotRequest request, CancellationToken cancellationToken = default)
        {
            InspectionSealedRunSnapshotOpenResult opened = await inner.OpenSealedRunSnapshotAsync(request,
                cancellationToken);
            return opened.Snapshot is null ? opened :
                InspectionSealedRunSnapshotOpenResult.Available(new Snapshot(opened.Snapshot, gate));
        }

        private sealed class Snapshot(IInspectionSealedRunSnapshot inner, ProductiveGate gate) :
            IInspectionSealedRunSnapshot
        {
            public ushort ContractVersion => inner.ContractVersion;
            public InspectionId InspectionId => inner.InspectionId;
            public AcquisitionRunId RunId => inner.RunId;
            public InspectionRevision SourceFinalRevision => inner.SourceFinalRevision;
            public InspectionRunState RunState => inner.RunState;
            public int MaximumPageSize => inner.MaximumPageSize;
            public IReadOnlyList<InspectionSealedStreamSnapshot> Streams => inner.Streams;
            public InspectionSealedRunEnumerationDiagnostics EnumerationDiagnostics => inner.EnumerationDiagnostics;
            public long CountIdentities(InspectionSealedRunRangeQuery query) => inner.CountIdentities(query);
            public InspectionSealedRunIdentityPage ReadIdentities(UtStreamIdentity stream,
                InspectionSealedRunCursor? cursor = null) => inner.ReadIdentities(stream, cursor);
            public InspectionSealedRunIdentityPage ReadIdentities(InspectionSealedRunRangeQuery query,
                InspectionSealedRunCursor? cursor = null) => inner.ReadIdentities(query, cursor);
            public async ValueTask<InspectionTraceReadResult> ReadAsync(TraceIdentity identity,
                CancellationToken cancellationToken = default)
            {
                InspectionTraceReadResult result = await inner.ReadAsync(identity, cancellationToken);
                return result.Lease is null ? result : InspectionTraceReadResult.Available(result.Revision,
                    new Lease(result.Lease, gate));
            }
            public void Dispose() => inner.Dispose();
        }

        private sealed class Lease(IInspectionTraceLease inner, ProductiveGate gate) : IInspectionTraceLease
        {
            public TraceDescriptor Descriptor => inner.Descriptor;
            public IReadOnlyUtSamples Samples { get; } = new Samples(inner.Samples, gate);
            public void Dispose() => inner.Dispose();
        }

        private sealed class Samples(IReadOnlyUtSamples inner, ProductiveGate gate) : IReadOnlyUtSamples
        {
            public int Count => inner.Count;
            public short this[int index] => inner[index];
            public void CopyTo(Span<short> destination) { gate.Block(); inner.CopyTo(destination); }
        }
    }

    private sealed class GateControlledStore(InMemoryGateDerivedStore inner, ProductiveGate gate,
        bool admittedBeforeGate) : IGateDerivedStore, IGateProcessingTerminalCoverageSink
    {
        public DerivedStoreRevision Revision => inner.Revision;
        public ActiveDerivedViewDescriptor? ActiveView => inner.ActiveView;
        public GateDerivedStoreDiagnostics Diagnostics => inner.Diagnostics;
        public ValueTask<GateBatchDeliveryResult> DeliverAsync(GateEvaluationBatch batch,
            CancellationToken exceptionalCancellation = default) => inner.DeliverAsync(batch, exceptionalCancellation);
        public async ValueTask<GateDerivedMutationResult> CommitAsync(GateEvaluationBatch batch,
            CancellationToken cancellationToken = default)
        {
            gate.Block();
            return await inner.CommitAsync(batch,
                admittedBeforeGate ? CancellationToken.None : cancellationToken);
        }
        public GateDerivedMutationResult Activate(GateDerivedCoverageManifest manifest, DerivedViewId viewId) =>
            inner.Activate(manifest, viewId);
        public ValueTask<GateDerivedMutationResult> ActivateAsync(GateDerivedCoverageManifest manifest,
            DerivedViewId viewId, CancellationToken cancellationToken = default) =>
            inner.ActivateAsync(manifest, viewId, cancellationToken);
        public GateDerivedMutationResult Invalidate(DerivedViewId viewId) => inner.Invalidate(viewId);
        public ValueTask<GateDerivedMutationResult> ReclaimInvalidatedAsync(
            CancellationToken cancellationToken = default) => inner.ReclaimInvalidatedAsync(cancellationToken);
        public IGateDerivedReadLease OpenActiveView() => inner.OpenActiveView();
        public GateProcessingTerminalCoverageStatus ConfirmTerminalCoverage(
            IReadOnlyList<GateProcessingTerminalCoverage> coverage) => inner.ConfirmTerminalCoverage(coverage);
        public ValueTask<GateDerivedMutationResult> ConfirmTerminalCoverageAsync(
            IReadOnlyList<GateProcessingTerminalCoverage> coverage,
            CancellationToken cancellationToken = default) => inner.ConfirmTerminalCoverageAsync(coverage, cancellationToken);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    public sealed record LoadScenario(int Id, int SampleCount, int StreamCount, int BlockSize,
        GateReprocessingPublicationPolicy Policy, bool SlowProvider, bool SlowStore, bool Live,
        LoadTermination Termination)
    {
        public override string ToString() => $"{Id:D2}_{Termination}_{SampleCount}_{StreamCount}x_b{BlockSize}";
    }

    public sealed record LoadScenarioResult(int Id, LoadScenario Dimensions, long ExpectedIdentities,
        GateReprocessingProgressSnapshot Terminal, GateDerivedStoreDiagnostics StoreDiagnostics,
        bool LiveCompleted, bool ActiveViewPresent, string TerminalStage,
        bool ProviderBoundaryReached, bool StoreBoundaryReached, bool RfCopyBoundaryReached,
        bool EvaluationBoundaryReached);

    private sealed record PairwiseExpectation(int Id, string Stage, bool Provider, bool Store, bool RfCopy,
        GateReprocessingLifecycle Lifecycle, GateReprocessingPublicationPolicy Policy, bool View,
        long New, long Duplicate, bool Evaluation);

    public enum LoadTermination
    {
        Normal, Duplicate, CancelBeforeAdmission, CancelAfterAdmission,
        StopDuringRead, StopDuringRfCopy, StopDuringAdmittedCommit
    }
}
