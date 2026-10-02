using System.Buffers;
using UTStudio.Domain.Acquisition;
using UTStudio.Visualization.Core;
using UTStudio.Visualization.Core.Scan2D;

namespace UTStudio.Core.Tests.Visualization;

[TestClass]
public sealed class BScanVisualDeliveryTests
{
    private static readonly AcquisitionRunId RunId = new(Guid.Parse("41000000-0000-0000-0000-000000000041"));

    [TestMethod]
    public void AuditRingIsBoundedOrderedAndReportsOverflow()
    {
        var audit = new BScanVisualAudit(2);
        audit.Record(BScanVisualAuditEventKind.BootstrapRequested, "c", null, 1, 1);
        audit.Record(BScanVisualAuditEventKind.BuildAdmitted, "c", null, 2, 2);
        audit.Record(BScanVisualAuditEventKind.BuildStarted, "c", null, 3, 3);

        BScanVisualAuditSnapshot snapshot = audit.Snapshot();
        Assert.HasCount(2, snapshot.Entries);
        Assert.AreEqual(1L, snapshot.OverflowCount);
        Assert.IsLessThan(snapshot.Entries[1].Sequence, snapshot.Entries[0].Sequence);
        Assert.AreEqual(BScanVisualAuditEventKind.BuildAdmitted, snapshot.Entries[0].Kind);
        Assert.AreEqual(BScanVisualAuditEventKind.BuildStarted, snapshot.Entries[1].Kind);
    }

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
    public async Task Bilinear_upsamples_visible_rf_samples_along_time_axis_instead_of_repeating_rows()
    {
        var ingress = new BScanIngress(Options(1, 1));
        ConventionalUtFrameMetadata metadata = Metadata(8);
        var indexed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ingress.AfterIndexCommit = _ => indexed.TrySetResult();
        ingress.OpenRun(metadata);
        await using var delivery = new BScanVisualDelivery(ingress);
        ingress.Accept(metadata, 0, 0, TimeSpan.Zero, null,
            new short[] { -1, -1, short.MinValue, 0, short.MaxValue, 0, -1, -1 });
        await indexed.Task.WaitAsync(TimeSpan.FromSeconds(10));

        var request = new BScanRenderRequest(metadata.RunId, BScanSpatialViewportOperations.Reset(new(0, 0)),
            ScanTimeViewportOperations.Create(.000002, .000006), 1, 8, 1,
            Resampling: new(Scan2DInterpolationMode.Bilinear));
        BScanRasterResult result = await delivery.GetRegionAsync(request);

        Assert.AreEqual(BScanRasterResultKind.Available, result.Kind, result.Error);
        using BScanRasterRegion region = result.Region!;
        Assert.IsTrue(region.ValidityMask.Span.ToArray().All(value => value == 1));
        CollectionAssert.AreEqual(new byte[] { 1, 33, 96, 160, 223, 223, 160, 128 },
            region.Pixels.Span.ToArray());
        Assert.IsTrue(region.Provenance.Span.ToArray().All(value =>
            value == (byte)Scan2DSampleProvenance.VisualDerived));
        region.Dispose();
        await delivery.DisposeAsync();
        await delivery.DisposeAsync();
        await ingress.DisposeAsync();
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
        await delivery.DisposeAsync();
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
        await delivery.DisposeAsync();
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
        transformed.Dispose();
        compatible.Dispose();
        await delivery.DisposeAsync();
        await delivery.DisposeAsync();
        await ingress.DisposeAsync();
    }

