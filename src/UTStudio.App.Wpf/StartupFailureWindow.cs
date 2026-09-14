using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using UTStudio.App.Wpf.Services;

namespace UTStudio.App.Wpf;

/// <summary>Visible failure surface, retained while startup cleanup is not confirmed.</summary>
internal sealed class StartupFailureWindow : Window
{
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 16, 0, 0) };
    private readonly DispatcherTimer _timer;
    private bool _canClose;

    internal StartupFailureWindow(Exception error, ShutdownCoordinator? shutdown)
    {
        Title = "UT-Studio · Error de inicio";
        Width = 700;
        Height = 280;
        var content = new StackPanel { Margin = new Thickness(24) };
        content.Children.Add(new TextBlock { Text = error.Message, TextWrapping = TextWrapping.Wrap });
        content.Children.Add(_status);
        Content = content;
        _timer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher) { Interval = TimeSpan.FromMilliseconds(200) };
        _timer.Tick += (_, _) =>
        {
            if (shutdown is not null && !_canClose)
            {
                var state = shutdown.Status;
                _status.Text = $"Cierre pendiente: {state.Phase}. {state.Error}" +
                    (state.DiagnosticTimeout ? " Más de cinco segundos; sin liberación forzada." : "");
            }
        };
        Closing += OnClosing;
        Closed += (_, _) => _timer.Stop();
        _timer.Start();
    }

    internal void ConfirmCleanup() { _canClose = true; _timer.Stop(); _status.Text = "Recursos liberados. Puede cerrar esta ventana."; }
    private void OnClosing(object? sender, CancelEventArgs args) { args.Cancel = !_canClose; }
}
