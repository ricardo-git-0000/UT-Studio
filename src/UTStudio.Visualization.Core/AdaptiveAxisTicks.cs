namespace UTStudio.Visualization.Core;

/// <summary>A bounded axis mark expressed relative to the caller-owned origin.</summary>
public readonly record struct AdaptiveAxisTick(decimal Value, double Position, bool IsEndpoint);

/// <summary>Creates stable, DPI-independent 1/2/5 ticks for neutral visualizations.</summary>
public static class AdaptiveAxisTicks
{
    public static IReadOnlyList<AdaptiveAxisTick> Create(decimal minimum, decimal maximum,
        double lengthDip, double minimumSpacingDip = 64, int maximumTickCount = 32)
    {
        if (maximum <= minimum || !double.IsFinite(lengthDip) || lengthDip <= 0 ||
            !double.IsFinite(minimumSpacingDip) || minimumSpacingDip <= 0 || maximumTickCount < 2)
        { return Array.Empty<AdaptiveAxisTick>(); }

        int desired = Math.Clamp((int)Math.Floor(lengthDip / minimumSpacingDip) + 1, 2, maximumTickCount);
        decimal span = maximum - minimum;
        decimal step = NiceStep(span / (desired - 1));
        decimal first = decimal.Ceiling(minimum / step) * step;
        var ticks = new List<AdaptiveAxisTick>(desired + 2);
        Add(ticks, minimum, minimum, maximum, true, maximumTickCount);
        for (decimal value = first; value <= maximum && ticks.Count < maximumTickCount; value += step)
        {
            if (value > minimum && value < maximum)
            { Add(ticks, value, minimum, maximum, false, maximumTickCount); }
            if (step <= 0) { break; }
        }
        Add(ticks, maximum, minimum, maximum, true, maximumTickCount);
        return ticks;
    }

    private static decimal NiceStep(decimal raw)
    {
        if (raw <= 0) { return 1; }
        double rawDouble = (double)raw;
        double exponent = Math.Floor(Math.Log10(rawDouble));
        decimal scale = (decimal)Math.Pow(10, exponent);
        decimal fraction = raw / scale;
        decimal nice = fraction <= 1 ? 1 : fraction <= 2 ? 2 : fraction <= 5 ? 5 : 10;
        return nice * scale;
    }

    private static void Add(List<AdaptiveAxisTick> ticks, decimal value, decimal minimum,
        decimal maximum, bool endpoint, int maximumTickCount)
    {
        if (ticks.Count >= maximumTickCount || (ticks.Count > 0 && ticks[^1].Value == value)) { return; }
        ticks.Add(new(value, (double)((value - minimum) / (maximum - minimum)), endpoint));
    }
}
