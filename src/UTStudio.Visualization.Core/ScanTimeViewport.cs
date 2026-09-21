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
    public bool IsReset => VisibleMinimumSeconds == DomainMinimumSeconds && VisibleMaximumSeconds == DomainMaximumSeconds;
}

public static class ScanTimeViewportOperations
{
    private const double MaximumZoom = 4096;

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
        double minimumSpan = viewport.DomainSpanSeconds / MaximumZoom;
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
