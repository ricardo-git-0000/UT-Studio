using System.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UTStudio.Domain.Acquisition;
using UTStudio.Visualization.Core;

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
        _refresh, _cancelSelection;
    private BScanViewState _state = BScanViewState.Empty;
    private BScanVisualSnapshot? _latestMetadata;
    private CancellationTokenSource? _rasterCancellation;
    private Task _rasterOperation = Task.CompletedTask;
    private readonly HashSet<Task> _rasterOperations = [];
    private long _rasterGeneration;
    private int _pixelWidth = 1, _pixelHeight = 1;
    private ulong _renderConfigurationVersion;
    private BScanRenderRequest? _lastRequested;
    private long _metadataGeneration, _appliedMetadataGeneration;
    private bool _applyingMetadata;
    private int _closing;
    private Task? _disposal;
    private int _recoveryPosted;
    private long _observerErrors;
    // Deterministic post-operation seam for cleanup actions (such as event detachment) that cannot
    // fail through their production contract. Real fallible dependencies are tested separately.
    private readonly Action<string>? _afterCleanupStage;

    public BScanViewModel(IBScanVisualSource source, SharedScanTimeViewport timeViewport,
        BScanSelectionCoordinator selection, IUiDispatcher dispatcher)
        : this(source, timeViewport, selection, dispatcher, null) { }

    internal BScanViewModel(IBScanVisualSource source, SharedScanTimeViewport timeViewport,
        BScanSelectionCoordinator selection, IUiDispatcher dispatcher, Action<string>? afterCleanupStage)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _timeViewport = timeViewport ?? throw new ArgumentNullException(nameof(timeViewport));
        _selection = selection ?? throw new ArgumentNullException(nameof(selection));
        _afterCleanupStage = afterCleanupStage;
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
        _refresh = Command(() => RequestRefresh(force: true, full: true), HasRenderableState);
        _cancelSelection = Command(() => _selection.Cancel(_consumerId), () => _state.Selection.IsPending);
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
    public IRelayCommand RefreshCommand => _refresh;
    public IRelayCommand CancelSelectionCommand => _cancelSelection;

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
    }

    public void CancelSelection() => _lifetime.Post(() => _selection.Cancel(_consumerId));
    public void Refresh() => _lifetime.Post(() => RequestRefresh(force: true, full: true));
    public void ReportAcquisitionError(string? error) => _lifetime.Post(() =>
        ReplaceState(_state with { AcquisitionError = error }));
    public void ReportIngressError(string? error) => _lifetime.Post(() =>
        ReplaceState(_state with { BScanIngressError = error }));
    public void ReportUiInteractionError(string? error) => _lifetime.Post(() =>
        ReplaceState(_state with { UiInteractionError = error }));

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
            visitor(_visibleRegion.Pixels.Span, descriptor.Stride);
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
        if (newRun) { _ = _selection.SetRunAsync(snapshot.RunId); }
        BScanSpatialViewport spatial = newRun || _state.SpatialViewport is null || _state.IsSpatialAutoFit
            ? BScanSpatialViewportOperations.Reset(snapshot.ResidentRange)
            : BScanSpatialViewportOperations.Reconcile(_state.SpatialViewport.Value, snapshot.ResidentRange);
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
            Diagnostics = _source.Diagnostics,
            ResidentRange = snapshot.ResidentRange,
            TemporalDomain = snapshot.TemporalDomain,
            SpatialViewport = spatial,
            TimeViewportBinding = binding,
            RequiresFullRefresh = newRun || snapshot.RequiresFullRefresh || _state.RequiresFullRefresh,
            Selection = newRun ? BScanSelectionState.Empty with { RunId = snapshot.RunId } : _selection.State,
            SelectionError = newRun ? null : _selection.State.SelectionError,
            IsSpatialAutoFit = newRun || _state.IsSpatialAutoFit
        });
        _timeViewport.PublishDeferredChanges();
        _applyingMetadata = false;
        PublishTimeBinding();
        _selection.UpdateResidency(_consumerId, snapshot.RunId, snapshot.SnapshotVersion, snapshot.ResidentRange);
        RefreshCommands();
        RequestRefresh();
    }

    private void RequestRefresh(bool force = false, bool full = false)
    {
        if (!HasRenderableState() || _latestMetadata is null || _timeViewport.Viewport is not { } time) { return; }
        var request = new BScanRenderRequest(_latestMetadata.RunId, _state.SpatialViewport!.Value, time,
            _pixelWidth, _pixelHeight, _renderConfigurationVersion,
            full ? null : _state.DisplayedRevision?.SnapshotVersion,
            full ? null : _state.DisplayedRevision?.StoreRevision,
            full ? null : _state.DisplayedRevision?.WrapGeneration, _consumerId,
            !full && _state.Region is { } displayed ? new(displayed.Snapshot.Stream,
                _state.DisplayedRevision!.Value, new(displayed.Snapshot.SpatialViewport,
                    displayed.Snapshot.TimeViewport, displayed.Snapshot.RasterWidth,
                    displayed.Snapshot.RasterHeight, displayed.Snapshot.RenderConfigurationVersion)) : null);
        if (!force && request == _lastRequested) { return; }
        _lastRequested = request;
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
            ReplaceState(_state with { BScanVisualError = Message(error) });
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
            lock (_gate) { _rasterOperations.Remove(completion.Task); }
            if (failure is null) { completion.TrySetResult(); }
            else { completion.TrySetException(failure); }
        }
    }

    private async Task ResolveRegionAsync(BScanRenderRequest request, long generation,
        CancellationTokenSource cancellation)
    {
        BScanRasterResult? result = null;
        string? operationError = null;
        try { result = await _source.GetRegionAsync(request, cancellation.Token).ConfigureAwait(false); }
        catch (Exception error) { operationError = Message(error); }
        BScanRasterRegion? owned = result?.Region;
        BScanPresentedRegion? managed = owned is null ? null : new(new(Guid.NewGuid()), owned.Snapshot,
            owned.X, owned.Y, owned.Width, owned.Height, owned.Stride, owned.Format, generation);
        BScanRasterRegion? retired = null;
        bool accepted = false;
        await _lifetime.InvokeAsync(() =>
        {
            if (IsClosing || generation != _rasterGeneration || managed is null ||
                managed.Snapshot.ConsumerId != _consumerId || managed.Snapshot.RunId != _state.RunId ||
                _latestMetadata is null || managed.Snapshot.Stream != _latestMetadata.Stream ||
                managed.Snapshot.SpatialViewport != _state.SpatialViewport ||
                managed.Snapshot.TimeViewport != _timeViewport.Viewport) { return; }
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
                    SnapshotVersion = managed.Snapshot.SnapshotVersion,
                    StoreRevision = managed.Snapshot.StoreRevision,
                    TimeViewportBinding = new(managed.Snapshot.RunId, managed.Snapshot.SnapshotVersion,
                        managed.Snapshot.TimeViewport),
                    RequiresFullRefresh = false,
                    BScanVisualError = null
                };
                _state = nextState;
                Volatile.Write(ref _recoveryPosted, 0);
            }
            OnPropertyChanged(nameof(State));
            OnPropertyChanged(nameof(Phase));
        }).ConfigureAwait(false);
        if (!accepted) { owned?.Dispose(); }
        retired?.Dispose();
        string? visualError = operationError ?? (result?.Kind == BScanRasterResultKind.Faulted ? result.Error : null);
        if (managed is null && visualError is not null)
        {
            await _lifetime.InvokeAsync(() =>
            {
                if (!IsClosing && generation == _rasterGeneration && request.RunId == _state.RunId)
                { ReplaceState(_state with { BScanVisualError = visualError }); }
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
        lock (_gate) { if (generation == _rasterGeneration) { _rasterCancellation = null; } }
    }

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
        _lifetime.Post(() =>
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
        });
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
    }
    private SafeRelayCommand[] Commands => [_zoomTimeIn, _zoomTimeOut, _panTimeUp, _panTimeDown, _resetTime,
        _zoomSpatialIn, _zoomSpatialOut, _panSpatialLeft, _panSpatialRight, _resetSpatial, _resetAll,
        _refresh, _cancelSelection];

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
        Capture("raster-cancel", () => rasterCancellation?.Cancel());
        Task[] rasters;
        lock (_gate) { rasters = [.. _rasterOperations]; }
        await CaptureAsync("raster-operations", () => Task.WhenAll(rasters)).ConfigureAwait(false);
        await CaptureAsync("ui-state", () => _lifetime.InvokeAsync(() =>
        {
            foreach (SafeRelayCommand command in Commands) { command.NotifyCanExecuteChanged(); }
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
        if (failures.Count == 0) { completion.TrySetResult(); }
        else { completion.TrySetException(new CleanupFailureException("B-Scan ViewModel cleanup failed.", failures[0], failures.Skip(1).ToArray())); }
    }

    private static string Message(Exception error) => string.IsNullOrWhiteSpace(error.Message)
        ? error.GetType().Name : error.Message;

    protected override void OnObserverFailure(Exception error) => Interlocked.Increment(ref _observerErrors);

    private sealed class Observer(BScanViewModel owner) : IObserver<BScanVisualUpdate>
    {
        public void OnNext(BScanVisualUpdate value) => owner.Receive(value);
        public void OnError(Exception error) => owner.ReceiveError(error);
        public void OnCompleted() { }
    }
}
