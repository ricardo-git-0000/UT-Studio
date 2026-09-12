using UTStudio.Acquisition.Simulator;
using UTStudio.Domain.Acquisition;

namespace UTStudio.Core.Tests.Simulator;

[TestClass]
public sealed class SimulatorSourceTests
{
    [TestMethod]
    public async Task FramesAreReproducibleAcrossSourcesAndSeedsAffectNoise()
    {
        static async Task<short[]> Capture(ulong seed)
        {
            await using var simulation = new SimulatorHarness(new SimulatorOptions(seed: seed));
            await simulation.StartAsync();
            var frame = await simulation.ReadAsync();
            return frame.Samples.ToArray();
        }

        var first = await Capture(1);
        CollectionAssert.AreEqual(first, await Capture(1));
        CollectionAssert.AreNotEqual(first, await Capture(2));
        Assert.IsLessThan(-10000, first.Min());
        Assert.IsGreaterThan(10000, first.Max());
    }

    [TestMethod]
    public async Task MetadataSequenceAndMonotonicTimeAreCorrectWithoutCatchup()
    {
        await using var simulation = new SimulatorHarness();
        await simulation.StartAsync();
        var first = await simulation.ReadAsync();
        Assert.AreEqual(0UL, first.Sequence);
        Assert.AreEqual(TimeSpan.Zero, first.ElapsedSinceRunStart);
        Assert.AreEqual(2048, first.Samples.Length);
        Assert.AreEqual(50_000_000d, first.Metadata.Configuration.SampleRateHz);
        Assert.AreEqual(UtSignalMode.Rf, first.Metadata.Configuration.SignalMode);
        Assert.AreEqual(DateTimeOffset.UnixEpoch, first.Metadata.RunStartedAtUtc);
        var timer = await simulation.Clock.NextTimerAsync();
        simulation.Clock.SetUtc(DateTimeOffset.UnixEpoch.AddDays(-10));
        timer.Fire(TimeSpan.FromSeconds(1));

        var second = await simulation.ReadAsync();
        Assert.AreEqual(1UL, second.Sequence);
        Assert.AreEqual(TimeSpan.FromMilliseconds(1010), second.ElapsedSinceRunStart);
        Assert.AreSame(first.Metadata, second.Metadata);
        _ = await simulation.Clock.NextTimerAsync();
        Assert.IsFalse(simulation.Run.Frames.TryPeek(out _)); // No catch-up burst despite advancing a second.
    }

    [TestMethod]
    public async Task FullChannelBackpressureStopsWithoutWaitingForDrain()
    {
        await using var simulation = new SimulatorHarness();
        await simulation.StartAsync();
        for (int i = 0; i < 4; i++)
        {
            (await simulation.Clock.NextTimerAsync()).Fire();
        }

        await SimulatorHarness.UntilAsync(() => simulation.Source.WaitReason == SimulatorWaitReason.Channel);
        Assert.AreEqual(4, simulation.Run.Frames.Count);
        Assert.AreEqual(5, simulation.Source.OutstandingBuffers);
        await simulation.Source.StopAsync().WaitAsync(TimeSpan.FromSeconds(10));
        await simulation.Run.ProducerCompletion;
        Assert.AreEqual(4, simulation.Source.OutstandingBuffers);
        Assert.IsFalse(simulation.Run.AllFramesReleased.IsCompleted);
        Assert.AreEqual(UtAcquisitionState.Stopping, simulation.Source.State.Acquisition);
        for (ulong sequence = 0; sequence < 4; sequence++)
        {
            var frame = await simulation.ReadAsync();
            Assert.AreEqual(sequence, frame.Sequence);
            frame.Dispose();
        }

        await simulation.Run.AllFramesReleased.WaitAsync(TimeSpan.FromSeconds(10));
        await simulation.Run.Frames.Completion;
        Assert.IsFalse(simulation.Run.Frames.TryRead(out _));
        Assert.AreEqual(UtAcquisitionState.Idle, simulation.Source.State.Acquisition);
    }

