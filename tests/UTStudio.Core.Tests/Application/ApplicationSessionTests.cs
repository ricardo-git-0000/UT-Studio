using UTStudio.Acquisition.Simulator;
using UTStudio.Application;
using UTStudio.Core.Tests.Simulator;
using UTStudio.Domain.Acquisition;

namespace UTStudio.Core.Tests.Application;

[TestClass]
public sealed class ApplicationSessionTests
{
    private static readonly ConventionalAcquisitionConfiguration Configuration = new(new PhysicalChannelId(0), 8, 50_000_000);
    private static AcquisitionRunId NewRun() => new(Guid.NewGuid());
    private static Task Watch(Task task) => task.WaitAsync(TimeSpan.FromSeconds(10));

    [TestMethod]
    public async Task StartStopDispose_OrderAndBorrowedSource()
    {
        var source = new ManualSessionSource();
        var session = new ApplicationSession(source);
        try
        {
            var initial = session.Snapshot;
            var runId = NewRun();
            await Watch(session.StartAsync(Configuration, runId));
            CollectionAssert.AreEqual(new[] { "Connect", "Configure", "Start" }, source.Calls.ToArray());
            Assert.AreEqual(SessionPhase.Running, session.Snapshot.Phase);
            Assert.AreEqual(runId, session.Snapshot.RunId);
            await Watch(session.StopAsync());
            await Watch(session.StopAsync());
            CollectionAssert.AreEqual(new[] { "Connect", "Configure", "Start", "Stop" }, source.Calls.ToArray());
            Assert.AreEqual(SessionPhase.Idle, session.Snapshot.Phase);
            Assert.AreEqual(SessionPhase.Idle, initial.Phase);
            Assert.AreEqual(0UL, initial.Version);
            Assert.IsGreaterThan(initial.Version, session.Snapshot.Version);
            var disposal = session.DisposeAsync().AsTask();
            Assert.AreSame(disposal, session.DisposeAsync().AsTask());
            await Watch(disposal);
            Assert.AreEqual(SessionPhase.Disposed, session.Snapshot.Phase);
            Assert.AreEqual(1, source.Calls.Count(call => call == "Disconnect"));
            Assert.AreEqual(0, source.DisposeCount);
            Assert.Throws<ObjectDisposedException>(() => session.StartAsync(Configuration, NewRun()));
        }
        finally { await FinishAsync(session, source); }
    }

    [TestMethod]
    public async Task SecondStartIsRejectedDuringPreparationAndRunning()
    {
        var source = new ManualSessionSource();
        source.Connect.Held = true;
        var session = new ApplicationSession(source);
        try
        {
            var start = session.StartAsync(Configuration, NewRun());
            await Watch(source.Connect.Entered.Task);
            Assert.Throws<InvalidOperationException>(() => session.StartAsync(Configuration, NewRun()));
            source.Connect.Continue.TrySetResult();
            await Watch(start);
            Assert.Throws<InvalidOperationException>(() => session.StartAsync(Configuration, NewRun()));
            Assert.AreEqual(1, source.Calls.Count(call => call == "Start"));
        }
        finally { await FinishAsync(session, source); }
    }

    [TestMethod]
    [DataRow("Connect", false)]
    [DataRow("Configure", false)]
    [DataRow("Start", false)]
    [DataRow("Connect", true)]
    [DataRow("Configure", true)]
    [DataRow("Start", true)]
    public async Task StopOrDisposeCancelsPendingPreparationBeforeWaiting(string phase, bool dispose)
    {
        var source = new ManualSessionSource();
        var step = phase switch { "Connect" => source.Connect, "Configure" => source.Configure, _ => source.Start };
        step.Held = true;
        var session = new ApplicationSession(source);
        try
        {
            var start = session.StartAsync(Configuration, NewRun());
            await Watch(step.Entered.Task);
            var stop = dispose ? session.DisposeAsync().AsTask() : session.StopAsync();
            await Watch(step.Cancelled.Task); // Continue remains unset: cancellation must unblock the operation.
            await Watch(stop);
            await Assert.ThrowsAsync<OperationCanceledException>(() => Watch(start));
            Assert.AreEqual(1, source.Calls.Count(call => call == "Stop"));
            Assert.AreEqual(1, source.Calls.Count(call => call == "Disconnect"));
            Assert.AreEqual(dispose ? SessionPhase.Disposed : SessionPhase.Idle, session.Snapshot.Phase);
            Assert.IsNull(session.Snapshot.PrimaryError);
        }
        finally { await FinishAsync(session, source); }
    }

