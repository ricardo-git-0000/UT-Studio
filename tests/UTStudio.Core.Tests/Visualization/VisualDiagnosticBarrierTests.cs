using UTStudio.Core.Tests.Application;
using UTStudio.Core.Tests.Simulator;
using UTStudio.Core.Tests.TestDoubles;
using UTStudio.Visualization.Core;
using static UTStudio.Core.Tests.Visualization.VisualTestSupport;

namespace UTStudio.Core.Tests.Visualization;

[TestClass]
public sealed class VisualDiagnosticBarrierTests
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(10);

    [TestMethod]
    public async Task BarrierReachesRegisteredSignalWaitWithoutVisualInterest()
    {
        await using var delivery = new AScanVisualDelivery(timeProvider: new ManualSimulatorTimeProvider());
        var metadata = Metadata(8);
        delivery.OpenRun(metadata.RunId);
        await delivery.WaitForDiagnosticStateAsync(new(VisualDiagnosticWorkerState.WaitingForSignal, false), Limit);
        delivery.Accept(metadata, 0, TimeSpan.Zero, new short[8]);
        Assert.AreEqual(1L, delivery.Statistics.Dropped);
    }

    [TestMethod]
    public async Task InitialCallbackMustReturnBeforeSignalBarrierCompletes()
    {
        await using var delivery = new AScanVisualDelivery(timeProvider: new ManualSimulatorTimeProvider());
        var entered = ManualSessionSource.Signal();
        var release = ManualSessionSource.Signal();
        using var subscription = delivery.Subscribe(new Observer(_ =>
        {
            entered.TrySetResult();
            DiagnosticWait.For(release.Task, "release diagnostic observer").GetAwaiter().GetResult();
        }));
        var metadata = Metadata(8);
        delivery.OpenRun(metadata.RunId);
        delivery.Accept(metadata, 0, TimeSpan.Zero, new short[8]);
        try
        {
            await DiagnosticWait.For(entered.Task, "diagnostic callback entered");
            Task barrier = delivery.WaitForDiagnosticStateAsync(new(VisualDiagnosticWorkerState.WaitingForSignal, false), Limit);
            Assert.IsFalse(barrier.IsCompleted);
            release.TrySetResult();
            await DiagnosticWait.For(barrier, "callback returned and worker reached signal wait");
        }
        finally { release.TrySetResult(); }
    }

    [TestMethod]
    public async Task ProjectionInProgressPreventsBarrierCompletion()
    {
        var projector = new BlockingProjector();
        await using var delivery = new AScanVisualDelivery(projector, new ManualSimulatorTimeProvider());
        using var subscription = delivery.Subscribe(new Observer());
        var metadata = Metadata(8);
        delivery.OpenRun(metadata.RunId);
        Task accept = Task.Run(() => delivery.Accept(metadata, 0, TimeSpan.Zero, new short[8]));
        try
        {
            await DiagnosticWait.For(projector.Entered.Task, "projection entered");
            Task barrier = delivery.WaitForDiagnosticStateAsync(new(VisualDiagnosticWorkerState.WaitingForSignal, false), Limit);
            Assert.IsFalse(barrier.IsCompleted);
            projector.Release.TrySetResult();
            await DiagnosticWait.For(accept, "projection completed");
            await DiagnosticWait.For(barrier, "projected snapshot and callback drained");
        }
        finally { projector.Release.TrySetResult(); await DiagnosticWait.For(accept, "projection cleanup"); }
    }

    [TestMethod]
    public async Task InvalidatedFailingProjectionReevaluatesNewGenerationBarrier()
    {
        var projector = new BlockingProjector(throwAfterRelease: true);
        await using var delivery = new AScanVisualDelivery(projector, new ManualSimulatorTimeProvider());
        using var subscription = delivery.Subscribe(new Observer());
        var first = Metadata(8);
        delivery.OpenRun(first.RunId);
        Task accept = Task.Run(() => delivery.Accept(first, 0, TimeSpan.Zero, new short[8]));
        await DiagnosticWait.For(projector.Entered.Task, "failing projection entered");
        delivery.CloseRun(first.RunId);
        var second = Metadata(8);
        delivery.OpenRun(second.RunId);
        Task barrier = delivery.WaitForDiagnosticStateAsync(new(VisualDiagnosticWorkerState.WaitingForSignal, false), Limit);
        projector.Release.TrySetResult();
        await Assert.ThrowsAsync<InvalidOperationException>(() => accept);
        await DiagnosticWait.For(barrier, "new generation after invalidated projection failure");
    }

    [TestMethod]
    public async Task CancellingOneOfMultipleWaitersDoesNotAffectTheOther()
    {
        await using var delivery = new AScanVisualDelivery(timeProvider: new ManualSimulatorTimeProvider());
        var metadata = Metadata(8);
        delivery.OpenRun(metadata.RunId);
        using var cancellation = new CancellationTokenSource();
        Task cancelled = delivery.WaitForDiagnosticStateAsync(new(VisualDiagnosticWorkerState.WaitingForTimer, true), Limit, cancellation.Token);
        Task surviving = delivery.WaitForDiagnosticStateAsync(new(VisualDiagnosticWorkerState.WaitingForSignal, false), Limit);
        cancellation.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => cancelled);
        await DiagnosticWait.For(surviving, "independent diagnostic waiter");
    }

    [TestMethod]
    public async Task DiagnosticTimeoutIsReportedWithoutChangingDelivery()
    {
        await using var delivery = new AScanVisualDelivery(timeProvider: new ManualSimulatorTimeProvider());
        await Assert.ThrowsAsync<TimeoutException>(() => delivery.WaitForDiagnosticStateAsync(
            new(VisualDiagnosticWorkerState.WaitingForTimer, true), TimeSpan.FromMilliseconds(20)));
        await delivery.WaitForDiagnosticStateAsync(
            new(VisualDiagnosticWorkerState.WaitingForSignal, false), Limit);
    }

    [TestMethod]
    public async Task DiagnosticDisposalFaultsWaiterAndWaitsForBlockedCallbackPump()
    {
        var delivery = new AScanVisualDelivery(timeProvider: new ManualSimulatorTimeProvider());
        var entered = ManualSessionSource.Signal();
        var release = ManualSessionSource.Signal();
        _ = delivery.Subscribe(new Observer(_ =>
        {
            entered.TrySetResult();
            DiagnosticWait.For(release.Task, "release disposal observer").GetAwaiter().GetResult();
        }));
        var metadata = Metadata(8);
        delivery.OpenRun(metadata.RunId);
        delivery.Accept(metadata, 0, TimeSpan.Zero, new short[8]);
        await DiagnosticWait.For(entered.Task, "disposal callback entered");
        Task waiter = delivery.WaitForDiagnosticStateAsync(new(VisualDiagnosticWorkerState.WaitingForTimer, true), Limit);
        Task disposal = delivery.DisposeForDiagnosticsAsync(Limit);
        try
        {
            Assert.IsFalse(disposal.IsCompleted);
            await Assert.ThrowsAsync<ObjectDisposedException>(() => waiter);
        }
        finally { release.TrySetResult(); await DiagnosticWait.For(disposal, "visual diagnostic disposal"); }
    }

    [TestMethod]
    public async Task TimerBarrierPreservesLatestOnlyAndLeavesNoPreparatoryWorkForMeasuredAccept()
    {
        var clock = new ManualSimulatorTimeProvider();
        var projector = new CountingProjector();
        await using var delivery = new AScanVisualDelivery(projector, clock);
        int callbacks = 0;
        using var subscription = delivery.Subscribe(new Observer(_ => Interlocked.Increment(ref callbacks)));
        var metadata = Metadata(8);
        delivery.OpenRun(metadata.RunId);
        delivery.Accept(metadata, 0, TimeSpan.Zero, new short[8]);
        await delivery.WaitForDiagnosticStateAsync(new(VisualDiagnosticWorkerState.WaitingForSignal, false), Limit);
        Assert.AreEqual(1, projector.Calls);
        Assert.AreEqual(1, callbacks);
        delivery.Accept(metadata, 1, TimeSpan.FromTicks(1), new short[8]);
        await delivery.WaitForDiagnosticStateAsync(new(VisualDiagnosticWorkerState.WaitingForTimer, true), Limit);
        int preparedProjections = projector.Calls, preparedCallbacks = callbacks;
        delivery.Accept(metadata, 2, TimeSpan.FromTicks(2), new short[8]);
        Assert.AreEqual(preparedProjections + 1, projector.Calls);
        Assert.AreEqual(preparedCallbacks, callbacks);
        Assert.AreEqual(1L, delivery.Statistics.Replaced);
    }

    private sealed class BlockingProjector(bool throwAfterRelease = false) : IAScanProjector
    {
        internal TaskCompletionSource Entered { get; } = ManualSessionSource.Signal();
        internal TaskCompletionSource Release { get; } = ManualSessionSource.Signal();
        public AScanSnapshot Project(UTStudio.Domain.Acquisition.ConventionalUtFrameMetadata metadata, ulong sequence,
            TimeSpan elapsedSinceRunStart, ReadOnlySpan<short> samples, ulong version)
        {
            Entered.TrySetResult();
            DiagnosticWait.For(Release.Task, "release blocked projection").GetAwaiter().GetResult();
            if (throwAfterRelease) { throw new InvalidOperationException("synthetic invalidated projection failure"); }
            return new AScanProjector().Project(metadata, sequence, elapsedSinceRunStart, samples, version);
        }
    }

    private sealed class CountingProjector : IAScanProjector
    {
        internal int Calls { get; private set; }
        public AScanSnapshot Project(UTStudio.Domain.Acquisition.ConventionalUtFrameMetadata metadata, ulong sequence,
            TimeSpan elapsedSinceRunStart, ReadOnlySpan<short> samples, ulong version)
        {
            Calls++;
            return new AScanProjector().Project(metadata, sequence, elapsedSinceRunStart, samples, version);
        }
    }
}
