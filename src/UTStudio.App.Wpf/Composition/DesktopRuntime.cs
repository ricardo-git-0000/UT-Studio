using System.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using UTStudio.Acquisition.Simulator;
using UTStudio.Application;
using UTStudio.App.Wpf.Services;
using UTStudio.Contracts.Acquisition;
using UTStudio.Contracts.Application;
using UTStudio.Contracts.Presentation;
using UTStudio.Domain.Acquisition;
using UTStudio.Presentation;
using UTStudio.Visualization.Core;

namespace UTStudio.App.Wpf.Composition;

/// <summary>
/// Owns externally registered singletons, including partially constructed startup resources.
/// Always returns the owner: display InitializationError and await Shutdown if initialization failed.
/// </summary>
public sealed class DesktopRuntime
{
    private IHost? _host;
    private IUtFrameSource? _source;
    private AScanVisualDelivery? _visual;
    private BScanIngress? _bScanIngress;
    private BScanVisualDelivery? _bScanVisual;
    private BScanSelectionCoordinator? _selection;
    private BScanWindowManager? _bScanWindows;
    private ApplicationSession? _session;
    private AScanViewModel? _viewModel;
    private SharedScanTimeViewport? _timeViewport;
    private ShutdownInteractionGate? _interactionGate;
    private bool _viewModelReleased;
    private bool _sessionReleased;
    private bool _pipelineStopRequested;
    private bool _pipelineDrained;
    private readonly Action<string>? _lifecycleObserver;
    public IHost Host => _host ?? throw new InvalidOperationException("Host initialization failed.", InitializationError);
    public Exception? InitializationError { get; private set; }
    public ShutdownCoordinator Shutdown { get; }

    private DesktopRuntime(TimeProvider clock, Action<string>? lifecycleObserver = null)
    {
        _lifecycleObserver = lifecycleObserver;
        Shutdown = new ShutdownCoordinator([
            new("Bloquear nuevas aperturas e interacciones", () => _interactionGate?.BlockAsync() ?? Task.CompletedTask),
            new("Cerrar ventanas B-Scan", () => _bScanWindows?.BlockAndCloseAsync() ?? Task.CompletedTask),
            new("Cerrar A-Scan y sus callbacks", ReleaseAScanAsync, () => _viewModelReleased),
            new("Parar y disponer ApplicationSession", ReleaseSessionAsync,
                () => _sessionReleased && (_session is null || _session.Snapshot.Phase == SessionPhase.Disposed)),
            new("Solicitar parada de productor, consumidor y ramas visuales", RequestPipelineStopAsync,
                () => _pipelineStopRequested),
            new("Esperar workers, callbacks, operaciones, leases y owners", WaitForPipelineDrainAsync,
                () => _pipelineDrained),
            new("Liberar deliveries, stores, coordinadores, viewport y fuente", DisposePipelineAsync,
                RequiresConfirmedCleanup: true),
            new("Detener Host", StopHostAsync,
                RequiresConfirmedCleanup: true),
            new("Liberar Host", DisposeHostAsync, RequiresConfirmedCleanup: true)
        ], new ShutdownLogger(), clock);
    }

    private async Task ReleaseAScanAsync()
    {
        await (_viewModel?.DisposeAsync().AsTask() ?? Task.CompletedTask).ConfigureAwait(false);
        _viewModelReleased = true;
    }

    private async Task ReleaseSessionAsync()
    {
        try { await (_session?.DisposeAsync().AsTask() ?? Task.CompletedTask).ConfigureAwait(false); }
        finally { _sessionReleased = _session is null || _session.Snapshot.Phase == SessionPhase.Disposed; }
    }

    private Task RequestPipelineStopAsync()
    {
        List<Exception> failures = [];
        TryStop(() => _bScanVisual?.Stop(), failures);
        TryStop(() => _bScanIngress?.Stop(), failures);
        TryStop(() => _visual?.Stop(), failures);
        _pipelineStopRequested = failures.Count == 0;
        if (_pipelineStopRequested) { ObserveLifecycle("phase5.stop"); }
        return failures.Count == 0 ? Task.CompletedTask :
            Task.FromException(new AggregateException("Pipeline stop request failed.", failures));
    }

