using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Input;
using System.Windows.Threading;
using UTStudio.Visualization.Core;
using UTStudio.Visualization.Wpf.Rendering;

namespace UTStudio.Visualization.Wpf.Controls;

/// <summary>WPF drawing surface for an immutable, already projected A-Scan snapshot.</summary>
public sealed class AScanControl : FrameworkElement
{
    public static readonly DependencyProperty SnapshotProperty = DependencyProperty.Register(nameof(Snapshot),
        typeof(AScanSnapshot), typeof(AScanControl), new PropertyMetadata(null, SnapshotChanged));
    public static readonly DependencyProperty CursorStateProperty = DependencyProperty.Register(nameof(CursorState),
        typeof(AScanCursorState), typeof(AScanControl),
        new FrameworkPropertyMetadata(null, CursorStateChanged));
    public static readonly DependencyProperty TimeViewportProperty = DependencyProperty.Register(nameof(TimeViewport),
        typeof(ScanTimeViewport), typeof(AScanControl),
        new FrameworkPropertyMetadata(null, TimeViewportChanged));
    private readonly DispatcherTimer _timer;
    private readonly TimeProvider _clock;
    private long? _lastRefresh;
    private AScanSnapshot? _displayed;
    private AScanCursorState? _displayedCursors;
    private bool _dirty;
    private AScanCursorId? _draggingCursor;
    private bool _panning;
    private double _lastPanX;
    private static readonly Brush BackgroundBrush = FrozenBrush(0x0B, 0x12, 0x20);
    private static readonly Brush CurveBrush = FrozenBrush(0x41, 0xDD, 0xBA);
    private static readonly Brush LabelBrush = FrozenBrush(0xB8, 0xC7, 0xDA);
    private static readonly Pen AxisPen = FrozenPen(FrozenBrush(0x64, 0x79, 0x92), 1);
    private static readonly Pen GridPen = FrozenPen(FrozenBrush(0x27, 0x38, 0x4D), 1);
    private static readonly Pen CurvePen = FrozenPen(CurveBrush, 1.25);
    private static readonly Pen CursorAPen = FrozenPen(FrozenBrush(0xFF, 0xC8, 0x57), 1.5);
    private static readonly Pen CursorBPen = FrozenPen(FrozenBrush(0xF7, 0x7F, 0xBE), 1.5);
    private const double CursorHitToleranceDip = 8;

    public AScanControl() : this(TimeProvider.System) { }

    internal AScanControl(TimeProvider clock)
    {
        _clock = clock;
        ClipToBounds = true;
        _timer = new DispatcherTimer(DispatcherPriority.Render, Dispatcher) { Interval = TimeSpan.FromMilliseconds(16) };
        _timer.Tick += Refresh;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        MouseLeftButtonDown += OnMouseLeftButtonDown;
        MouseDown += OnMouseDown;
        MouseMove += OnMouseMove;
        MouseLeftButtonUp += OnMouseLeftButtonUp;
        MouseUp += OnMouseUp;
        MouseWheel += OnMouseWheel;
        LostMouseCapture += OnLostMouseCapture;
    }

    public AScanSnapshot? Snapshot
    {
        get => (AScanSnapshot?)GetValue(SnapshotProperty);
        set => SetValue(SnapshotProperty, value);
    }

    public AScanCursorState? CursorState
    {
        get => (AScanCursorState?)GetValue(CursorStateProperty);
        set => SetValue(CursorStateProperty, value);
    }

    public ScanTimeViewport? TimeViewport
    {
        get => (ScanTimeViewport?)GetValue(TimeViewportProperty);
        set => SetValue(TimeViewportProperty, value);
    }

    public event EventHandler<AScanCursorMoveRequestedEventArgs>? CursorMoveRequested;
    public event EventHandler<AScanCursorActivatedEventArgs>? CursorActivated;
    public event EventHandler<ScanTimeZoomRequestedEventArgs>? TimeZoomRequested;
    public event EventHandler<ScanTimePanRequestedEventArgs>? TimePanRequested;

    internal bool IsRefreshTimerEnabled => _timer.IsEnabled;
    internal bool HasDisplayedSnapshot => _displayed is not null;
    internal ulong? DisplayedCursorSnapshotVersion => _displayedCursors?.SnapshotVersion;

