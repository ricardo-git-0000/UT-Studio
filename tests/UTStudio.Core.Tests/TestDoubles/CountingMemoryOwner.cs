using System.Buffers;

namespace UTStudio.Core.Tests.TestDoubles;

/// <summary>A deliberately non-idempotent owner to detect every disposal invocation.</summary>
internal sealed class CountingMemoryOwner(short[] samples) : IMemoryOwner<short>
{
    private int _disposeCount;
    public int DisposeCount => Volatile.Read(ref _disposeCount);
    public bool ThrowOnMemoryAccess { get; init; }
    public bool ThrowOnDispose { get; init; }

    public Memory<short> Memory
    {
        get
        {
            if (ThrowOnMemoryAccess)
            {
                throw new InvalidOperationException("Memory access failed.");
            }

            return samples;
        }
    }

    public void Dispose()
    {
        Interlocked.Increment(ref _disposeCount);
        if (ThrowOnDispose)
        {
            throw new InvalidOperationException("Owner release failed.");
        }
    }
}