    [TestMethod]
    public async Task BufferExhaustionIsCancelledWithoutReclaimingBorrowedFrames()
    {
        await using var simulation = new SimulatorHarness();
        await simulation.StartAsync();
        for (int i = 0; i < 8; i++)
        {
            _ = await simulation.ReadAsync(); // Deliberately hold every lease.
            (await simulation.Clock.NextTimerAsync()).Fire();
        }

        await SimulatorHarness.UntilAsync(() => simulation.Source.WaitReason == SimulatorWaitReason.Buffer);
        Assert.AreEqual(8, simulation.Source.OutstandingBuffers);
        await simulation.Source.StopAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.IsFalse(simulation.Run.AllFramesReleased.IsCompleted);
        Assert.IsTrue(simulation.Run.ProducerCompletion.IsCompletedSuccessfully);
    }

    [TestMethod]
    public async Task DisposeWaitsForBorrowedFrameAndIsIdempotent()
    {
        await using var simulation = new SimulatorHarness();
        await simulation.StartAsync();
        var frame = await simulation.ReadAsync();
        var timer = await simulation.Clock.NextTimerAsync();
        var firstDispose = simulation.Source.DisposeAsync().AsTask();
        var secondDispose = simulation.Source.DisposeAsync().AsTask();
        Assert.AreSame(firstDispose, secondDispose);
        await simulation.Run.ProducerCompletion.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.IsTrue(timer.IsDisposed);
        Assert.IsFalse(firstDispose.IsCompleted);
        Assert.IsFalse(simulation.Run.AllFramesReleased.IsCompleted);
        Assert.AreEqual(2048, frame.Samples.Length);
        frame.Dispose();
        frame.Dispose();
        await firstDispose.WaitAsync(TimeSpan.FromSeconds(10));
        await secondDispose;
        Assert.IsTrue(simulation.Run.AllFramesReleased.IsCompletedSuccessfully);
        Assert.AreEqual(UtConnectionState.Disconnected, simulation.Source.State.Connection);
        await simulation.Source.DisposeAsync();
        Assert.ThrowsExactly<ObjectDisposedException>(() => simulation.Source.ConnectAsync());
    }

    [TestMethod]
    public async Task StartRequestCancellationAfterCommitDoesNotStopRun()
    {
        await using var simulation = new SimulatorHarness();
        using var request = new CancellationTokenSource();
        await simulation.StartAsync(request.Token);
        var first = await simulation.ReadAsync();
        first.Dispose();
        request.Cancel();
        (await simulation.Clock.NextTimerAsync()).Fire();
        var second = await simulation.ReadAsync();
        Assert.AreEqual(1UL, second.Sequence);
        Assert.IsFalse(simulation.Run.ProducerCompletion.IsCompleted);
    }

    [TestMethod]
    public async Task CancelledStopWaitStillRequestsProducerCancellation()
    {
        await using var simulation = new SimulatorHarness();
        await simulation.StartAsync();
        _ = await simulation.ReadAsync();
        _ = await simulation.Clock.NextTimerAsync();
        try { await simulation.Source.StopAsync(new CancellationToken(true)); }
        catch (OperationCanceledException) { }
        await simulation.Run.ProducerCompletion.WaitAsync(TimeSpan.FromSeconds(10));
        await simulation.Source.StopAsync();
        Assert.IsFalse(simulation.Run.AllFramesReleased.IsCompleted);
    }

    [TestMethod]
    public async Task CancelledStartRollsBackAllocatedPoolAndCanRetry()
    {
        await using var simulation = new SimulatorHarness();
        await simulation.Source.ConnectAsync();
        using var request = new CancellationTokenSource();
        simulation.Clock.BeforeUtcRead = request.Cancel; // Invoked after pool allocation during preparation.
        Assert.ThrowsExactly<OperationCanceledException>(() =>
            simulation.Source.StartAsync(new AcquisitionRunId(Guid.NewGuid()), request.Token));
        Assert.AreEqual(UtAcquisitionState.Idle, simulation.Source.State.Acquisition);
        Assert.AreEqual(0, simulation.Source.OutstandingBuffers);
        simulation.Clock.BeforeUtcRead = null;
        await simulation.StartAsync();
        Assert.AreEqual(0UL, (await simulation.ReadAsync()).Sequence);
    }

