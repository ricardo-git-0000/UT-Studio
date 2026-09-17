using System.Diagnostics;
using UTStudio.Acquisition.Simulator;
namespace UTStudio.LoadTests;

internal readonly record struct RateWindow(CounterSnapshot Start, CounterSnapshot End)
{
    internal double Seconds => (End.Timestamp - Start.Timestamp) / (double)Stopwatch.Frequency;
    internal double OfferedPerSecond => Rate(End.Offered - Start.Offered);
    internal double AcceptedPerSecond => Rate(End.Accepted - Start.Accepted);
    internal double ConsumedPerSecond => Rate(End.Consumed - Start.Consumed);
    private double Rate(long count) => Seconds > 0 ? count / Seconds : 0;
}

internal readonly record struct ResourceSample(double ElapsedSeconds, double IntervalSeconds, double CpuPercent,
    long ManagedEstimateBytes, long WorkingSetBytes, long Accepted, long Consumed);

// Fixed trailing series, chronological on export. Records all overwrites explicitly.
internal sealed class ResourceSeries(int capacity = 4096)
{
    private readonly ResourceSample[] _samples = new ResourceSample[capacity];
    private long _count;
    internal long TotalSamples => _count;
    internal void Add(ResourceSample sample) { _samples[_count++ % _samples.Length] = sample; }
    internal ResourceSample[] Snapshot()
    {
        int count = (int)Math.Min(_count, _samples.Length);
        var result = new ResourceSample[count];
        for (int i = 0; i < count; i++) { result[i] = _samples[(_count - count + i) % _samples.Length]; }
        return result;
    }
}

internal sealed class ProgressWatchdog(long timestamp, long consumed, long timeoutTicks)
{
    private long _lastProgress = timestamp, _lastConsumed = consumed;
    internal bool Expired(long now, long currentConsumed)
    {
        if (currentConsumed != _lastConsumed) { _lastConsumed = currentConsumed; _lastProgress = now; }
        return now - _lastProgress >= timeoutTicks;
    }
}

internal static class CpuMeasurement
{
    internal static double Percent(TimeSpan cpu, TimeSpan wall, int logicalProcessors) =>
        wall > TimeSpan.Zero ? cpu.TotalSeconds / wall.TotalSeconds / logicalProcessors * 100 : 0;
}
