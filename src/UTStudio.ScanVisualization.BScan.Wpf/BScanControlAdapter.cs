using System.ComponentModel;
using System.Windows;
using System.Windows.Media;
using UTStudio.Presentation;
using UTStudio.Visualization.Core;
using UTStudio.Visualization.Wpf.Controls;
using UTStudio.Visualization.Wpf.Rendering;

namespace UTStudio.ScanVisualization.BScan.Wpf;

internal enum BScanAdapterConstructionStage
{
    AdapterCreated,
    ViewModelSubscribed,
    ControlConnected,
    EventsRegistered,
    InitialStateApplied,
    InitialStatePublished,
    Connected
}

internal enum BScanRasterCopyStage { Started, Admitted, Visited, Completed, Failed }

internal enum BScanAdapterLifecycle { Created, Prepared, Active, Closing, Closed }

internal readonly record struct BScanAdapterDiagnostics(BScanAdapterLifecycle Lifecycle, bool ViewModelSubscribed,
    int RegisteredHandlers, long CopiesStarted, long CopiesAdmitted, long CopiesCompleted, int ActiveCopies,
    long RejectedVisits)
{
    internal bool Connected => Lifecycle == BScanAdapterLifecycle.Active;
}

internal sealed class BScanControlAdapter : IDisposable
{
    private readonly BScanViewModel _viewModel;
    private readonly BScanControl _control;
    private BScanVisualSnapshot? _initialSnapshot;
    private BScanSpatialViewport? _initialSpatialViewport;
    private ScanTimeViewport? _initialTimeViewport;
    private BScanAcquisitionRange? _initialResidentRange;
    private ulong? _initialSelectedAcquisitionIndex;
    private string? _initialSelectionStatus;
    private BScanColorPalette? _palette;
    private BScanPresentedRegionToken? _applied;
    private ulong? _requestedColumn;
    private readonly Action<string>? _afterCleanupStage;
    private readonly Action<BScanRasterCopyStage, BScanPresentedRegionToken>? _copyProbe;
    private long _copiesStarted, _copiesAdmitted, _copiesCompleted, _rejectedVisits;
    private int _activeCopies;
    private int _lifecycle = (int)BScanAdapterLifecycle.Created;
    private bool _refreshPending, _viewModelSubscribed, _eventsRegistered, _controlConnected, _activationPublishing;

    internal BScanControlAdapter(BScanViewModel viewModel, BScanControl control,
        Action<BScanAdapterConstructionStage>? afterStage = null, Action<string>? afterCleanupStage = null,
        Action<BScanRasterCopyStage, BScanPresentedRegionToken>? copyProbe = null)
    {
        _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        _control = control ?? throw new ArgumentNullException(nameof(control));
        _afterCleanupStage = afterCleanupStage;
        _copyProbe = copyProbe;
        try
        {
            afterStage?.Invoke(BScanAdapterConstructionStage.AdapterCreated);
            viewModel.PropertyChanged += OnViewModelChanged;
            _viewModelSubscribed = true;
            afterStage?.Invoke(BScanAdapterConstructionStage.ViewModelSubscribed);
            ConnectControl();
            afterStage?.Invoke(BScanAdapterConstructionStage.ControlConnected);
            RegisterControlEvents();
            afterStage?.Invoke(BScanAdapterConstructionStage.EventsRegistered);
            Volatile.Write(ref _lifecycle, (int)BScanAdapterLifecycle.Prepared);
        }
        catch (Exception primary)
        {
            Volatile.Write(ref _lifecycle, (int)BScanAdapterLifecycle.Closing);
            List<CleanupStageFailure> cleanup = RollbackConstruction();
            Volatile.Write(ref _lifecycle, (int)BScanAdapterLifecycle.Closed);
            if (cleanup.Count == 0) { throw; }
            throw new CleanupFailureException("B-Scan adapter construction and rollback failed.",
                new("adapter-construction", primary), cleanup);
        }
    }