    [TestMethod]
    [DataRow("Synthetic startup failure.")]
    [DataRow("")]
    public async Task FailedStartRollsBackAndRequiresRecovery(string message)
    {
        await using var simulation = new SimulatorHarness();
        await simulation.Source.ConnectAsync();
        var expected = new InvalidOperationException(message);
        simulation.Clock.BeforeUtcRead = () => throw expected;
        var observed = Assert.ThrowsExactly<InvalidOperationException>(() =>
            simulation.Source.StartAsync(new AcquisitionRunId(Guid.NewGuid())));
        Assert.AreSame(expected, observed);
        Assert.AreEqual(UtAcquisitionState.Faulted, simulation.Source.State.Acquisition);
        Assert.AreEqual("simulator.start", simulation.Source.State.PrimaryError!.Code);
        Assert.AreEqual(0, simulation.Source.OutstandingBuffers);
        Assert.ThrowsExactly<InvalidOperationException>(() => simulation.Source.StartAsync(new AcquisitionRunId(Guid.NewGuid())));
        await simulation.Source.DisconnectAsync();
        simulation.Clock.BeforeUtcRead = null;
        await simulation.StartAsync();
        Assert.AreEqual(0UL, (await simulation.ReadAsync()).Sequence);
    }

    [TestMethod]
    [DataRow("Synthetic timer failure.")]
    [DataRow("")]
    [DataRow(" ")]
    public async Task ProducerFailureCompletesChannelButDoesNotReclaimQueuedFrame(string message)
    {
        await using var simulation = new SimulatorHarness();
        simulation.Clock.FailTimerCreation = true;
        var expected = new InvalidOperationException(message);
        simulation.Clock.TimerFailure = expected;
        await simulation.StartAsync();
        var observed = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            simulation.Run.ProducerCompletion.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.AreSame(expected, observed);
        Assert.AreEqual(UtAcquisitionState.Faulted, simulation.Source.State.Acquisition);
        Assert.AreEqual("simulator.producer", simulation.Source.State.PrimaryError!.Code);
        Assert.IsFalse(simulation.Run.AllFramesReleased.IsCompleted);
        (await simulation.ReadAsync()).Dispose();
        await simulation.Run.AllFramesReleased;
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => simulation.Run.Frames.Completion);
        Assert.IsFalse(simulation.Run.Frames.TryRead(out _));
    }

    [TestMethod]
    public async Task FractionalPeriodsAndEarlyWakeupsCannotExceedRequestedRate()
    {
        await using var simulation = new SimulatorHarness(new SimulatorOptions(maxAScansPerSecond: 60));
        await simulation.StartAsync();
        (await simulation.ReadAsync()).Dispose();
        (await simulation.Clock.NextTimerAsync()).FireEarly();
        var rearmed = await simulation.Clock.NextTimerAsync();
        Assert.IsFalse(simulation.Run.Frames.TryPeek(out _));
        rearmed.Fire();
        var second = await simulation.ReadAsync();
        Assert.IsGreaterThanOrEqualTo(TimeSpan.FromSeconds(1d / 60), second.ElapsedSinceRunStart);
        Assert.AreEqual(1UL, second.Sequence);
    }

    [TestMethod]
    public async Task ConcurrentStopAndDisposeDoNotWaitUnderLifecycleLock()
    {
        await using var simulation = new SimulatorHarness();
        await simulation.StartAsync();
        var frame = await simulation.ReadAsync();
        _ = await simulation.Clock.NextTimerAsync();
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stop = Task.Run(async () => { await start.Task; await simulation.Source.StopAsync(); });
        var dispose = Task.Run(async () => { await start.Task; await simulation.Source.DisposeAsync(); });
        start.SetResult();
        await stop.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.IsFalse(dispose.IsCompleted);
        frame.Dispose();
        await dispose.WaitAsync(TimeSpan.FromSeconds(10));
        await simulation.Source.StopAsync();
        await simulation.Source.DisposeAsync();
        Assert.IsTrue(simulation.Run.AllFramesReleased.IsCompletedSuccessfully);
    }

    [TestMethod]
    public async Task CancellationCallbackFailureIsObservedOutsideStateLock()
    {
        await using var simulation = new SimulatorHarness();
        await simulation.StartAsync();
        var frame = await simulation.ReadAsync();
        _ = await simulation.Clock.NextTimerAsync();
        bool readState = false;
        simulation.Clock.OnTimerDispose = () =>
        {
            _ = Task.Run(() => simulation.Source.State).WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
            readState = true;
            throw new InvalidOperationException("Synthetic timer disposal failure.");
        };
        await Assert.ThrowsExactlyAsync<AggregateException>(() =>
            simulation.Source.StopAsync().WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.IsTrue(readState);
        Assert.IsTrue(simulation.Run.ProducerCompletion.IsCompletedSuccessfully);
        Assert.AreEqual(UtAcquisitionState.Faulted, simulation.Source.State.Acquisition);
        Assert.AreEqual("simulator.cancellation", simulation.Source.State.PrimaryError!.Code);
        frame.Dispose();
        await simulation.Run.AllFramesReleased;
        await Assert.ThrowsExactlyAsync<AggregateException>(() =>
            simulation.Source.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.AreEqual(UtConnectionState.Disconnected, simulation.Source.State.Connection);
    }

    [TestMethod]
    public async Task CleanupBlocksConfigurationAndRestartUntilFramesReturn()
    {
        await using var simulation = new SimulatorHarness();
        await simulation.StartAsync();
        var frame = await simulation.ReadAsync();
        _ = await simulation.Clock.NextTimerAsync();
        await simulation.Source.StopAsync();
        Assert.ThrowsExactly<InvalidOperationException>(() => simulation.Source.ConfigureAsync(SimulatorUtFrameSource.DefaultConfiguration));
        Assert.ThrowsExactly<InvalidOperationException>(() => simulation.Source.StartAsync(new AcquisitionRunId(Guid.NewGuid())));
        Assert.ThrowsExactly<InvalidOperationException>(() => simulation.Source.DisconnectAsync());
        var previous = simulation.Run;
        var samples = frame.Samples.ToArray();
        frame.Dispose();
        await previous.AllFramesReleased;
        await simulation.StartAsync();
        Assert.AreNotSame(previous.Frames, simulation.Run.Frames);
        Assert.AreNotEqual(previous.Metadata.RunId, simulation.Run.Metadata.RunId);
        var restarted = await simulation.ReadAsync();
        Assert.AreEqual(0UL, restarted.Sequence);
        CollectionAssert.AreEqual(samples, restarted.Samples.ToArray());
    }

    [TestMethod]
    public async Task ConfigurationIsValidatedJointlyAndRejectionPreservesPreviousSettings()
    {
        await using var simulation = new SimulatorHarness();
        await simulation.Source.ConnectAsync();
        var valid = new ConventionalAcquisitionConfiguration(default, 1, 100_000_000, -1e-6);
        await simulation.Source.ConfigureAsync(valid);
        foreach (var invalid in new[]
        {
            new ConventionalAcquisitionConfiguration(new PhysicalChannelId(1), 2048, 50_000_000),
            new ConventionalAcquisitionConfiguration(default, 2048, 10_000_000),
            new ConventionalAcquisitionConfiguration(default, 2048, double.Epsilon),
            new ConventionalAcquisitionConfiguration(default, 2048, 50_000_000, double.MaxValue)
        })
        {
            Assert.ThrowsExactly<ArgumentException>(() => simulation.Source.ConfigureAsync(invalid));
        }

        await simulation.StartAsync();
        var frame = await simulation.ReadAsync();
        Assert.AreSame(valid, frame.Metadata.Configuration);
        Assert.AreEqual(1, frame.Samples.Length);
    }

    [TestMethod]
    public async Task LifecycleRejectsInvalidIdsAndActiveReconfiguration()
    {
        await using var simulation = new SimulatorHarness();
        Assert.ThrowsExactly<InvalidOperationException>(() => simulation.Source.StartAsync(new AcquisitionRunId(Guid.NewGuid())));
        await simulation.Source.ConnectAsync();
        Assert.ThrowsExactly<ArgumentException>(() => simulation.Source.StartAsync(default));
        Assert.ThrowsExactly<OperationCanceledException>(() =>
            simulation.Source.StartAsync(new AcquisitionRunId(Guid.NewGuid()), new CancellationToken(true)));
        await simulation.StartAsync();
        Assert.ThrowsExactly<InvalidOperationException>(() => simulation.Source.StartAsync(new AcquisitionRunId(Guid.NewGuid())));
        Assert.ThrowsExactly<InvalidOperationException>(() => simulation.Source.ConfigureAsync(SimulatorUtFrameSource.DefaultConfiguration));
    }
}
