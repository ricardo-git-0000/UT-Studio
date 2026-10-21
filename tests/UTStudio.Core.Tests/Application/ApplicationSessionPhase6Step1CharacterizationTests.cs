using UTStudio.Application;
using UTStudio.Contracts.Inspection;
using UTStudio.Contracts.Processing;
using UTStudio.Core.Tests.Processing;
using UTStudio.Domain.Acquisition;
using UTStudio.Domain.Inspection;
using UTStudio.Domain.Processing;
using UTStudio.Inspection.Core;
using UTStudio.Processing.Core;

namespace UTStudio.Core.Tests.Application;

[TestClass]
public sealed class ApplicationSessionPhase6Step1CharacterizationTests
{
    private static readonly ConventionalAcquisitionConfiguration Configuration =
        new(new PhysicalChannelId(0), 8, 50_000_000, firstSampleOffsetSeconds: 0.000_001);

    [TestMethod]
    public async Task AuthoritativeTraceCommitPrecedesLocalPublicationAndFrameRelease()
    {
        await using var source = new ManualSessionSource();
        var inspection = new InspectionId(Guid.NewGuid());
        var run = new AcquisitionRunId(Guid.NewGuid());
        await using var stores = new InspectionDataStoreRegistry(inspection, new(1024, 10));
        var sink = new CountingSink();
        PostCommitBarrierWriter? barrier = null;
        await using var session = new ApplicationSession(source, inspection, id =>
        {
            barrier = new(stores.OpenRun(id), blockTraceCommit: true);
            return barrier;
        }, sink);

        await session.StartAsync(Configuration, run).WaitAsync(TimeSpan.FromSeconds(10));
        ManualSessionSource.CountingOwner owner = source.QueueFrame(sequence: 0);
        PostCommitBarrierWriter writer = barrier!;
        try
        {
            await writer.Confirmed.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.AreEqual(new InspectionRevision(1), stores.Store.Revision);
            Assert.AreEqual(0, sink.NotificationCount);
            Assert.IsFalse(owner.Returned.Task.IsCompleted);
        }
        finally
        {
            writer.Release.TrySetResult();
        }
        await owner.Returned.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await session.StopAsync().WaitAsync(TimeSpan.FromSeconds(10));

        Assert.AreEqual(1, sink.NotificationCount);
        Assert.AreEqual(1, owner.DisposeCount);
        Assert.AreEqual(1, writer.DisposeCount);
    }

    [TestMethod]
    public async Task AuthoritativeGapCommitPrecedesLocalIntegrityPublication()
    {
        await using var source = new ManualSessionSource();
        var inspection = new InspectionId(Guid.NewGuid());
        var run = new AcquisitionRunId(Guid.NewGuid());
        await using var stores = new InspectionDataStoreRegistry(inspection, new(1024, 10));
        var sink = new CountingSink();
        PostCommitBarrierWriter? barrier = null;
        await using var session = new ApplicationSession(source, inspection, id =>
        {
            barrier = new(stores.OpenRun(id), blockGapCommit: true);
            return barrier;
        }, sink);

        await session.StartAsync(Configuration, run).WaitAsync(TimeSpan.FromSeconds(10));
        ManualSessionSource.CountingOwner first = source.QueueFrame(
            sequence: 0, acquisitionIndex: 0, elapsed: TimeSpan.Zero);
        await first.Returned.Task.WaitAsync(TimeSpan.FromSeconds(10));
        ManualSessionSource.CountingOwner rejected = source.QueueFrame(sequence: 3, acquisitionIndex: 0,
            elapsed: TimeSpan.FromTicks(1));
        PostCommitBarrierWriter writer = barrier!;
        try
        {
            await writer.Confirmed.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var stream = new UtStreamIdentity(inspection, run, source.SourceId,
                Configuration.PhysicalChannelId);
            IReadOnlyList<GapRecord> confirmed = await stores.Store.ReadGapsAsync(stream, 10);
            Assert.HasCount(1, confirmed);
            Assert.AreEqual(new InspectionRevision(2), confirmed[0].ConfirmedRevision);
            Assert.AreEqual(0UL, session.Snapshot.Integrity.AcquisitionMissingFrames);
            Assert.AreEqual(1, sink.NotificationCount);
            Assert.IsFalse(rejected.Returned.Task.IsCompleted);
        }
        finally
        {
            writer.Release.TrySetResult();
        }
        await rejected.Returned.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await session.StopAsync().WaitAsync(TimeSpan.FromSeconds(10));

        Assert.AreEqual(2UL, session.Snapshot.Integrity.AcquisitionMissingFrames);
        Assert.AreEqual(1, sink.NotificationCount);
        Assert.AreEqual(1, first.DisposeCount);
        Assert.AreEqual(1, rejected.DisposeCount);
        Assert.AreEqual(1, writer.DisposeCount);
    }