    internal void Activate(Action activationCommit, Action<BScanAdapterConstructionStage>? afterStage = null)
    {
        ArgumentNullException.ThrowIfNull(activationCommit);
        if (Lifecycle != BScanAdapterLifecycle.Prepared)
        { throw new InvalidOperationException("A B-Scan adapter can only be activated from Prepared."); }
        try
        {
            _activationPublishing = true;
            ApplyState();
            afterStage?.Invoke(BScanAdapterConstructionStage.InitialStateApplied);
            PublishRaster();
            afterStage?.Invoke(BScanAdapterConstructionStage.InitialStatePublished);
            afterStage?.Invoke(BScanAdapterConstructionStage.Connected);
            if (Interlocked.CompareExchange(ref _lifecycle, (int)BScanAdapterLifecycle.Active,
                (int)BScanAdapterLifecycle.Prepared) != (int)BScanAdapterLifecycle.Prepared)
            { throw new InvalidOperationException("B-Scan activation was interrupted by lifecycle closure."); }
            activationCommit();
        }
        finally { _activationPublishing = false; }
    }

    private void ConnectControl()
    {
        if (_control.RasterSource is not null)
        { throw new InvalidOperationException("A B-Scan adapter requires an unconnected control."); }
        _initialSnapshot = _control.Snapshot;
        _initialSpatialViewport = _control.SpatialViewport;
        _initialTimeViewport = _control.TimeViewport;
        _initialResidentRange = _control.ResidentRange;
        _initialSelectedAcquisitionIndex = _control.SelectedAcquisitionIndex;
        _initialSelectionStatus = _control.SelectionStatus;
        _controlConnected = true;
    }

    private void RegisterControlEvents()
    {
        // Removing a handler that was never added is safe, so mark the whole compensation
        // before the first acquisition. A failure in any later += still rolls back all seven.
        _eventsRegistered = true;
        _control.TimeZoomRequested += OnTimeZoom;
        _control.SpatialZoomRequested += OnSpatialZoom;
        _control.PanRequested += OnPan;
        _control.ColumnRequested += OnColumn;
        _control.ResetAllRequested += OnReset;
        _control.FullRefreshRequired += OnFullRefresh;
        _control.RasterSizeRequested += OnRasterSize;
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (Lifecycle == BScanAdapterLifecycle.Active &&
        (string.IsNullOrEmpty(args.PropertyName) || args.PropertyName == nameof(BScanViewModel.State))) PublishState();
    }

    private void PublishState()
    {
        ApplyState();
        PublishRaster();
    }

    private void ApplyState()
    {
        BScanViewState state = _viewModel.State;
        _control.SpatialViewport = state.Region?.Snapshot.SpatialViewport ?? state.SpatialViewport;
        _control.TimeViewport = state.TimeViewportBinding?.Viewport;
        _control.ResidentRange = state.ResidentRange;
        _control.SelectedAcquisitionIndex = _requestedColumn ?? state.Selection.AcquisitionIndex;
        _control.SelectionStatus = _requestedColumn is not null || state.Selection.IsPending ? "Resolving" :
            state.Selection.Residency?.ToString() ?? state.Selection.LastResult?.ToString();
    }

    private void PublishRaster()
    {
        BScanViewState state = _viewModel.State;
        if (state.Region is not { } region)
        { if (_applied is not null) { _control.ClearRaster(); _applied = null; } return; }
        if (_applied == region.Token) { return; }
        if (region.Format != BScanRasterFormat.BipolarIntensity8)
        { _viewModel.ReportUiInteractionError("Unsupported B-Scan raster format."); return; }
        DpiScale dpi = VisualTreeHelper.GetDpi(_control);
        try
        {
            BScanRasterCopy copy = (destination, length, stride) =>
            {
                Interlocked.Increment(ref _copiesStarted);
                Interlocked.Increment(ref _activeCopies);
                try
                {
                    _copyProbe?.Invoke(BScanRasterCopyStage.Started, region.Token);
                    return _viewModel.TryVisitPresentedRegion(region.Token,
                        (source, validityMask, sourceStride) =>
                        {
                            Interlocked.Increment(ref _copiesAdmitted);
                            _copyProbe?.Invoke(BScanRasterCopyStage.Admitted, region.Token);
                            ReadOnlySpan<uint> palette = _palette is null ? BScanPalette.Bgra32 : _palette.Bgra32.Span;
                            BScanPalette.CopyToBgra32(source, validityMask, sourceStride, Surface(destination, length), stride,
                                new(0, 0, region.Snapshot.AffectedRegion.Width, region.Snapshot.AffectedRegion.Height), palette);
                            _copyProbe?.Invoke(BScanRasterCopyStage.Visited, region.Token);
                        });
                }
                finally
                {
                    try { _copyProbe?.Invoke(BScanRasterCopyStage.Completed, region.Token); }
                    finally
                    {
                        Interlocked.Increment(ref _copiesCompleted);
                        if (Interlocked.Decrement(ref _activeCopies) == 0 && Lifecycle == BScanAdapterLifecycle.Closing)
                        { Volatile.Write(ref _lifecycle, (int)BScanAdapterLifecycle.Closed); }
                    }
                }
            };
            if (!_activationPublishing && Lifecycle != BScanAdapterLifecycle.Active)
            { Interlocked.Increment(ref _rejectedVisits); return; }
            bool applied = _control.ApplyRaster(region.Snapshot, copy, dpi.PixelsPerInchX, dpi.PixelsPerInchY);
            if (applied) { _applied = region.Token; _refreshPending = false; }
            else if (_activationPublishing)
            { throw new InvalidOperationException("The initial B-Scan raster could not be applied."); }
            else { RequestFullRefresh(); }
        }
        catch (Exception error)
        {
            if (_activationPublishing) { throw; }
            _copyProbe?.Invoke(BScanRasterCopyStage.Failed, region.Token);
            _viewModel.ReportUiInteractionError(error.Message);
            RequestFullRefresh();
        }
    }

