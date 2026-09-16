using UTStudio.Core.Tests.Simulator;
using UTStudio.Core.Tests.TestDoubles;
using UTStudio.Visualization.Core;
using static UTStudio.Core.Tests.Visualization.VisualTestSupport;

namespace UTStudio.Core.Tests.Visualization;

[TestClass]
public sealed class VisualSubscriptionTests
{
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task CancellationBetweenExtractionAndCallbackAttemptPreventsEntry(bool disposeService)
    {
        await using var delivery = new AScanVisualDelivery(timeProvider: new ManualSimulatorTimeProvider());
        var extracted = Signal();
        var attempt = Signal();
        var release = Signal();
        int callbacks = 0;
        delivery.BeforeCallbackAttempt = () =>
        {
            extracted.TrySetResult();
            DiagnosticWait.For(release.Task, "allow extracted snapshot to attempt callback").GetAwaiter().GetResult();
        };
        delivery.AfterCallbackAttempt = () => attempt.TrySetResult();
        using var subscription = delivery.Subscribe(new Observer(_ => Interlocked.Increment(ref callbacks)));
        var metadata = Metadata(8);
        delivery.OpenRun(metadata.RunId);
        try
        {
            delivery.Accept(metadata, 0, TimeSpan.Zero, new short[8]);
            await DiagnosticWait.For(extracted.Task, "snapshot extracted before callback entry");
            if (disposeService) { await DiagnosticWait.For(delivery.DisposeAsync().AsTask(), "service disposal before callback attempt"); }
            else { subscription.Dispose(); subscription.Dispose(); }
            Assert.AreEqual(0, callbacks);
        }
        finally { release.TrySetResult(); await DiagnosticWait.For(attempt.Task, "cancelled callback attempt finished"); }
        Assert.AreEqual(0, callbacks);
    }

    [TestMethod]
    public async Task ObserverCanCancelItselfWithoutDeadlockOrAnotherCallback()
    {
        await using var delivery = new AScanVisualDelivery(timeProvider: new ManualSimulatorTimeProvider());
        var cancelled = Signal();
        IDisposable? subscription = null;
        int callbacks = 0;
        subscription = delivery.Subscribe(new Observer(_ =>
        {
            Interlocked.Increment(ref callbacks);
            subscription!.Dispose();
            subscription.Dispose();
            cancelled.TrySetResult();
        }));
        var metadata = Metadata(8);
        delivery.OpenRun(metadata.RunId);
        try
        {
            delivery.Accept(metadata, 0, TimeSpan.Zero, new short[8]);
            await DiagnosticWait.For(cancelled.Task, "observer self-cancellation returned");
            delivery.Accept(metadata, 1, TimeSpan.FromTicks(1), new short[8]);
            await DiagnosticWait.For(delivery.DisposeAsync().AsTask());
            Assert.AreEqual(1, callbacks);
        }
        finally { subscription.Dispose(); }
    }

    [TestMethod]
    public async Task ConcurrentCancellationsWaitForSlowObserverButNotItsPeersOrInput()
    {
        var clock = new ManualSimulatorTimeProvider();
        await using var delivery = new AScanVisualDelivery(timeProvider: clock);
        var entered = Signal();
        var release = Signal();
        int callbacks = 0;
        using var slow = delivery.Subscribe(new Observer(_ =>
        {
            Interlocked.Increment(ref callbacks);
            entered.TrySetResult();
            DiagnosticWait.For(release.Task, "release slow observer").GetAwaiter().GetResult();
        }));
        var healthy = new Observer();
        using var peer = delivery.Subscribe(healthy);
        var metadata = Metadata(8);
        delivery.OpenRun(metadata.RunId);
        Task first = Task.CompletedTask, second = Task.CompletedTask;
        try
        {
            delivery.Accept(metadata, 0, TimeSpan.Zero, new short[8]);
            await DiagnosticWait.For(entered.Task, "slow callback began");
            await healthy.NextAsync();
            var cancelling = Signal();
            int waiting = 0;
            delivery.BeforeSubscriptionWait = () => { if (Interlocked.Increment(ref waiting) == 2) { cancelling.TrySetResult(); } };
            first = Task.Run(slow.Dispose);
            second = Task.Run(slow.Dispose);
            await DiagnosticWait.For(cancelling.Task, "both cancellations detached and reached their callback barrier");
            Assert.IsFalse(first.IsCompleted);
            Assert.IsFalse(second.IsCompleted);
            delivery.Accept(metadata, 1, TimeSpan.FromTicks(1), new short[8]);
            (await clock.NextTimerAsync()).Fire();
            Assert.AreEqual(1UL, (await healthy.NextAsync()).Sequence);
            Assert.AreEqual(2L, delivery.Statistics.Received);
        }
        finally
        {
            release.TrySetResult();
            await DiagnosticWait.For(Task.WhenAll(first, second), "both cancellations returned after observer completed");
        }
        Assert.AreEqual(1, callbacks);
    }
}
