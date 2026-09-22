using UTStudio.Visualization.Core;

namespace UTStudio.Visualization.Wpf.Rendering;

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
        if (points.Count == 0) { return []; }
        for (int i = 0; i < points.Count; i++)
        {
            var point = points[i];
            if (!double.IsFinite(point.TimeSeconds) || !double.IsFinite(point.AmplitudePercent)) { return []; }
        }
        var result = new List<PlotCoordinate>(points.Count);
        if (points.Count == 1)
        {
            if (points[0].TimeSeconds < minimumTime || points[0].TimeSeconds > maximumTime) { return []; }
            Add(points[0].TimeSeconds, points[0].AmplitudePercent);
            return result.ToArray();
        }
        for (int i = 1; i < points.Count; i++)
        {
            AScanPoint first = points[i - 1], second = points[i];
            double segmentMinimum = Math.Min(first.TimeSeconds, second.TimeSeconds);
            double segmentMaximum = Math.Max(first.TimeSeconds, second.TimeSeconds);
            double clippedMinimum = Math.Max(minimumTime, segmentMinimum);
            double clippedMaximum = Math.Min(maximumTime, segmentMaximum);
            if (clippedMaximum < clippedMinimum) { continue; }
            if (first.TimeSeconds == second.TimeSeconds)
            {
                if (first.TimeSeconds >= minimumTime && first.TimeSeconds <= maximumTime)
                { Add(first.TimeSeconds, first.AmplitudePercent); Add(second.TimeSeconds, second.AmplitudePercent); }
                continue;
            }
            Add(clippedMinimum, Interpolate(first, second, clippedMinimum));
            Add(clippedMaximum, Interpolate(first, second, clippedMaximum));
        }
        return result.ToArray();

        static double Interpolate(AScanPoint first, AScanPoint second, double time) => first.AmplitudePercent +
            (time - first.TimeSeconds) / (second.TimeSeconds - first.TimeSeconds) *
            (second.AmplitudePercent - first.AmplitudePercent);

        void Add(double time, double amplitude)
        {
            if (!LinearAxisTransform.TryDataToCoordinate(time, minimumTime, maximumTime, 0, width, out double x) ||
                !LinearAxisTransform.TryDataToCoordinate(Math.Clamp(amplitude, -100, 100), -100, 100,
                    height, -height, out double y)) { return; }
            var coordinate = new PlotCoordinate(x, y);
            if (result.Count == 0 || result[^1] != coordinate) { result.Add(coordinate); }
        }
    }
}
