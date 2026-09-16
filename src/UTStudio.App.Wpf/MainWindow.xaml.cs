using System.ComponentModel;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.Logging;
using UTStudio.App.Wpf.Services;
using UTStudio.Presentation;

namespace UTStudio.App.Wpf;

public partial class MainWindow : Window
{
    private readonly ShutdownCoordinator _shutdown;
    private readonly ILogger<MainWindow> _logger;
    private readonly VisualMetrics _metrics;
    private readonly DispatcherTimer _statusTimer;
    private readonly RefreshGate _metricsGate;
    private bool _closing, _allowClose;

    public MainWindow(AScanViewModel viewModel, ShutdownCoordinator shutdown, VisualMetrics metrics,
        TimeProvider clock, ILogger<MainWindow> logger)
    {
        InitializeComponent();
        DataContext = viewModel;
        _shutdown = shutdown;
        _logger = logger;
        _metrics = metrics;
        _metricsGate = new RefreshGate(clock, TimeSpan.FromMilliseconds(200));
        _statusTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher) { Interval = TimeSpan.FromMilliseconds(100) };
        _statusTimer.Tick += UpdateStatus;
        Loaded += (_, _) => _statusTimer.Start();
        Closed += (_, _) => _statusTimer.Stop();
        Closing += OnClosing;
    }

    internal void UpdateStatus(object? sender, EventArgs args)
    {
        try
        {
            if (_closing)
            {
                var status = _shutdown.Status;
                ShutdownText.Text = $"Cerrando: {status.Phase}. {status.Error}" +
                    (status.DiagnosticTimeout ? " Más de cinco segundos; esperando limpieza segura." : "");
            }
            else if (_metricsGate.TryEnter())
            {
                var statistics = _metrics.Read();
                MetricsText.Text = $"Visual · recibidos: {statistics.Received:N0} · publicados: {statistics.Published:N0} · sustituidos: {statistics.Replaced:N0} · descartados: {statistics.Dropped:N0}";
            }
        }
        catch (Exception error) { _logger.LogError(error, "Status update failed"); ShutdownText.Text = error.Message; }
    }

    // WPF requires a void Closing event. Every asynchronous failure is caught and logged.
    private async void OnClosing(object? sender, CancelEventArgs args)
    {
        if (_allowClose) { return; }
        args.Cancel = true;
        if (_closing) { return; }
        _closing = true;
        Interactions.IsEnabled = false;
        ShutdownText.Text = "Cerrando: liberando suscripciones y deteniendo adquisición…";
        try
        {
            await _shutdown.ShutdownAsync();
            // Always unwind Closing first, even when shutdown was already completed.
            // _closing admits only this one final-close operation.
            await Dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(() =>
            {
                _allowClose = true;
                Close();
            })).Task;
        }
        catch (Exception error)
        {
            _logger.LogError(error, "Shutdown remains pending; MainWindow stays open");
            UpdateStatus(null, EventArgs.Empty);
        }
    }
}
