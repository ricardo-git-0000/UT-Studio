using System.ComponentModel;
using System.Windows;
using System.Windows.Media;
using UTStudio.Presentation;
using UTStudio.Visualization.Wpf.Controls;
using UTStudio.Visualization.Wpf.Rendering;

namespace UTStudio.App.Wpf.Adapters;

/// <summary>UI-only bridge. The reusable control remains independent from Presentation.</summary>
internal sealed class BScanControlAdapter : IDisposable
{
    private readonly BScanViewModel _viewModel;
    private readonly BScanControl _control;
    private BScanColorPalette? _palette;
    private BScanPresentedRegionToken? _applied;
    private ulong? _requestedColumn;
    private bool _disposed, _refreshPending;

    internal BScanControlAdapter(BScanViewModel viewModel, BScanControl control)
    {
        _viewModel = viewModel;
        _control = control;
        viewModel.PropertyChanged += OnViewModelChanged;
        control.TimeZoomRequested += OnTimeZoom;
        control.SpatialZoomRequested += OnSpatialZoom;
        control.PanRequested += OnPan;
        control.ColumnRequested += OnColumn;
        control.ResetAllRequested += OnReset;
        control.FullRefreshRequired += OnFullRefresh;
        control.RasterSizeRequested += OnRasterSize;
        PublishState();
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs args)
    { if (!_disposed && (string.IsNullOrEmpty(args.PropertyName) || args.PropertyName == nameof(BScanViewModel.State))) PublishState(); }

    private void PublishState()
    {
        var state = _viewModel.State;
        _control.SpatialViewport = state.Region?.Snapshot.SpatialViewport ?? state.SpatialViewport;
        _control.TimeViewport = state.TimeViewportBinding?.Viewport;
        _control.ResidentRange = state.ResidentRange;
        _control.SelectedAcquisitionIndex = _requestedColumn ?? state.Selection.AcquisitionIndex;
        _control.SelectionStatus = _requestedColumn is not null || state.Selection.IsPending ? "Resolving" :
            state.Selection.Residency?.ToString() ?? state.Selection.LastResult?.ToString();
        if (state.Region is not { } region)
        { if (_applied is not null) { _control.ClearRaster(); _applied = null; } return; }
        if (_applied == region.Token) return;
        if (region.Format != UTStudio.Visualization.Core.BScanRasterFormat.BipolarIntensity8)
        { _viewModel.ReportUiInteractionError("Unsupported B-Scan raster format."); return; }
        var dpi = VisualTreeHelper.GetDpi(_control);
        try
        {
            if (_control.ApplyRaster(region.Snapshot,
                (destination, length, stride) => _viewModel.TryVisitPresentedRegion(region.Token,
                    (source, validityMask, sourceStride) =>
                    {
                        ReadOnlySpan<uint> palette = _palette is null
                            ? BScanPalette.Bgra32 : _palette.Bgra32.Span;
                        BScanPalette.CopyToBgra32(source, validityMask, sourceStride,
                            Surface(destination, length), stride,
                            new(0, 0, region.Snapshot.AffectedRegion.Width,
                                region.Snapshot.AffectedRegion.Height), palette);
                    }),
                dpi.PixelsPerInchX, dpi.PixelsPerInchY))
            { _applied = region.Token; _refreshPending = false; }
            else RequestFullRefresh();
        }
        catch (Exception error) { _viewModel.ReportUiInteractionError(error.Message); RequestFullRefresh(); }
    }

    private void RequestFullRefresh()
    { if (_refreshPending || _disposed) return; _refreshPending = true; _viewModel.Refresh(); }
    private void OnTimeZoom(object? s, BScanTimeZoomRequestedEventArgs e) => _viewModel.ZoomTimeAt(e.AnchorSeconds, e.Factor);
    private void OnSpatialZoom(object? s, BScanSpatialZoomRequestedEventArgs e) => _viewModel.ZoomSpatialAt(e.AnchorRatio, e.Factor);
    private void OnPan(object? s, BScanPanRequestedEventArgs e) { _viewModel.PanTimeBy(e.TimeDeltaSeconds); _viewModel.PanSpatialBy(e.SpatialDelta); }
    private async void OnColumn(object? s, BScanColumnRequestedEventArgs e)
    {
        if (e.RasterPixelX is { } pixel)
        { await SelectPixelAsync(pixel, e.AcquisitionIndex); }
        else { await SelectColumnAsync(e.AcquisitionIndex); }
    }

    private async Task SelectPixelAsync(int pixel, ulong requestedIndex)
    {
        if (_disposed) { return; }
        _requestedColumn = requestedIndex;
        _control.SelectedAcquisitionIndex = requestedIndex;
        try { await _viewModel.SelectPixelAsync(pixel); }
        catch (Exception error) { _viewModel.ReportUiInteractionError(error.Message); }
        finally
        {
            if (_requestedColumn == requestedIndex) { _requestedColumn = null; }
            if (!_disposed) { PublishState(); }
        }
    }

    internal async Task SelectColumnAsync(ulong acquisitionIndex)
    {
        if (_disposed) { return; }
        _requestedColumn = acquisitionIndex;
        _control.SelectedAcquisitionIndex = acquisitionIndex;
        try { await _viewModel.SelectColumnAsync(acquisitionIndex); }
        catch (Exception error) { _viewModel.ReportUiInteractionError(error.Message); }
        finally
        {
            if (_requestedColumn == acquisitionIndex) { _requestedColumn = null; }
            if (!_disposed) { PublishState(); }
        }
    }
    private void OnReset(object? s, EventArgs e)
    { if (_viewModel.ResetAllCommand.CanExecute(null)) _viewModel.ResetAllCommand.Execute(null); }
    private void OnFullRefresh(object? s, EventArgs e) => RequestFullRefresh();
    private void OnRasterSize(object? s, BScanRasterSizeRequestedEventArgs e) =>
        _viewModel.SetRasterSize(e.PixelWidth, e.PixelHeight, e.ConfigurationVersion);

    internal void SetPalette(BScanColorPalette palette)
    {
        ArgumentNullException.ThrowIfNull(palette);
        if (_disposed) { throw new ObjectDisposedException(nameof(BScanControlAdapter)); }
        _palette = palette;
        _applied = null;
        _refreshPending = false;
        _viewModel.Refresh();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _viewModel.PropertyChanged -= OnViewModelChanged;
        _control.TimeZoomRequested -= OnTimeZoom;
        _control.SpatialZoomRequested -= OnSpatialZoom;
        _control.PanRequested -= OnPan;
        _control.ColumnRequested -= OnColumn;
        _control.ResetAllRequested -= OnReset;
        _control.FullRefreshRequired -= OnFullRefresh;
        _control.RasterSizeRequested -= OnRasterSize;
        _control.EndInteraction();
    }

    private static unsafe Span<byte> Surface(nint address, int length) =>
        new(address.ToPointer(), length);
}
