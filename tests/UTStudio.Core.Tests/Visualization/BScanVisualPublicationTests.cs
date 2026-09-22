using System.Threading.Channels;
using UTStudio.Core.Tests.Simulator;
using UTStudio.Domain.Acquisition;
using UTStudio.Visualization.Core;

namespace UTStudio.Core.Tests.Visualization;

[TestClass]
public sealed class BScanVisualPublicationTests
{
    [TestMethod]
    public async Task FirstPublicationIsImmediateWithManualClock()
    {
        await using PublicationHarness harness = await PublicationHarness.CreateAsync();
        var observer = new ChannelObserver();
        using IDisposable subscription = harness.Delivery.Subscribe(observer);
        harness.Delivery.RequestRender(harness.Request(2));

        BScanVisualUpdate update = await observer.NextAsync();

        Assert.AreEqual(1UL, update.Snapshot.SnapshotVersion);
        Assert.AreEqual(1UL, harness.Delivery.Diagnostics.VisualUpdatesPublished);
    }

    [TestMethod]
    public async Task RequestsBeforeBoundaryAreCoalescedAndExactBoundaryPublishesLatest()
    {
        await using PublicationHarness harness = await PublicationHarness.CreateAsync();
        var observer = new ChannelObserver();
        using IDisposable subscription = harness.Delivery.Subscribe(observer);
        harness.Delivery.RequestRender(harness.Request(2));
        _ = await observer.NextAsync();

        harness.Delivery.RequestRender(harness.Request(3));
        harness.Delivery.RequestRender(harness.Request(4));
        ManualSimulatorTimeProvider.ManualTimer early = await harness.Clock.NextTimerAsync();
        early.FireEarly();
        ManualSimulatorTimeProvider.ManualTimer exact = await harness.Clock.NextTimerAsync();
        exact.Fire();
        BScanVisualUpdate latest = await observer.NextAsync();

        Assert.AreEqual(4, latest.Snapshot.RasterWidth);
        Assert.AreEqual(2UL, harness.Delivery.Diagnostics.VisualUpdatesPublished);
        Assert.IsGreaterThanOrEqualTo(1UL, harness.Delivery.Diagnostics.VisualUpdatesCoalesced);
    }

    [TestMethod]
    public async Task BurstAcrossSeveralPeriodsNeverCatchesUpAndKeepsLatestRequest()
    {
        await using PublicationHarness harness = await PublicationHarness.CreateAsync();
        var observer = new ChannelObserver();
        using IDisposable subscription = harness.Delivery.Subscribe(observer);
        harness.Delivery.RequestRender(harness.Request(2));
        _ = await observer.NextAsync();
        for (int width = 3; width <= 8; width++) { harness.Delivery.RequestRender(harness.Request(width)); }
        ManualSimulatorTimeProvider.ManualTimer timer = await harness.Clock.NextTimerAsync();
        timer.Fire(BScanVisualDelivery.MinimumPublicationInterval * 4);

        BScanVisualUpdate update = await observer.NextAsync();

        Assert.AreEqual(8, update.Snapshot.RasterWidth);
        Assert.AreEqual(2UL, harness.Delivery.Diagnostics.VisualUpdatesPublished);
    }

    [TestMethod]
    public async Task IndexedColumnWithoutExternalRequestReusesLatestRequest()
    {
        await using PublicationHarness harness = await PublicationHarness.CreateAsync();
        var observer = new ChannelObserver();
        using IDisposable subscription = harness.Delivery.Subscribe(observer);
        harness.Delivery.RequestRender(harness.Request(2));
        _ = await observer.NextAsync();

        await harness.AddAsync(1, 1);
        ManualSimulatorTimeProvider.ManualTimer timer = await harness.Clock.NextTimerAsync();
        timer.Fire();
        BScanVisualUpdate update = await observer.NextAsync();

        Assert.AreEqual(2UL, update.Snapshot.StoreRevision);
        Assert.IsTrue(update.Snapshot.RequiresFullRefresh);
        Assert.AreEqual(BScanFullRefreshReason.Initial, update.Snapshot.FullRefreshReason);
    }

