using System.Diagnostics;
using UTStudio.Acquisition.Simulator;
namespace UTStudio.LoadTests;

internal sealed class LoadTelemetry
{
    private readonly CorrelationRing _correlation = new(65536);
    private long _activeStart = long.MaxValue, _activeEnd = long.MaxValue;
    internal LoadTelemetry(double? rate = null)
    {
        Demand = new(rate);
        Counters = new() { Offering = Demand.Offer };
    }
    internal AcquisitionMetrics Counters { get; }
    internal DemandSchedule Demand { get; }
    internal BoundedHistogram AcceptToCallbackMicroseconds { get; } = new();
    internal BoundedHistogram ProjectionMicroseconds { get; } = new();
    internal long Produced => Counters.Snapshot().Accepted;
    internal long CorrelationMisses => _correlation.Misses;
    internal long CorrelationOverwrites => _correlation.Overwrites;
    internal CounterSnapshot BeginWindow()
    {
        lock (Counters.SyncRoot)
        {
            var cut = Counters.Snapshot();
            _activeStart = cut.Timestamp;
            return cut;
        }
    }
    internal CounterSnapshot EndWindow(out DemandSnapshot demand)
    {
        lock (Counters.SyncRoot)
        {
            var cut = Counters.Snapshot();
            _activeEnd = cut.Timestamp;
            demand = Demand.Snapshot(cut.Timestamp);
            return cut;
        }
    }
    internal bool InWindow(long timestamp) => timestamp >= Volatile.Read(ref _activeStart) && timestamp < Volatile.Read(ref _activeEnd);
    internal void PrepareFrame(ulong sequence) => _correlation.Write(sequence, Stopwatch.GetTimestamp());
    internal void ObserveVisual(ulong sequence)
    {
        long end = Stopwatch.GetTimestamp();
        if (_correlation.TryRead(sequence, out long start))
        {
            lock (Counters.SyncRoot)
            {
                if (InWindow(start) && InWindow(end))
                { AcceptToCallbackMicroseconds.Record(Stopwatch.GetElapsedTime(start, end).TotalMicroseconds); }
            }
        }
    }
    internal void Projection(long start, long end)
    {
        lock (Counters.SyncRoot)
        {
            if (InWindow(start) && InWindow(end))
            { ProjectionMicroseconds.Record(Stopwatch.GetElapsedTime(start, end).TotalMicroseconds); }
        }
    }
}

// One writer (Application's sole reader). Version protects the pair without a global lock.
internal sealed class CorrelationRing(int capacity)
{
    private readonly Slot[] _slots = new Slot[capacity > 0 ? capacity : throw new ArgumentOutOfRangeException(nameof(capacity))];
    private long _misses, _overwrites;
    internal long Misses => Interlocked.Read(ref _misses);
    internal long Overwrites => Interlocked.Read(ref _overwrites);
    internal void Write(ulong sequence, long timestamp)
    {
        ref Slot slot = ref _slots[sequence % (ulong)_slots.Length];
        if (Volatile.Read(ref slot.Version) != 0) { Interlocked.Increment(ref _overwrites); }
        Interlocked.Increment(ref slot.Version);
        slot.Sequence = sequence;
        slot.Timestamp = timestamp;
        Interlocked.Increment(ref slot.Version);
    }
    internal bool TryRead(ulong sequence, out long timestamp, Action? afterFirstVersion = null)
    {
        ref Slot slot = ref _slots[sequence % (ulong)_slots.Length];
        long before = Volatile.Read(ref slot.Version);
        afterFirstVersion?.Invoke(); // test seam; null on measured path
        ulong found = slot.Sequence;
        long value = slot.Timestamp;
        Thread.MemoryBarrier();
        long after = Volatile.Read(ref slot.Version);
        if (before != 0 && (before & 1) == 0 && before == after && found == sequence)
        { timestamp = value; return true; }
        Interlocked.Increment(ref _misses);
        timestamp = 0;
        return false;
    }
    private struct Slot { internal long Version, Timestamp; internal ulong Sequence; }
}
