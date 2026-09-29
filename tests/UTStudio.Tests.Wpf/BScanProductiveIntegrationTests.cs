using System.Windows.Media;
using System.Windows.Media.Imaging;
using UTStudio.App.Wpf;
using UTStudio.App.Wpf.Services;
using UTStudio.Domain.Acquisition;
using UTStudio.Presentation;
using UTStudio.Visualization.Core;

namespace UTStudio.Tests.Wpf;

[TestClass]
public sealed class BScanProductiveIntegrationTests
{
    [TestMethod]
    public Task Active_store_survives_window_close_and_reopens_with_balanced_consumers() => StaTest.Run(async () =>
    {
        await using var fixture = await ProductiveBScanFixture.CreateAsync(4);
        var manager = new BScanWindowManager(fixture.Delivery, fixture.SharedViewport, fixture.Selection,
            fixture.Dispatcher, System.Windows.Threading.Dispatcher.CurrentDispatcher);
        await manager.OpenAsync();
        BScanWindow first = manager.ActiveWindow!;
        string firstConsumer = first.ConsumerId;
        Assert.AreEqual(2, fixture.Delivery.ActiveConsumerCount);

        await first.CloseOwnedAsync();
        Assert.IsNull(manager.ActiveWindow);
        Assert.AreEqual(1, fixture.Delivery.ActiveConsumerCount);
        await fixture.AddIndexedAsync(4);

        await manager.OpenAsync();
        BScanWindow second = manager.ActiveWindow!;
        Assert.AreNotEqual(firstConsumer, second.ConsumerId);
        Assert.AreEqual(2, fixture.Delivery.ActiveConsumerCount);
        await manager.BlockAndCloseAsync();
        Assert.AreEqual(1, fixture.Delivery.ActiveConsumerCount);
        fixture.AssertOpenBalances();
    });

    [TestMethod]
    public Task Real_delivery_bootstraps_a_new_consumer_from_an_active_store() => StaTest.Run(async () =>
    {
        await using var fixture = await ProductiveBScanFixture.CreateAsync();
        await fixture.AddIndexedAsync(0);
        await fixture.WaitForRevisionAsync(1);

        Assert.IsNotNull(fixture.Control.RasterSource);
        Assert.IsTrue(fixture.Control.AppliedSnapshot!.RequiresFullRefresh);
        Assert.AreEqual(1UL, fixture.Control.AppliedSnapshot.StoreRevision);
        fixture.AssertOpenBalances();
    });

    [TestMethod]
    public Task Bootstrap_before_metadata_and_retirement_remove_pending_consumer_state() => StaTest.Run(async () =>
    {
        await using var fixture = await ProductiveBScanFixture.CreateAsync();
        Assert.AreEqual(1, fixture.Delivery.PendingBootstrapCount);
        Assert.IsTrue(fixture.Delivery.RegisterConsumer("retired-before-bootstrap"));
        fixture.Delivery.RequestBootstrap("retired-before-bootstrap");
        Assert.AreEqual(2, fixture.Delivery.PendingBootstrapCount);

        fixture.Delivery.UnregisterConsumer("retired-before-bootstrap");

        Assert.AreEqual(1, fixture.Delivery.ActiveConsumerCount);
        Assert.AreEqual(1, fixture.Delivery.PendingBootstrapCount);
        Assert.AreEqual(0, fixture.Delivery.RetainedSnapshotCount);
    });

    [TestMethod]
    public Task Repeated_bootstrap_for_second_consumer_coalesces_to_one_request() => StaTest.Run(async () =>
    {
        await using var fixture = await ProductiveBScanFixture.CreateAsync(4);
        await fixture.WaitForRevisionAsync(4);
        Assert.IsTrue(fixture.Delivery.RegisterConsumer("second"));
        long before = fixture.Delivery.RenderRequestCount;

        fixture.Delivery.RequestBootstrap("second");
        fixture.Delivery.RequestBootstrap("second");
        fixture.Delivery.RequestBootstrap("second");

        Assert.AreEqual(before + 1, fixture.Delivery.RenderRequestCount);
        fixture.Delivery.UnregisterConsumer("second");
        Assert.AreEqual(1, fixture.Delivery.ActiveConsumerCount);
        Assert.AreEqual(0, fixture.Delivery.PendingBootstrapCount);
    });

