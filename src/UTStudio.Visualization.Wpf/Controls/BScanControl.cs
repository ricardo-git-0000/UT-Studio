using System.Buffers;
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using UTStudio.Visualization.Core;
using UTStudio.Visualization.Wpf.Rendering;

namespace UTStudio.Visualization.Wpf.Controls;

public delegate bool BScanRasterCopy(nint destination, int length, int stride);

public sealed class BScanTimeZoomRequestedEventArgs(double anchorSeconds, double factor) : EventArgs
{ public double AnchorSeconds { get; } = anchorSeconds; public double Factor { get; } = factor; }
public sealed class BScanSpatialZoomRequestedEventArgs(decimal anchorRatio, decimal factor) : EventArgs
{ public decimal AnchorRatio { get; } = anchorRatio; public decimal Factor { get; } = factor; }
public sealed class BScanPanRequestedEventArgs(double timeDeltaSeconds, decimal spatialDelta) : EventArgs
{ public double TimeDeltaSeconds { get; } = timeDeltaSeconds; public decimal SpatialDelta { get; } = spatialDelta; }
public sealed class BScanColumnRequestedEventArgs(ulong acquisitionIndex, int? rasterPixelX = null) : EventArgs
{
    public ulong AcquisitionIndex { get; } = acquisitionIndex;
    public int? RasterPixelX { get; } = rasterPixelX;
}
public sealed class BScanRasterSizeRequestedEventArgs(int pixelWidth, int pixelHeight, ulong configurationVersion) : EventArgs
{ public int PixelWidth { get; } = pixelWidth; public int PixelHeight { get; } = pixelHeight; public ulong ConfigurationVersion { get; } = configurationVersion; }

/// <summary>Reusable WPF surface for a neutral B-Scan raster and interaction intentions.</summary>
public sealed class BScanControl : FrameworkElement
{
    public static readonly DependencyProperty SnapshotProperty = DependencyProperty.Register(nameof(Snapshot),
        typeof(BScanVisualSnapshot), typeof(BScanControl), new FrameworkPropertyMetadata(null,
            FrameworkPropertyMetadataOptions.AffectsRender, Redraw));
    public static readonly DependencyProperty SpatialViewportProperty = DependencyProperty.Register(nameof(SpatialViewport),
        typeof(BScanSpatialViewport?), typeof(BScanControl), new FrameworkPropertyMetadata(null,
            FrameworkPropertyMetadataOptions.AffectsRender, Redraw));
    public static readonly DependencyProperty TimeViewportProperty = DependencyProperty.Register(nameof(TimeViewport),
        typeof(ScanTimeViewport), typeof(BScanControl), new FrameworkPropertyMetadata(null,
            FrameworkPropertyMetadataOptions.AffectsRender, Redraw));
    public static readonly DependencyProperty ResidentRangeProperty = DependencyProperty.Register(nameof(ResidentRange),
        typeof(BScanAcquisitionRange?), typeof(BScanControl), new FrameworkPropertyMetadata(null,
            FrameworkPropertyMetadataOptions.AffectsRender, Redraw));
    public static readonly DependencyProperty SelectedAcquisitionIndexProperty = DependencyProperty.Register(
        nameof(SelectedAcquisitionIndex), typeof(ulong?), typeof(BScanControl), new FrameworkPropertyMetadata(null,
            FrameworkPropertyMetadataOptions.AffectsRender, Redraw));
    public static readonly DependencyProperty SelectionStatusProperty = DependencyProperty.Register(nameof(SelectionStatus),
        typeof(string), typeof(BScanControl), new FrameworkPropertyMetadata(null,
            FrameworkPropertyMetadataOptions.AffectsRender, Redraw));

