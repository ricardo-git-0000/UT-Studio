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
    private SimulatorUtFrameSource? _source;
    private AScanVisualDelivery? _visual;
    private ApplicationSession? _session;
    private AScanViewModel? _viewModel;
    private bool _viewModelReleased;
    public IHost Host => _host ?? throw new InvalidOperationException("Host initialization failed.", InitializationError);
    public Exception? InitializationError { get; private set; }
    public ShutdownCoordinator Shutdown { get; }

    private DesktopRuntime(TimeProvider clock)
    {
        Shutdown = new ShutdownCoordinator([
            new("ViewModel y sesión: cancelar, drenar y desconectar", ReleaseSessionAsync,
                () => _viewModelReleased && (_session is null || _session.Snapshot.Phase == SessionPhase.Disposed)),
            new("Liberar fuente", () => _source?.DisposeAsync().AsTask() ?? Task.CompletedTask,
                () => (_session is null || _session.Snapshot.Phase == SessionPhase.Disposed) &&
                    _source?.State.Connection == UtConnectionState.Disconnected),
            new("Liberar entrega visual", () => _visual?.DisposeAsync().AsTask() ?? Task.CompletedTask),
            new("Detener Host", () => _host?.StopAsync(CancellationToken.None) ?? Task.CompletedTask),
            new("Liberar Host", async () =>
            {
                if (_host is IAsyncDisposable asyncHost) { await asyncHost.DisposeAsync().ConfigureAwait(false); }
                else { _host?.Dispose(); }
            })
        ], new ShutdownLogger(), clock);
    }

    private async Task ReleaseSessionAsync()
    {
        Task vm = _viewModel?.DisposeAsync().AsTask() ?? Task.CompletedTask;
        Task session = _session?.DisposeAsync().AsTask() ?? Task.CompletedTask;
        async Task ObserveViewModel() { await vm.ConfigureAwait(false); _viewModelReleased = true; }
        await Task.WhenAll(ObserveViewModel(), session).ConfigureAwait(false);
    }

    public static Task<DesktopRuntime> CreateAsync(IUiDispatcher dispatcher, string[]? args = null, TimeProvider? clock = null) =>
        CreateAsync(dispatcher, args, clock ?? TimeProvider.System, null);

    // Deterministic partial-construction fault injection, without changing neutral APIs.
    internal static Task<DesktopRuntime> CreateAsync(IUiDispatcher dispatcher, string[]? args, TimeProvider clock,
        Action<string>? checkpoint)
    {
        var runtime = new DesktopRuntime(clock);
        try
        {
            ArgumentNullException.ThrowIfNull(dispatcher);
            var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder(args ?? []);
            builder.Services.AddSingleton<IHostLifetime, DesktopHostLifetime>();
            builder.Services.Configure<HostOptions>(options => options.ShutdownTimeout = Timeout.InfiniteTimeSpan);
            var options = new SimulatorOptions(
                seed: builder.Configuration.GetValue<ulong?>("Simulator:Seed") ?? 1,
                maxAScansPerSecond: builder.Configuration.GetValue<double?>("Simulator:AScansPerSecond") ?? 50);
            var source = runtime._source = new SimulatorUtFrameSource(new UtSourceId("simulator-1"), options, clock);
            var visual = runtime._visual = new AScanVisualDelivery(timeProvider: clock);
            checkpoint?.Invoke("visual");
            var session = runtime._session = new ApplicationSession(source, visual);
            var viewModel = runtime._viewModel = new AScanViewModel(session, visual, dispatcher, SimulatorUtFrameSource.DefaultConfiguration,
                readVisualStatistics: () => visual.Statistics, visualStatus: visual.StatusChanges);
            // Instance registrations, including aliases, are borrowed and never disposed by DI.
            builder.Services.AddSingleton(source);
            builder.Services.AddSingleton<IUtFrameSource>(source);
            builder.Services.AddSingleton(visual);
            builder.Services.AddSingleton<IConventionalFrameSink>(visual);
            builder.Services.AddSingleton<IObservable<AScanSnapshot>>(visual);
            builder.Services.AddSingleton(session);
            builder.Services.AddSingleton<IApplicationSession>(session);
            builder.Services.AddSingleton(viewModel);
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