    [TestMethod]
    public async Task ProcessingEpochAdmissionClosesBeforeInspectionWriterIsSealed()
    {
        await using var source = new ManualSessionSource();
        var inspection = new InspectionId(Guid.NewGuid());
        var run = new AcquisitionRunId(Guid.NewGuid());
        await using var stores = new InspectionDataStoreRegistry(inspection, new(1024, 10));
        List<string> events = [];
        var processing = new OrderedProcessingIngress(events);
        await using var session = new ApplicationSession(source, inspection,
            id => new SealOrderingWriter(stores.OpenRun(id), events), new CountingSink(), new CountingSink(),
            processing, ProcessingPlan());

        await session.StartAsync(Configuration, run).WaitAsync(TimeSpan.FromSeconds(10));
        await session.StopAsync().WaitAsync(TimeSpan.FromSeconds(10));

        CollectionAssert.AreEqual(new[] { "processing-close", "seal", "processing-final", "processing-drain" },
            events);
    }

    [TestMethod]
    public async Task ProcessingCloseRetriesBeforeSealFinalAndDrain()
    {
        await using var source = new ManualSessionSource();
        var inspection = new InspectionId(Guid.NewGuid());
        var run = new AcquisitionRunId(Guid.NewGuid());
        await using var stores = new InspectionDataStoreRegistry(inspection, new(1024, 10));
        List<string> events = [];
        var processing = new OrderedProcessingIngress(events, stopFailures: 1);
        await using var session = new ApplicationSession(source, inspection,
            id => new SealOrderingWriter(stores.OpenRun(id), events), new CountingSink(), new CountingSink(),
            processing, ProcessingPlan());

        await session.StartAsync(Configuration, run).WaitAsync(TimeSpan.FromSeconds(10));
        await session.StopAsync().WaitAsync(TimeSpan.FromSeconds(10));

        CollectionAssert.AreEqual(new[]
        { "processing-close-fail", "processing-close", "seal", "processing-final", "processing-drain" }, events);
        Assert.AreEqual(2, processing.StopCalls);
        Assert.AreEqual(1, processing.FinalCalls);
        Assert.AreEqual(1, processing.DrainCalls);
    }

