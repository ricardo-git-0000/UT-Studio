using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using UTStudio.Contracts.Processing;
using UTStudio.Domain.Processing;
using UTStudio.Visualization.Core;
using UTStudio.Visualization.Wpf.Rendering;

namespace UTStudio.Visualization.Wpf.Controls;

/// <summary>WPF surface whose sole visual input is one immutable, indivisible snapshot.</summary>
public sealed class AScanControl : FrameworkElement
{
    public static readonly DependencyProperty VisualSnapshotProperty = DependencyProperty.Register(
        nameof(VisualSnapshot), typeof(AScanCompositeVisualSnapshot), typeof(AScanControl),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, VisualSnapshotChanged));
    private readonly DispatcherTimer _timer;
    private readonly TimeProvider _clock;
    private readonly Func<ModifierKeys> _readModifiers;
    private readonly Func<MouseEventArgs, Point> _readPosition;
    private readonly Func<Point> _readCurrentPosition;
    private readonly Func<MouseButtonEventArgs, int> _readClickCount;
    private long? _lastRefresh;
    private AScanCompositeVisualSnapshot? _received, _displayed;
    private bool _retainingCompleteGateSnapshot;
    private bool _dirty;
    private InteractionOwner _owner;
    private AScanCursorId? _draggingCursor;
    private double? _lastRequestedCursorTime;
    private GateCaptureGeneration? _gateCapture;
    private GateHandleLogicalIdentity? _gateHandle;
    private double _windowPointerStartTime, _windowInitialStart;
    private bool _cursorInputTracking;
    private double _lastPanX;
    private ulong _transformInvalidation;
    private AScanControlAutomationPeer? _automationPeer;
    private static readonly Brush BackgroundBrush = FrozenBrush(0x0B, 0x12, 0x20);
    private static readonly Brush CurveBrush = FrozenBrush(0x41, 0xDD, 0xBA);
    private static readonly Brush LabelBrush = FrozenBrush(0xB8, 0xC7, 0xDA);
    private static readonly Brush GateBrush = FrozenBrush(0x7D, 0xC4, 0xFF);
    private static readonly Brush SelectedGateBrush = FrozenBrush(0xFF, 0xC8, 0x57);
    private static readonly Pen AxisPen = FrozenPen(FrozenBrush(0x64, 0x79, 0x92), 1);
    private static readonly Pen GridPen = FrozenPen(FrozenBrush(0x27, 0x38, 0x4D), 1);
    private static readonly Pen CurvePen = FrozenPen(CurveBrush, 1.25);
    private static readonly Pen CursorAPen = FrozenPen(FrozenBrush(0xFF, 0xC8, 0x57), 1.5);
    private static readonly Pen CursorBPen = FrozenPen(FrozenBrush(0xF7, 0x7F, 0xBE), 1.5);
    private static readonly Pen GatePen = FrozenPen(GateBrush, 1.25);
    private static readonly Pen SelectedGatePen = FrozenPen(SelectedGateBrush, 2);
    private const double CursorHitToleranceDip = 8, GateHitToleranceDip = 9, GateBoundaryHitToleranceDip = 24;

    public AScanControl() : this(TimeProvider.System, () => Keyboard.Modifiers, null, null, null) { }
    internal AScanControl(TimeProvider clock) : this(clock, () => Keyboard.Modifiers, null, null, null) { }
    internal AScanControl(TimeProvider clock, Func<ModifierKeys> readModifiers,
        Func<MouseEventArgs, Point>? readPosition, Func<MouseButtonEventArgs, int>? readClickCount,
        Func<Point>? readCurrentPosition = null)
    {
        _clock = clock; _readModifiers = readModifiers ?? throw new ArgumentNullException(nameof(readModifiers));
        _readPosition = readPosition ?? (args => args.GetPosition(this));
        _readCurrentPosition = readCurrentPosition ?? ReadPhysicalCursorPosition;
        _readClickCount = readClickCount ?? (args => args.ClickCount);
        ClipToBounds = true; Focusable = true;
        _timer = new(DispatcherPriority.Render, Dispatcher) { Interval = TimeSpan.FromMilliseconds(16) };
        _timer.Tick += (_, _) => RefreshSnapshot();
        Loaded += OnLoaded; Unloaded += OnUnloaded;
        AddHandler(MouseLeftButtonDownEvent, new MouseButtonEventHandler(OnMouseLeftButtonDown), handledEventsToo: true);
        AddHandler(MouseDownEvent, new MouseButtonEventHandler(OnMouseDown), handledEventsToo: true);
        AddHandler(MouseMoveEvent, new MouseEventHandler(OnMouseMove), handledEventsToo: true);
        AddHandler(MouseLeftButtonUpEvent, new MouseButtonEventHandler(OnMouseLeftButtonUp), handledEventsToo: true);
        AddHandler(MouseRightButtonUpEvent, new MouseButtonEventHandler(OnMouseRightButtonUp), handledEventsToo: true);
        MouseUp += OnMouseUp; MouseWheel += OnMouseWheel; LostMouseCapture += OnLostMouseCapture;
    }

    public AScanCompositeVisualSnapshot? VisualSnapshot
    { get => (AScanCompositeVisualSnapshot?)GetValue(VisualSnapshotProperty); set => SetValue(VisualSnapshotProperty, value); }
    public event EventHandler<AScanCursorMoveRequestedEventArgs>? CursorMoveRequested;
    public event EventHandler<AScanCursorActivatedEventArgs>? CursorActivated;
    public event EventHandler<ScanTimeZoomRequestedEventArgs>? TimeZoomRequested;
    public event EventHandler<ScanTimePanRequestedEventArgs>? TimePanRequested;
    public event EventHandler? TimeResetRequested;
    public event EventHandler<GateBeginCaptureRequestedEventArgs>? GateBeginCaptureRequested;
    public event EventHandler<GateBeginWindowCaptureRequestedEventArgs>? GateBeginWindowCaptureRequested;
    public event EventHandler<GateInteractionRequestedEventArgs>? GateInteractionRequested;
    public event EventHandler<AScanVisualContextChangedEventArgs>? VisualContextChanged;
    internal bool IsRefreshTimerEnabled => _timer.IsEnabled;
    internal bool IsCursorInputTrackingEnabled => _cursorInputTracking;
    internal bool HasDisplayedSnapshot => _displayed is not null;
    internal ulong? DisplayedCursorSnapshotVersion => _displayed?.Cursors?.SnapshotVersion;
    internal AScanCompositeVisualSnapshot? DisplayedVisualSnapshot => _displayed;
    internal ulong? RenderedSignalSnapshotVersion => SignalSnapshotForRender()?.Signal.Version;
    internal int RenderedDetectedGateEventCount => _retainingCompleteGateSnapshot ? 0 :
        _displayed?.Gates.Gates.Count(gate => gate.DetectedEvent is not null) ?? 0;
    internal GateCaptureGeneration? ActiveGateCapture => _gateCapture;
    internal bool HasCurrentTransform => _displayed is not null && IsTransformCompatible(_displayed.Transform);

    private static void VisualSnapshotChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        var control = (AScanControl)sender;
        control._received = (AScanCompositeVisualSnapshot?)args.NewValue; control._dirty = true;
        if (control._received is not null && control._gateCapture is { } token &&
            !IsCompatibleCaptureContext(control._received, token))
            control.CancelInteraction(true);
        if (args.NewValue is null)
        {
            // Live composition briefly becomes unavailable while signal, gate geometry and viewport revisions
            // converge. That is not an authority or lifecycle boundary and must not terminate a physical gesture.
            // A subsequent incompatible non-null snapshot still cancels an exact gate capture above.
            bool preservesPhysicalGesture = control._owner is InteractionOwner.Cursor or InteractionOwner.Gate;
            if (!preservesPhysicalGesture || !control.IsLoaded)
                control.CancelInteraction(true);
            if (control._displayed?.Gates.Gates.Count > 0 && control.IsLoaded)
                control._retainingCompleteGateSnapshot = true;
            else
            {
                control._displayed = null; control._retainingCompleteGateSnapshot = false;
                control.UpdateAutomationPeers(null);
            }
        }
        else if (ShouldRetainCompleteGateSnapshot(control._displayed, control._received!))
        {
            control._retainingCompleteGateSnapshot = true; control._dirty = false;
            control._lastRefresh = control._clock.GetTimestamp(); control.UpdateAutomationPeers(control._displayed);
        }
        else if ((control._displayed is null || !control.IsTransformCompatible(control._displayed.Transform)) &&
            control.IsTransformCompatible(control._received!.Transform))
        {
            control._displayed = control._received; control._dirty = false;
            control._retainingCompleteGateSnapshot = false;
            control._lastRefresh = control._clock.GetTimestamp(); control.UpdateAutomationPeers(control._displayed);
        }
        else if (control._received!.Gates.Status == GateOverlayGeometryStatus.Current &&
            control.IsTransformCompatible(control._received.Transform))
        {
            // A complete exact gate snapshot can be shorter lived than the ordinary visual cadence while RF is
            // live. Latch it immediately so a following Evaluating placeholder cannot make the Preview vanish.
            control._displayed = control._received; control._dirty = false;
            control._retainingCompleteGateSnapshot = false;
            control._lastRefresh = control._clock.GetTimestamp(); control.UpdateAutomationPeers(control._displayed);
        }
    }
    private void OnLoaded(object sender, RoutedEventArgs args) { _dirty = true; _timer.Start(); RequestVisualContext(); }
    private void OnUnloaded(object sender, RoutedEventArgs args)
    { CancelInteraction(true); _timer.Stop(); _displayed = null; UpdateAutomationPeers(null); _transformInvalidation++; }
    internal void RefreshSnapshot()
    {
        if (_dirty) { TryAdmitReceived(); InvalidateVisual(); }
    }
    internal void RefreshCursorGesture() => ContinuePhysicalGestureFromInput();
    private Point ReadPhysicalCursorPosition()
    {
        if (!GetCursorPos(out NativePoint screen)) return Mouse.GetPosition(this);
        try { return PointFromScreen(new(screen.X, screen.Y)); }
        catch (InvalidOperationException) { return Mouse.GetPosition(this); }
    }
    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    { CancelForTransformChange(); base.OnRenderSizeChanged(sizeInfo); RequestVisualContext(); InvalidateVisual(); }
    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    { CancelForTransformChange(); base.OnDpiChanged(oldDpi, newDpi); RequestVisualContext(); InvalidateVisual(); }
    private void CancelForTransformChange()
    {
        _transformInvalidation++;
        // Gate captures and pan deltas are tied to the previous exact transform. A manual cursor gesture is not:
        // it can resume as soon as the coherent replacement transform is published, and live layout must not steal
        // its mouse capture merely because changing status text resized the plot.
        if (_owner != InteractionOwner.Cursor) CancelInteraction(true);
        if (_displayed is not null) UpdateAutomationPeers(null);
    }

    protected override void OnRender(DrawingContext drawing)
    {
        base.OnRender(drawing);
        if (_dirty) TryAdmitReceived();
        AScanCompositeVisualSnapshot? gateSnapshot = _displayed;
        AScanCompositeVisualSnapshot? displayed = SignalSnapshotForRender();
        bool currentTransform = displayed is not null && (IsTransformCompatible(displayed.Transform) ||
            _retainingCompleteGateSnapshot && _received is not null &&
            displayed.Transform.TimeViewport == _received.Transform.TimeViewport);
        if (!currentTransform) displayed = null;
        drawing.DrawRectangle(BackgroundBrush, null, new Rect(RenderSize));
        Rect plot = PlotBounds(); if (plot.Width <= 0 || plot.Height <= 0) return;
        DrawAxes(drawing, plot, displayed);
        if (displayed is null)
        { Label(drawing, "Pulse Start para adquirir RF simulada", new(plot.Left + 16, plot.Top + 16)); return; }
        DrawSignal(drawing, plot, displayed); DrawCursors(drawing, plot, displayed);
        DrawGates(drawing, plot, gateSnapshot?.Gates ?? displayed.Gates,
            suppressDetectedEvents: _retainingCompleteGateSnapshot);
    }

    private AScanCompositeVisualSnapshot? SignalSnapshotForRender() =>
        _retainingCompleteGateSnapshot && _received is { } received && IsTransformCompatible(received.Transform)
            ? received : _displayed;
    private void DrawAxes(DrawingContext drawing, Rect plot, AScanCompositeVisualSnapshot? displayed)
    {
        for (int i = 0; i <= 4; i++)
        {
            double y = plot.Top + plot.Height * i / 4;
            drawing.DrawLine(i == 2 ? AxisPen : GridPen, new(plot.Left, y), new(plot.Right, y));
            Label(drawing, $"{100 - 50 * i:+0;-0;0} %", new(5, y - 8));
        }
        drawing.DrawRectangle(null, AxisPen, plot);
        (double minimum, double maximum) = VisibleRange(displayed);
        for (int i = 0; i <= 4; i++)
        {
            double x = plot.Left + plot.Width * i / 4, time = minimum + (maximum - minimum) * i / 4;
            drawing.DrawLine(GridPen, new(x, plot.Top), new(x, plot.Bottom));
            Label(drawing, $"{time * 1e6:0.##} µs", new(LabelOriginX(plot, x - 20, 48), plot.Bottom + 10));
        }
    }
    private static void DrawSignal(DrawingContext drawing, Rect plot, AScanCompositeVisualSnapshot displayed)
    {
        (double minimum, double maximum) = VisibleRange(displayed);
        var points = AScanCoordinates.Map(displayed.Signal.Points, minimum, maximum, plot.Width, plot.Height);
        if (points.Length == 0) return;
        drawing.PushClip(new RectangleGeometry(plot));
        if (points.Length == 1) drawing.DrawEllipse(CurveBrush, null, new(plot.Left + points[0].X, plot.Top + points[0].Y), 2, 2);
        else
        {
            var geometry = new StreamGeometry();
            using (var context = geometry.Open())
            {
                context.BeginFigure(new(plot.Left + points[0].X, plot.Top + points[0].Y), false, false);
                for (int i = 1; i < points.Length; i++) context.LineTo(new(plot.Left + points[i].X, plot.Top + points[i].Y), true, false);
            }
            geometry.Freeze(); drawing.DrawGeometry(null, CurvePen, geometry);
        }
        drawing.Pop();
    }
    private void DrawCursors(DrawingContext drawing, Rect plot, AScanCompositeVisualSnapshot displayed)
    {
        if (displayed.Cursors is not { IsVisible: true } cursors) return;
        (double minimum, double maximum) = VisibleRange(displayed); if (maximum <= minimum) return;
        DrawCursor(AScanCursorId.A, cursors.A, CursorAPen); DrawCursor(AScanCursorId.B, cursors.B, CursorBPen);
        Label(drawing, $"Δt {cursors.DeltaTimeSeconds * 1e6:+0.###;-0.###;0} µs   " +
            $"ΔRF {cursors.DeltaAmplitudePercent:+0.##;-0.##;0} %   puntos visuales", new(plot.Left + 8, plot.Top + 4));
        void DrawCursor(AScanCursorId id, AScanCursorMeasurement measurement, Pen pen)
        {
            if (measurement.TimeSeconds < minimum || measurement.TimeSeconds > maximum) return;
            double x = plot.Left + (measurement.TimeSeconds - minimum) / (maximum - minimum) * plot.Width;
            drawing.DrawLine(pen, new(x, plot.Top), new(x, plot.Bottom));
            Label(drawing, $"{id}{(cursors.ActiveCursor == id ? "*" : "")} {measurement.TimeSeconds * 1e6:0.###} µs  {measurement.AmplitudePercent:+0.##;-0.##;0} %",
                new(LabelOriginX(plot, x + 4, 128), id == AScanCursorId.A ? plot.Top + 22 : plot.Top + 40));
        }
    }
    private static void DrawGates(DrawingContext drawing, Rect plot, GateOverlayVisualSnapshot overlay,
        bool suppressDetectedEvents)
    {
        if (overlay.Status is not (GateOverlayGeometryStatus.Current or GateOverlayGeometryStatus.Evaluating)) return;
        drawing.PushClip(new RectangleGeometry(plot));
        foreach (ProjectedGateGeometryItem gate in overlay.Gates)
        {
            Pen pen = overlay.SelectedGate == gate.GateId ? SelectedGatePen : GatePen;
            if (gate.StartCoordinate is { } start) drawing.DrawLine(pen, new(start, plot.Top), new(start, plot.Bottom));
            if (gate.EndCoordinate is { } end) drawing.DrawLine(pen, new(end, plot.Top), new(end, plot.Bottom));
            foreach (GateThresholdProjection threshold in gate.Thresholds)
                if (gate.StartCoordinate is { } x1 && gate.EndCoordinate is { } x2)
                    drawing.DrawLine(pen, new(x1, threshold.Coordinate), new(x2, threshold.Coordinate));
            if (!suppressDetectedEvents && gate.DetectedEvent is { } detected)
            {
                Point center = new(detected.TimeCoordinate, detected.AmplitudeCoordinate);
                drawing.DrawEllipse(overlay.SelectedGate == gate.GateId ? SelectedGateBrush : GateBrush,
                    AxisPen, center, 4, 4);
            }
        }
        // Handles remain semantic hit-test and automation targets, but the complete boundary and threshold
        // lines are directly draggable; extra circular affordances would obscure the RF trace.
        drawing.Pop();
    }

    private void OnMouseLeftButtonDown(object sender, MouseButtonEventArgs args)
    {
        if (_owner == InteractionOwner.Cursor && !IsMouseCaptured) CancelInteraction(false, false);
        Point position = _readPosition(args);
        Point gatePosition = position;
        bool gateHit = HitTestGate(gatePosition) is not null;
        if (!gateHit)
        {
            Point physicalPosition = _readCurrentPosition();
            if (HitTestGate(physicalPosition) is not null)
            {
                gatePosition = physicalPosition;
                gateHit = true;
            }
        }
        if (gateHit)
        {
            BeginGateDrag(gatePosition);
            args.Handled = true;
            return;
        }
        if (_readClickCount(args) == 2 && RequestTimeReset(position)) { args.Handled = true; return; }
        if ((_readModifiers() & ModifierKeys.Shift) != 0)
        { if (BeginPan(position)) args.Handled = true; return; }
        if (BeginCursorDrag(position, allowNearest: true)) args.Handled = true;
    }
    private void OnMouseDown(object sender, MouseButtonEventArgs args)
    {
        if (args.ChangedButton == MouseButton.Middle && BeginPan(_readPosition(args))) args.Handled = true;
        else if (args.ChangedButton == MouseButton.Right)
        {
            Point position = _readPosition(args);
            if (HitTestGateWindow(position) is null)
            {
                Point physicalPosition = _readCurrentPosition();
                if (HitTestGateWindow(physicalPosition) is not null) position = physicalPosition;
            }
            if (BeginGateWindowDrag(position)) args.Handled = true;
        }
    }
    private void OnMouseMove(object sender, MouseEventArgs args)
    {
        if (_owner == InteractionOwner.Pan) { ContinuePan(_readPosition(args)); args.Handled = true; }
        else if (_owner == InteractionOwner.Cursor)
        { ContinueCursorDrag(_readPosition(args)); args.Handled = true; }
        else if (_owner == InteractionOwner.Gate && IsMouseCaptured)
        { ContinueGateDrag(_readPosition(args)); args.Handled = true; }
    }
    private void OnMouseUp(object sender, MouseButtonEventArgs args)
    {
        if (_owner == InteractionOwner.Pan && args.ChangedButton is MouseButton.Middle or MouseButton.Left)
        { ContinuePan(_readPosition(args)); CancelInteraction(false); args.Handled = true; }
    }
    private void OnMouseLeftButtonUp(object sender, MouseButtonEventArgs args)
    {
        if (_owner == InteractionOwner.Cursor) { ContinueCursorDrag(_readPosition(args)); CancelInteraction(false); args.Handled = true; }
        else if (_owner == InteractionOwner.Gate)
        {
            ContinueGateDrag(_readPosition(args));
            EndGateCapture(); args.Handled = true;
        }
    }
    private void OnMouseRightButtonUp(object sender, MouseButtonEventArgs args)
    {
        if (_owner == InteractionOwner.Gate && _gateHandle?.Kind == GateHandleKind.Window)
        { ContinueGateDrag(_readPosition(args)); EndGateCapture(); args.Handled = true; }
    }
    private void OnMouseWheel(object sender, MouseWheelEventArgs args)
    { if ((_readModifiers() & (ModifierKeys.Control | ModifierKeys.Shift | ModifierKeys.Alt)) == 0 && RequestTimeZoom(_readPosition(args), args.Delta)) args.Handled = true; }
    private void OnLostMouseCapture(object sender, MouseEventArgs args)
    {
        // Cursor and gate gestures deliberately continue from the physical pointer stream. LostMouseCapture is
        // routed and can also be raised by transient live UI changes; ending here truncates a real drag after its
        // first pixel. The global input stream completes the gesture on the matching button release.
        if (_owner is InteractionOwner.Cursor or InteractionOwner.Gate) return;
        CancelInteraction(true, false);
    }

    protected override void OnPreviewKeyDown(KeyEventArgs args)
    {
        base.OnPreviewKeyDown(args);
        if (_displayed is not { } displayed || !IsTransformCompatible(displayed.Transform)) return;
        GateHandleVisualState[] handles = OrderedHandles(displayed);
        GateHandleVisualState? focused = handles.FirstOrDefault(x => x.IsFocused);
        if (args.Key == Key.Tab)
        {
            int index = Array.FindIndex(handles, x => x.IsFocused);
            bool backwards = _readModifiers().HasFlag(ModifierKeys.Shift);
            int target = index < 0 ? (backwards ? handles.Length - 1 : 0) : index + (backwards ? -1 : 1);
            if (target >= 0 && target < handles.Length)
            { EmitGateIntent(backwards ? GateOverlayInteractionKind.FocusPrevious : GateOverlayInteractionKind.FocusNext, handles[target], null); args.Handled = true; }
            return;
        }
        bool isPreview = displayed.Gates.Source?.Kind == GateGeometrySourceKind.Preview;
        if (args.Key == Key.Enter && isPreview) { EmitGateIntent(GateOverlayInteractionKind.ConfirmPreview, focused, null); args.Handled = true; return; }
        if (args.Key == Key.Escape && isPreview) { EmitGateIntent(GateOverlayInteractionKind.CancelPreview, focused, null); args.Handled = true; return; }
        if (focused is null) return;
        bool decrement = args.Key is Key.Left or Key.Down, increment = args.Key is Key.Right or Key.Up;
        if (!decrement && !increment) return;
        if (focused.Kind == GateHandleKind.Threshold && args.Key is Key.Left or Key.Right ||
            focused.Kind != GateHandleKind.Threshold && args.Key is Key.Up or Key.Down) return;
        EmitGateIntent(increment ? GateOverlayInteractionKind.Increment : GateOverlayInteractionKind.Decrement, focused,
            Math.Clamp(focused.Value + (increment ? focused.Step : -focused.Step), focused.Minimum, focused.Maximum));
        args.Handled = true;
    }

    internal bool BeginGateDrag(Point position)
    {
        if (_owner != InteractionOwner.None || _displayed is not { } displayed || !IsTransformCompatible(displayed.Transform) ||
            HitTestGate(position) is not { } handle || !displayed.HasAuthoritativeIdentity)
            return false;
        AScanCompositeVisualSnapshot? receivedBefore = _received;
        var request = new GateBeginCaptureRequestedEventArgs(displayed, handle);
        GateBeginCaptureRequested?.Invoke(this, request);
        if (!request.IsAccepted || request.Capture is not { } acceptedCapture)
            return false;
        bool receivedIsCompatible = _received is null || ReferenceEquals(_received, receivedBefore) ||
            IsCompatibleCaptureContext(_received, acceptedCapture);
        if (!receivedIsCompatible || _displayed is null || !IsAcceptedToken(acceptedCapture, displayed, handle) ||
            !IsLoaded || !CaptureMouse())
        {
            GateInteractionRequested?.Invoke(this, new(GateOverlayInteractionKind.CancelPreview, request.Snapshot,
                handle.Identity, acceptedCapture, null));
            return false;
        }
        _owner = InteractionOwner.Gate; _gateCapture = acceptedCapture; _gateHandle = handle.Identity;
        StartCursorInputTracking(); Focus(); return true;
    }
    internal void ContinueGateDrag(Point position)
    {
        if (_owner != InteractionOwner.Gate || _displayed is not { } displayed || _gateCapture is not { } token ||
            _gateHandle is not { } identity || !IsCompatibleCaptureContext(displayed, token))
            return;
        if (identity.Kind == GateHandleKind.Window)
        {
            Rect plot = PlotBounds(); (double minimum, double maximum) = VisibleRange(displayed);
            if (plot.Width > 0 && LinearAxisTransform.TryCoordinateToData(Math.Clamp(position.X, plot.Left, plot.Right),
                plot.Left, plot.Width, minimum, maximum, out double pointerTime))
                EmitGateIntent(GateOverlayInteractionKind.MoveCapture, null,
                    _windowInitialStart + pointerTime - _windowPointerStartTime, token);
            return;
        }
        GateHandleVisualState? handle = displayed.Gates.Handles.FirstOrDefault(x => x.Identity == identity);
        if (handle is null) return;
        double value;
        if (handle.Kind == GateHandleKind.Threshold)
        {
            GateVisualTransform transform = displayed.Transform;
            if (!LinearAxisTransform.TryCoordinateToData(position.Y, transform.AmplitudeOrigin, transform.AmplitudeLength,
                transform.AmplitudePresentation == GateAmplitudePresentationKind.RawRf ? -100 : 0, 100, out double percent)) return;
            value = transform.AmplitudePresentation == GateAmplitudePresentationKind.RawRf
                ? (percent + 100d) / 200d : percent / 100d;
        }
        else
        {
            ScanTimeViewport viewport = displayed.Transform.TimeViewport;
            double exactCoordinate = position.X + handle.DragCoordinateOffset;
            if (!LinearAxisTransform.TryCoordinateToData(exactCoordinate, displayed.Transform.TimeOrigin, displayed.Transform.TimeLength,
                viewport.VisibleMinimumSeconds, viewport.VisibleMaximumSeconds, out value)) return;
        }
        EmitGateIntent(GateOverlayInteractionKind.MoveCapture, handle, value, token);
    }

    internal bool BeginGateWindowDrag(Point position)
    {
        if (_owner != InteractionOwner.None || _displayed is not { } displayed ||
            !IsTransformCompatible(displayed.Transform) || HitTestGateWindow(position) is not { } gate ||
            !displayed.HasAuthoritativeIdentity) return false;
        Rect plot = PlotBounds(); (double minimum, double maximum) = VisibleRange(displayed);
        if (!LinearAxisTransform.TryCoordinateToData(position.X, plot.Left, plot.Width, minimum, maximum,
            out double pointerTime)) return false;
        AScanCompositeVisualSnapshot? receivedBefore = _received;
        var request = new GateBeginWindowCaptureRequestedEventArgs(displayed, gate.GateId);
        GateBeginWindowCaptureRequested?.Invoke(this, request);
        if (!request.IsAccepted || request.Capture is not { } accepted) return false;
        bool receivedIsCompatible = _received is null || ReferenceEquals(_received, receivedBefore) ||
            IsCompatibleCaptureContext(_received, accepted);
        if (!receivedIsCompatible || _displayed is null || !IsCompatibleCaptureContext(displayed, accepted) ||
            !IsLoaded || !CaptureMouse())
        {
            GateInteractionRequested?.Invoke(this, new(GateOverlayInteractionKind.CancelPreview, request.Snapshot,
                null, accepted, null));
            return false;
        }
        _owner = InteractionOwner.Gate; _gateCapture = accepted;
        _gateHandle = new(gate.GateId, gate.GateRevision, GateHandleKind.Window);
        _windowPointerStartTime = pointerTime; _windowInitialStart = request.InitialStartOffsetSeconds;
        StartCursorInputTracking(); Focus(); return true;
    }

    internal ProjectedGateGeometryItem? HitTestGateWindow(Point position)
    {
        if (_displayed is not { } displayed || !IsTransformCompatible(displayed.Transform) ||
            !PlotBounds().Contains(position)) return null;
        return displayed.Gates.Gates.Where(gate => gate.StartCoordinate is { } start && gate.EndCoordinate is { } end &&
                position.X >= Math.Min(start, end) && position.X <= Math.Max(start, end))
            .OrderByDescending(gate => displayed.Gates.SelectedGate == gate.GateId)
            .ThenBy(gate => Math.Abs(gate.EndCoordinate!.Value - gate.StartCoordinate!.Value))
            .ThenBy(gate => gate.GateId.Value, StringComparer.Ordinal).FirstOrDefault();
    }

    private void EndGateCapture()
    {
        GateCaptureGeneration? token = _gateCapture; GateHandleVisualState? handle = CurrentGateHandle();
        CancelInteraction(false); if (token is { } capture) EmitGateIntent(GateOverlayInteractionKind.EndCapture, handle, null, capture);
    }
    internal GateHandleVisualState? HitTestGate(Point position)
    {
        if (_displayed is not { } displayed || !IsTransformCompatible(displayed.Transform) ||
            !displayed.HasAuthoritativeIdentity ||
            !PlotBounds().Contains(position)) return null;
        GateHandleVisualState? timeBoundary = displayed.Gates.Handles
            .Where(handle => handle.IsEnabled && handle.Kind is GateHandleKind.Start or GateHandleKind.End)
            .Select(handle => (Handle: handle, Distance: GateBoundaryDistance(displayed.Gates, handle, position.X)))
            .Where(candidate => candidate.Distance <= GateBoundaryHitToleranceDip)
            .OrderBy(candidate => candidate.Distance).ThenBy(candidate => candidate.Handle.GateId.Value)
            .ThenBy(candidate => candidate.Handle.Kind).Select(candidate => candidate.Handle).FirstOrDefault();
        GateHandleVisualState? thresholdLine = displayed.Gates.Handles
            .Where(handle => handle.IsEnabled && handle.Kind == GateHandleKind.Threshold)
            .Select(handle => (Handle: handle,
                Distance: GateThresholdDistance(displayed.Gates, handle, position.X, position.Y)))
            .Where(candidate => candidate.Distance <= GateHitToleranceDip)
            .OrderBy(candidate => candidate.Distance).ThenBy(candidate => candidate.Handle.GateId.Value)
            .Select(candidate => candidate.Handle).FirstOrDefault();
        return timeBoundary ?? thresholdLine ??
            GateOverlayHitTester.HitTest(displayed.Gates, position.X, position.Y, GateHitToleranceDip);
    }

    private static double GateBoundaryDistance(GateOverlayVisualSnapshot overlay,
        GateHandleVisualState handle, double pointerX)
    {
        double distance = Math.Abs(handle.X - pointerX);
        ProjectedGateGeometryItem? gate = overlay.Gates.FirstOrDefault(candidate =>
            candidate.GateId == handle.GateId && candidate.GateRevision == handle.GateRevision);
        double? exact = handle.Kind == GateHandleKind.Start ? gate?.StartCoordinate : gate?.EndCoordinate;
        return exact is { } coordinate ? Math.Min(distance, Math.Abs(coordinate - pointerX)) : distance;
    }

    private static double GateThresholdDistance(GateOverlayVisualSnapshot overlay,
        GateHandleVisualState handle, double pointerX, double pointerY)
    {
        ProjectedGateGeometryItem? gate = overlay.Gates.FirstOrDefault(candidate =>
            candidate.GateId == handle.GateId && candidate.GateRevision == handle.GateRevision);
        if (gate?.StartCoordinate is not { } start || gate.EndCoordinate is not { } end ||
            pointerX < Math.Min(start, end) || pointerX > Math.Max(start, end)) return double.PositiveInfinity;
        double projected = gate.Thresholds.Count == 0 ? Math.Abs(handle.Y - pointerY) :
            gate.Thresholds.Min(threshold => Math.Abs(threshold.Coordinate - pointerY));
        return Math.Min(projected, Math.Abs(handle.Y - pointerY));
    }

    internal bool BeginCursorDrag(Point position) => BeginCursorDrag(position, allowNearest: false);
    private bool BeginCursorDrag(Point position, bool allowNearest)
    {
        if (_owner != InteractionOwner.None ||
            (HitTestCursor(position) ?? (allowNearest ? NearestVisibleCursor(position) : null)) is not { } hit ||
            !CaptureMouse()) return false;
        _owner = InteractionOwner.Cursor; _draggingCursor = hit; _lastRequestedCursorTime = null;
        StartCursorInputTracking();
        CursorActivated?.Invoke(this, new(hit)); ContinueCursorDrag(position); return true;
    }
    private void StartCursorInputTracking()
    {
        if (_cursorInputTracking) return;
        InputManager.Current.PreProcessInput += OnPreProcessInput;
        CompositionTarget.Rendering += OnCursorRendering;
        _cursorInputTracking = true;
    }
    private void StopCursorInputTracking()
    {
        if (!_cursorInputTracking) return;
        InputManager.Current.PreProcessInput -= OnPreProcessInput;
        CompositionTarget.Rendering -= OnCursorRendering;
        _cursorInputTracking = false;
    }
    private void OnPreProcessInput(object sender, PreProcessInputEventArgs args)
    {
        if (_owner is not InteractionOwner.Cursor and not InteractionOwner.Gate)
        { StopCursorInputTracking(); return; }
        if (!IsMouseCaptured && args.StagingItem.Input is MouseButtonEventArgs button &&
            button.ButtonState == MouseButtonState.Released &&
            (_owner == InteractionOwner.Cursor && button.ChangedButton == MouseButton.Left ||
             _owner == InteractionOwner.Gate && _gateHandle?.Kind == GateHandleKind.Window && button.ChangedButton == MouseButton.Right ||
             _owner == InteractionOwner.Gate && _gateHandle?.Kind != GateHandleKind.Window && button.ChangedButton == MouseButton.Left))
        {
            ContinuePhysicalGestureFromInput();
            if (_owner == InteractionOwner.Gate) EndGateCapture();
            else CancelInteraction(false);
        }
        else if (args.StagingItem.Input is MouseEventArgs mouse && mouse.RoutedEvent == Mouse.MouseMoveEvent)
            ContinuePhysicalGestureFromInput();
    }
    private void OnCursorRendering(object? sender, EventArgs args) => ContinuePhysicalGestureFromInput();
    private void ContinuePhysicalGestureFromInput()
    {
        if (_owner == InteractionOwner.Cursor) ContinueCursorDrag(_readCurrentPosition());
        else if (_owner == InteractionOwner.Gate) ContinueGateDrag(_readCurrentPosition());
    }
    internal void ContinueCursorDrag(Point position)
    {
        if (_owner != InteractionOwner.Cursor || _draggingCursor is not { } cursor || _displayed is not { } displayed ||
            !IsTransformCompatible(displayed.Transform)) return;
        Rect plot = PlotBounds(); (double minimum, double maximum) = VisibleRange(displayed);
        if (plot.Width > 0 && LinearAxisTransform.TryCoordinateToData(Math.Clamp(position.X, plot.Left, plot.Right), plot.Left,
            plot.Width, minimum, maximum, out double time) &&
            (_lastRequestedCursorTime is not { } previous || Math.Abs(previous - time) > 1e-15))
        {
            _lastRequestedCursorTime = time;
            CursorMoveRequested?.Invoke(this, new(cursor, time));
        }
    }
    internal void EndCursorDrag() => CancelInteraction(false);
    internal void CancelCursorDrag() => CancelInteraction(false);
    internal AScanCursorId? DraggingCursor => _draggingCursor;
    internal AScanCursorId? HitTestCursor(Point position)
    {
        if (_displayed is not { Cursors: { IsVisible: true } cursors } displayed ||
            !IsTransformCompatible(displayed.Transform)) return null;
        Rect plot = PlotBounds(); if (!plot.Contains(position) || plot.Width <= 0) return null;
        (double minimum, double maximum) = VisibleRange(displayed); double range = maximum - minimum; if (range <= 0) return null;
        double ad = Distance(cursors.A.TimeSeconds), bd = Distance(cursors.B.TimeSeconds);
        if (ad > CursorHitToleranceDip && bd > CursorHitToleranceDip) return null;
        return ad == bd ? cursors.ActiveCursor : ad < bd ? AScanCursorId.A : AScanCursorId.B;
        double Distance(double time) => time < minimum || time > maximum ? double.PositiveInfinity :
            Math.Abs(position.X - (plot.Left + (time - minimum) / range * plot.Width));
    }
    private AScanCursorId? NearestVisibleCursor(Point position)
    {
        if (_displayed is not { Cursors: { IsVisible: true } cursors } displayed ||
            !IsTransformCompatible(displayed.Transform) || !PlotBounds().Contains(position)) return null;
        (double minimum, double maximum) = VisibleRange(displayed); double range = maximum - minimum;
        if (range <= 0) return null;
        double a = Coordinate(cursors.A.TimeSeconds), b = Coordinate(cursors.B.TimeSeconds);
        return Math.Abs(position.X - a) <= Math.Abs(position.X - b) ? AScanCursorId.A : AScanCursorId.B;
        double Coordinate(double time) => PlotBounds().Left + (time - minimum) / range * PlotBounds().Width;
    }
    internal bool BeginPan(Point position)
    {
        if (_owner != InteractionOwner.None || !PlotBounds().Contains(position) || _displayed is not { } displayed ||
            !IsTransformCompatible(displayed.Transform) || !CaptureMouse()) return false;
        _owner = InteractionOwner.Pan; _lastPanX = position.X; return true;
    }
    internal void ContinuePan(Point position)
    {
        if (_owner != InteractionOwner.Pan || _displayed is not { } displayed || !IsTransformCompatible(displayed.Transform)) return;
        Rect plot = PlotBounds(); if (plot.Width <= 0) return;
        double delta = -((position.X - _lastPanX) / plot.Width) * displayed.Transform.TimeViewport.VisibleSpanSeconds;
        _lastPanX = position.X; TimePanRequested?.Invoke(this, new(delta));
    }
    internal void EndInteraction() => CancelInteraction(false);
    internal bool RequestTimeZoom(Point position, int wheelDelta)
    {
        if (_displayed is not { } displayed || !IsTransformCompatible(displayed.Transform) ||
            !PlotBounds().Contains(position) || wheelDelta == 0) return false;
        Rect plot = PlotBounds(); (double minimum, double maximum) = VisibleRange(displayed);
        if (!LinearAxisTransform.TryCoordinateToData(Math.Clamp(position.X, plot.Left, plot.Right), plot.Left, plot.Width,
            minimum, maximum, out double anchor)) return false;
        TimeZoomRequested?.Invoke(this, new(anchor, Math.Pow(1.2, wheelDelta / 120d))); return true;
    }
    internal bool RequestTimeReset(Point position)
    {
        if (_displayed is not { } displayed || !IsTransformCompatible(displayed.Transform) ||
            !PlotBounds().Contains(position) || HitTestGate(position) is not null || HitTestCursor(position) is not null) return false;
        TimeResetRequested?.Invoke(this, EventArgs.Empty); return true;
    }

    private void CancelInteraction(bool notifyGate, bool releaseMouse = true)
    {
        GateCaptureGeneration? token = _gateCapture; GateHandleVisualState? handle = CurrentGateHandle();
        _owner = InteractionOwner.None; _draggingCursor = null; _lastRequestedCursorTime = null;
        _gateCapture = null; _gateHandle = null;
        StopCursorInputTracking();
        if (releaseMouse && IsMouseCaptured) ReleaseMouseCapture();
        if (notifyGate && token is { } capture) EmitGateIntent(GateOverlayInteractionKind.CancelPreview, handle, null, capture);
    }

    private GateHandleVisualState? CurrentGateHandle() => _displayed?.Gates.Handles
        .FirstOrDefault(x => _gateHandle is { } identity && x.Identity == identity);
    private static bool IsAcceptedToken(GateCaptureGeneration? token, AScanCompositeVisualSnapshot snapshot, GateHandleVisualState handle)
    {
        if (token is not { IsValid: true } value || snapshot.Gates.Source is not { } source ||
            value.RunId != snapshot.Signal.Metadata.RunId ||
            snapshot.Signal.TraceIdentity is not { } shownTrace || value.TraceIdentity.Stream != shownTrace.Stream ||
            value.SourceKind != source.Kind ||
            value.PlanRevision != source.Revision || value.Preview != source.Preview || value.GateId != handle.GateId ||
            value.Handle != handle.Kind || value.TransformVersion != snapshot.Transform.Version) return false;
        return value.AnchorGateId == handle.AnchorGateId && value.AnchorRevision == handle.AnchorRevision;
    }
    private static bool IsCompatibleCaptureContext(AScanCompositeVisualSnapshot? snapshot, GateCaptureGeneration token)
    {
        if (snapshot is null || snapshot.Transform.Version != token.TransformVersion ||
            snapshot.Signal.Metadata.RunId != token.RunId ||
            snapshot.Signal.TraceIdentity is not { } trace || trace.Stream != token.TraceIdentity.Stream)
            return false;
        // An accepted gesture owns one stable capture token while each MoveCapture legitimately produces a new
        // Preview source identity. When the portable overlay projects that exact token, it is the authority for
        // continuity; source identity checks are only the fallback for lagging live frames without a projection.
        if (snapshot.Gates.Capture is { } projected) return projected == token;
        return snapshot.Gates.Source is { } source && source.Kind == token.SourceKind &&
            source.Revision == token.PlanRevision && source.Preview == token.Preview;
    }
    private void EmitGateIntent(GateOverlayInteractionKind kind, GateHandleVisualState? handle, double? value,
        GateCaptureGeneration? capture = null)
    {
        if (_displayed is { } displayed) GateInteractionRequested?.Invoke(this,
            new(kind, displayed, handle?.Identity, capture, value));
    }
    private void RequestVisualContext()
    {
        if (!IsLoaded || ActualWidth <= 0 || ActualHeight <= 0) return;
        DpiScale dpi = VisualTreeHelper.GetDpi(this);
        VisualContextChanged?.Invoke(this, new(ActualWidth, ActualHeight, dpi.DpiScaleX, dpi.DpiScaleY,
            _transformInvalidation, _displayed));
    }
    private bool IsTransformCompatible(GateVisualTransform transform)
    {
        DpiScale dpi = VisualTreeHelper.GetDpi(this);
        return NearlyEqual(transform.WidthDip, ActualWidth) && NearlyEqual(transform.HeightDip, ActualHeight) &&
            NearlyEqual(transform.DpiScaleX, dpi.DpiScaleX) && NearlyEqual(transform.DpiScaleY, dpi.DpiScaleY);
    }
    private static bool NearlyEqual(double left, double right) => Math.Abs(left - right) <= 1e-6 * Math.Max(1, Math.Max(Math.Abs(left), Math.Abs(right)));
    private Rect PlotBounds() => new(64, 16, Math.Max(0, ActualWidth - 84), Math.Max(0, ActualHeight - 54));
    private static (double Minimum, double Maximum) VisibleRange(AScanCompositeVisualSnapshot? snapshot) => snapshot is null
        ? (0, 2047d / 50_000_000) : (Math.Max(snapshot.Signal.MinimumTimeSeconds, snapshot.Transform.TimeViewport.VisibleMinimumSeconds),
            Math.Min(snapshot.Signal.MaximumTimeSeconds, snapshot.Transform.TimeViewport.VisibleMaximumSeconds));
    private static GateHandleVisualState[] OrderedHandles(AScanCompositeVisualSnapshot displayed) => displayed.Gates.Handles
        .Where(x => x.IsEnabled).OrderBy(x => x.GateId.Value, StringComparer.Ordinal).ThenBy(x => x.Kind).ToArray();
    private bool TryAdmitSnapshot()
    {
        long now = _clock.GetTimestamp();
        if (_lastRefresh is { } last && _clock.GetElapsedTime(last, now) < AScanVisualDelivery.MinimumPublicationInterval) return false;
        _lastRefresh = now; return true;
    }
    private void TryAdmitReceived()
    {
        if (_received is not { } candidate || !IsTransformCompatible(candidate.Transform)) return;
        if (ShouldRetainCompleteGateSnapshot(_displayed, candidate))
        {
            _dirty = false; _retainingCompleteGateSnapshot = true; UpdateAutomationPeers(_displayed);
            return;
        }
        if (!TryAdmitSnapshot()) return;
        _dirty = false; _displayed = candidate; _retainingCompleteGateSnapshot = false; UpdateAutomationPeers(candidate);
    }
    private static bool ShouldRetainCompleteGateSnapshot(AScanCompositeVisualSnapshot? displayed,
        AScanCompositeVisualSnapshot candidate)
    {
        if (displayed?.Gates is not { } previous || previous.Gates.Count == 0 ||
            candidate.Gates.Gates.Any(gate => gate.StartCoordinate is not null || gate.EndCoordinate is not null ||
                gate.Thresholds.Count != 0 || gate.DetectedEvent is not null) ||
            candidate.Gates.Status is not (GateOverlayGeometryStatus.Evaluating or
                GateOverlayGeometryStatus.Stale)) return false;
        return true;
    }
    private static double LabelOriginX(Rect plot, double desired, double labelWidth) => plot.Width <= labelWidth ? plot.Left : Math.Clamp(desired, plot.Left, plot.Right - labelWidth);
    private void Label(DrawingContext drawing, string text, Point origin) => drawing.DrawText(
        new(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), 12,
            LabelBrush, VisualTreeHelper.GetDpi(this).PixelsPerDip), origin);
    private static Brush FrozenBrush(byte red, byte green, byte blue)
    { var brush = new SolidColorBrush(Color.FromRgb(red, green, blue)); brush.Freeze(); return brush; }
    private static Pen FrozenPen(Brush brush, double width)
    { var pen = new Pen(brush, width); pen.Freeze(); return pen; }
    protected override AutomationPeer OnCreateAutomationPeer()
    {
        if (_automationPeer is null) { _automationPeer = new(this); _automationPeer.Update(_displayed); }
        return _automationPeer;
    }
    private void UpdateAutomationPeers(AScanCompositeVisualSnapshot? snapshot) => _automationPeer?.Update(snapshot);
    internal T OnDispatcher<T>(Func<T> action) => Dispatcher.CheckAccess() ? action() : Dispatcher.Invoke(action);
    internal void OnDispatcher(Action action) { if (Dispatcher.CheckAccess()) action(); else Dispatcher.Invoke(action); }
    internal GateHandleVisualState? FindCurrentHandle(GateHandleAutomationIdentity identity) => _displayed is { } displayed &&
        IsTransformCompatible(displayed.Transform) ? displayed.Gates.Handles.FirstOrDefault(handle => identity.Matches(displayed, handle)) : null;
    internal void RequestAutomationSelect(GateHandleAutomationIdentity identity)
    { if (FindCurrentHandle(identity) is { } handle) { Focus(); EmitGateIntent(GateOverlayInteractionKind.Select, handle, null); } }
    internal void RequestAutomationValue(GateHandleAutomationIdentity identity, double value)
    {
        if (FindCurrentHandle(identity) is not { IsEnabled: true } handle) throw new ElementNotAvailableException();
        EmitGateIntent(value >= handle.Value ? GateOverlayInteractionKind.Increment : GateOverlayInteractionKind.Decrement,
            handle, Math.Clamp(value, handle.Minimum, handle.Maximum));
    }
    private enum InteractionOwner { None, Cursor, Pan, Gate }
    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint { internal int X; internal int Y; }
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out NativePoint point);
}