    [TestMethod]
    public Task Bootstrap_rebinds_the_existing_consumer_to_a_new_run_without_duplicate_publication() => StaTest.Run(async () =>
    {
        await using var fixture = await ProductiveBScanFixture.CreateAsync(4);
        await fixture.WaitForRevisionAsync(4);
        ulong publicationsBefore = fixture.Delivery.Diagnostics.VisualUpdatesPublished;
        Guid nextRun = Guid.Parse("99000000-0000-0000-0000-000000000100");

        await fixture.StartNewRunAsync(nextRun);
        await fixture.AddIndexedAsync(1000);
        await fixture.Clock.NextTimerAsync();
        fixture.Clock.Advance(BScanVisualDelivery.MinimumPublicationInterval);
        await fixture.WaitForRunAsync(new(nextRun), 1);

        Assert.AreEqual(new AcquisitionRunId(nextRun), fixture.Control.AppliedSnapshot!.RunId);
        Assert.IsTrue(fixture.Control.AppliedSnapshot.RequiresFullRefresh);
        Assert.AreEqual(publicationsBefore + 1, fixture.Delivery.Diagnostics.VisualUpdatesPublished);
        Assert.AreEqual(0, fixture.Delivery.PendingBootstrapCount);
        Assert.AreEqual(1, fixture.Delivery.RetainedSnapshotCount);
        fixture.AssertOpenBalances();
    });

    [TestMethod]
    public Task Real_delivery_viewmodel_adapter_control_commits_full_and_incrementals() => StaTest.Run(async () =>
    {
        await using var fixture = await ProductiveBScanFixture.CreateAsync(128);
        await fixture.WaitForRevisionAsync(128);
        BScanPresentedRegionToken autoFit = fixture.ViewModel.State.Region!.Token;
        fixture.ViewModel.ZoomSpatialAt(.5m, 1.2m);
        await fixture.WaitForTokenChangeAsync(autoFit);
        var bitmap = (WriteableBitmap)fixture.Control.RasterSource!;
        ulong expectedRevision = 128;

        foreach (int accumulated in new[] { 1, 8, 33, 100 })
        {
            for (int column = 0; column < accumulated; column++)
            { await fixture.AddIndexedAsync(expectedRevision + (ulong)column); }
            expectedRevision += (ulong)accumulated;
            fixture.Delivery.RequestRender(fixture.Request());
            fixture.Clock.Advance(BScanVisualDelivery.MinimumPublicationInterval);
            await fixture.WaitForRevisionAsync(expectedRevision);
            Assert.AreSame(bitmap, fixture.Control.RasterSource);
            Assert.AreEqual(expectedRevision, fixture.Control.AppliedSnapshot!.StoreRevision);
            Assert.IsFalse(fixture.Control.AppliedSnapshot.RequiresFullRefresh);
            fixture.AssertOpenBalances();
        }
    });