    private WriteableBitmap? _bitmap;
    private readonly DrawingVisual _selectionVisual = new();
    private BScanVisualSnapshot? _applied;
    private bool _panning;
    private bool _selectingColumn;
    private bool _interactionEnabled = true;
    private MouseButton? _panButton;
    private Point _lastPosition;
    private ulong _renderConfigurationVersion;
    private int _requestedRasterWidth, _requestedRasterHeight;
    private long _rasterGeneration;
    private readonly Func<ModifierKeys> _modifiers;
    private readonly Func<IInputElement, Point> _position;
    private readonly Func<MouseButtonEventArgs, int> _clickCount;
    private int _activePooledRasterBytes;
    private int _peakPooledRasterBytes;
    private int _activeCandidateBitmaps;
    private bool _hasSelectionVisual;
    private static readonly Brush Background = Freeze(new SolidColorBrush(Color.FromRgb(11, 18, 32)));
    private static readonly Brush Text = Freeze(new SolidColorBrush(Color.FromRgb(205, 218, 232)));
    private static readonly Pen Axis = Freeze(new Pen(new SolidColorBrush(Color.FromRgb(92, 112, 135)), 1));
    private static readonly Pen Selection = Freeze(new Pen(new SolidColorBrush(Color.FromRgb(255, 205, 87)), 2));

    public BScanControl() : this(() => Keyboard.Modifiers, Mouse.GetPosition, args => args.ClickCount) { }

