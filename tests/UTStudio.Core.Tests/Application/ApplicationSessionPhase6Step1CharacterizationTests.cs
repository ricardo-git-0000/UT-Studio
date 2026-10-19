using UTStudio.Application;
using UTStudio.Contracts.Inspection;
using UTStudio.Domain.Acquisition;
using UTStudio.Domain.Inspection;
using UTStudio.Inspection.Core;

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
}
