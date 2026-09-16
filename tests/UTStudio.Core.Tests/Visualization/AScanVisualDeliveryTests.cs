using UTStudio.Core.Tests.Application;
using UTStudio.Core.Tests.Simulator;
using UTStudio.Domain.Acquisition;
using UTStudio.Visualization.Core;
using static UTStudio.Core.Tests.Visualization.VisualTestSupport;

namespace UTStudio.Core.Tests.Visualization;

[TestClass]
public sealed class AScanVisualDeliveryTests
{
    [TestMethod]
    public async Task PendingSnapshotIsReplacedAndPublicationIsLimitedWithoutCatchUp()
    {
        var clock = new ManualSimulatorTimeProvider();
        await using var delivery = new AScanVisualDelivery(timeProvider: clock);
        var observer = new Observer();
        using var subscription = delivery.Subscribe(observer);
        var metadata = Metadata(8);
        delivery.OpenRun(metadata.RunId);
        delivery.Accept(metadata, 0, TimeSpan.Zero, new short[8]);
        var first = await observer.NextAsync();
        long firstAt = clock.GetTimestamp();
        delivery.Accept(metadata, 1, TimeSpan.FromTicks(1), new short[8]);
        var timer = await clock.NextTimerAsync();
        delivery.Accept(metadata, 2, TimeSpan.FromTicks(2), new short[8]);
        Assert.AreEqual(3L, delivery.Statistics.Received);
        Assert.AreEqual(1L, delivery.Statistics.Published);
        Assert.AreEqual(1L, delivery.Statistics.Replaced);
        Assert.IsTrue(delivery.Statistics.HasPending);
        timer.FireEarly();
        var remainder = await clock.NextTimerAsync();
        Assert.AreEqual(1L, delivery.Statistics.Published);
        remainder.Fire();
        var latest = await observer.NextAsync();
        Assert.AreEqual(2UL, latest.Sequence);
        Assert.AreEqual(3UL, latest.Version);
        Assert.IsGreaterThan(first.Version, latest.Version);
        Assert.IsGreaterThanOrEqualTo(AScanVisualDelivery.MinimumPublicationInterval, clock.GetElapsedTime(firstAt));
        delivery.Accept(metadata, 3, TimeSpan.FromTicks(3), new short[8]);
        (await clock.NextTimerAsync()).Fire(TimeSpan.FromSeconds(10));
        Assert.AreEqual(3UL, (await observer.NextAsync()).Sequence);
        long afterJump = clock.GetTimestamp();
        delivery.Accept(metadata, 4, TimeSpan.FromTicks(4), new short[8]);
        var next = await clock.NextTimerAsync();
        Assert.AreEqual(3L, delivery.Statistics.Published); // A clock jump cannot authorize a burst.
        next.Fire();
        Assert.AreEqual(4UL, (await observer.NextAsync()).Sequence);
        Assert.IsGreaterThanOrEqualTo(AScanVisualDelivery.MinimumPublicationInterval, clock.GetElapsedTime(afterJump));
    }

    [TestMethod]
    public async Task SlowObserverDoesNotBlockInputAndDisposalWaitsOutsideGlobalLock()
    {
        var clock = new ManualSimulatorTimeProvider();
        await using var delivery = new AScanVisualDelivery(timeProvider: clock);
        var entered = ManualSessionSource.Signal();
        var unblock = ManualSessionSource.Signal();
        var observer = new Observer(snapshot =>
        {
            if (snapshot.Sequence == 0)
            {
                entered.TrySetResult();
                Watch(unblock.Task).GetAwaiter().GetResult();
            }
        });
        using var subscription = delivery.Subscribe(observer);
        var metadata = Metadata(8);
        delivery.OpenRun(metadata.RunId);
        Task? disposal = null;
        try
        {
            delivery.Accept(metadata, 0, TimeSpan.Zero, new short[8]);
            await Watch(entered.Task);
            for (ulong sequence = 1; sequence <= 20; sequence++)
            {
                delivery.Accept(metadata, sequence, TimeSpan.FromTicks((long)sequence), new short[8]);
            }
            (await clock.NextTimerAsync()).Fire();
            await UntilAsync(() => delivery.Statistics.Published == 2);
            Assert.AreEqual(21L, delivery.Statistics.Received);
            Assert.AreEqual(19L, delivery.Statistics.Replaced);
            disposal = delivery.DisposeAsync().AsTask();
            Assert.AreSame(disposal, delivery.DisposeAsync().AsTask());
            Assert.IsFalse(disposal.IsCompleted);
            Assert.IsNull(delivery.Current);
            Assert.IsFalse(delivery.Statistics.HasPending);
        }
        finally { unblock.TrySetResult(); if (disposal is not null) { await Watch(disposal); } }
    }

