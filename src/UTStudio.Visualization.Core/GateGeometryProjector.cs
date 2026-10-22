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

public sealed class ProjectedGateGeometryItem
{
    private readonly ReadOnlyCollection<GateThresholdProjection> _thresholds;
    public ProjectedGateGeometryItem(GateId gateId, GateRevision gateRevision,
        GateGeometryProjectionStatus status, double? startCoordinate, double? endCoordinate,
        IEnumerable<GateThresholdProjection> thresholds)
    {
        ArgumentNullException.ThrowIfNull(thresholds);
        GateId = gateId; GateRevision = gateRevision; Status = status;
        StartCoordinate = startCoordinate; EndCoordinate = endCoordinate;
        _thresholds = Array.AsReadOnly(thresholds.ToArray());
    }
    public GateId GateId { get; }
    public GateRevision GateRevision { get; }
    public GateGeometryProjectionStatus Status { get; }
    public double? StartCoordinate { get; }
    public double? EndCoordinate { get; }
    public IReadOnlyList<GateThresholdProjection> Thresholds => _thresholds;
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
        if (!gate.ThresholdNormalized.HasValue)
            return new(gate.GateId, gate.GateRevision, GateGeometryProjectionStatus.Projected, start, end, []);
        if (context.AmplitudePresentation == GateAmplitudePresentationKind.RectifiedMagnitude &&
            gate.SignalDomain == GateSignalDomain.RawSigned)
            return new(gate.GateId, gate.GateRevision,
                GateGeometryProjectionStatus.IncompatibleAmplitudeDomain, start, end, []);

        double counts = GateThresholdScale.ToSignalCounts(gate.SignalDomain, gate.ThresholdNormalized.Value);
        double[] percentages = context.AmplitudePresentation switch
        {
            GateAmplitudePresentationKind.RawRf => gate.SignalDomain switch
            {
                GateSignalDomain.RawSigned => [100d * counts / 32768d],
                GateSignalDomain.Positive => [100d * counts / 32768d],
                GateSignalDomain.NegativeMagnitude => [-100d * counts / 32768d],
                GateSignalDomain.Absolute => [-100d * counts / 32768d, 100d * counts / 32768d],
                _ => []
            },
            GateAmplitudePresentationKind.RectifiedMagnitude =>
                [100d * counts / (gate.SignalDomain == GateSignalDomain.Positive ? 32767d : 32768d)],
            _ => []
        };
        var projected = new List<GateThresholdProjection>(percentages.Length);
        foreach (double percentage in percentages)
        {
            double minimum = context.AmplitudePresentation == GateAmplitudePresentationKind.RawRf ? -100 : 0;
            if (LinearAxisTransform.TryDataToCoordinate(percentage, minimum, 100,
                context.AmplitudeCoordinateOrigin, context.AmplitudeCoordinateLength, out double coordinate))
                projected.Add(new(counts, percentage, coordinate));
        }
        return new(gate.GateId, gate.GateRevision, GateGeometryProjectionStatus.Projected,
            start, end, projected);
    }

    private static double? ProjectTime(double? seconds, GateGeometryProjectionContext context)
    {
        if (!seconds.HasValue) return null;
        return LinearAxisTransform.TryDataToCoordinate(seconds.Value,
            context.TimeViewport.VisibleMinimumSeconds, context.TimeViewport.VisibleMaximumSeconds,
            context.TimeCoordinateOrigin, context.TimeCoordinateLength, out double coordinate) ? coordinate : null;
    }
}
