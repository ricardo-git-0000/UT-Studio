using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace UTStudio.Presentation;

/// <summary>
/// Toolkit async-command contract with explicitly dispatched state/events. A plain AsyncRelayCommand
/// captures its caller context; that is insufficient for arbitrary-thread entry and a neutral dispatcher.
/// </summary>
internal sealed class UiAsyncCommand : ObservableObject, IAsyncRelayCommand
{
    private readonly UiLifetime _lifetime;
    private readonly Func<CancellationToken, Task> _execute;
    private readonly Func<bool> _canExecute;
    private readonly Action<Exception> _reportError;
    private readonly Action _stateChanged;
    private readonly object _gate = new();
    private CancellationTokenSource? _cancellation;
    private Task _cancellationCallbacks = Task.CompletedTask;
    private int _reserved;
    private bool _available, _running, _cancelled;
    private Task? _executionTask;

    internal UiAsyncCommand(UiLifetime lifetime, Func<CancellationToken, Task> execute,
        Func<bool> canExecute, Action<Exception> reportError, Action stateChanged)
    {
        _lifetime = lifetime;
        _execute = execute;
        _canExecute = canExecute;
        _reportError = reportError;
        _stateChanged = stateChanged;
    }

    // Initial hydration is silent, before the ViewModel is exposed to bindings.
    internal void Initialize() => _available = _canExecute();
    public Task? ExecutionTask => _executionTask;
    public bool IsRunning => _running;
    public bool CanBeCanceled => _running && !_cancelled;
    public bool IsCancellationRequested => _cancelled;
    public event EventHandler? CanExecuteChanged;
    public bool CanExecute(object? parameter) => !_lifetime.IsClosed && _available;
    public void Execute(object? parameter) => _ = ExecuteAsync(parameter);

    public Task ExecuteAsync(object? parameter)
    {
        if (_lifetime.IsClosed || Interlocked.CompareExchange(ref _reserved, 1, 0) != 0) { return Task.CompletedTask; }
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancellation = new CancellationTokenSource();
        lock (_gate) { _cancellation = cancellation; _cancellationCallbacks = Task.CompletedTask; }
        _ = Task.Run(() => RunAsync(completion, cancellation));
        return completion.Task;
    }

    private async Task RunAsync(TaskCompletionSource completion, CancellationTokenSource cancellation)
    {
        Exception? dispatchError = null;
        bool admitted = false;
        try
        {
            await _lifetime.InvokeAsync(() =>
            {
                if (!_canExecute()) { return; }
                admitted = true;
                _running = true;
                _cancelled = false;
                _executionTask = completion.Task;
                NotifyState();
            }).ConfigureAwait(false);
            if (admitted && !_lifetime.IsClosed)
            {
                try { await _execute(cancellation.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
                catch (Exception error) { await _lifetime.InvokeAsync(() => _reportError(error)).ConfigureAwait(false); }
            }
        }
        catch (Exception error) { dispatchError = error; }
        finally
        {
            Task callbacks;
            lock (_gate) { callbacks = _cancellationCallbacks; _cancellation = null; }
            try { await callbacks.ConfigureAwait(false); }
            catch (Exception error)
            {
                try { await _lifetime.InvokeAsync(() => _reportError(error)).ConfigureAwait(false); }
                catch (Exception failure) { dispatchError ??= failure; }
            }
            cancellation.Dispose();
            if (admitted)
            {
                try
                {
                    await _lifetime.InvokeAsync(() =>
                    {
                        _running = false;
                        _executionTask = Task.CompletedTask;
                        NotifyState();
                    }).ConfigureAwait(false);
                }
                catch (Exception error) { dispatchError ??= error; }
            }
            Interlocked.Exchange(ref _reserved, 0);
            if (dispatchError is null) { completion.TrySetResult(); }
            else { completion.TrySetException(dispatchError); _ = completion.Task.Exception; }
        }
    }

    public void Cancel() => _ = Observe(_lifetime.InvokeAsync(() =>
    {
        if (!_running || _cancelled) { return; }
        _cancelled = true;
        CancelPending();
        NotifyState();
    }));

    internal Task CancelPending()
    {
        lock (_gate)
        {
            if (_cancellation is { IsCancellationRequested: false }) { _cancellationCallbacks = _cancellation.CancelAsync(); }
            return _cancellationCallbacks;
        }
    }

    public void NotifyCanExecuteChanged() => _ = Observe(_lifetime.InvokeAsync(Refresh));
    internal void Refresh()
    {
        _available = _canExecute();
        if (_lifetime.IsClosed) { return; }
        try { CanExecuteChanged?.Invoke(this, EventArgs.Empty); }
        catch (Exception error) { _reportError(error); }
    }

    private void NotifyState()
    {
        _stateChanged();
        OnPropertyChanged(nameof(IsRunning));
        OnPropertyChanged(nameof(ExecutionTask));
        OnPropertyChanged(nameof(CanBeCanceled));
        OnPropertyChanged(nameof(IsCancellationRequested));
    }

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        if (_lifetime.IsClosed) { return; }
        try { base.OnPropertyChanged(e); }
        catch (Exception error) { _reportError(error); }
    }

    // ICommand's void methods must not leave dispatcher task failures unobserved.
    private static async Task Observe(Task task)
    {
        try { await task.ConfigureAwait(false); }
        catch { } // No safe observable mutation is possible if the UI dispatcher itself is unavailable.
    }
}
