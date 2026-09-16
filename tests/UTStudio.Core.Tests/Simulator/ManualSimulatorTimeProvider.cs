using System.Threading.Channels;
using UTStudio.Core.Tests.TestDoubles;

namespace UTStudio.Core.Tests.Simulator;

/// <summary>Only test watchdogs use real time. Production delays fire explicitly through registered timers.</summary>
internal sealed class ManualSimulatorTimeProvider : TimeProvider
{
    private readonly object _gate = new();
    private readonly Channel<ManualTimer> _registrations = Channel.CreateUnbounded<ManualTimer>();
    private long _ticks;
    private DateTimeOffset _utc = DateTimeOffset.UnixEpoch;
    internal Action? BeforeUtcRead { get; set; }
    internal bool FailTimerCreation { get; set; }
    internal Exception TimerFailure { get; set; } = new InvalidOperationException("Synthetic timer failure.");
    internal Action? OnTimerDispose { get; set; }
    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override long GetTimestamp() { lock (_gate) { return _ticks; } }
    public override DateTimeOffset GetUtcNow()
    {
        BeforeUtcRead?.Invoke();
        lock (_gate) { return _utc; }
    }

    internal void SetUtc(DateTimeOffset value) { lock (_gate) { _utc = value; } }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        if (FailTimerCreation) { throw TimerFailure; }
        if (period != Timeout.InfiniteTimeSpan) { throw new NotSupportedException("Tests expect one-shot delays."); }
        ManualTimer timer;
        lock (_gate) { timer = new ManualTimer(this, callback, state, checked(_ticks + dueTime.Ticks)); }
        _registrations.Writer.TryWrite(timer);
        return timer;
    }

    internal async Task<ManualTimer> NextTimerAsync() =>
        await DiagnosticWait.For(_registrations.Reader.ReadAsync().AsTask(), "manual simulator timer registered");

    internal sealed class ManualTimer(ManualSimulatorTimeProvider clock, TimerCallback callback, object? state, long due) : ITimer
    {
        private bool _disposed;
        internal bool IsDisposed { get { lock (clock._gate) { return _disposed; } } }

        internal void FireEarly()
        {
            lock (clock._gate)
            {
                if (_disposed) { throw new InvalidOperationException("Cannot fire a cancelled timer."); }
                long advance = (due - clock._ticks) / 2;
                clock._ticks += advance;
                clock._utc += TimeSpan.FromTicks(advance);
                _disposed = true;
            }

            callback(state);
        }

        internal void Fire(TimeSpan extraAdvance = default)
        {
            lock (clock._gate)
            {
                if (_disposed) { throw new InvalidOperationException("Cannot fire a cancelled timer."); }
                long next = checked(Math.Max(clock._ticks, due) + extraAdvance.Ticks);
                clock._utc += TimeSpan.FromTicks(next - clock._ticks);
                clock._ticks = next;
                _disposed = true;
            }

            callback(state);
        }

        public bool Change(TimeSpan dueTime, TimeSpan period) => throw new NotSupportedException();
        public void Dispose()
        {
            lock (clock._gate) { _disposed = true; }
            clock.OnTimerDispose?.Invoke();
        }
        public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
    }
}