public sealed class AScanCursorMoveRequestedEventArgs(AScanCursorId cursor, double timeSeconds) : EventArgs
{ public AScanCursorId Cursor { get; } = cursor; public double TimeSeconds { get; } = timeSeconds; }
public sealed class AScanCursorActivatedEventArgs(AScanCursorId cursor) : EventArgs
{ public AScanCursorId Cursor { get; } = cursor; }
public sealed class ScanTimeZoomRequestedEventArgs(double anchorSeconds, double factor) : EventArgs
{ public double AnchorSeconds { get; } = anchorSeconds; public double Factor { get; } = factor; }
public sealed class ScanTimePanRequestedEventArgs(double deltaSeconds) : EventArgs
{ public double DeltaSeconds { get; } = deltaSeconds; }
public sealed class GateBeginCaptureRequestedEventArgs(AScanCompositeVisualSnapshot snapshot, GateHandleVisualState handle) : EventArgs
{
    public AScanCompositeVisualSnapshot Snapshot { get; } = snapshot; public GateHandleVisualState Handle { get; } = handle;
    public GateCaptureGeneration? Capture { get; private set; }
    public bool IsAccepted => Capture is { IsValid: true };
    public void Accept(GateCaptureGeneration capture)
    {
        if (!capture.IsValid || capture.RunId != Snapshot.Signal.Metadata.RunId ||
            Snapshot.Signal.TraceIdentity is not { } shownTrace || capture.TraceIdentity.Stream != shownTrace.Stream ||
            capture.GateId != Handle.GateId || capture.Handle != Handle.Kind || capture.TransformVersion != Snapshot.Transform.Version)
            throw new ArgumentException("The accepted capture does not belong to the displayed handle.", nameof(capture));
        Capture = capture;
    }
}
public sealed class GateBeginWindowCaptureRequestedEventArgs(AScanCompositeVisualSnapshot snapshot, GateId gateId) : EventArgs
{
    public AScanCompositeVisualSnapshot Snapshot { get; } = snapshot;
    public GateId GateId { get; } = gateId;
    public GateCaptureGeneration? Capture { get; private set; }
    public double InitialStartOffsetSeconds { get; private set; }
    public bool IsAccepted => Capture is { IsValid: true, Handle: GateHandleKind.Window };
    public void Accept(GateCaptureGeneration capture, double initialStartOffsetSeconds)
    {
        if (!capture.IsValid || capture.Handle != GateHandleKind.Window || capture.GateId != GateId ||
            capture.RunId != Snapshot.Signal.Metadata.RunId || Snapshot.Signal.TraceIdentity is not { } shownTrace ||
            capture.TraceIdentity.Stream != shownTrace.Stream ||
            capture.TransformVersion != Snapshot.Transform.Version || !double.IsFinite(initialStartOffsetSeconds))
            throw new ArgumentException("The accepted window capture does not belong to the displayed gate.", nameof(capture));
        Capture = capture; InitialStartOffsetSeconds = initialStartOffsetSeconds;
    }
}
public sealed class GateInteractionRequestedEventArgs(GateOverlayInteractionKind kind, AScanCompositeVisualSnapshot snapshot,
    GateHandleLogicalIdentity? handle, GateCaptureGeneration? capture, double? value) : EventArgs
{
    public GateOverlayInteractionKind Kind { get; } = kind; public AScanCompositeVisualSnapshot Snapshot { get; } = snapshot;
    public GateHandleLogicalIdentity? Handle { get; } = handle; public GateCaptureGeneration? Capture { get; } = capture;
    public double? Value { get; } = value;
}
public sealed class AScanVisualContextChangedEventArgs(double widthDip, double heightDip, double dpiScaleX, double dpiScaleY,
    ulong invalidation, AScanCompositeVisualSnapshot? previous) : EventArgs
{
    public double WidthDip { get; } = widthDip; public double HeightDip { get; } = heightDip;
    public double DpiScaleX { get; } = dpiScaleX; public double DpiScaleY { get; } = dpiScaleY;
    public ulong Invalidation { get; } = invalidation; public AScanCompositeVisualSnapshot? Previous { get; } = previous;
}