    private static void SnapshotChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        var control = (AScanControl)sender;
        control._dirty = true;
        if (args.NewValue is null)
        { control._displayed = null; control._displayedCursors = null; control.InvalidateVisual(); }
    }

    private static void CursorStateChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        var control = (AScanControl)sender;
        var state = (AScanCursorState?)args.NewValue;
        if (state is null)
        { control._displayedCursors = null; control.InvalidateVisual(); return; }
        if (control._displayed is { } snapshot && Matches(state, snapshot))
        { control._displayedCursors = state; control.InvalidateVisual(); }
    }

    private static void TimeViewportChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args) =>
        ((AScanControl)sender).InvalidateVisual();

    private void OnLoaded(object sender, RoutedEventArgs args) { _dirty = true; _timer.Start(); }
    private void OnUnloaded(object sender, RoutedEventArgs args)
    { EndInteraction(); _timer.Stop(); _displayed = null; _displayedCursors = null; }
    private void Refresh(object? sender, EventArgs args) => RefreshSnapshot();

    internal void RefreshSnapshot()
    {
        if (_dirty) { InvalidateVisual(); }
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext drawing)
    {
        base.OnRender(drawing);
        // Measure from actual drawing, not timer admission: delayed WPF work cannot bunch new data.
        if (_dirty && TryAdmitSnapshot())
        {
            _dirty = false;
            _displayed = Snapshot;
            _displayedCursors = _displayed is { } snapshot && Matches(CursorState, snapshot) ? CursorState : null;
        }
        drawing.DrawRectangle(BackgroundBrush, null, new Rect(RenderSize));
        Rect plot = PlotBounds();
        double width = plot.Width, height = plot.Height;
        if (width <= 0 || height <= 0) { return; }
        for (int i = 0; i <= 4; i++)
        {
            double y = plot.Top + height * i / 4;
            drawing.DrawLine(i == 2 ? AxisPen : GridPen, new Point(plot.Left, y), new Point(plot.Right, y));
            Label(drawing, $"{100 - 50 * i:+0;-0;0} %", new Point(5, y - 8));
        }
        drawing.DrawRectangle(null, AxisPen, plot);
        (double minimum, double maximum) = VisibleRange();
        for (int i = 0; i <= 4; i++)
        {
            double x = plot.Left + width * i / 4;
            double time = minimum + (maximum - minimum) * i / 4;
            drawing.DrawLine(GridPen, new Point(x, plot.Top), new Point(x, plot.Bottom));
            Label(drawing, $"{time * 1e6:0.##} µs", new Point(LabelOriginX(plot, x - 20, 48), plot.Bottom + 10));
        }
        if (_displayed is null)
        {
            Label(drawing, "Pulse Start para adquirir RF simulada", new Point(plot.Left + 16, plot.Top + 16));
            return;
        }
        var points = AScanCoordinates.Map(_displayed.Points, minimum, maximum, width, height);
        if (points.Length == 0) { return; }
        drawing.PushClip(new RectangleGeometry(plot));
        if (points.Length == 1)
        {
            drawing.DrawEllipse(CurveBrush, null, new Point(plot.Left + points[0].X, plot.Top + points[0].Y), 2, 2);
        }
        else
        {
            var geometry = new StreamGeometry();
            using (var context = geometry.Open())
            {
                context.BeginFigure(new Point(plot.Left + points[0].X, plot.Top + points[0].Y), false, false);
                for (int i = 1; i < points.Length; i++)
                { context.LineTo(new Point(plot.Left + points[i].X, plot.Top + points[i].Y), true, false); }
            }
            geometry.Freeze();
            drawing.DrawGeometry(null, CurvePen, geometry);
        }
        drawing.Pop();
        DrawCursors(drawing, plot, minimum, maximum);
    }

    private void DrawCursors(DrawingContext drawing, Rect plot, double minimum, double maximum)
    {
        if (_displayedCursors is not { IsVisible: true } cursors || maximum <= minimum) { return; }
        DrawCursor(AScanCursorId.A, cursors.A, CursorAPen);
        DrawCursor(AScanCursorId.B, cursors.B, CursorBPen);
        string approximation = $"Δt {cursors.DeltaTimeSeconds * 1e6:+0.###;-0.###;0} µs   " +
            $"ΔRF {cursors.DeltaAmplitudePercent:+0.##;-0.##;0} %   puntos visuales";
        Label(drawing, approximation, new Point(plot.Left + 8, plot.Top + 4));

        void DrawCursor(AScanCursorId id, AScanCursorMeasurement measurement, Pen pen)
        {
            if (measurement.TimeSeconds < minimum || measurement.TimeSeconds > maximum) { return; }
            double x = plot.Left + (measurement.TimeSeconds - minimum) / (maximum - minimum) * plot.Width;
            drawing.DrawLine(pen, new Point(x, plot.Top), new Point(x, plot.Bottom));
            string active = cursors.ActiveCursor == id ? "*" : string.Empty;
            Label(drawing, $"{id}{active} {measurement.TimeSeconds * 1e6:0.###} µs  {measurement.AmplitudePercent:+0.##;-0.##;0} %",
                new Point(LabelOriginX(plot, x + 4, 128),
                    id == AScanCursorId.A ? plot.Top + 22 : plot.Top + 40));
        }
    }

    private void OnMouseLeftButtonDown(object sender, MouseButtonEventArgs args)
    {
        if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0)
        { if (BeginPan(args.GetPosition(this))) { args.Handled = true; } return; }
        if (BeginCursorDrag(args.GetPosition(this))) { args.Handled = true; }
    }

    private void OnMouseDown(object sender, MouseButtonEventArgs args)
    {
        if (args.ChangedButton == MouseButton.Middle && BeginPan(args.GetPosition(this))) { args.Handled = true; }
    }

    private void OnMouseMove(object sender, MouseEventArgs args)
    {
        if (_panning)
        { ContinuePan(args.GetPosition(this)); args.Handled = true; return; }
        if (_draggingCursor is not null && args.LeftButton == MouseButtonState.Pressed)
        { ContinueCursorDrag(args.GetPosition(this)); args.Handled = true; }
    }

    private void OnMouseUp(object sender, MouseButtonEventArgs args)
    {
        if (_panning && (args.ChangedButton == MouseButton.Middle || args.ChangedButton == MouseButton.Left))
        { ContinuePan(args.GetPosition(this)); EndInteraction(); args.Handled = true; }
    }

    private void OnMouseWheel(object sender, MouseWheelEventArgs args)
    {
        if (RequestTimeZoom(args.GetPosition(this), args.Delta)) { args.Handled = true; }
    }

    internal bool RequestTimeZoom(Point position, int wheelDelta)
    {
        Rect plot = PlotBounds();
        if (!plot.Contains(position) || plot.Width <= 0 || wheelDelta == 0) { return false; }
        (double minimum, double maximum) = VisibleRange();
        double anchor = minimum + Math.Clamp((position.X - plot.Left) / plot.Width, 0, 1) * (maximum - minimum);
        TimeZoomRequested?.Invoke(this, new(anchor, Math.Pow(1.2, wheelDelta / 120d)));
        return true;
    }

    private void OnMouseLeftButtonUp(object sender, MouseButtonEventArgs args)
    {
        if (_draggingCursor is not null) { ContinueCursorDrag(args.GetPosition(this)); EndCursorDrag(); args.Handled = true; }
    }

    private void OnLostMouseCapture(object sender, MouseEventArgs args)
    { _draggingCursor = null; _panning = false; }

    internal bool BeginCursorDrag(Point position)
    {
        AScanCursorId? hit = HitTestCursor(position);
        if (hit is null) { return false; }
        if (!CaptureMouse()) { return false; }
        _draggingCursor = hit;
        CursorActivated?.Invoke(this, new(hit.Value));
        ContinueCursorDrag(position);
        return true;
    }

    internal void ContinueCursorDrag(Point position)
    {
        if (_draggingCursor is not { } cursor || _displayed is null) { return; }
        Rect plot = PlotBounds();
        if (plot.Width <= 0) { return; }
        double ratio = Math.Clamp((position.X - plot.Left) / plot.Width, 0, 1);
        (double minimum, double maximum) = VisibleRange();
        double time = minimum + ratio * (maximum - minimum);
        CursorMoveRequested?.Invoke(this, new(cursor, time));
    }

    internal void EndCursorDrag()
    {
        _draggingCursor = null;
        if (IsMouseCaptured) { ReleaseMouseCapture(); }
    }

    internal bool BeginPan(Point position)
    {
        if (!PlotBounds().Contains(position) || TimeViewport is null || !CaptureMouse()) { return false; }
        _panning = true;
        _lastPanX = position.X;
        return true;
    }

    internal void ContinuePan(Point position)
    {
        if (!_panning || TimeViewport is not { } viewport) { return; }
        Rect plot = PlotBounds();
        if (plot.Width <= 0) { return; }
        double delta = -((position.X - _lastPanX) / plot.Width) * viewport.VisibleSpanSeconds;
        _lastPanX = position.X;
        TimePanRequested?.Invoke(this, new(delta));
    }

    internal void EndInteraction()
    {
        _draggingCursor = null;
        _panning = false;
        if (IsMouseCaptured) { ReleaseMouseCapture(); }
    }

    internal void CancelCursorDrag() => EndCursorDrag();
    internal AScanCursorId? DraggingCursor => _draggingCursor;

    internal AScanCursorId? HitTestCursor(Point position)
    {
        if (_displayed is null || _displayedCursors is not { IsVisible: true } cursors) { return null; }
        Rect plot = PlotBounds();
        if (!plot.Contains(position) || plot.Width <= 0) { return null; }
        (double minimum, double maximum) = VisibleRange();
        double range = maximum - minimum;
        if (range <= 0) { return null; }
        double ad = Distance(cursors.A.TimeSeconds), bd = Distance(cursors.B.TimeSeconds);
        if (ad > CursorHitToleranceDip && bd > CursorHitToleranceDip) { return null; }
        if (ad == bd) { return cursors.ActiveCursor; }
        return ad < bd ? AScanCursorId.A : AScanCursorId.B;

        double Distance(double time) => time < minimum || time > maximum
            ? double.PositiveInfinity
            : Math.Abs(position.X - (plot.Left + (time - minimum) / range * plot.Width));
    }

    private Rect PlotBounds() => new(64, 16, Math.Max(0, ActualWidth - 84), Math.Max(0, ActualHeight - 54));
    private (double Minimum, double Maximum) VisibleRange() => TimeViewport is { } viewport && _displayed is not null
        ? (Math.Max(_displayed.MinimumTimeSeconds, viewport.VisibleMinimumSeconds),
            Math.Min(_displayed.MaximumTimeSeconds, viewport.VisibleMaximumSeconds))
        : (_displayed?.MinimumTimeSeconds ?? 0, _displayed?.MaximumTimeSeconds ?? 2047d / 50_000_000);
    private static double LabelOriginX(Rect plot, double desired, double labelWidth) =>
        plot.Width <= labelWidth ? plot.Left : Math.Clamp(desired, plot.Left, plot.Right - labelWidth);
    private static bool Matches(AScanCursorState? state, AScanSnapshot snapshot) =>
        state is not null && state.RunId == snapshot.Metadata.RunId && state.SnapshotVersion == snapshot.Version;

    private bool TryAdmitSnapshot()
    {
        long now = _clock.GetTimestamp();
        if (_lastRefresh is { } last &&
            _clock.GetElapsedTime(last, now) < AScanVisualDelivery.MinimumPublicationInterval)
        { return false; }
        _lastRefresh = now;
        return true;
    }

    private void Label(DrawingContext drawing, string text, Point origin) => drawing.DrawText(
        new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            new Typeface("Segoe UI"), 12, LabelBrush, VisualTreeHelper.GetDpi(this).PixelsPerDip), origin);
    private static Brush FrozenBrush(byte red, byte green, byte blue)
    { var brush = new SolidColorBrush(Color.FromRgb(red, green, blue)); brush.Freeze(); return brush; }
    private static Pen FrozenPen(Brush brush, double width)
    { var pen = new Pen(brush, width); pen.Freeze(); return pen; }
}

public sealed class AScanCursorMoveRequestedEventArgs(AScanCursorId cursor, double timeSeconds) : EventArgs
{
    public AScanCursorId Cursor { get; } = cursor;
    public double TimeSeconds { get; } = timeSeconds;
}

public sealed class AScanCursorActivatedEventArgs(AScanCursorId cursor) : EventArgs
{
    public AScanCursorId Cursor { get; } = cursor;
}

public sealed class ScanTimeZoomRequestedEventArgs(double anchorSeconds, double factor) : EventArgs
{
    public double AnchorSeconds { get; } = anchorSeconds;
    public double Factor { get; } = factor;
}

public sealed class ScanTimePanRequestedEventArgs(double deltaSeconds) : EventArgs
{
    public double DeltaSeconds { get; } = deltaSeconds;
}