    [TestMethod]
    public async Task CloseInvalidatesProjectionAlreadyInProgressAndRestartKeepsVersionOrder()
    {
        var projector = new BlockingProjector();
        var clock = new ManualSimulatorTimeProvider();
        await using var delivery = new AScanVisualDelivery(projector, clock);
        var observer = new Observer();
        using var subscription = delivery.Subscribe(observer);
        var metadata = Metadata(8);
        delivery.OpenRun(metadata.RunId);
        var accept = Task.Run(() => delivery.Accept(metadata, 0, TimeSpan.Zero, new short[8]));
        try
        {
            await Watch(projector.Entered.Task);
            delivery.CloseRun(metadata.RunId);
            delivery.CloseRun(metadata.RunId);
            projector.Continue.TrySetResult();
            await Watch(accept);
            Assert.IsNull(delivery.Current);
            Assert.IsFalse(delivery.Statistics.HasPending);
            Assert.AreEqual(1L, delivery.Statistics.Dropped);
            var second = Metadata(8);
            delivery.OpenRun(second.RunId);
            delivery.Accept(second, 0, TimeSpan.Zero, new short[8]);
            var snapshot = await observer.NextAsync();
            Assert.AreEqual(second.RunId, snapshot.Metadata.RunId);
            Assert.AreEqual(2UL, snapshot.Version);
            delivery.Accept(metadata, 1, TimeSpan.FromTicks(1), new short[8]);
            Assert.AreEqual(2L, delivery.Statistics.Dropped);
        }
        finally { projector.Continue.TrySetResult(); await Watch(accept); }
    }

    [TestMethod]
    public async Task NoInterestSkipsProjectionAndUnsubscribeClearsPending()
    {
        var projector = new CountingProjector();
        var clock = new ManualSimulatorTimeProvider();
        await using var delivery = new AScanVisualDelivery(projector, clock);
        var metadata = Metadata(8);
        delivery.OpenRun(metadata.RunId);
        delivery.Accept(metadata, 0, TimeSpan.Zero, new short[8]);
        Assert.AreEqual(0, projector.Calls);
        var observer = new Observer();
        using var subscription = delivery.Subscribe(observer);
        delivery.Accept(metadata, 1, TimeSpan.FromTicks(1), new short[8]);
        await observer.NextAsync();
        delivery.Accept(metadata, 2, TimeSpan.FromTicks(2), new short[8]);
        await clock.NextTimerAsync();
        subscription.Dispose();
        subscription.Dispose();
        Assert.IsNull(delivery.Current);
        Assert.IsFalse(delivery.Statistics.HasPending);
        Assert.AreEqual(2L, delivery.Statistics.Dropped);
    }

    [TestMethod]
    public async Task ThrowingObserverIsDetachedAndOtherObserverContinues()
    {
        var clock = new ManualSimulatorTimeProvider();
        await using var delivery = new AScanVisualDelivery(timeProvider: clock);
        using var broken = delivery.Subscribe(new Observer(_ => throw new InvalidOperationException("observer failed")));
        var healthy = new Observer();
        using var subscription = delivery.Subscribe(healthy);
        var metadata = Metadata(8);
        delivery.OpenRun(metadata.RunId);
        delivery.Accept(metadata, 0, TimeSpan.Zero, new short[8]);
        await healthy.NextAsync();
        await UntilAsync(() => delivery.Statistics.ObserverErrors == 1);
        Assert.AreEqual("visual.observer", delivery.Statistics.LastError!.Code);
        delivery.Accept(metadata, 1, TimeSpan.FromTicks(1), new short[8]);
        (await clock.NextTimerAsync()).Fire();
        Assert.AreEqual(1UL, (await healthy.NextAsync()).Sequence);
        Assert.AreEqual(1L, delivery.Statistics.ObserverErrors);
    }

    [TestMethod]
    public async Task StopCancelsClockWaitAndRepeatedDisposalIsSafe()
    {
        var clock = new ManualSimulatorTimeProvider();
        await using var delivery = new AScanVisualDelivery(timeProvider: clock);
        var observer = new Observer();
        using var subscription = delivery.Subscribe(observer);
        var metadata = Metadata(8);
        delivery.OpenRun(metadata.RunId);
        delivery.Accept(metadata, 0, TimeSpan.Zero, new short[8]);
        await observer.NextAsync();
        delivery.Accept(metadata, 1, TimeSpan.FromTicks(1), new short[8]);
        var timer = await clock.NextTimerAsync();
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        try { await Watch(delivery.StopAsync(cancelled.Token)); }
        catch (OperationCanceledException) { }
        await Watch(delivery.StopAsync());
        await Watch(delivery.DisposeAsync().AsTask());
        Assert.IsTrue(timer.IsDisposed);
        Assert.IsNull(delivery.Current);
        Assert.IsFalse(delivery.Statistics.HasPending);
        delivery.Accept(metadata, 2, TimeSpan.FromTicks(2), new short[8]);
        Assert.AreEqual(1L, delivery.Statistics.Published);
    }

