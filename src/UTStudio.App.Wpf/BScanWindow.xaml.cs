using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Xml;
using Microsoft.Win32;
using UTStudio.App.Wpf.Adapters;
using UTStudio.Presentation;
using UTStudio.Visualization.Wpf.Rendering;

namespace UTStudio.App.Wpf;

public partial class BScanWindow : Window
{
    private readonly object _closeGate = new();
    private readonly BScanViewModel _viewModel;
    private readonly BScanControlAdapter? _adapter;
    private readonly Action _disposeAdapter;
    private readonly Func<Task> _disposeViewModel;
    private readonly Action? _beforeWindowClose;
    private Task? _closingTask;
    private bool _allowClose;
    private int _cleanupErrorPublished;
    internal Exception? CleanupError { get; private set; }
    internal event Action<Exception>? CleanupFailed;

    public BScanWindow(BScanViewModel viewModel)
    {
        InitializeComponent();
        DataContext = _viewModel = viewModel;
        var adapter = _adapter = new BScanControlAdapter(viewModel, Scan);
        _disposeAdapter = adapter.Dispose;
        _disposeViewModel = () => viewModel.DisposeAsync().AsTask();
        _beforeWindowClose = null;
        Closing += OnClosing;
    }

    internal BScanWindow(BScanViewModel viewModel, Action disposeAdapter, Func<Task> disposeViewModel,
        Action? beforeWindowClose = null)
    {
        InitializeComponent();
        DataContext = _viewModel = viewModel;
        _adapter = null;
        _disposeAdapter = disposeAdapter;
        _disposeViewModel = disposeViewModel;
        _beforeWindowClose = beforeWindowClose;
        Closing += OnClosing;
    }

    internal string ConsumerId => _viewModel.ConsumerId;

    private void OnLoadPalette(object sender, RoutedEventArgs e)
    {
        if (_adapter is null)
        {
            PaletteStatus.Text = "Paleta: carga no disponible en esta composición.";
            return;
        }
        var dialog = new OpenFileDialog
        {
            Title = "Cargar paleta B-Scan",
            Filter = "Paleta XML (*.xml)|*.xml|Todos los archivos (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false
        };
        if (dialog.ShowDialog(this) != true) { return; }
        try
        {
            BScanColorPalette palette = BScanPaletteFile.Load(dialog.FileName);
            _adapter.SetPalette(palette);
            PaletteStatus.Text = $"Paleta: {palette.Name}";
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or XmlException or InvalidDataException)
        {
            PaletteStatus.Text = $"Paleta no cargada: {error.Message}";
        }
    }

    internal Task CloseOwnedAsync()
    {
        lock (_closeGate) { return _closingTask ??= CloseCoreAsync(); }
    }

    private async Task CloseCoreAsync()
    {
        List<CleanupStageFailure> failures = [];
        try { _disposeAdapter(); } catch (Exception error) { failures.Add(new("adapter", error)); }
        try { await _disposeViewModel().ConfigureAwait(false); }
        catch (Exception error) { failures.Add(new("viewmodel", error)); }
        try { _beforeWindowClose?.Invoke(); }
        catch (Exception error) { failures.Add(new("window", error)); }
        if (Dispatcher.HasShutdownStarted)
        { failures.Add(new("ui-barrier", new InvalidOperationException("B-Scan UI barrier is unavailable because its Dispatcher is shutting down."))); }
        else
        {
            try { await Dispatcher.InvokeAsync(() => { _allowClose = true; Close(); }); }
            catch (Exception error) { failures.Add(new("window", error)); }
        }
        if (failures.Count != 0)
        {
            var cleanup = new CleanupFailureException("B-Scan window cleanup failed.", failures[0], failures.Skip(1).ToArray());
            PublishCleanupError(cleanup);
            throw cleanup;
        }
    }

    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_allowClose) return;
        e.Cancel = true;
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
