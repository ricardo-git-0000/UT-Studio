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
    private readonly UiAsyncCommand _start, _stop;
    private readonly RelayCommand _toggleCursors, _resetCursors;
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
        _timeViewport = timeViewport ?? new SharedScanTimeViewport(dispatcher);
        _timeViewport.PropertyChanged += OnTimeViewportChanged;
        _latestSession = session.Snapshot;
        _state = new(_latestSession, null, null, null, null);
        _start = new(_lifetime, StartAsync, () => _state.Session.CanStart && !_start!.IsRunning && !_stop!.IsRunning,
            ReportCommandError, RefreshCommands);
        _stop = new(_lifetime, StopAsync, () => (_state.Session.CanStop || _start.IsRunning) && !_stop!.IsRunning,
            ReportCommandError, RefreshCommands);
        _toggleCursors = new(ToggleCursors, () => _state.Cursors is not null);
        _resetCursors = new(ResetCursors, () => _state.AScan is not null && _state.Cursors is not null);
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
    public UtSourceError? VisualError => _state.VisualStatus?.TerminalError ?? _state.Session.VisualError ?? _state.VisualStatistics?.LastError;
    public UtSourceError? NotificationError => _notificationError;
    public IAsyncRelayCommand StartCommand => _start;
    public IAsyncRelayCommand StopCommand => _stop;
    public IRelayCommand ToggleCursorsCommand => _toggleCursors;
    public IRelayCommand ResetCursorsCommand => _resetCursors;

    public void ZoomTime(double anchorSeconds, double factor) => _timeViewport.Zoom(anchorSeconds, factor);
    public void PanTime(double deltaSeconds) => _timeViewport.Pan(deltaSeconds);
    public void ResetTimeZoom() => _timeViewport.Reset();

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
        if (current is not null) { _timeViewport.ReconcileDomain(current.MinimumTimeSeconds, current.MaximumTimeSeconds); }
        if (ReplaceState(new(session, current, statistics, _state.CommandError, error, status, cursors)))
        { RefreshCommands(); }
    }

    private bool ReplaceState(AScanViewState state)
    {
        if (_lifetime.IsClosed || state == _state) { return false; }
        _state = state;
        foreach (string name in new[] { nameof(State), nameof(Session), nameof(AScan), nameof(Points), nameof(Metadata),
            nameof(VisualStatistics), nameof(VisualStatus), nameof(Cursors), nameof(ReceivedFrames), nameof(ReleasedFrames), nameof(Error), nameof(VisualError) })
        {
            OnPropertyChanged(name);
        }
        return true;
    }

    private void RefreshCommands()
    {
        if (_lifetime.IsClosed) { return; }
        _start.Refresh();
        _stop.Refresh();
        _toggleCursors.NotifyCanExecuteChanged();
        _resetCursors.NotifyCanExecuteChanged();
    }

    private void ReportCommandError(Exception error)
    {
        if (!_lifetime.IsClosed) { ReplaceState(_state with { CommandError = Describe("presentation.command", error) }); }
    }

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        if (_lifetime.IsClosed) { return; }
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
        if (args.PropertyName == nameof(SharedScanTimeViewport.Viewport)) { OnPropertyChanged(nameof(TimeViewport)); }
    }

    public ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (_disposal is null)
            {
                _lifetime.Close();
                _latestVisual = null;
                _disposal = Task.Run(CloseAsync);
            }
            return new ValueTask(_disposal);
        }
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
        try { await Task.WhenAll(_start.CancelPending(), _stop.CancelPending()).ConfigureAwait(false); }
        catch (Exception error) { errors.Add(error); }
        try { await _lifetime.BarrierAsync().ConfigureAwait(false); }
        catch (Exception error) { errors.Add(error); }
        if (errors.Count != 0) { throw new AggregateException("ViewModel cleanup failed.", errors); }
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