    [TestMethod]
    public async Task PendingTimerIsCancelledAndDisposedDuringClose()
    {
        await using PublicationHarness harness = await PublicationHarness.CreateAsync();
        var observer = new ChannelObserver();
        using IDisposable subscription = harness.Delivery.Subscribe(observer);
        harness.Delivery.RequestRender(harness.Request(2));
        _ = await observer.NextAsync();
        harness.Delivery.RequestRender(harness.Request(3));
        ManualSimulatorTimeProvider.ManualTimer timer = await harness.Clock.NextTimerAsync();

        await harness.Delivery.DisposeAsync();

        Assert.IsTrue(timer.IsDisposed);
        Assert.AreEqual(BScanVisualCloseState.Closed, harness.Delivery.Diagnostics.CloseState);
        Assert.AreEqual(harness.Delivery.Diagnostics.ObserverCallbacksStarted,
            harness.Delivery.Diagnostics.ObserverCallbacksCompleted);
    }

    [TestMethod]
    public async Task UnsubscribeBeforeCallbackPreventsCallbackEntry()
    {
        await using PublicationHarness harness = await PublicationHarness.CreateAsync();
        int calls = 0;
        IDisposable removed = harness.Delivery.Subscribe(new DelegateObserver(_ => Interlocked.Increment(ref calls)));
        removed.Dispose();
        var healthy = new ChannelObserver();
        using IDisposable active = harness.Delivery.Subscribe(healthy);
        harness.Delivery.RequestRender(harness.Request(2));
        _ = await healthy.NextAsync();
        Assert.AreEqual(0, calls);
    }

    [TestMethod]
    public async Task UnsubscribeDuringCallbackWaitsForStartedCallbackAndPreventsAnother()
    {
        await using PublicationHarness harness = await PublicationHarness.CreateAsync();
        var entered = new ManualResetEventSlim();
        var release = new ManualResetEventSlim();
        int calls = 0;
        IDisposable subscription = harness.Delivery.Subscribe(new DelegateObserver(_ =>
        {
            Interlocked.Increment(ref calls);
            entered.Set();
            release.Wait();
        }));
        harness.Delivery.RequestRender(harness.Request(2));
        Assert.IsTrue(entered.Wait(TimeSpan.FromSeconds(10)));
        Task unsubscribe = Task.Run(subscription.Dispose);
        Assert.IsFalse(unsubscribe.IsCompleted);
        release.Set();
        await unsubscribe.WaitAsync(TimeSpan.FromSeconds(10));
        harness.Delivery.RequestRender(harness.Request(3));
        ManualSimulatorTimeProvider.ManualTimer timer = await harness.Clock.NextTimerAsync();
        timer.Fire();
        var peer = new ChannelObserver();
        using IDisposable peerSubscription = harness.Delivery.Subscribe(peer);
        _ = await peer.NextAsync();
        Assert.AreEqual(1, calls);
    }

    [TestMethod]
    public async Task DisposeWaitsForStartedCallbackAndNoCallbackRemainsAfterCompletion()
    {
        await using PublicationHarness harness = await PublicationHarness.CreateAsync();
        var entered = new ManualResetEventSlim();
        var release = new ManualResetEventSlim();
        using IDisposable subscription = harness.Delivery.Subscribe(new DelegateObserver(_ =>
        { entered.Set(); release.Wait(); }));
        harness.Delivery.RequestRender(harness.Request(2));
        Assert.IsTrue(entered.Wait(TimeSpan.FromSeconds(10)));
        Task close = harness.Delivery.DisposeAsync().AsTask();
        Assert.IsFalse(close.IsCompleted);
        release.Set();
        await close.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.AreEqual(harness.Delivery.Diagnostics.ObserverCallbacksStarted,
            harness.Delivery.Diagnostics.ObserverCallbacksCompleted);
    }

    [TestMethod]
    public async Task SlowAndFailingObserversDoNotBlockHealthyObserver()
    {
        await using PublicationHarness harness = await PublicationHarness.CreateAsync();
        var slowEntered = new ManualResetEventSlim();
        var slowRelease = new ManualResetEventSlim();
        using IDisposable slow = harness.Delivery.Subscribe(new DelegateObserver(_ =>
        { slowEntered.Set(); slowRelease.Wait(); }));
        using IDisposable failing = harness.Delivery.Subscribe(new DelegateObserver(_ => throw new InvalidOperationException("observer")));
        var healthy = new ChannelObserver();
        using IDisposable healthySubscription = harness.Delivery.Subscribe(healthy);
        harness.Delivery.RequestRender(harness.Request(2));
        Assert.IsTrue(slowEntered.Wait(TimeSpan.FromSeconds(10)));
        _ = await healthy.NextAsync();
        slowRelease.Set();
        await harness.Delivery.DisposeAsync();
        Assert.AreEqual(1UL, harness.Delivery.Diagnostics.ObserverErrors);
    }

