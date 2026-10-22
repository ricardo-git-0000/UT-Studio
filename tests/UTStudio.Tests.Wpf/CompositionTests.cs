using System.Windows;
using System.Windows.Threading;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.Buffers;
using System.Threading.Channels;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using UTStudio.Acquisition.Simulator;
using UTStudio.Application;
using UTStudio.Inspection.Core;
using UTStudio.App.Wpf;
using UTStudio.App.Wpf.Composition;
using UTStudio.App.Wpf.Services;
using UTStudio.Contracts.Acquisition;
using UTStudio.Contracts.Application;
using UTStudio.Contracts.Inspection;
using UTStudio.Contracts.Processing;
using UTStudio.Domain.Acquisition;
using UTStudio.Presentation;
using UTStudio.Visualization.Core;
using UTStudio.Visualization.Core.Scan2D;
using UTStudio.Visualization.Wpf.Controls;
using UTStudio.Visualization.Wpf.Rendering;

namespace UTStudio.Tests.Wpf;

[TestClass]
public sealed class CompositionTests
{
    [TestMethod]
    public Task Productive_gate_vertical_apply_stop_and_close_is_owned_and_repeatable() => StaTest.Run(async () =>
    {
        var lifecycle = new List<string>();
        var source = new GateControlledSource();
        var configuration = new ConventionalAcquisitionConfiguration(new(0), 16, 1_000_000, 0, UtSignalMode.Rf);
        DesktopRuntime runtime = await DesktopRuntimeFactory.CreateAsync(
            new WpfUiDispatcher(Dispatcher.CurrentDispatcher), source, configuration,
            new BScanOptions(4_000_000, 8, 2, 2), TimeProvider.System, lifecycleObserver: lifecycle.Add);
        bool shutdownCompleted = false;
        try
        {
            static async Task ExecuteWhenEnabled(IAsyncRelayCommand command)
            {
                if (!command.CanExecute(null))
                {
                    var enabled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    void Changed(object? _, EventArgs __)
                    { if (command.CanExecute(null)) enabled.TrySetResult(); }
                    command.CanExecuteChanged += Changed;
                    try { await enabled.Task.WaitAsync(TimeSpan.FromSeconds(5)); }
                    finally { command.CanExecuteChanged -= Changed; }
                }
                await command.ExecuteAsync(null).WaitAsync(TimeSpan.FromSeconds(10));
            }
            var services = runtime.Host.Services;
            var window = services.GetRequiredService<MainWindow>();
            var host = services.GetRequiredService<AScanHostViewModel>();
            ProductiveGateInspectionScope scope = services.GetRequiredService<ProductiveGateInspectionScope>();
            window.Show(); window.AScan.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent, window.AScan));
            window.UpdateLayout();
            await runtime.Host.StartAsync();
            await host.StartCommand.ExecuteAsync(null);
            GateEditorViewCoordinator editor = window.AScan.ViewModel!.GateEditor!;
            var visualReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            void ObserveEditor(object? _, System.ComponentModel.PropertyChangedEventArgs __)
            {
                if (editor.CompositeVisual?.HasAuthoritativeIdentity == true) visualReady.TrySetResult();
            }
            editor.PropertyChanged += ObserveEditor;
            GateControlledSource.FrameOwner frame = source.QueueFrame();
            await Task.WhenAll(frame.Returned.Task, visualReady.Task).WaitAsync(TimeSpan.FromSeconds(5));
            editor.PropertyChanged -= ObserveEditor;

            await ExecuteWhenEnabled(editor.BeginEditCommand);
            await ExecuteWhenEnabled(editor.PreviewCommand);
            if (editor.State.Phase != GateEditorViewPhase.Previewed)
            {
                var previewed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                void PreviewChanged(object? _, System.ComponentModel.PropertyChangedEventArgs __)
                { if (editor.State.Phase == GateEditorViewPhase.Previewed) previewed.TrySetResult(); }
                editor.PropertyChanged += PreviewChanged;
                try
                {
                    await previewed.Task.WaitAsync(TimeSpan.FromSeconds(5));
                }
                catch (TimeoutException error)
                {
                    throw new TimeoutException($"Preview did not stabilize: phase={editor.State.Phase}; " +
                        $"editor={editor.State.Editor.State}; overlay={editor.State.Overlay.Status}; " +
                        $"source={editor.State.Overlay.Source?.Kind}.", error);
                }
                finally { editor.PropertyChanged -= PreviewChanged; }
            }
            var authorityCommitted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var before = scope.Authority.Read(scope.AuthorityKey).Committed.Revision;
            using IDisposable authoritySubscription = scope.Authority.Observe(scope.AuthorityKey).Subscribe(
                new ActionObserver<GatePlanAuthoritySnapshot>(snapshot =>
                {
                    if (snapshot.Committed.Revision.Value == before.Value + 1) authorityCommitted.TrySetResult();
                }));
            Task applyWaiter = editor.ApplyCommand.ExecuteAsync(null);
            if (editor.ApplyCommand.IsRunning) editor.ApplyCommand.Cancel();
            await applyWaiter.WaitAsync(TimeSpan.FromSeconds(5));
            if (scope.Authority.Read(scope.AuthorityKey).Committed.Revision.Value != before.Value + 1)
                await authorityCommitted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            if (editor.State.Phase != GateEditorViewPhase.Committed)
            {
                var converged = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                void Converged(object? _, System.ComponentModel.PropertyChangedEventArgs __)
                { if (editor.State.Phase == GateEditorViewPhase.Committed) converged.TrySetResult(); }
                editor.PropertyChanged += Converged;
                try { await converged.Task.WaitAsync(TimeSpan.FromSeconds(5)); }
                finally { editor.PropertyChanged -= Converged; }
            }
            Assert.AreEqual(GateEditorViewPhase.Committed, editor.State.Phase);
            await host.StopCommand.ExecuteAsync(null);
            Assert.AreEqual(SessionPhase.Idle, services.GetRequiredService<ApplicationSession>().Snapshot.Phase);

            Task first = runtime.Shutdown.ShutdownAsync();
            Assert.AreSame(first, runtime.Shutdown.ShutdownAsync());
            await first.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.IsTrue(runtime.Shutdown.Status.Completed);
            shutdownCompleted = true;
            Assert.AreEqual(1, source.DisposeCount);
            CollectionAssert.AreEqual(new[]
            {
                "phase5.stop", "phase6.wait", "phase6.drain", "phase7.dispose", "host.stop", "host.dispose"
            }, lifecycle);
        }
        finally { if (!shutdownCompleted) await runtime.Shutdown.ShutdownAsync(); }
    }, TimeSpan.FromSeconds(40));

    [TestMethod]
    public Task ProductiveGateInspectionScopeIsTheSingleRegisteredGateGraph() => StaTest.Run(async () =>
    {
        var runtime = await DesktopRuntime.CreateAsync(new WpfUiDispatcher(Dispatcher.CurrentDispatcher));
        try
        {
            ProductiveGateInspectionScope scope = runtime.Host.Services.GetRequiredService<ProductiveGateInspectionScope>();
            ApplicationSession session = runtime.Host.Services.GetRequiredService<ApplicationSession>();
            AScanComponentContext context = runtime.Host.Services.GetRequiredService<AScanComponentContext>();
            Assert.AreSame(session, runtime.Host.Services.GetRequiredService<IApplicationSession>());
            Assert.IsNull(runtime.Host.Services.GetService<ICommittedGateProcessingIngress>());
            Assert.AreSame(scope.Authority, context.GateEditing!.Authority);
            Assert.AreSame(scope.PreviewCompiler, context.GateEditing.PreviewCompiler);
            Assert.AreSame(scope.GeometryEvaluator, context.GateEditing.GeometryEvaluator);
            Assert.AreSame(session, context.GateEditing.Apply);
            Assert.AreEqual(scope.AuthorityKey, context.GateEditing.AuthorityKey);
            Assert.IsNull(runtime.Host.Services.GetService<ProductiveGateProcessing>());
        }
        finally { await runtime.Shutdown.ShutdownAsync(); }
    });

    [TestMethod]
    [DataRow("visual")]
    [DataRow("host")]
    public Task PartialStartupRetainsOwnerAndCleansCreatedResources(string stage) => StaTest.Run(async () =>
    {
        var failure = new InvalidOperationException("startup checkpoint");
        var runtime = await DesktopRuntime.CreateAsync(new WpfUiDispatcher(Dispatcher.CurrentDispatcher), null,
            new ManualClock(), current => { if (current == stage) { throw failure; } });
        Assert.AreSame(failure, runtime.InitializationError);
        Task first = runtime.Shutdown.ShutdownAsync();
        await first;
        Assert.IsTrue(runtime.Shutdown.Status.Completed);
        Assert.AreSame(first, runtime.Shutdown.ShutdownAsync());
    });

    [TestMethod]
    public Task HistoricalProducerFailureDoesNotPreventSafeHostDisposal() => StaTest.Run(async () =>
    {
        var clock = new ManualClock { FailNextTimer = true };
        var runtime = await DesktopRuntime.CreateAsync(new WpfUiDispatcher(Dispatcher.CurrentDispatcher), clock: clock);
        var session = runtime.Host.Services.GetRequiredService<ApplicationSession>();
        var source = runtime.Host.Services.GetRequiredService<SimulatorUtFrameSource>();
        try
        {
            await session.StartAsync(SimulatorUtFrameSource.DefaultConfiguration, new UTStudio.Domain.Acquisition.AcquisitionRunId(Guid.NewGuid()));
        }
        catch (InvalidOperationException) { } // A fast producer failure may race the start acknowledgement.
        await clock.TimerFailed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await runtime.Shutdown.ShutdownAsync();
        Assert.AreEqual(SessionPhase.Disposed, session.Snapshot.Phase);
        Assert.AreEqual(UTStudio.Domain.Acquisition.UtConnectionState.Disconnected, source.State.Connection);
        Assert.IsNotNull(session.Snapshot.PrimaryError);
        Assert.IsTrue(runtime.Shutdown.Status.Completed);
    });

    [TestMethod]
    public Task StartupDiagnosticRemainsVisibleUntilCleanupIsConfirmed() => StaTest.Run(async () =>
    {
        var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var coordinator = new ShutdownCoordinator([new("held", () => held.Task)],
            Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance, new ManualClock());
        var window = new StartupFailureWindow(new InvalidOperationException("startup"), coordinator);
        window.Show();
        Task cleanup = coordinator.ShutdownAsync();
        window.Close();
        Assert.IsTrue(window.IsVisible);
        held.SetResult();
        await cleanup;
        window.ConfirmCleanup();
        window.Close();
        Assert.IsFalse(window.IsVisible);
    });

    [TestMethod]
    public Task ContainerResolvesOneInstanceAndWindowDataContext() => StaTest.Run(async () =>
    {
        var runtime = await DesktopRuntime.CreateAsync(new WpfUiDispatcher(Dispatcher.CurrentDispatcher));
        try
        {
            var services = runtime.Host.Services;
            Assert.AreSame(services.GetRequiredService<SimulatorUtFrameSource>(), services.GetRequiredService<IUtFrameSource>());
            Assert.AreSame(services.GetRequiredService<ApplicationSession>(), services.GetRequiredService<IApplicationSession>());
            Assert.IsNotNull(services.GetRequiredService<InspectionDataStoreRegistry>());
            Assert.IsNotNull(services.GetRequiredService<BScanIngress>());
            Assert.AreSame(services.GetRequiredService<AScanVisualDelivery>(), services.GetRequiredService<IObservable<AScanSnapshot>>());
            var vm = services.GetRequiredService<AScanHostViewModel>();
            var session = services.GetRequiredService<IApplicationSession>();
            var window = services.GetRequiredService<MainWindow>();
            Assert.AreSame(window, services.GetRequiredService<MainWindow>());
            Assert.AreSame(vm, window.DataContext);
            window.Show();
            await runtime.Host.StartAsync();
            await vm.StartCommand.ExecuteAsync(null);
            Assert.AreEqual(SessionPhase.Running, services.GetRequiredService<IApplicationSession>().Snapshot.Phase);
            await DiagnosticWait.Until(() => window.AScan.ViewModel?.Session.Phase == SessionPhase.Running,
                "host session state forwarded to AScanView");
            Assert.AreEqual(vm.Session.Version, window.AScan.ViewModel!.Session.Version);
            await runtime.Shutdown.ShutdownAsync();
            Assert.AreEqual(SessionPhase.Disposed, session.Snapshot.Phase);
        }
        finally { await runtime.Shutdown.ShutdownAsync(); }
    });

    [TestMethod]
    public Task Productive_simulator_started_before_opening_bscan_presents_live_raster() => StaTest.Run(async () =>
    {
        var runtime = await DesktopRuntime.CreateAsync(new WpfUiDispatcher(Dispatcher.CurrentDispatcher),
            ["--Simulator:AScansPerSecond=100"]);
        var services = runtime.Host.Services;
        var host = services.GetRequiredService<AScanHostViewModel>();
        var mainWindow = services.GetRequiredService<MainWindow>();
        mainWindow.AScan.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent, mainWindow.AScan));
        var aScan = mainWindow.AScan.ViewModel!;
        var windows = services.GetRequiredService<BScanWindowManager>();
        try
        {
            await runtime.Host.StartAsync();
            await host.StartCommand.ExecuteAsync(null);
            await DiagnosticWait.Until(() => aScan.AScan is not null, "productive simulator A-Scan");

            await windows.OpenAsync();
            BScanWindow window = windows.ActiveWindow!;
            BScanViewModel bScan = window.Component.ViewModel!;
            await DiagnosticWait.Until(() => bScan.State.Region is not null, "productive simulator B-Scan raster");
            BScanControl control = window.Component.Renderer;

            Assert.AreEqual(BScanViewPhase.Ready, bScan.State.Phase);
            Assert.IsNull(bScan.State.BScanIngressError);
            Assert.IsNull(bScan.State.BScanVisualError);
            Assert.IsNotNull(bScan.State.Region);
            Assert.IsNotNull(control.RasterSource);
            Assert.IsNotNull(control.Snapshot);
            await DiagnosticWait.Until(() => control.RasterSource is BitmapSource { PixelWidth: > 1, PixelHeight: > 1 },
                "productive simulator sized B-Scan raster");
            AssertRasterContainsSignal(control);

            await DiagnosticWait.Until(() => aScan.AScan?.Sequence >= 100,
                "productive simulator mature B-Scan store");
            await window.CloseOwnedAsync();
            await windows.OpenAsync();
            BScanWindow reopened = windows.ActiveWindow!;
            BScanViewModel reopenedViewModel = reopened.Component.ViewModel!;
            BScanControl reopenedControl = reopened.Component.Renderer;
            await DiagnosticWait.Until(() => reopenedViewModel.State.Region is not null &&
                reopenedControl.RasterSource is not null, "reopened productive simulator B-Scan raster");
            await DiagnosticWait.Until(() => reopenedControl.RasterSource is BitmapSource
            { PixelWidth: > 1, PixelHeight: > 1 }, "reopened productive simulator sized B-Scan raster");
            AssertRasterContainsSignal(reopenedControl);
        }
        finally { await runtime.Shutdown.ShutdownAsync(); }
    });

    [TestMethod]
    [Timeout(30_000, CooperativeCancellation = true)]
    public Task Open_bilinear_bscan_keeps_advancing_during_continuous_productive_simulation() => StaTest.Run(async () =>
    {
        var runtime = await DesktopRuntime.CreateAsync(new WpfUiDispatcher(Dispatcher.CurrentDispatcher),
            ["--Simulator:AScansPerSecond=100"]);
        var services = runtime.Host.Services;
        var host = services.GetRequiredService<AScanHostViewModel>();
        var mainWindow = services.GetRequiredService<MainWindow>();
        mainWindow.AScan.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent, mainWindow.AScan));
        var aScan = mainWindow.AScan.ViewModel!;
        var windows = services.GetRequiredService<BScanWindowManager>();
        try
        {
            await runtime.Host.StartAsync();
            await windows.OpenAsync();
            BScanWindow window = windows.ActiveWindow!;
            BScanViewModel bScan = window.Component.ViewModel!;
            BScanControl control = window.Component.Renderer;
            bScan.SetResamplingMode(Scan2DInterpolationMode.Bilinear);
            await host.StartCommand.ExecuteAsync(null);
            try
            {
                await DiagnosticWait.Until(() => control.AppliedSnapshot?.StoreRevision >= 450,
                    "open Bilinear B-Scan reaches revision 450");
            }
            catch (TimeoutException error)
            {
                throw new AssertFailedException($"sequence={aScan.AScan?.Sequence}; " +
                    $"ingress={services.GetRequiredService<BScanIngress>().CurrentVisualState?.StoreState.StoreRevision}; " +
                    $"view={bScan.State.StoreRevision}; displayed={bScan.State.DisplayedRevision?.StoreRevision}; " +
                    $"region={bScan.State.Region?.Snapshot.StoreRevision}; control={control.AppliedSnapshot?.StoreRevision}; " +
                    $"regionSize={bScan.State.Region?.Snapshot.RasterWidth}x{bScan.State.Region?.Snapshot.RasterHeight}; " +
                    $"regionConfig={bScan.State.Region?.Snapshot.RenderConfigurationVersion}; " +
                    $"full={bScan.State.Region?.Snapshot.RequiresFullRefresh}; reason={bScan.State.Region?.Snapshot.FullRefreshReason}; " +
                    $"request={services.GetRequiredService<BScanVisualDelivery>().GetLastRequest(bScan.ConsumerId)}; " +
                    $"pending={bScan.State.IsRenderPending}; visual={bScan.State.BScanVisualError}; " +
                    $"ui={bScan.State.UiInteractionError}; reject={control.LastIncrementalRejection}; " +
                    $"delivery={services.GetRequiredService<BScanVisualDelivery>().Diagnostics}", error);
            }
            ulong first = control.AppliedSnapshot!.StoreRevision;
            ulong configuration = control.AppliedSnapshot.RenderConfigurationVersion;
            await DiagnosticWait.Until(() => aScan.AScan?.Sequence >= first + 100,
                "productive simulator advances 100 further traces");
            await DiagnosticWait.Until(() => control.AppliedSnapshot?.StoreRevision >= first + 75,
                $"B-Scan catches up after revision {first}; ingress=" +
                $"{services.GetRequiredService<BScanIngress>().CurrentVisualState?.StoreState.StoreRevision}; " +
                $"view={bScan.State.StoreRevision}; displayed={bScan.State.DisplayedRevision?.StoreRevision}; " +
                $"region={bScan.State.Region?.Snapshot.StoreRevision}; pending={bScan.State.IsRenderPending}; " +
                $"error={bScan.State.BScanVisualError}; ui={bScan.State.UiInteractionError}");
            Assert.AreEqual(configuration, control.AppliedSnapshot!.RenderConfigurationVersion);
            Assert.IsGreaterThan(0UL,
                services.GetRequiredService<BScanVisualDelivery>().Diagnostics.IncrementalUpdatesBuilt);
        }
        finally { await runtime.Shutdown.ShutdownAsync(); }
    }, TimeSpan.FromSeconds(30));

    private static void AssertRasterContainsSignal(BScanControl control)
    {
        var bitmap = (WriteableBitmap)control.RasterSource!;
        byte[] pixels = new byte[bitmap.BackBufferStride * bitmap.PixelHeight];
        bitmap.CopyPixels(pixels, bitmap.BackBufferStride, 0);
        var colors = Enumerable.Range(0, pixels.Length / 4)
            .Select(index => (B: pixels[index * 4], G: pixels[index * 4 + 1],
                R: pixels[index * 4 + 2], A: pixels[index * 4 + 3]))
            .Distinct()
            .Take(3)
            .ToArray();
        Assert.IsGreaterThanOrEqualTo(2, colors.Length, "B-Scan raster is uniformly colored.");
        var noData = BScanPalette.GetColor(BScanBipolarIntensity8.NoData);
        Assert.IsTrue(colors.Any(color => color != (noData.B, noData.G, noData.R, noData.A)));
    }

    [TestMethod]
    public Task Internal_factory_composes_controlled_source_and_small_productive_ring() => StaTest.Run(async () =>
    {
        var source = new ControlledSource();
        var clock = new ManualClock();
        var configuration = new ConventionalAcquisitionConfiguration(new(0), 16, 1_000_000, 0, UtSignalMode.Rf);
        var options = new BScanOptions(1_000_000, 8, 2, 2);
        var runtime = await DesktopRuntimeFactory.CreateAsync(new WpfUiDispatcher(Dispatcher.CurrentDispatcher),
            source, configuration, options, clock);
        try
        {
            Assert.AreSame(source, runtime.Host.Services.GetRequiredService<IUtFrameSource>());
            Assert.AreEqual(8, runtime.Host.Services.GetRequiredService<BScanIngress>().Options.MaximumColumns);
            Assert.IsNotNull(runtime.Host.Services.GetRequiredService<BScanWindowManager>());
        }
        finally { await runtime.Shutdown.ShutdownAsync(); }
        Assert.AreEqual(1, source.DisposeCount);
    });

    [TestMethod]
    public Task Productive_factory_observes_complete_shutdown_phase_order() => StaTest.Run(async () =>
    {
        var source = new ControlledSource();
        var events = new List<string>();
        var configuration = new ConventionalAcquisitionConfiguration(new(0), 16, 1_000_000, 0, UtSignalMode.Rf);
        var runtime = await DesktopRuntimeFactory.CreateAsync(new WpfUiDispatcher(Dispatcher.CurrentDispatcher),
            source, configuration, new BScanOptions(4_000_000, 8, 2, 2), new ManualClock(),
            lifecycleObserver: events.Add);

        await runtime.Shutdown.ShutdownAsync();

        CollectionAssert.AreEqual(new[]
        {
            "phase5.stop", "phase6.wait", "phase6.drain", "phase7.dispose", "host.stop", "host.dispose"
        }, events);
        Assert.IsTrue(runtime.Shutdown.Status.Completed);
        Assert.AreEqual(1, source.DisposeCount);
    });

    [TestMethod]
    public Task Productive_shutdown_waits_for_raster_owner_before_disposal_and_host() => StaTest.Run(async () =>
    {
        var source = new ControlledSource();
        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var drainEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var drained = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var disposed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var configuration = new ConventionalAcquisitionConfiguration(new(0), 16, 1_000_000, 0, UtSignalMode.Rf);
        var runtime = await DesktopRuntimeFactory.CreateAsync(new WpfUiDispatcher(Dispatcher.CurrentDispatcher),
            source, configuration, new BScanOptions(4_000_000, 8, 2, 2), new ManualClock(),
            lifecycleObserver: phase =>
            {
                if (phase == "phase5.stop") { stopped.TrySetResult(); }
                else if (phase == "phase6.wait") { drainEntered.TrySetResult(); }
                else if (phase == "phase6.drain") { drained.TrySetResult(); }
                else if (phase == "phase7.dispose") { disposed.TrySetResult(); }
            });
        var services = runtime.Host.Services;
        var ingress = services.GetRequiredService<BScanIngress>();
        var delivery = services.GetRequiredService<BScanVisualDelivery>();
        var run = new AcquisitionRunId(Guid.NewGuid());
        var stores = services.GetRequiredService<InspectionDataStoreRegistry>();
        await using IInspectionRunWriter writer = stores.OpenRun(run);
        ingress.OpenRun(run);
        var stream = new UtStreamIdentity(stores.InspectionId, run, source.SourceId,
            configuration.PhysicalChannelId);
        var identity = new TraceIdentity(stream, 0);
        var descriptor = new TraceDescriptor(identity, 0, TimeSpan.Zero, DateTimeOffset.UnixEpoch,
            null, 16, 1_000_000, 0, UtSignalMode.Rf);
        InspectionAppendResult commit = await writer.CommitAsync(new InspectionCommitInput(
            new InspectionTraceInput(descriptor, new short[16])));
        ingress.NotifyCommitted(identity, commit.Revision);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (ingress.CurrentVisualState?.StoreState.StoreRevision != commit.Revision.Value)
        { await Task.Delay(10, timeout.Token); }
        BScanRasterRegion owner = (await delivery.GetRegionAsync(new(run,
            BScanSpatialViewportOperations.Reset(new(0, 0)),
            ScanTimeViewportOperations.Create(0, .000016), 1, 1, 1))).Region!;
        Task shutdown = runtime.Shutdown.ShutdownAsync();
        await stopped.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await drainEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.IsFalse(shutdown.IsCompleted);
        Assert.IsFalse(drained.Task.IsCompleted);
        Assert.IsFalse(runtime.Shutdown.Status.Completed);
        Assert.AreEqual(0, source.DisposeCount);
        Assert.AreEqual(1, delivery.Diagnostics.ActiveRasterOwners);

        owner.Dispose();
        await drained.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await disposed.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await shutdown.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.IsTrue(runtime.Shutdown.Status.Completed);
        Assert.AreEqual(0, delivery.Diagnostics.ActiveRasterOwners);
        Assert.AreEqual(BScanVisualCloseState.Closed, delivery.Diagnostics.CloseState);
        Assert.AreEqual(1, source.DisposeCount);
    });

    [TestMethod]
    public Task NavigationKeyBindingsTargetTheExactViewportCommands() => StaTest.Run(async () =>
    {
        var runtime = await DesktopRuntime.CreateAsync(new WpfUiDispatcher(Dispatcher.CurrentDispatcher));
        try
        {
            var window = runtime.Host.Services.GetRequiredService<MainWindow>();
            window.Show();
            window.UpdateLayout();
            var vm = window.AScan.ViewModel!;
            AssertBinding(Key.Home, ModifierKeys.None, vm.ResetViewportCommand);
            AssertBinding(Key.Add, ModifierKeys.None, vm.ZoomInCommand);
            AssertBinding(Key.Subtract, ModifierKeys.None, vm.ZoomOutCommand);
            AssertBinding(Key.Left, ModifierKeys.Alt, vm.PanLeftCommand);
            AssertBinding(Key.Right, ModifierKeys.Alt, vm.PanRightCommand);

            void AssertBinding(Key key, ModifierKeys modifiers, System.Windows.Input.ICommand expected)
            {
                var binding = window.AScan.InputBindings.OfType<KeyBinding>().Single(item =>
                    item.Key == key && item.Modifiers == modifiers);
                Assert.AreSame(expected, binding.Command);
            }
        }
        finally { await runtime.Shutdown.ShutdownAsync(); }
    });

    [TestMethod]
    public Task BScan_window_activates_single_instance_closes_and_reopens_with_new_consumer() => StaTest.Run(async () =>
    {
        var runtime = await DesktopRuntime.CreateAsync(new WpfUiDispatcher(Dispatcher.CurrentDispatcher));
        try
        {
            var manager = runtime.Host.Services.GetRequiredService<BScanWindowManager>();
            await manager.OpenAsync();
            BScanWindow first = manager.ActiveWindow!;
            string firstConsumer = first.ConsumerId;
            await manager.OpenAsync();
            Assert.AreSame(first, manager.ActiveWindow);
            Assert.AreEqual(1, manager.CreationCount);

            await first.CloseOwnedAsync();
            Assert.IsNull(manager.ActiveWindow);
            await manager.OpenAsync();
            BScanWindow second = manager.ActiveWindow!;
            Assert.AreNotSame(first, second);
            Assert.AreNotEqual(firstConsumer, second.ConsumerId);
            Assert.AreEqual(2, manager.CreationCount);

            await runtime.Shutdown.ShutdownAsync();
            Assert.IsNull(manager.ActiveWindow);
        }
        finally { await runtime.Shutdown.ShutdownAsync(); }
    });

    [TestMethod]
    public Task Failed_BScan_Show_releases_window_viewmodel_and_consumer() => StaTest.Run(async () =>
    {
        var runtime = await DesktopRuntime.CreateAsync(new WpfUiDispatcher(Dispatcher.CurrentDispatcher));
        try
        {
            var services = runtime.Host.Services;
            var delivery = services.GetRequiredService<BScanVisualDelivery>();
            var manager = new BScanWindowManager(delivery,
                services.GetRequiredService<SharedScanTimeViewport>(),
                services.GetRequiredService<TraceSelectionScope>(),
                Dispatcher.CurrentDispatcher, null, null, null,
                _ => throw new InvalidOperationException("show failed"));

            InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(() => manager.OpenAsync());

            Assert.AreEqual("show failed", error.Message);
            Assert.IsNull(manager.ActiveWindow);
            Assert.AreEqual(0, delivery.ActiveConsumerCount);
            Assert.AreEqual(0, delivery.PendingBootstrapCount);
            Assert.AreEqual(0, delivery.RetainedSnapshotCount);
        }
        finally { await runtime.Shutdown.ShutdownAsync(); }
    });

    [TestMethod]
    public Task BScan_window_close_is_idempotent_through_public_component_context() => StaTest.Run(async () =>
    {
        var runtime = await DesktopRuntime.CreateAsync(new WpfUiDispatcher(Dispatcher.CurrentDispatcher));
        BScanWindow? window = null;
        try
        {
            var services = runtime.Host.Services;
            var delivery = services.GetRequiredService<BScanVisualDelivery>();
            window = new BScanWindow(new(delivery, services.GetRequiredService<SharedScanTimeViewport>(),
                services.GetRequiredService<TraceSelectionScope>()));
            window.Show();
            await window.Component.Activation;

            Task first = window.CloseOwnedAsync();
            Task second = window.CloseOwnedAsync();
            await first;

            Assert.AreSame(first, second);
            Assert.IsFalse(window.IsVisible);
            Assert.AreEqual(0, delivery.ActiveConsumerCount);
        }
        finally
        {
            if (window?.IsVisible == true) { window.Close(); }
            await runtime.Shutdown.ShutdownAsync();
        }
    });

    [TestMethod]
    public Task BScan_window_reopens_with_a_new_public_context_after_close() => StaTest.Run(async () =>
    {
        var runtime = await DesktopRuntime.CreateAsync(new WpfUiDispatcher(Dispatcher.CurrentDispatcher));
        try
        {
            var services = runtime.Host.Services;
            var delivery = services.GetRequiredService<BScanVisualDelivery>();
            BScanComponentContext Context() => new(delivery,
                services.GetRequiredService<SharedScanTimeViewport>(), services.GetRequiredService<TraceSelectionScope>());
            var first = new BScanWindow(Context());
            first.Show();
            await first.Component.Activation;
            string firstConsumer = first.ConsumerId;
            await first.CloseOwnedAsync();
            var second = new BScanWindow(Context());
            second.Show();
            await second.Component.Activation;
            Assert.AreNotEqual(firstConsumer, second.ConsumerId);
            await second.CloseOwnedAsync();
            Assert.AreEqual(0, delivery.ActiveConsumerCount);
        }
        finally { await runtime.Shutdown.ShutdownAsync(); }
    });

    [TestMethod]
    public Task Shutdown_started_off_ui_blocks_entire_shell_before_cleanup() => StaTest.Run(async () =>
    {
        var runtime = await DesktopRuntime.CreateAsync(new WpfUiDispatcher(Dispatcher.CurrentDispatcher));
        var window = runtime.Host.Services.GetRequiredService<MainWindow>();
        var manager = runtime.Host.Services.GetRequiredService<BScanWindowManager>();
        var delivery = runtime.Host.Services.GetRequiredService<BScanVisualDelivery>();
        window.Show();
        await manager.OpenAsync(window);
        Assert.IsNotNull(manager.ActiveWindow);
        Assert.AreEqual(1, delivery.ActiveConsumerCount);
        Task shutdown = Task.Run(() => runtime.Shutdown.ShutdownAsync());

        await DiagnosticWait.Until(() => !window.IsEnabled, "shell interaction gate");
        await shutdown;

        Assert.IsFalse(window.IsEnabled);
        Assert.IsTrue(runtime.Shutdown.Status.Completed);
        Assert.IsNull(manager.ActiveWindow);
        Assert.AreEqual(0, delivery.ActiveConsumerCount);
        window.Close();
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Assert.IsFalse(window.IsVisible);
    });

    [TestMethod]
    public Task DispatcherRunsOnStaPropagatesExceptionAndHonorsCancellation() => StaTest.Run(async () =>
    {
        var dispatcher = new WpfUiDispatcher(Dispatcher.CurrentDispatcher);
        int thread = Environment.CurrentManagedThreadId;
        await Task.Run(() => dispatcher.InvokeAsync(() =>
        {
            Assert.IsTrue(dispatcher.CheckAccess());
            Assert.AreEqual(thread, Environment.CurrentManagedThreadId);
        }));
        await Assert.ThrowsAsync<InvalidOperationException>(() => dispatcher.InvokeAsync(() => throw new InvalidOperationException()));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        bool executed = false;
        await Assert.ThrowsAsync<TaskCanceledException>(() => dispatcher.InvokeAsync(() => executed = true, cancellation.Token));
        Assert.IsFalse(executed);
    });

    [TestMethod]
    public Task FirstClosingIsCancelledUntilCleanupAndRepeatedCloseDoesNotReenter() => StaTest.Run(async () =>
    {
        var runtime = await DesktopRuntime.CreateAsync(new WpfUiDispatcher(Dispatcher.CurrentDispatcher));
        var window = runtime.Host.Services.GetRequiredService<MainWindow>();
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int cancelled = 0;
        window.Closing += (_, args) => { if (args.Cancel) { cancelled++; } };
        window.Closed += (_, _) => closed.TrySetResult();
        window.Show();
        await runtime.Host.StartAsync();
        await ((AScanHostViewModel)window.DataContext).StartCommand.ExecuteAsync(null);
        window.Close();
        window.Close();
        Assert.IsTrue(window.IsVisible);
        await closed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.IsGreaterThanOrEqualTo(2, cancelled);
        Assert.IsTrue(runtime.Shutdown.Status.Completed);
    });

    private sealed class ActionObserver<T>(Action<T> next) : IObserver<T>
    {
        public void OnNext(T value) => next(value);
        public void OnError(Exception error) => Assert.Fail(error.ToString());
        public void OnCompleted() { }
    }

    private sealed class GateControlledSource : IUtFrameSource
    {
        private readonly Channel<ConventionalUtFrame> _frames = Channel.CreateBounded<ConventionalUtFrame>(1);
        private readonly TaskCompletionSource _producer = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private ConventionalUtFrameMetadata? _metadata;
        private ConventionalAcquisitionConfiguration? _configuration;
        private FrameOwner? _owner;
        private bool _stopped;
        public UtSourceId SourceId { get; } = new("gate-controlled-wpf");
        public UtSourceCapabilities Capabilities { get; } = new([new PhysicalChannelId(0)], 64, 1_000_000, true);
        public UtSourceState State { get; private set; } = new(0, UtConnectionState.Disconnected, UtAcquisitionState.Idle);
        internal int DisposeCount { get; private set; }

        public Task ConnectAsync(CancellationToken cancellationToken = default)
        { State = new(1, UtConnectionState.Connected, UtAcquisitionState.Idle); return Task.CompletedTask; }
        public Task ConfigureAsync(ConventionalAcquisitionConfiguration configuration,
            CancellationToken cancellationToken = default)
        { _configuration = configuration; return Task.CompletedTask; }
        public Task<UtAcquisitionRun> StartAsync(AcquisitionRunId runId, CancellationToken cancellationToken = default)
        {
            _metadata = new(SourceId, runId, _configuration!, DateTimeOffset.UnixEpoch);
            State = new(2, UtConnectionState.Connected, UtAcquisitionState.Running);
            return Task.FromResult(new UtAcquisitionRun(_metadata, _frames.Reader, _producer.Task, _released.Task));
        }
        internal FrameOwner QueueFrame()
        {
            var owner = _owner = new FrameOwner(_metadata!.Configuration.SampleCount, ReleaseOwner);
            for (int index = 0; index < owner.Memory.Length; index++) owner.Memory.Span[index] = (short)(index * 100);
            if (!_frames.Writer.TryWrite(new(_metadata, 0, 0, TimeSpan.Zero, null, owner)))
                throw new InvalidOperationException("The controlled frame was not admitted.");
            return owner;
        }
        public Task StopAsync(CancellationToken cancellationToken = default)
        {
            _stopped = true; _frames.Writer.TryComplete(); _producer.TrySetResult();
            if (_owner?.IsReturned != false) _released.TrySetResult();
            State = new(3, UtConnectionState.Connected, UtAcquisitionState.Idle);
            return Task.CompletedTask;
        }
        private void ReleaseOwner()
        { if (_stopped) _released.TrySetResult(); }
        public Task DisconnectAsync(CancellationToken cancellationToken = default)
        { State = new(4, UtConnectionState.Disconnected, UtAcquisitionState.Idle); return Task.CompletedTask; }
        public ValueTask DisposeAsync() { DisposeCount++; return ValueTask.CompletedTask; }

        internal sealed class FrameOwner(int count, Action returned) : IMemoryOwner<short>
        {
            private short[]? _samples = new short[count];
            internal TaskCompletionSource Returned { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
            internal bool IsReturned => _samples is null;
            public Memory<short> Memory => _samples ?? throw new ObjectDisposedException(nameof(FrameOwner));
            public void Dispose()
            {
                if (Interlocked.Exchange(ref _samples, null) is null) return;
                returned(); Returned.TrySetResult();
            }
        }
    }

    private sealed class ControlledSource : IUtFrameSource
    {
        public UtSourceId SourceId { get; } = new("controlled-wpf");
        public UtSourceCapabilities Capabilities { get; } = new([new PhysicalChannelId(0)], 64, 1_000_000, true);
        public UtSourceState State { get; private set; } = new(0, UtConnectionState.Disconnected, UtAcquisitionState.Idle);
        internal int DisposeCount { get; private set; }
        public Task ConnectAsync(CancellationToken cancellationToken = default)
        { State = new(1, UtConnectionState.Connected, UtAcquisitionState.Idle); return Task.CompletedTask; }
        public Task ConfigureAsync(ConventionalAcquisitionConfiguration configuration,
            CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<UtAcquisitionRun> StartAsync(AcquisitionRunId runId, CancellationToken cancellationToken = default) =>
            Task.FromException<UtAcquisitionRun>(new InvalidOperationException("No run requested by this composition test."));
        public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task DisconnectAsync(CancellationToken cancellationToken = default)
        { State = new(State.Version + 1, UtConnectionState.Disconnected, UtAcquisitionState.Idle); return Task.CompletedTask; }
        public ValueTask DisposeAsync() { DisposeCount++; return ValueTask.CompletedTask; }
    }

}
