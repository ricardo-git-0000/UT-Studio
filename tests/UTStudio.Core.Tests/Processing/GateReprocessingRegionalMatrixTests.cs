using UTStudio.Contracts.Inspection;
using UTStudio.Contracts.Processing;
using UTStudio.Domain.Acquisition;
using UTStudio.Domain.Inspection;
using UTStudio.Domain.Processing;
using UTStudio.Inspection.Core;
using UTStudio.Processing.Core;

namespace UTStudio.Core.Tests.Processing;

[TestClass]
public sealed class GateReprocessingRegionalMatrixTests
{
    [TestMethod]
    public async Task MultiStreamSparseRegionProcessesExactSetWithObservableComplexity()
    {
        var inspection = new InspectionId(Guid.NewGuid()); var run = new AcquisitionRunId(Guid.NewGuid());
        UtStreamIdentity[] streams = Enumerable.Range(0, 3).Select(i => new UtStreamIdentity(inspection, run,
            new UtSourceId($"regional-{i}"), new PhysicalChannelId(i))).ToArray();
        await using var rf = new InMemoryInspectionDataStore(inspection, new(4_000_000, 512));
        IInspectionRunWriter writer = await rf.OpenRunAsync(run);
        var committed = new Dictionary<UtStreamIdentity, List<TraceIdentity>>();
        var revisions = new Dictionary<TraceIdentity, InspectionRevision>();
        foreach (UtStreamIdentity stream in streams) committed[stream] = [];
        for (int i = 0; i < 70; i++) await CommitAsync(writer, streams[0], checked((ulong)i * 2), checked((ulong)i + 10_000), committed, revisions);
        await CommitAsync(writer, streams[0], ulong.MaxValue, 99_999, committed, revisions);
        foreach (ulong index in new ulong[] { 1, 4, 7, 10, 13, 16 })
            await CommitAsync(writer, streams[1], index, index + 20_000, committed, revisions);
        foreach (ulong index in new ulong[] { 3, 9, 27, 81 })
            await CommitAsync(writer, streams[2], index, index + 30_000, committed, revisions);
        InspectionRunSealResult seal = await writer.SealRunAsync();

        GateReprocessingStreamSelection[] selection =
        [
            new(streams[0], [new(50, 90), new(0, 50), new(90, 138), new(ulong.MaxValue, ulong.MaxValue)]),
            new(streams[1], [new(13, 16), new(1, 7), new(7, 13)]),
            new(streams[2], [new(27, 27)])
        ];
        TraceIdentity[] expected = selection.SelectMany(item => committed[item.Stream]
            .Where(identity => item.Ranges.Any(range => identity.AcquisitionIndex >= range.FirstInclusive &&
                identity.AcquisitionIndex <= range.LastInclusive))).ToArray();
        CompiledGatePlan plan = GateEvaluationTestData.Compile(GateEvaluationTestData.Gate("A", 4));
        await using var derived = new InMemoryGateDerivedStore(new(256, 8_000_000));
        await using var scheduler = new GateReprocessingScheduler(rf, derived,
            new(maximumWorkers: 2, maximumAdmittedJobs: 2, sampleBufferBudgetBytes: 262_144),
            new GateScratchPool(2, 600_000));
        var descriptor = new GateReprocessingJobDescriptor(new(Guid.NewGuid()), new(Guid.NewGuid()), inspection,
            run, seal.FinalRevision, selection, plan, 64, GateReprocessingPublicationPolicy.ActivateOnComplete,
            new DerivedViewId("regional-exact"), snapshotPageSize: 1);
        GateReprocessingProgressSnapshot terminal = await (await scheduler.AdmitAsync(descriptor)).Job!.Completion;

        Assert.AreEqual(GateReprocessingLifecycle.Completed, terminal.Lifecycle, terminal.PrimaryError);
        Assert.AreEqual(expected.LongLength, terminal.SelectedTraceCount);
        Assert.AreEqual(expected.LongLength, terminal.EntriesReturned);
        Assert.AreEqual(expected.LongLength, terminal.IdentitiesVisited);
        Assert.IsGreaterThanOrEqualTo(64L, terminal.PagesRead);
        Assert.AreEqual(selection.Sum(item => item.Ranges.Count), terminal.NormalizedRangeCount);
        Assert.AreEqual(terminal.NormalizedRangeCount, terminal.LowerBoundSearches);
        Assert.AreEqual(terminal.NormalizedRangeCount, terminal.UpperBoundSearches);
        Assert.AreEqual(0L, terminal.PrefixRescans);
        Assert.IsLessThanOrEqualTo(2 * terminal.NormalizedRangeCount *
            (long)Math.Ceiling(Math.Log2(72)) + 32, terminal.BinarySearchComparisons);
        Assert.AreEqual(expected.Length, derived.Diagnostics.BatchCount);
        Assert.AreEqual(expected.Length, derived.Diagnostics.MembershipCount);
        foreach (GateDerivedStreamCoverage coverage in derived.ActiveView!.Manifest.Streams)
        {
            TraceIdentity[] perStream = expected.Where(x => x.Stream == coverage.Stream).ToArray();
            Assert.AreEqual(perStream.Length, coverage.EligibleTraceCount);
            Assert.AreEqual(GateDerivedStreamCoverage.ComputeIdentityDigest(perStream), coverage.IdentityDigest);
        }
        using IGateDerivedReadLease view = derived.OpenActiveView();
        foreach (TraceIdentity identity in expected)
            Assert.IsNotNull(view.ReadExact(Key(identity, revisions[identity], plan)));
        foreach (TraceIdentity identity in committed.SelectMany(x => x.Value).Except(expected))
            Assert.IsNull(view.ReadExact(Key(identity, revisions[identity], plan)));
        AssertBalances(terminal, derived.Diagnostics);
    }

