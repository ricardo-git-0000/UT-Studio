using System.Buffers;
using UTStudio.Domain.Acquisition;
using UTStudio.Presentation;
using UTStudio.Visualization.Core;
using UTStudio.Visualization.Core.Scan2D;

namespace UTStudio.Core.Tests.Presentation;

[TestClass]
public sealed class BScanPresentationTests
{
    [TestMethod]
    public async Task Synchronous_raster_source_runs_off_dispatcher_and_does_not_block_ui()
    {
        var ui = new ManualUiDispatcher();
        var shared = new SharedScanTimeViewport(ui);
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var source = new ManualBScanSource
        {
            IsOnUiThread = ui.CheckAccess
        };
        var coordinator = new BScanSelectionCoordinator(ui, new PendingTraceProvider(), new AScanProjector());
        var vm = new BScanViewModel(source, shared, coordinator, ui);
        AcquisitionRunId run = new(Guid.NewGuid());

        source.Emit(Snapshot(run, 1, 0, 10));
        await ui.DriveUntilAsync(() => source.GetRegionCalls == 1 && !vm.State.IsRenderPending);
        source.BeforeGetRegion = () =>
        {
            entered.Set();
            Assert.IsTrue(release.Wait(TimeSpan.FromSeconds(5)), "Diagnostic raster barrier timed out.");
        };
        vm.ChangeResamplingModeCommand.Execute(Scan2DInterpolationMode.Lanczos);
        Assert.IsTrue(ui.RunNext(), "The UI dispatcher must remain able to process the mode change.");
        Assert.AreEqual(Scan2DInterpolationMode.Lanczos, vm.RequestedResamplingMode);
        Assert.IsTrue(entered.Wait(TimeSpan.FromSeconds(5)), "The Lanczos request did not reach the raster source.");
        Assert.IsFalse(source.GetRegionRanOnUiThread);
        release.Set();

        await Drive(ui, vm.DisposeAsync().AsTask());
        await Drive(ui, coordinator.DisposeAsync().AsTask());
        await Drive(ui, shared.DisposeAsync().AsTask());
    }

    [TestMethod]
    public async Task Resampling_command_exposes_five_modes_and_latest_request_wins_without_relabelling_visible_raster()
    {
        var ui = new ManualUiDispatcher();
        var shared = new SharedScanTimeViewport(ui);
        var source = new ManualBScanSource { PendRegions = true };
        var coordinator = new BScanSelectionCoordinator(ui, new PendingTraceProvider(), new AScanProjector());
        var vm = new BScanViewModel(source, shared, coordinator, ui);
        AcquisitionRunId run = new(Guid.NewGuid());
        source.Emit(Snapshot(run, 1, 0, 10));
        await ui.DriveUntilAsync(() => source.PendingRegions.Count == 1);

        Assert.HasCount(5, vm.AvailableResamplingModes);
        Assert.AreEqual(Scan2DInterpolationMode.None, vm.RequestedResamplingMode);
        vm.ChangeResamplingModeCommand.Execute(Scan2DInterpolationMode.Lanczos);
        await ui.DriveUntilAsync(() => vm.RequestedResamplingMode == Scan2DInterpolationMode.Lanczos);
        vm.ChangeResamplingModeCommand.Execute(Scan2DInterpolationMode.Bicubic);
        await ui.DriveUntilAsync(() => vm.RequestedResamplingMode == Scan2DInterpolationMode.Bicubic);
        await ui.DriveUntilAsync(() => source.PendingRegions.Count >= 2);

        BScanRenderRequest latest = source.PendingRegions[^1].Request;
        Assert.AreEqual(Scan2DInterpolationMode.Bicubic, latest.EffectiveResampling.Mode);
        Assert.IsTrue(vm.State.IsRenderPending);
        Assert.IsNull(vm.State.PresentedResamplingMode);
        BScanVisualSnapshot raster = Snapshot(run, 2, 0, 10) with
        {
            ConsumerId = latest.ConsumerId,
            SpatialViewport = latest.SpatialViewport,
            TimeViewport = latest.TimeViewport,
            RasterWidth = latest.PixelWidth,
            RasterHeight = latest.PixelHeight,
            RenderConfigurationVersion = latest.RenderConfigurationVersion,
            Resampling = latest.EffectiveResampling
        };
        source.PendingRegions[^1].Completion.TrySetResult(BScanRasterResult.Available(new(raster, 0, 0,
            latest.PixelWidth, latest.PixelHeight, new ByteOwner(new byte[latest.PixelWidth * latest.PixelHeight]),
            () => source.ReturnedRegions++)));
        await ui.DriveUntilAsync(() => vm.State.PresentedResamplingMode == Scan2DInterpolationMode.Bicubic);
        Assert.IsFalse(vm.State.IsRenderPending);
        Assert.AreEqual(Scan2DInterpolationMode.Bicubic, vm.State.ConfirmedRenderSignature!.Value.Resampling.Mode);

        await Drive(ui, vm.DisposeAsync().AsTask());
        Assert.IsFalse(vm.ChangeResamplingModeCommand.CanExecute(Scan2DInterpolationMode.None));
        await Drive(ui, coordinator.DisposeAsync().AsTask());
        await Drive(ui, shared.DisposeAsync().AsTask());
    }