    [TestMethod]
    public async Task TimerFailureIsObservableAndDisablesOnlyVisualDelivery()
    {
        var clock = new ManualSimulatorTimeProvider();
        await using var delivery = new AScanVisualDelivery(timeProvider: clock);
        var observer = new Observer();
        using var subscription = delivery.Subscribe(observer);
        var metadata = Metadata(8);
        delivery.OpenRun(metadata.RunId);
        delivery.Accept(metadata, 0, TimeSpan.Zero, new short[8]);
        await observer.NextAsync();
        clock.FailTimerCreation = true;
        delivery.Accept(metadata, 1, TimeSpan.FromTicks(1), new short[8]);
        await UntilAsync(() => delivery.Statistics.LastError is not null);
        Assert.AreEqual("visual.publication", delivery.Statistics.LastError!.Code);
        Assert.IsNull(delivery.Current);
        Assert.IsFalse(delivery.Statistics.HasPending);
        delivery.Accept(metadata, 2, TimeSpan.FromTicks(2), new short[8]);
        Assert.AreEqual(1L, delivery.Statistics.Published);
    }

    [TestMethod]
    public async Task SubscriberTimerCleanupRunsOutsideLockAndItsFailureIsObserved()
    {
        var clock = new ManualSimulatorTimeProvider();
        await using var delivery = new AScanVisualDelivery(timeProvider: clock);
        var initial = new Observer();
        using var firstSubscription = delivery.Subscribe(initial);
        var metadata = Metadata(8);
        delivery.OpenRun(metadata.RunId);
        delivery.Accept(metadata, 0, TimeSpan.Zero, new short[8]);
        await initial.NextAsync();
        delivery.Accept(metadata, 1, TimeSpan.FromTicks(1), new short[8]);
        (await clock.NextTimerAsync()).FireEarly();
        var remainder = await clock.NextTimerAsync();
        var late = new Observer();
        using var lateSubscription = delivery.Subscribe(late);
        await late.NextAsync(); // Admitted halfway through the publisher's interval.
        remainder.Fire();
        await initial.NextAsync();
        var subscriberTimer = await clock.NextTimerAsync();
        clock.OnTimerDispose = () =>
        {
            Watch(Task.Run(() => _ = delivery.Statistics)).GetAwaiter().GetResult();
            throw new InvalidOperationException("timer disposal failed");
        };
        lateSubscription.Dispose();
        await UntilAsync(() => delivery.Statistics.LastError?.Code == "visual.timer_cleanup");
        Assert.IsTrue(subscriberTimer.IsDisposed);
        Assert.AreEqual("timer disposal failed", delivery.Statistics.LastError!.Message);
        await Watch(delivery.DisposeAsync().AsTask());
    }

    [TestMethod]
    public async Task PublisherTimerCleanupFailureDoesNotSkipWorkerShutdown()
    {
        var clock = new ManualSimulatorTimeProvider();
        await using var delivery = new AScanVisualDelivery(timeProvider: clock);
        var observer = new Observer();
        using var subscription = delivery.Subscribe(observer);
        var metadata = Metadata(8);
        delivery.OpenRun(metadata.RunId);
        delivery.Accept(metadata, 0, TimeSpan.Zero, new short[8]);
        await observer.NextAsync();
        delivery.Accept(metadata, 1, TimeSpan.FromTicks(1), new short[8]);
        var timer = await clock.NextTimerAsync();
        clock.OnTimerDispose = () =>
        {
            Watch(Task.Run(() => _ = delivery.Current)).GetAwaiter().GetResult();
            throw new InvalidOperationException("publisher timer disposal failed");
        };
        await Watch(delivery.DisposeAsync().AsTask());
        await Watch(delivery.DisposeAsync().AsTask());
        Assert.IsTrue(timer.IsDisposed);
        Assert.IsNull(delivery.Current);
        Assert.IsFalse(delivery.Statistics.HasPending);
        Assert.AreEqual("publisher timer disposal failed", delivery.Statistics.LastError!.Message);
    }

    private sealed class BlockingProjector : IAScanProjector
    {
        internal TaskCompletionSource Entered { get; } = ManualSessionSource.Signal();
        internal TaskCompletionSource Continue { get; } = ManualSessionSource.Signal();
        public AScanSnapshot Project(ConventionalUtFrameMetadata metadata, ulong sequence, TimeSpan elapsedSinceRunStart,
            ReadOnlySpan<short> samples, ulong version)
        {
            Entered.TrySetResult();
            Watch(Continue.Task).GetAwaiter().GetResult();
            return new AScanProjector().Project(metadata, sequence, elapsedSinceRunStart, samples, version);
        }
    }

    private sealed class CountingProjector : IAScanProjector
    {
        internal int Calls { get; private set; }
        public AScanSnapshot Project(ConventionalUtFrameMetadata metadata, ulong sequence, TimeSpan elapsedSinceRunStart,
            ReadOnlySpan<short> samples, ulong version)
        {
            Calls++;
            return new AScanProjector().Project(metadata, sequence, elapsedSinceRunStart, samples, version);
        }
    }
}
