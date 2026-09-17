using System.Buffers;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Threading.Channels;
using UTStudio.Contracts.Acquisition;

namespace UTStudio.Acquisition.Simulator;

// Optional friend-assembly instrumentation. One reader, no extra queue or sample copy.
// The gate orders channel admission/read and counter cuts. Never await under it.
internal sealed class AcquisitionMetrics
{
    private readonly object _gate = new();
    internal object SyncRoot => _gate;
    private long _offered, _generated, _accepted, _consumed, _released, _untransferred, _rented, _returned;
    internal Action<long>? Offering { get; init; }
    internal void Offer()
    {
        lock (_gate)
        {
            // Internal synchronous diagnostic only: no external observers or waits.
            Offering?.Invoke(Stopwatch.GetTimestamp());
            _offered++;
        }
    }
    internal CounterSnapshot Snapshot()
    {
        lock (_gate)
        {
            return new(Stopwatch.GetTimestamp(), _offered, _generated, _accepted, _consumed,
            _released, _untransferred, _rented, _returned);
        }
    }
    internal TrackedOwner Track(IMemoryOwner<short> owner)
    {
        var tracked = new TrackedOwner(this, owner);
        lock (_gate) { _rented++; }
        return tracked;
    }
    internal ChannelReader<ConventionalUtFrame> Reader(ChannelReader<ConventionalUtFrame> reader) => new CountingReader(this, reader);
    internal async ValueTask WriteAsync(ChannelWriter<ConventionalUtFrame> writer, ConventionalUtFrame frame, CancellationToken token)
    {
        while (await writer.WaitToWriteAsync(token).ConfigureAwait(false))
        {
            lock (_gate)
            {
                token.ThrowIfCancellationRequested();
                if (writer.TryWrite(frame)) { _accepted++; return; }
            }
        }
        throw new ChannelClosedException();
    }
    internal sealed class TrackedOwner(AcquisitionMetrics metrics, IMemoryOwner<short> owner) : IMemoryOwner<short>
    {
        private IMemoryOwner<short>? _owner = owner;
        private bool _generated, _untransferred;
        public Memory<short> Memory => (_owner ?? throw new ObjectDisposedException(nameof(TrackedOwner))).Memory;
        internal void Generated() { lock (metrics._gate) { _generated = true; metrics._generated++; } }
        internal void NotTransferred() { _untransferred = true; }
        public void Dispose()
        {
            var owned = Interlocked.Exchange(ref _owner, null);
            if (owned is null) { return; }
            owned.Dispose();
            lock (metrics._gate)
            {
                metrics._returned++;
                if (_generated)
                {
                    if (_untransferred) { metrics._untransferred++; }
                    else { metrics._released++; }
                }
            }
        }
    }
    private sealed class CountingReader(AcquisitionMetrics metrics, ChannelReader<ConventionalUtFrame> inner) : ChannelReader<ConventionalUtFrame>
    {
        public override Task Completion => inner.Completion;
        public override ValueTask<bool> WaitToReadAsync(CancellationToken cancellationToken = default) => inner.WaitToReadAsync(cancellationToken);
        public override bool TryRead([MaybeNullWhen(false)] out ConventionalUtFrame item)
        {
            lock (metrics._gate)
            {
                if (!inner.TryRead(out item)) { return false; }
                metrics._consumed++;
                return true;
            }
        }
    }
}

internal readonly record struct CounterSnapshot(long Timestamp, long Offered, long Generated, long Accepted,
    long Consumed, long Released, long Untransferred, long Rented, long Returned)
{
    internal long InTransit => Accepted - Released;
    internal long Queued => Accepted - Consumed;
}