    [TestMethod]
    public async Task PermanentProcessingCloseFailureNeverSealsFinalizesOrDrainsAndDisposeTerminates()
    {
        await using var source = new ManualSessionSource();
        var inspection = new InspectionId(Guid.NewGuid());
        var run = new AcquisitionRunId(Guid.NewGuid());
        await using var stores = new InspectionDataStoreRegistry(inspection, new(1024, 10));
        List<string> events = [];
        var processing = new OrderedProcessingIngress(events, stopFailures: int.MaxValue);
        var writer = new SealOrderingWriter(stores.OpenRun(run), events);
        var session = new ApplicationSession(source, inspection, _ => writer,
            new CountingSink(), new CountingSink(), processing, ProcessingPlan());

        await session.StartAsync(Configuration, run).WaitAsync(TimeSpan.FromSeconds(10));
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => session.StopAsync().WaitAsync(TimeSpan.FromSeconds(10)));
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => session.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10)));

        CollectionAssert.AreEqual(new[] { "processing-close-fail", "processing-close-fail" }, events);
        Assert.AreEqual(0, writer.SealCalls);
        Assert.AreEqual(0, processing.FinalCalls);
        Assert.AreEqual(0, processing.DrainCalls);
    }

    [TestMethod]
    public async Task SealFailureNeverPublishesFinalRevisionOrWaitsForProcessingDrain()
    {
        await using var source = new ManualSessionSource();
        var inspection = new InspectionId(Guid.NewGuid());
        var run = new AcquisitionRunId(Guid.NewGuid());
        await using var stores = new InspectionDataStoreRegistry(inspection, new(1024, 10));
        List<string> events = [];
        var processing = new OrderedProcessingIngress(events);
        var writer = new SealOrderingWriter(stores.OpenRun(run), events, sealFailures: 1);
        var session = new ApplicationSession(source, inspection, _ => writer,
            new CountingSink(), new CountingSink(), processing, ProcessingPlan());

        await session.StartAsync(Configuration, run).WaitAsync(TimeSpan.FromSeconds(10));
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => session.StopAsync().WaitAsync(TimeSpan.FromSeconds(10)));
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => session.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10)));

        CollectionAssert.AreEqual(new[] { "processing-close", "seal-fail" }, events);
        Assert.AreEqual(1, writer.SealCalls);
        Assert.AreEqual(0, processing.FinalCalls);
        Assert.AreEqual(0, processing.DrainCalls);
    }

    [TestMethod]
    public async Task FinalRevisionPublicationRetriesDuringDisposeWithoutResealing()
    {
        await using var source = new ManualSessionSource();
        var inspection = new InspectionId(Guid.NewGuid());
        var run = new AcquisitionRunId(Guid.NewGuid());
        await using var stores = new InspectionDataStoreRegistry(inspection, new(1024, 10));
        List<string> events = [];
        var processing = new OrderedProcessingIngress(events, finalFailures: 1);
        var writer = new SealOrderingWriter(stores.OpenRun(run), events);
        var session = new ApplicationSession(source, inspection, _ => writer,
            new CountingSink(), new CountingSink(), processing, ProcessingPlan());

        await session.StartAsync(Configuration, run).WaitAsync(TimeSpan.FromSeconds(10));
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => session.StopAsync().WaitAsync(TimeSpan.FromSeconds(10)));
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => session.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10)));

        CollectionAssert.AreEqual(new[]
        { "processing-close", "seal", "processing-final-fail", "processing-final", "processing-drain" }, events);
        Assert.AreEqual(1, writer.SealCalls);
        Assert.AreEqual(2, processing.FinalCalls);
        Assert.AreEqual(1, processing.DrainCalls);
    }

    [TestMethod]
    public async Task RepeatedStopAndDisposeDoNotRepeatCompletedProcessingCloseStages()
    {
        await using var source = new ManualSessionSource();
        var inspection = new InspectionId(Guid.NewGuid());
        var run = new AcquisitionRunId(Guid.NewGuid());
        await using var stores = new InspectionDataStoreRegistry(inspection, new(1024, 10));
        List<string> events = [];
        var processing = new OrderedProcessingIngress(events);
        var writer = new SealOrderingWriter(stores.OpenRun(run), events);
        var session = new ApplicationSession(source, inspection, _ => writer,
            new CountingSink(), new CountingSink(), processing, ProcessingPlan());

        await session.StartAsync(Configuration, run).WaitAsync(TimeSpan.FromSeconds(10));
        Task first = session.StopAsync();
        Task second = session.StopAsync();
        await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(10));
        await session.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));

        Assert.AreEqual(1, processing.StopCalls);
        Assert.AreEqual(1, writer.SealCalls);
        Assert.AreEqual(1, processing.FinalCalls);
        Assert.AreEqual(1, processing.DrainCalls);
    }

    private sealed class PostCommitBarrierWriter(
        IInspectionRunWriter inner,
        bool blockTraceCommit = false,
        bool blockGapCommit = false) : IInspectionRunWriter
    {
        private int _disposeCount;
        internal TaskCompletionSource Confirmed { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int DisposeCount => Volatile.Read(ref _disposeCount);
        public AcquisitionRunId RunId => inner.RunId;
        public InspectionRunState State => inner.State;

        public async ValueTask<InspectionAppendResult> CommitAsync(
            InspectionCommitInput commit, CancellationToken cancellationToken = default)
        {
            InspectionAppendResult result = await inner.CommitAsync(commit, cancellationToken);
            if (blockTraceCommit)
            {
                Confirmed.TrySetResult();
                await Release.Task;
            }
            return result;
        }

        public async ValueTask<InspectionAppendResult> CommitGapAsync(
            GapRecord gap, CancellationToken cancellationToken = default)
        {
            InspectionAppendResult result = await inner.CommitGapAsync(gap, cancellationToken);
            if (blockGapCommit)
            {
                Confirmed.TrySetResult();
                await Release.Task;
            }
            return result;
        }

        public ValueTask<InspectionRunSealResult> SealRunAsync(
            CancellationToken cancellationToken = default) =>
            inner.SealRunAsync(cancellationToken);

        public async ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref _disposeCount);
            Release.TrySetResult();
            await inner.DisposeAsync();
        }
    }

    private sealed class CountingSink : ICommittedTraceSink
    {
        internal int NotificationCount { get; private set; }
        public void OpenRun(AcquisitionRunId runId) { }
        public void NotifyCommitted(TraceIdentity identity, InspectionRevision revision) =>
            NotificationCount++;
        public void CloseRun(AcquisitionRunId runId) { }
    }

    private sealed class SealOrderingWriter(
        IInspectionRunWriter inner, List<string> events, int sealFailures = 0) : IInspectionRunWriter
    {
        private int _sealCalls;
        internal int SealCalls => Volatile.Read(ref _sealCalls);
        public AcquisitionRunId RunId => inner.RunId;
        public InspectionRunState State => inner.State;
        public ValueTask<InspectionAppendResult> CommitAsync(InspectionCommitInput commit,
            CancellationToken cancellationToken = default) => inner.CommitAsync(commit, cancellationToken);
        public ValueTask<InspectionAppendResult> CommitGapAsync(GapRecord gap,
            CancellationToken cancellationToken = default) => inner.CommitGapAsync(gap, cancellationToken);
        public ValueTask<InspectionRunSealResult> SealRunAsync(CancellationToken cancellationToken = default)
        {
            int call = Interlocked.Increment(ref _sealCalls);
            Assert.Contains("processing-close", events);
            if (call <= sealFailures)
            {
                events.Add("seal-fail");
                throw new InvalidOperationException("Synthetic seal failure.");
            }
            events.Add("seal");
            return inner.SealRunAsync(cancellationToken);
        }
        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }

    private sealed class OrderedProcessingIngress(
        List<string> events, int stopFailures = 0, int finalFailures = 0) : ICommittedGateProcessingIngress
    {
        private int _stopCalls;
        private int _finalCalls;
        private int _drainCalls;
        internal int StopCalls => Volatile.Read(ref _stopCalls);
        internal int FinalCalls => Volatile.Read(ref _finalCalls);
        internal int DrainCalls => Volatile.Read(ref _drainCalls);
        public GateProcessingAdmissionStatus OpenRun(GateProcessingRunRegistration registration) =>
            GateProcessingAdmissionStatus.Accepted;
        public GateProcessingAdmissionStatus NotifyCommitted(GateProcessingSignal signal) =>
            GateProcessingAdmissionStatus.Accepted;
        public void NotifyProviderProgress(AcquisitionRunId runId) { }
        public void RequestFinalReconciliation(AcquisitionRunId runId, InspectionRevision finalRevision)
        {
            int call = Interlocked.Increment(ref _finalCalls);
            if (call <= finalFailures)
            {
                events.Add("processing-final-fail");
                throw new InvalidOperationException("Synthetic final publication failure.");
            }
            events.Add("processing-final");
        }
        public void StopAdmission(AcquisitionRunId runId)
        {
            int call = Interlocked.Increment(ref _stopCalls);
            if (call <= stopFailures)
            {
                events.Add("processing-close-fail");
                throw new InvalidOperationException("Synthetic processing close failure.");
            }
            events.Add("processing-close");
        }
        public ValueTask WaitForDrainAsync(AcquisitionRunId runId)
        {
            Interlocked.Increment(ref _drainCalls);
            events.Add("processing-drain");
            return ValueTask.CompletedTask;
        }
    }

    private static CompiledGatePlan ProcessingPlan() => new GatePlanCompiler().Compile(
        GateTestPlan.Plan([GateTestPlan.Gate("A", durationSeconds: 0.0000001)])).CompiledPlan!;
}