    [TestMethod]
    public Task Fixed_live_window_keeps_publishing_without_resize() => StaTest.Run(async () =>
    {
        await using var fixture = await ProductiveBScanFixture.CreateAsync();
        for (ulong column = 0; column < 12; column++)
        {
            try { await fixture.AddAndPublishAsync(column); }
            catch (TimeoutException error)
            {
                throw new AssertFailedException($"Column {column}; state={fixture.ViewModel.State.StoreRevision}; " +
                    $"displayed={fixture.ViewModel.State.DisplayedRevision?.StoreRevision}; " +
                    $"builds={fixture.Delivery.Diagnostics.RasterRegionsBuilt}; " +
                    $"full={fixture.Delivery.Diagnostics.FullRefreshesBuilt}; " +
                    $"incremental={fixture.Delivery.Diagnostics.IncrementalUpdatesBuilt}; " +
                    $"stale={fixture.Delivery.Diagnostics.VisualBuildsSuperseded}; " +
                    $"region={fixture.ViewModel.State.Region?.Snapshot.StoreRevision}; " +
                    $"control={fixture.Control.AppliedSnapshot?.StoreRevision}; " +
                    $"visualError={fixture.ViewModel.State.BScanVisualError}; " +
                    $"uiError={fixture.ViewModel.State.UiInteractionError}", error);
            }
            var bitmap = (WriteableBitmap)fixture.Control.RasterSource!;
            Assert.AreEqual(128, bitmap.PixelWidth);
            Assert.AreEqual(16, bitmap.PixelHeight);
            Assert.AreEqual(column + 1, fixture.Control.AppliedSnapshot!.StoreRevision);
            Assert.AreEqual(512m, fixture.ViewModel.State.SpatialViewport!.Value.Span);
        }

        Assert.IsGreaterThan(1UL, fixture.Delivery.Diagnostics.IncrementalUpdatesBuilt);
        fixture.AssertOpenBalances();
    });

    [TestMethod]
    public Task Full_live_ring_scrolls_one_column_without_rebuilding_the_complete_raster() => StaTest.Run(async () =>
    {
        await using var fixture = await ProductiveBScanFixture.CreateAsync(512);
        await fixture.WaitForRevisionAsync(512);
        await DiagnosticWait.Until(() => fixture.Control.AppliedSnapshot is
        { RasterWidth: 512, RenderConfigurationVersion: 1 },
            "full live ring has applied its configured raster surface");
        var bitmap = (WriteableBitmap)fixture.Control.RasterSource!;
        Assert.AreEqual(512, bitmap.PixelWidth);
        BScanDisplayedVisualIdentity acknowledged = fixture.Delivery.GetAcknowledgedDisplay(
            fixture.ViewModel.ConsumerId)!;
        Assert.AreEqual(512, acknowledged.Render.PixelWidth);
        Assert.AreEqual(fixture.Control.AppliedSnapshot!.RenderConfigurationVersion,
            acknowledged.Render.RenderConfigurationVersion);
        ulong fullBefore = fixture.Delivery.Diagnostics.FullRefreshesBuilt;

        await fixture.AddAndPublishAsync(512);

        Assert.AreSame(bitmap, fixture.Control.RasterSource,
            $"full={fixture.Control.AppliedSnapshot?.RequiresFullRefresh}; " +
            $"reason={fixture.Control.AppliedSnapshot?.FullRefreshReason}; " +
            $"store={fixture.Control.AppliedSnapshot?.StoreRevision}; " +
            $"shift={fixture.Control.AppliedSnapshot?.HorizontalPixelShift}; " +
            $"affected={fixture.Control.AppliedSnapshot?.AffectedRegion}");
        Assert.AreEqual(513UL, fixture.Control.AppliedSnapshot!.StoreRevision);
        Assert.IsFalse(fixture.Control.AppliedSnapshot.RequiresFullRefresh);
        Assert.AreEqual(1, fixture.Control.AppliedSnapshot.HorizontalPixelShift);
        Assert.AreEqual(new BScanRasterRectangle(511, 0, 1, 16),
            fixture.Control.AppliedSnapshot.AffectedRegion);
        Assert.AreEqual(fullBefore, fixture.Delivery.Diagnostics.FullRefreshesBuilt);
        Assert.IsGreaterThan(0UL, fixture.Delivery.Diagnostics.IncrementalUpdatesBuilt);
        fixture.AssertOpenBalances();
    });

