using UTStudio.Domain.Acquisition;
using UTStudio.Visualization.Core;

namespace UTStudio.Core.Tests.Visualization;

[TestClass]
public sealed class BScanVisualDeliveryTests
{
    private static readonly AcquisitionRunId RunId = new(Guid.Parse("41000000-0000-0000-0000-000000000041"));

    [TestMethod]
    public void BipolarMappingIsMonotonicAndReservesZeroForNoData()
    {
        byte previous = BScanBipolarIntensity8.Map(short.MinValue);
        Assert.AreEqual((byte)1, previous);
        for (int value = short.MinValue + 1; value <= short.MaxValue; value++)
        {
            byte mapped = BScanBipolarIntensity8.Map((short)value);
            Assert.IsGreaterThanOrEqualTo(previous, mapped);
            Assert.AreNotEqual(BScanBipolarIntensity8.NoData, mapped);
            previous = mapped;
        }
        Assert.AreEqual((byte)128, BScanBipolarIntensity8.Map(0));
        Assert.AreEqual((byte)255, BScanBipolarIntensity8.Map(short.MaxValue));
    }

    [TestMethod]
    public void SpatialViewportPreservesPrecisionBeyondDoubleIntegerRangeAndClamps()
    {
        const ulong first = 9_007_199_254_740_993;
        var domain = new BScanAcquisitionRange(first, first + 999);
        BScanSpatialViewport reset = BScanSpatialViewportOperations.Reset(domain);
        BScanSpatialViewport zoom = BScanSpatialViewportOperations.ZoomAt(reset, domain, .75m, 8m);
        decimal anchorBefore = reset.MinimumOffset + reset.Span * .75m;
        decimal anchorAfter = zoom.MinimumOffset + zoom.Span * .75m;

        Assert.AreEqual(anchorBefore, anchorAfter);
        Assert.AreEqual(1m, BScanSpatialViewportOperations.RelativeOffset(first + 1, first));
        Assert.AreEqual(reset, BScanSpatialViewportOperations.Pan(reset, domain, decimal.MaxValue));
        Assert.IsGreaterThanOrEqualTo(1m, BScanSpatialViewportOperations.ZoomAt(reset, domain, .5m, decimal.MaxValue).Span);
    }

    [TestMethod]
    public async Task FullRasterUsesNoDataGapsStableTieAndSurvivesRingWrap()
    {
        var ingress = new BScanIngress(Options(maximumColumns: 2, minimumColumns: 2));
        ConventionalUtFrameMetadata metadata = Metadata(sampleCount: 4);
        var indexed = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        int count = 0;
        ingress.AfterIndexCommit = _ => indexed.TrySetResult(Interlocked.Increment(ref count));
        ingress.OpenRun(metadata);
        await using var delivery = new BScanVisualDelivery(ingress);

        ingress.Accept(metadata, 0, 10, TimeSpan.Zero, null, new short[] { -100, 100, 0, 0 });
        await WaitForCount(indexed, 1);
        var request = Request(metadata.RunId, new(10, 12), 3, 1);
        BScanRasterResult result = await delivery.GetRegionAsync(request);

        Assert.AreEqual(BScanRasterResultKind.Available, result.Kind);
        BScanRasterRegion region = result.Region!;
        CollectionAssert.AreEqual(new byte[] { BScanBipolarIntensity8.Map(-100), 0, 0 }, region.Pixels.ToArray());
        ingress.Accept(metadata, 1, 11, TimeSpan.Zero, null, new short[] { 1, 2, 3, 4 });
        ingress.Accept(metadata, 2, 12, TimeSpan.Zero, null, new short[] { 5, 6, 7, 8 });
        Assert.AreEqual(BScanBipolarIntensity8.Map(-100), region.Pixels.Span[0]);
        region.Dispose();
        Assert.ThrowsExactly<ObjectDisposedException>(() => _ = region.Pixels);
        await ingress.DisposeAsync();
    }

