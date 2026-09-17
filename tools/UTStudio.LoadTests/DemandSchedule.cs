using System.Diagnostics;
namespace UTStudio.LoadTests;

// Fixed grid. Late demand is counted rather than replayed in an unbounded burst.
internal sealed class DemandSchedule(double? rate, long frequency = 0)
{
    private readonly long _frequency = frequency == 0 ? Stopwatch.Frequency : frequency;
    private readonly object _gate = new();
    private long? _origin;
    private long _nextSlot, _offered, _duplicates;
    private double _delaySum, _delayMax;
    internal long Period => rate is { } target ? Math.Max(1, (long)Math.Ceiling(_frequency / target)) : 0;
    internal double? GridRate => rate is null ? null : (double)_frequency / Period;
    internal void Start(long now) { lock (_gate) { _origin ??= now; } }
    internal void Offer(long now)
    {
        lock (_gate)
        {
            _origin ??= now;
            if (rate is not null)
            {
                long slot = (now - _origin.Value) / Period;
                double delay = Math.Max(0, now - (_origin.Value + _nextSlot * Period)) * 1_000_000d / _frequency;
                _delaySum += delay;
                _delayMax = Math.Max(_delayMax, delay);
                if (slot < _nextSlot) { _duplicates++; }
                _nextSlot = Math.Max(_nextSlot, slot + 1);
            }
            _offered++;
        }
    }
    internal TimeSpan UntilNext(long now)
    {
        lock (_gate)
        {
            if (rate is null || _origin is null) { return TimeSpan.Zero; }
            return TimeSpan.FromTicks((long)Math.Ceiling(Math.Max(0, _origin.Value + _nextSlot * Period - now) * (double)TimeSpan.TicksPerSecond / _frequency));
        }
    }
    internal DemandSnapshot Snapshot(long now)
    {
        lock (_gate)
        {
            long scheduled = rate is null ? _offered : _origin is null ? 0 : Math.Max(0, (now - _origin.Value) / Period + 1);
            return new(scheduled, _offered, Math.Max(0, scheduled - (_offered - _duplicates)), _duplicates,
                _offered == 0 ? 0 : _delaySum / _offered, _delayMax);
        }
    }
}
internal readonly record struct DemandSnapshot(long Scheduled, long Offered, long Missed, long EarlyOrDuplicate,
    double MeanDelayMicroseconds, double MaximumDelayMicroseconds);
