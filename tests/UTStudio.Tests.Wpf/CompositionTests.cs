using System.Windows.Threading;
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
using UTStudio.Presentation;
using UTStudio.Visualization.Core;

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
            Assert.AreSame(services.GetRequiredService<AScanVisualDelivery>(), services.GetRequiredService<IConventionalFrameSink>());
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
}
