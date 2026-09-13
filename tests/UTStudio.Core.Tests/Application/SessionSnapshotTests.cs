using System.Collections.Concurrent;
using UTStudio.Application;
using UTStudio.Domain.Acquisition;

namespace UTStudio.Core.Tests.Application;

[TestClass]
public sealed class SessionSnapshotTests
{
    private static readonly ConventionalAcquisitionConfiguration Configuration = new(new PhysicalChannelId(0), 8, 50_000_000);
    private static Task Watch(Task task) => task.WaitAsync(TimeSpan.FromSeconds(10));

    [TestMethod]
    public async Task SlowObserverCoalescesSnapshotsWithoutBlockingStopOrDisposal()
    {
        await using var source = new ManualSessionSource();
        await using var session = new ApplicationSession(source);
        var entered = ManualSessionSource.Signal();
        var allowReturn = ManualSessionSource.Signal();
        var completed = ManualSessionSource.Signal();
        var received = new ConcurrentQueue<SessionSnapshot>();
        using var subscription = session.Subscribe(new ApplicationSessionTests.CallbackObserver(snapshot =>
        {
            received.Enqueue(snapshot);
            if (snapshot.Version == 0)
            {
                entered.TrySetResult();
                Watch(allowReturn.Task).GetAwaiter().GetResult();
            }
        }, () => completed.TrySetResult()));
        try
        {
            await Watch(entered.Task);
            await Watch(session.StartAsync(Configuration, new AcquisitionRunId(Guid.NewGuid())));
            var owner = source.QueueFrame(0);
            await Watch(owner.Returned.Task);
            await Watch(session.StopAsync());
            await Watch(session.DisposeAsync().AsTask());
            Assert.HasCount(1, received);
            Assert.IsFalse(completed.Task.IsCompleted);
            allowReturn.TrySetResult();
            await Watch(completed.Task);
            var snapshots = received.ToArray();
            Assert.HasCount(2, snapshots);
            Assert.AreEqual(SessionPhase.Idle, snapshots[0].Phase);
            Assert.AreEqual(SessionPhase.Disposed, snapshots[1].Phase);
            Assert.AreEqual(1L, snapshots[1].ReleasedFrames);
            Assert.IsGreaterThan(snapshots[0].Version, snapshots[1].Version);
        }
        finally { allowReturn.TrySetResult(); }
    }

    [TestMethod]
    public async Task UnsubscribeDropsPendingNotificationsWithoutWaitingForAdmittedCallback()
    {
        await using var source = new ManualSessionSource();
        await using var session = new ApplicationSession(source);
        var entered = ManualSessionSource.Signal();
        var allowReturn = ManualSessionSource.Signal();
        var exited = ManualSessionSource.Signal();
        int callbacks = 0;
        using var subscription = session.Subscribe(new ApplicationSessionTests.CallbackObserver(_ =>
        {
            Interlocked.Increment(ref callbacks);
            entered.TrySetResult();
            Watch(allowReturn.Task).GetAwaiter().GetResult();
            exited.TrySetResult();
        }));
        try
        {
            await Watch(entered.Task);
            await Watch(session.StartAsync(Configuration, new AcquisitionRunId(Guid.NewGuid())));
            subscription.Dispose();
            subscription.Dispose();
            await Watch(session.DisposeAsync().AsTask());
            allowReturn.TrySetResult();
            await Watch(exited.Task);
            Assert.AreEqual(1, Volatile.Read(ref callbacks));
        }
        finally { allowReturn.TrySetResult(); }
    }

    [TestMethod]
    public async Task ThrowingObserverIsIsolatedAndLateSubscriberGetsTerminalSnapshot()
    {
        await using var source = new ManualSessionSource();
        await using var session = new ApplicationSession(source);
        var failed = ManualSessionSource.Signal();
        int invocations = 0;
        using var broken = session.Subscribe(new ApplicationSessionTests.CallbackObserver(_ =>
        {
            Interlocked.Increment(ref invocations);
            failed.TrySetResult();
            throw new InvalidOperationException("observer failure");
        }));
        await Watch(failed.Task);
        await Watch(session.StartAsync(Configuration, new AcquisitionRunId(Guid.NewGuid())));
        await ApplicationSessionTests.WaitForPhaseAsync(session, SessionPhase.Running);
        await Watch(session.DisposeAsync().AsTask());
        var completed = ManualSessionSource.Signal();
        SessionSnapshot? last = null;
        using var late = session.Subscribe(new ApplicationSessionTests.CallbackObserver(value => last = value, () => completed.TrySetResult()));
        await Watch(completed.Task);
        Assert.AreEqual(SessionPhase.Disposed, last!.Phase);
        Assert.IsNull(last.PrimaryError);
        Assert.AreEqual(1, Volatile.Read(ref invocations));
    }

    [TestMethod]
    public async Task PreparationSnapshotsExposeExplicitPhasesAndIncreasingVersions()
    {
        await using var source = new ManualSessionSource();
        source.Connect.Held = source.Configure.Held = source.Start.Held = true;
        await using var session = new ApplicationSession(source);
        var initial = session.Snapshot;
        var start = session.StartAsync(Configuration, new AcquisitionRunId(Guid.NewGuid()));
        try
        {
            await Watch(source.Connect.Entered.Task);
            await ApplicationSessionTests.WaitForPhaseAsync(session, SessionPhase.Connecting);
            var connecting = session.Snapshot;
            source.Connect.Continue.TrySetResult();
            await Watch(source.Configure.Entered.Task);
            await ApplicationSessionTests.WaitForPhaseAsync(session, SessionPhase.Configuring);
            var configuring = session.Snapshot;
            source.Configure.Continue.TrySetResult();
            await Watch(source.Start.Entered.Task);
            await ApplicationSessionTests.WaitForPhaseAsync(session, SessionPhase.Starting);
            var starting = session.Snapshot;
            source.Start.Continue.TrySetResult();
            await Watch(start);
            Assert.IsGreaterThan(initial.Version, connecting.Version);
            Assert.IsGreaterThan(connecting.Version, configuring.Version);
            Assert.IsGreaterThan(configuring.Version, starting.Version);
            Assert.IsGreaterThan(starting.Version, session.Snapshot.Version);
            Assert.AreEqual(SessionPhase.Idle, initial.Phase);
            Assert.AreEqual(SessionPhase.Connecting, connecting.Phase);
            Assert.IsNull(initial.RunId);
        }
        finally
        {
            await Watch(session.StopAsync());
            try { await Watch(start); }
            catch (OperationCanceledException) { }
        }
    }
}
