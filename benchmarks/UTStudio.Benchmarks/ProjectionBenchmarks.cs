using BenchmarkDotNet.Attributes;
using UTStudio.Visualization.Core;

namespace UTStudio.Benchmarks;

[MemoryDiagnoser]
public class ProjectionBenchmarks
{
    [Params(2048, 65535)] public int SampleCount { get; set; }
    [Params(4, 256, 1024)] public int MaximumPoints { get; set; }
    private short[] _samples = null!;
    private UTStudio.Domain.Acquisition.ConventionalUtFrameMetadata _metadata = null!;
    private AScanProjector _projector = null!;

    [GlobalSetup]
    public void Setup()
    {
        _samples = BenchmarkData.Samples(SampleCount);
        _metadata = BenchmarkData.Metadata(SampleCount);
        _projector = new AScanProjector(MaximumPoints);
    }

    [Benchmark]
    public int Project() => _projector.Project(_metadata, 1, TimeSpan.Zero, _samples, 1).Points.Count;
}
