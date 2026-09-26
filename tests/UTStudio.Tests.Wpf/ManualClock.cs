namespace UTStudio.Tests.Wpf;

internal sealed class ManualClock : TimeProvider
{
    private long _ticks;
    private readonly object _gate = new();
    private readonly List<ManualTimer> _timers = [];
    private TaskCompletionSource<ITimer> _nextTimer = NewTimerSignal();
    internal bool FailNextTimer { get; set; }
    internal TaskCompletionSource TimerFailed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal int RegisteredTimers { get { lock (_gate) { return _timers.Count; } } }
    public override long TimestampFrequency => TimeSpan.TicksPerSecond;
    public override long GetTimestamp() => Interlocked.Read(ref _ticks);
    public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch + TimeSpan.FromTicks(GetTimestamp());
    internal void Advance(TimeSpan duration)
    {
        Interlocked.Add(ref _ticks, duration.Ticks);
        ManualTimer[] ready;
        lock (_gate) { ready = _timers.Where(timer => timer.Due <= GetTimestamp()).ToArray(); _timers.RemoveAll(timer => ready.Contains(timer)); }
        foreach (var timer in ready) { timer.Fire(); }
    }
    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        if (FailNextTimer) { FailNextTimer = false; TimerFailed.TrySetResult(); throw new InvalidOperationException("Synthetic clock failure"); }
        if (period != Timeout.InfiniteTimeSpan) { throw new NotSupportedException(); }
        var timer = new ManualTimer(this, callback, state, GetTimestamp() + dueTime.Ticks);
        lock (_gate)
        {
            _timers.Add(timer);
            _nextTimer.TrySetResult(timer);
            _nextTimer = NewTimerSignal();
        }
        return timer;
    }
    internal Task<ITimer> NextTimerAsync()
    {
        lock (_gate)
        { return _timers.Count > 0 ? Task.FromResult<ITimer>(_timers[0]) : _nextTimer.Task; }
    }
    private static TaskCompletionSource<ITimer> NewTimerSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private void Remove(ManualTimer timer) { lock (_gate) { _timers.Remove(timer); } }
    private sealed class ManualTimer(ManualClock owner, TimerCallback callback, object? state, long due) : ITimer
    {
        private int _disposed;
        internal long Due => due;
        internal void Fire() { if (Interlocked.Exchange(ref _disposed, 1) == 0) { callback(state); } }
        public bool Change(TimeSpan dueTime, TimeSpan period) => throw new NotSupportedException();
        public void Dispose() { if (Interlocked.Exchange(ref _disposed, 1) == 0) { owner.Remove(this); } }
        public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
    }
}
