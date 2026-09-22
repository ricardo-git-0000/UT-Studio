namespace UTStudio.Visualization.Core;

/// <summary>Immutable physical-time viewport shared by scan presentations, independent of axis orientation.</summary>
public sealed record ScanTimeViewport
{
    internal ScanTimeViewport(double domainMinimumSeconds, double domainMaximumSeconds,
        double visibleMinimumSeconds, double visibleMaximumSeconds)
    {
        DomainMinimumSeconds = domainMinimumSeconds;
        DomainMaximumSeconds = domainMaximumSeconds;
        VisibleMinimumSeconds = visibleMinimumSeconds;
        VisibleMaximumSeconds = visibleMaximumSeconds;
    }

    public double DomainMinimumSeconds { get; }
    public double DomainMaximumSeconds { get; }
    public double VisibleMinimumSeconds { get; }
    public double VisibleMaximumSeconds { get; }
    public double DomainSpanSeconds => DomainMaximumSeconds - DomainMinimumSeconds;
    public double VisibleSpanSeconds => VisibleMaximumSeconds - VisibleMinimumSeconds;
    public double ZoomFactor => DomainSpanSeconds / VisibleSpanSeconds;
    public bool IsReset => VisibleMinimumSeconds == DomainMinimumSeconds && VisibleMaximumSeconds == DomainMaximumSeconds;
}

public static class ScanTimeViewportOperations
{
    public const double MinimumZoomFactor = 1;
    public const double MaximumZoomFactor = 4096;

    public static ScanTimeViewport Create(double minimumSeconds, double maximumSeconds)
    {
        ValidateDomain(minimumSeconds, maximumSeconds);
        return new(minimumSeconds, maximumSeconds, minimumSeconds, maximumSeconds);
    }

    public static ScanTimeViewport Zoom(ScanTimeViewport viewport, double anchorSeconds, double factor)
    {
        ArgumentNullException.ThrowIfNull(viewport);
        if (!double.IsFinite(anchorSeconds)) { throw new ArgumentOutOfRangeException(nameof(anchorSeconds)); }
        if (!double.IsFinite(factor) || factor <= 0) { throw new ArgumentOutOfRangeException(nameof(factor)); }
        double anchor = Math.Clamp(anchorSeconds, viewport.VisibleMinimumSeconds, viewport.VisibleMaximumSeconds);
        double minimumSpan = viewport.DomainSpanSeconds / MaximumZoomFactor;
        double span = Math.Clamp(viewport.VisibleSpanSeconds / factor, minimumSpan, viewport.DomainSpanSeconds);
        double ratio = (anchor - viewport.VisibleMinimumSeconds) / viewport.VisibleSpanSeconds;
        return WithVisible(viewport, anchor - ratio * span, span);
    }

    public static ScanTimeViewport Pan(ScanTimeViewport viewport, double deltaSeconds)
    {
        ArgumentNullException.ThrowIfNull(viewport);
        if (!double.IsFinite(deltaSeconds)) { throw new ArgumentOutOfRangeException(nameof(deltaSeconds)); }
        return WithVisible(viewport, viewport.VisibleMinimumSeconds + deltaSeconds, viewport.VisibleSpanSeconds);
    }

    public static ScanTimeViewport Reset(ScanTimeViewport viewport) =>
        Create(viewport.DomainMinimumSeconds, viewport.DomainMaximumSeconds);

    public static ScanTimeViewport Reconcile(ScanTimeViewport viewport, double minimumSeconds, double maximumSeconds)
    {
        ArgumentNullException.ThrowIfNull(viewport);
        ValidateDomain(minimumSeconds, maximumSeconds);
        if (viewport.DomainMinimumSeconds == minimumSeconds && viewport.DomainMaximumSeconds == maximumSeconds) { return viewport; }
        var domain = Create(minimumSeconds, maximumSeconds);
        double span = Math.Min(viewport.VisibleSpanSeconds, domain.DomainSpanSeconds);
        if (span >= domain.DomainSpanSeconds * (1 - 1e-12)) { return domain; }
        double center = (viewport.VisibleMinimumSeconds + viewport.VisibleMaximumSeconds) / 2;
        return WithVisible(domain, center - span / 2, span);
    }

    private static ScanTimeViewport WithVisible(ScanTimeViewport viewport, double minimum, double span)
    {
        double maximumMinimum = viewport.DomainMaximumSeconds - span;
        double clampedMinimum = Math.Clamp(minimum, viewport.DomainMinimumSeconds, maximumMinimum);
        return new(viewport.DomainMinimumSeconds, viewport.DomainMaximumSeconds, clampedMinimum, clampedMinimum + span);
    }

    private static void ValidateDomain(double minimum, double maximum)
    {
        if (!double.IsFinite(minimum)) { throw new ArgumentOutOfRangeException(nameof(minimum)); }
        if (!double.IsFinite(maximum) || maximum <= minimum) { throw new ArgumentOutOfRangeException(nameof(maximum)); }
    }
}

/// <summary>Associates a shared physical viewport with the A-Scan snapshot that may display it.</summary>
public sealed record ScanTimeViewportBinding(
    UTStudio.Domain.Acquisition.AcquisitionRunId RunId,
    ulong SnapshotVersion,
    ScanTimeViewport Viewport);

/// <summary>Neutral reversible linear-axis calculations. Coordinates are logical units such as DIP.</summary>
public static class LinearAxisTransform
{
    public static bool TryDataToCoordinate(double value, double minimum, double maximum,
        double coordinateMinimum, double coordinateLength, out double coordinate)
    {
        coordinate = default;
        if (!IsValid(minimum, maximum, coordinateMinimum, coordinateLength) || !double.IsFinite(value)) { return false; }
        coordinate = coordinateMinimum + (value - minimum) / (maximum - minimum) * coordinateLength;
        return double.IsFinite(coordinate);
    }

    public static bool TryCoordinateToData(double coordinate, double coordinateMinimum, double coordinateLength,
        double minimum, double maximum, out double value)
    {
        value = default;
        if (!IsValid(minimum, maximum, coordinateMinimum, coordinateLength) || !double.IsFinite(coordinate)) { return false; }
        value = minimum + (coordinate - coordinateMinimum) / coordinateLength * (maximum - minimum);
        return double.IsFinite(value);
    }

    private static bool IsValid(double minimum, double maximum, double coordinateMinimum, double coordinateLength) =>
        double.IsFinite(minimum) && double.IsFinite(maximum) && maximum > minimum &&
        double.IsFinite(coordinateMinimum) && double.IsFinite(coordinateLength) && coordinateLength != 0;
}