    private void RequestFullRefresh()
    { if (_refreshPending || Lifecycle != BScanAdapterLifecycle.Active) { return; } _refreshPending = true; _viewModel.Refresh(); }
    private void OnTimeZoom(object? sender, BScanTimeZoomRequestedEventArgs args)
    { if (Lifecycle == BScanAdapterLifecycle.Active) { _viewModel.ZoomTimeAt(args.AnchorSeconds, args.Factor); } }
    private void OnSpatialZoom(object? sender, BScanSpatialZoomRequestedEventArgs args)
    { if (Lifecycle == BScanAdapterLifecycle.Active) { _viewModel.ZoomSpatialAt(args.AnchorRatio, args.Factor); } }
    private void OnPan(object? sender, BScanPanRequestedEventArgs args)
    { if (Lifecycle == BScanAdapterLifecycle.Active) { _viewModel.PanTimeBy(args.TimeDeltaSeconds); _viewModel.PanSpatialBy(args.SpatialDelta); } }
    private async void OnColumn(object? sender, BScanColumnRequestedEventArgs args)
    {
        if (Lifecycle != BScanAdapterLifecycle.Active) { return; }
        if (args.RasterPixelX is { } pixel) { await SelectPixelAsync(pixel, args.AcquisitionIndex); }
        else { await SelectColumnAsync(args.AcquisitionIndex); }
    }

    private async Task SelectPixelAsync(int pixel, ulong requestedIndex)
    {
        if (Lifecycle != BScanAdapterLifecycle.Active) { return; }
        _requestedColumn = requestedIndex;
        _control.SelectedAcquisitionIndex = requestedIndex;
        try { await _viewModel.SelectPixelAsync(pixel); }
        catch (Exception error) { _viewModel.ReportUiInteractionError(error.Message); }
        finally
        {
            if (_requestedColumn == requestedIndex) { _requestedColumn = null; }
            if (Lifecycle == BScanAdapterLifecycle.Active) { PublishState(); }
        }
    }

    internal async Task SelectColumnAsync(ulong acquisitionIndex)
    {
        if (Lifecycle != BScanAdapterLifecycle.Active) { return; }
        _requestedColumn = acquisitionIndex;
        _control.SelectedAcquisitionIndex = acquisitionIndex;
        try { await _viewModel.SelectColumnAsync(acquisitionIndex); }
        catch (Exception error) { _viewModel.ReportUiInteractionError(error.Message); }
        finally
        {
            if (_requestedColumn == acquisitionIndex) { _requestedColumn = null; }
            if (Lifecycle == BScanAdapterLifecycle.Active) { PublishState(); }
        }
    }

    private void OnReset(object? sender, EventArgs args)
    { if (Lifecycle == BScanAdapterLifecycle.Active && _viewModel.ResetAllCommand.CanExecute(null)) { _viewModel.ResetAllCommand.Execute(null); } }
    private void OnFullRefresh(object? sender, EventArgs args) => RequestFullRefresh();
    private void OnRasterSize(object? sender, BScanRasterSizeRequestedEventArgs args)
    {
        if (Lifecycle == BScanAdapterLifecycle.Active)
        { _viewModel.SetRasterSize(args.PixelWidth, args.PixelHeight, args.ConfigurationVersion); }
    }

