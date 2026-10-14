using System.Windows;
using System.Windows.Threading;
using UTStudio.Presentation;
using UTStudio.ScanVisualization.BScan.Wpf;
using UTStudio.Visualization.Core;
using UTStudio.Visualization.Core.Scan2D;

namespace UTStudio.App.Wpf.Services;

public sealed class BScanWindowManager
{
    private readonly object _gate = new();
    private readonly Dispatcher _windowDispatcher;
    private readonly Func<BScanComponentContext> _contextFactory;
    private readonly Func<BScanComponentContext, BScanWindow> _windowFactory;
    private readonly Action<BScanWindow> _showWindow;
    private BScanWindow? _window;
    private Task _closing = Task.CompletedTask;
    private bool _blocked;
    private int _creationCount;
    private Exception? _lastCloseError;

    internal BScanWindow? ActiveWindow { get { lock (_gate) { return _window; } } }
    internal int CreationCount => Volatile.Read(ref _creationCount);
    internal Exception? LastCloseError { get { lock (_gate) { return _lastCloseError; } } }

    public BScanWindowManager(IBScanVisualSource source, SharedScanTimeViewport viewport,
        TraceSelectionScope selection, Dispatcher windowDispatcher)
        : this(source, viewport, selection, windowDispatcher, null, null, null, null) { }

    internal BScanWindowManager(IBScanVisualSource source, SharedScanTimeViewport viewport,
        TraceSelectionScope selection, Dispatcher windowDispatcher, BScanResamplingPreferenceStore preferenceStore)
        : this(source, viewport, selection, windowDispatcher, preferenceStore, new BScanPaletteLoader(), null, null) { }

    internal BScanWindowManager(IBScanVisualSource source, SharedScanTimeViewport viewport,
        TraceSelectionScope selection, Dispatcher windowDispatcher,
        BScanResamplingPreferenceStore? preferenceStore, IBScanPaletteLoader? paletteLoader,
        Func<BScanComponentContext, BScanWindow>? windowFactory, Action<BScanWindow>? showWindow)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(viewport);
        ArgumentNullException.ThrowIfNull(selection);
        _windowDispatcher = windowDispatcher ?? throw new ArgumentNullException(nameof(windowDispatcher));
        _contextFactory = () => new(source, viewport, selection,
            preferenceStore?.Current ?? Scan2DInterpolationMode.None, preferenceStore);
        _windowFactory = windowFactory ?? (context => new(context, paletteLoader));
        _showWindow = showWindow ?? (window => window.Show());
    }

    internal Task OpenAsync(Window? owner = null)
    {
        if (!_windowDispatcher.CheckAccess())
        { return _windowDispatcher.InvokeAsync(() => OpenAsync(owner)).Task.Unwrap(); }
        return OpenCoreAsync(owner);
    }

    private async Task OpenCoreAsync(Window? owner)
    {
        lock (_gate)
        {
            if (_blocked) { return; }
            if (_window is { } existing) { existing.Activate(); return; }
            if (!_closing.IsCompleted) { return; }
        }
        BScanWindow window = _windowFactory(_contextFactory());
        bool accepted;
        lock (_gate)
        {
            accepted = !_blocked && _window is null && _closing.IsCompleted;
            if (accepted) { _window = window; Interlocked.Increment(ref _creationCount); }
            if (owner is not null) { window.Owner = owner; }
            window.CleanupFailed += error => { lock (_gate) { _lastCloseError ??= error; } };
            window.Closed += (_, _) => { lock (_gate) { if (ReferenceEquals(_window, window)) { _window = null; } } };
        }
        if (!accepted) { await window.CloseOwnedAsync().ConfigureAwait(false); return; }
        try { _showWindow(window); }
        catch (Exception showError)
        {
            Exception? cleanupError = null;
            try { await window.CloseOwnedAsync().ConfigureAwait(false); }
            catch (Exception error) { cleanupError = error; }
            lock (_gate) { if (ReferenceEquals(_window, window)) { _window = null; } }
            if (cleanupError is null) { throw; }
            throw new CleanupFailureException("B-Scan Show failed and cleanup also reported a failure.", showError, [cleanupError]);
        }
    }

    public Task BlockAndCloseAsync()
    {
        lock (_gate)
        {
            _blocked = true;
            if (_window is null) { return _closing; }
            if (_closing.IsCompleted)
            {
                BScanWindow window = _window;
                _closing = window.Dispatcher.CheckAccess()
                    ? window.CloseOwnedAsync()
                    : window.Dispatcher.InvokeAsync(window.CloseOwnedAsync).Task.Unwrap();
            }
            return _closing;
        }
    }
}