    [TestMethod]
    public async Task ReentrantObserverCanUnsubscribeItself()
    {
        await using PublicationHarness harness = await PublicationHarness.CreateAsync();
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        IDisposable? subscription = null;
        subscription = harness.Delivery.Subscribe(new DelegateObserver(_ =>
        { subscription!.Dispose(); completed.TrySetResult(); }));
        harness.Delivery.RequestRender(harness.Request(2));
        await completed.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [TestMethod]
    public async Task TwoConsumersReceiveMetadataOnlyAndOwnIndependentRasterRegions()
    {
        await using PublicationHarness harness = await PublicationHarness.CreateAsync();
        BScanRenderRequest request = harness.Request(2);
        BScanRasterRegion first = (await harness.Delivery.GetRegionAsync(request)).Region!;
        BScanRasterRegion second = (await harness.Delivery.GetRegionAsync(request)).Region!;
        Assert.AreEqual(2, harness.Delivery.Diagnostics.ActiveRasterOwners);
        first.Dispose();
        Assert.IsGreaterThan(0, second.Pixels.Length);
        second.Dispose();
        Assert.AreEqual(0, harness.Delivery.Diagnostics.ActiveRasterOwners);
        Assert.AreEqual(0L, harness.Delivery.Diagnostics.VisualBytesRented);
    }

    [TestMethod]
    public async Task SupersededMetadataCandidateIsNotPublishedAndLatestRequestWins()
    {
        await using PublicationHarness harness = await PublicationHarness.CreateAsync();
        var observer = new ChannelObserver();
        using IDisposable subscription = harness.Delivery.Subscribe(observer);
        var entered = new ManualResetEventSlim();
        var release = new ManualResetEventSlim();
        harness.Delivery.BeforeMetadataCandidateValidation = () => { entered.Set(); release.Wait(); };
        harness.Delivery.RequestRender(harness.Request(2));
        Assert.IsTrue(entered.Wait(TimeSpan.FromSeconds(10)));
        harness.Delivery.RequestRender(harness.Request(5));
        release.Set();

        BScanVisualUpdate update = await observer.NextAsync();

        Assert.AreEqual(5, update.Snapshot.RasterWidth);
        Assert.IsGreaterThanOrEqualTo(1UL, harness.Delivery.Diagnostics.VisualBuildsSuperseded);
        Assert.IsTrue(update.Snapshot.FullRefreshReason.HasFlag(BScanFullRefreshReason.Recovery));
    }

    [TestMethod]
    public async Task CancellationAfterQuotaAcquisitionReturnsPermitAndOwner()
    {
        await using PublicationHarness harness = await PublicationHarness.CreateAsync();
        var entered = new ManualResetEventSlim();
        var release = new ManualResetEventSlim();
        harness.Delivery.BeforeRasterCandidateValidation = () => { entered.Set(); release.Wait(); };
        using var cancellation = new CancellationTokenSource();
        Task<BScanRasterResult> candidate = Task.Run(async () =>
            await harness.Delivery.GetRegionAsync(harness.Request(2), cancellation.Token));
        Assert.IsTrue(entered.Wait(TimeSpan.FromSeconds(10)));
        cancellation.Cancel();
        release.Set();
        Assert.AreEqual(BScanRasterResultKind.Cancelled,
            (await candidate.WaitAsync(TimeSpan.FromSeconds(10))).Kind);
        harness.Delivery.BeforeRasterCandidateValidation = null;
        using BScanRasterRegion next = (await harness.Delivery.GetRegionAsync(harness.Request(2))).Region!;
        Assert.AreEqual(1, harness.Delivery.Diagnostics.ActiveRasterOwners);
    }

    [TestMethod]
    public async Task CloseCancelsThirdRequestWaitingForQuota()
    {
        await using PublicationHarness harness = await PublicationHarness.CreateAsync();
        BScanRasterRegion first = (await harness.Delivery.GetRegionAsync(harness.Request(2))).Region!;
        BScanRasterRegion second = (await harness.Delivery.GetRegionAsync(harness.Request(2))).Region!;
        Task<BScanRasterResult> waiting = harness.Delivery.GetRegionAsync(harness.Request(2)).AsTask();
        Task close = harness.Delivery.DisposeAsync().AsTask();

        Assert.AreEqual(BScanRasterResultKind.NotAvailable,
            (await waiting.WaitAsync(TimeSpan.FromSeconds(10))).Kind);
        await close.WaitAsync(TimeSpan.FromSeconds(10));
        first.Dispose();
        second.Dispose();
        Assert.AreEqual(0, harness.Delivery.Diagnostics.ActiveRasterOwners);
    }

    [TestMethod]
    public async Task TimerFailureIsDiagnosedAndCloseLeavesNoTimerOrCallback()
    {
        await using PublicationHarness harness = await PublicationHarness.CreateAsync();
        var observer = new ChannelObserver();
        using IDisposable subscription = harness.Delivery.Subscribe(observer);
        harness.Delivery.RequestRender(harness.Request(2));
        _ = await observer.NextAsync();
        var attempted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Clock.OnTimerCreate = () => attempted.TrySetResult();
        harness.Clock.FailTimerCreation = true;
        harness.Delivery.RequestRender(harness.Request(3));
        await attempted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await harness.Delivery.DisposeAsync();
        Assert.IsNotNull(harness.Delivery.Diagnostics.LastVisualError);
        Assert.AreEqual(BScanVisualCloseState.Closed, harness.Delivery.Diagnostics.CloseState);
    }

    private sealed class PublicationHarness : IAsyncDisposable
    {
        private PublicationHarness(BScanIngress ingress, BScanVisualDelivery delivery,
            ConventionalUtFrameMetadata metadata, ManualSimulatorTimeProvider clock)
        { Ingress = ingress; Delivery = delivery; Metadata = metadata; Clock = clock; }
        internal BScanIngress Ingress { get; }
        internal BScanVisualDelivery Delivery { get; }
        internal ConventionalUtFrameMetadata Metadata { get; }
        internal ManualSimulatorTimeProvider Clock { get; }
        internal static async Task<PublicationHarness> CreateAsync()
        {
            var clock = new ManualSimulatorTimeProvider();
            var options = new BScanOptions(1_000_000, 16 * 1024 * 1024, 8, 8, 2);
            var ingress = new BScanIngress(options, clock);
            var metadata = new ConventionalUtFrameMetadata(new("bscan-publication"),
                new(Guid.Parse("44000000-0000-0000-0000-000000000044")),
                new(new(1), 4, 1_000_000, 0, UtSignalMode.Rf), DateTimeOffset.UnixEpoch);
            ingress.OpenRun(metadata);
            var delivery = new BScanVisualDelivery(ingress, clock);
            var harness = new PublicationHarness(ingress, delivery, metadata, clock);
            await harness.AddAsync(0, 0);
            return harness;
        }
        internal async Task AddAsync(ulong sequence, ulong acquisitionIndex)
        {
            var indexed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Ingress.AfterIndexCommit = _ => indexed.TrySetResult();
            Ingress.Accept(Metadata, sequence, acquisitionIndex, TimeSpan.FromTicks((long)sequence), null, [1, -2, 3, -4]);
            await indexed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }
        internal BScanRenderRequest Request(int width) => new(Metadata.RunId,
            BScanSpatialViewportOperations.Reset(new(0, 7)), ScanTimeViewportOperations.Create(0, .000004),
            width, 4, 1);
        public async ValueTask DisposeAsync()
        {
            await Delivery.DisposeAsync();
            await Ingress.DisposeAsync();
        }
    }

    private sealed class ChannelObserver : IObserver<BScanVisualUpdate>
    {
        private readonly Channel<BScanVisualUpdate> _updates = Channel.CreateUnbounded<BScanVisualUpdate>();
        public void OnNext(BScanVisualUpdate value) => _updates.Writer.TryWrite(value);
        public void OnError(Exception error) => _updates.Writer.TryComplete(error);
        public void OnCompleted() => _updates.Writer.TryComplete();
        internal async Task<BScanVisualUpdate> NextAsync() =>
            await _updates.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
    }

    private sealed class DelegateObserver(Action<BScanVisualUpdate> callback) : IObserver<BScanVisualUpdate>
    {
        public void OnNext(BScanVisualUpdate value) => callback(value);
        public void OnError(Exception error) { }
        public void OnCompleted() { }
    }
}