    [TestMethod]
    public async Task SelectionProjectsExactSamplesAndLatestSelectionWins()
    {
        var ui = new ManualUiDispatcher();
        var provider = new PendingTraceProvider();
        var coordinator = new BScanSelectionCoordinator(ui, provider, new AScanProjector(16));
        int healthyNotifications = 0;
        coordinator.PropertyChanged += (_, _) => throw new InvalidOperationException("observer failure");
        coordinator.PropertyChanged += (_, _) => healthyNotifications++;
        AcquisitionRunId run = new(Guid.NewGuid());
        await Drive(ui, coordinator.SetRunAsync(run));
        BScanTraceIdentity first = Identity(run, 1);
        BScanTraceIdentity second = Identity(run, 2);

        Task old = coordinator.SelectAsync("b-1", first);
        await ui.DriveUntilAsync(() => provider.Requests.Count == 1);
        Task current = coordinator.SelectAsync("b-2", second);
        Assert.HasCount(1, provider.Requests);
        provider.Complete(0, Available(first, 3, [1, 2, 3]));
        await ui.DriveAsync(old);
        await ui.DriveUntilAsync(() => provider.Requests.Count == 2);
        provider.Complete(1, Available(second, 7, [-32768, 0, 32767]));
        await ui.DriveAsync(current);

        Assert.AreEqual(second, coordinator.State.Identity);
        Assert.AreEqual(7UL, coordinator.State.AcquisitionIndex);
        CollectionAssert.AreEqual(new[] { -100d, 0d, 100d * 32767 / 32768 },
            coordinator.State.Snapshot!.Points.Select(point => point.AmplitudePercent).ToArray());
        Assert.AreEqual(1, provider.ReturnedOwners);
        Assert.AreEqual(1, coordinator.ObserverErrorCount);
        Assert.IsGreaterThan(0, healthyNotifications);
        await Drive(ui, coordinator.DisposeAsync().AsTask());
        Assert.AreEqual(2, provider.ReturnedOwners);
    }

    [TestMethod]
    public async Task FailedSelectionKeepsPreviousAndRunChangeInvalidatesIt()
    {
        var ui = new ManualUiDispatcher();
        var provider = new PendingTraceProvider();
        var coordinator = new BScanSelectionCoordinator(ui, provider, new AScanProjector());
        AcquisitionRunId run = new(Guid.NewGuid());
        await Drive(ui, coordinator.SetRunAsync(run));
        BScanTraceIdentity identity = Identity(run, 1);
        Task selection = coordinator.SelectAsync("b", identity);
        await ui.DriveUntilAsync(() => provider.Requests.Count == 1);
        provider.Complete(0, Available(identity, 4, [1, 2, 3]));
        await ui.DriveAsync(selection);

        BScanTraceIdentity missing = Identity(run, 2);
        Task failed = coordinator.SelectAsync("b", missing);
        await ui.DriveUntilAsync(() => provider.Requests.Count == 2);
        provider.Complete(1, BScanTraceResult.NotAvailable());
        await ui.DriveAsync(failed);
        Assert.AreEqual(identity, coordinator.State.Identity);
        Assert.AreEqual(BScanTraceResultKind.NotAvailable, coordinator.State.LastResult);

        AcquisitionRunId next = new(Guid.NewGuid());
        await Drive(ui, coordinator.SetRunAsync(next));
        Assert.IsNull(coordinator.State.Snapshot);
        Assert.AreEqual(next, coordinator.State.RunId);
        await Drive(ui, coordinator.SetRunAsync(run));
        Assert.AreEqual(next, coordinator.State.RunId);
        await Drive(ui, coordinator.DisposeAsync().AsTask());
    }

    [TestMethod]
    public async Task Unresolvable_latest_selection_cancels_suspended_resolution_and_preserves_visible_selection()
    {
        var ui = new ManualUiDispatcher();
        var provider = new PendingTraceProvider();
        var coordinator = new BScanSelectionCoordinator(ui, provider, new AScanProjector());
        AcquisitionRunId run = new(Guid.NewGuid());
        await Drive(ui, coordinator.SetRunAsync(run));
        BScanTraceIdentity identity = Identity(run, 1);
        Task old = coordinator.SelectAsync("b", identity);
        await ui.DriveUntilAsync(() => provider.Requests.Count == 1);

        Task unavailable = coordinator.SelectAsync("b", run, 999);
        await ui.DriveAsync(unavailable);
        Assert.AreEqual(BScanTraceResultKind.NotAvailable, coordinator.State.LastResult);
        Assert.IsFalse(coordinator.State.IsPending);

        provider.Complete(0, Available(identity, 1, [1, 2, 3]));
        await ui.DriveAsync(old);
        Assert.IsNull(coordinator.State.Identity);
        Assert.AreEqual(BScanTraceResultKind.NotAvailable, coordinator.State.LastResult);
        Assert.AreEqual(1, provider.ReturnedOwners);
        await Drive(ui, coordinator.DisposeAsync().AsTask());
    }

    [TestMethod]
    public async Task BScanUsesLocalVersionAndSharedViewportAndRemovesOnlyItsConsumer()
    {
        var ui = new ManualUiDispatcher();
        var shared = new SharedScanTimeViewport(ui);
        var source = new ManualBScanSource();
        var provider = new PendingTraceProvider();
        var coordinator = new BScanSelectionCoordinator(ui, provider, new AScanProjector());
        var vm = new BScanViewModel(source, shared, coordinator, ui);
        int healthyCommandNotifications = 0;
        vm.ZoomSpatialInCommand.CanExecuteChanged += (_, _) => throw new InvalidOperationException("command observer failure");
        vm.ZoomSpatialInCommand.CanExecuteChanged += (_, _) => healthyCommandNotifications++;
        AcquisitionRunId run = new(Guid.NewGuid());
        shared.ReconcileDomain("a", run, 40, 0, 10);
        await ui.DriveUntilAsync(() => shared.Viewport is not null);

        source.Emit(Snapshot(run, 7, 2, 8));
        await ui.DriveUntilAsync(() => vm.State.SnapshotVersion == 7);
        Assert.AreEqual(7UL, vm.State.TimeViewportBinding!.SnapshotVersion);
        Assert.AreEqual(2d, vm.State.TimeViewportBinding.Viewport.DomainMinimumSeconds);
        Assert.AreEqual(8d, vm.State.TimeViewportBinding.Viewport.DomainMaximumSeconds);
        Assert.IsGreaterThan(0, healthyCommandNotifications);
        Assert.AreEqual(1, vm.ObserverErrorCount);

        await Drive(ui, vm.DisposeAsync().AsTask());
        Assert.AreEqual(0d, shared.Viewport!.DomainMinimumSeconds);
        Assert.AreEqual(10d, shared.Viewport.DomainMaximumSeconds);
        await Drive(ui, coordinator.DisposeAsync().AsTask());
        await Drive(ui, shared.DisposeAsync().AsTask());
    }