    private async Task WaitForPipelineDrainAsync()
    {
        List<Exception> failures = [];
        Task[] drains =
        [
            CaptureDrain(() => _bScanVisual?.WaitForDrainAsync() ?? Task.CompletedTask, failures),
            CaptureDrain(() => _bScanIngress?.WaitForDrainAsync() ?? Task.CompletedTask, failures),
            CaptureDrain(() => _visual?.WaitForDrainAsync() ?? Task.CompletedTask, failures)
        ];
        foreach (Task drain in drains) { await TryDrainAsync(drain, failures).ConfigureAwait(false); }
        _pipelineDrained = failures.Count == 0;
        if (_pipelineDrained) { ObserveLifecycle("phase6.drain"); }
        if (failures.Count != 0) { throw new AggregateException("Pipeline drain failed.", failures); }
    }

    private async Task DisposePipelineAsync()
    {
        List<Exception> failures = [];
        await TryDisposeAsync(() => _bScanVisual?.DisposeAsync().AsTask() ?? Task.CompletedTask, failures).ConfigureAwait(false);
        await TryDisposeAsync(() => _bScanIngress?.DisposeAsync().AsTask() ?? Task.CompletedTask, failures).ConfigureAwait(false);
        await TryDisposeAsync(() => _selection?.DisposeAsync().AsTask() ?? Task.CompletedTask, failures).ConfigureAwait(false);
        await TryDisposeAsync(() => _visual?.DisposeAsync().AsTask() ?? Task.CompletedTask, failures).ConfigureAwait(false);
        await TryDisposeAsync(() => _timeViewport?.DisposeAsync().AsTask() ?? Task.CompletedTask, failures).ConfigureAwait(false);
        await TryDisposeAsync(() => _source?.DisposeAsync().AsTask() ?? Task.CompletedTask, failures).ConfigureAwait(false);
        if (_session is not null && _session.Snapshot.Phase != SessionPhase.Disposed)
        { failures.Add(new InvalidOperationException("ApplicationSession disposal was not confirmed.")); }
        if (_source is not null && _source.State.Connection != UtConnectionState.Disconnected)
        { failures.Add(new InvalidOperationException("Frame source disconnection was not confirmed.")); }
        if (failures.Count != 0) { throw new AggregateException("Pipeline disposal failed.", failures); }
        ObserveLifecycle("phase7.dispose");
    }

    private async Task StopHostAsync()
    {
        await (_host?.StopAsync(CancellationToken.None) ?? Task.CompletedTask).ConfigureAwait(false);
        ObserveLifecycle("host.stop");
    }

    private async Task DisposeHostAsync()
    {
        if (_host is IAsyncDisposable asyncHost) { await asyncHost.DisposeAsync().ConfigureAwait(false); }
        else { _host?.Dispose(); }
        ObserveLifecycle("host.dispose");
    }

    private void ObserveLifecycle(string phase)
    {
        try { _lifecycleObserver?.Invoke(phase); }
        catch (Exception error) { Trace.WriteLine($"Lifecycle observer failed at {phase}: {error}"); }
    }

    private static Task CaptureDrain(Func<Task> start, List<Exception> failures)
    {
        try { return start(); }
        catch (Exception error)
        {
            failures.Add(error);
            return Task.CompletedTask;
        }
    }

    private static async Task TryDisposeAsync(Func<Task> dispose, List<Exception> failures)
    {
        Task operation;
        try { operation = dispose(); }
        catch (Exception error)
        {
            failures.Add(error);
            return;
        }
        await TryDrainAsync(operation, failures).ConfigureAwait(false);
    }

    private static void TryStop(Action action, List<Exception> failures)
    {
        try { action(); }
        catch (Exception error) { failures.Add(error); }
    }

    private static async Task TryDrainAsync(Task drain, List<Exception> failures)
    {
        try { await drain.ConfigureAwait(false); }
        catch (Exception error)
        {
            if (error is AggregateException aggregate) { failures.AddRange(aggregate.Flatten().InnerExceptions); }
            else { failures.Add(error); }
        }
    }

    public static Task<DesktopRuntime> CreateAsync(IUiDispatcher dispatcher, string[]? args = null, TimeProvider? clock = null) =>
        CreateAsync(dispatcher, args, clock ?? TimeProvider.System, null);

    // Deterministic partial-construction fault injection, without changing neutral APIs.
    internal static Task<DesktopRuntime> CreateAsync(IUiDispatcher dispatcher, string[]? args, TimeProvider clock,
        Action<string>? checkpoint) => CreateAsync(dispatcher, args, clock, checkpoint, null, null, null, null, null, null);

