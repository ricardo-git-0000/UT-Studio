using System.ComponentModel;
using System.Windows;
using UTStudio.Presentation;
using UTStudio.ScanVisualization.BScan.Wpf;

namespace UTStudio.App.Wpf;

public partial class BScanWindow : Window
{
    private readonly object _closeGate = new();
    private Task? _closingTask;
    private bool _allowClose;
    private int _cleanupErrorPublished;
    internal Exception? CleanupError { get; private set; }
    internal event Action<Exception>? CleanupFailed;

    public BScanWindow(BScanComponentContext context, IBScanPaletteLoader? paletteLoader = null)
    {
        InitializeComponent();
        ScanView.Context = context;
        ScanView.PaletteLoader = paletteLoader;
        Closing += OnClosing;
    }

    internal string ConsumerId => ScanView.ConsumerId
        ?? throw new InvalidOperationException("B-Scan has not been activated yet.");
    internal BScanView Component => ScanView;
    internal Task CloseOwnedAsync()
    {
        lock (_closeGate) { return _closingTask ??= CloseCoreAsync(); }
    }

    private async Task CloseCoreAsync()
    {
        List<CleanupStageFailure> failures = [];
        try { await ScanView.CloseAsync().ConfigureAwait(true); }
        catch (CleanupFailureException error)
        {
            failures.Add(error.PrimaryFailure);
            failures.AddRange(error.SecondaryFailures);
        }
        catch (Exception error) { failures.Add(new("component", error)); }
        if (Dispatcher.HasShutdownStarted)
        {
            failures.Add(new("ui-barrier", new InvalidOperationException(
                "B-Scan UI barrier is unavailable because its Dispatcher is shutting down.")));
        }
        else
        {
            try { _allowClose = true; Close(); }
            catch (Exception error) { failures.Add(new("window", error)); }
        }
        if (failures.Count == 0) { return; }
        var cleanup = new CleanupFailureException("B-Scan window cleanup failed.", failures[0], failures.Skip(1).ToArray());
        PublishCleanupError(cleanup);
        throw cleanup;
    }

    private async void OnClosing(object? sender, CancelEventArgs args)
    {
        if (_allowClose) { return; }
        args.Cancel = true;
        try { await CloseOwnedAsync(); }
        catch (Exception error)
        {
            PublishCleanupError(error);
            Title = $"B-Scan · cierre pendiente: {error.Message}";
        }
    }

    private void PublishCleanupError(Exception error)
    {
        if (Interlocked.Exchange(ref _cleanupErrorPublished, 1) != 0) { return; }
        CleanupError = error;
        foreach (Action<Exception> handler in CleanupFailed?.GetInvocationList().Cast<Action<Exception>>() ?? [])
        { try { handler(error); } catch { } }
    }
}
