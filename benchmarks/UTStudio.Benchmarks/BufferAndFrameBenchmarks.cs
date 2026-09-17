using System.Buffers;
using BenchmarkDotNet.Attributes;
using UTStudio.Acquisition.Simulator;
using UTStudio.Contracts.Acquisition;

namespace UTStudio.Benchmarks;

[MemoryDiagnoser]
public class BufferAndFrameBenchmarks
{
    [Params(2048, 65535)] public int SampleCount { get; set; }
    private BoundedSampleBufferPool _pool = null!;
    private UTStudio.Domain.Acquisition.ConventionalUtFrameMetadata _metadata = null!;

    [GlobalSetup]
    public void Setup()
    {
        _pool = new BoundedSampleBufferPool(8, SampleCount);
        _metadata = BenchmarkData.Metadata(SampleCount);
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _pool.Seal();
        if (_pool.Outstanding != 0 || !_pool.AllFramesReleased.IsCompletedSuccessfully)
        { throw new InvalidOperationException("Benchmark left outstanding sample owners."); }
    }

    [Benchmark]
    public async ValueTask<short> RentUseReturn()
    {
        using IMemoryOwner<short> owner = await _pool.RentAsync(CancellationToken.None);
        owner.Memory.Span[0] = 42;
        return owner.Memory.Span[0];
    }

    [Benchmark]
    public async ValueTask<ulong> CreateAndDisposeFrame()
    {
        IMemoryOwner<short>? owner = await _pool.RentAsync(CancellationToken.None);
        try
        {
            using var frame = new ConventionalUtFrame(_metadata, 7, TimeSpan.FromTicks(7), owner);
            owner = null;
            return frame.Sequence;
        }
        finally { owner?.Dispose(); }
    }
}
