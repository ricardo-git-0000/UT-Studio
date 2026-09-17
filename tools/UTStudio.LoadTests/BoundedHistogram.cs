namespace UTStudio.LoadTests;

/// <summary>Fixed trailing window; no allocation on Record. Percentiles describe its most recent values.</summary>
internal sealed class BoundedHistogram(int capacity = 8192)
{
    private readonly object _gate = new();
    private readonly double[] _values = new double[capacity > 0 ? capacity : throw new ArgumentOutOfRangeException(nameof(capacity))];
    private long _seen;
    private double _sum;

    internal long Count { get { lock (_gate) { return _seen; } } }
    internal void Record(double value)
    {
        if (!double.IsFinite(value) || value < 0) { return; }
        lock (_gate)
        {
            long index = _seen++;
            int slot = (int)(index % _values.Length);
            if (index >= _values.Length) { _sum -= _values[slot]; }
            _sum += value;
            _values[slot] = value;
        }
    }

    internal PercentileSnapshot Snapshot()
    {
        double[] copy;
        long seen;
        double mean;
        lock (_gate)
        {
            seen = _seen;
            int count = (int)Math.Min(seen, _values.Length);
            mean = count == 0 ? double.NaN : _sum / count;
            copy = new double[count];
            Array.Copy(_values, copy, count);
        }
        Array.Sort(copy);
        return new(seen, copy.Length, mean, Select(copy, 0.50), Select(copy, 0.95), Select(copy, 0.99));
    }

    private static double Select(double[] sorted, double percentile)
    {
        if (sorted.Length == 0) { return double.NaN; }
        int index = (int)Math.Ceiling(percentile * sorted.Length) - 1;
        return sorted[Math.Clamp(index, 0, sorted.Length - 1)];
    }
}

internal readonly record struct PercentileSnapshot(long Population, int WindowPopulation, double Mean,
    double P50, double P95, double P99);