    [TestMethod]
    public async Task AScanSelectedKeepsSignalDuringLiveAndReturnToLiveIsImmediate()
    {
        var ui = new ManualUiDispatcher();
        var provider = new PendingTraceProvider();
        var coordinator = new BScanSelectionCoordinator(ui, provider, new AScanProjector());
        var session = new ManualApplicationSession();
        var feed = new ManualObservable<AScanSnapshot>();
        var configuration = new ConventionalAcquisitionConfiguration(new(0), 3, 10);
        var vm = new AScanViewModel(session, feed, ui, configuration, selectionCoordinator: coordinator);
        AcquisitionRunId run = new(Guid.NewGuid());
        session.Set(ManualApplicationSession.Create(1, UTStudio.Contracts.Application.SessionPhase.Running,
            run, canStop: true));
        var metadata = new ConventionalUtFrameMetadata(ManualApplicationSession.Source, run, configuration,
            DateTimeOffset.UnixEpoch);
        AScanSnapshot live1 = new AScanProjector().Project(metadata, 1, TimeSpan.Zero, [1, 2, 3], 40);
        feed.Emit(live1);
        await ui.DriveUntilAsync(() => vm.DisplayMode == AScanDisplayMode.Live);

        BScanTraceIdentity selectedIdentity = new(metadata.SourceId, run, configuration.PhysicalChannelId, 5);
        Task selection = coordinator.SelectAsync("b", selectedIdentity);
        await ui.DriveUntilAsync(() => provider.Requests.Count == 1);
        provider.Complete(0, Available(selectedIdentity, 5, [10, 20, 30]));
        await ui.DriveAsync(selection);
        await ui.DriveUntilAsync(() => vm.DisplayMode == AScanDisplayMode.Selected);
        AScanSnapshot selected = vm.AScan!;

        AScanSnapshot live2 = new AScanProjector().Project(metadata, 2, TimeSpan.Zero, [4, 5, 6], 41);
        feed.Emit(live2);
        await ui.DriveUntilAsync(() => vm.State.LatestCompatibleLive == live2);
        Assert.AreSame(selected, vm.AScan);
        vm.ReturnToLive();
        await ui.DriveUntilAsync(() => vm.DisplayMode == AScanDisplayMode.Live);
        Assert.AreSame(live2, vm.AScan);

        await Drive(ui, vm.DisposeAsync().AsTask());
        await Drive(ui, coordinator.DisposeAsync().AsTask());
    }

    [TestMethod]
    public async Task StoppedRunKeepsFrozenLiveSignalAndAllowsSelectionAndReturnToLive()
    {
        var ui = new ManualUiDispatcher();
        var provider = new PendingTraceProvider();
        var coordinator = new BScanSelectionCoordinator(ui, provider, new AScanProjector());
        var session = new ManualApplicationSession();
        var feed = new ManualObservable<AScanSnapshot>();
        var configuration = new ConventionalAcquisitionConfiguration(new(0), 3, 10);
        var vm = new AScanViewModel(session, feed, ui, configuration, selectionCoordinator: coordinator);
        AcquisitionRunId run = new(Guid.NewGuid());
        session.Set(ManualApplicationSession.Create(1, UTStudio.Contracts.Application.SessionPhase.Running,
            run, canStop: true));
        var metadata = new ConventionalUtFrameMetadata(ManualApplicationSession.Source, run, configuration,
            DateTimeOffset.UnixEpoch);
        AScanSnapshot live = new AScanProjector().Project(metadata, 3, TimeSpan.Zero, [1, 2, 3], 40);
        feed.Emit(live);
        await ui.DriveUntilAsync(() => vm.DisplayMode == AScanDisplayMode.Live);

        session.Set(ManualApplicationSession.Create(2, UTStudio.Contracts.Application.SessionPhase.Stopping,
            run));
        await ui.DriveUntilAsync(() => vm.Session.Phase == UTStudio.Contracts.Application.SessionPhase.Stopping);
        Assert.AreSame(live, vm.AScan);
        session.Set(ManualApplicationSession.Create(3,
            UTStudio.Contracts.Application.SessionPhase.AwaitingFramesReleased, run));
        await ui.DriveUntilAsync(() =>
            vm.Session.Phase == UTStudio.Contracts.Application.SessionPhase.AwaitingFramesReleased);
        Assert.AreSame(live, vm.AScan);
        session.Set(ManualApplicationSession.Create(4, UTStudio.Contracts.Application.SessionPhase.Disconnecting,
            run));
        await ui.DriveUntilAsync(() =>
            vm.Session.Phase == UTStudio.Contracts.Application.SessionPhase.Disconnecting);
        Assert.AreSame(live, vm.AScan);
        session.Set(ManualApplicationSession.Create(5, UTStudio.Contracts.Application.SessionPhase.Idle,
            canStart: true));
        await ui.DriveUntilAsync(() => vm.Session.Phase == UTStudio.Contracts.Application.SessionPhase.Idle);
        Assert.AreSame(live, vm.AScan);

        BScanTraceIdentity identity = new(metadata.SourceId, run, configuration.PhysicalChannelId, 8);
        Task selection = coordinator.SelectAsync("b", identity);
        await ui.DriveUntilAsync(() => provider.Requests.Count == 1);
        provider.Complete(0, Available(identity, 8, [10, 20, 30]));
        await ui.DriveAsync(selection);
        await ui.DriveUntilAsync(() => vm.DisplayMode == AScanDisplayMode.Selected);
        CollectionAssert.AreEqual(new[] { 100d * 10 / 32768, 100d * 20 / 32768, 100d * 30 / 32768 },
            vm.AScan!.Points.Select(point => point.AmplitudePercent).ToArray());

        vm.ReturnToLive();
        await ui.DriveUntilAsync(() => vm.DisplayMode == AScanDisplayMode.Live);
        Assert.AreSame(live, vm.AScan);

        await Drive(ui, vm.DisposeAsync().AsTask());
        await Drive(ui, coordinator.DisposeAsync().AsTask());
    }

