using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UTStudio.Contracts.Application;
using UTStudio.Domain.Acquisition;
using UTStudio.Visualization.Core;

namespace UTStudio.Presentation;

/// <summary>Neutral A-Scan bindings and commands. All changes after construction execute on the UI port.</summary>
/// <remarks>
/// Session and visual feed are borrowed. Closing detaches and invalidates callbacks, not the session.
/// One dispatcher update coalesces latest input; no sample processing and no per-point collection edits.
/// Statistics are sampled on visual feed updates, not periodically at 5 Hz. Effective UI throttling is
/// a future scheduling concern; this ViewModel does not claim that upstream 30 Hz limits UI execution.
/// DisposeAsync completes after a serial UI barrier. Already executing updates finish before that barrier;
/// late/queued callbacks become no-ops. Terminal visual status does not depend on frame arrival.
/// </remarks>
public sealed class AScanViewModel : ObservableObject, IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly IApplicationSession _session;
    private readonly UiLifetime _lifetime;
    private readonly ConventionalAcquisitionConfiguration _configuration;
    private readonly Func<AcquisitionRunId> _newRun;
    private readonly Func<AScanDeliveryStatistics>? _readStatistics;
    private readonly SharedScanTimeViewport _timeViewport;
    private readonly bool _ownsTimeViewport;
    private readonly string _viewportConsumerId = $"ascan-{Guid.NewGuid():N}";
    private readonly UiAsyncCommand _start, _stop;
    private readonly RelayCommand _toggleCursors, _resetCursors, _resetViewport, _zoomIn, _zoomOut, _panLeft, _panRight;
    private IDisposable? _sessionSubscription, _visualSubscription, _statusSubscription;
    private SessionSnapshot _latestSession;
    private AScanSnapshot? _latestVisual;
    private AScanDeliveryStatistics? _latestStatistics;
    private AScanDeliveryStatus? _latestStatus;
    private UtSourceError? _feedError;
    private AScanViewState _state;
    private UtSourceError? _notificationError;
    private bool _pumpScheduled, _dirty;
    private Task? _disposal;
    private ScanTimeViewportBinding? _publishedViewportBinding;
    private int _closing;
    private bool IsClosing => Volatile.Read(ref _closing) != 0;

    public AScanViewModel(IApplicationSession session, IObservable<AScanSnapshot> visualFeed,
        IUiDispatcher dispatcher, ConventionalAcquisitionConfiguration configuration,
        Func<AcquisitionRunId>? newRun = null, Func<AScanDeliveryStatistics>? readVisualStatistics = null,
        IObservable<AScanDeliveryStatus>? visualStatus = null, SharedScanTimeViewport? timeViewport = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(visualFeed);
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(configuration);
        _session = session;
        _configuration = configuration;
        _newRun = newRun ?? (() => new AcquisitionRunId(Guid.NewGuid()));
        _readStatistics = readVisualStatistics;
        _lifetime = new UiLifetime(dispatcher);
        _ownsTimeViewport = timeViewport is null;
        _timeViewport = timeViewport ?? new SharedScanTimeViewport(dispatcher);
        _timeViewport.PropertyChanged += OnTimeViewportChanged;
        _latestSession = session.Snapshot;
        _state = new(_latestSession, null, null, null, null);
        _start = new(_lifetime, StartAsync, () => !IsClosing && _state.Session.CanStart && !_start!.IsRunning && !_stop!.IsRunning,
            ReportCommandError, RefreshCommands);
        _stop = new(_lifetime, StopAsync, () => !IsClosing && (_state.Session.CanStop || _start.IsRunning) && !_stop!.IsRunning,
            ReportCommandError, RefreshCommands);
        _toggleCursors = new(ToggleCursors, () => !IsClosing && !_lifetime.IsClosed && _state.Cursors is not null);
        _resetCursors = new(ResetCursors, () => !IsClosing && !_lifetime.IsClosed && _state.AScan is not null && _state.Cursors is not null);
        _resetViewport = new(ResetTimeZoom, () => !IsClosing && !_lifetime.IsClosed && TimeViewport is { IsReset: false });
        _zoomIn = new(() => ZoomAtCenter(1.2), () => !IsClosing && !_lifetime.IsClosed &&
            TimeViewport is { } viewport && viewport.VisibleSpanSeconds >
                viewport.DomainSpanSeconds / ScanTimeViewportOperations.MaximumZoomFactor * (1 + 1e-12));
        _zoomOut = new(() => ZoomAtCenter(1 / 1.2), () => !IsClosing && !_lifetime.IsClosed && TimeViewport is { IsReset: false });
        _panLeft = new(() => PanByFraction(-.1), () => !IsClosing && !_lifetime.IsClosed && TimeViewport is { } viewport &&
            viewport.VisibleMinimumSeconds > viewport.DomainMinimumSeconds);
        _panRight = new(() => PanByFraction(.1), () => !IsClosing && !_lifetime.IsClosed && TimeViewport is { } viewport &&
            viewport.VisibleMaximumSeconds < viewport.DomainMaximumSeconds);
        _start.Initialize();
        _stop.Initialize();
        try { _sessionSubscription = session.Subscribe(new Observer<SessionSnapshot>(ReceiveSession, ReceiveError)); }
        catch (Exception error) { ReceiveError(error); }
        try { _visualSubscription = visualFeed.Subscribe(new Observer<AScanSnapshot>(ReceiveVisual, ReceiveError)); }
        catch (Exception error) { ReceiveError(error); }
        try { _statusSubscription = visualStatus?.Subscribe(new Observer<AScanDeliveryStatus>(ReceiveStatus, ReceiveError)); }
        catch (Exception error) { ReceiveError(error); }
    }

    public AScanViewState State => _state;
    public SessionSnapshot Session => _state.Session;
    public AScanSnapshot? AScan => _state.AScan;
    public IReadOnlyList<AScanPoint> Points => _state.AScan?.Points ?? (IReadOnlyList<AScanPoint>)Array.Empty<AScanPoint>();
    public ConventionalUtFrameMetadata? Metadata => _state.AScan?.Metadata;
    public AScanDeliveryStatistics? VisualStatistics => _state.VisualStatistics;
    public long ReceivedFrames => _state.Session.ReceivedFrames;
    public long ReleasedFrames => _state.Session.ReleasedFrames;
    public UtSourceError? Error => _state.CommandError ?? _state.FeedError ?? _state.Session.PrimaryError;
    public AScanDeliveryStatus? VisualStatus => _state.VisualStatus;
    public AScanCursorState? Cursors => _state.Cursors;
    public ScanTimeViewport? TimeViewport => _timeViewport.Viewport;
    public ScanTimeViewportBinding? ViewportBinding => _state.AScan is { } snapshot &&
        _timeViewport.RunId == snapshot.Metadata.RunId && _timeViewport.Viewport is { } viewport
        ? new(snapshot.Metadata.RunId, snapshot.Version, viewport) : null;
    public UtSourceError? VisualError => _state.VisualStatus?.TerminalError ?? _state.Session.VisualError ?? _state.VisualStatistics?.LastError;
    public UtSourceError? NotificationError => _notificationError;
    public IAsyncRelayCommand StartCommand => _start;
    public IAsyncRelayCommand StopCommand => _stop;
    public IRelayCommand ToggleCursorsCommand => _toggleCursors;
    public IRelayCommand ResetCursorsCommand => _resetCursors;
    public IRelayCommand ResetViewportCommand => _resetViewport;
    public IRelayCommand ZoomInCommand => _zoomIn;
    public IRelayCommand ZoomOutCommand => _zoomOut;
    public IRelayCommand PanLeftCommand => _panLeft;
    public IRelayCommand PanRightCommand => _panRight;

    public void ZoomTime(double anchorSeconds, double factor)
    {
        if (!IsClosing) { _lifetime.Post(() => { if (!IsClosing) { _timeViewport.Zoom(anchorSeconds, factor); } }); }
    }
    public void PanTime(double deltaSeconds)
    {
        if (!IsClosing) { _lifetime.Post(() => { if (!IsClosing) { _timeViewport.Pan(deltaSeconds); } }); }
    }
    public void ResetTimeZoom()
    {
        if (!IsClosing) { _lifetime.Post(() => { if (!IsClosing) { _timeViewport.Reset(); } }); }
    }

    private void ZoomAtCenter(double factor)
    {
        if (TimeViewport is not { } viewport) { return; }
        ZoomTime((viewport.VisibleMinimumSeconds + viewport.VisibleMaximumSeconds) / 2, factor);
    }

    private void PanByFraction(double fraction)
    {
        if (TimeViewport is { } viewport) { PanTime(viewport.VisibleSpanSeconds * fraction); }
    }

    public void ActivateCursor(AScanCursorId cursor) => _lifetime.Post(() =>
    {
        if (_state.Cursors is not { } cursors) { return; }
        ReplaceState(_state with { Cursors = cursors with { ActiveCursor = cursor } });
    });

    public void MoveCursor(AScanCursorId cursor, double timeSeconds) => _lifetime.Post(() =>
    {
        if (_state is not { AScan: { } snapshot, Cursors: { } cursors }) { return; }
        ReplaceState(_state with { Cursors = AScanCursorMeasurements.Move(cursors, snapshot, cursor, timeSeconds) });
    });

    private void ToggleCursors() => _lifetime.Post(() =>
    {
        if (_state.Cursors is not { } cursors) { return; }
        ReplaceState(_state with { Cursors = cursors with { IsVisible = !cursors.IsVisible } });
    });

    private void ResetCursors() => _lifetime.Post(() =>
    {
        if (_state is not { AScan: { } snapshot, Cursors: { } cursors }) { return; }
        ReplaceState(_state with { Cursors = AScanCursorMeasurements.Reset(cursors, snapshot) });
    });

    private async Task StartAsync(CancellationToken token)
    {
        await _lifetime.InvokeAsync(() => ReplaceState(_state with { CommandError = null })).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        try { await _session.StartAsync(_configuration, _newRun(), token).ConfigureAwait(false); }
        finally { await SynchronizeSessionAsync().ConfigureAwait(false); }
    }

    private async Task StopAsync(CancellationToken token)
    {
        var startCancellation = _start.CancelPending();
        await _lifetime.InvokeAsync(() => ReplaceState(_state with { CommandError = null })).ConfigureAwait(false);
        try { await _session.StopAsync(token).ConfigureAwait(false); }
        finally
        {
            await startCancellation.ConfigureAwait(false);
            await SynchronizeSessionAsync().ConfigureAwait(false);
        }
    }

    // Command completion is a presentation barrier: the authoritative session snapshot must be applied
    // on UI before UiAsyncCommand clears IsRunning and refreshes CanExecute.
    private async Task SynchronizeSessionAsync()
    {
        ReceiveSession(_session.Snapshot);
        await _lifetime.InvokeAsync(ApplyLatest).ConfigureAwait(false);
    }

    private void ReceiveSession(SessionSnapshot snapshot)
    {
        lock (_gate)
        {
            if (_lifetime.IsClosed || snapshot.Version < _latestSession.Version) { return; }
            _latestSession = snapshot;
            Schedule();
        }
    }

    private void ReceiveVisual(AScanSnapshot snapshot)
    {
        AScanDeliveryStatistics? statistics = null;
        try { if (!_lifetime.IsClosed) { statistics = _readStatistics?.Invoke(); } }
        catch (Exception error) { ReceiveError(error); }
        lock (_gate)
        {
            if (_lifetime.IsClosed || (_latestVisual is { } previous && snapshot.Version <= previous.Version)) { return; }
            _latestVisual = snapshot;
            _latestStatistics = statistics;
            Schedule();
        }
    }

    private void ReceiveError(Exception error)
    {
        lock (_gate)
        {
            if (_lifetime.IsClosed) { return; }
            _feedError = Describe("presentation.feed", error);
            Schedule();
        }
    }

    private void ReceiveStatus(AScanDeliveryStatus status)
    {
        lock (_gate)
        {
            if (_lifetime.IsClosed || (_latestStatus is { } previous && status.Version <= previous.Version)) { return; }
            _latestStatus = status;
            Schedule();
        }
    }

    // Must hold _gate. No callback captures a historical snapshot; the UI action reads latest.
    private void Schedule()
    {
        _dirty = true;
        if (_pumpScheduled) { return; }
        _pumpScheduled = true;
        _ = Task.Run(PumpAsync);
    }

    private async Task PumpAsync()
    {
        try
        {
            while (true)
            {
                await _lifetime.InvokeAsync(ApplyLatest).ConfigureAwait(false);
                lock (_gate)
                {
                    if (_lifetime.IsClosed || !_dirty) { _pumpScheduled = false; return; }
                }
            }
        }
        catch (Exception error)
        {
            // Preserve for a later successful dispatch; never mutate bindings on this worker thread.
            lock (_gate) { _feedError = Describe("presentation.dispatch", error); _pumpScheduled = false; }
        }
    }

    private void ApplyLatest()
    {
        if (IsClosing) { return; }
        SessionSnapshot session;
        AScanSnapshot? visual;
        AScanDeliveryStatistics? statistics;
        UtSourceError? error;
        AScanDeliveryStatus? status;
        lock (_gate)
        {
            _dirty = false;
            session = _latestSession;
            visual = _latestVisual;
            statistics = _latestStatistics;
            error = _feedError;
            status = _latestStatus;
        }
        AScanSnapshot? current = _state.AScan;
        if (session.Phase != SessionPhase.Running || current?.Metadata.RunId != session.RunId) { current = null; }
        if (session.Phase == SessionPhase.Running && visual is not null && visual.Metadata.RunId == session.RunId &&
            visual.Metadata.SourceId == session.SourceId) { current = visual; }
        if (status?.TerminalError is not null) { current = null; }
        AScanCursorState? cursors = current is null ? null : _state.Cursors is null || _state.AScan?.Metadata.RunId != current.Metadata.RunId
            ? AScanCursorMeasurements.Create(current)
            : AScanCursorMeasurements.Reconcile(_state.Cursors, current);
        if (current is not null)
        {
            _timeViewport.ReconcileDomainDeferred(_viewportConsumerId, current.Metadata.RunId, current.Version,
                current.MinimumTimeSeconds, current.MaximumTimeSeconds);
        }
        else if (_state.AScan is not null) { _timeViewport.RemoveConsumerDeferred(_viewportConsumerId); }
        bool changed = ReplaceState(new(session, current, statistics, _state.CommandError, error, status, cursors));
        _timeViewport.PublishDeferredChanges();
        PublishViewportBindingIfChanged();
        if (changed) { RefreshCommands(); }
    }

    private bool ReplaceState(AScanViewState state)
    {
        if (IsClosing || _lifetime.IsClosed || state == _state) { return false; }
        _state = state;
        foreach (string name in new[] { nameof(State), nameof(Session), nameof(AScan), nameof(Points), nameof(Metadata),
            nameof(VisualStatistics), nameof(VisualStatus), nameof(Cursors), nameof(ReceivedFrames),
            nameof(ReleasedFrames), nameof(Error), nameof(VisualError) })
        {
            OnPropertyChanged(name);
        }
        return true;
    }

    private void RefreshCommands()
    {
        if (IsClosing || _lifetime.IsClosed) { return; }
        _start.NotifyCanExecuteChanged();
        _stop.NotifyCanExecuteChanged();
        _toggleCursors.NotifyCanExecuteChanged();
        _resetCursors.NotifyCanExecuteChanged();
        _resetViewport.NotifyCanExecuteChanged();
        _zoomIn.NotifyCanExecuteChanged();
        _zoomOut.NotifyCanExecuteChanged();
        _panLeft.NotifyCanExecuteChanged();
        _panRight.NotifyCanExecuteChanged();
    }

    private void ReportCommandError(Exception error)
    {
        if (!IsClosing && !_lifetime.IsClosed) { ReplaceState(_state with { CommandError = Describe("presentation.command", error) }); }
    }

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        if (IsClosing || _lifetime.IsClosed) { return; }
        try { base.OnPropertyChanged(e); }
        catch (Exception error)
        {
            if (_notificationError is not null || _lifetime.IsClosed) { return; }
            _notificationError = Describe("presentation.observer", error);
            try { base.OnPropertyChanged(new PropertyChangedEventArgs(nameof(NotificationError))); }
            catch { } // Standard multicast handlers can fail; never recurse or strand the update pump.
        }
    }

    private void OnTimeViewportChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is nameof(SharedScanTimeViewport.Viewport) or nameof(SharedScanTimeViewport.RunId))
        {
            OnPropertyChanged(nameof(TimeViewport));
            PublishViewportBindingIfChanged();
            RefreshCommands();
        }
    }

    private void PublishViewportBindingIfChanged()
    {
        ScanTimeViewportBinding? current = ViewportBinding;
        if (EqualityComparer<ScanTimeViewportBinding?>.Default.Equals(_publishedViewportBinding, current)) { return; }
        _publishedViewportBinding = current;
        OnPropertyChanged(nameof(ViewportBinding));
    }

    public ValueTask DisposeAsync()
    {
        TaskCompletionSource? completion = null;
        Task disposal;
        lock (_gate)
        {
            if (_disposal is null)
            {
                completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
                _disposal = completion.Task;
                Volatile.Write(ref _closing, 1);
                _start.DisableForClose();
                _stop.DisableForClose();
            }
            disposal = _disposal;
        }
        if (completion is not null) { _ = CompleteCloseAsync(completion); }
        return new ValueTask(disposal);
    }

    private async Task CompleteCloseAsync(TaskCompletionSource completion)
    {
        try { await CloseAsync().ConfigureAwait(false); completion.TrySetResult(); }
        catch (Exception error) { completion.TrySetException(error); }
    }

    private async Task CloseAsync()
    {
        List<Exception> errors = [];
        _timeViewport.PropertyChanged -= OnTimeViewportChanged;
        foreach (var subscription in new[] { _sessionSubscription, _visualSubscription, _statusSubscription })
        {
            try { subscription?.Dispose(); }
            catch (Exception error) { errors.Add(error); }
        }
        Task commandCompletion = Task.WhenAll(_start.FinishForCloseAsync(), _stop.FinishForCloseAsync());
        try { await commandCompletion.ConfigureAwait(false); }
        catch (Exception error) { errors.Add(error); }
        try
        {
            await _lifetime.InvokeAsync(() =>
            {
                _latestVisual = null;
                NotifyRelayCommandsClosed();
                _lifetime.Close();
            }).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            _lifetime.Close();
            errors.Add(error);
        }
        try { await _timeViewport.RemoveConsumerAsync(_viewportConsumerId).ConfigureAwait(false); }
        catch (Exception error) { errors.Add(error); }
        try { await _lifetime.BarrierAsync().ConfigureAwait(false); }
        catch (Exception error) { errors.Add(error); }
        if (_ownsTimeViewport)
        {
            try { await _timeViewport.DisposeAsync().ConfigureAwait(false); }
            catch (Exception error) { errors.Add(error); }
        }
        if (errors.Count != 0) { throw new AggregateException("ViewModel cleanup failed.", errors); }
    }

    private void NotifyRelayCommandsClosed()
    {
        foreach (RelayCommand command in new[]
        {
            _toggleCursors, _resetCursors, _resetViewport, _zoomIn, _zoomOut, _panLeft, _panRight
        })
        {
            try { command.NotifyCanExecuteChanged(); }
            catch { } // A failing observer must not prevent the remaining commands or cleanup from closing.
        }
    }

    private static UtSourceError Describe(string code, Exception error) =>
        new(code, string.IsNullOrWhiteSpace(error.Message) ? error.GetType().Name : error.Message);

    private sealed class Observer<T>(Action<T> next, Action<Exception> error) : IObserver<T>
    {
        public void OnNext(T value) => next(value);
        public void OnError(Exception failure) => error(failure);
        public void OnCompleted() { }
    }
}