    [TestMethod]
    public async Task RevisionMismatchForcesFullAndMatchingBaseProducesIncremental()
    {
        var ingress = new BScanIngress(Options(4, 2));
        ConventionalUtFrameMetadata metadata = Metadata(4);
        var indexed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ingress.AfterIndexCommit = _ => indexed.TrySetResult();
        ingress.OpenRun(metadata);
        await using var delivery = new BScanVisualDelivery(ingress);
        ingress.Accept(metadata, 0, 100, TimeSpan.Zero, null, new short[] { 0, 1, 2, 3 });
        await indexed.Task.WaitAsync(TimeSpan.FromSeconds(10));

        BScanRasterResult first = await delivery.GetRegionAsync(Request(metadata.RunId, new(100, 103), 4, 4));
        Assert.IsTrue(first.Region!.Snapshot.RequiresFullRefresh);
        BScanVisualSnapshot baseline = first.Region.Snapshot;
        first.Region.Dispose();

        indexed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        ingress.AfterIndexCommit = _ => indexed.TrySetResult();
        ingress.Accept(metadata, 1, 101, TimeSpan.Zero, null, new short[] { 4, 5, 6, 7 });
        await indexed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        BScanRenderRequest next = Request(metadata.RunId, new(100, 103), 4, 4) with
        {
            DisplayedSnapshotVersion = baseline.SnapshotVersion,
            DisplayedStoreRevision = baseline.StoreRevision,
            DisplayedWrapGeneration = baseline.WrapGeneration
        };
        BScanRasterResult incremental = await delivery.GetRegionAsync(next);
        Assert.AreEqual(BScanRasterResultKind.Available, incremental.Kind);
        Assert.IsFalse(incremental.Region!.Snapshot.RequiresFullRefresh);
        Assert.AreEqual(1, incremental.Region.Width);
        incremental.Region.Dispose();

        BScanRasterResult mismatch = await delivery.GetRegionAsync(next with { DisplayedStoreRevision = 0 });
        Assert.IsTrue(mismatch.Region!.Snapshot.RequiresFullRefresh);
        Assert.IsTrue(mismatch.Region.Snapshot.FullRefreshReason.HasFlag(BScanFullRefreshReason.BaseRevisionMismatch));
        mismatch.Region.Dispose();
        await ingress.DisposeAsync();
    }

    [TestMethod]
    public async Task OwnerQuotaIsBoundedAndDisposeIsIdempotent()
    {
        var ingress = new BScanIngress(Options(2, 2));
        ConventionalUtFrameMetadata metadata = Metadata(2);
        var indexed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ingress.AfterIndexCommit = _ => indexed.TrySetResult();
        ingress.OpenRun(metadata);
        var delivery = new BScanVisualDelivery(ingress);
        ingress.Accept(metadata, 0, 0, TimeSpan.Zero, null, new short[] { 1, 2 });
        await indexed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        BScanRenderRequest request = Request(metadata.RunId, new(0, 1), 2, 2);
        BScanRasterRegion first = (await delivery.GetRegionAsync(request)).Region!;
        BScanRasterRegion second = (await delivery.GetRegionAsync(request)).Region!;
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        Assert.AreEqual(BScanRasterResultKind.Cancelled, (await delivery.GetRegionAsync(request, cancelled.Token)).Kind);
        Assert.AreEqual(2, delivery.Diagnostics.ActiveRasterOwners);
        first.Dispose(); first.Dispose(); second.Dispose();
        Assert.AreEqual(0, delivery.Diagnostics.ActiveRasterOwners);
        ValueTask close1 = delivery.DisposeAsync();
        ValueTask close2 = delivery.DisposeAsync();
        await Task.WhenAll(close1.AsTask(), close2.AsTask());
        Assert.AreEqual(BScanVisualCloseState.Closed, delivery.Diagnostics.CloseState);
        await ingress.DisposeAsync();
    }

