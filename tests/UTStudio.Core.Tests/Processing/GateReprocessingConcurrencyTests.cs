using UTStudio.Contracts.Inspection;
using UTStudio.Contracts.Processing;
using UTStudio.Domain.Acquisition;
using UTStudio.Domain.Inspection;
using UTStudio.Domain.Processing;
using UTStudio.Inspection.Core;
using UTStudio.Processing.Core;

namespace UTStudio.Core.Tests.Processing;

[TestClass]
public sealed class GateReprocessingConcurrencyTests
{
    [TestMethod]
    public async Task CancellingOneOfTwoConcurrentJobsDoesNotAffectTheOtherAcrossOneHundredRuns()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        for (int repetition = 0; repetition < 100; repetition++)
        {
            var gate = new ReadGate(fixture.Store, fixture.StreamA);
            await using var derived = new InMemoryGateDerivedStore(new(32, 2_000_000));
            await using var scheduler = new GateReprocessingScheduler(gate, derived,
                new(maximumWorkers: 2, maximumAdmittedJobs: 2, sampleBufferBudgetBytes: 262_144),
                new GateScratchPool(2, 600_000));
            IGateReprocessingJob first = (await scheduler.AdmitAsync(fixture.Descriptor(fixture.StreamA))).Job!;
            await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            IGateReprocessingJob second = (await scheduler.AdmitAsync(fixture.Descriptor(fixture.StreamB))).Job!;
            GateReprocessingProgressSnapshot completed;
            try { completed = await second.Completion.WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (TimeoutException)
            {
                GateReprocessingProgressSnapshot progress = second.Progress;
                Assert.Fail($"Healthy job stalled at {progress.Lifecycle}; read={progress.ReadTraceCount}; " +
                    $"evaluated={progress.EvaluatedTraceCount}; commits={progress.NewlyCommittedBatchCount}.");
                throw;
            }
            first.Cancel(); gate.Release.TrySetResult();
            GateReprocessingProgressSnapshot cancelled = await first.Completion.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.AreEqual(GateReprocessingLifecycle.Completed, completed.Lifecycle);
            Assert.AreEqual(GateReprocessingLifecycle.Cancelled, cancelled.Lifecycle);
            AssertTerminalBalances(completed); AssertTerminalBalances(cancelled);
            Assert.AreEqual(derived.Diagnostics.WritersAdmitted, derived.Diagnostics.WritersCompleted);
        }
    }

