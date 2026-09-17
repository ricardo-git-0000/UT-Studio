using BenchmarkDotNet.Attributes;
using UTStudio.Acquisition.Simulator;

namespace UTStudio.Benchmarks;

[MemoryDiagnoser]
public class SignalGenerationBenchmarks
{
    [Params(2048, 65535)] public int SampleCount { get; set; }
    private short[] _samples = null!;
    private SimulatorOptions _options = null!;
    private UTStudio.Domain.Acquisition.ConventionalAcquisitionConfiguration _configuration = null!;
    private ulong _sequence;

    [GlobalSetup]
    public void Setup()
    {
        _samples = new short[SampleCount];
        _options = new SimulatorOptions(seed: 1, noiseAmplitude: 0.01);
        _configuration = BenchmarkData.Configuration(SampleCount);
    }

    [Benchmark(OperationsPerInvoke = 1)]
    public short Fill()
    {
        SyntheticRfGenerator.Fill(_samples, _configuration, _options, _sequence++, CancellationToken.None);
        return _samples[^1];
    }
}
