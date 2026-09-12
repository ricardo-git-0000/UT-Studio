using System.Buffers;
using System.Threading.Channels;

namespace UTStudio.Acquisition.Simulator;

/// <summary>Fixed arrays, fresh owners per loan. Seal and loan accounting share one lock.</summary>
internal sealed class BoundedSampleBufferPool
{
    private readonly object _gate = new();
    private readonly Channel<short[]> _free;
    private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _outstanding;
    private bool _sealed;

    internal BoundedSampleBufferPool(int bufferCount, int sampleCount)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(bufferCount, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(sampleCount, 1);
        _ = checked((long)bufferCount * sampleCount * sizeof(short));
        _free = Channel.CreateBounded<short[]>(new BoundedChannelOptions(bufferCount)
        {
            FullMode = BoundedChannelFullMode.Wait,
            AllowSynchronousContinuations = false
        });
        for (int i = 0; i < bufferCount; i++)
        {
            _free.Writer.TryWrite(new short[sampleCount]);
        }
    }

    internal Task AllFramesReleased => _released.Task;
    internal int Outstanding { get { lock (_gate) { return _outstanding; } } }

    internal async ValueTask<IMemoryOwner<short>> RentAsync(CancellationToken cancellationToken)
    {
        while (await _free.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
        {
            lock (_gate)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (_sealed)
                {
                    throw new InvalidOperationException("The pool is sealed.");
                }

                if (_free.Reader.TryRead(out var samples))
                {
                    var owner = new SampleOwner(this, samples);
                    _outstanding++;
                    return owner;
                }
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        throw new InvalidOperationException("The pool is sealed.");
    }

    internal void Seal()
    {
        lock (_gate)
        {
            _sealed = true;
            _free.Writer.TryComplete();
            while (_free.Reader.TryRead(out _)) { }
            CompleteIfReleased();
        }
    }

    private void Return(short[] samples)
    {
        lock (_gate)
        {
            if (!_sealed && !_free.Writer.TryWrite(samples))
            {
                throw new InvalidOperationException("The buffer accounting is inconsistent.");
            }

            _outstanding--;
            CompleteIfReleased();
        }
    }

    private void CompleteIfReleased()
    {
        if (_sealed && _outstanding == 0)
        {
            _released.TrySetResult();
        }
    }

    private sealed class SampleOwner(BoundedSampleBufferPool pool, short[] samples) : IMemoryOwner<short>
    {
        private BoundedSampleBufferPool? _pool = pool;
        private short[]? _samples = samples;

        public Memory<short> Memory
        {
            get
            {
                ObjectDisposedException.ThrowIf(Volatile.Read(ref _pool) is null, this);
                return _samples!;
            }
        }

        public void Dispose()
        {
            var owner = Interlocked.Exchange(ref _pool, null);
            if (owner is null) { return; }
            var buffer = _samples!;
            _samples = null;
            owner.Return(buffer);
        }
    }
}
