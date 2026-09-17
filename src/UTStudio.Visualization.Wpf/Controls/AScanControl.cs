using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using UTStudio.Visualization.Core;
using UTStudio.Visualization.Wpf.Rendering;

namespace UTStudio.Visualization.Wpf.Controls;

/// <summary>WPF drawing surface for an immutable, already projected A-Scan snapshot.</summary>
public sealed class AScanControl : FrameworkElement
{
    public static readonly DependencyProperty SnapshotProperty = DependencyProperty.Register(nameof(Snapshot),
        typeof(AScanSnapshot), typeof(AScanControl), new PropertyMetadata(null, SnapshotChanged));
    private readonly DispatcherTimer _timer;
    private readonly TimeProvider _clock;
    private long? _lastRefresh;
    private AScanSnapshot? _displayed;
    private bool _dirty;
    private static readonly Brush BackgroundBrush = FrozenBrush(0x0B, 0x12, 0x20);
    private static readonly Brush CurveBrush = FrozenBrush(0x41, 0xDD, 0xBA);
    private static readonly Brush LabelBrush = FrozenBrush(0xB8, 0xC7, 0xDA);
    private static readonly Pen AxisPen = FrozenPen(FrozenBrush(0x64, 0x79, 0x92), 1);
    private static readonly Pen GridPen = FrozenPen(FrozenBrush(0x27, 0x38, 0x4D), 1);
    private static readonly Pen CurvePen = FrozenPen(CurveBrush, 1.25);

    public AScanControl() : this(TimeProvider.System) { }

    internal AScanControl(TimeProvider clock)
    {
        _clock = clock;
        ClipToBounds = true;
        _timer = new DispatcherTimer(DispatcherPriority.Render, Dispatcher) { Interval = TimeSpan.FromMilliseconds(16) };
        _timer.Tick += Refresh;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    public AScanSnapshot? Snapshot
    {
        get => (AScanSnapshot?)GetValue(SnapshotProperty);
        set => SetValue(SnapshotProperty, value);
    }

    internal bool IsRefreshTimerEnabled => _timer.IsEnabled;
    internal bool HasDisplayedSnapshot => _displayed is not null;

    private static void SnapshotChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        var control = (AScanControl)sender;
        control._dirty = true;
        if (args.NewValue is null) { control._displayed = null; control.InvalidateVisual(); }
    }

    private void OnLoaded(object sender, RoutedEventArgs args) { _dirty = true; _timer.Start(); }
    private void OnUnloaded(object sender, RoutedEventArgs args) { _timer.Stop(); _displayed = null; }
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
        if (_dirty && TryAdmitSnapshot()) { _dirty = false; _displayed = Snapshot; }
        drawing.DrawRectangle(BackgroundBrush, null, new Rect(RenderSize));
        double width = ActualWidth - 84, height = ActualHeight - 54;
        if (width <= 0 || height <= 0) { return; }
        var plot = new Rect(64, 16, width, height);
        for (int i = 0; i <= 4; i++)
        {
            double y = plot.Top + height * i / 4;
            drawing.DrawLine(i == 2 ? AxisPen : GridPen, new Point(plot.Left, y), new Point(plot.Right, y));
            Label(drawing, $"{100 - 50 * i:+0;-0;0} %", new Point(5, y - 8));
        }
        drawing.DrawRectangle(null, AxisPen, plot);
        double minimum = _displayed?.MinimumTimeSeconds ?? 0;
        double maximum = _displayed?.MaximumTimeSeconds ?? 2047d / 50_000_000;
        Label(drawing, $"{minimum * 1e6:0.##} µs", new Point(plot.Left, plot.Bottom + 10));
        Label(drawing, $"{maximum * 1e6:0.##} µs", new Point(Math.Max(plot.Left, plot.Right - 64), plot.Bottom + 10));
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
    }

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
