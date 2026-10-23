namespace UTStudio.Acquisition.Simulator;

/// <summary>Simulator-local defaults, not limits of IUtFrameSource. Capacities require benchmarks.</summary>
public sealed class SimulatorOptions
{
    public SimulatorOptions(ulong seed = 1, double maxAScansPerSecond = 100,
        int channelCapacity = 4, int bufferCount = 8, double noiseAmplitude = 0.01,
        SimulatorSignalProfile? signalProfile = null)
    {
        if (!double.IsFinite(maxAScansPerSecond) || maxAScansPerSecond <= 0 || maxAScansPerSecond > 100 ||
            1 / maxAScansPerSecond > uint.MaxValue / 1000d - 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maxAScansPerSecond));
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(channelCapacity, 1);
        if ((long)bufferCount < (long)channelCapacity + 4)
        {
            throw new ArgumentOutOfRangeException(nameof(bufferCount), "Allow the queue and four additional owners.");
        }

        if (!double.IsFinite(noiseAmplitude) || noiseAmplitude < 0 || noiseAmplitude > 0.01)
        {
            throw new ArgumentOutOfRangeException(nameof(noiseAmplitude));
        }

        Seed = seed;
        MaxAScansPerSecond = maxAScansPerSecond;
        ChannelCapacity = channelCapacity;
        BufferCount = bufferCount;
        NoiseAmplitude = noiseAmplitude;
        SignalProfile = signalProfile ?? SimulatorSignalProfiles.MovingV1;
        if (!SignalProfile.IsValid) throw new ArgumentException("A valid signal profile is required.", nameof(signalProfile));
        // Task.Delay may truncate fractional milliseconds; round conservatively.
        Period = TimeSpan.FromMilliseconds(Math.Ceiling(1000 / maxAScansPerSecond));
    }

    public ulong Seed { get; }
    public double MaxAScansPerSecond { get; }
    public int ChannelCapacity { get; }
    public int BufferCount { get; }
    public double NoiseAmplitude { get; }
    public SimulatorSignalProfile SignalProfile { get; }
    internal TimeSpan Period { get; }
}
