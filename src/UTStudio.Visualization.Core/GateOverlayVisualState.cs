using System.Collections.ObjectModel;
using UTStudio.Contracts.Inspection;
using UTStudio.Contracts.Processing;
using UTStudio.Domain.Acquisition;
using UTStudio.Domain.Processing;

namespace UTStudio.Visualization.Core;

public enum GateHandleKind { Unknown, Start, End, Threshold }
public enum GateOverlayGeometryStatus { Unknown, NotRequested, Evaluating, Current, Stale, Unavailable, Closed, Faulted }
public enum PortablePointerCaptureOwner { None, Cursor, Gate }
public enum GateOverlayInteractionKind
{
    Select, FocusPrevious, FocusNext, BeginCapture, MoveCapture, EndCapture,
    Decrement, Increment, ConfirmPreview, CancelPreview
}

public readonly record struct GateHandleLogicalIdentity(
    GateId GateId, GateRevision GateRevision, GateHandleKind Kind)
{
    public bool IsValid => GateId.IsValid && GateRevision.IsValid && Kind != GateHandleKind.Unknown;
}

public readonly record struct GateVisualTransform
{
    public GateVisualTransform(ulong version, ScanTimeViewport timeViewport,
        GateAmplitudePresentationKind amplitudePresentation, double timeOrigin, double timeLength,
        double amplitudeOrigin, double amplitudeLength, double widthDip, double heightDip,
        double dpiScaleX, double dpiScaleY)
    {
        ArgumentNullException.ThrowIfNull(timeViewport);
        if (version == 0 || !Enum.IsDefined(amplitudePresentation) ||
            amplitudePresentation == GateAmplitudePresentationKind.Unknown ||
            !AllFinite(timeOrigin, timeLength, amplitudeOrigin, amplitudeLength, widthDip, heightDip,
                dpiScaleX, dpiScaleY) || timeLength <= 0 || amplitudeLength == 0 || widthDip <= 0 ||
            heightDip <= 0 || dpiScaleX <= 0 || dpiScaleY <= 0)
            throw new ArgumentException("A valid versioned visual transform is required.");
        Version = version; TimeViewport = timeViewport; AmplitudePresentation = amplitudePresentation;
        TimeOrigin = timeOrigin; TimeLength = timeLength; AmplitudeOrigin = amplitudeOrigin;
        AmplitudeLength = amplitudeLength; WidthDip = widthDip; HeightDip = heightDip;
        DpiScaleX = dpiScaleX; DpiScaleY = dpiScaleY;
    }
    public ulong Version { get; }
    public ScanTimeViewport TimeViewport { get; }
    public GateAmplitudePresentationKind AmplitudePresentation { get; }
    public double TimeOrigin { get; }
    public double TimeLength { get; }
    public double AmplitudeOrigin { get; }
    public double AmplitudeLength { get; }
    public double WidthDip { get; }
    public double HeightDip { get; }
    public double DpiScaleX { get; }
    public double DpiScaleY { get; }
    public GateGeometryProjectionContext ProjectionContext => new(TimeViewport, TimeOrigin, TimeLength,
        AmplitudePresentation, AmplitudeOrigin, AmplitudeLength);
    private static bool AllFinite(params double[] values) => values.All(double.IsFinite);
}

public readonly record struct GateCaptureGeneration(
    ulong ContextGeneration, ulong Nonce, AcquisitionRunId RunId, TraceIdentity TraceIdentity,
    GateGeometrySourceKind SourceKind, GatePlanRevision PlanRevision, GatePreviewIdentity Preview,
    GateId GateId, GateHandleKind Handle, GateId AnchorGateId, GateRevision AnchorRevision,
    ulong TransformVersion)
{
    public bool IsValid => ContextGeneration != 0 && Nonce != 0 && RunId.IsValid && TraceIdentity.IsValid &&
        SourceKind != GateGeometrySourceKind.Unknown && PlanRevision.IsValid && GateId.IsValid &&
        Handle != GateHandleKind.Unknown && TransformVersion != 0 &&
        (SourceKind != GateGeometrySourceKind.Preview || Preview.IsValid);
}

public sealed class GateHandleVisualState
{
    public GateHandleVisualState(GateId gateId, GateRevision gateRevision, GateHandleKind kind,
        double x, double y, double value, string unit, double minimum, double maximum, double step,
        bool isSelected, bool isFocused, bool isEnabled,
        GateId anchorGateId = default, GateRevision anchorRevision = default)
    {
        if (!gateId.IsValid || !gateRevision.IsValid || kind == GateHandleKind.Unknown ||
            !double.IsFinite(x) || !double.IsFinite(y) || !double.IsFinite(value) || string.IsNullOrWhiteSpace(unit) ||
            !double.IsFinite(minimum) || !double.IsFinite(maximum) || maximum < minimum ||
            !double.IsFinite(step) || step <= 0)
            throw new ArgumentException("A valid immutable gate handle is required.");
        GateId = gateId; GateRevision = gateRevision; Kind = kind; X = x; Y = y; Value = value;
        Unit = unit; Minimum = minimum; Maximum = maximum; Step = step; IsSelected = isSelected;
        IsFocused = isFocused; IsEnabled = isEnabled;
        AnchorGateId = anchorGateId; AnchorRevision = anchorRevision;
    }
    public GateId GateId { get; }
    public GateRevision GateRevision { get; }
    public GateHandleKind Kind { get; }
    public double X { get; }
    public double Y { get; }
    public double Value { get; }
    public string Unit { get; }
    public double Minimum { get; }
    public double Maximum { get; }
    public double Step { get; }
    public bool IsSelected { get; }
    public bool IsFocused { get; }
    public bool IsEnabled { get; }
    public GateId AnchorGateId { get; }
    public GateRevision AnchorRevision { get; }
    public GateHandleLogicalIdentity Identity => new(GateId, GateRevision, Kind);
}