    internal static Task<DesktopRuntime> CreateAsync(IUiDispatcher dispatcher, string[]? args, TimeProvider clock,
        Action<string>? checkpoint, IUtFrameSource? controlledSource,
        ConventionalAcquisitionConfiguration? controlledConfiguration, BScanOptions? bScanOptions,
        IShortArrayPool? tracePool, IByteOwnerPool? rasterPool, Action<string>? lifecycleObserver)
    {
        var runtime = new DesktopRuntime(clock, lifecycleObserver);
        try
        {
            ArgumentNullException.ThrowIfNull(dispatcher);
            var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder(args ?? []);
            builder.Services.AddSingleton<IHostLifetime, DesktopHostLifetime>();
            builder.Services.Configure<HostOptions>(options => options.ShutdownTimeout = Timeout.InfiniteTimeSpan);
            SimulatorUtFrameSource? simulator = null;
            IUtFrameSource source;
            ConventionalAcquisitionConfiguration configuration;
            if (controlledSource is null)
            {
                var options = new SimulatorOptions(
                    seed: builder.Configuration.GetValue<ulong?>("Simulator:Seed") ?? 1,
                    maxAScansPerSecond: builder.Configuration.GetValue<double?>("Simulator:AScansPerSecond") ?? 50);
                simulator = new SimulatorUtFrameSource(new UtSourceId("simulator-1"), options, clock);
                source = simulator;
                configuration = SimulatorUtFrameSource.DefaultConfiguration;
            }
            else
            {
                source = controlledSource;
                configuration = controlledConfiguration ?? throw new ArgumentNullException(nameof(controlledConfiguration));
            }
            runtime._source = source;
            var interactionGate = runtime._interactionGate = new ShutdownInteractionGate(dispatcher);
            var visual = runtime._visual = new AScanVisualDelivery(timeProvider: clock);
            var bScanIngress = runtime._bScanIngress = tracePool is null
                ? new BScanIngress(bScanOptions, clock)
                : new BScanIngress(bScanOptions, clock, tracePool, SharedTraceCopyOwnerPool.Instance);
            var bScanVisual = runtime._bScanVisual = rasterPool is null
                ? new BScanVisualDelivery(bScanIngress, clock)
                : new BScanVisualDelivery(bScanIngress, clock, rasterPool);
            var fanOut = new ConventionalFrameSinkFanOut(visual, bScanIngress);
            checkpoint?.Invoke("visual");
            var session = runtime._session = new ApplicationSession(source, fanOut);
            var timeViewport = runtime._timeViewport = new SharedScanTimeViewport(dispatcher);
            var selection = runtime._selection = new BScanSelectionCoordinator(dispatcher, bScanIngress, new AScanProjector());
            var viewModel = runtime._viewModel = new AScanViewModel(session, visual, dispatcher, configuration,
                readVisualStatistics: () => visual.Statistics, visualStatus: visual.StatusChanges, timeViewport: timeViewport,
                selectionCoordinator: selection);
            var bScanWindows = runtime._bScanWindows = new BScanWindowManager(bScanVisual, timeViewport, selection,
                dispatcher, System.Windows.Threading.Dispatcher.CurrentDispatcher);
            // Instance registrations, including aliases, are borrowed and never disposed by DI.
            if (simulator is not null) { builder.Services.AddSingleton(simulator); }
            builder.Services.AddSingleton<IUtFrameSource>(source);
            builder.Services.AddSingleton(visual);
            builder.Services.AddSingleton(fanOut);
            builder.Services.AddSingleton<IConventionalFrameSink>(fanOut);
            builder.Services.AddSingleton<IObservable<AScanSnapshot>>(visual);
            builder.Services.AddSingleton(session);
            builder.Services.AddSingleton<IApplicationSession>(session);
            builder.Services.AddSingleton(viewModel);
            builder.Services.AddSingleton(timeViewport);
            builder.Services.AddSingleton(bScanIngress);
            builder.Services.AddSingleton(bScanVisual);
            builder.Services.AddSingleton<IBScanVisualSource>(bScanVisual);
            builder.Services.AddSingleton(selection);
            builder.Services.AddSingleton(bScanWindows);
            builder.Services.AddSingleton(interactionGate);
            builder.Services.AddSingleton(dispatcher);
            builder.Services.AddSingleton(clock);
            builder.Services.AddSingleton(runtime.Shutdown);
            builder.Services.AddSingleton(new VisualMetrics(() => visual.Statistics));
            builder.Services.AddSingleton<MainWindow>();
            runtime._host = builder.Build();
            checkpoint?.Invoke("host");
        }
        catch (Exception error) { runtime.InitializationError = error; }
        return Task.FromResult(runtime);
    }

    // Remains usable before Host exists and after its logging providers are disposed.
    private sealed class ShutdownLogger : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Trace.WriteLine($"{logLevel}: {formatter(state, exception)} {exception}");
    }
}
