using UTStudio.Domain.Acquisition;

namespace UTStudio.Acquisition.Simulator;

internal static class SyntheticRfGenerator
{
    private const double CarrierHz = 5_000_000;
    private const double SigmaSeconds = 0.35e-6;

    internal static void Validate(ConventionalAcquisitionConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        if (configuration.PhysicalChannelId.Value != 0 || configuration.SignalMode != UtSignalMode.Rf ||
            configuration.SampleRateHz <= 2 * CarrierHz)
        {
            throw new ArgumentException("The simulator requires RF channel zero and sampling above 10 MHz.", nameof(configuration));
        }

        double last = configuration.FirstSampleOffsetSeconds +
            (configuration.SampleCount - 1) / configuration.SampleRateHz;
        // Bound phase arithmetic as well as time. The product model itself permits broader offsets.
        if (!double.IsFinite(last) ||
            !double.IsFinite(2 * Math.PI * CarrierHz * configuration.FirstSampleOffsetSeconds) ||
            !double.IsFinite(2 * Math.PI * CarrierHz * last))
        {
            throw new ArgumentException("Sample times cannot be represented by this generator.", nameof(configuration));
        }
    }

    internal static void Fill(Span<short> samples, ConventionalAcquisitionConfiguration configuration,
        SimulatorOptions options, ulong sequence, CancellationToken cancellationToken)
    {
        ulong random = Mix(options.Seed ^ Mix(sequence));
        for (int i = 0; i < samples.Length; i++)
        {
            if ((i & 255) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            double time = configuration.FirstSampleOffsetSeconds + i / configuration.SampleRateHz;
            double signal = Pulse(time - 10e-6, 0.6) + Pulse(time - 25e-6, 0.3);
            random = Mix(random);
            double noise = ((random >> 11) * (1.0 / (1UL << 53)) * 2 - 1) * options.NoiseAmplitude;
            double counts = Math.Round((signal + noise) * 32768, MidpointRounding.ToEven);
            samples[i] = (short)Math.Clamp(counts, short.MinValue, short.MaxValue);
        }
    }

    private static double Pulse(double delta, double amplitude)
    {
        double normalized = delta / SigmaSeconds;
        // Outside this envelope the contribution is negligible; avoids unnecessary extreme phase arithmetic.
        if (Math.Abs(normalized) > 40)
        {
            return 0;
        }

        return amplitude * Math.Exp(-0.5 * normalized * normalized) * Math.Sin(2 * Math.PI * CarrierHz * delta);
    }

    // SplitMix64 mixing, version 1. Independent of System.Random/runtime seed algorithms.
    private static ulong Mix(ulong value)
    {
        unchecked
        {
            value += 0x9E3779B97F4A7C15UL;
            value = (value ^ (value >> 30)) * 0xBF58476D1CE4E5B9UL;
            value = (value ^ (value >> 27)) * 0x94D049BB133111EBUL;
            return value ^ (value >> 31);
        }
    }
}