    internal void SetPalette(BScanColorPalette palette)
    {
        ArgumentNullException.ThrowIfNull(palette);
        if (Lifecycle != BScanAdapterLifecycle.Active) { throw new ObjectDisposedException(nameof(BScanControlAdapter)); }
        _palette = palette; _applied = null; _refreshPending = false; _viewModel.Refresh();
    }

    internal BScanAdapterLifecycle Lifecycle => (BScanAdapterLifecycle)Volatile.Read(ref _lifecycle);

    internal bool TryDiagnosticVisit(BScanPresentedRegionToken token)
    {
        if (Lifecycle != BScanAdapterLifecycle.Active)
        { Interlocked.Increment(ref _rejectedVisits); return false; }
        return _viewModel.TryVisitPresentedRegion(token, static (_, _, _) => { });
    }

    internal BScanAdapterDiagnostics Diagnostics => new(Lifecycle, _viewModelSubscribed,
        _eventsRegistered ? 7 : 0, Interlocked.Read(ref _copiesStarted), Interlocked.Read(ref _copiesAdmitted),
        Interlocked.Read(ref _copiesCompleted), Volatile.Read(ref _activeCopies), Interlocked.Read(ref _rejectedVisits));

    public void Dispose()
    {
        while (true)
        {
            BScanAdapterLifecycle lifecycle = Lifecycle;
            if (lifecycle is BScanAdapterLifecycle.Closing or BScanAdapterLifecycle.Closed) { return; }
            if (Interlocked.CompareExchange(ref _lifecycle, (int)BScanAdapterLifecycle.Closing,
                (int)lifecycle) == (int)lifecycle) { break; }
        }
        List<CleanupStageFailure> failures = RollbackConstruction();
        if (Volatile.Read(ref _activeCopies) == 0)
        { Volatile.Write(ref _lifecycle, (int)BScanAdapterLifecycle.Closed); }
        if (failures.Count != 0)
        { throw new CleanupFailureException("B-Scan adapter cleanup failed.", failures[0], failures.Skip(1).ToArray()); }
    }

    private List<CleanupStageFailure> RollbackConstruction()
    {
        List<CleanupStageFailure> failures = [];
        if (_eventsRegistered)
        {
            _eventsRegistered = false;
            Capture("control-event-raster-size", () => _control.RasterSizeRequested -= OnRasterSize);
            Capture("control-event-full-refresh", () => _control.FullRefreshRequired -= OnFullRefresh);
            Capture("control-event-reset", () => _control.ResetAllRequested -= OnReset);
            Capture("control-event-column", () => _control.ColumnRequested -= OnColumn);
            Capture("control-event-pan", () => _control.PanRequested -= OnPan);
            Capture("control-event-spatial-zoom", () => _control.SpatialZoomRequested -= OnSpatialZoom);
            Capture("control-event-time-zoom", () => _control.TimeZoomRequested -= OnTimeZoom);
        }
        if (_viewModelSubscribed)
        {
            _viewModelSubscribed = false;
            Capture("viewmodel-subscription", () => _viewModel.PropertyChanged -= OnViewModelChanged);
        }
        if (_controlConnected)
        {
            _controlConnected = false;
            Capture("control-raster", () =>
            {
                if (_applied is not null) { _control.ClearRaster(); _applied = null; }
                _control.Snapshot = _initialSnapshot;
                _control.SpatialViewport = _initialSpatialViewport;
                _control.TimeViewport = _initialTimeViewport;
                _control.ResidentRange = _initialResidentRange;
                _control.SelectedAcquisitionIndex = _initialSelectedAcquisitionIndex;
                _control.SelectionStatus = _initialSelectionStatus;
            });
            Capture("control-interaction", _control.EndInteraction);
        }
        return failures;

        void Capture(string stage, Action cleanup)
        {
            try { cleanup(); _afterCleanupStage?.Invoke(stage); }
            catch (Exception error) { failures.Add(new(stage, error)); }
        }
    }

    private static unsafe Span<byte> Surface(nint address, int length) => new(address.ToPointer(), length);
}
