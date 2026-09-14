using UTStudio.Visualization.Core;

namespace UTStudio.App.Wpf.Controls;

internal readonly record struct PlotCoordinate(double X, double Y);

/// <summary>Viewport math only. RF scale stays fixed even for constant or empty signals.</summary>
internal static class AScanCoordinates
{
    internal static PlotCoordinate[] Map(IReadOnlyList<AScanPoint> points, double minimumTime,
        double maximumTime, double width, double height)
    {
        double range = maximumTime - minimumTime;
        if (!double.IsFinite(width) || !double.IsFinite(height) || width <= 0 || height <= 0 ||
            !double.IsFinite(minimumTime) || !double.IsFinite(maximumTime) || !double.IsFinite(range) || range <= 0)
        { return []; }
        var result = new PlotCoordinate[points.Count];
        for (int i = 0; i < points.Count; i++)
        {
            var point = points[i];
            if (!double.IsFinite(point.TimeSeconds) || !double.IsFinite(point.AmplitudePercent)) { return []; }
            result[i] = new(Math.Clamp((point.TimeSeconds - minimumTime) / range, 0, 1) * width,
                (100 - Math.Clamp(point.AmplitudePercent, -100, 100)) / 200 * height);
        }
        return result;
    }
}