    [TestMethod]
    public async Task LanczosInteriorViewportUsesHorizontalHaloAndSeesGapOutsideVisibleRange()
    {
        var ingress = new BScanIngress(Options(8, 2));
        ConventionalUtFrameMetadata metadata = Metadata(4);
        var indexed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int remaining = 7;
        ingress.AfterIndexCommit = _ => { if (Interlocked.Decrement(ref remaining) == 0) indexed.TrySetResult(); };
        ingress.OpenRun(metadata);
        await using var delivery = new BScanVisualDelivery(ingress);
        ulong[] acquisitionIndices = [0, 1, 2, 4, 5, 6, 7];
        for (int index = 0; index < acquisitionIndices.Length; index++)
            ingress.Accept(metadata, (ulong)index, acquisitionIndices[index], TimeSpan.Zero, null,
                new short[] { 100, 100, 100, 100 });
        await indexed.Task.WaitAsync(TimeSpan.FromSeconds(10));

        var request = new BScanRenderRequest(metadata.RunId, new(0, 4, 6),
            ScanTimeViewportOperations.Create(0, .000004), 2, 1, 1,
            Resampling: new(Scan2DInterpolationMode.Lanczos));
        using BScanRasterRegion region = (await delivery.GetRegionAsync(request)).Region!;

        CollectionAssert.AreEqual(new byte[] { 0, 0 }, region.ValidityMask.ToArray());
        CollectionAssert.AreEqual(new byte[] { 0, 0 }, region.Pixels.ToArray());
        region.Dispose();
        Assert.AreEqual(0, delivery.Diagnostics.ActiveRasterOwners);
        await delivery.DisposeAsync();
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
        delivery.BeforeRasterCandidateCommit = () => { entered.Set(); release.Wait(); };

        Task<BScanRasterResult> pending = Task.Run(async () => await delivery.GetRegionAsync(none));
        Assert.IsTrue(entered.Wait(TimeSpan.FromSeconds(10)));
        delivery.RequestRender(none with { Resampling = new(Scan2DInterpolationMode.Lanczos) });
        release.Set();

        Assert.AreEqual(BScanRasterResultKind.Stale,
            (await pending.WaitAsync(TimeSpan.FromSeconds(10))).Kind);
        delivery.BeforeRasterCandidateCommit = null;
        Assert.AreEqual(0, delivery.Diagnostics.ActiveRasterOwners);
        await delivery.DisposeAsync();
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
        { Resampling = new(Scan2DInterpolationMode.Lanczos, algorithmVersion: 2) };
        var entered = new ManualResetEventSlim();
        var release = new ManualResetEventSlim();
        delivery.BeforeRasterCandidateCommit = () => { entered.Set(); release.Wait(); };

        Task<BScanRasterResult> pending = Task.Run(async () => await delivery.GetRegionAsync(request));
        Assert.IsTrue(entered.Wait(TimeSpan.FromSeconds(10)));
        delivery.RequestRender(request with
        { Resampling = new(Scan2DInterpolationMode.Lanczos, algorithmVersion: 3) });
        release.Set();

        Assert.AreEqual(BScanRasterResultKind.Stale,
            (await pending.WaitAsync(TimeSpan.FromSeconds(10))).Kind);
        delivery.BeforeRasterCandidateCommit = null;
        Assert.AreEqual(0, delivery.Diagnostics.ActiveRasterOwners);
        await delivery.DisposeAsync();
        await ingress.DisposeAsync();
    }