    [TestMethod]
    public async Task StopBlocksAdmissionAndDrainWaitsForTransferredOwnerWithoutForcingRelease()
    {
        var ingress = new BScanIngress(Options(2, 2));
        ConventionalUtFrameMetadata metadata = Metadata(2);
        var indexed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ingress.AfterIndexCommit = _ => indexed.TrySetResult();
        ingress.OpenRun(metadata);
        var delivery = new BScanVisualDelivery(ingress);
        ingress.Accept(metadata, 0, 0, TimeSpan.Zero, null, new short[] { 1, 2 });
        await indexed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        BScanRasterRegion owner = (await delivery.GetRegionAsync(Request(metadata.RunId, new(0, 1), 2, 2))).Region!;

        delivery.Stop();
        Task first = delivery.WaitForDrainAsync();
        Task second = delivery.WaitForDrainAsync();
        Assert.AreSame(first, second);
        Assert.IsFalse(first.IsCompleted);
        Assert.AreEqual(1, delivery.Diagnostics.ActiveRasterOwners);
        Assert.AreEqual(BScanRasterResultKind.NotAvailable,
            (await delivery.GetRegionAsync(Request(metadata.RunId, new(0, 1), 2, 2))).Kind);

        owner.Dispose();
        await first.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.AreEqual(0, delivery.Diagnostics.ActiveRasterOwners);
        await Task.WhenAll(delivery.DisposeAsync().AsTask(), delivery.DisposeAsync().AsTask());
        Assert.AreEqual(BScanVisualCloseState.Closed, delivery.Diagnostics.CloseState);
        await ingress.DisposeAsync();
    }

    [TestMethod]
    public async Task ObservablePublishesMetadataOnlyAndFailingObserverDoesNotStopPeer()
    {
        var ingress = new BScanIngress(Options(2, 2));
        ConventionalUtFrameMetadata metadata = Metadata(2);
        var indexed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ingress.AfterIndexCommit = _ => indexed.TrySetResult();
        ingress.OpenRun(metadata);
        var delivery = new BScanVisualDelivery(ingress);
        var observerFailed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using IDisposable failed = delivery.Subscribe(new ThrowingObserver(observerFailed));
        var received = new TaskCompletionSource<BScanVisualUpdate>(TaskCreationOptions.RunContinuationsAsynchronously);
        using IDisposable healthy = delivery.Subscribe(new DelegateObserver(update => received.TrySetResult(update)));
        ingress.Accept(metadata, 0, 0, TimeSpan.Zero, null, new short[] { 1, -2 });
        await indexed.Task.WaitAsync(TimeSpan.FromSeconds(10));

        delivery.RequestRender(Request(metadata.RunId, new(0, 1), 2, 2));
        BScanVisualUpdate update = await received.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await observerFailed.Task.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.AreEqual(metadata.RunId, update.Snapshot.RunId);
        Assert.AreEqual(1UL, update.Snapshot.StoreRevision);
        Assert.AreEqual(1UL, delivery.Diagnostics.ObserverErrors);
        await delivery.DisposeAsync();
        Assert.ThrowsExactly<ObjectDisposedException>(() => delivery.RequestRender(Request(metadata.RunId, new(0, 1), 2, 2)));
        await ingress.DisposeAsync();
    }

    private static async Task WaitForCount(TaskCompletionSource<int> signal, int expected)
    {
        int actual = await signal.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.AreEqual(expected, actual);
    }

    private static BScanRenderRequest Request(AcquisitionRunId run, BScanAcquisitionRange domain, int width, int height) =>
        new(run, BScanSpatialViewportOperations.Reset(domain), ScanTimeViewportOperations.Create(0, .000004),
            width, height, 1);

    private static BScanOptions Options(int maximumColumns, int minimumColumns) =>
        new(traceMemoryBudgetBytes: 1_000_000, visualMemoryBudgetBytes: 16 * 1024 * 1024,
            maximumColumns: maximumColumns, minimumColumns: minimumColumns, indexBucketSamples: 2);

    private static ConventionalUtFrameMetadata Metadata(int sampleCount) => new(
        new UtSourceId("bscan-visual"), RunId,
        new(new(1), sampleCount, 1_000_000, 0, UtSignalMode.Rf), DateTimeOffset.UnixEpoch);

    private sealed class ThrowingObserver(TaskCompletionSource entered) : IObserver<BScanVisualUpdate>
    {
        public void OnNext(BScanVisualUpdate value)
        {
            entered.TrySetResult();
            throw new InvalidOperationException("synthetic observer failure");
        }
        public void OnError(Exception error) { }
        public void OnCompleted() { }
    }

    private sealed class DelegateObserver(Action<BScanVisualUpdate> callback) : IObserver<BScanVisualUpdate>
    {
        public void OnNext(BScanVisualUpdate value) => callback(value);
        public void OnError(Exception error) { }
        public void OnCompleted() { }
    }
}