    [TestMethod]
    public void PresentationRemainsNeutralAndViewModelsDoNotReferenceEachOther()
    {
        var assembly = typeof(BScanViewModel).Assembly;
        Assert.IsFalse(assembly.GetReferencedAssemblies().Any(name =>
            name.Name is "PresentationFramework" or "PresentationCore" or "WindowsBase"));
        Assert.IsFalse(typeof(BScanViewModel).GetFields(System.Reflection.BindingFlags.Instance |
            System.Reflection.BindingFlags.NonPublic).Any(field => field.FieldType == typeof(AScanViewModel)));
        Assert.IsFalse(typeof(AScanViewModel).GetFields(System.Reflection.BindingFlags.Instance |
            System.Reflection.BindingFlags.NonPublic).Any(field => field.FieldType == typeof(BScanViewModel)));
    }

    [TestMethod]
    public async Task ReversedMetadataCallbacksCannotRestoreAnOlderRun()
    {
        var ui = new ManualUiDispatcher { AlwaysQueue = true };
        var shared = new SharedScanTimeViewport(ui);
        var source = new ManualBScanSource();
        var coordinator = new BScanSelectionCoordinator(ui, new PendingTraceProvider(), new AScanProjector());
        var vm = new BScanViewModel(source, shared, coordinator, ui);
        AcquisitionRunId oldRun = new(Guid.NewGuid());
        AcquisitionRunId newRun = new(Guid.NewGuid());
        source.Emit(Snapshot(oldRun, 1, 0, 10));
        source.Emit(Snapshot(newRun, 1, 1, 9));
        Assert.IsTrue(ui.RunLast());
        while (ui.RunNext()) { }
        Assert.AreEqual(newRun, vm.State.RunId);

        await Drive(ui, vm.DisposeAsync().AsTask());
        await Drive(ui, coordinator.DisposeAsync().AsTask());
        await Drive(ui, shared.DisposeAsync().AsTask());
    }

    [TestMethod]
    public async Task CoordinatorCloseWaitsForEverySupersededResolutionAndPublishesNothingLate()
    {
        var ui = new ManualUiDispatcher();
        var provider = new PendingTraceProvider();
        var coordinator = new BScanSelectionCoordinator(ui, provider, new AScanProjector());
        AcquisitionRunId run = new(Guid.NewGuid());
        await Drive(ui, coordinator.SetRunAsync(run));
        Task first = coordinator.SelectAsync("one", Identity(run, 1));
        await ui.DriveUntilAsync(() => provider.Requests.Count == 1);
        Task second = coordinator.SelectAsync("two", Identity(run, 2));
        Assert.HasCount(1, provider.Requests);
        Task close = coordinator.DisposeAsync().AsTask();
        Assert.IsFalse(close.IsCompleted);
        provider.Complete(0, Available(Identity(run, 1), 1, [4, 5, 6]));
        await ui.DriveAsync(close);
        await Task.WhenAll(first, second);
        Assert.IsTrue(coordinator.IsClosed);
        Assert.AreEqual(1, provider.ReturnedOwners);
        int pending = ui.Pending;
        while (ui.RunNext()) { }
        Assert.AreEqual(0, pending);
    }

    [TestMethod]
    public async Task RasterPublicationCopiesOwnerPairsRevisionAndCommandsStayUiAffine()
    {
        var ui = new ManualUiDispatcher();
        var shared = new SharedScanTimeViewport(ui);
        var source = new ManualBScanSource();
        var coordinator = new BScanSelectionCoordinator(ui, new PendingTraceProvider(), new AScanProjector());
        var vm = new BScanViewModel(source, shared, coordinator, ui);
        AcquisitionRunId run = new(Guid.NewGuid());
        BScanVisualSnapshot metadata = Snapshot(run, 7, 0, 10);
        source.RegionFactory = request =>
        {
            var rasterSnapshot = metadata with
            {
                SnapshotVersion = 8,
                BaseStoreRevision = metadata.StoreRevision,
                StoreRevision = metadata.StoreRevision + 1,
                SpatialViewport = request.SpatialViewport,
                TimeViewport = request.TimeViewport,
                RequiresFullRefresh = true,
                ConsumerId = request.ConsumerId
            };
            return BScanRasterResult.Available(new(rasterSnapshot, 0, 0, 2, 2,
                new ByteOwner([1, 2, 3, 4]), () => source.ReturnedRegions++));
        };
        bool outsideUi = false;
        vm.PropertyChanged += (_, _) => outsideUi |= !ui.CheckAccess();
        source.Emit(metadata);
        await ui.DriveUntilAsync(() => vm.State.Region is not null);

        Assert.AreEqual(8UL, vm.State.DisplayedRevision!.Value.SnapshotVersion);
        Assert.AreEqual(8UL, vm.State.TimeViewportBinding!.SnapshotVersion);
        byte[] copied = new byte[4];
        BScanPresentedRegionToken firstToken = vm.State.Region!.Token;
        Assert.IsTrue(vm.TryCopyPresentedRegion(firstToken, copied, 2));
        CollectionAssert.AreEqual(new byte[] { 1, 2, 3, 4 }, copied);
        Assert.AreEqual(0, source.ReturnedRegions);
        decimal span = vm.State.SpatialViewport!.Value.Span;
        vm.ZoomSpatialInCommand.Execute(null);
        await ui.DriveUntilAsync(() => vm.State.SpatialViewport!.Value.Span < span);
        await ui.DriveUntilAsync(() => vm.State.Region is { Token: var token } && token != firstToken);
        Assert.IsFalse(vm.TryCopyPresentedRegion(firstToken, copied, 2));
        Assert.IsTrue(vm.TryCopyPresentedRegion(vm.State.Region!.Token, copied, 2));
        Assert.IsFalse(outsideUi);

        await Drive(ui, vm.DisposeAsync().AsTask());
        Assert.AreEqual(2, source.ReturnedRegions);
        await Drive(ui, coordinator.DisposeAsync().AsTask());
        await Drive(ui, shared.DisposeAsync().AsTask());
    }

