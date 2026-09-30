using UTStudio.Domain.Acquisition;
using UTStudio.Visualization.Core;
using UTStudio.Visualization.Core.Scan2D;

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
    public async Task NoneKeepsExactBytesAndNearestUsesMaskAndFullRecovery()
    {
        var ingress = new BScanIngress(Options(4, 2));
        ConventionalUtFrameMetadata metadata = Metadata(4);
        var indexed = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        int committed = 0;
        ingress.AfterIndexCommit = _ => indexed.TrySetResult(Interlocked.Increment(ref committed));
        ingress.OpenRun(metadata);
        await using var delivery = new BScanVisualDelivery(ingress);
        ingress.Accept(metadata, 0, 10, TimeSpan.Zero, null, new short[] { 0, 0, 0, 0 });
        await WaitForCount(indexed, 1);
        indexed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        ingress.AfterIndexCommit = _ => indexed.TrySetResult(Interlocked.Increment(ref committed));
        ingress.Accept(metadata, 1, 12, TimeSpan.Zero, null, new short[] { 100, 100, 100, 100 });
        await WaitForCount(indexed, 2);

        BScanRenderRequest none = Request(metadata.RunId, new(10, 13), 4, 1);
        using BScanRasterRegion compatible = (await delivery.GetRegionAsync(none)).Region!;
        CollectionAssert.AreEqual(new byte[] { 128, 0, BScanBipolarIntensity8.Map(100), 0 }, compatible.Pixels.ToArray());
        CollectionAssert.AreEqual(new byte[] { 1, 0, 1, 0 }, compatible.ValidityMask.ToArray());

        BScanRenderRequest nearest = none with
        {
            Resampling = new(Scan2DInterpolationMode.NearestNeighbor),
            DisplayedSnapshotVersion = compatible.Snapshot.SnapshotVersion,
            DisplayedStoreRevision = compatible.Snapshot.StoreRevision,
            DisplayedWrapGeneration = compatible.Snapshot.WrapGeneration,
            Displayed = new(compatible.Snapshot.Stream,
                new(compatible.Snapshot.SnapshotVersion, compatible.Snapshot.StoreRevision, compatible.Snapshot.WrapGeneration),
                new(compatible.Snapshot.SpatialViewport, compatible.Snapshot.TimeViewport,
                    compatible.Snapshot.RasterWidth, compatible.Snapshot.RasterHeight,
                    compatible.Snapshot.RenderConfigurationVersion, compatible.Snapshot.Resampling))
        };
        BScanRasterResult transformedResult = await delivery.GetRegionAsync(nearest);
        Assert.AreEqual(BScanRasterResultKind.Available, transformedResult.Kind, transformedResult.Error);
        using BScanRasterRegion transformed = transformedResult.Region!;
        Assert.IsTrue(transformed.Snapshot.RequiresFullRefresh);
        Assert.IsTrue(transformed.Snapshot.FullRefreshReason.HasFlag(BScanFullRefreshReason.RenderConfigurationChanged));
        CollectionAssert.AreEqual(new byte[] { 128, 0, BScanBipolarIntensity8.Map(100), 0 }, transformed.Pixels.ToArray());
        CollectionAssert.AreEqual(new byte[] { 1, 0, 1, 0 }, transformed.ValidityMask.ToArray());
        byte[] transformedMask = transformed.ValidityMask.ToArray();
        Assert.IsTrue(transformed.Provenance.ToArray().Where((_, i) => transformedMask[i] != 0)
            .All(x => x == (byte)Scan2DSampleProvenance.AcquiredSupport));
        await ingress.DisposeAsync();
    }

    [TestMethod]
    public async Task CandidateIsStaleWhenModeChangesBeforeTransactionalCommit()
    {
        var ingress = new BScanIngress(Options(2, 2));
        ConventionalUtFrameMetadata metadata = Metadata(2);
        var indexed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ingress.AfterIndexCommit = _ => indexed.TrySetResult();
        ingress.OpenRun(metadata);
        await using var delivery = new BScanVisualDelivery(ingress);
        ingress.Accept(metadata, 0, 0, TimeSpan.Zero, null, new short[] { 1, 2 });
        await indexed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        BScanRenderRequest none = Request(metadata.RunId, new(0, 1), 2, 2);
        var entered = new ManualResetEventSlim();
        var release = new ManualResetEventSlim();
        delivery.BeforeRasterCandidateValidation = () => { entered.Set(); release.Wait(); };

        Task<BScanRasterResult> pending = Task.Run(async () => await delivery.GetRegionAsync(none));
        Assert.IsTrue(entered.Wait(TimeSpan.FromSeconds(10)));
        delivery.RequestRender(none with { Resampling = new(Scan2DInterpolationMode.NearestNeighbor) });
        release.Set();

        Assert.AreEqual(BScanRasterResultKind.Stale,
            (await pending.WaitAsync(TimeSpan.FromSeconds(10))).Kind);
        Assert.AreEqual(0, delivery.Diagnostics.ActiveRasterOwners);
        await ingress.DisposeAsync();
    }

    [TestMethod]
    public async Task CandidateIsStaleWhenAlgorithmVersionChangesBeforeTransactionalCommit()
    {
        var ingress = new BScanIngress(Options(2, 2));
        ConventionalUtFrameMetadata metadata = Metadata(2);
        var indexed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ingress.AfterIndexCommit = _ => indexed.TrySetResult();
        ingress.OpenRun(metadata);
        await using var delivery = new BScanVisualDelivery(ingress);
        ingress.Accept(metadata, 0, 0, TimeSpan.Zero, null, new short[] { 1, 2 });
        await indexed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        BScanRenderRequest request = Request(metadata.RunId, new(0, 1), 2, 2) with
        { Resampling = new(Scan2DInterpolationMode.Bicubic, algorithmVersion: 2) };
        var entered = new ManualResetEventSlim();
        var release = new ManualResetEventSlim();
        delivery.BeforeRasterCandidateValidation = () => { entered.Set(); release.Wait(); };

        Task<BScanRasterResult> pending = Task.Run(async () => await delivery.GetRegionAsync(request));
        Assert.IsTrue(entered.Wait(TimeSpan.FromSeconds(10)));
        delivery.RequestRender(request with
        { Resampling = new(Scan2DInterpolationMode.Bicubic, algorithmVersion: 3) });
        release.Set();

        Assert.AreEqual(BScanRasterResultKind.Stale,
            (await pending.WaitAsync(TimeSpan.FromSeconds(10))).Kind);
        Assert.AreEqual(0, delivery.Diagnostics.ActiveRasterOwners);
        await ingress.DisposeAsync();
    }

    [TestMethod]
    [DataRow(Scan2DInterpolationMode.Bilinear)]
    [DataRow(Scan2DInterpolationMode.Bicubic)]
    public async Task InterpolatingModesAreFullOnlyAndKeepScratchAndOwnersBalanced(
        Scan2DInterpolationMode mode)
    {
        var ingress = new BScanIngress(Options(4, 2));
        ConventionalUtFrameMetadata metadata = Metadata(4);
        var indexed = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        int committed = 0;
        ingress.AfterIndexCommit = _ => indexed.TrySetResult(Interlocked.Increment(ref committed));
        ingress.OpenRun(metadata);
        await using var delivery = new BScanVisualDelivery(ingress);
        ingress.Accept(metadata, 0, 0, TimeSpan.Zero, null, new short[] { 0, 10, 20, 30 });
        await WaitForCount(indexed, 1);
        indexed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        ingress.AfterIndexCommit = _ => indexed.TrySetResult(Interlocked.Increment(ref committed));
        ingress.Accept(metadata, 1, 1, TimeSpan.Zero, null, new short[] { 30, 20, 10, 0 });
        await WaitForCount(indexed, 2);

        BScanRenderRequest request = Request(metadata.RunId, new(0, 3), 8, 70) with
        { Resampling = new(mode) };
        BScanRasterResult firstResult = await delivery.GetRegionAsync(request);
        Assert.AreEqual(BScanRasterResultKind.Available, firstResult.Kind, firstResult.Error);
        using BScanRasterRegion first = firstResult.Region!;
        Assert.IsTrue(first.Snapshot.RequiresFullRefresh);
        Assert.AreEqual(8, first.Width);
        Assert.AreEqual((byte)1, first.ValidityMask.Span[63 * first.Width]);
        Assert.AreEqual((byte)1, first.ValidityMask.Span[64 * first.Width]);
        Assert.IsGreaterThan(0L, delivery.Diagnostics.MaximumVisualBytesRented);
        Assert.AreEqual(1, delivery.Diagnostics.ActiveRasterOwners);

        BScanRenderRequest based = request with
        {
            DisplayedSnapshotVersion = first.Snapshot.SnapshotVersion,
            DisplayedStoreRevision = first.Snapshot.StoreRevision,
            DisplayedWrapGeneration = first.Snapshot.WrapGeneration,
            Displayed = new(first.Snapshot.Stream,
                new(first.Snapshot.SnapshotVersion, first.Snapshot.StoreRevision, first.Snapshot.WrapGeneration),
                new(first.Snapshot.SpatialViewport, first.Snapshot.TimeViewport, first.Snapshot.RasterWidth,
                    first.Snapshot.RasterHeight, first.Snapshot.RenderConfigurationVersion, first.Snapshot.Resampling))
        };
        first.Dispose();
        BScanRasterResult secondResult = await delivery.GetRegionAsync(based);
        Assert.AreEqual(BScanRasterResultKind.Available, secondResult.Kind, secondResult.Error);
        using BScanRasterRegion second = secondResult.Region!;
        Assert.IsTrue(second.Snapshot.RequiresFullRefresh);
        Assert.IsTrue(second.Snapshot.FullRefreshReason.HasFlag(BScanFullRefreshReason.RenderConfigurationChanged));
        second.Dispose();
        Assert.AreEqual(0, delivery.Diagnostics.ActiveRasterOwners);
        Assert.AreEqual(0L, delivery.Diagnostics.VisualBytesRented);
        await ingress.DisposeAsync();
    }

    [TestMethod]
    [DataRow(Scan2DInterpolationMode.Bilinear)]
    [DataRow(Scan2DInterpolationMode.Bicubic)]
    public async Task InterpolatingModesAtMaximumRasterStayWithinNeutralBudget(
        Scan2DInterpolationMode mode)
    {
        var ingress = new BScanIngress(Options(4, 2));
        ConventionalUtFrameMetadata metadata = Metadata(4);
        var indexed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ingress.AfterIndexCommit = _ => indexed.TrySetResult();
        ingress.OpenRun(metadata);
        await using var delivery = new BScanVisualDelivery(ingress);
        ingress.Accept(metadata, 0, 0, TimeSpan.Zero, null, new short[] { short.MinValue, 0, short.MaxValue, 1 });
        await indexed.Task.WaitAsync(TimeSpan.FromSeconds(10));

        BScanRenderRequest none = Request(metadata.RunId, new(0, 3), 1024, 1024);
        BScanRasterResult baselineResult = await delivery.GetRegionAsync(none);
        Assert.AreEqual(BScanRasterResultKind.Available, baselineResult.Kind, baselineResult.Error);
        using BScanRasterRegion baseline = baselineResult.Region!;
        BScanRenderRequest request = none with
        { Resampling = new(mode) };
        BScanRasterResult result = await delivery.GetRegionAsync(request);
        Assert.AreEqual(BScanRasterResultKind.Available, result.Kind, result.Error);
        using BScanRasterRegion region = result.Region!;
        Assert.AreEqual(checked(1024 * 1024), region.Pixels.Length);
        Assert.AreEqual(8_650_752L, delivery.Diagnostics.MaximumVisualBytesRented);
        Assert.IsLessThanOrEqualTo(16L * 1024 * 1024, delivery.Diagnostics.MaximumVisualBytesRented);
        region.Dispose(); baseline.Dispose();
        Assert.AreEqual(0L, delivery.Diagnostics.VisualBytesRented);
        await ingress.DisposeAsync();
    }

    [TestMethod]
    public async Task ConsumerByteCeilingPreservesTheOtherConsumersProportionalShare()
    {
        var options = new BScanOptions(traceMemoryBudgetBytes: 1_000_000,
            visualMemoryBudgetBytes: 16 * 1024 * 1024, maximumColumns: 4, minimumColumns: 2,
            indexBucketSamples: 2, maximumRasterConsumers: 2, maximumRasterOwnersPerConsumer: 3);
        var ingress = new BScanIngress(options);
        ConventionalUtFrameMetadata metadata = Metadata(4);
        var indexed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ingress.AfterIndexCommit = _ => indexed.TrySetResult();
        ingress.OpenRun(metadata);
        await using var delivery = new BScanVisualDelivery(ingress);
        Assert.IsTrue(delivery.RegisterConsumer("first"));
        Assert.IsTrue(delivery.RegisterConsumer("second"));
        ingress.Accept(metadata, 0, 0, TimeSpan.Zero, null, new short[] { 1, 2, 3, 4 });
        await indexed.Task.WaitAsync(TimeSpan.FromSeconds(10));

        BScanRenderRequest request = Request(metadata.RunId, new(0, 3), 1024, 1024) with
        {
            ConsumerId = "first",
            Resampling = new(Scan2DInterpolationMode.Bilinear)
        };
        using BScanRasterRegion first = (await delivery.GetRegionAsync(request)).Region!;
        using BScanRasterRegion second = (await delivery.GetRegionAsync(request)).Region!;
        using BScanRasterRegion otherConsumer = (await delivery.GetRegionAsync(request with
        { ConsumerId = "second" })).Region!;
        BScanRasterResult rejected = await delivery.GetRegionAsync(request);

        Assert.AreEqual(BScanRasterResultKind.ResourceLimit, rejected.Kind);
        Assert.AreEqual(3, delivery.Diagnostics.ActiveRasterOwners);
        first.Dispose();
        second.Dispose();
        otherConsumer.Dispose();
        Assert.AreEqual(0L, delivery.Diagnostics.VisualBytesRented);
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