    [TestMethod]
    public Task Reducing_full_live_ring_keeps_incremental_scroll_and_ui_work_bounded() => StaTest.Run(async () =>
    {
        await using var fixture = await ProductiveBScanFixture.CreateAsync(512);
        await fixture.WaitForRevisionAsync(512);
        await DiagnosticWait.Until(() => fixture.Control.AppliedSnapshot is { RasterWidth: 512 },
            "initial live ring has applied its configured surface");

        fixture.ViewModel.DecreaseLiveWindowCommand.Execute(null);
        Assert.AreEqual(256m, fixture.ViewModel.State.SpatialViewport!.Value.Span);
        await DiagnosticWait.Until(() => fixture.ViewModel.LiveWindowColumns == 256 &&
            fixture.Control.AppliedSnapshot is { RasterWidth: 256, SpatialViewport.Span: 256m },
            "reduced live ring has applied one neutral pixel per trace");
        BScanDisplayedVisualIdentity reduced = fixture.Delivery.GetAcknowledgedDisplay(
            fixture.ViewModel.ConsumerId)!;
        Assert.AreEqual(256, reduced.Render.PixelWidth);
        Assert.AreEqual(256m, reduced.Render.SpatialViewport.Span);
        Assert.AreEqual(256UL, reduced.Render.SpatialViewport.OriginAcquisitionIndex);
        var bitmap = (WriteableBitmap)fixture.Control.RasterSource!;
        ulong fullBefore = fixture.Delivery.Diagnostics.FullRefreshesBuilt;

        await fixture.AddAndPublishAsync(512);

        Assert.AreSame(bitmap, fixture.Control.RasterSource,
            $"full={fixture.Control.AppliedSnapshot?.RequiresFullRefresh}; " +
            $"reason={fixture.Control.AppliedSnapshot?.FullRefreshReason}; " +
            $"width={fixture.Control.AppliedSnapshot?.RasterWidth}; " +
            $"shift={fixture.Control.AppliedSnapshot?.HorizontalPixelShift}; " +
            $"viewport={fixture.Control.AppliedSnapshot?.SpatialViewport}; " +
            $"stateViewport={fixture.ViewModel.State.SpatialViewport}; " +
            $"requiresFull={fixture.ViewModel.State.RequiresFullRefresh}");
        Assert.IsFalse(fixture.Control.AppliedSnapshot!.RequiresFullRefresh);
        Assert.AreEqual(1, fixture.Control.AppliedSnapshot.HorizontalPixelShift);
        Assert.AreEqual(new BScanRasterRectangle(255, 0, 1, 16),
            fixture.Control.AppliedSnapshot.AffectedRegion);
        Assert.AreEqual(fullBefore, fixture.Delivery.Diagnostics.FullRefreshesBuilt);
        fixture.AssertOpenBalances();
    });

    [TestMethod]
    public Task Productive_selection_uses_exact_identity_when_acquisition_index_differs_from_sequence() => StaTest.Run(async () =>
    {
        await using var fixture = await ProductiveBScanFixture.CreateAsync(4);
        await fixture.WaitForRevisionAsync(4);
        await fixture.AddIndexedAsync(400);
        var identity = new BScanTraceIdentity(fixture.Metadata.SourceId, fixture.Metadata.RunId,
            fixture.Metadata.Configuration.PhysicalChannelId, 4);

        await fixture.ViewModel.SelectColumnAsync(identity);

        Assert.AreEqual(identity, fixture.Selection.State.Identity);
        Assert.AreEqual(400UL, fixture.Selection.State.AcquisitionIndex);
        CollectionAssert.AreEqual(Enumerable.Range(0, 16).Select(value => (short)(value + 400)).ToArray(),
            fixture.Selection.State.Snapshot!.Points.Select(point =>
                (short)Math.Round(point.AmplitudePercent * 32768 / 100)).ToArray());
        Assert.AreEqual(BScanTraceResultKind.Available, fixture.Selection.State.LastResult);
        Assert.AreEqual(400UL, fixture.ViewModel.State.Selection.AcquisitionIndex);
        Assert.AreEqual(400UL, fixture.Control.SelectedAcquisitionIndex);
    });