    [TestMethod]
    public async Task SourceRollbackIsObservedBeforeStopAndDisconnect()
    {
        var source = new ManualSessionSource();
        source.Start.Held = true;
        source.Rollback.Held = true;
        var session = new ApplicationSession(source);
        try
        {
            var start = session.StartAsync(Configuration, NewRun());
            await Watch(source.Start.Entered.Task);
            var stop = session.StopAsync();
            await Watch(source.Rollback.Entered.Task);
            Assert.IsFalse(start.IsCompleted);
            Assert.IsFalse(stop.IsCompleted);
            Assert.IsFalse(source.StopEntered.Task.IsCompleted);
            source.Rollback.Continue.TrySetResult();
            await Watch(stop);
            await Assert.ThrowsAsync<OperationCanceledException>(() => Watch(start));
            CollectionAssert.AreEqual(new[] { "Connect", "Configure", "Start", "Rollback", "RollbackFinished", "Stop", "Disconnect" }, source.Calls.ToArray());
        }
        finally { await FinishAsync(session, source); }
    }

    [TestMethod]
    [DataRow("Connect")]
    [DataRow("Configure")]
    [DataRow("Start")]
    public async Task PartialFailureDisconnectsAndPreservesOriginalError(string phase)
    {
        var source = new ManualSessionSource();
        var step = phase switch { "Connect" => source.Connect, "Configure" => source.Configure, _ => source.Start };
        var original = new InvalidOperationException("preparation failed");
        step.Error = original;
        source.StopError = new InvalidOperationException("stop failed");
        var session = new ApplicationSession(source);
        try
        {
            var error = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => Watch(session.StartAsync(Configuration, NewRun())));
            Assert.AreSame(original, error);
            Assert.AreEqual(SessionPhase.Faulted, session.Snapshot.Phase);
            Assert.AreEqual("preparation failed", session.Snapshot.PrimaryError!.Message);
            Assert.AreEqual("stop failed", session.Snapshot.CleanupErrors.Single().Message);
            Assert.AreEqual(1, source.Calls.Count(call => call == "Disconnect"));
            Assert.Throws<NotSupportedException>(() => ((IList<UtSourceError>)session.Snapshot.CleanupErrors).Clear());
        }
        finally { await FinishAsync(session, source); }
    }

    [TestMethod]
    public async Task RunDeliveredDuringCancellationIsStillDrained()
    {
        var source = new ManualSessionSource();
        using var cancellation = new CancellationTokenSource();
        ManualSessionSource.CountingOwner? owner = null;
        source.AfterRunCreated = () => { owner = source.QueueFrame(0); cancellation.Cancel(); };
        var session = new ApplicationSession(source);
        try
        {
            await Assert.ThrowsAsync<OperationCanceledException>(() => Watch(session.StartAsync(Configuration, NewRun(), cancellation.Token)));
            Assert.AreEqual(1, owner!.DisposeCount);
            Assert.IsTrue(source.Producer.Task.IsCompletedSuccessfully);
            Assert.IsTrue(source.Released.Task.IsCompletedSuccessfully);
            Assert.AreEqual(1L, session.Snapshot.ReleasedFrames);
            Assert.AreEqual(1, source.Calls.Count(call => call == "Disconnect"));
        }
        finally { await FinishAsync(session, source); }
    }

    [TestMethod]
    public async Task EstablishedRunIgnoresSubsequentStartTokenCancellation()
    {
        var source = new ManualSessionSource();
        using var cancellation = new CancellationTokenSource();
        var session = new ApplicationSession(source);
        try
        {
            await Watch(session.StartAsync(Configuration, NewRun(), cancellation.Token));
            cancellation.Cancel();
            var owner = source.QueueFrame(0);
            await Watch(owner.Returned.Task);
            Assert.AreEqual(SessionPhase.Running, session.Snapshot.Phase);
            Assert.IsFalse(source.StopEntered.Task.IsCompleted);
            await Watch(session.StopAsync());
            Assert.AreEqual(1L, session.Snapshot.ReceivedFrames);
            Assert.AreEqual(1L, session.Snapshot.ReleasedFrames);
        }
        finally { await FinishAsync(session, source); }
    }

    [TestMethod]
    public async Task DrainingAndReleaseBarrierAreIndependentOfProducerCompletion()
    {
        var source = new ManualSessionSource { HoldProducer = true, HoldReleaseBarrier = true };
        var session = new ApplicationSession(source);
        try
        {
            await Watch(session.StartAsync(Configuration, NewRun()));
            var owners = Enumerable.Range(0, 4).Select(i => source.QueueFrame((ulong)i)).ToArray();
            var stop = session.StopAsync();
            await Watch(source.StopEntered.Task);
            await Watch(Task.WhenAll(owners.Select(owner => owner.Returned.Task)));
            Assert.IsFalse(source.Producer.Task.IsCompleted);
            Assert.IsFalse(stop.IsCompleted);
            source.FinishProducer();
            await WaitForPhaseAsync(session, SessionPhase.AwaitingFramesReleased);
            Assert.IsTrue(source.Producer.Task.IsCompletedSuccessfully);
            Assert.IsFalse(stop.IsCompleted);
            Assert.AreEqual(4L, session.Snapshot.ReleasedFrames);
            Assert.AreEqual(3UL, session.Snapshot.LastSequence);
            Assert.Throws<InvalidOperationException>(() => session.StartAsync(Configuration, NewRun()));
            var dispose = session.DisposeAsync().AsTask();
            Assert.IsFalse(dispose.IsCompleted);
            Assert.IsFalse(source.Disconnect.Entered.Task.IsCompleted);
            source.Released.TrySetResult();
            await Watch(Task.WhenAll(stop, dispose));
            Assert.IsTrue(owners.All(owner => owner.DisposeCount == 1));
            Assert.AreEqual(SessionPhase.Disposed, session.Snapshot.Phase);
            Assert.AreEqual(1, source.Calls.Count(call => call == "Stop"));
            Assert.AreEqual(1, source.Calls.Count(call => call == "Disconnect"));
        }
        finally { await FinishAsync(session, source); }
    }

    [TestMethod]
    public async Task CancelledStopWaitDoesNotCancelSharedCleanup()
    {
        var source = new ManualSessionSource { HoldReleaseBarrier = true };
        var session = new ApplicationSession(source);
        try
        {
            await Watch(session.StartAsync(Configuration, NewRun()));
            using var cancellation = new CancellationTokenSource();
            var abandoned = session.StopAsync(cancellation.Token);
            await WaitForPhaseAsync(session, SessionPhase.AwaitingFramesReleased);
            cancellation.Cancel();
            await Assert.ThrowsAsync<OperationCanceledException>(() => Watch(abandoned));
            var repeated = session.StopAsync();
            Assert.IsFalse(repeated.IsCompleted);
            source.Released.TrySetResult();
            await Watch(repeated);
            Assert.AreEqual(1, source.Calls.Count(call => call == "Stop"));
            Assert.AreEqual(SessionPhase.Idle, session.Snapshot.Phase);
        }
        finally { await FinishAsync(session, source); }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task FailedReleaseBarrierCannotAuthorizeDisconnectOrRestart(bool cancelled)
    {
        var source = new ManualSessionSource { HoldReleaseBarrier = true };
        var session = new ApplicationSession(source);
        try
        {
            await Watch(session.StartAsync(Configuration, NewRun()));
            var stop = session.StopAsync();
            await WaitForPhaseAsync(session, SessionPhase.AwaitingFramesReleased);
            if (cancelled) { source.Released.TrySetCanceled(); }
            else { source.Released.TrySetException(new InvalidOperationException("release unconfirmed")); }
            await Assert.ThrowsAsync<Exception>(() => Watch(stop));
            Assert.AreEqual(SessionPhase.Faulted, session.Snapshot.Phase);
            Assert.Throws<InvalidOperationException>(() => session.StartAsync(Configuration, NewRun()));
            await Assert.ThrowsAsync<Exception>(() => Watch(session.DisposeAsync().AsTask()));
            Assert.IsFalse(source.Disconnect.Entered.Task.IsCompleted);
            Assert.AreEqual(SessionPhase.Faulted, session.Snapshot.Phase);
        }
        finally { await FinishAsync(session, source); }
    }

    [TestMethod]
    public async Task ConsumerValidationFailureDrainsRemainingFramesAndDisconnects()
    {
        var source = new ManualSessionSource();
        ManualSessionSource.CountingOwner[] owners = [];
        source.AfterRunCreated = () =>
        {
            var wrong = new ConventionalUtFrameMetadata(source.SourceId, NewRun(), Configuration, DateTimeOffset.UnixEpoch);
            owners = [source.QueueFrame(0, wrong), source.QueueFrame(1), source.QueueFrame(2)];
        };
        var session = new ApplicationSession(source);
        try
        {
            await Watch(session.StartAsync(Configuration, NewRun()));
            await Watch(source.StopEntered.Task);
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => Watch(session.StopAsync()));
            Assert.IsTrue(owners.All(owner => owner.DisposeCount == 1));
            Assert.AreEqual(3L, session.Snapshot.ReceivedFrames);
            Assert.AreEqual(3L, session.Snapshot.ReleasedFrames);
            Assert.AreEqual("session.frame", session.Snapshot.PrimaryError!.Code);
            Assert.AreEqual(1, source.Calls.Count(call => call == "Disconnect"));
        }
        finally { await FinishAsync(session, source); }
    }

    [TestMethod]
    public async Task ProducerFailureTriggersAutomaticCleanupWithoutStopCommand()
    {
        var source = new ManualSessionSource();
        var session = new ApplicationSession(source);
        try
        {
            await Watch(session.StartAsync(Configuration, NewRun()));
            var owner = source.QueueFrame(0);
            source.FinishProducer(new InvalidOperationException("producer failed"));
            await WaitForPhaseAsync(session, SessionPhase.Faulted);
            Assert.AreEqual(1, owner.DisposeCount);
            Assert.AreEqual("producer failed", session.Snapshot.PrimaryError!.Message);
            Assert.AreEqual(1, source.Calls.Count(call => call == "Disconnect"));
        }
        finally { await FinishAsync(session, source); }
    }

    [TestMethod]
    public async Task ReleaseFailureStillDrainsAndKeepsSeparateDiagnostics()
    {
        var source = new ManualSessionSource { StopError = new InvalidOperationException("stop failed") };
        ManualSessionSource.CountingOwner[] owners = [];
        source.AfterRunCreated = () => owners =
            [source.QueueFrame(0, releaseError: new InvalidOperationException("owner failed after return")), source.QueueFrame(1)];
        var session = new ApplicationSession(source);
        try
        {
            await Watch(session.StartAsync(Configuration, NewRun()));
            await WaitForPhaseAsync(session, SessionPhase.Faulted);
            Assert.AreEqual("session.release", session.Snapshot.PrimaryError!.Code);
            Assert.AreEqual("stop failed", session.Snapshot.CleanupErrors.Single().Message);
            Assert.IsTrue(owners.All(owner => owner.DisposeCount == 1));
            Assert.AreEqual(2L, session.Snapshot.ReceivedFrames);
            Assert.AreEqual(1L, session.Snapshot.ReleasedFrames);
            Assert.IsTrue(source.Released.Task.IsCompletedSuccessfully);
        }
        finally { await FinishAsync(session, source); }
    }

    [TestMethod]
    public async Task DisconnectIsAwaitedAndRepeatedDisposeSharesItsTask()
    {
        var source = new ManualSessionSource();
        source.Disconnect.Held = true;
        var session = new ApplicationSession(source);
        try
        {
            await Watch(session.StartAsync(Configuration, NewRun()));
            var dispose = session.DisposeAsync().AsTask();
            await Watch(source.Disconnect.Entered.Task);
            Assert.AreEqual(SessionPhase.Disconnecting, session.Snapshot.Phase);
            Assert.IsFalse(dispose.IsCompleted);
            Assert.AreSame(dispose, session.DisposeAsync().AsTask());
            Assert.Throws<ObjectDisposedException>(() => session.StartAsync(Configuration, NewRun()));
            source.Disconnect.Continue.TrySetResult();
            await Watch(dispose);
            Assert.AreEqual(1, source.Calls.Count(call => call == "Disconnect"));
        }
        finally { await FinishAsync(session, source); }
    }

    [TestMethod]
    public async Task FullChannelStopCancelsWriterWhileSoleReaderContinuesDraining()
    {
        var source = new ManualSessionSource { HoldProducer = true };
        var session = new ApplicationSession(source);
        var returning = ManualSessionSource.Signal();
        var allowReturn = ManualSessionSource.Signal();
        Task? writer = null;
        try
        {
            await Watch(session.StartAsync(Configuration, NewRun()));
            var first = source.QueueFrame(0, beforeReturn: () =>
            {
                returning.TrySetResult();
                Watch(allowReturn.Task).GetAwaiter().GetResult();
            });
            await Watch(returning.Task);
            var queued = Enumerable.Range(1, 4).Select(i => source.QueueFrame((ulong)i)).ToArray();
            var (frame, localOwner) = source.CreateFrame(5);
            var write = source.Channel.Writer.WriteAsync(frame, source.ProductionCancellation.Token);
            Assert.IsFalse(write.IsCompleted); // Reader is holding frame zero; four slots are occupied.
            writer = CompleteWriterAsync();
            var stop = session.StopAsync();
            await Watch(writer);
            Assert.IsTrue(source.Producer.Task.IsCompletedSuccessfully);
            Assert.AreEqual(1, localOwner.DisposeCount);
            Assert.IsFalse(stop.IsCompleted); // Pending reader owns the first frame and must not be abandoned.
            allowReturn.TrySetResult();
            await Watch(stop);
            Assert.AreEqual(1, first.DisposeCount);
            Assert.IsTrue(queued.All(owner => owner.DisposeCount == 1));
            Assert.AreEqual(5L, session.Snapshot.ReleasedFrames);

            async Task CompleteWriterAsync()
            {
                try { await write; Assert.Fail("The blocked write should be cancelled by Stop."); }
                catch (OperationCanceledException) { frame.Dispose(); }
                finally { source.FinishProducer(); }
            }
        }
        finally
        {
            allowReturn.TrySetResult();
            await FinishAsync(session, source);
            if (writer is not null) { await Watch(writer); }
        }
    }

    [TestMethod]
    public async Task ProducerAndDisconnectFailuresPreservePrimaryAndDoNotRetryDisposal()
    {
        var source = new ManualSessionSource();
        source.Disconnect.Error = new InvalidOperationException("disconnect failed");
        var session = new ApplicationSession(source);
        try
        {
            await Watch(session.StartAsync(Configuration, NewRun()));
            var original = new InvalidOperationException("producer failed");
            source.FinishProducer(original);
            await WaitForPhaseAsync(session, SessionPhase.Faulted);
            var dispose = session.DisposeAsync().AsTask();
            var error = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => Watch(dispose));
            Assert.AreSame(original, error);
            Assert.AreSame(dispose, session.DisposeAsync().AsTask());
            Assert.AreEqual("producer failed", session.Snapshot.PrimaryError!.Message);
            Assert.IsTrue(session.Snapshot.CleanupErrors.Any(item => item.Message == "disconnect failed"));
            Assert.AreEqual(1, source.Calls.Count(call => call == "Disconnect"));
            Assert.AreEqual(SessionPhase.Faulted, session.Snapshot.Phase);
        }
        finally { await FinishAsync(session, source); }
    }

    [TestMethod]
    public async Task DisposeAfterCancelledStartupDoesNotDisconnectTwice()
    {
        var source = new ManualSessionSource();
        source.Connect.Held = true;
        var session = new ApplicationSession(source);
        try
        {
            var start = session.StartAsync(Configuration, NewRun());
            await Watch(source.Connect.Entered.Task);
            await Watch(session.StopAsync());
            await Assert.ThrowsAsync<OperationCanceledException>(() => Watch(start));
            await Watch(session.DisposeAsync().AsTask());
            Assert.AreEqual(1, source.Calls.Count(call => call == "Disconnect"));
        }
        finally { await FinishAsync(session, source); }
    }

    [TestMethod]
    public async Task SimulatorIntegrationSupportsStopRestartAndActiveDisposal()
    {
        var clock = new ManualSimulatorTimeProvider();
        await using var source = new SimulatorUtFrameSource(new UtSourceId("session-integration"), timeProvider: clock);
        await using var session = new ApplicationSession(source);
        var first = NewRun();
        await Watch(session.StartAsync(SimulatorUtFrameSource.DefaultConfiguration, first));
        await clock.NextTimerAsync(); // The first frame has been accepted, without advancing real time.
        await Watch(session.StopAsync());
        Assert.AreEqual(1L, session.Snapshot.ReceivedFrames);
        Assert.AreEqual(1L, session.Snapshot.ReleasedFrames);
        Assert.AreEqual(UtConnectionState.Connected, source.State.Connection);
        var second = NewRun();
        await Watch(session.StartAsync(SimulatorUtFrameSource.DefaultConfiguration, second));
        await clock.NextTimerAsync();
        await Watch(session.DisposeAsync().AsTask());
        Assert.AreEqual(second, session.Snapshot.RunId);
        Assert.AreEqual(0UL, session.Snapshot.LastSequence);
        Assert.AreEqual(1L, session.Snapshot.ReleasedFrames);
        Assert.AreEqual(UtConnectionState.Disconnected, source.State.Connection);
    }

    internal static async Task WaitForPhaseAsync(ApplicationSession session, SessionPhase phase)
    {
        var signal = ManualSessionSource.Signal();
        using var subscription = session.Subscribe(new CallbackObserver(snapshot =>
        {
            if (snapshot.Phase == phase) { signal.TrySetResult(); }
        }));
        await Watch(signal.Task);
    }

    private static async Task FinishAsync(ApplicationSession session, ManualSessionSource source)
    {
        source.Connect.Continue.TrySetResult();
        source.Configure.Continue.TrySetResult();
        source.Start.Continue.TrySetResult();
        source.Rollback.Continue.TrySetResult();
        source.Disconnect.Continue.TrySetResult();
        source.FinishProducer();
        source.Released.TrySetResult();
        try { await Watch(session.DisposeAsync().AsTask()); }
        catch (InvalidOperationException) { }
        catch (OperationCanceledException) { }
        await source.DisposeAsync();
    }

    internal sealed class CallbackObserver(Action<SessionSnapshot> callback, Action? completed = null) : IObserver<SessionSnapshot>
    {
        public void OnNext(SessionSnapshot value) => callback(value);
        public void OnCompleted() => completed?.Invoke();
        public void OnError(Exception error) => throw new AssertFailedException("Snapshots carry neutral errors instead of OnError.", error);
    }
}
