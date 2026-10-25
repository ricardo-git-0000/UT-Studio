using System.Collections.ObjectModel;
using UTStudio.Contracts.Processing;
using UTStudio.Domain.Processing;

namespace UTStudio.Visualization.Core;

public enum GateAmplitudePresentationKind { Unknown, RawRf, RectifiedMagnitude }
public enum GateGeometryProjectionStatus { Unknown, Projected, IncompatibleAmplitudeDomain }

public readonly record struct GateGeometryProjectionContext
{
    public GateGeometryProjectionContext(ScanTimeViewport timeViewport,
        double timeCoordinateOrigin, double timeCoordinateLength,
        GateAmplitudePresentationKind amplitudePresentation,
        double amplitudeCoordinateOrigin, double amplitudeCoordinateLength)
    {
        ArgumentNullException.ThrowIfNull(timeViewport);
        if (!double.IsFinite(timeCoordinateOrigin) || !double.IsFinite(timeCoordinateLength) || timeCoordinateLength == 0 ||
            !Enum.IsDefined(amplitudePresentation) || amplitudePresentation == GateAmplitudePresentationKind.Unknown ||
            !double.IsFinite(amplitudeCoordinateOrigin) || !double.IsFinite(amplitudeCoordinateLength) || amplitudeCoordinateLength == 0)
            throw new ArgumentException("A valid projection context is required.");
        TimeViewport = timeViewport; TimeCoordinateOrigin = timeCoordinateOrigin;
        TimeCoordinateLength = timeCoordinateLength; AmplitudePresentation = amplitudePresentation;
        AmplitudeCoordinateOrigin = amplitudeCoordinateOrigin; AmplitudeCoordinateLength = amplitudeCoordinateLength;
    }
    public ScanTimeViewport TimeViewport { get; }
    public double TimeCoordinateOrigin { get; }
    public double TimeCoordinateLength { get; }
    public GateAmplitudePresentationKind AmplitudePresentation { get; }
    public double AmplitudeCoordinateOrigin { get; }
    public double AmplitudeCoordinateLength { get; }
}

public sealed record GateThresholdProjection(double SignalCounts, double AmplitudePercent, double Coordinate);
public sealed record GateEventProjection(double TimeCoordinate, double AmplitudeCoordinate,
    double TimeSeconds, double DisplayedRfAmplitudeCounts, double DetectorAmplitudeCounts,
    GateDetectionEventKind EventKind);

public sealed class ProjectedGateGeometryItem
{
    private readonly ReadOnlyCollection<GateThresholdProjection> _thresholds;
    public ProjectedGateGeometryItem(GateId gateId, GateRevision gateRevision,
        GateGeometryProjectionStatus status, double? startCoordinate, double? endCoordinate,
        IEnumerable<GateThresholdProjection> thresholds, GateEventProjection? detectedEvent = null)
    {
        ArgumentNullException.ThrowIfNull(thresholds);
        GateId = gateId; GateRevision = gateRevision; Status = status;
        StartCoordinate = startCoordinate; EndCoordinate = endCoordinate;
        _thresholds = Array.AsReadOnly(thresholds.ToArray());
        DetectedEvent = detectedEvent;
    }
    public GateId GateId { get; }
    public GateRevision GateRevision { get; }
    public GateGeometryProjectionStatus Status { get; }
    public double? StartCoordinate { get; }
    public double? EndCoordinate { get; }
    public IReadOnlyList<GateThresholdProjection> Thresholds => _thresholds;
    public GateEventProjection? DetectedEvent { get; }
}

public static class GateGeometryProjector
{
    public static IReadOnlyList<ProjectedGateGeometryItem> Project(GateGeometrySnapshot snapshot,
        GateGeometryProjectionContext context)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return Array.AsReadOnly(snapshot.Gates.Select(gate => Project(gate, context)).ToArray());
    }

    private static ProjectedGateGeometryItem Project(GateGeometryItemSnapshot gate,
        GateGeometryProjectionContext context)
    {
        double? start = ProjectTime(gate.StartSeconds, context);
        double? end = ProjectTime(gate.EndSeconds, context);
        GateEventProjection? detectedEvent = ProjectEvent(gate.DetectedEvent, context);
        if (!gate.ThresholdNormalized.HasValue)
            return new(gate.GateId, gate.GateRevision, GateGeometryProjectionStatus.Projected, start, end, [], detectedEvent);
        double normalized = gate.ThresholdNormalized.Value;
        double percentage = context.AmplitudePresentation == GateAmplitudePresentationKind.RawRf
            ? -100d + 200d * normalized : 100d * normalized;
        double minimum = context.AmplitudePresentation == GateAmplitudePresentationKind.RawRf ? -100d : 0d;
        var projected = new List<GateThresholdProjection>(1);
        if (LinearAxisTransform.TryDataToCoordinate(percentage, minimum, 100d,
            context.AmplitudeCoordinateOrigin, context.AmplitudeCoordinateLength, out double coordinate))
            projected.Add(new(GateThresholdScale.ToSignalCounts(GateSignalDomain.RawSigned, normalized),
                percentage, coordinate));
        return new(gate.GateId, gate.GateRevision, GateGeometryProjectionStatus.Projected,
            start, end, projected, detectedEvent);
    }

    private static GateEventProjection? ProjectEvent(GateExactEventSnapshot? detected,
        GateGeometryProjectionContext context)
    {
        if (detected?.Status != GateResultStatus.Detected ||
            detected.DetectionTimeSeconds is not { } time ||
            detected.DisplayedRfAmplitudeCounts is not { } displayed ||
            detected.DetectorAmplitudeCounts is not { } detector ||
            ProjectTime(time, context) is not { } x) return null;
        double percent = context.AmplitudePresentation == GateAmplitudePresentationKind.RawRf
            ? 100d * displayed / 32768d : 100d * Math.Abs(displayed) / 32768d;
        double minimum = context.AmplitudePresentation == GateAmplitudePresentationKind.RawRf ? -100d : 0d;
        if (!LinearAxisTransform.TryDataToCoordinate(percent, minimum, 100d,
            context.AmplitudeCoordinateOrigin, context.AmplitudeCoordinateLength, out double y)) return null;
        return new(x, y, time, displayed, detector, detected.EventKind);
    }

    private static double? ProjectTime(double? seconds, GateGeometryProjectionContext context)
    {
        if (!seconds.HasValue) return null;
        return LinearAxisTransform.TryDataToCoordinate(seconds.Value,
            context.TimeViewport.VisibleMinimumSeconds, context.TimeViewport.VisibleMaximumSeconds,
            context.TimeCoordinateOrigin, context.TimeCoordinateLength, out double coordinate) ? coordinate : null;
    }
}
