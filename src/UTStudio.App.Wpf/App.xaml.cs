using System.Diagnostics;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using UTStudio.App.Wpf.Composition;
using UTStudio.App.Wpf.Services;

namespace UTStudio.App.Wpf;

public partial class App : System.Windows.Application
{
    private DesktopRuntime? _runtime;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        try
        {
            _runtime = await DesktopRuntime.CreateAsync(new WpfUiDispatcher(Dispatcher), e.Args);
            if (_runtime.InitializationError is { } initializationError) { throw initializationError; }
            await _runtime.Host.StartAsync();
            var window = _runtime.Host.Services.GetRequiredService<MainWindow>();
            MainWindow = window;
            window.Show();
        }
        catch (Exception error)
        {
            Trace.TraceError("UT-Studio startup failed: {0}", error);
            var diagnostic = new StartupFailureWindow(error, _runtime?.Shutdown);
            MainWindow = diagnostic;
            diagnostic.Show();
            if (_runtime is not null)
            {
                try { await _runtime.Shutdown.ShutdownAsync(); }
                catch (Exception cleanupError)
                {
                    Trace.TraceError("Startup cleanup remains pending: {0}", cleanupError);
                    return;
                }
            }
            diagnostic.ConfirmCleanup();
        }
    }
}
