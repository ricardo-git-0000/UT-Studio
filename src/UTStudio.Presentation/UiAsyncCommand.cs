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
    private bool _cancellationStarted;
    private Task? _closeCompletion;
    private TaskCompletionSource? _refreshDrain;
    private bool _refreshRequested;
    private int _reserved;
    private int _closing;
    private bool _available, _publishedCanExecute, _running, _cancelled;
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
    internal void Initialize() => _publishedCanExecute = _available = _canExecute();
    public Task? ExecutionTask => _executionTask;
    public bool IsRunning => _running;
    public bool CanBeCanceled => _running && !_cancelled;
    public bool IsCancellationRequested => _cancelled;
    public event EventHandler? CanExecuteChanged;
    public bool CanExecute(object? parameter) => !_lifetime.IsClosed && Volatile.Read(ref _closing) == 0 && _available;
    public void Execute(object? parameter) => _ = ExecuteAsync(parameter);

    public Task ExecuteAsync(object? parameter)
    {
        TaskCompletionSource completion;
        CancellationTokenSource cancellation;
        lock (_gate)
        {
            if (_lifetime.IsClosed || _closing != 0 || _reserved != 0) { return Task.CompletedTask; }
            _reserved = 1;
            completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            cancellation = new();
            _cancellation = cancellation;
            _cancellationCallbacks = Task.CompletedTask;
            _cancellationStarted = false;
            _closeCompletion = completion.Task;
        }
        _ = Observe(RunAsync(completion, cancellation));
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
                if (Volatile.Read(ref _closing) != 0 || !_canExecute()) { return; }
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
            lock (_gate)
            {
                if (dispatchError is null) { completion.TrySetResult(); }
                else { completion.TrySetException(dispatchError); }
                _reserved = 0;
                _closeCompletion = null;
            }
            if (dispatchError is not null) { _ = completion.Task.Exception; }
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
        CancellationTokenSource? cancellation = null;
        TaskCompletionSource? completion = null;
        lock (_gate)
        {
            if (_cancellation is { IsCancellationRequested: false } pending && !_cancellationStarted)
            {
                _cancellationStarted = true;
                cancellation = pending;
                completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
                _cancellationCallbacks = completion.Task;
            }
            else { return _cancellationCallbacks; }
        }
        _ = CompleteCancellationAsync(cancellation, completion);
        return completion.Task;
    }

    internal void DisableForClose()
    {
        lock (_gate) { _closing = 1; }
        _ = Observe(RequestRefresh(allowClosing: true));
    }

    internal async Task FinishForCloseAsync()
    {
        Task cancellation = CancelPending();
        Task? completion;
        Task refresh;
        lock (_gate)
        {
            completion = _closeCompletion;
            refresh = _refreshDrain?.Task ?? Task.CompletedTask;
        }
        await Task.WhenAll(cancellation, completion ?? Task.CompletedTask, refresh).ConfigureAwait(false);
    }

    public void NotifyCanExecuteChanged() => _ = Observe(RequestRefresh());

    private Task RequestRefresh(bool allowClosing = false)
    {
        TaskCompletionSource? drain = null;
        lock (_gate)
        {
            if (_lifetime.IsClosed || (_closing != 0 && !allowClosing))
            { return _refreshDrain?.Task ?? Task.CompletedTask; }
            _refreshRequested = true;
            if (_refreshDrain is not null) { return _refreshDrain.Task; }
            drain = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _refreshDrain = drain;
        }
        _ = ObserveRefreshDispatchAsync(_lifetime.InvokeAsync(() => DrainRefresh(drain)), drain);
        return drain.Task;
    }

    private void DrainRefresh(TaskCompletionSource drain)
    {
        while (true)
        {
            lock (_gate)
            {
                if (!ReferenceEquals(_refreshDrain, drain)) { return; }
                if (!_refreshRequested)
                {
                    _refreshDrain = null;
                    drain.TrySetResult();
                    return;
                }
                _refreshRequested = false;
            }
            bool available = Volatile.Read(ref _closing) == 0 && !_lifetime.IsClosed && _canExecute();
            _available = available;
            if (available == _publishedCanExecute) { continue; }
            _publishedCanExecute = available;
            try { CanExecuteChanged?.Invoke(this, EventArgs.Empty); }
            catch (Exception error) { _reportError(error); }
        }
    }

    private async Task ObserveRefreshDispatchAsync(Task dispatch, TaskCompletionSource drain)
    {
        try { await dispatch.ConfigureAwait(false); }
        catch (Exception error)
        {
            lock (_gate)
            {
                if (ReferenceEquals(_refreshDrain, drain))
                { _refreshRequested = false; _refreshDrain = null; }
            }
            drain.TrySetException(error);
            _ = drain.Task.Exception;
        }
    }

    private static async Task CompleteCancellationAsync(CancellationTokenSource cancellation, TaskCompletionSource completion)
    {
        try { await cancellation.CancelAsync().ConfigureAwait(false); completion.TrySetResult(); }
        catch (Exception error) { completion.TrySetException(error); _ = completion.Task.Exception; }
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