    [TestMethod]
    public async Task TwoRegionsAndTwoPlanEpochsRemainIndependentAndReuseDuplicates()
    {
        var inspection = new InspectionId(Guid.NewGuid()); var run = new AcquisitionRunId(Guid.NewGuid());
        var stream = new UtStreamIdentity(inspection, run, new UtSourceId("epochs"), new PhysicalChannelId(0));
        await using var rf = new InMemoryInspectionDataStore(inspection, new(1_000_000, 64));
        IInspectionRunWriter writer = await rf.OpenRunAsync(run);
        var committed = new Dictionary<UtStreamIdentity, List<TraceIdentity>> { [stream] = [] };
        var revisions = new Dictionary<TraceIdentity, InspectionRevision>();
        foreach (ulong index in new ulong[] { 1, 3, 5, 7 }) await CommitAsync(writer, stream, index, index + 100, committed, revisions);
        InspectionRunSealResult seal = await writer.SealRunAsync();
        CompiledGatePlan first = GateEvaluationTestData.Compile(GateEvaluationTestData.Gate("A", 4));
        CompiledGatePlan second = new GatePlanCompiler().Compile(GateTestPlan.Plan(
            [GateTestPlan.Gate("A", durationSeconds: 4e-6, revision: 2)], revision: 2)).CompiledPlan!;
        await using var derived = new InMemoryGateDerivedStore(new(64, 4_000_000));

        GateReprocessingProgressSnapshot left = await RunAsync(first, [new(1, 3)],
            GateReprocessingPublicationPolicy.StoreOnly, null);
        GateReprocessingProgressSnapshot duplicate = await RunAsync(first, [new(1, 3)],
            GateReprocessingPublicationPolicy.StoreOnly, null);
        GateReprocessingProgressSnapshot right = await RunAsync(second, [new(5, 7)],
            GateReprocessingPublicationPolicy.ActivateOnComplete, new("epoch-2"));

        Assert.AreEqual(2L, left.NewlyCommittedBatchCount);
        Assert.AreEqual(0L, duplicate.NewlyCommittedBatchCount); Assert.AreEqual(2L, duplicate.ReusedBatchCount);
        Assert.AreEqual(2L, right.NewlyCommittedBatchCount);
        Assert.AreEqual(second.Source.Revision, derived.ActiveView!.Manifest.PlanRevision);
        Assert.AreEqual(4, derived.Diagnostics.BatchCount);

        async Task<GateReprocessingProgressSnapshot> RunAsync(CompiledGatePlan plan,
            IReadOnlyList<AcquisitionIndexRange> ranges, GateReprocessingPublicationPolicy policy, DerivedViewId? view)
        {
            await using var scheduler = new GateReprocessingScheduler(rf, derived);
            var request = new GateReprocessingJobDescriptor(new(Guid.NewGuid()), new(Guid.NewGuid()), inspection,
                run, seal.FinalRevision, [new GateReprocessingStreamSelection(stream, ranges)], plan, 1,
                policy, view, 1);
            GateReprocessingProgressSnapshot result = await (await scheduler.AdmitAsync(request)).Job!.Completion;
            Assert.AreEqual(GateReprocessingLifecycle.Completed, result.Lifecycle, result.PrimaryError);
            AssertBalances(result, derived.Diagnostics); return result;
        }
    }

    private static async Task CommitAsync(IInspectionRunWriter writer, UtStreamIdentity stream, ulong index,
        ulong sequence, IDictionary<UtStreamIdentity, List<TraceIdentity>> committed,
        IDictionary<TraceIdentity, InspectionRevision> revisions)
    {
        var identity = new TraceIdentity(stream, index);
        var descriptor = new TraceDescriptor(identity, sequence, TimeSpan.FromTicks((long)(sequence % long.MaxValue)),
            null, null, 4, 1_000_000, 0, UtSignalMode.Rf, provenance: UtTraceProvenance.Simulator,
            traceQuality: UtTraceQuality.Good);
        InspectionAppendResult result = await writer.CommitAsync(new(new(descriptor, new short[] { 0, 7, 2, 0 })));
        Assert.AreEqual(InspectionAppendStatus.Committed, result.Status);
        committed[stream].Add(identity); revisions.Add(identity, result.Revision);
    }

    private static GateDerivedBatchKey Key(TraceIdentity identity, InspectionRevision revision, CompiledGatePlan plan) =>
        new(identity, revision, plan.Source.Id, plan.Source.Revision, plan.Signature.Hex,
            plan.Source.SchemaVersion, plan.Source.AlgorithmVersion);

    private static void AssertBalances(GateReprocessingProgressSnapshot terminal, GateDerivedStoreDiagnostics store)
    {
        Assert.IsTrue(terminal.CleanupConfirmed); Assert.AreEqual(0, terminal.ActiveWorkers);
        Assert.AreEqual(0, terminal.ActiveCallbacks); Assert.AreEqual(terminal.LeasesAcquired, terminal.LeasesReleased);
        Assert.AreEqual(terminal.BufferRents, terminal.BufferReturns); Assert.AreEqual(terminal.ScratchRents, terminal.ScratchReturns);
        Assert.AreEqual(store.WritersAdmitted, store.WritersCompleted); Assert.AreEqual(0, store.AdmittedWriters);
        Assert.AreEqual(0, store.ReservedBatchCount); Assert.AreEqual(0, store.ReservedCoverageCount);
        Assert.AreEqual(0, store.ReservedActivationCount);
    }
}