    internal BScanControl(Func<ModifierKeys> modifiers, Func<IInputElement, Point> position,
        Func<MouseButtonEventArgs, int> clickCount)
    {
        _modifiers = modifiers;
        _position = position;
        _clickCount = clickCount;
        AddVisualChild(_selectionVisual);
        RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.NearestNeighbor);
        Focusable = true;
        ClipToBounds = true;
        MouseWheel += OnMouseWheel;
        MouseDown += OnMouseDown;
        MouseMove += OnMouseMove;
        MouseUp += OnMouseUp;
        MouseLeftButtonDown += OnMouseLeftButtonDown;
        LostMouseCapture += (_, _) => EndInteraction();
        Loaded += (_, _) => { _interactionEnabled = true; RequestRasterSize(); UpdateSelectionVisual(); };
        Unloaded += (_, _) =>
        {
            _interactionEnabled = false;
            EndInteraction();
        };
        KeyDown += OnKeyDown;
    }

    public BScanVisualSnapshot? Snapshot { get => (BScanVisualSnapshot?)GetValue(SnapshotProperty); set => SetValue(SnapshotProperty, value); }
    public BScanSpatialViewport? SpatialViewport { get => (BScanSpatialViewport?)GetValue(SpatialViewportProperty); set => SetValue(SpatialViewportProperty, value); }
    public ScanTimeViewport? TimeViewport { get => (ScanTimeViewport?)GetValue(TimeViewportProperty); set => SetValue(TimeViewportProperty, value); }
    public BScanAcquisitionRange? ResidentRange { get => (BScanAcquisitionRange?)GetValue(ResidentRangeProperty); set => SetValue(ResidentRangeProperty, value); }
    public ulong? SelectedAcquisitionIndex { get => (ulong?)GetValue(SelectedAcquisitionIndexProperty); set => SetValue(SelectedAcquisitionIndexProperty, value); }
    public string? SelectionStatus { get => (string?)GetValue(SelectionStatusProperty); set => SetValue(SelectionStatusProperty, value); }
    public ImageSource? RasterSource => _bitmap;
    internal BScanVisualSnapshot? AppliedSnapshot => _applied;
    internal int ActivePooledRasterBytes => _activePooledRasterBytes;
    internal int PeakPooledRasterBytes => _peakPooledRasterBytes;
    internal int ActiveCandidateBitmaps => Volatile.Read(ref _activeCandidateBitmaps);
    internal string? LastIncrementalRejection { get; private set; }
    internal bool HasSelectionVisual => _hasSelectionVisual;
    internal int? FailFullCommitAtRow { get; set; }
    internal int? FailIncrementalCommitAtRow { get; set; }

    public event EventHandler<BScanTimeZoomRequestedEventArgs>? TimeZoomRequested;
    public event EventHandler<BScanSpatialZoomRequestedEventArgs>? SpatialZoomRequested;
    public event EventHandler<BScanPanRequestedEventArgs>? PanRequested;
    public event EventHandler<BScanColumnRequestedEventArgs>? ColumnRequested;
    public event EventHandler? ResetAllRequested;
    public event EventHandler? FullRefreshRequired;
    public event EventHandler<BScanRasterSizeRequestedEventArgs>? RasterSizeRequested;

    protected override int VisualChildrenCount => 1;
    protected override Visual GetVisualChild(int index) => index == 0
        ? _selectionVisual : throw new ArgumentOutOfRangeException(nameof(index));

    public bool ApplyRaster(BScanVisualSnapshot descriptor, BScanRasterCopy copy,
        double dpiX = 96, double dpiY = 96)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(copy);
        if (!double.IsFinite(dpiX) || dpiX <= 0 || !double.IsFinite(dpiY) || dpiY <= 0)
        { throw new ArgumentOutOfRangeException(nameof(dpiX), "Raster DPI must be finite and positive."); }
        long generation = Interlocked.Increment(ref _rasterGeneration);
        if (descriptor.RasterWidth is < 1 or > 1024 || descriptor.RasterHeight is < 1 or > 1024 ||
            descriptor.AffectedRegion.X < 0 || descriptor.AffectedRegion.Y < 0 ||
            descriptor.AffectedRegion.Width < 1 || descriptor.AffectedRegion.Height < 1 ||
            checked(descriptor.AffectedRegion.X + descriptor.AffectedRegion.Width) > descriptor.RasterWidth ||
            checked(descriptor.AffectedRegion.Y + descriptor.AffectedRegion.Height) > descriptor.RasterHeight)
        { throw new ArgumentException("Invalid B-Scan raster descriptor.", nameof(descriptor)); }
        if (descriptor.RequiresFullRefresh && descriptor.AffectedRegion !=
            new BScanRasterRectangle(0, 0, descriptor.RasterWidth, descriptor.RasterHeight))
        { throw new ArgumentException("A full refresh must cover the complete raster.", nameof(descriptor)); }
        if (descriptor.HorizontalPixelShift < 0 || descriptor.HorizontalPixelShift >= descriptor.RasterWidth ||
            descriptor.HorizontalPixelShift > 0 &&
            (descriptor.RequiresFullRefresh ||
             descriptor.AffectedRegion.X + descriptor.AffectedRegion.Width != descriptor.RasterWidth ||
             descriptor.AffectedRegion.Width < descriptor.HorizontalPixelShift))
        { throw new ArgumentException("Invalid B-Scan horizontal shift descriptor.", nameof(descriptor)); }
        bool recreate = _bitmap is null || _bitmap.PixelWidth != descriptor.RasterWidth ||
            _bitmap.PixelHeight != descriptor.RasterHeight || _bitmap.DpiX != dpiX || _bitmap.DpiY != dpiY;
        if (!descriptor.RequiresFullRefresh && (recreate || _applied is null ||
            _applied.Stream != descriptor.Stream ||
            (_applied.WrapGeneration != descriptor.WrapGeneration && descriptor.HorizontalPixelShift == 0) ||
            _applied.StoreRevision != descriptor.BaseStoreRevision ||
            _applied.RenderConfigurationVersion != descriptor.RenderConfigurationVersion ||
            _applied.Resampling != descriptor.Resampling ||
            (_applied.SpatialViewport != descriptor.SpatialViewport && descriptor.HorizontalPixelShift == 0) ||
            _applied.TimeViewport != descriptor.TimeViewport))
        {
            LastIncrementalRejection = $"recreate={recreate}; applied={_applied is not null}; " +
                $"appliedRevision={_applied?.StoreRevision}; base={descriptor.BaseStoreRevision}; " +
                $"appliedConfig={_applied?.RenderConfigurationVersion}; config={descriptor.RenderConfigurationVersion}; " +
                $"appliedViewport={_applied?.SpatialViewport}; viewport={descriptor.SpatialViewport}; " +
                $"shift={descriptor.HorizontalPixelShift}; appliedMode={_applied?.Resampling.Mode}; mode={descriptor.Resampling.Mode}";
            FullRefreshRequired?.Invoke(this, EventArgs.Empty); return false;
        }
        LastIncrementalRejection = null;
        int stagedStride = checked(descriptor.AffectedRegion.Width * 4);
        int stagedBytes = checked(stagedStride * descriptor.AffectedRegion.Height);
        byte[] staging = RentRasterBuffer(stagedBytes);
        try
        {
            bool prepared;
            unsafe
            {
                fixed (byte* address = staging)
                { prepared = copy((nint)address, stagedBytes, stagedStride); }
            }
            if (!prepared) { FullRefreshRequired?.Invoke(this, EventArgs.Empty); return false; }
            if (generation != Volatile.Read(ref _rasterGeneration)) { return false; }
            return descriptor.RequiresFullRefresh || recreate
                ? CommitFull(descriptor, staging.AsSpan(0, stagedBytes), stagedStride, dpiX, dpiY, generation)
                : descriptor.HorizontalPixelShift > 0
                    ? CommitScrolledIncremental(descriptor, staging.AsSpan(0, stagedBytes), stagedStride, generation)
                    : CommitIncremental(descriptor, staging.AsSpan(0, stagedBytes), stagedStride, generation);
        }
        catch
        { FullRefreshRequired?.Invoke(this, EventArgs.Empty); throw; }
        finally
        { ReturnRasterBuffer(staging); }
    }

    private bool CommitScrolledIncremental(BScanVisualSnapshot descriptor, ReadOnlySpan<byte> staging,
        int stagedStride, long generation)
    {
        if (generation != Volatile.Read(ref _rasterGeneration)) { return false; }
        WriteableBitmap bitmap = _bitmap!;
        int fullStride = checked(descriptor.RasterWidth * 4);
        int fullBytes = checked(fullStride * descriptor.RasterHeight);
        byte[] backup = RentRasterBuffer(fullBytes);
        bool locked = false;
        try
        {
            bitmap.Lock();
            locked = true;
            var complete = new BScanRasterRectangle(0, 0, descriptor.RasterWidth, descriptor.RasterHeight);
            CopyFromBitmap(bitmap, complete, backup.AsSpan(0, fullBytes), fullStride);
            try
            {
                ScrollBitmapLeft(bitmap, descriptor.HorizontalPixelShift);
                CopyRows(staging, stagedStride, bitmap.BackBuffer, bitmap.BackBufferStride,
                    descriptor.AffectedRegion, FailIncrementalCommitAtRow);
                if (generation != Volatile.Read(ref _rasterGeneration))
                {
                    CopyRows(backup.AsSpan(0, fullBytes), fullStride, bitmap.BackBuffer,
                        bitmap.BackBufferStride, complete, null);
                    return false;
                }
            }
            catch
            {
                CopyRows(backup.AsSpan(0, fullBytes), fullStride, bitmap.BackBuffer,
                    bitmap.BackBufferStride, complete, null);
                throw;
            }
            bitmap.AddDirtyRect(new(0, 0, descriptor.RasterWidth, descriptor.RasterHeight));
        }
        finally
        {
            try { if (locked) { bitmap.Unlock(); } }
            finally { ReturnRasterBuffer(backup); }
        }
        PublishCommittedRaster(bitmap, descriptor);
        return true;
    }

    private static unsafe void ScrollBitmapLeft(WriteableBitmap bitmap, int pixels)
    {
        int remainingBytes = checked((bitmap.PixelWidth - pixels) * 4);
        for (int row = 0; row < bitmap.PixelHeight; row++)
        {
            var scan = new Span<byte>((byte*)bitmap.BackBuffer + row * bitmap.BackBufferStride,
                bitmap.PixelWidth * 4);
            scan.Slice(pixels * 4, remainingBytes).CopyTo(scan);
        }
    }

    private bool CommitFull(BScanVisualSnapshot descriptor, ReadOnlySpan<byte> staging, int stagedStride,
        double dpiX, double dpiY, long generation)
    {
        var candidate = new WriteableBitmap(descriptor.RasterWidth, descriptor.RasterHeight,
            dpiX, dpiY, PixelFormats.Bgra32, null);
        Interlocked.Increment(ref _activeCandidateBitmaps);
        try
        {
            candidate.Lock();
            try
            {
                CopyRows(staging, stagedStride, candidate.BackBuffer, candidate.BackBufferStride,
                    descriptor.AffectedRegion, FailFullCommitAtRow);
                candidate.AddDirtyRect(new(0, 0, descriptor.RasterWidth, descriptor.RasterHeight));
            }
            finally { candidate.Unlock(); }
            if (generation != Volatile.Read(ref _rasterGeneration)) { return false; }
            PublishCommittedRaster(candidate, descriptor);
            return true;
        }
        finally { Interlocked.Decrement(ref _activeCandidateBitmaps); }
    }

    private bool CommitIncremental(BScanVisualSnapshot descriptor, ReadOnlySpan<byte> staging, int stagedStride,
        long generation)
    {
        if (generation != Volatile.Read(ref _rasterGeneration)) { return false; }
        WriteableBitmap bitmap = _bitmap!;
        BScanRasterRectangle affected = descriptor.AffectedRegion;
        int backupStride = checked(affected.Width * 4);
        int backupBytes = checked(backupStride * affected.Height);
        byte[] backup = RentRasterBuffer(backupBytes);
        bool locked = false;
        try
        {
            bitmap.Lock();
            locked = true;
            CopyFromBitmap(bitmap, affected, backup.AsSpan(0, backupBytes), backupStride);
            try
            {
                CopyRows(staging, stagedStride, bitmap.BackBuffer, bitmap.BackBufferStride,
                    affected, FailIncrementalCommitAtRow);
                if (generation != Volatile.Read(ref _rasterGeneration))
                {
                    CopyRows(backup.AsSpan(0, backupBytes), backupStride, bitmap.BackBuffer,
                        bitmap.BackBufferStride, affected, null);
                    return false;
                }
            }
            catch
            {
                CopyRows(backup.AsSpan(0, backupBytes), backupStride, bitmap.BackBuffer,
                    bitmap.BackBufferStride, affected, null);
                throw;
            }
            bitmap.AddDirtyRect(new(affected.X, affected.Y, affected.Width, affected.Height));
        }
        finally
        {
            try { if (locked) { bitmap.Unlock(); } }
            finally { ReturnRasterBuffer(backup); }
        }
        PublishCommittedRaster(bitmap, descriptor);
        return true;
    }

    private void PublishCommittedRaster(WriteableBitmap bitmap, BScanVisualSnapshot descriptor)
    {
        _bitmap = bitmap;
        _applied = descriptor;
        Snapshot = descriptor;
        InvalidateVisual();
    }

    private unsafe void CopyRows(ReadOnlySpan<byte> source, int sourceStride, nint destination,
        int destinationStride, BScanRasterRectangle affected, int? failingRow)
    {
        for (int row = 0; row < affected.Height; row++)
        {
            if (failingRow == row) { throw new InvalidOperationException("Injected raster row failure."); }
            source.Slice(checked(row * sourceStride), checked(affected.Width * 4)).CopyTo(
                new Span<byte>((byte*)destination + checked((affected.Y + row) * destinationStride + affected.X * 4),
                    checked(affected.Width * 4)));
        }
    }

    private static unsafe void CopyFromBitmap(WriteableBitmap bitmap, BScanRasterRectangle affected,
        Span<byte> destination, int destinationStride)
    {
        for (int row = 0; row < affected.Height; row++)
        {
            new ReadOnlySpan<byte>((byte*)bitmap.BackBuffer + checked((affected.Y + row) * bitmap.BackBufferStride + affected.X * 4),
                checked(affected.Width * 4)).CopyTo(destination.Slice(row * destinationStride, affected.Width * 4));
        }
    }

    private byte[] RentRasterBuffer(int requiredBytes)
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(requiredBytes);
        int active = Interlocked.Add(ref _activePooledRasterBytes, buffer.Length);
        int observed;
        while (active > (observed = Volatile.Read(ref _peakPooledRasterBytes)) &&
            Interlocked.CompareExchange(ref _peakPooledRasterBytes, active, observed) != observed) { }
        return buffer;
    }

    private void ReturnRasterBuffer(byte[] buffer)
    {
        Interlocked.Add(ref _activePooledRasterBytes, -buffer.Length);
        ArrayPool<byte>.Shared.Return(buffer);
    }

    public void ClearRaster()
    {
        Interlocked.Increment(ref _rasterGeneration);
        _bitmap = null;
        _applied = null;
        Snapshot = null;
        InvalidateVisual();
    }

    public void EndInteraction()
    {
        _panning = false;
        _selectingColumn = false;
        _panButton = null;
        if (IsMouseCaptured) { ReleaseMouseCapture(); }
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        RequestRasterSize();
        UpdateSelectionVisual();
        InvalidateVisual();
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        RequestRasterSize();
        FullRefreshRequired?.Invoke(this, EventArgs.Empty);
    }

    private void RequestRasterSize()
    {
        Rect plot = PlotBounds();
        DpiScale dpi = VisualTreeHelper.GetDpi(this);
        if (plot.Width > 0 && plot.Height > 0)
        {
            int width = Math.Clamp(checked((int)Math.Ceiling(plot.Width * dpi.DpiScaleX)), 1, 1024);
            int height = Math.Clamp(checked((int)Math.Ceiling(plot.Height * dpi.DpiScaleY)), 1, 1024);
            if (width == _requestedRasterWidth && height == _requestedRasterHeight) { return; }
            _requestedRasterWidth = width;
            _requestedRasterHeight = height;
            RasterSizeRequested?.Invoke(this, new(width, height, ++_renderConfigurationVersion));
        }
    }

    protected override void OnRender(DrawingContext drawing)
    {
        drawing.DrawRectangle(Background, null, new Rect(RenderSize));
        Rect plot = PlotBounds();
        if (plot.Width <= 0 || plot.Height <= 0) { return; }
        if (_bitmap is not null) { drawing.DrawImage(_bitmap, plot); }
        drawing.DrawRectangle(null, Axis, plot);
        if (SpatialViewport is { } spatialTicks)
        {
            foreach (AdaptiveAxisTick tick in AdaptiveAxisTicks.Create(
                spatialTicks.MinimumOffset, spatialTicks.MaximumOffset, plot.Width))
            {
                double x = plot.Left + plot.Width * tick.Position;
                drawing.DrawLine(Axis, new Point(x, plot.Bottom), new Point(x, plot.Bottom + 4));
            }
        }
        if (TimeViewport is { } timeTicks)
        {
            foreach (AdaptiveAxisTick tick in AdaptiveAxisTicks.Create(
                (decimal)timeTicks.VisibleMinimumSeconds, (decimal)timeTicks.VisibleMaximumSeconds, plot.Height))
            {
                double y = plot.Top + plot.Height * tick.Position;
                drawing.DrawLine(Axis, new Point(plot.Left - 4, y), new Point(plot.Left, y));
            }
        }
        if (Snapshot is { HasDiscontinuities: true })
        {
            Label(drawing, "⚠ Gap / discontinuidad", new Point(plot.Left + 4, plot.Top + 4));
        }
        DrawAxisLabels(drawing, plot);
        Label(drawing, $"Índice: {SelectedAcquisitionIndex?.ToString(CultureInfo.InvariantCulture) ?? "—"}  " +
            $"Estado: {SelectionStatus ?? "Sin selección"}", new Point(plot.Left, plot.Bottom + 8));
        if (_bitmap is null) { Label(drawing, "Sin datos B-Scan", new Point(plot.Left + 12, plot.Top + 12)); }
    }

    private void OnMouseWheel(object sender, MouseWheelEventArgs args)
    {
        if (!_interactionEnabled) { return; }
        Point position = _position(this);
        Rect plot = PlotBounds();
        if (!plot.Contains(position) || args.Delta == 0) { return; }
        double factor = Math.Pow(1.2, args.Delta / 120d);
        if ((_modifiers() & ModifierKeys.Control) != 0)
        {
            SpatialZoomRequested?.Invoke(this, new((decimal)Math.Clamp((position.X - plot.Left) / plot.Width, 0, 1), (decimal)factor));
        }
        else if ((_modifiers() & (ModifierKeys.Shift | ModifierKeys.Alt)) == 0 && TimeViewport is { } time)
        {
            double ratio = Math.Clamp((position.Y - plot.Top) / plot.Height, 0, 1);
            TimeZoomRequested?.Invoke(this, new(time.VisibleMinimumSeconds + ratio * time.VisibleSpanSeconds, factor));
        }
        else { return; }
        args.Handled = true;
    }

    private void OnMouseDown(object sender, MouseButtonEventArgs args)
    {
        if (!_interactionEnabled) { return; }
        if (args.ChangedButton == MouseButton.Middle) { BeginPan(_position(this), args); }
    }

    private void OnMouseLeftButtonDown(object sender, MouseButtonEventArgs args)
    {
        if (!_interactionEnabled) { return; }
        Focus();
        Point position = _position(this);
        bool onSelection = HitSelection(position);
        bool hasColumn = TryAcquisitionIndex(position, out ulong index);
        _ = TryRasterPixelX(position, out int rasterPixelX);
        if (_clickCount(args) == 2 && PlotBounds().Contains(position) && !onSelection)
        { ResetAllRequested?.Invoke(this, EventArgs.Empty); args.Handled = true; return; }
        if ((_modifiers() & ModifierKeys.Shift) != 0 && !onSelection)
        { BeginPan(position, args); return; }
        if (hasColumn)
        {
            ColumnRequested?.Invoke(this, new(index, rasterPixelX));
            if (onSelection && CaptureMouse()) { _selectingColumn = true; _panButton = args.ChangedButton; }
            args.Handled = true;
        }
    }

    private void BeginPan(Point position, MouseButtonEventArgs args)
    {
        if (!PlotBounds().Contains(position) || TimeViewport is null || SpatialViewport is null || !CaptureMouse()) { return; }
        _panning = true;
        _panButton = args.ChangedButton;
        _lastPosition = position;
        args.Handled = true;
    }

    private void OnMouseMove(object sender, MouseEventArgs args)
    {
        if (!_interactionEnabled) { return; }
        if (_selectingColumn)
        {
            Point position = _position(this);
            if (TryAcquisitionIndex(position, out ulong index) && index != SelectedAcquisitionIndex &&
                TryRasterPixelX(position, out int rasterPixelX))
            { ColumnRequested?.Invoke(this, new(index, rasterPixelX)); }
            args.Handled = true;
            return;
        }
        if (!_panning || TimeViewport is not { } time || SpatialViewport is not { } spatial) { return; }
        Point current = _position(this);
        Rect plot = PlotBounds();
        if (plot.Width <= 0 || plot.Height <= 0) { EndInteraction(); return; }
        double timeDelta = -((current.Y - _lastPosition.Y) / plot.Height) * time.VisibleSpanSeconds;
        decimal spatialDelta = -(decimal)((current.X - _lastPosition.X) / plot.Width) * spatial.Span;
        _lastPosition = current;
        PanRequested?.Invoke(this, new(timeDelta, spatialDelta));
        args.Handled = true;
    }

    private void OnMouseUp(object sender, MouseButtonEventArgs args)
    {
        if ((_panning || _selectingColumn) && args.ChangedButton == _panButton)
        { EndInteraction(); args.Handled = true; }
    }

    private void OnKeyDown(object sender, KeyEventArgs args)
    {
        if (!_interactionEnabled) { return; }
        if (args.Key == Key.Home) { ResetAllRequested?.Invoke(this, EventArgs.Empty); args.Handled = true; }
        else if (args.Key == Key.Escape) { EndInteraction(); args.Handled = true; }
    }

    private bool TryAcquisitionIndex(Point point, out ulong index)
    {
        index = 0;
        Rect plot = PlotBounds();
        if (!plot.Contains(point) || SpatialViewport is not { } viewport || ResidentRange is not { } resident)
        { return false; }
        decimal offset = viewport.MinimumOffset + (decimal)((point.X - plot.Left) / plot.Width) * viewport.Span;
        decimal absolute = viewport.OriginAcquisitionIndex + offset;
        if (absolute < 0 || absolute > ulong.MaxValue) { return false; }
        index = (ulong)Math.Floor(absolute);
        index = Math.Clamp(index, resident.First, resident.Last);
        return true;
    }

    private bool TryRasterPixelX(Point point, out int pixel)
    {
        Rect plot = PlotBounds();
        if (Snapshot is not { RasterWidth: > 0 } snapshot || plot.Width <= 0 ||
            !plot.Contains(point)) { pixel = 0; return false; }
        pixel = Math.Clamp((int)((point.X - plot.Left) / plot.Width * snapshot.RasterWidth),
            0, snapshot.RasterWidth - 1);
        return true;
    }

    private static bool TrySpatialCoordinate(ulong index, BScanSpatialViewport viewport, Rect plot, out double x)
    {
        decimal offset = BScanSpatialViewportOperations.RelativeOffset(index, viewport.OriginAcquisitionIndex);
        decimal ratio = (offset - viewport.MinimumOffset) / viewport.Span;
        x = plot.Left + (double)ratio * plot.Width;
        return ratio is >= 0 and <= 1;
    }

    internal bool HitSelection(Point point) => SelectedAcquisitionIndex is { } selected &&
        SpatialViewport is { } viewport && TrySpatialCoordinate(selected, viewport, PlotBounds(), out double x) &&
        Math.Abs(point.X - x) <= 8 && point.Y >= PlotBounds().Top && point.Y <= PlotBounds().Bottom;

    private void DrawAxisLabels(DrawingContext drawing, Rect plot)
    {
        if (SpatialViewport is { } spatial)
        {
            foreach (AdaptiveAxisTick tick in AdaptiveAxisTicks.Create(
                spatial.MinimumOffset, spatial.MaximumOffset, plot.Width))
            {
                decimal absolute = spatial.OriginAcquisitionIndex + tick.Value;
                Label(drawing, decimal.Floor(absolute).ToString(CultureInfo.InvariantCulture),
                    new Point(plot.Left + plot.Width * tick.Position, plot.Bottom + 22));
            }
        }
        if (TimeViewport is { } time)
        {
            foreach (AdaptiveAxisTick tick in AdaptiveAxisTicks.Create(
                (decimal)time.VisibleMinimumSeconds, (decimal)time.VisibleMaximumSeconds, plot.Height))
            {
                Label(drawing, $"{(double)tick.Value * 1e6:0.###} µs",
                    new Point(2, plot.Top + plot.Height * tick.Position - 7));
            }
        }
    }

    private Rect PlotBounds() => new(64, 16, Math.Max(0, ActualWidth - 84), Math.Max(0, ActualHeight - 58));
    private static void Redraw(DependencyObject sender, DependencyPropertyChangedEventArgs args) =>
        ((BScanControl)sender).RedrawVisuals();

    private void RedrawVisuals()
    {
        UpdateSelectionVisual();
        InvalidateVisual();
    }

    private void UpdateSelectionVisual()
    {
        using DrawingContext drawing = _selectionVisual.RenderOpen();
        Rect plot = PlotBounds();
        if (plot.Width > 0 && plot.Height > 0 && SelectedAcquisitionIndex is { } selected &&
            SpatialViewport is { } spatial && TrySpatialCoordinate(selected, spatial, plot, out double selectedX))
        {
            drawing.DrawLine(Selection, new Point(selectedX, plot.Top), new Point(selectedX, plot.Bottom));
            _hasSelectionVisual = true;
        }
        else { _hasSelectionVisual = false; }
    }
    private static T Freeze<T>(T value) where T : Freezable { value.Freeze(); return value; }
    private void Label(DrawingContext drawing, string value, Point origin) => drawing.DrawText(
        new FormattedText(value, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface("Segoe UI"), 11, Text, VisualTreeHelper.GetDpi(this).PixelsPerDip), origin);
}