    [TestMethod]
    public Task Frozen_productive_selection_updates_ascan_and_cursor_without_raster_refresh() => StaTest.Run(async () =>
    {
        await using var fixture = await ProductiveBScanFixture.CreateAsync(4);
        await fixture.WaitForRevisionAsync(4);
        fixture.Ingress.CloseRun(fixture.Metadata.RunId);
        await fixture.Ingress.WaitForCloseAsync(fixture.Metadata.RunId);

        await fixture.Adapter.SelectColumnAsync(2);

        Assert.AreEqual(2UL, fixture.Selection.State.AcquisitionIndex);
        Assert.IsNotNull(fixture.Selection.State.Snapshot);
        Assert.AreEqual(2UL, fixture.Control.SelectedAcquisitionIndex);
        Assert.IsTrue(fixture.Control.HasSelectionVisual);
        int width = Math.Max(1, (int)Math.Ceiling(fixture.Control.ActualWidth));
        int height = Math.Max(1, (int)Math.Ceiling(fixture.Control.ActualHeight));
        var composed = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        composed.Render(fixture.Control);
        int stride = width * 4;
        var pixels = new byte[stride * height];
        composed.CopyPixels(pixels, stride, 0);
        int cursorPixels = 0;
        for (int offset = 0; offset < pixels.Length; offset += 4)
        {
            if (pixels[offset] == 87 && pixels[offset + 1] == 205 &&
                pixels[offset + 2] == 255 && pixels[offset + 3] == 255)
            { cursorPixels++; }
        }
        Assert.IsGreaterThan(10, cursorPixels);
    });

    [TestMethod]
    public Task Stale_token_is_rejected_coalesces_recovery_and_preserves_visible_bitmap() => StaTest.Run(async () =>
    {
        await using var fixture = await ProductiveBScanFixture.CreateAsync(128);
        await fixture.WaitForRevisionAsync(128);
        BScanPresentedRegionToken stale = fixture.ViewModel.State.Region!.Token;
        await fixture.AddAndPublishAsync(128);
        var bitmap = (WriteableBitmap)fixture.Control.RasterSource!;
        byte[] pixels = new byte[bitmap.BackBufferStride * bitmap.PixelHeight];
        bitmap.CopyPixels(pixels, bitmap.BackBufferStride, 0);
        BScanVisualSnapshot identity = fixture.Control.AppliedSnapshot!;
        long fullBefore = fixture.Delivery.FullIdentityRequestCount;

        Assert.IsFalse(fixture.ViewModel.TryCopyPresentedRegion(stale, new byte[128 * 16], 128));
        Assert.IsFalse(fixture.ViewModel.TryCopyPresentedRegion(stale, new byte[128 * 16], 128));
        Assert.IsFalse(fixture.ViewModel.TryCopyPresentedRegion(stale, new byte[128 * 16], 128));
        Assert.AreSame(bitmap, fixture.Control.RasterSource);
        Assert.AreEqual(identity, fixture.Control.AppliedSnapshot);
        byte[] after = new byte[pixels.Length];
        bitmap.CopyPixels(after, bitmap.BackBufferStride, 0);
        CollectionAssert.AreEqual(pixels, after);
        BScanPresentedRegionToken beforeRecovery = fixture.ViewModel.State.Region!.Token;
        await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        await fixture.WaitForTokenChangeAsync(beforeRecovery);
        Assert.AreEqual(fullBefore + 1, fixture.Delivery.FullIdentityRequestCount);
        Assert.IsTrue(fixture.Control.AppliedSnapshot!.RequiresFullRefresh);
        Assert.AreEqual(0, fixture.Control.ActivePooledRasterBytes);
    });

}
