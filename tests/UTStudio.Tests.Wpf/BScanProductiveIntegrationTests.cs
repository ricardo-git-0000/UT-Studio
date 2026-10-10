using System.Windows.Media;
using System.Windows.Media.Imaging;
using UTStudio.App.Wpf;
using UTStudio.App.Wpf.Services;
using UTStudio.Contracts.Inspection;
using UTStudio.Domain.Acquisition;
using UTStudio.Domain.Inspection;
using UTStudio.Presentation;
using UTStudio.Visualization.Core;
using UTStudio.Visualization.Core.Scan2D;

namespace UTStudio.Tests.Wpf;

[TestClass]
public sealed class BScanProductiveIntegrationTests
{
    [TestMethod]
    public Task Resampling_selector_tracks_programmatic_user_and_reopen_changes_once() => StaTest.Run(async () =>
    {
        await using var fixture = await ProductiveBScanFixture.CreateAsync(4);
        await fixture.WaitForRevisionAsync(4);
        long beforeInitialWindow = fixture.Delivery.RenderRequestCount;
        var first = new BScanWindow(fixture.ViewModel, () => { }, () => Task.CompletedTask);
        Assert.AreEqual(beforeInitialWindow, fixture.Delivery.RenderRequestCount,
            "Constructing the selector must not echo the initial ViewModel value.");
        first.Show();
        try
        {
            await first.Dispatcher.InvokeAsync(() => { },
                System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            long afterInitialSettled = fixture.Delivery.RenderRequestCount;
            await first.Dispatcher.InvokeAsync(() => { },
                System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            Assert.AreEqual(afterInitialSettled, fixture.Delivery.RenderRequestCount,
                "Initial ViewModel-to-selector synchronization must not echo a second render request.");
            long beforeProgrammatic = fixture.Delivery.RenderRequestCount;
            long intentions = first.ResamplingIntentCount;
            fixture.ViewModel.SetResamplingMode(Scan2DInterpolationMode.Lanczos);
            await first.Dispatcher.InvokeAsync(() => { },
                System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            Assert.AreEqual(Scan2DInterpolationMode.Lanczos, first.SelectedResamplingMode);
            Assert.IsTrue(first.IsLanczosWarningVisible);
            Assert.AreEqual(intentions, first.ResamplingIntentCount);
            Assert.AreEqual(beforeProgrammatic + 1, fixture.Delivery.RenderRequestCount);

            long beforeUser = fixture.Delivery.RenderRequestCount;
            first.SelectResamplingMode(Scan2DInterpolationMode.Bicubic);
            await first.Dispatcher.InvokeAsync(() => { },
                System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            Assert.AreEqual(Scan2DInterpolationMode.Bicubic,
                fixture.ViewModel.State.RequestedResamplingMode);
            Assert.IsFalse(first.IsLanczosWarningVisible);
            Assert.AreEqual(intentions + 1, first.ResamplingIntentCount);
            Assert.AreEqual(beforeUser + 1, fixture.Delivery.RenderRequestCount);
            long afterUser = fixture.Delivery.RenderRequestCount;

            first.SelectResamplingMode(Scan2DInterpolationMode.Bicubic);
            await first.Dispatcher.InvokeAsync(() => { },
                System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            Assert.AreEqual(afterUser, fixture.Delivery.RenderRequestCount);
            Assert.AreEqual(intentions + 1, first.ResamplingIntentCount);

            long beforeRapid = fixture.Delivery.RenderRequestCount;
            using ProductiveBScanFixture.PublicationProbe rapidPublications =
                fixture.ObservePublications(fixture.Metadata.RunId);
            await rapidPublications.WaitForFirstAsync();
            int publicationsBeforeRapid = rapidPublications.Count;
            using var firstCandidateEntered = new ManualResetEventSlim();
            using var releaseFirstCandidate = new ManualResetEventSlim();
            int blockedCandidate = 0;
            fixture.Delivery.BeforeRasterCandidateCommit = () =>
            {
                if (Interlocked.CompareExchange(ref blockedCandidate, 1, 0) != 0) { return; }
                firstCandidateEntered.Set();
                releaseFirstCandidate.Wait();
            };
            first.SelectResamplingMode(Scan2DInterpolationMode.None);
            Assert.IsTrue(await Task.Run(() => firstCandidateEntered.Wait(TimeSpan.FromSeconds(5))));
            first.SelectResamplingMode(Scan2DInterpolationMode.Bilinear);
            first.SelectResamplingMode(Scan2DInterpolationMode.Lanczos);
            releaseFirstCandidate.Set();
            await first.Dispatcher.InvokeAsync(() => { },
                System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            Assert.AreEqual(intentions + 4, first.ResamplingIntentCount);
            Assert.AreEqual(beforeRapid + 3, fixture.Delivery.RenderRequestCount);
            Assert.AreEqual(Scan2DInterpolationMode.Lanczos,
                fixture.ViewModel.State.RequestedResamplingMode);
            Assert.IsTrue(first.IsLanczosWarningVisible);

            await fixture.Clock.NextTimerAsync();
            fixture.Clock.Advance(BScanVisualDelivery.MinimumPublicationInterval);
            await fixture.WaitForPresentedModeAsync(Scan2DInterpolationMode.Lanczos);
            fixture.Delivery.BeforeRasterCandidateCommit = null;
            CollectionAssert.AreEqual(
                new[] { Scan2DInterpolationMode.Lanczos },
                rapidPublications.PublishedModes.Skip(publicationsBeforeRapid).ToArray(),
                "Only the last rapid user intention may be published.");

            Guid nextRun = Guid.Parse("99000000-0000-0000-0000-000000000101");
            var nextRunId = new AcquisitionRunId(nextRun);
            await fixture.StartNewRunAsync(nextRun);
            await fixture.AddIndexedAsync(1000);
            await fixture.WaitForBootstrapRequestAsync();
            await fixture.Clock.NextTimerAsync();
            fixture.Clock.Advance(BScanVisualDelivery.MinimumPublicationInterval);
            await fixture.WaitForMetadataCandidateAsync();
            fixture.ReleaseMetadataCandidate();
            await fixture.WaitForRunAsync(nextRunId, 1);
            await first.Dispatcher.InvokeAsync(() => { },
                System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            Assert.AreEqual(Scan2DInterpolationMode.Lanczos,
                fixture.ViewModel.State.RequestedResamplingMode);
            Assert.AreEqual(Scan2DInterpolationMode.Lanczos, first.SelectedResamplingMode);
            Assert.IsTrue(first.IsLanczosWarningVisible);
        }
        finally { await first.CloseOwnedAsync(); }

        long closedIntentions = first.ResamplingIntentCount;
        fixture.ViewModel.SetResamplingMode(Scan2DInterpolationMode.Bicubic);
        await first.Dispatcher.InvokeAsync(() => { },
            System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        Assert.AreEqual(Scan2DInterpolationMode.Lanczos, first.SelectedResamplingMode,
            "A closed window must be detached from ViewModel changes.");
        Assert.IsTrue(first.IsLanczosWarningVisible);
        Assert.AreEqual(closedIntentions, first.ResamplingIntentCount);

        long beforeReopenedWindow = fixture.Delivery.RenderRequestCount;
        var reopened = new BScanWindow(fixture.ViewModel, () => { }, () => Task.CompletedTask);
        Assert.AreEqual(beforeReopenedWindow, fixture.Delivery.RenderRequestCount,
            "Reconstructing the selector must not echo the current ViewModel value.");
        reopened.Show();
        try
        {
            await reopened.Dispatcher.InvokeAsync(() => { },
                System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            Assert.AreEqual(Scan2DInterpolationMode.Bicubic, reopened.SelectedResamplingMode);
            Assert.IsFalse(reopened.IsLanczosWarningVisible);
            Assert.AreEqual(0L, reopened.ResamplingIntentCount);
            long afterReopenSettled = fixture.Delivery.RenderRequestCount;
            await reopened.Dispatcher.InvokeAsync(() => { },
                System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            Assert.AreEqual(afterReopenSettled, fixture.Delivery.RenderRequestCount,
                "Reopening must reflect the current preference without a selector echo.");
        }
        finally { await reopened.CloseOwnedAsync(); }
    });

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
        await fixture.WaitForBootstrapDrainAsync();
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
        fixture.Delivery.EnableAudit(128);
        BScanRenderRequest lateRunA = fixture.Request();
        using ProductiveBScanFixture.PublicationProbe priorRunPublications =
            fixture.ObservePublications(fixture.Metadata.RunId);
        await priorRunPublications.WaitForFirstAsync();
        Assert.AreEqual(1, priorRunPublications.Count);
        Guid nextRun = Guid.Parse("99000000-0000-0000-0000-000000000100");
        var nextRunId = new AcquisitionRunId(nextRun);
        using ProductiveBScanFixture.PublicationProbe publications =
            fixture.ObservePublications(nextRunId);

        await fixture.StartNewRunAsync(nextRun);
        await fixture.AddIndexedAsync(1000);
        await fixture.WaitForBootstrapRequestAsync();
        BScanRenderRequest bootstrapB = fixture.Delivery.GetLastRequest(fixture.ViewModel.ConsumerId)!;
        Assert.AreEqual(nextRunId, bootstrapB.RunId);
        long requestsAfterBootstrapB = fixture.Delivery.RenderRequestCount;
        BScanVisualAuditEntry admittedB = fixture.Delivery.AuditSnapshot.Entries.Last(entry =>
            entry.Kind == BScanVisualAuditEventKind.BuildAdmitted && entry.RunId == nextRunId);

        fixture.Delivery.RequestRender(lateRunA);

        Assert.AreEqual(requestsAfterBootstrapB, fixture.Delivery.RenderRequestCount);
        Assert.AreEqual(bootstrapB,
            fixture.Delivery.GetLastRequest(fixture.ViewModel.ConsumerId));
        Assert.AreEqual(1, fixture.Delivery.PendingBootstrapCount);
        Assert.IsFalse(fixture.Delivery.AuditSnapshot.Entries.Any(entry =>
            entry.Kind == BScanVisualAuditEventKind.BuildAdmitted &&
            entry.RunId == lateRunA.RunId &&
            entry.Sequence > admittedB.Sequence));
        await fixture.Clock.NextTimerAsync();
        fixture.Clock.Advance(BScanVisualDelivery.MinimumPublicationInterval);
        await fixture.WaitForMetadataCandidateAsync();
        fixture.ReleaseMetadataCandidate();
        try { await publications.WaitForFirstAsync(); }
        catch (TimeoutException)
        {
            Assert.Fail($"New-run publication was not committed; " +
                $"lastError={fixture.Delivery.Diagnostics.LastVisualError}; " +
                $"pendingBootstrap={fixture.Delivery.PendingBootstrapCount}; " +
                $"audit={fixture.Delivery.LastMetadataCandidateAudit}; " +
                $"lastRequest={fixture.Delivery.GetLastRequest(fixture.ViewModel.ConsumerId)}; " +
                $"published={fixture.Delivery.Diagnostics.VisualUpdatesPublished}.");
        }
        await fixture.WaitForRunAsync(nextRunId, 1);
        await System.Windows.Threading.Dispatcher.Yield(
            System.Windows.Threading.DispatcherPriority.ApplicationIdle);

        Assert.AreEqual(nextRunId, fixture.Control.AppliedSnapshot!.RunId);
        Assert.IsTrue(fixture.Control.AppliedSnapshot.RequiresFullRefresh);
        Assert.AreEqual(1, priorRunPublications.Count);
        Assert.AreEqual(1, publications.Count);
        Assert.IsTrue(fixture.Delivery.AuditSnapshot.Entries.Any(entry =>
            entry.Kind == BScanVisualAuditEventKind.CandidateCommitted &&
            entry.RunId == nextRunId &&
            entry.RequestGeneration == admittedB.RequestGeneration));
        Assert.AreEqual(0, fixture.Delivery.PendingBootstrapCount);
        Assert.AreEqual(1, fixture.Delivery.RetainedSnapshotCount);
        fixture.AssertOpenBalances();
    });

    [TestMethod]
    public Task Frozen_viewport_ignores_commits_outside_its_represented_range() => StaTest.Run(async () =>
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
            await Task.Delay(50);
            Assert.AreSame(bitmap, fixture.Control.RasterSource);
            Assert.AreEqual(128UL, fixture.Control.AppliedSnapshot!.StoreRevision);
            Assert.AreEqual(128UL, fixture.ViewModel.State.DisplayedRevision?.StoreRevision);
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
        var identity = new TraceIdentity(new UtStreamIdentity(
            new InspectionId(Guid.Parse("98000000-0000-0000-0000-000000000098")),
            fixture.Metadata.RunId, fixture.Metadata.SourceId,
            fixture.Metadata.Configuration.PhysicalChannelId), 400);

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
        fixture.Ingress.FreezeRun(fixture.Metadata.RunId);

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
    public Task Frozen_productive_run_can_rebuild_with_a_new_resampling_mode() => StaTest.Run(async () =>
    {
        await using var fixture = await ProductiveBScanFixture.CreateAsync(128);
        await fixture.WaitForRevisionAsync(128);
        fixture.Ingress.CloseRun(fixture.Metadata.RunId);
        await fixture.Ingress.WaitForCloseAsync(fixture.Metadata.RunId);
        fixture.Ingress.FreezeRun(fixture.Metadata.RunId);
        BScanPresentedRegionToken previous = fixture.ViewModel.State.Region!.Token;

        fixture.ViewModel.ChangeResamplingModeCommand.Execute(Scan2DInterpolationMode.Bicubic);
        await fixture.WaitForTokenChangeAsync(previous);

        Assert.AreEqual(Scan2DInterpolationMode.Bicubic, fixture.ViewModel.State.RequestedResamplingMode);
        Assert.AreEqual(Scan2DInterpolationMode.Bicubic, fixture.ViewModel.State.PresentedResamplingMode);
        Assert.AreEqual(Scan2DInterpolationMode.Bicubic,
            fixture.Control.AppliedSnapshot!.Resampling.Mode);
        Assert.IsFalse(fixture.ViewModel.State.IsRenderPending);
        fixture.AssertOpenBalances();
    });

    [TestMethod]
    public Task Reduced_live_window_can_rebuild_with_a_new_resampling_mode() => StaTest.Run(async () =>
    {
        await using var fixture = await ProductiveBScanFixture.CreateAsync(512);
        await fixture.WaitForRevisionAsync(512);
        BScanPresentedRegionToken fullWindow = fixture.ViewModel.State.Region!.Token;

        fixture.ViewModel.DecreaseLiveWindowCommand.Execute(null);
        await fixture.WaitForTokenChangeAsync(fullWindow);
        Assert.AreEqual(256, fixture.ViewModel.LiveWindowColumns);
        BScanPresentedRegionToken reducedWindow = fixture.ViewModel.State.Region!.Token;

        fixture.ViewModel.ChangeResamplingModeCommand.Execute(Scan2DInterpolationMode.Bicubic);
        await fixture.WaitForTokenChangeAsync(reducedWindow);

        Assert.AreEqual(Scan2DInterpolationMode.Bicubic, fixture.ViewModel.State.RequestedResamplingMode);
        Assert.AreEqual(Scan2DInterpolationMode.Bicubic, fixture.ViewModel.State.PresentedResamplingMode);
        Assert.AreEqual(Scan2DInterpolationMode.Bicubic, fixture.Control.AppliedSnapshot!.Resampling.Mode);
        Assert.AreEqual(256m, fixture.ViewModel.State.Region.Snapshot.SpatialViewport.Span);
        Assert.IsFalse(fixture.ViewModel.State.IsRenderPending);
        fixture.AssertOpenBalances();
    });

    [TestMethod]
    public Task Reducing_live_window_preserves_the_selected_resampling_mode() => StaTest.Run(async () =>
    {
        await using var fixture = await ProductiveBScanFixture.CreateAsync(512);
        await fixture.WaitForRevisionAsync(512);
        BScanPresentedRegionToken none = fixture.ViewModel.State.Region!.Token;
        fixture.ViewModel.ChangeResamplingModeCommand.Execute(Scan2DInterpolationMode.Bicubic);
        await fixture.WaitForTokenChangeAsync(none);
        BScanPresentedRegionToken interpolated = fixture.ViewModel.State.Region!.Token;

        fixture.ViewModel.DecreaseLiveWindowCommand.Execute(null);
        await fixture.WaitForTokenChangeAsync(interpolated);

        Assert.AreEqual(256, fixture.ViewModel.LiveWindowColumns);
        Assert.AreEqual(Scan2DInterpolationMode.Bicubic, fixture.ViewModel.State.RequestedResamplingMode);
        Assert.AreEqual(Scan2DInterpolationMode.Bicubic, fixture.ViewModel.State.PresentedResamplingMode);
        Assert.AreEqual(Scan2DInterpolationMode.Bicubic, fixture.Control.AppliedSnapshot!.Resampling.Mode);
        Assert.AreEqual(256m, fixture.Control.AppliedSnapshot.SpatialViewport.Span);
        fixture.AssertOpenBalances();
    });

    [TestMethod]
    public Task Rapid_reduce_and_mode_change_publish_only_the_latest_signature() => StaTest.Run(async () =>
    {
        await using var fixture = await ProductiveBScanFixture.CreateAsync(512);
        await fixture.WaitForRevisionAsync(512);
        BScanPresentedRegionToken previous = fixture.ViewModel.State.Region!.Token;

        fixture.ViewModel.DecreaseLiveWindowCommand.Execute(null);
        fixture.ViewModel.ChangeResamplingModeCommand.Execute(Scan2DInterpolationMode.Bicubic);
        await DiagnosticWait.Until(() => fixture.ViewModel.State is
        {
            RequestedResamplingMode: Scan2DInterpolationMode.Bicubic,
            PresentedResamplingMode: Scan2DInterpolationMode.Bicubic,
            IsRenderPending: false,
            Region.Token: var token
        } && token != previous && fixture.Control.AppliedSnapshot is
        {
            RasterWidth: 128,
            Resampling.Mode: Scan2DInterpolationMode.Bicubic
        }, "latest reduced Bicubic signature");

        Assert.AreEqual(128, fixture.ViewModel.State.ConfirmedRenderSignature!.Value.PixelWidth);
        Assert.AreEqual(Scan2DInterpolationMode.Bicubic,
            fixture.ViewModel.State.ConfirmedRenderSignature.Value.Resampling.Mode);
        fixture.AssertOpenBalances();
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
