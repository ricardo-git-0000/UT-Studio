using System.Windows.Threading;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using UTStudio.Acquisition.Simulator;
using UTStudio.Application;
using UTStudio.App.Wpf;
using UTStudio.App.Wpf.Composition;
using UTStudio.App.Wpf.Services;
using UTStudio.Contracts.Acquisition;
using UTStudio.Contracts.Application;
using UTStudio.Contracts.Presentation;
using UTStudio.Domain.Acquisition;
using UTStudio.Presentation;
using UTStudio.Visualization.Core;
using UTStudio.Visualization.Wpf.Controls;
using UTStudio.Visualization.Wpf.Rendering;

namespace UTStudio.Tests.Wpf;

[TestClass]
public sealed class CompositionTests
{
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
            Assert.AreSame(services.GetRequiredService<ConventionalFrameSinkFanOut>(), services.GetRequiredService<IConventionalFrameSink>());
            Assert.AreSame(services.GetRequiredService<AScanVisualDelivery>(), services.GetRequiredService<IObservable<AScanSnapshot>>());
            var vm = services.GetRequiredService<AScanViewModel>();
            var session = services.GetRequiredService<IApplicationSession>();
            var window = services.GetRequiredService<MainWindow>();
            Assert.AreSame(window, services.GetRequiredService<MainWindow>());
            Assert.AreSame(vm, window.DataContext);
            await runtime.Host.StartAsync();
            await vm.StartCommand.ExecuteAsync(null);
            Assert.AreEqual(SessionPhase.Running, services.GetRequiredService<IApplicationSession>().Snapshot.Phase);
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
        var aScan = services.GetRequiredService<AScanViewModel>();
        var windows = services.GetRequiredService<BScanWindowManager>();
        try
        {
            await runtime.Host.StartAsync();
            await aScan.StartCommand.ExecuteAsync(null);
            await DiagnosticWait.Until(() => aScan.AScan is not null, "productive simulator A-Scan");

            await windows.OpenAsync();
            BScanWindow window = windows.ActiveWindow!;
            var bScan = (BScanViewModel)window.DataContext;
            await DiagnosticWait.Until(() => bScan.State.Region is not null, "productive simulator B-Scan raster");
            var control = (BScanControl)window.FindName("Scan");

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
            var reopenedViewModel = (BScanViewModel)reopened.DataContext;
            var reopenedControl = (BScanControl)reopened.FindName("Scan");
            await DiagnosticWait.Until(() => reopenedViewModel.State.Region is not null &&
                reopenedControl.RasterSource is not null, "reopened productive simulator B-Scan raster");
            await DiagnosticWait.Until(() => reopenedControl.RasterSource is BitmapSource
            { PixelWidth: > 1, PixelHeight: > 1 }, "reopened productive simulator sized B-Scan raster");
            AssertRasterContainsSignal(reopenedControl);
        }
        finally { await runtime.Shutdown.ShutdownAsync(); }
    });

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
        var options = new BScanOptions(1_000_000, 1_000_000, 8, 2, 2);
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
            source, configuration, new BScanOptions(1_000_000, 4_000_000, 8, 2, 2), new ManualClock(),
            lifecycleObserver: events.Add);

        await runtime.Shutdown.ShutdownAsync();

        CollectionAssert.AreEqual(new[]
        {
            "phase5.stop", "phase6.drain", "phase7.dispose", "host.stop", "host.dispose"
        }, events);
        Assert.IsTrue(runtime.Shutdown.Status.Completed);
        Assert.AreEqual(1, source.DisposeCount);
    });

    [TestMethod]
    public Task Productive_shutdown_waits_for_raster_owner_before_disposal_and_host() => StaTest.Run(async () =>
    {
        var source = new ControlledSource();
        var configuration = new ConventionalAcquisitionConfiguration(new(0), 16, 1_000_000, 0, UtSignalMode.Rf);
        var runtime = await DesktopRuntimeFactory.CreateAsync(new WpfUiDispatcher(Dispatcher.CurrentDispatcher),
            source, configuration, new BScanOptions(1_000_000, 4_000_000, 8, 2, 2), new ManualClock());
        var services = runtime.Host.Services;
        var ingress = services.GetRequiredService<BScanIngress>();
        var delivery = services.GetRequiredService<BScanVisualDelivery>();
        var window = services.GetRequiredService<MainWindow>();
        var run = new AcquisitionRunId(Guid.NewGuid());
        var metadata = new ConventionalUtFrameMetadata(source.SourceId, run, configuration, DateTimeOffset.UnixEpoch);
        var indexed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ingress.AfterIndexCommit = _ => indexed.TrySetResult();
        ingress.OpenRun(metadata);
        ingress.Accept(metadata, 0, 0, TimeSpan.Zero, null, new short[16]);
        await indexed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        BScanRasterRegion owner = (await delivery.GetRegionAsync(new(run,
            BScanSpatialViewportOperations.Reset(new(0, 0)),
            ScanTimeViewportOperations.Create(0, .000016), 1, 1, 1))).Region!;
        window.Show();

        Task shutdown = runtime.Shutdown.ShutdownAsync();
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Assert.IsFalse(shutdown.IsCompleted);
        Assert.IsFalse(runtime.Shutdown.Status.Completed);
        Assert.IsTrue(window.IsVisible);
        Assert.AreEqual(0, source.DisposeCount);
        Assert.AreEqual(1, delivery.Diagnostics.ActiveRasterOwners);

        owner.Dispose();
        await shutdown.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.IsTrue(runtime.Shutdown.Status.Completed);
        Assert.AreEqual(0, delivery.Diagnostics.ActiveRasterOwners);
        Assert.AreEqual(BScanVisualCloseState.Closed, delivery.Diagnostics.CloseState);
        Assert.AreEqual(1, source.DisposeCount);
        window.Close();
    });

    [TestMethod]
    public Task Failed_block_return_keeps_productive_cleanup_unconfirmed_and_shell_visible() => StaTest.Run(async () =>
    {
        var source = new ControlledSource();
        var pool = new FailingTracePool();
        var configuration = new ConventionalAcquisitionConfiguration(new(0), 16, 1_000_000, 0, UtSignalMode.Rf);
        var runtime = await DesktopRuntimeFactory.CreateAsync(new WpfUiDispatcher(Dispatcher.CurrentDispatcher),
            source, configuration, new BScanOptions(1_000_000, 4_000_000, 8, 2, 2), new ManualClock(), pool);
        var services = runtime.Host.Services;
        var ingress = services.GetRequiredService<BScanIngress>();
        var window = services.GetRequiredService<MainWindow>();
        var run = new AcquisitionRunId(Guid.NewGuid());
        ingress.OpenRun(new(source.SourceId, run, configuration, DateTimeOffset.UnixEpoch));
        window.Show();

        InvalidOperationException error = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => runtime.Shutdown.ShutdownAsync());

        StringAssert.Contains(error.Message, "synthetic trace return failure");
        Assert.IsFalse(runtime.Shutdown.Status.Completed);
        Assert.IsNotNull(runtime.Shutdown.Status.PrimaryFailure);
        Assert.IsTrue(window.IsVisible);
        Assert.AreEqual(0, source.DisposeCount);
        Assert.AreEqual(pool.RentCount, pool.ReturnAttempts);
        Assert.IsFalse(ingress.Diagnostics.IsClosed);
        window.Hide();
    });

    [TestMethod]
    public Task NavigationKeyBindingsTargetTheExactViewportCommands() => StaTest.Run(async () =>
    {
        var runtime = await DesktopRuntime.CreateAsync(new WpfUiDispatcher(Dispatcher.CurrentDispatcher));
        try
        {
            var window = runtime.Host.Services.GetRequiredService<MainWindow>();
            var vm = (AScanViewModel)window.DataContext;
            window.Show();
            window.UpdateLayout();
            AssertBinding(Key.Home, ModifierKeys.None, vm.ResetViewportCommand);
            AssertBinding(Key.Add, ModifierKeys.None, vm.ZoomInCommand);
            AssertBinding(Key.Subtract, ModifierKeys.None, vm.ZoomOutCommand);
            AssertBinding(Key.Left, ModifierKeys.Alt, vm.PanLeftCommand);
            AssertBinding(Key.Right, ModifierKeys.Alt, vm.PanRightCommand);

            void AssertBinding(Key key, ModifierKeys modifiers, System.Windows.Input.ICommand expected)
            {
                var binding = window.InputBindings.OfType<KeyBinding>().Single(item =>
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
                services.GetRequiredService<BScanSelectionCoordinator>(),
                services.GetRequiredService<IUiDispatcher>(), Dispatcher.CurrentDispatcher,
                null, null, _ => throw new InvalidOperationException("show failed"));

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
    [DataRow("adapter")]
    [DataRow("viewmodel")]
    [DataRow("window")]
    public Task BScan_window_close_attempts_adapter_viewmodel_and_window_once(string failingStage) => StaTest.Run(async () =>
    {
        var runtime = await DesktopRuntime.CreateAsync(new WpfUiDispatcher(Dispatcher.CurrentDispatcher));
        BScanWindow? window = null;
        try
        {
            var services = runtime.Host.Services;
            var delivery = services.GetRequiredService<BScanVisualDelivery>();
            var vm = new BScanViewModel(delivery, services.GetRequiredService<SharedScanTimeViewport>(),
                services.GetRequiredService<BScanSelectionCoordinator>(), services.GetRequiredService<IUiDispatcher>());
            List<string> calls = [];
            window = new BScanWindow(vm,
                () => { calls.Add("adapter"); if (failingStage == "adapter") throw new InvalidOperationException("adapter"); },
                async () =>
                {
                    calls.Add("viewmodel");
                    await vm.DisposeAsync();
                    if (failingStage == "viewmodel") throw new InvalidOperationException("viewmodel");
                },
                () => { calls.Add("window"); if (failingStage == "window") throw new InvalidOperationException("window"); });
            window.Show();

            Task first = window.CloseOwnedAsync();
            CleanupFailureException error = await Assert.ThrowsAsync<CleanupFailureException>(() => first);

            Assert.AreEqual(failingStage, error.PrimaryError.Message);
            CollectionAssert.AreEqual(new[] { "adapter", "viewmodel", "window" }, calls);
            Assert.AreSame(first, window.CloseOwnedAsync());
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
    public Task BScan_window_close_preserves_multiple_failures_in_stage_order() => StaTest.Run(async () =>
    {
        var runtime = await DesktopRuntime.CreateAsync(new WpfUiDispatcher(Dispatcher.CurrentDispatcher));
        try
        {
            var services = runtime.Host.Services;
            var delivery = services.GetRequiredService<BScanVisualDelivery>();
            var vm = new BScanViewModel(delivery, services.GetRequiredService<SharedScanTimeViewport>(),
                services.GetRequiredService<BScanSelectionCoordinator>(), services.GetRequiredService<IUiDispatcher>());
            var adapterError = new InvalidOperationException("adapter");
            var viewModelError = new InvalidOperationException("viewmodel");
            var windowError = new InvalidOperationException("window");
            var window = new BScanWindow(vm,
                () => throw adapterError,
                async () => { await vm.DisposeAsync(); throw viewModelError; },
                () => throw windowError);
            int notifications = 0;
            window.CleanupFailed += _ => notifications++;
            window.CleanupFailed += _ => throw new InvalidOperationException("observer");
            window.Show();

            window.Close();
            CleanupFailureException error = await Assert.ThrowsAsync<CleanupFailureException>(() => window.CloseOwnedAsync());

            Assert.AreSame(adapterError, error.PrimaryError);
            Assert.AreEqual("adapter", error.PrimaryFailure.Stage);
            Assert.HasCount(2, error.SecondaryErrors);
            Assert.AreSame(viewModelError, error.SecondaryErrors[0]);
            Assert.AreSame(windowError, error.SecondaryErrors[1]);
            CollectionAssert.AreEqual(new[] { "viewmodel", "window" },
                error.SecondaryFailures.Select(failure => failure.Stage).ToArray());
            Assert.IsFalse(window.IsVisible);
            Assert.AreEqual(1, notifications);
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
        await ((AScanViewModel)window.DataContext).StartCommand.ExecuteAsync(null);
        window.Close();
        window.Close();
        Assert.IsTrue(window.IsVisible);
        await closed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.IsGreaterThanOrEqualTo(2, cancelled);
        Assert.IsTrue(runtime.Shutdown.Status.Completed);
    });

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

    private sealed class FailingTracePool : IShortArrayPool
    {
        internal int RentCount { get; private set; }
        internal int ReturnAttempts { get; private set; }
        public short[] Rent(int minimumLength)
        {
            RentCount++;
            return new short[minimumLength];
        }
        public void Return(short[] array)
        {
            ReturnAttempts++;
            if (ReturnAttempts == 1) { throw new InvalidOperationException("synthetic trace return failure"); }
        }
    }
}
