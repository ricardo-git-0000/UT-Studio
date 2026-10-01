using System.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UTStudio.Domain.Acquisition;
using UTStudio.Visualization.Core;
using UTStudio.Visualization.Core.Scan2D;

namespace UTStudio.Presentation;

/// <summary>Neutral B-Scan presentation owner. It borrows source, selection and time coordinators.</summary>
public sealed class BScanViewModel : SafeObservableObject, IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly UiLifetime _lifetime;
    private readonly IBScanVisualSource _source;
    private readonly SharedScanTimeViewport _timeViewport;
    private readonly BScanSelectionCoordinator _selection;
    private readonly string _consumerId = $"bscan-{Guid.NewGuid():N}";
    private readonly IDisposable _subscription;
    private BScanRasterRegion? _visibleRegion;
    private readonly SafeRelayCommand _zoomTimeIn, _zoomTimeOut, _panTimeUp, _panTimeDown, _resetTime,
        _zoomSpatialIn, _zoomSpatialOut, _panSpatialLeft, _panSpatialRight, _resetSpatial, _resetAll,
        _decreaseLiveWindow, _increaseLiveWindow, _refresh, _cancelSelection;
    private readonly SafeParameterRelayCommand _changeResamplingMode;
    private readonly IBScanResamplingPreferenceWriter? _preferenceWriter;
    private BScanViewState _state = BScanViewState.Empty;
    private BScanVisualSnapshot? _latestMetadata;
    private CancellationTokenSource? _rasterCancellation;
    private Task _rasterOperation = Task.CompletedTask;
    private readonly HashSet<Task> _rasterOperations = [];
    private readonly HashSet<Task> _persistenceOperations = [];
    private readonly CancellationTokenSource _persistenceCancellation = new();
    private long _rasterGeneration;
    private int _pixelWidth = 1, _pixelHeight = 1;
    private int _liveWindowColumns;
    private int _maximumLiveWindowColumns = 1;
    private ulong _renderConfigurationVersion;
    private BScanRenderRequest? _lastRequested;
    private long _metadataGeneration, _appliedMetadataGeneration;
    private bool _applyingMetadata;
    private bool _refreshAfterRaster;
    private bool _fullRefreshAfterRaster;
    private int _closing;
    private Task? _disposal;
    private int _recoveryPosted;
    private long _observerErrors;
    // Deterministic post-operation seam for cleanup actions (such as event detachment) that cannot
    // fail through their production contract. Real fallible dependencies are tested separately.
    private readonly Action<string>? _afterCleanupStage;

    public BScanViewModel(IBScanVisualSource source, SharedScanTimeViewport timeViewport,
        BScanSelectionCoordinator selection, IUiDispatcher dispatcher)
        : this(source, timeViewport, selection, dispatcher, Scan2DInterpolationMode.None, null, null) { }

    public BScanViewModel(IBScanVisualSource source, SharedScanTimeViewport timeViewport,
        BScanSelectionCoordinator selection, IUiDispatcher dispatcher, Scan2DInterpolationMode initialResamplingMode,
        IBScanResamplingPreferenceWriter? preferenceWriter)
        : this(source, timeViewport, selection, dispatcher, initialResamplingMode, preferenceWriter, null) { }

    internal BScanViewModel(IBScanVisualSource source, SharedScanTimeViewport timeViewport,
        BScanSelectionCoordinator selection, IUiDispatcher dispatcher, Action<string>? afterCleanupStage)
        : this(source, timeViewport, selection, dispatcher, Scan2DInterpolationMode.None, null, afterCleanupStage) { }

    internal BScanViewModel(IBScanVisualSource source, SharedScanTimeViewport timeViewport,
        BScanSelectionCoordinator selection, IUiDispatcher dispatcher, Scan2DInterpolationMode initialResamplingMode,
        IBScanResamplingPreferenceWriter? preferenceWriter, Action<string>? afterCleanupStage)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _timeViewport = timeViewport ?? throw new ArgumentNullException(nameof(timeViewport));
        _selection = selection ?? throw new ArgumentNullException(nameof(selection));
        _afterCleanupStage = afterCleanupStage;
        _preferenceWriter = preferenceWriter;
        if (!Enum.IsDefined(initialResamplingMode)) { initialResamplingMode = Scan2DInterpolationMode.None; }
        _state = BScanViewState.Empty with { RequestedResamplingMode = initialResamplingMode };
        if (!_source.RegisterConsumer(_consumerId))
        { throw new InvalidOperationException("The B-Scan raster consumer limit has been reached."); }
        _lifetime = new(dispatcher ?? throw new ArgumentNullException(nameof(dispatcher)));
        _timeViewport.PropertyChanged += OnTimeViewportChanged;
        _selection.PropertyChanged += OnSelectionChanged;
        _zoomTimeIn = Command(() => ZoomTime(1.2), HasTime);
        _zoomTimeOut = Command(() => ZoomTime(1 / 1.2), () => HasTime() && _timeViewport.Viewport is { IsReset: false });
        _panTimeUp = Command(() => PanTime(-.1), CanPanTimeUp);
        _panTimeDown = Command(() => PanTime(.1), CanPanTimeDown);
        _resetTime = Command(ResetTime, () => HasTime() && _timeViewport.Viewport is { IsReset: false });
        _zoomSpatialIn = Command(() => ZoomSpatial(1.2m), CanZoomSpatialIn);
        _zoomSpatialOut = Command(() => ZoomSpatial(1m / 1.2m), CanZoomSpatialOut);
        _panSpatialLeft = Command(() => PanSpatial(-.1m), CanPanSpatialLeft);
        _panSpatialRight = Command(() => PanSpatial(.1m), CanPanSpatialRight);
        _resetSpatial = Command(ResetSpatial, () => HasSpatial() && !_state.IsSpatialAutoFit);
        _resetAll = Command(ResetAll, () => HasTime() || HasSpatial());
        _decreaseLiveWindow = Command(() => ChangeLiveWindow(increase: false),
            () => _liveWindowColumns > 1);
        _increaseLiveWindow = Command(() => ChangeLiveWindow(increase: true),
            () => _liveWindowColumns < _maximumLiveWindowColumns);
        _refresh = Command(() => RequestRefresh(force: true, full: true), HasRenderableState);
        _cancelSelection = Command(() => _selection.Cancel(_consumerId), () => _state.Selection.IsPending);
        _changeResamplingMode = new(parameter =>
        {
            if (parameter is Scan2DInterpolationMode mode) SetResamplingMode(mode);
        }, parameter => !IsClosing && !_lifetime.IsClosed && parameter is Scan2DInterpolationMode mode &&
            Enum.IsDefined(mode), _ => Interlocked.Increment(ref _observerErrors));
        try { _subscription = _source.Subscribe(new Observer(this)); }
        catch
        {
            _source.UnregisterConsumer(_consumerId);
            throw;
        }
        try { _source.RequestBootstrap(_consumerId); }
        catch
        {
            _subscription.Dispose();
            _source.UnregisterConsumer(_consumerId);
            throw;
        }
    }

    public BScanViewState State => _state;
    public BScanViewPhase Phase => _state.Phase;
    public string ConsumerId => _consumerId;
    public ScanTimeViewport? TimeViewport => _timeViewport.Viewport;
    public long ObserverErrorCount => Interlocked.Read(ref _observerErrors);
    public int LiveWindowColumns => _liveWindowColumns;
    public int MaximumLiveWindowColumns => _maximumLiveWindowColumns;
    public IRelayCommand ZoomTimeInCommand => _zoomTimeIn;
    public IRelayCommand ZoomTimeOutCommand => _zoomTimeOut;
    public IRelayCommand PanTimeUpCommand => _panTimeUp;
    public IRelayCommand PanTimeDownCommand => _panTimeDown;
    public IRelayCommand ResetTimeCommand => _resetTime;
    public IRelayCommand ZoomSpatialInCommand => _zoomSpatialIn;
    public IRelayCommand ZoomSpatialOutCommand => _zoomSpatialOut;
    public IRelayCommand PanSpatialLeftCommand => _panSpatialLeft;
    public IRelayCommand PanSpatialRightCommand => _panSpatialRight;
    public IRelayCommand ResetSpatialCommand => _resetSpatial;
    public IRelayCommand ResetAllCommand => _resetAll;
    public IRelayCommand DecreaseLiveWindowCommand => _decreaseLiveWindow;
    public IRelayCommand IncreaseLiveWindowCommand => _increaseLiveWindow;
    public IRelayCommand RefreshCommand => _refresh;
    public IRelayCommand CancelSelectionCommand => _cancelSelection;
    public IRelayCommand ChangeResamplingModeCommand => _changeResamplingMode;
    public IReadOnlyList<BScanResamplingOption> AvailableResamplingModes => BScanResamplingOptions.All;
    public Scan2DInterpolationMode RequestedResamplingMode
    {
        get => _state.RequestedResamplingMode;
        set => SetResamplingMode(value);
    }

    public void SetResamplingMode(Scan2DInterpolationMode mode)
    {
        _lifetime.Post(() =>
        {
            if (!Enum.IsDefined(mode))
            { ReplaceState(_state with { ConfigurationError = "Modo de remuestreo no compatible." }); return; }
            ChangeResamplingMode(mode);
        });
    }

    private void ChangeResamplingMode(Scan2DInterpolationMode mode)
    {
        if (IsClosing || mode == _state.RequestedResamplingMode) { return; }
        ReplaceState(_state with
        {
            RequestedResamplingMode = mode,
            IsRenderPending = HasRenderableState(),
            RequiresFullRefresh = true,
            BScanVisualError = null,
            PersistenceError = null,
            ConfigurationError = null
        });
        OnPropertyChanged(nameof(RequestedResamplingMode));
        PersistResamplingMode(mode);
        RequestRefresh(force: true, full: true);
    }

    private void PersistResamplingMode(Scan2DInterpolationMode mode)
    {
        if (_preferenceWriter is null) { return; }
        Task operation = PersistResamplingModeAsync(mode);
        lock (_gate) _persistenceOperations.Add(operation);
        _ = operation.ContinueWith(completed =>
        {
            lock (_gate) _persistenceOperations.Remove(completed);
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    private async Task PersistResamplingModeAsync(Scan2DInterpolationMode mode)
    {
        string? error;
        try { error = await _preferenceWriter!.SaveAsync(mode, _persistenceCancellation.Token).ConfigureAwait(false); }
        catch (Exception failure) { error = Message(failure); }
        await _lifetime.InvokeAsync(() =>
        {
            if (!IsClosing && mode == _state.RequestedResamplingMode)
            { ReplaceState(_state with { PersistenceError = error }); }
        }).ConfigureAwait(false);
    }

    public void SetRasterSize(int pixelWidth, int pixelHeight, ulong renderConfigurationVersion = 0)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(pixelWidth, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(pixelWidth, 1024);
        ArgumentOutOfRangeException.ThrowIfLessThan(pixelHeight, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(pixelHeight, 1024);
        _lifetime.Post(() =>
        {
            if (IsClosing) { return; }
            _pixelWidth = pixelWidth;
            _pixelHeight = pixelHeight;
            _renderConfigurationVersion = renderConfigurationVersion;
            RequestRefresh();
        });
    }

    public void ZoomTimeAt(double anchorSeconds, double factor) => _lifetime.Post(() =>
    { if (!IsClosing) { _timeViewport.Zoom(anchorSeconds, factor); } });
    public void PanTimeBy(double deltaSeconds) => _lifetime.Post(() =>
    { if (!IsClosing) { _timeViewport.Pan(deltaSeconds); } });
    public void ResetTimeViewport() => _lifetime.Post(() =>
    { if (!IsClosing) { _timeViewport.Reset(); } });

    public void ZoomSpatialAt(decimal anchorRatio, decimal factor) => _lifetime.Post(() =>
    {
        if (_state is not { SpatialViewport: { } viewport, ResidentRange: { } domain }) { return; }
        ReplaceState(_state with
        {
            SpatialViewport = BScanSpatialViewportOperations.ZoomAt(viewport, domain,
            anchorRatio, factor),
            IsSpatialAutoFit = false,
            RequiresFullRefresh = true
        });
        RequestRefresh();
    });

    public void PanSpatialBy(decimal delta) => _lifetime.Post(() =>
    {
        if (_state is not { SpatialViewport: { } viewport, ResidentRange: { } domain }) { return; }
        ReplaceState(_state with
        {
            SpatialViewport = BScanSpatialViewportOperations.Pan(viewport, domain, delta),
            IsSpatialAutoFit = false,
            RequiresFullRefresh = true
        });
        RequestRefresh();
    });

    public async Task SelectColumnAsync(BScanTraceIdentity identity)
    {
        Task selection = Task.CompletedTask;
        await _lifetime.InvokeAsync(() =>
        {
            if (IsClosing || _state.RunId != identity.RunId) { return; }
            ReplaceState(_state with { SelectedColumn = identity });
            selection = _selection.SelectAsync(_consumerId, identity);
        }).ConfigureAwait(false);
        await selection.ConfigureAwait(false);
        await _lifetime.InvokeAsync(ApplySelection).ConfigureAwait(false);
    }

    public async Task SelectColumnAsync(ulong acquisitionIndex)
    {
        Task selection = Task.CompletedTask;
        await _lifetime.InvokeAsync(() =>
        {
            if (IsClosing || _state.RunId is not { } run || _state.ResidentRange is not { } resident ||
                acquisitionIndex < resident.First || acquisitionIndex > resident.Last) { return; }
            selection = _selection.SelectAsync(_consumerId, run, acquisitionIndex);
        }).ConfigureAwait(false);
        await selection.ConfigureAwait(false);
        await _lifetime.InvokeAsync(ApplySelection).ConfigureAwait(false);
    }

    public void CancelSelection() => _lifetime.Post(() => _selection.Cancel(_consumerId));
    public void Refresh() => _lifetime.Post(() => RequestRefresh(force: true, full: true));
    public void ReportAcquisitionError(string? error) => _lifetime.Post(() =>
        ReplaceState(_state with { AcquisitionError = error }));
    public void ReportIngressError(string? error) => _lifetime.Post(() =>
        ReplaceState(_state with { BScanIngressError = error }));
    public void ReportUiInteractionError(string? error) => _lifetime.Post(() =>
        ReplaceState(_state with { UiInteractionError = error }));
    public void ReportPersistenceError(string? error) => _lifetime.Post(() =>
        ReplaceState(_state with { PersistenceError = error }));

    public bool TryCopyPresentedRegion(BScanPresentedRegionToken token, Span<byte> destination,
        int destinationStride)
    {
        bool stale;
        lock (_gate)
        {
            if (IsClosing || _visibleRegion is null || _state.Region is not { } descriptor ||
                descriptor.Token != token || descriptor.PresentationGeneration != _rasterGeneration ||
                descriptor.Snapshot != _visibleRegion.Snapshot || descriptor.Snapshot.RunId != _state.RunId ||
                descriptor.Snapshot.StoreRevision != _state.DisplayedRevision?.StoreRevision)
            { stale = true; }
            else
            {
                stale = false;
                if (destinationStride < checked(descriptor.X + descriptor.Width))
                { throw new ArgumentException("The destination stride is too small.", nameof(destinationStride)); }
                int required = checked((descriptor.Y + descriptor.Height - 1) * destinationStride +
                    descriptor.X + descriptor.Width);
                if (destination.Length < required)
                { throw new ArgumentException("The destination is too small.", nameof(destination)); }
                ReadOnlySpan<byte> source = _visibleRegion.Pixels.Span;
                for (int row = 0; row < descriptor.Height; row++)
                {
                    source.Slice(checked(row * descriptor.Stride), descriptor.Width).CopyTo(
                        destination.Slice(checked((descriptor.Y + row) * destinationStride + descriptor.X),
                            descriptor.Width));
                }
            }
        }
        if (stale && !IsClosing && Interlocked.Exchange(ref _recoveryPosted, 1) == 0)
        { _lifetime.Post(() => RequestRefresh(force: true, full: true)); }
        return !stale;
    }

    public bool TryVisitPresentedRegion(BScanPresentedRegionToken token, BScanIntensityVisitor visitor)
    {
        ArgumentNullException.ThrowIfNull(visitor);
        lock (_gate)
        {
            if (IsClosing || _visibleRegion is null || _state.Region is not { } descriptor ||
                descriptor.Token != token || descriptor.PresentationGeneration != _rasterGeneration ||
                descriptor.Snapshot != _visibleRegion.Snapshot)
            { return false; }
            visitor(_visibleRegion.Pixels.Span, _visibleRegion.ValidityMask.Span, descriptor.Stride);
            return true;
        }
    }

    private SafeRelayCommand Command(Action execute, Func<bool> canExecute) =>
        new(() => { if (!IsClosing) { _lifetime.Post(() => { if (!IsClosing) { execute(); } }); } },
            () => !IsClosing && !_lifetime.IsClosed && canExecute(),
            _ => Interlocked.Increment(ref _observerErrors));

    private bool IsClosing => Volatile.Read(ref _closing) != 0;
    private bool HasTime() => _timeViewport.Viewport is not null;
    private bool HasSpatial() => _state.SpatialViewport is not null && _state.ResidentRange is not null;
    private bool HasRenderableState() => HasTime() && HasSpatial() && _state.RunId is not null;

    private void ZoomTime(double factor)
    {
        if (_timeViewport.Viewport is { } viewport)
        { _timeViewport.Zoom((viewport.VisibleMinimumSeconds + viewport.VisibleMaximumSeconds) / 2, factor); }
    }
    private void PanTime(double fraction)
    { if (_timeViewport.Viewport is { } viewport) { _timeViewport.Pan(viewport.VisibleSpanSeconds * fraction); } }
    private void ResetTime() => _timeViewport.Reset();
    private bool CanPanTimeUp() => _timeViewport.Viewport is { } v && v.VisibleMinimumSeconds > v.DomainMinimumSeconds;
    private bool CanPanTimeDown() => _timeViewport.Viewport is { } v && v.VisibleMaximumSeconds < v.DomainMaximumSeconds;
    private void ZoomSpatial(decimal factor) => ZoomSpatialAt(.5m, factor);
    private bool CanZoomSpatialIn() => _state is { SpatialViewport: { } v, ResidentRange: { } d } &&
        v.Span > Math.Max(1m, ((decimal)(d.Last - d.First) + 1) / BScanSpatialViewportOperations.MaximumZoom);
    private bool CanZoomSpatialOut() => _state is { SpatialViewport: { } v, ResidentRange: { } d } &&
        v != BScanSpatialViewportOperations.Reset(d);
    private void PanSpatial(decimal fraction)
    { if (_state.SpatialViewport is { } v) { PanSpatialBy(v.Span * fraction); } }
    private bool CanPanSpatialLeft() => _state is { SpatialViewport: { } v, ResidentRange: { } d } &&
        v.MinimumOffset > BScanSpatialViewportOperations.RelativeOffset(d.First, v.OriginAcquisitionIndex);
    private bool CanPanSpatialRight() => _state is { SpatialViewport: { } v, ResidentRange: { } d } &&
        v.MaximumOffset < BScanSpatialViewportOperations.RelativeOffset(d.Last, v.OriginAcquisitionIndex) + 1;
    private void ResetSpatial()
    {
        if (_state.ResidentRange is not { } domain) { return; }
        ReplaceState(_state with
        {
            SpatialViewport = BScanSpatialViewportOperations.Reset(domain),
            IsSpatialAutoFit = true,
            RequiresFullRefresh = true
        });
        RequestRefresh(force: true);
    }
    private void ResetAll() { ResetTime(); ResetSpatial(); }

    private void ChangeLiveWindow(bool increase)
    {
        if (_latestMetadata is null || _state.ResidentRange is not { } resident) { return; }
        int next = increase
            ? (int)Math.Min((long)_maximumLiveWindowColumns, Math.Max(1L, (long)_liveWindowColumns * 2))
            : Math.Max(1, _liveWindowColumns / 2);
        if (next == _liveWindowColumns) { return; }
        _liveWindowColumns = next;
        ReplaceState(_state with
        {
            SpatialViewport = CreateLiveWindow(resident, next),
            IsSpatialAutoFit = true,
            RequiresFullRefresh = true
        });
        OnPropertyChanged(nameof(LiveWindowColumns));
        RefreshCommands();
        RequestRefresh(force: true, full: true);
    }

    private void Receive(BScanVisualUpdate update)
    {
        if (update.Snapshot.ConsumerId != _consumerId) { return; }
        long generation = Interlocked.Increment(ref _metadataGeneration);
        _lifetime.Post(() => ApplyMetadata(update.Snapshot, generation));
    }
    private void ReceiveError(Exception error) => _lifetime.Post(() =>
        ReplaceState(_state with { BScanVisualError = Message(error) }));

    private void ApplyMetadata(BScanVisualSnapshot snapshot, long generation)
    {
        if (IsClosing || generation <= _appliedMetadataGeneration || (_latestMetadata is { } prior && prior.RunId == snapshot.RunId &&
            snapshot.SnapshotVersion <= prior.SnapshotVersion)) { return; }
        _appliedMetadataGeneration = generation;
        bool newRun = _latestMetadata?.RunId != snapshot.RunId;
        _latestMetadata = snapshot;
        if (newRun)
        {
            _ = _selection.SetRunAsync(snapshot.RunId);
            BScanRasterRegion? previousRegion;
            lock (_gate) { previousRegion = _visibleRegion; _visibleRegion = null; }
            previousRegion?.Dispose();
        }
        BScanVisualDiagnostics diagnostics = _source.Diagnostics;
        int maximumLiveWindow = Math.Max(diagnostics.EffectiveCapacity, 1);
        int liveWindow = _liveWindowColumns == 0
            ? maximumLiveWindow
            : Math.Clamp(_liveWindowColumns, 1, maximumLiveWindow);
        if (_maximumLiveWindowColumns != maximumLiveWindow)
        {
            _maximumLiveWindowColumns = maximumLiveWindow;
            OnPropertyChanged(nameof(MaximumLiveWindowColumns));
        }
        if (_liveWindowColumns != liveWindow)
        {
            _liveWindowColumns = liveWindow;
            OnPropertyChanged(nameof(LiveWindowColumns));
        }
        BScanSpatialViewport spatial = newRun || _state.SpatialViewport is null || _state.IsSpatialAutoFit
            ? CreateLiveWindow(snapshot.ResidentRange, liveWindow)
            : BScanSpatialViewportOperations.Reconcile(_state.SpatialViewport.Value, snapshot.ResidentRange);
        // Metadata wakes the consumer; the subsequent region request carries the authoritative
        // displayed identity. Do not discard that base merely because a queued metadata request
        // was created before the latest region reached the UI.
        bool metadataRequiresFull = snapshot.RequiresFullRefresh && _state.DisplayedRevision is null;
        _applyingMetadata = true;
        _timeViewport.ReconcileDomainDeferred(_consumerId, snapshot.RunId, snapshot.SnapshotVersion,
            snapshot.TemporalDomain.DomainMinimumSeconds, snapshot.TemporalDomain.DomainMaximumSeconds);
        var binding = _timeViewport.RunId == snapshot.RunId && _timeViewport.Viewport is { } viewport
            ? new ScanTimeViewportBinding(snapshot.RunId, snapshot.SnapshotVersion, viewport) : null;
        ReplaceState(_state with
        {
            Phase = BScanViewPhase.Ready,
            RunId = snapshot.RunId,
            SnapshotVersion = snapshot.SnapshotVersion,
            StoreRevision = snapshot.StoreRevision,
            Diagnostics = diagnostics,
            ResidentRange = snapshot.ResidentRange,
            TemporalDomain = snapshot.TemporalDomain,
            SpatialViewport = spatial,
            TimeViewportBinding = binding,
            RequiresFullRefresh = newRun || metadataRequiresFull || _state.RequiresFullRefresh,
            Selection = newRun ? BScanSelectionState.Empty with { RunId = snapshot.RunId } : _selection.State,
            SelectionError = newRun ? null : _selection.State.SelectionError,
            IsSpatialAutoFit = newRun || _state.IsSpatialAutoFit,
            Region = newRun ? null : _state.Region,
            DisplayedRevision = newRun ? null : _state.DisplayedRevision,
            PresentedResamplingMode = newRun ? null : _state.PresentedResamplingMode,
            ConfirmedRenderSignature = newRun ? null : _state.ConfirmedRenderSignature,
            IsRenderPending = true
        });
        _timeViewport.PublishDeferredChanges();
        _applyingMetadata = false;
        PublishTimeBinding();
        _selection.UpdateResidency(_consumerId, snapshot.RunId, snapshot.SnapshotVersion, snapshot.ResidentRange);
        RefreshCommands();
        // The render request carries the displayed base, not the target store revision. A prior
        // no-change request can therefore compare equal after new metadata arrives; metadata is
        // authoritative and must bypass that request-level deduplication.
        RequestRefresh(force: true);
    }

    private static BScanSpatialViewport CreateLiveWindow(BScanAcquisitionRange residentRange, int visibleColumns)
    {
        decimal residentCount = checked((decimal)(residentRange.Last - residentRange.First) + 1);
        ulong width = (ulong)Math.Max(visibleColumns, 1);
        ulong first = residentCount <= width
            ? residentRange.First
            : residentRange.Last - width + 1;
        return new(first, 0, width);
    }

    private void RequestRefresh(bool force = false, bool full = false)
    {
        if (!HasRenderableState() || _latestMetadata is null || _timeViewport.Viewport is not { } time) { return; }
        bool surfaceBootstrap;
        lock (_gate)
        {
            if (!_rasterOperation.IsCompleted && !full)
            {
                _refreshAfterRaster = true;
                return;
            }
            surfaceBootstrap = NeedsCurrentRasterUnderLock();
        }
        bool requestFull = full || surfaceBootstrap;
        int rasterWidth = CurrentRasterWidth();
        var request = new BScanRenderRequest(_latestMetadata.RunId, _state.SpatialViewport!.Value, time,
            rasterWidth, _pixelHeight, _renderConfigurationVersion,
            requestFull ? null : _state.DisplayedRevision?.SnapshotVersion,
            requestFull ? null : _state.DisplayedRevision?.StoreRevision,
            requestFull ? null : _state.DisplayedRevision?.WrapGeneration, _consumerId,
            !requestFull && _state.Region is { } displayed ? new(displayed.Snapshot.Stream,
                _state.DisplayedRevision!.Value, new(displayed.Snapshot.SpatialViewport,
                    displayed.Snapshot.TimeViewport, displayed.Snapshot.RasterWidth,
                    displayed.Snapshot.RasterHeight, displayed.Snapshot.RenderConfigurationVersion,
                    displayed.Snapshot.Resampling)) : null,
            _state.IsSpatialAutoFit, new Scan2DResamplingOptions(_state.RequestedResamplingMode));
        if (!force && request == _lastRequested) { return; }
        _lastRequested = request;
        if (!_state.IsRenderPending) ReplaceState(_state with { IsRenderPending = true });
        CancellationTokenSource cancellation;
        CancellationTokenSource? previousCancellation;
        long generation;
        Task previous;
        TaskCompletionSource completion;
        lock (_gate)
        {
            if (IsClosing) { return; }
            previous = _rasterOperation;
            previousCancellation = _rasterCancellation;
            cancellation = new();
            _rasterCancellation = cancellation;
            generation = ++_rasterGeneration;
            completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _rasterOperation = completion.Task;
            _rasterOperations.Add(completion.Task);
        }
        try { previousCancellation?.Cancel(); }
        catch { Interlocked.Increment(ref _observerErrors); }
        try { _source.RequestRender(request); }
        catch (Exception error)
        {
            lock (_gate) { _rasterOperations.Remove(completion.Task); }
            cancellation.Dispose();
            completion.TrySetResult();
            ReplaceState(_state with { BScanVisualError = Message(error), IsRenderPending = false });
            return;
        }
        _ = CompleteRegionAsync(previous, request, generation, cancellation, completion);
    }

    private async Task CompleteRegionAsync(Task previous, BScanRenderRequest request, long generation,
        CancellationTokenSource cancellation, TaskCompletionSource completion)
    {
        Exception? failure = null;
        try
        {
            try { await previous.ConfigureAwait(false); } catch { }
            if (!IsClosing && generation == Volatile.Read(ref _rasterGeneration))
            { await ResolveRegionAsync(request, generation, cancellation).ConfigureAwait(false); }
        }
        catch (Exception error) { failure = error; }
        finally
        {
            cancellation.Dispose();
            bool refreshAfterRaster;
            bool fullRefreshAfterRaster;
            lock (_gate)
            {
                _rasterOperations.Remove(completion.Task);
                refreshAfterRaster = _refreshAfterRaster;
                fullRefreshAfterRaster = _fullRefreshAfterRaster;
                _refreshAfterRaster = false;
                _fullRefreshAfterRaster = false;
            }
            if (failure is null) { completion.TrySetResult(); }
            else { completion.TrySetException(failure); }
            if (refreshAfterRaster && !IsClosing)
            {
                _lifetime.Post(() => RequestRefresh(force: true,
                    full: fullRefreshAfterRaster || _state.Region is null));
            }
        }
    }

    private async Task ResolveRegionAsync(BScanRenderRequest request, long generation,
        CancellationTokenSource cancellation)
    {
        BScanRasterResult? result = null;
        string? operationError = null;
        // A visual source may implement ValueTask synchronously. Invoke it on the worker scheduler so
        // CPU-heavy rasterization can never run inline on the UI dispatcher.
        try
        {
            result = await Task.Run(async () =>
                await _source.GetRegionAsync(request, cancellation.Token).ConfigureAwait(false),
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception error) { operationError = Message(error); }
        BScanRasterRegion? owned = result?.Region;
        BScanPresentedRegion? managed = owned is null ? null : new(new(Guid.NewGuid()), owned.Snapshot,
            owned.X, owned.Y, owned.Width, owned.Height, owned.Stride, owned.Format, generation);
        BScanRasterRegion? retired = null;
        bool accepted = false;
        await _lifetime.InvokeAsync(() =>
        {
            bool initialAutoFit;
            lock (_gate) { initialAutoFit = NeedsCurrentRasterUnderLock() && _state.IsSpatialAutoFit; }
            if (IsClosing || generation != _rasterGeneration || managed is null ||
                managed.Snapshot.ConsumerId != _consumerId || managed.Snapshot.RunId != _state.RunId ||
                _latestMetadata is null || managed.Snapshot.Stream != _latestMetadata.Stream ||
                (!initialAutoFit && !_state.IsSpatialAutoFit &&
                    managed.Snapshot.SpatialViewport != _state.SpatialViewport) ||
                managed.Snapshot.TimeViewport != _timeViewport.Viewport ||
                EffectiveResampling(managed.Snapshot.Resampling) != request.EffectiveResampling ||
                EffectiveResampling(managed.Snapshot.Resampling).Mode != _state.RequestedResamplingMode) { return; }
            if (!managed.Snapshot.RequiresFullRefresh && _state.DisplayedRevision is { } displayed &&
                managed.Snapshot.BaseStoreRevision != displayed.StoreRevision)
            {
                ReplaceState(_state with { RequiresFullRefresh = true });
                RequestRefresh(force: true, full: true);
                return;
            }
            BScanViewState nextState;
            lock (_gate)
            {
                if (IsClosing || generation != _rasterGeneration) { return; }
                retired = _visibleRegion;
                _visibleRegion = owned;
                accepted = true;
                var revision = new BScanVisualRevision(managed.Snapshot.SnapshotVersion,
                    managed.Snapshot.StoreRevision, managed.Snapshot.WrapGeneration);
                nextState = _state with
                {
                    Region = managed,
                    DisplayedRevision = revision,
                    SnapshotVersion = Math.Max(_state.SnapshotVersion.GetValueOrDefault(),
                        managed.Snapshot.SnapshotVersion),
                    StoreRevision = Math.Max(_state.StoreRevision.GetValueOrDefault(),
                        managed.Snapshot.StoreRevision),
                    SpatialViewport = _state.IsSpatialAutoFit
                        ? managed.Snapshot.SpatialViewport
                        : _state.SpatialViewport,
                    TimeViewportBinding = new(managed.Snapshot.RunId,
                        Math.Max(_state.TimeViewportBinding?.SnapshotVersion ?? 0,
                            managed.Snapshot.SnapshotVersion),
                        _state.TimeViewportBinding?.Viewport ?? managed.Snapshot.TimeViewport),
                    RequiresFullRefresh = false,
                    BScanVisualError = null,
                    PresentedResamplingMode = EffectiveResampling(managed.Snapshot.Resampling).Mode,
                    ConfirmedRenderSignature = new(managed.Snapshot.SpatialViewport, managed.Snapshot.TimeViewport,
                        managed.Snapshot.RasterWidth, managed.Snapshot.RasterHeight,
                        managed.Snapshot.RenderConfigurationVersion, EffectiveResampling(managed.Snapshot.Resampling)),
                    IsRenderPending = false
                };
                _state = nextState;
                Volatile.Write(ref _recoveryPosted, 0);
            }
            OnPropertyChanged(nameof(State));
            OnPropertyChanged(nameof(Phase));
            _source.AcknowledgeDisplayed(_consumerId, new(managed.Snapshot.Stream,
                new(managed.Snapshot.SnapshotVersion, managed.Snapshot.StoreRevision,
                    managed.Snapshot.WrapGeneration),
                new(managed.Snapshot.SpatialViewport, managed.Snapshot.TimeViewport,
                    managed.Snapshot.RasterWidth, managed.Snapshot.RasterHeight,
                    managed.Snapshot.RenderConfigurationVersion, managed.Snapshot.Resampling)));
        }).ConfigureAwait(false);
        if (!accepted) { owned?.Dispose(); }
        retired?.Dispose();
        string? visualError = operationError ?? (result?.Kind == BScanRasterResultKind.Faulted ? result.Error : null);
        if (managed is null && visualError is not null)
        {
            await _lifetime.InvokeAsync(() =>
            {
                if (!IsClosing && generation == _rasterGeneration && request.RunId == _state.RunId)
                { ReplaceState(_state with { BScanVisualError = visualError, IsRenderPending = false }); }
            }).ConfigureAwait(false);
        }
        else if (managed is null && result?.Kind == BScanRasterResultKind.Stale)
        {
            await _lifetime.InvokeAsync(() =>
            {
                if (!IsClosing && generation == _rasterGeneration && request.RunId == _state.RunId)
                { RequestRefresh(force: true); }
            }).ConfigureAwait(false);
        }
        else if (managed is null)
        {
            await _lifetime.InvokeAsync(() =>
            {
                if (!IsClosing && generation == _rasterGeneration)
                { ReplaceState(_state with { IsRenderPending = false }); }
            }).ConfigureAwait(false);
        }
        lock (_gate) { if (generation == _rasterGeneration) { _rasterCancellation = null; } }
    }

    private int CurrentRasterWidth()
    {
        if (_state.RequestedResamplingMode == Scan2DInterpolationMode.None &&
            _state.IsSpatialAutoFit && _state.ResidentRange is { } resident)
        {
            decimal residentCount = checked((decimal)(resident.Last - resident.First) + 1);
            if (residentCount >= _liveWindowColumns && _liveWindowColumns <= 1024)
            { return _liveWindowColumns; }
        }
        return _pixelWidth;
    }

    private bool NeedsCurrentRasterUnderLock() => _visibleRegion is null ||
        _visibleRegion.Snapshot.RasterWidth != CurrentRasterWidth() ||
        _visibleRegion.Snapshot.RasterHeight != _pixelHeight ||
        _visibleRegion.Snapshot.RenderConfigurationVersion != _renderConfigurationVersion;

    private void OnTimeViewportChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is not (nameof(SharedScanTimeViewport.Viewport) or nameof(SharedScanTimeViewport.RunId))) { return; }
        _lifetime.Post(() =>
        {
            PublishTimeBinding();
            if (!_applyingMetadata) { RequestRefresh(); }
            RefreshCommands();
        });
    }
    private void PublishTimeBinding()
    {
        ScanTimeViewportBinding? binding = _state.RunId is { } run && _state.SnapshotVersion is { } version &&
            _timeViewport.RunId == run && _timeViewport.Viewport is { } viewport
            ? new(run, version, viewport) : null;
        ReplaceState(_state with { TimeViewportBinding = binding });
        OnPropertyChanged(nameof(TimeViewport));
    }
    private void OnSelectionChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName != nameof(BScanSelectionCoordinator.State)) { return; }
        _lifetime.Post(ApplySelection);
    }

    private void ApplySelection()
    {
        BScanSelectionState selection = _selection.State;
        bool compatible = selection.RunId is null || selection.RunId == _state.RunId;
        if (compatible && selection.Identity is { } identity && _latestMetadata?.Stream is { } stream &&
            stream.RunId.IsValid)
        {
            compatible = identity.SourceId == stream.SourceId && identity.RunId == stream.RunId &&
                identity.PhysicalChannelId == stream.PhysicalChannelId;
        }
        if (!compatible) { return; }
        ReplaceState(_state with
        {
            Selection = selection,
            SelectedColumn = selection.Identity ?? _state.SelectedColumn,
            SelectionError = selection.SelectionError
        });
        RefreshCommands();
    }

    private void ReplaceState(BScanViewState state)
    {
        if (IsClosing || _lifetime.IsClosed || state == _state) { return; }
        _state = state;
        OnPropertyChanged(nameof(State));
        OnPropertyChanged(nameof(Phase));
    }
    private void RefreshCommands()
    {
        if (IsClosing || _lifetime.IsClosed) { return; }
        foreach (SafeRelayCommand command in Commands) { command.NotifyCanExecuteChanged(); }
        _changeResamplingMode.NotifyCanExecuteChanged();
    }
    private SafeRelayCommand[] Commands => [_zoomTimeIn, _zoomTimeOut, _panTimeUp, _panTimeDown, _resetTime,
        _zoomSpatialIn, _zoomSpatialOut, _panSpatialLeft, _panSpatialRight, _resetSpatial, _resetAll,
        _decreaseLiveWindow, _increaseLiveWindow, _refresh, _cancelSelection];

    public ValueTask DisposeAsync()
    {
        TaskCompletionSource? completion = null;
        lock (_gate)
        {
            if (_disposal is null)
            {
                Volatile.Write(ref _closing, 1);
                completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
                _disposal = completion.Task;
                _rasterGeneration++;
            }
        }
        if (completion is not null)
        { _ = CloseAsync(completion); }
        return new(_disposal!);
    }

    private async Task CloseAsync(TaskCompletionSource completion)
    {
        List<CleanupStageFailure> failures = [];
        void Capture(string stage, Action action)
        {
            try { action(); _afterCleanupStage?.Invoke(stage); }
            catch (Exception error) { failures.Add(new(stage, error)); }
        }
        async Task CaptureAsync(string stage, Func<Task> action)
        {
            try { await action().ConfigureAwait(false); _afterCleanupStage?.Invoke(stage); }
            catch (Exception error) { failures.Add(new(stage, error)); }
        }

        Capture("subscription", _subscription.Dispose);
        Capture("time-viewport-events", () => _timeViewport.PropertyChanged -= OnTimeViewportChanged);
        Capture("selection-events", () => _selection.PropertyChanged -= OnSelectionChanged);
        Capture("selection-cancel", () => _selection.Cancel(_consumerId));
        CancellationTokenSource? rasterCancellation;
        lock (_gate) { rasterCancellation = _rasterCancellation; }
        Capture("raster-cancel", () =>
        {
            try { rasterCancellation?.Cancel(); }
            catch (ObjectDisposedException) { }
        });
        Task[] rasters;
        lock (_gate) { rasters = [.. _rasterOperations]; }
        await CaptureAsync("raster-operations", () => Task.WhenAll(rasters)).ConfigureAwait(false);
        Task[] persistence;
        lock (_gate) { persistence = [.. _persistenceOperations]; }
        await CaptureAsync("preference-writes", async () =>
        {
            Task drain = Task.WhenAll(persistence);
            if (await Task.WhenAny(drain, Task.Delay(TimeSpan.FromSeconds(5))).ConfigureAwait(false) != drain)
            {
                _persistenceCancellation.Cancel();
                _ = drain.ContinueWith(completed => { _ = completed.Exception; }, CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously | TaskContinuationOptions.OnlyOnFaulted,
                    TaskScheduler.Default);
                Interlocked.Increment(ref _observerErrors);
                return;
            }
            await drain.ConfigureAwait(false);
        }).ConfigureAwait(false);
        await CaptureAsync("ui-state", () => _lifetime.InvokeAsync(() =>
        {
            foreach (SafeRelayCommand command in Commands) { command.NotifyCanExecuteChanged(); }
            _changeResamplingMode.NotifyCanExecuteChanged();
            _state = _state with { Phase = BScanViewPhase.Closed, Region = null };
            OnPropertyChanged(nameof(State));
            OnPropertyChanged(nameof(Phase));
        })).ConfigureAwait(false);
        await CaptureAsync("time-viewport-consumer", () => _timeViewport.RemoveConsumerAsync(_consumerId)).ConfigureAwait(false);
        BScanRasterRegion? visible;
        lock (_gate) { visible = _visibleRegion; _visibleRegion = null; }
        Capture("visible-region", () => visible?.Dispose());
        Capture("raster-consumer", () => _source.UnregisterConsumer(_consumerId));
        await CaptureAsync("ui-barrier", _lifetime.BarrierAsync).ConfigureAwait(false);
        Capture("ui-lifetime", _lifetime.Close);
        Capture("preference-cancellation", _persistenceCancellation.Dispose);
        if (failures.Count == 0) { completion.TrySetResult(); }
        else { completion.TrySetException(new CleanupFailureException("B-Scan ViewModel cleanup failed.", failures[0], failures.Skip(1).ToArray())); }
    }

    private static string Message(Exception error) => string.IsNullOrWhiteSpace(error.Message)
        ? error.GetType().Name : error.Message;
    private static Scan2DResamplingOptions EffectiveResampling(Scan2DResamplingOptions options) =>
        options == default ? Scan2DResamplingOptions.Default : options;

    protected override void OnObserverFailure(Exception error) => Interlocked.Increment(ref _observerErrors);

    private sealed class Observer(BScanViewModel owner) : IObserver<BScanVisualUpdate>
    {
        public void OnNext(BScanVisualUpdate value) => owner.Receive(value);
        public void OnError(Exception error) => owner.ReceiveError(error);
        public void OnCompleted() { }
    }
}
