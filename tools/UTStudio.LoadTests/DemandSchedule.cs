using System.Diagnostics;

namespace UTStudio.LoadTests;

internal interface IPacingClock
{
    long Frequency { get; }
    long Timestamp { get; }
    ValueTask DelayAsync(TimeSpan delay, CancellationToken cancellationToken);
}

internal sealed class StopwatchPacingClock : IPacingClock
{
    internal static readonly StopwatchPacingClock Instance = new();
    public long Frequency => Stopwatch.Frequency;
    public long Timestamp => Stopwatch.GetTimestamp();
    public async ValueTask DelayAsync(TimeSpan delay, CancellationToken cancellationToken) =>
        await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
}

internal sealed class DemandSchedule
{
    private readonly double? _rate;
    private readonly long _frequency;
    private readonly bool _detailed;
    private readonly PacingMode _mode;
    private readonly int _capacity;
    private readonly Func<long> _timestamp;
    private readonly object _gate = new();
    private long? _origin;
    private int _preparedIndex;
    private bool _prepared;
    private long _nextSlot, _offered, _duplicates, _missed, _recovered, _bursts, _burstFrames, _maximumBurst;
    private double _delaySum, _delayMax;

    internal DemandSchedule(double? rate, long frequency = 0, bool detailed = true,
        PacingMode mode = PacingMode.SkipMissed, int capacity = 32, Func<long>? timestamp = null)
    {
        _rate = rate;
        _frequency = frequency == 0 ? Stopwatch.Frequency : frequency;
        if (_frequency <= 0) { throw new ArgumentOutOfRangeException(nameof(frequency)); }
        if (rate is { } value && (!double.IsFinite(value) || value <= 0)) { throw new ArgumentOutOfRangeException(nameof(rate)); }
        _detailed = detailed;
        _mode = mode;
        _capacity = capacity > 0 ? capacity : throw new ArgumentOutOfRangeException(nameof(capacity));
        _timestamp = timestamp ?? Stopwatch.GetTimestamp;
    }

    internal long Period => _rate is { } target ? Math.Max(1, checked((long)Math.Ceiling(_frequency / target))) : 0;
    internal double? GridRate => _rate is null ? null : (double)_frequency / Period;
    internal void Start(long now) { lock (_gate) { _origin ??= now; } }

    internal int Claim(long now)
    {
        lock (_gate)
        {
            _origin ??= now;
            if (_rate is null) { return 1; }
            long current = SlotAt(now);
            if (current < _nextSlot) { return 0; }
            long due = checked(current - _nextSlot + 1);
            if (_mode == PacingMode.SkipMissed)
            {
                return 1;
            }
            int batch = (int)Math.Min(due, _capacity);
            _missed = checked(_missed + due - batch);
            _nextSlot = checked(current + 1);
            return batch;
        }
    }

    internal void Offer(long now, int indexInBatch)
    {
        lock (_gate)
        {
            _origin ??= now;
            if (_rate is not null)
            {
                long current = SlotAt(now);
                long offeredSlot = _mode == PacingMode.SkipMissed ? _nextSlot : Math.Max(0, _nextSlot - 1 - indexInBatch);
                long deadline = SaturatingAdd(_origin.Value, SaturatingMultiply(offeredSlot, Period));
                if (_detailed)
                {
                    double lateTicks = now <= deadline ? 0 : (double)((Int128)now - deadline);
                    double delay = lateTicks * 1_000_000d / _frequency;
                    _delaySum += delay;
                    _delayMax = Math.Max(_delayMax, delay);
                }
                if (now < deadline) { _duplicates++; }
                if (_mode == PacingMode.SkipMissed) { _nextSlot = Math.Max(_nextSlot, checked(current + 1)); }
                if (indexInBatch > 0)
                {
                    _recovered = checked(_recovered + 1);
                    if (indexInBatch == 1) { _bursts = checked(_bursts + 1); _burstFrames = checked(_burstFrames + 2); }
                    else { _burstFrames = checked(_burstFrames + 1); }
                    _maximumBurst = Math.Max(_maximumBurst, indexInBatch + 1L);
                }
            }
            _offered = checked(_offered + 1);
        }
    }

    internal void Offer(long now)
    {
        _ = Claim(now);
        Offer(now, 0);
    }
    internal void PrepareOffer(int indexInBatch) { lock (_gate) { _preparedIndex = indexInBatch; _prepared = true; } }
    internal void Offer()
    {
        int index;
        long now = _timestamp();
        lock (_gate)
        {
            if (!_prepared && _rate is not null && _mode == PacingMode.CatchUpBounded)
            {
                _origin ??= now;
                long current = SlotAt(now);
                if (current >= _nextSlot)
                {
                    _missed = checked(_missed + current - _nextSlot);
                    _nextSlot = checked(current + 1);
                }
            }
            index = _preparedIndex;
            _preparedIndex = 0;
            _prepared = false;
        }
        Offer(now, index);
    }

    internal TimeSpan UntilNext(long now)
    {
        lock (_gate)
        {
            if (_rate is null || _origin is null) { return TimeSpan.Zero; }
            long deadline = SaturatingAdd(_origin.Value, SaturatingMultiply(_nextSlot, Period));
            long remaining = (long)Int128.Clamp((Int128)deadline - now, 0, long.MaxValue);
            double ticks = remaining * (double)TimeSpan.TicksPerSecond / _frequency;
            return TimeSpan.FromTicks((long)Math.Min(TimeSpan.MaxValue.Ticks, Math.Ceiling(ticks)));
        }
    }

    internal DemandSnapshot Snapshot(long now)
    {
        lock (_gate)
        {
            long scheduled = _rate is null ? _offered : _origin is null ? 0 : checked(SlotAt(now) + 1);
            long missed = _mode == PacingMode.SkipMissed ? Math.Max(0, scheduled - (_offered - _duplicates)) : _missed;
            long pending = _mode == PacingMode.SkipMissed ? 0 : Math.Max(0, scheduled - _offered - missed);
            return new(scheduled, _offered, missed, pending, _duplicates, _recovered, _bursts,
                _bursts == 0 ? 0 : (double)_burstFrames / _bursts, _maximumBurst,
                _detailed ? (_offered == 0 ? 0 : _delaySum / _offered) : null, _detailed ? _delayMax : null);
        }
    }

    private long SlotAt(long now) => now <= _origin!.Value ? 0 :
        (long)Int128.Min(((Int128)now - _origin.Value) / Period, long.MaxValue - 1);
    private static long SaturatingMultiply(long left, long right) =>
        left == 0 || right == 0 ? 0 : left > long.MaxValue / right ? long.MaxValue : left * right;
    private static long SaturatingAdd(long left, long right) => left > long.MaxValue - right ? long.MaxValue : left + right;
}

internal readonly record struct DemandSnapshot(long Scheduled, long Offered, long Missed, long Pending, long EarlyOrDuplicate,
    long Recovered, long Bursts, double MeanBurstSize, long MaximumBurstSize,
    double? MeanDelayMicroseconds, double? MaximumDelayMicroseconds);
