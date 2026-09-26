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