    [TestMethod]
    public async Task Initial_live_raster_is_not_starved_by_newer_autofit_metadata()
    {
        var ui = new ManualUiDispatcher();
        var shared = new SharedScanTimeViewport(ui);
        var source = new ManualBScanSource { PendRegions = true };
        var coordinator = new BScanSelectionCoordinator(ui, new PendingTraceProvider(), new AScanProjector());
        var vm = new BScanViewModel(source, shared, coordinator, ui);
        AcquisitionRunId run = new(Guid.NewGuid());
        BScanVisualSnapshot first = Snapshot(run, 1, 0, 10);

        source.Emit(first);
        await ui.DriveUntilAsync(() => source.PendingRegions.Count == 1);
        BScanRenderRequest firstRequest = source.PendingRegions[0].Request;
        var grownRange = new BScanAcquisitionRange(0, 10);
        source.Emit(first with
        {
            SnapshotVersion = 2,
            BaseStoreRevision = 1,
            StoreRevision = 2,
            RepresentedRange = grownRange,
            ResidentRange = grownRange,
            SpatialViewport = BScanSpatialViewportOperations.Reset(grownRange)
        });
        await ui.DriveUntilAsync(() => vm.State.StoreRevision == 2);

        Assert.IsFalse(source.PendingRegions[0].CancellationToken.IsCancellationRequested);
        BScanVisualSnapshot raster = first with
        {
            SnapshotVersion = 3,
            SpatialViewport = firstRequest.SpatialViewport,
            TimeViewport = firstRequest.TimeViewport,
            ConsumerId = firstRequest.ConsumerId
        };
        source.PendingRegions[0].Completion.SetResult(BScanRasterResult.Available(new(raster, 0, 0, 2, 2,
            new ByteOwner([1, 2, 3, 4]), () => source.ReturnedRegions++)));
        await ui.DriveUntilAsync(() => vm.State.Region is not null);
        await ui.DriveUntilAsync(() => source.PendingRegions.Count == 2);

        Assert.AreEqual(1UL, vm.State.Region!.Snapshot.StoreRevision);
        Assert.AreEqual(2UL, vm.State.StoreRevision);
        await Drive(ui, vm.DisposeAsync().AsTask());
        await Drive(ui, coordinator.DisposeAsync().AsTask());
        await Drive(ui, shared.DisposeAsync().AsTask());
    }