    [TestMethod]
    public async Task FailureOfOneOfTwoConcurrentJobsDoesNotAffectTheOtherAcrossOneHundredRuns()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        for (int repetition = 0; repetition < 100; repetition++)
        {
            var provider = new SelectiveFailureProvider(fixture.Store, fixture.StreamA);
            await using var derived = new InMemoryGateDerivedStore(new(32, 2_000_000));
            await using var scheduler = new GateReprocessingScheduler(provider, derived,
                new(maximumWorkers: 2, maximumAdmittedJobs: 2, sampleBufferBudgetBytes: 262_144),
                new GateScratchPool(2, 600_000));
            IGateReprocessingJob failed = (await scheduler.AdmitAsync(fixture.Descriptor(fixture.StreamA))).Job!;
            IGateReprocessingJob healthy = (await scheduler.AdmitAsync(fixture.Descriptor(fixture.StreamB))).Job!;
            GateReprocessingProgressSnapshot[] terminal = await Task.WhenAll(failed.Completion, healthy.Completion)
                .WaitAsync(TimeSpan.FromSeconds(5));
            Assert.AreEqual(GateReprocessingLifecycle.Faulted, terminal[0].Lifecycle);
            Assert.AreEqual(GateReprocessingLifecycle.Completed, terminal[1].Lifecycle);
            AssertTerminalBalances(terminal[0]); AssertTerminalBalances(terminal[1]);
            Assert.AreEqual(derived.Diagnostics.WritersAdmitted, derived.Diagnostics.WritersCompleted);
        }
    }

    [TestMethod]
    public async Task SingleWorkerYieldsBetweenBlocksSoSecondJobProgressesBeforeFirstFinishes()
    {
        await using Fixture fixture = await Fixture.CreateAsync(traceCountA: 3);
        var provider = new OrderingProvider(fixture.Store);
        await using var derived = new InMemoryGateDerivedStore(new(32, 2_000_000));
        await using var scheduler = new GateReprocessingScheduler(provider, derived,
            new(maximumWorkers: 1, maximumAdmittedJobs: 2));
        IGateReprocessingJob first = (await scheduler.AdmitAsync(fixture.Descriptor(fixture.StreamA, blockSize: 1))).Job!;
        await provider.FirstRead.Task.WaitAsync(TimeSpan.FromSeconds(5));
        IGateReprocessingJob second = (await scheduler.AdmitAsync(fixture.Descriptor(fixture.StreamB, blockSize: 1))).Job!;
        provider.ReleaseFirstRead.TrySetResult();
        await Task.WhenAll(first.Completion, second.Completion).WaitAsync(TimeSpan.FromSeconds(5));
        int firstB = provider.Order.FindIndex(x => x.Stream == fixture.StreamB);
        int lastA = provider.Order.FindLastIndex(x => x.Stream == fixture.StreamA);
        Assert.IsGreaterThanOrEqualTo(0, firstB);
        Assert.IsLessThan(lastA, firstB);
    }

    [TestMethod]
    public async Task LiveProgressesWhileHistoricalReadIsBlockedAndResourcesRemainIsolated()
    {
        var inspection = new InspectionId(Guid.NewGuid());
        var historicalRun = new AcquisitionRunId(Guid.NewGuid()); var liveRun = new AcquisitionRunId(Guid.NewGuid());
        var historicalStream = new UtStreamIdentity(inspection, historicalRun, new UtSourceId("historical"), new PhysicalChannelId(0));
        var liveStream = new UtStreamIdentity(inspection, liveRun, new UtSourceId("live"), new PhysicalChannelId(1));
        await using var rf = new InMemoryInspectionDataStore(inspection, new(2_000_000, 64));
        IInspectionRunWriter historicalWriter = await rf.OpenRunAsync(historicalRun);
        await CommitAsync(historicalWriter, historicalStream, 0);
        InspectionRevision historicalRevision = (await historicalWriter.SealRunAsync()).FinalRevision;
        IInspectionRunWriter liveWriter = await rf.OpenRunAsync(liveRun);
        CompiledGatePlan plan = GateEvaluationTestData.Compile(GateEvaluationTestData.Gate("A", 4));
        var liveSink = new CapturingSink();
        var liveScratch = new GateScratchPool(1, 300_000);
        await using var live = new CommittedGateProcessingOrchestrator(rf, rf, liveSink,
            new GateEvaluator(liveScratch));
        Assert.AreEqual(GateProcessingAdmissionStatus.Accepted,
            live.OpenRun(new(inspection, liveRun, [liveStream], plan)));
        var gate = new ReadGate(rf, historicalStream);
        await using var derived = new InMemoryGateDerivedStore();
        var historicalScratch = new GateScratchPool(1, 300_000);
        await using var scheduler = new GateReprocessingScheduler(gate, derived,
            reprocessingScratchPool: historicalScratch);
        var request = new GateReprocessingJobDescriptor(new(Guid.NewGuid()), new(Guid.NewGuid()), inspection,
            historicalRun, historicalRevision,
            [new GateReprocessingStreamSelection(historicalStream, [new(0, 0)])], plan, 1,
            GateReprocessingPublicationPolicy.StoreOnly, snapshotPageSize: 1);
        IGateReprocessingJob historical = (await scheduler.AdmitAsync(request)).Job!;
        await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        InspectionAppendResult liveCommit = await CommitAsync(liveWriter, liveStream, 0);
        Assert.AreEqual(GateProcessingAdmissionStatus.Accepted,
            live.NotifyCommitted(new(new(liveStream, 0), liveCommit.Revision)));
        await liveSink.Next.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.IsFalse(historical.Completion.IsCompleted);
        gate.Release.TrySetResult();
        GateReprocessingProgressSnapshot historicalTerminal = await historical.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        InspectionRevision liveRevision = (await liveWriter.SealRunAsync()).FinalRevision;
        live.StopAdmission(liveRun); live.RequestFinalReconciliation(liveRun, liveRevision);
        await live.WaitForDrainAsync(liveRun).AsTask().WaitAsync(TimeSpan.FromSeconds(5));

        Assert.AreEqual(GateReprocessingLifecycle.Completed, historicalTerminal.Lifecycle);
        AssertTerminalBalances(historicalTerminal);
        Assert.AreEqual(0, live.Diagnostics.ActiveLeases); Assert.AreEqual(0, live.Diagnostics.ActiveCallbacks);
        Assert.AreEqual(live.Diagnostics.LeasesAcquired, live.Diagnostics.LeasesReleased);
        Assert.AreNotSame(liveScratch, historicalScratch);
    }

    [TestMethod]
    public async Task StopDuringRfCopyCancelsBeforeEvaluationAndBalancesTerminalResourcesAcrossOneHundredRuns()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        for (int repetition = 0; repetition < 100; repetition++)
        {
            await using var derived = new InMemoryGateDerivedStore(new(32, 2_000_000));
            var copyGate = new CopyGateProvider(fixture.Store);
            await using var scheduler = new GateReprocessingScheduler(copyGate, derived,
                new(maximumWorkers: 1, maximumAdmittedJobs: 1), new GateScratchPool(1, 300_000));

            IGateReprocessingJob job = (await scheduler.AdmitAsync(
                fixture.Descriptor(fixture.StreamA, blockSize: 1))).Job!;
            await copyGate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Task stop = scheduler.StopAsync();
            Assert.IsFalse(stop.IsCompleted);
            copyGate.Release.TrySetResult();
            GateReprocessingProgressSnapshot terminal = await job.Completion.WaitAsync(TimeSpan.FromSeconds(5));
            await stop.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.AreEqual(GateReprocessingLifecycle.Cancelled, terminal.Lifecycle);
            Assert.AreEqual(1, terminal.PeakCallbacks);
            Assert.AreEqual(1L, terminal.ReadTraceCount);
            Assert.AreEqual(1L, terminal.EvaluatedTraceCount);
            Assert.AreEqual(1L, terminal.EvaluatedNotPublishedCount);
            Assert.AreEqual(0L, terminal.CommitProgress!.Attempts);
            Assert.AreEqual(0L, terminal.ScratchRents);
            AssertTerminalBalances(terminal);
            Assert.AreEqual(derived.Diagnostics.WritersAdmitted, derived.Diagnostics.WritersCompleted);
        }
    }

    private static void AssertTerminalBalances(GateReprocessingProgressSnapshot terminal)
    {
        Assert.IsTrue(terminal.CleanupConfirmed); Assert.AreEqual(0, terminal.ActiveWorkers);
        Assert.AreEqual(0, terminal.ActiveCallbacks); Assert.AreEqual(0, terminal.ActiveLeases);
        Assert.AreEqual(terminal.LeasesAcquired, terminal.LeasesReleased);
        Assert.AreEqual(terminal.BufferRents, terminal.BufferReturns);
        Assert.AreEqual(terminal.ScratchRents, terminal.ScratchReturns);
        Assert.IsNotNull(terminal.PlanEpoch); Assert.HasCount(1, terminal.StreamProgress);
    }

    private static async Task<InspectionAppendResult> CommitAsync(IInspectionRunWriter writer,
        UtStreamIdentity stream, ulong index)
    {
        var identity = new TraceIdentity(stream, index);
        var descriptor = new TraceDescriptor(identity, index + 100, TimeSpan.Zero, null, null, 4,
            1_000_000, 0, UtSignalMode.Rf, traceQuality: UtTraceQuality.Good);
        InspectionAppendResult result = await writer.CommitAsync(new(new(descriptor, new short[] { 0, 7, 2, 0 })));
        Assert.AreEqual(InspectionAppendStatus.Committed, result.Status); return result;
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private Fixture(InMemoryInspectionDataStore store, InspectionId inspection, AcquisitionRunId run,
            UtStreamIdentity streamA, UtStreamIdentity streamB, InspectionRevision revision, CompiledGatePlan plan)
        { Store = store; Inspection = inspection; Run = run; StreamA = streamA; StreamB = streamB; Revision = revision; Plan = plan; }
        internal InMemoryInspectionDataStore Store { get; }
        internal InspectionId Inspection { get; }
        internal AcquisitionRunId Run { get; }
        internal UtStreamIdentity StreamA { get; }
        internal UtStreamIdentity StreamB { get; }
        internal InspectionRevision Revision { get; }
        internal CompiledGatePlan Plan { get; }
        internal static async Task<Fixture> CreateAsync(int traceCountA = 1)
        {
            var inspection = new InspectionId(Guid.NewGuid()); var run = new AcquisitionRunId(Guid.NewGuid());
            var a = new UtStreamIdentity(inspection, run, new UtSourceId("concurrent-a"), new PhysicalChannelId(0));
            var b = new UtStreamIdentity(inspection, run, new UtSourceId("concurrent-b"), new PhysicalChannelId(1));
            var store = new InMemoryInspectionDataStore(inspection, new(1_000_000, 64));
            IInspectionRunWriter writer = await store.OpenRunAsync(run);
            for (int i = 0; i < traceCountA; i++) await CommitAsync(writer, a, (ulong)i);
            await CommitAsync(writer, b, 0);
            InspectionRevision revision = (await writer.SealRunAsync()).FinalRevision;
            return new(store, inspection, run, a, b, revision,
                GateEvaluationTestData.Compile(GateEvaluationTestData.Gate("A", 4)));
        }
        internal GateReprocessingJobDescriptor Descriptor(UtStreamIdentity stream, int blockSize = 1) =>
            new(new(Guid.NewGuid()), new(Guid.NewGuid()), Inspection, Run, Revision,
                [new GateReprocessingStreamSelection(stream, [new(0, ulong.MaxValue)])], Plan, blockSize,
                GateReprocessingPublicationPolicy.StoreOnly, snapshotPageSize: 1);
        public ValueTask DisposeAsync() => Store.DisposeAsync();
        private static async Task CommitAsync(IInspectionRunWriter writer, UtStreamIdentity stream, ulong index)
        {
            var identity = new TraceIdentity(stream, index);
            var descriptor = new TraceDescriptor(identity, index + 100, TimeSpan.Zero, null, null, 4,
                1_000_000, 0, UtSignalMode.Rf, traceQuality: UtTraceQuality.Good);
            Assert.AreEqual(InspectionAppendStatus.Committed,
                (await writer.CommitAsync(new(new(descriptor, new short[] { 0, 7, 2, 0 })))).Status);
        }
    }

    private sealed class ReadGate(IInspectionSealedRunSnapshotProvider inner, UtStreamIdentity blocked) :
        WrappingProvider(inner)
    {
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override IInspectionSealedRunSnapshot Wrap(IInspectionSealedRunSnapshot snapshot) =>
            new WrappedSnapshot(snapshot, async (identity, cancellationToken) =>
            {
                if (identity.Stream == blocked)
                { Entered.TrySetResult(); await Release.Task.WaitAsync(cancellationToken); }
                return await snapshot.ReadAsync(identity, cancellationToken);
            });
    }

    private sealed class SelectiveFailureProvider(IInspectionSealedRunSnapshotProvider inner,
        UtStreamIdentity failed) : WrappingProvider(inner)
    {
        protected override IInspectionSealedRunSnapshot Wrap(IInspectionSealedRunSnapshot snapshot) =>
            new WrappedSnapshot(snapshot, (identity, cancellationToken) => identity.Stream == failed
                ? ValueTask.FromResult(InspectionTraceReadResult.Unavailable(InspectionReadStatus.Faulted,
                    snapshot.SourceFinalRevision, "selected-job"))
                : snapshot.ReadAsync(identity, cancellationToken));
    }

    private sealed class OrderingProvider(IInspectionSealedRunSnapshotProvider inner) : WrappingProvider(inner)
    {
        internal List<TraceIdentity> Order { get; } = [];
        internal TaskCompletionSource FirstRead { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource ReleaseFirstRead { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override IInspectionSealedRunSnapshot Wrap(IInspectionSealedRunSnapshot snapshot) =>
            new WrappedSnapshot(snapshot, async (identity, cancellationToken) =>
            {
                bool isFirst;
                lock (Order) { isFirst = Order.Count == 0; Order.Add(identity); }
                if (isFirst)
                {
                    FirstRead.TrySetResult();
                    await ReleaseFirstRead.Task.WaitAsync(cancellationToken);
                }
                return await snapshot.ReadAsync(identity, cancellationToken);
            });
    }

    private abstract class WrappingProvider(IInspectionSealedRunSnapshotProvider inner) :
        IInspectionSealedRunSnapshotProvider
    {
        public async ValueTask<InspectionSealedRunSnapshotOpenResult> OpenSealedRunSnapshotAsync(
            InspectionSealedRunSnapshotRequest request, CancellationToken cancellationToken = default)
        {
            InspectionSealedRunSnapshotOpenResult result = await inner.OpenSealedRunSnapshotAsync(request,
                cancellationToken);
            return result.Snapshot is null ? result : InspectionSealedRunSnapshotOpenResult.Available(Wrap(result.Snapshot));
        }
        protected abstract IInspectionSealedRunSnapshot Wrap(IInspectionSealedRunSnapshot snapshot);
    }

    private sealed class WrappedSnapshot(IInspectionSealedRunSnapshot inner,
        Func<TraceIdentity, CancellationToken, ValueTask<InspectionTraceReadResult>> read) : IInspectionSealedRunSnapshot
    {
        public ushort ContractVersion => inner.ContractVersion; public InspectionId InspectionId => inner.InspectionId;
        public AcquisitionRunId RunId => inner.RunId; public InspectionRevision SourceFinalRevision => inner.SourceFinalRevision;
        public InspectionRunState RunState => inner.RunState; public int MaximumPageSize => inner.MaximumPageSize;
        public IReadOnlyList<InspectionSealedStreamSnapshot> Streams => inner.Streams;
        public InspectionSealedRunEnumerationDiagnostics EnumerationDiagnostics => inner.EnumerationDiagnostics;
        public InspectionSealedRunIdentityPage ReadIdentities(UtStreamIdentity stream, InspectionSealedRunCursor? cursor = null) => inner.ReadIdentities(stream, cursor);
        public InspectionSealedRunIdentityPage ReadIdentities(InspectionSealedRunRangeQuery query, InspectionSealedRunCursor? cursor = null) => inner.ReadIdentities(query, cursor);
        public ValueTask<InspectionTraceReadResult> ReadAsync(TraceIdentity identity, CancellationToken cancellationToken = default) => read(identity, cancellationToken);
        public void Dispose() => inner.Dispose();
    }

    private sealed class CapturingSink : IGateEvaluationBatchSink
    {
        internal TaskCompletionSource<GateEvaluationBatch> Next { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ValueTask<GateBatchDeliveryResult> DeliverAsync(GateEvaluationBatch batch,
            CancellationToken exceptionalCancellation = default)
        { Next.TrySetResult(batch); return ValueTask.FromResult(GateBatchDeliveryResult.Accepted()); }
    }

    private sealed class CopyGateProvider(IInspectionSealedRunSnapshotProvider inner) : WrappingProvider(inner)
    {
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override IInspectionSealedRunSnapshot Wrap(IInspectionSealedRunSnapshot snapshot) =>
            new WrappedSnapshot(snapshot, async (identity, cancellationToken) =>
            {
                InspectionTraceReadResult result = await snapshot.ReadAsync(identity, cancellationToken);
                return result.Lease is null ? result : InspectionTraceReadResult.Available(result.Revision,
                    new CopyGateLease(result.Lease, Entered, Release));
            });

        private sealed class CopyGateLease(IInspectionTraceLease inner, TaskCompletionSource entered,
            TaskCompletionSource release) : IInspectionTraceLease
        {
            public TraceDescriptor Descriptor => inner.Descriptor;
            public IReadOnlyUtSamples Samples { get; } = new CopyGateSamples(inner.Samples, entered, release);
            public void Dispose() => inner.Dispose();
        }

        private sealed class CopyGateSamples(IReadOnlyUtSamples inner, TaskCompletionSource entered,
            TaskCompletionSource release) : IReadOnlyUtSamples
        {
            private int _signalled;
            public int Count => inner.Count;
            public short this[int index] => inner[index];
            public void CopyTo(Span<short> destination)
            {
                if (Interlocked.Exchange(ref _signalled, 1) == 0)
                { entered.TrySetResult(); release.Task.GetAwaiter().GetResult(); }
                inner.CopyTo(destination);
            }
        }
    }
}