public sealed class GateOverlayVisualSnapshot
{
    private readonly ReadOnlyCollection<ProjectedGateGeometryItem> _gates;
    private readonly ReadOnlyCollection<GateHandleVisualState> _handles;
    public GateOverlayVisualSnapshot(GateOverlayGeometryStatus status, GateGeometrySourceIdentity? source,
        TraceIdentity? traceIdentity, InspectionRevision inspectionRevision,
        IEnumerable<ProjectedGateGeometryItem> gates, IEnumerable<GateHandleVisualState> handles,
        GateId? selectedGate, GateHandleKind focusedHandle, ulong contextGeneration,
        ulong evaluationSequence, GateCaptureGeneration? capture, string? errorCode = null)
    {
        ArgumentNullException.ThrowIfNull(gates); ArgumentNullException.ThrowIfNull(handles);
        if (!Enum.IsDefined(status) || status == GateOverlayGeometryStatus.Unknown || contextGeneration == 0 ||
            focusedHandle == GateHandleKind.Unknown && selectedGate is null && handles.Any(x => x.IsFocused) ||
            status == GateOverlayGeometryStatus.Current && (source is null || traceIdentity is not { IsValid: true } ||
                inspectionRevision == InspectionRevision.Initial) ||
            status == GateOverlayGeometryStatus.Faulted != !string.IsNullOrWhiteSpace(errorCode))
            throw new ArgumentException("The gate overlay snapshot is inconsistent.");
        Status = status; Source = source; TraceIdentity = traceIdentity; InspectionRevision = inspectionRevision;
        _gates = Array.AsReadOnly(gates.ToArray()); _handles = Array.AsReadOnly(handles.ToArray());
        SelectedGate = selectedGate; FocusedHandle = focusedHandle; ContextGeneration = contextGeneration;
        EvaluationSequence = evaluationSequence; Capture = capture; ErrorCode = errorCode;
    }
    public GateOverlayGeometryStatus Status { get; }
    public GateGeometrySourceIdentity? Source { get; }
    public TraceIdentity? TraceIdentity { get; }
    public InspectionRevision InspectionRevision { get; }
    public IReadOnlyList<ProjectedGateGeometryItem> Gates => _gates;
    public IReadOnlyList<GateHandleVisualState> Handles => _handles;
    public GateId? SelectedGate { get; }
    public GateHandleKind FocusedHandle { get; }
    public ulong ContextGeneration { get; }
    public ulong EvaluationSequence { get; }
    public GateCaptureGeneration? Capture { get; }
    public string? ErrorCode { get; }
}

public sealed class AScanCompositeVisualSnapshot
{
    public AScanCompositeVisualSnapshot(AScanSnapshot signal, AScanCursorState? cursors,
        GateOverlayVisualSnapshot gates, GateVisualTransform transform)
    {
        ArgumentNullException.ThrowIfNull(signal); ArgumentNullException.ThrowIfNull(gates);
        bool hasAuthority = signal.TraceIdentity is { IsValid: true } &&
            signal.InspectionRevision != InspectionRevision.Initial;
        if (cursors is not null && (cursors.RunId != signal.Metadata.RunId || cursors.SnapshotVersion != signal.Version) ||
            !hasAuthority && (gates.Status is GateOverlayGeometryStatus.Current or GateOverlayGeometryStatus.Evaluating ||
                gates.Source is not null || gates.Gates.Count != 0 || gates.Handles.Count != 0 || gates.Capture is not null) ||
            hasAuthority && gates.Status is GateOverlayGeometryStatus.Current or GateOverlayGeometryStatus.Evaluating &&
                (gates.TraceIdentity != signal.TraceIdentity || gates.InspectionRevision != signal.InspectionRevision) ||
            gates.Status is not (GateOverlayGeometryStatus.Current or GateOverlayGeometryStatus.Evaluating) &&
                gates.Gates.Count != 0)
            throw new ArgumentException("Signal, cursors, gates, and transform must describe one coherent visual state.");
        Signal = signal; Cursors = cursors; Gates = gates; Transform = transform;
    }
    public AScanSnapshot Signal { get; }
    public AScanCursorState? Cursors { get; }
    public GateOverlayVisualSnapshot Gates { get; }
    public GateVisualTransform Transform { get; }
    public bool HasAuthoritativeIdentity => Signal.TraceIdentity is { IsValid: true } &&
        Signal.InspectionRevision != InspectionRevision.Initial;
}

public static class GateOverlayHitTester
{
    public static GateHandleVisualState? HitTest(GateOverlayVisualSnapshot snapshot, double x, double y,
        double toleranceDip)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (!double.IsFinite(x) || !double.IsFinite(y) || !double.IsFinite(toleranceDip) || toleranceDip < 0)
            throw new ArgumentOutOfRangeException(nameof(x));
        double squared = toleranceDip * toleranceDip;
        return snapshot.Handles.Where(handle => handle.IsEnabled &&
                DistanceSquared(handle, x, y) <= squared)
            .OrderBy(handle => DistanceSquared(handle, x, y)).ThenBy(handle => handle.GateId.Value)
            .ThenBy(handle => handle.Kind).FirstOrDefault();
    }
    private static double DistanceSquared(GateHandleVisualState handle, double x, double y) =>
        (handle.X - x) * (handle.X - x) + (handle.Y - y) * (handle.Y - y);
}