    [TestMethod]
    [DataRow(Scan2DInterpolationMode.Bilinear)]
    [DataRow(Scan2DInterpolationMode.Bicubic)]
    [DataRow(Scan2DInterpolationMode.Lanczos)]
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
        Assert.IsGreaterThan(0L, delivery.Diagnostics.VisualBytesRented);
        await delivery.DisposeAsync();
        Assert.AreEqual(0L, delivery.Diagnostics.VisualBytesRented);
        await delivery.DisposeAsync();
        await ingress.DisposeAsync();
    }

    [TestMethod]
    [DataRow(Scan2DInterpolationMode.Bilinear)]
    [DataRow(Scan2DInterpolationMode.Bicubic)]
    [DataRow(Scan2DInterpolationMode.Lanczos)]
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
        long expectedMaximum = mode switch
        {
            Scan2DInterpolationMode.Bilinear => 8_470_576L,
            Scan2DInterpolationMode.Bicubic => 8_552_496L,
            Scan2DInterpolationMode.Lanczos => 8_585_228L,
            _ => throw new AssertFailedException("Unexpected interpolating mode.")
        };
        Assert.AreEqual(expectedMaximum, delivery.Diagnostics.MaximumVisualBytesRented);
        Assert.IsLessThanOrEqualTo(16L * 1024 * 1024, delivery.Diagnostics.MaximumVisualBytesRented);
        Assert.IsLessThanOrEqualTo(16L * 1024 * 1024,
            delivery.VisualMemorySnapshot.MaximumOwnedBytes);
        region.Dispose(); baseline.Dispose();
        Assert.IsGreaterThan(0L, delivery.Diagnostics.VisualBytesRented);
        await delivery.DisposeAsync();
        Assert.AreEqual(0L, delivery.Diagnostics.VisualBytesRented);
        await delivery.DisposeAsync();
        await ingress.DisposeAsync();
    }

    [TestMethod]
    public async Task ConcurrentLanczosBuildsPreserveTheOtherConsumersProportionalShare()
    {
        var options = new BScanOptions(1_000_000, 16 * 1024 * 1024, 4, 2, 2,
            maximumRasterConsumers: 2, maximumRasterOwnersPerConsumer: 2);
        var ingress = new BScanIngress(options);
        ConventionalUtFrameMetadata metadata = Metadata(4);
        var indexed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ingress.AfterIndexCommit = _ => indexed.TrySetResult();
        ingress.OpenRun(metadata);
        var delivery = new BScanVisualDelivery(ingress);
        Assert.IsTrue(delivery.RegisterConsumer("first"));
        Assert.IsTrue(delivery.RegisterConsumer("second"));
        ingress.Accept(metadata, 0, 0, TimeSpan.Zero, null, [1, 2, 3, 4]);
        await indexed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var firstKernelEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var releaseFirstKernel = new ManualResetEventSlim();
        int blocked = 0;
        delivery.AfterLanczosKernelRowProcessed = _ =>
        {
            if (Interlocked.CompareExchange(ref blocked, 1, 0) != 0) { return; }
            firstKernelEntered.TrySetResult();
            if (!releaseFirstKernel.Wait(TimeSpan.FromSeconds(10)))
            { throw new TimeoutException("The first Lanczos consumer was not released."); }
        };
        BScanRenderRequest request = Request(metadata.RunId, new(0, 3), 1024, 1024) with
        { Resampling = new(Scan2DInterpolationMode.Lanczos) };
        Task<BScanRasterResult> first = Task.Run(async () => await delivery.GetRegionAsync(request with
        { ConsumerId = "first" }));
        await firstKernelEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        BScanRasterResult second;
        try
        {
            BScanRasterResult monopolizing = await delivery.GetRegionAsync(request with
            { ConsumerId = "first" });
            Assert.AreEqual(BScanRasterResultKind.ResourceLimit, monopolizing.Kind,
                "Scratch charged to the first consumer must prevent it from consuming the second share.");
            second = await delivery.GetRegionAsync(request with { ConsumerId = "second" })
                .AsTask().WaitAsync(TimeSpan.FromSeconds(10));
            Assert.AreEqual(BScanRasterResultKind.Available, second.Kind, second.Error);
            Assert.IsLessThanOrEqualTo(16L * 1024 * 1024,
                delivery.VisualMemorySnapshot.MaximumOwnedBytes);
        }
        finally { releaseFirstKernel.Set(); }
        BScanRasterResult firstResult = await first.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.AreEqual(BScanRasterResultKind.Available, firstResult.Kind, firstResult.Error);
        firstResult.Region!.Dispose();
        second.Region!.Dispose();
        delivery.AfterLanczosKernelRowProcessed = null;
        Assert.AreEqual(0, delivery.Diagnostics.ActiveRasterOwners);
        await delivery.DisposeAsync();
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
            Resampling = new(Scan2DInterpolationMode.None)
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
        await delivery.DisposeAsync();
        Assert.AreEqual(0L, delivery.Diagnostics.VisualBytesRented);
        await delivery.DisposeAsync();
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
        BScanRasterResult third = await delivery.GetRegionAsync(request);
        Assert.AreEqual(BScanRasterResultKind.ResourceLimit, third.Kind);
        Assert.AreEqual(2, delivery.Diagnostics.ActiveRasterOwners);

        first.Dispose();
        first.Dispose();
        BScanRasterResult admittedAfterRelease = await delivery.GetRegionAsync(request);
        Assert.AreEqual(BScanRasterResultKind.Available, admittedAfterRelease.Kind,
            admittedAfterRelease.Error);
        BScanRasterRegion replacement = admittedAfterRelease.Region!;
        Assert.AreEqual(2, delivery.Diagnostics.ActiveRasterOwners);

        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        Assert.AreEqual(BScanRasterResultKind.Cancelled, (await delivery.GetRegionAsync(request, cancelled.Token)).Kind);
        Assert.AreEqual(2, delivery.Diagnostics.ActiveRasterOwners);
        second.Dispose(); replacement.Dispose();
        Assert.AreEqual(0, delivery.Diagnostics.ActiveRasterOwners);
        ValueTask close1 = delivery.DisposeAsync();
        ValueTask close2 = delivery.DisposeAsync();
        await Task.WhenAll(close1.AsTask(), close2.AsTask());
        Assert.AreEqual(BScanVisualCloseState.Closed, delivery.Diagnostics.CloseState);
        await delivery.DisposeAsync();
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
        await delivery.DisposeAsync();
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
        Assert.IsTrue(await Task.Run(() => SpinWait.SpinUntil(
            () => delivery.Diagnostics.ObserverErrors == 1, TimeSpan.FromSeconds(10))),
            "The observer error counter did not settle after the failing callback.");

        Assert.AreEqual(metadata.RunId, update.Snapshot.RunId);
        Assert.AreEqual(1UL, update.Snapshot.StoreRevision);
        Assert.AreEqual(1UL, delivery.Diagnostics.ObserverErrors);
        await delivery.DisposeAsync();
        Assert.ThrowsExactly<ObjectDisposedException>(() => delivery.RequestRender(Request(metadata.RunId, new(0, 1), 2, 2)));
        await delivery.DisposeAsync();
        await ingress.DisposeAsync();
    }

    [TestMethod]
    public async Task DisposeBeforeCommitRejectsCandidateAndAuditOrdersClosure()
    {
        var ingress = new BScanIngress(Options(2, 2));
        ConventionalUtFrameMetadata metadata = Metadata(2);
        var indexed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ingress.AfterIndexCommit = _ => indexed.TrySetResult();
        ingress.OpenRun(metadata);
        var delivery = new BScanVisualDelivery(ingress);
        delivery.EnableAudit(64);
        ingress.Accept(metadata, 0, 0, TimeSpan.Zero, null, [1, 2]);
        await indexed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        BScanRenderRequest request = Request(metadata.RunId, new(0, 1), 2, 2);
        delivery.RequestRender(request);
        var entered = new ManualResetEventSlim();
        var release = new ManualResetEventSlim();
        delivery.BeforeRasterCandidateCommit = () => { entered.Set(); release.Wait(); };

        Task<BScanRasterResult> candidate = Task.Run(async () => await delivery.GetRegionAsync(request));
        Assert.IsTrue(entered.Wait(TimeSpan.FromSeconds(10)));
        long auditBeforeDispose = delivery.AuditSnapshot.Entries.Max(entry => entry.Sequence);
        Task disposal = delivery.DisposeAsync().AsTask();
        Assert.IsFalse(disposal.IsCompleted);
        Assert.AreEqual(BScanRasterResultKind.NotAvailable,
            (await delivery.GetRegionAsync(request)).Kind);
        release.Set();

        Assert.AreEqual(BScanRasterResultKind.NotAvailable,
            (await candidate.WaitAsync(TimeSpan.FromSeconds(10))).Kind);
        await disposal.WaitAsync(TimeSpan.FromSeconds(10));
        BScanVisualAuditEntry[] entries = [.. delivery.AuditSnapshot.Entries];
        Assert.IsTrue(entries.Any(entry => entry.Kind == BScanVisualAuditEventKind.CommitAttempted));
        Assert.IsTrue(entries.Any(entry => entry.Kind == BScanVisualAuditEventKind.CandidateRejectedClosed));
        Assert.IsFalse(entries.Any(entry => entry.Sequence > auditBeforeDispose &&
            entry.Kind == BScanVisualAuditEventKind.CandidateCommitted));
        Assert.AreEqual(0, delivery.Diagnostics.ActiveRasterOwners);
        await delivery.DisposeAsync();
        await ingress.DisposeAsync();
    }

    [TestMethod]
    public async Task TwoLaterRequestsSupersedeCandidateAndOnlyLatestCanCommit()
    {
        var ingress = new BScanIngress(Options(2, 2));
        ConventionalUtFrameMetadata metadata = Metadata(2);
        var indexed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ingress.AfterIndexCommit = _ => indexed.TrySetResult();
        ingress.OpenRun(metadata);
        await using var delivery = new BScanVisualDelivery(ingress);
        delivery.EnableAudit(128);
        ingress.Accept(metadata, 0, 0, TimeSpan.Zero, null, [1, 2]);
        await indexed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        BScanRenderRequest first = Request(metadata.RunId, new(0, 1), 2, 2);
        delivery.RequestRender(first);
        var entered = new ManualResetEventSlim();
        var release = new ManualResetEventSlim();
        delivery.BeforeRasterCandidateCommit = () => { entered.Set(); release.Wait(); };
        Task<BScanRasterResult> candidate = Task.Run(async () => await delivery.GetRegionAsync(first));
        Assert.IsTrue(entered.Wait(TimeSpan.FromSeconds(10)));
        long auditBeforeLaterRequests = delivery.AuditSnapshot.Entries.Max(entry => entry.Sequence);

        BScanRenderRequest second = first with { Resampling = new(Scan2DInterpolationMode.Bilinear) };
        BScanRenderRequest third = first with { Resampling = new(Scan2DInterpolationMode.Lanczos) };
        delivery.RequestRender(second);
        delivery.RequestRender(third);
        release.Set();
        Assert.AreEqual(BScanRasterResultKind.Stale,
            (await candidate.WaitAsync(TimeSpan.FromSeconds(10))).Kind);
        delivery.BeforeRasterCandidateCommit = null;
        using BScanRasterRegion latest = (await delivery.GetRegionAsync(third)).Region!;
        Assert.AreEqual(Scan2DInterpolationMode.Lanczos, latest.Snapshot.Resampling.Mode);

        BScanVisualAuditEntry[] entries = [.. delivery.AuditSnapshot.Entries];
        Assert.AreEqual(3, entries.Count(entry => entry.Kind == BScanVisualAuditEventKind.BuildAdmitted));
        Assert.IsTrue(entries.Any(entry => entry.Kind == BScanVisualAuditEventKind.CandidateRejectedStale));
        long latestGeneration = entries.Where(entry => entry.Kind == BScanVisualAuditEventKind.BuildAdmitted)
            .Max(entry => entry.RequestGeneration);
        Assert.IsFalse(entries.Any(entry => entry.Sequence > auditBeforeLaterRequests &&
            entry.Kind == BScanVisualAuditEventKind.CandidateCommitted &&
            entry.RequestGeneration != 0 && entry.RequestGeneration < latestGeneration));
        Assert.IsTrue(entries.Any(entry => entry.Kind == BScanVisualAuditEventKind.CandidateCommitted &&
            entry.RequestGeneration == latestGeneration));
        latest.Dispose();
        Assert.AreEqual(0, delivery.Diagnostics.ActiveRasterOwners);
        await delivery.DisposeAsync();
        await ingress.DisposeAsync();
    }

    [TestMethod]
    public async Task RemovingConsumerWithPendingBuildRejectsWithoutCallbackOrRetainedState()
    {
        var ingress = new BScanIngress(Options(2, 2));
        ConventionalUtFrameMetadata metadata = Metadata(2);
        var indexed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ingress.AfterIndexCommit = _ => indexed.TrySetResult();
        ingress.OpenRun(metadata);
        await using var delivery = new BScanVisualDelivery(ingress);
        delivery.EnableAudit(64);
        Assert.IsTrue(delivery.RegisterConsumer("retiring"));
        ingress.Accept(metadata, 0, 0, TimeSpan.Zero, null, [1, 2]);
        await indexed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        BScanRenderRequest request = Request(metadata.RunId, new(0, 1), 2, 2) with
        { ConsumerId = "retiring" };
        delivery.RequestRender(request);
        var entered = new ManualResetEventSlim();
        var release = new ManualResetEventSlim();
        delivery.BeforeRasterCandidateCommit = () => { entered.Set(); release.Wait(); };
        Task<BScanRasterResult> candidate = Task.Run(async () => await delivery.GetRegionAsync(request));
        Assert.IsTrue(entered.Wait(TimeSpan.FromSeconds(10)));
        delivery.UnregisterConsumer("retiring");
        release.Set();

        Assert.AreEqual(BScanRasterResultKind.NotAvailable,
            (await candidate.WaitAsync(TimeSpan.FromSeconds(10))).Kind);
        BScanVisualAuditEntry[] entries = [.. delivery.AuditSnapshot.Entries];
        long removed = entries.Single(entry => entry.Kind == BScanVisualAuditEventKind.ConsumerRemoved).Sequence;
        Assert.IsFalse(entries.Any(entry => entry.Sequence > removed &&
            entry.Kind is BScanVisualAuditEventKind.CandidateCommitted or
                BScanVisualAuditEventKind.CallbackStarted));
        Assert.AreEqual(0, delivery.RetainedSnapshotCount);
        Assert.AreEqual(0, delivery.Diagnostics.ActiveRasterOwners);
        await delivery.DisposeAsync();
        await ingress.DisposeAsync();
    }

    [TestMethod]
    public async Task NewRunBeforeCommitRejectsOldCandidateAndOnlyNewRunCanCommit()
    {
        var ingress = new BScanIngress(Options(2, 2));
        ConventionalUtFrameMetadata firstMetadata = Metadata(2);
        var indexed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ingress.AfterIndexCommit = _ => indexed.TrySetResult();
        ingress.OpenRun(firstMetadata);
        await using var delivery = new BScanVisualDelivery(ingress);
        delivery.EnableAudit(128);
        ingress.Accept(firstMetadata, 0, 0, TimeSpan.Zero, null, [1, 2]);
        await indexed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        BScanRenderRequest first = Request(firstMetadata.RunId, new(0, 1), 2, 2);
        delivery.RequestRender(first);
        var entered = new ManualResetEventSlim();
        var release = new ManualResetEventSlim();
        delivery.BeforeRasterCandidateCommit = () => { entered.Set(); release.Wait(); };
        Task<BScanRasterResult> oldCandidate = Task.Run(async () => await delivery.GetRegionAsync(first));
        Assert.IsTrue(entered.Wait(TimeSpan.FromSeconds(10)));

        ingress.CloseRun(firstMetadata.RunId);
        await ingress.WaitForCloseAsync(firstMetadata.RunId);
        var secondRun = new AcquisitionRunId(Guid.Parse("12000000-0000-0000-0000-000000000012"));
        var secondMetadata = new ConventionalUtFrameMetadata(firstMetadata.SourceId, secondRun,
            firstMetadata.Configuration, firstMetadata.RunStartedAtUtc.AddSeconds(1));
        var secondIndexed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ingress.AfterIndexCommit = _ => secondIndexed.TrySetResult();
        ingress.OpenRun(secondMetadata);
        ingress.Accept(secondMetadata, 0, 0, TimeSpan.Zero, null, [3, 4]);
        await secondIndexed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        release.Set();
        Assert.AreEqual(BScanRasterResultKind.Stale,
            (await oldCandidate.WaitAsync(TimeSpan.FromSeconds(10))).Kind);

        delivery.BeforeRasterCandidateCommit = null;
        BScanRenderRequest second = first with { RunId = secondRun };
        delivery.RequestRender(second);
        using BScanRasterRegion current = (await delivery.GetRegionAsync(second)).Region!;
        Assert.AreEqual(secondRun, current.Snapshot.RunId);
        BScanVisualAuditEntry[] entries = [.. delivery.AuditSnapshot.Entries];
        Assert.IsTrue(entries.Any(entry => entry.RunId == firstMetadata.RunId &&
            entry.Kind == BScanVisualAuditEventKind.CandidateRejectedStale));
        Assert.IsTrue(entries.Any(entry => entry.RunId == secondRun &&
            entry.Kind == BScanVisualAuditEventKind.CandidateCommitted));
        current.Dispose();
        Assert.AreEqual(0, delivery.Diagnostics.ActiveRasterOwners);
        await delivery.DisposeAsync();
        await ingress.DisposeAsync();
    }

    [TestMethod]
    public async Task ReentrantCallbackAdmitsLaterModeAfterCurrentCallbackCompletes()
    {
        var ingress = new BScanIngress(Options(2, 2));
        ConventionalUtFrameMetadata metadata = Metadata(2);
        var indexed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ingress.AfterIndexCommit = _ => indexed.TrySetResult();
        ingress.OpenRun(metadata);
        await using var delivery = new BScanVisualDelivery(ingress);
        delivery.EnableAudit(128);
        ingress.Accept(metadata, 0, 0, TimeSpan.Zero, null, [1, 2]);
        await indexed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        BScanRenderRequest first = Request(metadata.RunId, new(0, 1), 2, 2);
        BScanRenderRequest later = first with { Resampling = new(Scan2DInterpolationMode.Bicubic) };
        var callback = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int reentered = 0;
        using IDisposable subscription = delivery.Subscribe(new DelegateObserver(_ =>
        {
            if (Interlocked.Exchange(ref reentered, 1) == 0) { delivery.RequestRender(later); }
            callback.TrySetResult();
        }));

        delivery.RequestRender(first);
        await callback.Task.WaitAsync(TimeSpan.FromSeconds(10));
        using BScanRasterRegion region = (await delivery.GetRegionAsync(later)).Region!;
        Assert.AreEqual(Scan2DInterpolationMode.Bicubic, region.Snapshot.Resampling.Mode);
        BScanVisualAuditEntry[] entries = [.. delivery.AuditSnapshot.Entries];
        long started = entries.First(entry => entry.Kind == BScanVisualAuditEventKind.CallbackStarted).Sequence;
        long admitted = entries.First(entry => entry.Kind == BScanVisualAuditEventKind.BuildAdmitted &&
            entry.Sequence > started).Sequence;
        long completed = entries.First(entry => entry.Kind == BScanVisualAuditEventKind.CallbackCompleted).Sequence;
        Assert.IsTrue(started < admitted && admitted < completed);
        region.Dispose();
        Assert.AreEqual(0, delivery.Diagnostics.ActiveRasterOwners);
        await delivery.DisposeAsync();
        await ingress.DisposeAsync();
    }

    [TestMethod]
    public async Task FaultAfterSupersessionDoesNotReplaceCurrentDiagnosticsOrRecovery()
    {
        var ingress = new BScanIngress(Options(2, 2));
        ConventionalUtFrameMetadata metadata = Metadata(2);
        var indexed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ingress.AfterIndexCommit = _ => indexed.TrySetResult();
        ingress.OpenRun(metadata);
        await using var delivery = new BScanVisualDelivery(ingress);
        delivery.EnableAudit(128);
        ingress.Accept(metadata, 0, 0, TimeSpan.Zero, null, [1, 2]);
        await indexed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        BScanRenderRequest first = Request(metadata.RunId, new(0, 1), 2, 2);
        BScanRenderRequest later = first with { Resampling = new(Scan2DInterpolationMode.Bilinear) };
        delivery.RequestRender(first);
        var entered = new ManualResetEventSlim();
        var release = new ManualResetEventSlim();
        delivery.BeforeRasterCandidateCommit = () =>
        {
            entered.Set(); release.Wait(); throw new InvalidOperationException("obsolete failure");
        };
        Task<BScanRasterResult> obsolete = Task.Run(async () => await delivery.GetRegionAsync(first));
        Assert.IsTrue(entered.Wait(TimeSpan.FromSeconds(10)));
        delivery.RequestRender(later);
        release.Set();
        Assert.AreEqual(BScanRasterResultKind.Faulted,
            (await obsolete.WaitAsync(TimeSpan.FromSeconds(10))).Kind);
        Assert.IsNull(delivery.Diagnostics.LastVisualError);
        delivery.BeforeRasterCandidateCommit = null;
        using BScanRasterRegion current = (await delivery.GetRegionAsync(later)).Region!;
        Assert.AreEqual(Scan2DInterpolationMode.Bilinear, current.Snapshot.Resampling.Mode);
        Assert.IsTrue(delivery.AuditSnapshot.Entries.Any(entry =>
            entry.Kind == BScanVisualAuditEventKind.CandidateRejectedStale &&
            entry.Result == BScanVisualAuditResult.Faulted));
        current.Dispose();
        Assert.AreEqual(0, delivery.Diagnostics.ActiveRasterOwners);
        await delivery.DisposeAsync();
        await ingress.DisposeAsync();
    }

    [TestMethod]
    public async Task TwoConsumersReserveConcurrentRoundedRentalsWithinIndependentShares()
    {
        var options = new BScanOptions(1_000_000, 16 * 1024 * 1024, 4, 2, 2,
            maximumRasterConsumers: 2, maximumRasterOwnersPerConsumer: 2);
        var ingress = new BScanIngress(options);
        ConventionalUtFrameMetadata metadata = Metadata(4);
        var indexed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ingress.AfterIndexCommit = _ => indexed.TrySetResult();
        ingress.OpenRun(metadata);
        var pool = new BlockingRoundedBytePool(1024 * 1024);
        var delivery = new BScanVisualDelivery(ingress, null, pool);
        Assert.IsTrue(delivery.RegisterConsumer("first"));
        Assert.IsTrue(delivery.RegisterConsumer("second"));
        ingress.Accept(metadata, 0, 0, TimeSpan.Zero, null, [1, 2, 3, 4]);
        await indexed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        BScanRenderRequest request = Request(metadata.RunId, new(0, 3), 1024, 1024);
        using var start = new Barrier(3);
        Task<BScanRasterResult> first = Task.Run(async () =>
        {
            start.SignalAndWait();
            return await delivery.GetRegionAsync(request with { ConsumerId = "first" });
        });
        Task<BScanRasterResult> second = Task.Run(async () =>
        {
            start.SignalAndWait();
            return await delivery.GetRegionAsync(request with { ConsumerId = "second" });
        });
        start.SignalAndWait();
        try
        {
            await pool.FirstRentalEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.IsTrue(await Task.Run(() => SpinWait.SpinUntil(
                () => pool.RentalCount == 2, TimeSpan.FromSeconds(10))),
                "Both consumers did not reach their independently admitted rentals.");
            Assert.AreEqual(8L * 1024 * 1024, pool.OutstandingBytes);
            Assert.IsLessThanOrEqualTo(16L * 1024 * 1024, pool.MaximumOutstandingBytes);
        }
        finally { pool.ReleaseRentals(); }
        BScanRasterResult[] results = await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.IsTrue(results.All(result => result.Kind == BScanRasterResultKind.Available));
        foreach (BScanRasterResult result in results) { result.Region!.Dispose(); }
        Assert.AreEqual(0L, pool.OutstandingBytes);
        Assert.AreEqual(0L, delivery.Diagnostics.VisualBytesRented);
        await delivery.DisposeAsync();
        await ingress.DisposeAsync();
    }

    [TestMethod]
    public async Task ConcurrentRoundedRentalsReserveBeforeRentAndNeverExceedBudget()
    {
        var options = new BScanOptions(1_000_000, 16 * 1024 * 1024, 4, 2, 2,
            maximumRasterConsumers: 1, maximumRasterOwnersPerConsumer: 2);
        var ingress = new BScanIngress(options);
        ConventionalUtFrameMetadata metadata = Metadata(4);
        var indexed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ingress.AfterIndexCommit = _ => indexed.TrySetResult();
        ingress.OpenRun(metadata);
        var pool = new BlockingRoundedBytePool(6 * 1024 * 1024);
        await using var delivery = new BScanVisualDelivery(ingress, null, pool);
        Assert.IsTrue(delivery.RegisterConsumer("first"));
        ingress.Accept(metadata, 0, 0, TimeSpan.Zero, null, [1, 2, 3, 4]);
        await indexed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        BScanRenderRequest request = Request(metadata.RunId, new(0, 3), 1024, 1024);

        Task<BScanRasterResult> first = Task.Run(async () => await delivery.GetRegionAsync(request with
        { ConsumerId = "first" }));
        await pool.FirstRentalEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        try
        {
            BScanRasterResult second = await delivery.GetRegionAsync(request with
            { ConsumerId = "first" });
            Assert.AreEqual(BScanRasterResultKind.ResourceLimit, second.Kind);
            Assert.AreEqual(1, pool.RentalCount);
            Assert.AreEqual(9L * 1024 * 1024, pool.OutstandingBytes);
            Assert.AreEqual(9L * 1024 * 1024, delivery.Diagnostics.VisualBytesRented);
            Assert.IsLessThanOrEqualTo(16L * 1024 * 1024, pool.MaximumOutstandingBytes);
            Assert.IsLessThanOrEqualTo(16L * 1024 * 1024, delivery.Diagnostics.MaximumVisualBytesRented);
        }
        finally { pool.ReleaseRentals(); }
        BScanRasterResult result = await first.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.AreEqual(BScanRasterResultKind.Available, result.Kind);
        result.Region!.Dispose();
        Assert.AreEqual(0L, pool.OutstandingBytes);
        Assert.AreEqual(0L, delivery.Diagnostics.VisualBytesRented);
        await delivery.DisposeAsync();
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

    private sealed class BlockingRoundedBytePool(int roundingBytes) : IByteOwnerPool
    {
        private readonly ManualResetEventSlim _release = new();
        private long _outstanding;
        private long _maximum;
        private int _rentals;
        internal TaskCompletionSource FirstRentalEntered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int RentalCount => Volatile.Read(ref _rentals);
        internal long OutstandingBytes => Interlocked.Read(ref _outstanding);
        internal long MaximumOutstandingBytes => Interlocked.Read(ref _maximum);
        internal void ReleaseRentals() => _release.Set();
        public int GetMaximumRentalLength(int minimumLength) =>
            checked(minimumLength + roundingBytes);
        public IMemoryOwner<byte> Rent(int minimumLength)
        {
            int length = checked(minimumLength + roundingBytes);
            long outstanding = Interlocked.Add(ref _outstanding, length);
            long observed;
            while ((observed = Interlocked.Read(ref _maximum)) < outstanding &&
                Interlocked.CompareExchange(ref _maximum, outstanding, observed) != observed) { }
            if (Interlocked.Increment(ref _rentals) == 1) { FirstRentalEntered.TrySetResult(); }
            if (!_release.Wait(TimeSpan.FromSeconds(10)))
            { Interlocked.Add(ref _outstanding, -length); throw new TimeoutException("Rental barrier was not released."); }
            return new RoundedOwner(new byte[length], () => Interlocked.Add(ref _outstanding, -length));
        }
        private sealed class RoundedOwner(byte[] bytes, Action release) : IMemoryOwner<byte>
        {
            private byte[]? _bytes = bytes;
            public Memory<byte> Memory => _bytes ?? throw new ObjectDisposedException(nameof(RoundedOwner));
            public void Dispose() { if (Interlocked.Exchange(ref _bytes, null) is not null) { release(); } }
        }
    }
}