    [TestMethod]
    public async Task Live_autofit_uses_fixed_ring_width_then_slides_after_wrap()
    {
        var ui = new ManualUiDispatcher();
        var shared = new SharedScanTimeViewport(ui);
        var source = new ManualBScanSource { EffectiveCapacity = 4 };
        var coordinator = new BScanSelectionCoordinator(ui, new PendingTraceProvider(), new AScanProjector());
        var vm = new BScanViewModel(source, shared, coordinator, ui);
        AcquisitionRunId run = new(Guid.NewGuid());
        BScanVisualSnapshot first = Snapshot(run, 1, 0, 10) with
        {
            RepresentedRange = new(0, 0),
            ResidentRange = new(0, 0)
        };

        source.Emit(first);
        await ui.DriveUntilAsync(() => vm.State.SnapshotVersion == 1);
        Assert.AreEqual(new BScanSpatialViewport(0, 0, 4), vm.State.SpatialViewport);
        Assert.AreEqual(4, vm.LiveWindowColumns);
        Assert.IsTrue(vm.DecreaseLiveWindowCommand.CanExecute(null));
        Assert.IsFalse(vm.IncreaseLiveWindowCommand.CanExecute(null));

        BScanSpatialViewport? viewportObservedWithSize = null;
        vm.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(BScanViewModel.LiveWindowColumns))
            { viewportObservedWithSize = vm.State.SpatialViewport; }
        };
        vm.DecreaseLiveWindowCommand.Execute(null);
        await ui.DriveUntilAsync(() => vm.LiveWindowColumns == 2);
        Assert.AreEqual(new BScanSpatialViewport(0, 0, 2), vm.State.SpatialViewport);
        Assert.AreEqual(vm.State.SpatialViewport, viewportObservedWithSize);
        vm.IncreaseLiveWindowCommand.Execute(null);
        await ui.DriveUntilAsync(() => vm.LiveWindowColumns == 4);
        Assert.AreEqual(new BScanSpatialViewport(0, 0, 4), vm.State.SpatialViewport);

        source.Emit(first with
        {
            SnapshotVersion = 2,
            BaseStoreRevision = 1,
            StoreRevision = 2,
            RepresentedRange = new(0, 1),
            ResidentRange = new(0, 1)
        });
        await ui.DriveUntilAsync(() => vm.State.SnapshotVersion == 2);
        Assert.AreEqual(new BScanSpatialViewport(0, 0, 4), vm.State.SpatialViewport);

        source.Emit(first with
        {
            SnapshotVersion = 3,
            BaseStoreRevision = 2,
            StoreRevision = 5,
            WrapGeneration = 1,
            RepresentedRange = new(1, 4),
            ResidentRange = new(1, 4)
        });
        await ui.DriveUntilAsync(() => vm.State.SnapshotVersion == 3);
        Assert.AreEqual(new BScanSpatialViewport(1, 0, 4), vm.State.SpatialViewport);

        await Drive(ui, vm.DisposeAsync().AsTask());
        await Drive(ui, coordinator.DisposeAsync().AsTask());
        await Drive(ui, shared.DisposeAsync().AsTask());
    }

    [TestMethod]
    public async Task Provisional_one_pixel_raster_does_not_starve_sized_live_refresh()
    {
        var ui = new ManualUiDispatcher();
        var shared = new SharedScanTimeViewport(ui);
        var source = new ManualBScanSource { PendRegions = true, EffectiveCapacity = 512 };
        var coordinator = new BScanSelectionCoordinator(ui, new PendingTraceProvider(), new AScanProjector());
        var vm = new BScanViewModel(source, shared, coordinator, ui);
        AcquisitionRunId run = new(Guid.NewGuid());
        BScanVisualSnapshot metadata = Snapshot(run, 1, 0, 10);

        source.Emit(metadata);
        await ui.DriveUntilAsync(() => source.PendingRegions.Count == 1);
        vm.SetRasterSize(320, 180, 7);
        await ui.DriveUntilAsync(() => ui.Pending == 0);
        CompleteRegion(source, 0, metadata);
        await ui.DriveUntilAsync(() => source.PendingRegions.Count == 2);
        Assert.AreEqual(320, source.PendingRegions[1].Request.PixelWidth);
        Assert.AreEqual(180, source.PendingRegions[1].Request.PixelHeight);
        Assert.IsNull(source.PendingRegions[1].Request.DisplayedStoreRevision);
        Assert.IsNull(source.PendingRegions[1].Request.Displayed);

        source.Emit(metadata with { SnapshotVersion = 2, BaseStoreRevision = 1, StoreRevision = 2 });
        await ui.DriveUntilAsync(() => vm.State.StoreRevision == 2);
        Assert.IsFalse(source.PendingRegions[1].CancellationToken.IsCancellationRequested);
        CompleteRegion(source, 1, metadata with { SnapshotVersion = 3, BaseStoreRevision = 1, StoreRevision = 2 });
        await ui.DriveUntilAsync(() => vm.State.Region?.Snapshot.RasterWidth == 320);

        Assert.AreEqual(180, vm.State.Region!.Snapshot.RasterHeight);
        await ui.DriveUntilAsync(() => source.PendingRegions.Count == 3);
        source.Emit(metadata with { SnapshotVersion = 4, BaseStoreRevision = 2, StoreRevision = 3 });
        await ui.DriveUntilAsync(() => vm.State.StoreRevision == 3);
        Assert.IsFalse(source.PendingRegions[2].CancellationToken.IsCancellationRequested);
        Assert.HasCount(3, source.PendingRegions);
        CompleteRegion(source, 2, metadata with { SnapshotVersion = 5, BaseStoreRevision = 2, StoreRevision = 3 });
        await ui.DriveUntilAsync(() => source.PendingRegions.Count == 4);

        await Drive(ui, vm.DisposeAsync().AsTask());
        await Drive(ui, coordinator.DisposeAsync().AsTask());
        await Drive(ui, shared.DisposeAsync().AsTask());
    }

    private static void CompleteRegion(ManualBScanSource source, int index, BScanVisualSnapshot metadata)
    {
        BScanRenderRequest request = source.PendingRegions[index].Request;
        BScanVisualSnapshot raster = metadata with
        {
            SpatialViewport = request.SpatialViewport,
            TimeViewport = request.TimeViewport,
            RasterWidth = request.PixelWidth,
            RasterHeight = request.PixelHeight,
            RenderConfigurationVersion = request.RenderConfigurationVersion,
            AffectedRegion = new(0, 0, request.PixelWidth, request.PixelHeight),
            ConsumerId = request.ConsumerId
        };
        byte[] pixels = Enumerable.Repeat((byte)128, checked(request.PixelWidth * request.PixelHeight)).ToArray();
        source.PendingRegions[index].Completion.SetResult(BScanRasterResult.Available(new(raster, 0, 0,
            request.PixelWidth, request.PixelHeight, new ByteOwner(pixels), () => source.ReturnedRegions++)));
    }

    [TestMethod]
    public void PresentedRegionContractContainsMetadataOnly()
    {
        Type type = typeof(BScanPresentedRegion);
        Assert.IsFalse(type.GetProperties().Any(property =>
            property.PropertyType == typeof(byte[]) ||
            property.PropertyType == typeof(Memory<byte>) ||
            property.PropertyType == typeof(ReadOnlyMemory<byte>) ||
            typeof(IDisposable).IsAssignableFrom(property.PropertyType)));
    }

    [TestMethod]
    [DataRow("subscription")]
    [DataRow("time-viewport-events")]
    [DataRow("selection-events")]
    [DataRow("selection-cancel")]
    [DataRow("raster-cancel")]
    [DataRow("raster-operations")]
    [DataRow("preference-writes")]
    [DataRow("ui-state")]
    [DataRow("time-viewport-consumer")]
    [DataRow("visible-region")]
    [DataRow("raster-consumer")]
    [DataRow("ui-barrier")]
    [DataRow("ui-lifetime")]
    [DataRow("preference-cancellation")]
    public async Task BScanCloseAttemptsEveryStageWhenOneStageFails(string failingStage)
    {
        var ui = new ManualUiDispatcher();
        var shared = new SharedScanTimeViewport(ui);
        var source = new ManualBScanSource();
        var coordinator = new BScanSelectionCoordinator(ui, new PendingTraceProvider(), new AScanProjector());
        List<string> visited = [];
        var failure = new InvalidOperationException(failingStage);
        var vm = new BScanViewModel(source, shared, coordinator, ui, stage =>
        {
            visited.Add(stage);
            if (stage == failingStage) { throw failure; }
        });

        CleanupFailureException error = await Assert.ThrowsAsync<CleanupFailureException>(
            () => Drive(ui, vm.DisposeAsync().AsTask()));

        Assert.AreSame(failure, error.PrimaryError);
        Assert.AreEqual(failingStage, error.PrimaryFailure.Stage);
        CollectionAssert.AreEqual(new[] { "subscription", "time-viewport-events", "selection-events",
            "selection-cancel", "raster-cancel", "raster-operations", "preference-writes", "ui-state", "time-viewport-consumer", "visible-region",
            "raster-consumer", "ui-barrier", "ui-lifetime", "preference-cancellation" }, visited);
        Assert.IsFalse(source.HasConsumer);
        Assert.IsFalse(source.HasObserver);
        Assert.AreSame(vm.DisposeAsync().AsTask(), vm.DisposeAsync().AsTask());
        await Drive(ui, coordinator.DisposeAsync().AsTask());
        await Drive(ui, shared.DisposeAsync().AsTask());
    }

    [TestMethod]
    public async Task BScanClosePreservesPrimaryAndOrderedSecondaryFailures()
    {
        var ui = new ManualUiDispatcher();
        var shared = new SharedScanTimeViewport(ui);
        var source = new ManualBScanSource();
        var coordinator = new BScanSelectionCoordinator(ui, new PendingTraceProvider(), new AScanProjector());
        var primary = new InvalidOperationException("primary");
        var secondary = new InvalidOperationException("secondary");
        var vm = new BScanViewModel(source, shared, coordinator, ui, stage =>
        {
            if (stage == "subscription") { throw primary; }
            if (stage == "ui-barrier") { throw secondary; }
        });

        CleanupFailureException error = await Assert.ThrowsAsync<CleanupFailureException>(
            () => Drive(ui, vm.DisposeAsync().AsTask()));

        Assert.AreSame(primary, error.PrimaryError);
        Assert.AreEqual("subscription", error.PrimaryFailure.Stage);
        Assert.HasCount(1, error.SecondaryErrors);
        Assert.AreSame(secondary, error.SecondaryErrors[0]);
        Assert.AreEqual("ui-barrier", error.SecondaryFailures[0].Stage);
        await Drive(ui, coordinator.DisposeAsync().AsTask());
        await Drive(ui, shared.DisposeAsync().AsTask());
    }

    [TestMethod]
    public async Task RealCleanupDependenciesFailAfterBalancingAndLaterStagesStillRun()
    {
        var ui = new ManualUiDispatcher();
        var shared = new SharedScanTimeViewport(ui);
        var source = new ManualBScanSource { FailSubscriptionDispose = true, FailUnregisterConsumer = true };
        var coordinator = new BScanSelectionCoordinator(ui, new PendingTraceProvider(), new AScanProjector());
        var vm = new BScanViewModel(source, shared, coordinator, ui);

        CleanupFailureException error = await Assert.ThrowsAsync<CleanupFailureException>(
            () => Drive(ui, vm.DisposeAsync().AsTask()));

        Assert.AreEqual("subscription", error.PrimaryFailure.Stage);
        Assert.AreEqual("raster-consumer", error.SecondaryFailures.Single().Stage);
        Assert.AreEqual(1, source.SubscriptionDisposeAttempts);
        Assert.AreEqual(1, source.UnregisterAttempts);
        Assert.IsFalse(source.HasObserver);
        Assert.IsFalse(source.HasConsumer);
        Assert.AreSame(vm.DisposeAsync().AsTask(), vm.DisposeAsync().AsTask());
        await Drive(ui, coordinator.DisposeAsync().AsTask());
        await Drive(ui, shared.DisposeAsync().AsTask());
    }

    [TestMethod]
    public async Task BScanCloseFromNonUiConcurrentCallersSharesTaskAndRunsStagesOnce()
    {
        var ui = new ManualUiDispatcher();
        var shared = new SharedScanTimeViewport(ui);
        var source = new ManualBScanSource();
        var coordinator = new BScanSelectionCoordinator(ui, new PendingTraceProvider(), new AScanProjector());
        Dictionary<string, int> calls = [];
        var vm = new BScanViewModel(source, shared, coordinator, ui, stage =>
        { lock (calls) { calls[stage] = calls.GetValueOrDefault(stage) + 1; } });
        Task? first = null, second = null;

        await Task.WhenAll(
            Task.Run(() => { first = vm.DisposeAsync().AsTask(); }),
            Task.Run(() => { second = vm.DisposeAsync().AsTask(); }));
        Assert.AreSame(first, second);
        await Drive(ui, first!);

        Assert.HasCount(14, calls);
        Assert.IsTrue(calls.Values.All(count => count == 1));
        Assert.AreSame(first, vm.DisposeAsync().AsTask());
        await Drive(ui, coordinator.DisposeAsync().AsTask());
        await Drive(ui, shared.DisposeAsync().AsTask());
    }

    private static async Task Drive(ManualUiDispatcher ui, Task task) => await ui.DriveAsync(task);
    private static BScanTraceIdentity Identity(AcquisitionRunId run, ulong sequence) =>
        new(new("selection-test"), run, new(0), sequence);

    private static BScanTraceResult Available(BScanTraceIdentity identity, ulong index, short[] samples)
    {
        var metadata = new ConventionalUtFrameMetadata(identity.SourceId, identity.RunId,
            new(identity.PhysicalChannelId, samples.Length, 10), DateTimeOffset.UnixEpoch);
        var descriptor = new BScanTraceDescriptor(metadata, identity, index, TimeSpan.FromSeconds(index),
            samples.Length, 10, 0, null, index + 1, 1);
        return BScanTraceResult.Available(new(descriptor, new Owner(samples)));
    }

    private static BScanVisualSnapshot Snapshot(AcquisitionRunId run, ulong version, double timeMinimum,
        double timeMaximum)
    {
        var range = new BScanAcquisitionRange(0, 9);
        var spatial = BScanSpatialViewportOperations.Reset(range);
        var domain = ScanTimeViewportOperations.Create(timeMinimum, timeMaximum);
        return new(run, version, version - 1, version, 0, range, range, domain, spatial, domain,
            10, 10, 0, new(0, 9, version - 1, version), new(0, 0, 10, 10), false, true,
            BScanFullRefreshReason.Initial)
        {
            Stream = new(ManualApplicationSession.Source, run, new(0))
        };
    }

    private sealed class Owner(short[] samples) : IMemoryOwner<short>
    {
        public Memory<short> Memory { get; private set; } = samples;
        public void Dispose() => Memory = Memory<short>.Empty;
    }

    private sealed class ByteOwner : IMemoryOwner<byte>
    {
        internal ByteOwner(byte[] pixels)
        {
            byte[] storage = new byte[checked(pixels.Length * 3)];
            pixels.CopyTo(storage, 0);
            storage.AsSpan(pixels.Length, pixels.Length).Fill(1);
            storage.AsSpan(checked(pixels.Length * 2), pixels.Length).Fill(1);
            Memory = storage;
        }
        public Memory<byte> Memory { get; private set; }
        public void Dispose() => Memory = Memory<byte>.Empty;
    }

    private sealed class PendingTraceProvider : IBScanTraceProvider
    {
        internal List<TaskCompletionSource<BScanTraceResult>> Requests { get; } = [];
        internal int ReturnedOwners;
        public ValueTask<BScanTraceResult> GetTraceAsync(BScanTraceIdentity identity,
            CancellationToken cancellationToken = default)
        {
            var completion = new TaskCompletionSource<BScanTraceResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            Requests.Add(completion);
            return new(completion.Task);
        }
        internal void Complete(int index, BScanTraceResult result)
        {
            if (result.Trace is { } trace)
            {
                var descriptor = trace.Descriptor;
                short[] samples = trace.Samples.ToArray();
                trace.Dispose();
                result = BScanTraceResult.Available(new(descriptor, new CountingOwner(samples, this)));
            }
            Requests[index].TrySetResult(result);
        }
        private sealed class CountingOwner(short[] samples, PendingTraceProvider owner) : IMemoryOwner<short>
        {
            private short[]? _samples = samples;
            public Memory<short> Memory => _samples ?? throw new ObjectDisposedException(nameof(CountingOwner));
            public void Dispose() { if (Interlocked.Exchange(ref _samples, null) is not null) { owner.ReturnedOwners++; } }
        }
    }

    private sealed class ManualBScanSource : IBScanVisualSource
    {
        private IObserver<BScanVisualUpdate>? _observer;
        private string? _consumerId;
        internal Func<BScanRenderRequest, BScanRasterResult>? RegionFactory { get; set; }
        internal bool PendRegions { get; init; }
        internal LockedList<PendingRegion> PendingRegions { get; } = new();
        internal int ReturnedRegions { get; set; }
        internal bool HasConsumer => _consumerId is not null;
        internal bool HasObserver => _observer is not null;
        internal bool FailSubscriptionDispose { get; init; }
        internal bool FailUnregisterConsumer { get; init; }
        internal int SubscriptionDisposeAttempts { get; private set; }
        internal int UnregisterAttempts { get; private set; }
        internal int EffectiveCapacity { get; init; } = 10;
        private Action? _beforeGetRegion;
        private int _getRegionRanOnUiThread, _getRegionCalls;
        internal Action? BeforeGetRegion { get => Volatile.Read(ref _beforeGetRegion); set => Volatile.Write(ref _beforeGetRegion, value); }
        internal Func<bool>? IsOnUiThread { get; init; }
        internal bool GetRegionRanOnUiThread => Volatile.Read(ref _getRegionRanOnUiThread) != 0;
        internal int GetRegionCalls => Volatile.Read(ref _getRegionCalls);
        public BScanVisualDiagnostics Diagnostics => new(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
            0, 0, 0, new(0, TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero),
            BScanVisualCloseState.Open, 0, null)
        { EffectiveCapacity = EffectiveCapacity };
        public IDisposable Subscribe(IObserver<BScanVisualUpdate> observer)
        {
            _observer = observer;
            return new Subscription(() =>
            {
                SubscriptionDisposeAttempts++;
                _observer = null;
                if (FailSubscriptionDispose) { throw new InvalidOperationException("subscription cleanup failed"); }
            });
        }
        public bool RegisterConsumer(string consumerId) { _consumerId = consumerId; return true; }
        public void UnregisterConsumer(string consumerId)
        {
            UnregisterAttempts++;
            if (_consumerId == consumerId) { _consumerId = null; }
            if (FailUnregisterConsumer) { throw new InvalidOperationException("consumer cleanup failed"); }
        }
        internal void Emit(BScanVisualSnapshot snapshot) => _observer?.OnNext(new(snapshot with
        { ConsumerId = _consumerId ?? throw new InvalidOperationException("No consumer registered.") }));
        public void RequestRender(BScanRenderRequest request) { }
        public ValueTask<BScanRasterResult> GetRegionAsync(BScanRenderRequest request,
            CancellationToken cancellationToken = default)
        {
            if (IsOnUiThread?.Invoke() == true) Interlocked.Exchange(ref _getRegionRanOnUiThread, 1);
            Interlocked.Increment(ref _getRegionCalls);
            BeforeGetRegion?.Invoke();
            if (!PendRegions)
            {
                return new(RegionFactory?.Invoke(request) ?? BScanRasterResult.NotAvailable());
            }
            var completion = new TaskCompletionSource<BScanRasterResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            PendingRegions.Add(new(request, cancellationToken, completion));
            cancellationToken.Register(() => completion.TrySetResult(BScanRasterResult.Cancelled()));
            return new(completion.Task);
        }
        internal sealed record PendingRegion(BScanRenderRequest Request, CancellationToken CancellationToken,
            TaskCompletionSource<BScanRasterResult> Completion);
        internal sealed class LockedList<T> : IReadOnlyList<T>
        {
            private readonly object _gate = new();
            private readonly List<T> _items = [];
            public int Count { get { lock (_gate) return _items.Count; } }
            public T this[int index] { get { lock (_gate) return _items[index]; } }
            public T this[Index index] { get { lock (_gate) return _items[index]; } }
            internal void Add(T item) { lock (_gate) _items.Add(item); }
            public IEnumerator<T> GetEnumerator()
            { lock (_gate) return ((IEnumerable<T>)[.. _items]).GetEnumerator(); }
            System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
        }
        private sealed class Subscription(Action dispose) : IDisposable { public void Dispose() => dispose(); }
    }
}
