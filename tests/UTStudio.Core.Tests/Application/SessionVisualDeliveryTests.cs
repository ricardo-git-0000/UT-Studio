using UTStudio.Contracts.Application;
using UTStudio.Application;
using UTStudio.Contracts.Presentation;
using UTStudio.Core.Tests.Simulator;
using UTStudio.Core.Tests.Visualization;
using UTStudio.Domain.Acquisition;
using UTStudio.Visualization.Core;
using static UTStudio.Core.Tests.Visualization.VisualTestSupport;

namespace UTStudio.Core.Tests.Application;

[TestClass]
public sealed class SessionVisualDeliveryTests
{
    private static readonly ConventionalAcquisitionConfiguration Configuration = new(new PhysicalChannelId(0), 8, 50_000_000);

    [TestMethod]
    public async Task SessionPublishesIndependentDataAndReleasesEveryFrameWithSlowObserver()
    {
        var clock = new ManualSimulatorTimeProvider();
        await using var source = new ManualSessionSource();
        await using var delivery = new AScanVisualDelivery(timeProvider: clock);
        await using var session = new ApplicationSession(source, delivery);
        var entered = ManualSessionSource.Signal();
        var unblock = ManualSessionSource.Signal();
        AScanSnapshot? first = null;
        using var subscription = delivery.Subscribe(new Observer(snapshot =>
        {
            first = snapshot;
            entered.TrySetResult();
            Watch(unblock.Task).GetAwaiter().GetResult();
        }));
        try
        {
            var runId = new AcquisitionRunId(Guid.NewGuid());
            await Watch(session.StartAsync(Configuration, runId));
            var owner = source.QueueFrame(0);
            await Watch(entered.Task);
            await Watch(owner.Returned.Task);
            owner.Memory.Span.Fill(short.MaxValue); // Returned pool storage no longer backs the snapshot.
            Assert.AreEqual(0.0, first!.Points[0].AmplitudePercent);
            var owners = new List<ManualSessionSource.CountingOwner> { owner };
            for (ulong sequence = 1; sequence <= 10; sequence++)
            {
                var next = source.QueueFrame(sequence);
                owners.Add(next);
                await Watch(next.Returned.Task);
            }
            Assert.AreEqual(SessionPhase.Running, session.Snapshot.Phase);
            Assert.AreEqual(11L, delivery.Statistics.Received);
            Assert.AreEqual(9L, delivery.Statistics.Replaced);
            await Watch(session.StopAsync());
            Assert.AreEqual(11L, session.Snapshot.ReleasedFrames);
            Assert.IsTrue(owners.All(item => item.DisposeCount == 1));
            Assert.IsTrue(source.Released.Task.IsCompletedSuccessfully);
            Assert.IsNull(delivery.Current);
            Assert.IsFalse(delivery.Statistics.HasPending);
            await Watch(session.DisposeAsync().AsTask());
        }
        finally { unblock.TrySetResult(); }
    }

    [TestMethod]
    [DataRow("Open")]
    [DataRow("Accept")]
    [DataRow("Close")]
    public async Task BrokenBorrowedSinkDoesNotStopAcquisitionOrLeakFrames(string failingOperation)
    {
        await using var source = new ManualSessionSource();
        var sink = new FailingSink(failingOperation);
        await using var session = new ApplicationSession(source, sink);
        await Watch(session.StartAsync(Configuration, new AcquisitionRunId(Guid.NewGuid())));
        var first = source.QueueFrame(0);
        await Watch(first.Returned.Task);
        var second = source.QueueFrame(1);
        await Watch(second.Returned.Task);
        Assert.AreEqual(SessionPhase.Running, session.Snapshot.Phase);
        Assert.IsFalse(source.StopEntered.Task.IsCompleted);
        await Watch(session.StopAsync());
        Assert.IsNull(session.Snapshot.PrimaryError);
        Assert.AreEqual("session.visual", session.Snapshot.VisualError!.Code);
        Assert.AreEqual(2L, session.Snapshot.ReleasedFrames);
        Assert.AreEqual(1, first.DisposeCount);
        Assert.AreEqual(1, second.DisposeCount);
        Assert.AreEqual(1, sink.CloseCalls);
        Assert.AreEqual(failingOperation == "Open" ? 0 : failingOperation == "Accept" ? 1 : 2, sink.AcceptCalls);
    }

    [TestMethod]
    public async Task ProjectionFailureReleasesFrameAndDisablesBranchWithoutStoppingSource()
    {
        await using var source = new ManualSessionSource();
        await using var delivery = new AScanVisualDelivery(new FailingProjector(), new ManualSimulatorTimeProvider());
        using var subscription = delivery.Subscribe(new Observer());
        await using var session = new ApplicationSession(source, delivery);
        await Watch(session.StartAsync(Configuration, new AcquisitionRunId(Guid.NewGuid())));
        var first = source.QueueFrame(0);
        await Watch(first.Returned.Task);
        var second = source.QueueFrame(1);
        await Watch(second.Returned.Task);
        Assert.AreEqual(SessionPhase.Running, session.Snapshot.Phase);
        Assert.IsNotNull(session.Snapshot.VisualError);
        Assert.IsNull(session.Snapshot.PrimaryError);
        Assert.AreEqual("visual.projection", delivery.Statistics.LastError!.Code);
        Assert.AreEqual(1L, delivery.Statistics.Received);
        Assert.AreEqual(0L, delivery.Statistics.Published);
        await Watch(session.StopAsync());
        Assert.AreEqual(2L, session.Snapshot.ReleasedFrames);
        Assert.AreEqual(1, first.DisposeCount);
        Assert.AreEqual(1, second.DisposeCount);
    }

    private sealed class FailingSink(string failure) : IConventionalFrameSink
    {
        internal int AcceptCalls { get; private set; }
        internal int CloseCalls { get; private set; }
        public void OpenRun(AcquisitionRunId runId)
        {
            if (failure == "Open") { throw new InvalidOperationException("visual opening failed"); }
        }
        public void Accept(ConventionalUtFrameMetadata metadata, ulong sequence, TimeSpan elapsedSinceRunStart, ReadOnlySpan<short> samples)
        {
            AcceptCalls++;
            Assert.AreEqual(metadata.Configuration.SampleCount, samples.Length);
            if (failure == "Accept") { throw new InvalidOperationException("visual handoff failed"); }
        }
        public void CloseRun(AcquisitionRunId runId)
        {
            CloseCalls++;
            if (failure == "Close") { throw new InvalidOperationException("visual closing failed"); }
        }
    }

    private sealed class FailingProjector : IAScanProjector
    {
        public AScanSnapshot Project(ConventionalUtFrameMetadata metadata, ulong sequence, TimeSpan elapsedSinceRunStart,
            ReadOnlySpan<short> samples, ulong version) => throw new InvalidOperationException("synthetic projection failure");
    }
}
