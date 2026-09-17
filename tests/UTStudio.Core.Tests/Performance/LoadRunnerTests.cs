using UTStudio.LoadTests;
using System.Threading.Channels;
using UTStudio.Contracts.Acquisition;
using UTStudio.Domain.Acquisition;

namespace UTStudio.Core.Tests.Performance;

[TestClass]
public sealed class LoadRunnerTests
{
    [TestMethod]
    public async Task CancellationStopsAndBalancesRun()
    {
        using var cancellation = new CancellationTokenSource();
        var result = await new LoadRunner().RunAsync(
            new LoadOptions(LoadProfile.Smoke, 2048, 1000, TimeSpan.FromHours(1)),
            cancellation.Token, TimeSpan.FromSeconds(10), cancellation.Cancel).WaitAsync(TimeSpan.FromSeconds(20));
        Assert.IsTrue(result.Cancelled);
        Assert.AreEqual(result.Produced, result.Consumed);
        Assert.AreEqual(result.Consumed, result.Released);
        Assert.AreEqual(0, result.OutstandingBuffers);
        Assert.IsTrue(result.FramesReleasedBarrier);
        Assert.AreEqual(CampaignOutcome.Cancelled, result.Outcome);
        Assert.IsTrue(result.CleanupSucceeded);
        Assert.AreEqual(3, FunctionalCriteria.ExitCode(result));
    }

    [TestMethod]
    public void ObservedErrorAndMissingBarrierReturnFailureCode()
    {
        var result = new LoadResult(1, 1, 1, 0, 1, 1, 1, 0, 0, 1, 1,
            default, default, 0, 0, 0, 0, 0, 0, 0, 0, false, TimeSpan.Zero,
            [new UTStudio.Domain.Acquisition.UtSourceError("test", "failure")], false);
        Assert.AreEqual(2, FunctionalCriteria.ExitCode(result));
        Assert.IsGreaterThanOrEqualTo(2, FunctionalCriteria.Evaluate(result).Count);
    }

    [TestMethod]
    public async Task NoProgressCampaignIsNotSuccessfulBaseline()
    {
        var source = new QuietSource();
        var options = new LoadOptions(LoadProfile.Baseline, 2048, 1000, TimeSpan.FromHours(1))
        { ProgressTimeout = TimeSpan.FromMilliseconds(20) };
        var result = await new LoadRunner().RunAsync(options, CancellationToken.None,
            sourceFactory: _ => source).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.AreEqual(CampaignOutcome.NoProgress, result.Outcome);
        Assert.IsTrue(result.CleanupSucceeded);
        Assert.AreEqual(4, FunctionalCriteria.ExitCode(result));
    }

    [TestMethod]
    public async Task PeriodicLiveSampleAdvancesWhileFramesCirculate()
    {
        using var cancellation = new CancellationTokenSource();
        var result = await new LoadRunner().RunAsync(
            new(LoadProfile.Smoke, 65535, null, TimeSpan.FromHours(1)), cancellation.Token,
            telemetryInterval: TimeSpan.FromMilliseconds(1),
            sampleObserved: sample => { if (sample.Consumed > 0) { cancellation.Cancel(); } })
            .WaitAsync(TimeSpan.FromSeconds(10));
        Assert.IsTrue(result.ResourceSamples.Any(sample => sample.Consumed > 0));
        Assert.IsGreaterThan(0d, result.ActiveWindow.ConsumedPerSecond);
        Assert.IsGreaterThan(0d, result.ActiveWindow.AcceptedPerSecond);
        Assert.AreEqual(CampaignOutcome.Cancelled, result.Outcome);
        Assert.IsTrue(result.CleanupSucceeded);
    }

    [TestMethod]
    public async Task CleanupTimeoutDoesNotClaimReleasedBarrier()
    {
        var source = new QuietSource(holdBarrier: true);
        Task<CleanupReport>? cleanup = null;
        try
        {
            var options = new LoadOptions(LoadProfile.Smoke, 2048, 1000, TimeSpan.FromHours(1))
            { ProgressTimeout = TimeSpan.FromMilliseconds(20), CleanupTimeout = TimeSpan.FromMilliseconds(20) };
            var result = await new LoadRunner().RunAsync(options, CancellationToken.None,
                sourceFactory: _ => source).WaitAsync(TimeSpan.FromSeconds(10));
            Assert.AreEqual(CampaignOutcome.CleanupTimeout, result.Outcome);
            cleanup = result.PendingCleanup;
            Assert.AreEqual(CampaignOutcome.NoProgress, result.PrimaryOutcome);
            Assert.IsFalse(result.CleanupSucceeded);
            Assert.IsFalse(result.FramesReleasedBarrier);
            Assert.AreEqual(5, FunctionalCriteria.ExitCode(result));
        }
        finally
        {
            source.ReleaseBarrier();
            await source.Disposed.Task.WaitAsync(TimeSpan.FromSeconds(10));
            if (cleanup is not null) { await cleanup.WaitAsync(TimeSpan.FromSeconds(10)); }
        }
    }

    [TestMethod]
    public async Task CampaignFailureCanHaveSuccessfulCleanup()
    {
        var source = new QuietSource();
        var result = await new LoadRunner().RunAsync(new(LoadProfile.Smoke, 2048, 1000, TimeSpan.FromSeconds(1)),
            CancellationToken.None, runStarted: () => throw new InvalidOperationException("synthetic campaign error"),
            sourceFactory: _ => source).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.AreEqual(CampaignOutcome.FunctionalFailure, result.Outcome);
        Assert.IsTrue(result.CleanupSucceeded);
        Assert.AreEqual(2, FunctionalCriteria.ExitCode(result));
    }

    private sealed class QuietSource(bool holdBarrier = false) : IUtFrameSource
    {
        private readonly Channel<ConventionalUtFrame> _channel = Channel.CreateBounded<ConventionalUtFrame>(4);
        private readonly TaskCompletionSource _producer = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _barrier = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private ConventionalAcquisitionConfiguration _configuration = new(new(0), 2048, 50_000_000);
        internal TaskCompletionSource Disposed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public UtSourceId SourceId => new("quiet-test");
        public UtSourceCapabilities Capabilities => new([new(0)], 65535, 100_000_000, true);
        public UtSourceState State { get; private set; } = new(0, UtConnectionState.Disconnected, UtAcquisitionState.Idle);
        public Task ConnectAsync(CancellationToken cancellationToken = default)
        { State = new(1, UtConnectionState.Connected, UtAcquisitionState.Idle); return Task.CompletedTask; }
        public Task ConfigureAsync(ConventionalAcquisitionConfiguration configuration, CancellationToken cancellationToken = default)
        { _configuration = configuration; return Task.CompletedTask; }
        public Task<UtAcquisitionRun> StartAsync(AcquisitionRunId runId, CancellationToken cancellationToken = default)
        {
            State = new(2, UtConnectionState.Connected, UtAcquisitionState.Running);
            return Task.FromResult(new UtAcquisitionRun(new(SourceId, runId, _configuration, DateTimeOffset.UnixEpoch),
                _channel.Reader, _producer.Task, _barrier.Task));
        }
        public Task StopAsync(CancellationToken cancellationToken = default)
        {
            _channel.Writer.TryComplete(); _producer.TrySetResult();
            if (!holdBarrier) { ReleaseBarrier(); }
            return Task.CompletedTask;
        }
        internal void ReleaseBarrier() => _barrier.TrySetResult();
        public Task DisconnectAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public ValueTask DisposeAsync() { Disposed.TrySetResult(); return ValueTask.CompletedTask; }
    }
}
