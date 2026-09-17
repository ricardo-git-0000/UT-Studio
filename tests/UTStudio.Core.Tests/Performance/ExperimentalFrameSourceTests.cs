using UTStudio.Contracts.Acquisition;
using UTStudio.Domain.Acquisition;
using UTStudio.LoadTests;
namespace UTStudio.Core.Tests.Performance;

[TestClass]
public sealed class ExperimentalFrameSourceTests
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(10);
    [TestMethod]
    public async Task StopCancelsProducerWhileReaderDrainsAndBalances()
    {
        var telemetry = new LoadTelemetry();
        var source = Create(telemetry);
        UtAcquisitionRun? run = null;
        Task? drain = null;
        long consumed = 0;
        var first = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            run = await Start(source).WaitAsync(Limit);
            drain = Task.Run(async () =>
            {
                await foreach (var frame in run.Frames.ReadAllAsync())
                { try { consumed++; first.TrySetResult(); } finally { frame.Dispose(); } }
            });
            await first.Task.WaitAsync(Limit);
            await source.StopAsync().WaitAsync(Limit);
        }
        finally { await Cleanup(source, run, drain); }
        Assert.IsGreaterThan(0, consumed);
        Assert.AreEqual(source.Produced, consumed);
        Assert.AreEqual(telemetry.Counters.Snapshot().Rented, telemetry.Counters.Snapshot().Returned);
    }

    [TestMethod]
    public async Task StopCancelsWriteBlockedByFullChannel()
    {
        var telemetry = new LoadTelemetry();
        var source = Create(telemetry);
        UtAcquisitionRun? run = null;
        try
        {
            run = await Start(source).WaitAsync(Limit);
            await WaitForChannel(source).WaitAsync(Limit);
            await source.StopAsync().WaitAsync(Limit);
            Assert.IsTrue(run.ProducerCompletion.IsCompletedSuccessfully);
            Assert.IsFalse(run.AllFramesReleased.IsCompleted);
        }
        finally { await Cleanup(source, run); }
        var counts = telemetry.Counters.Snapshot();
        Assert.AreEqual(counts.Accepted, counts.Consumed);
        Assert.AreEqual(counts.Consumed, counts.Released);
        Assert.AreEqual(counts.Generated, counts.Accepted + counts.Untransferred);
    }

    [TestMethod]
    public async Task FailureBeforeDrainStillCleansEveryOwner()
    {
        var telemetry = new LoadTelemetry();
        var source = Create(telemetry);
        UtAcquisitionRun? run = null;
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            try
            {
                run = await Start(source).WaitAsync(Limit);
                await WaitForChannel(source).WaitAsync(Limit);
                throw new InvalidOperationException("Synthetic failure before drain");
            }
            finally { await Cleanup(source, run); }
        });
        Assert.IsTrue(run!.AllFramesReleased.IsCompletedSuccessfully);
        Assert.AreEqual(telemetry.Counters.Snapshot().Rented, telemetry.Counters.Snapshot().Returned);
    }

    [TestMethod]
    public async Task CancelledStartDoesNotCreateRunOrBorrowBuffers()
    {
        var source = Create(new());
        try
        {
            await source.ConnectAsync().WaitAsync(Limit);
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            await Assert.ThrowsAsync<OperationCanceledException>(() => source.StartAsync(
                new AcquisitionRunId(Guid.NewGuid()), cancellation.Token).WaitAsync(Limit));
            Assert.AreEqual(0, source.OutstandingBuffers);
            Assert.AreEqual(0, source.Produced);
        }
        finally { await Cleanup(source, null); }
    }

    [TestMethod]
    public async Task CatchUpBurstExposesChannelBackpressureAndBalancesOwnership()
    {
        var clock = new ManualPacingClock(1_000_000);
        var options = new LoadOptions(LoadProfile.Smoke, 2048, 1000, TimeSpan.FromSeconds(1))
        { Source = LoadSourceMode.Experimental, Pacing = PacingMode.CatchUpBounded, MaxCatchUp = 32 };
        var telemetry = new LoadTelemetry(options.Rate, pacing: options.Pacing, maxCatchUp: options.MaxCatchUp, clock: clock);
        var source = new ExperimentalFrameSource(options, telemetry, clock);
        UtAcquisitionRun? run = null;
        try
        {
            run = await Start(source).WaitAsync(Limit);
            await WaitForReason(source, ExperimentalWaitReason.Pacing).WaitAsync(Limit);
            clock.AdvanceTo(10_000);
            await WaitForReason(source, ExperimentalWaitReason.Channel).WaitAsync(Limit);
            var cut = telemetry.Counters.Snapshot();
            Assert.IsGreaterThan(cut.Accepted, cut.Offered);
            Assert.IsGreaterThan(0, telemetry.Demand.Snapshot(clock.Timestamp).Recovered);
            Assert.IsLessThanOrEqualTo(32L, telemetry.Demand.Snapshot(clock.Timestamp).MaximumBurstSize);
        }
        finally { await Cleanup(source, run); }
        var counts = telemetry.Counters.Snapshot();
        Assert.AreEqual(counts.Generated, counts.Accepted + counts.Untransferred);
        Assert.AreEqual(counts.Accepted, counts.Consumed);
        Assert.AreEqual(counts.Consumed, counts.Released);
        Assert.AreEqual(counts.Rented, counts.Returned);
        Assert.AreEqual(0, source.OutstandingBuffers);
    }

    [TestMethod]
    public async Task CancellationInterruptsManualPacingWait()
    {
        var clock = new ManualPacingClock(1_000_000);
        var options = new LoadOptions(LoadProfile.Smoke, 2048, 1000, TimeSpan.FromSeconds(1));
        var telemetry = new LoadTelemetry(options.Rate, clock: clock);
        var source = new ExperimentalFrameSource(options, telemetry, clock);
        UtAcquisitionRun? run = null;
        try
        {
            run = await Start(source).WaitAsync(Limit);
            await WaitForReason(source, ExperimentalWaitReason.Pacing).WaitAsync(Limit);
            await source.StopAsync().WaitAsync(Limit);
            Assert.IsTrue(run.ProducerCompletion.IsCompletedSuccessfully);
        }
        finally { await Cleanup(source, run); }
    }
    private static ExperimentalFrameSource Create(LoadTelemetry telemetry) =>
        new(new(LoadProfile.Smoke, 2048, null, TimeSpan.FromSeconds(1)), telemetry);
    private static async Task<UtAcquisitionRun> Start(ExperimentalFrameSource source)
    {
        await source.ConnectAsync();
        await source.ConfigureAsync(new(new PhysicalChannelId(0), 2048, 50_000_000));
        return await source.StartAsync(new AcquisitionRunId(Guid.NewGuid()));
    }
    private static async Task WaitForChannel(ExperimentalFrameSource source)
    {
        using var deadline = new CancellationTokenSource(Limit);
        while (source.WaitReason != ExperimentalWaitReason.Channel)
        { deadline.Token.ThrowIfCancellationRequested(); await Task.Yield(); }
    }
    private static async Task WaitForReason(ExperimentalFrameSource source, ExperimentalWaitReason reason)
    {
        while (source.WaitReason != reason) { await Task.Yield(); }
    }
    private static async Task Cleanup(ExperimentalFrameSource source, UtAcquisitionRun? run, Task? reader = null)
    {
        // Start cancellation and the sole drain together. Every phase is attempted and observed.
        Task stop = source.StopAsync();
        Task drain = reader ?? (run is null ? Task.CompletedTask : Drain(run));
        List<Exception> failures = [];
        foreach (Task phase in new[] { stop, drain, run?.ProducerCompletion ?? Task.CompletedTask,
            run?.AllFramesReleased ?? Task.CompletedTask })
        {
            try { await phase.WaitAsync(Limit); }
            catch (Exception error)
            {
                failures.Add(error);
                _ = phase.ContinueWith(t => { _ = t.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
            }
        }
        Task disposal = source.DisposeAsync().AsTask();
        try { await disposal.WaitAsync(Limit); }
        catch (Exception error)
        {
            failures.Add(error);
            _ = disposal.ContinueWith(t => { _ = t.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
        }
        if (failures.Count > 0) { throw new AggregateException(failures); }
    }
    private static async Task Drain(UtAcquisitionRun run)
    { await foreach (var frame in run.Frames.ReadAllAsync()) { frame.Dispose(); } }

    private sealed class ManualPacingClock(long frequency) : IPacingClock
    {
        private readonly object _gate = new();
        private long _timestamp;
        private TaskCompletionSource? _delay;
        private bool _permit;
        public long Frequency { get; } = frequency;
        public long Timestamp { get { lock (_gate) { return _timestamp; } } }
        public ValueTask DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                if (_permit) { _permit = false; return ValueTask.CompletedTask; }
                _delay = new(TaskCreationOptions.RunContinuationsAsynchronously);
                return new(_delay.Task.WaitAsync(cancellationToken));
            }
        }
        internal void AdvanceTo(long timestamp)
        {
            TaskCompletionSource? delay;
            lock (_gate)
            {
                _timestamp = timestamp; delay = _delay; _delay = null;
                if (delay is null) { _permit = true; }
            }
            delay?.TrySetResult();
        }
    }
}
