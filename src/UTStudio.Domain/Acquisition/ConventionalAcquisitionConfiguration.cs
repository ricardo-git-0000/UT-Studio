namespace UTStudio.Domain.Acquisition;

/// <summary>Immutable settings for one conventional RF execution.</summary>
/// <remarks>
/// Product limits are independent; a source must validate its actual configuration jointly.
/// Sample frequency is not the trigger rate. Negative finite offsets are allowed.
/// Extremely small frequencies may require further representability checks by consumers.
/// </remarks>
public sealed class ConventionalAcquisitionConfiguration
{
    public const int MaximumSampleCount = 65_535;
    public const double MaximumSampleRateHz = 100_000_000;

    public ConventionalAcquisitionConfiguration(
        PhysicalChannelId physicalChannelId,
        int sampleCount,
        double sampleRateHz,
        double firstSampleOffsetSeconds = 0,
        UtSignalMode signalMode = UtSignalMode.Rf)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(sampleCount, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(sampleCount, MaximumSampleCount);
        if (!double.IsFinite(sampleRateHz) || sampleRateHz <= 0 || sampleRateHz > MaximumSampleRateHz)
        {
            throw new ArgumentOutOfRangeException(nameof(sampleRateHz));
        }

        if (!double.IsFinite(firstSampleOffsetSeconds))
        {
            throw new ArgumentOutOfRangeException(nameof(firstSampleOffsetSeconds));
        }

        if (signalMode != UtSignalMode.Rf)
        {
            throw new ArgumentOutOfRangeException(nameof(signalMode), "Only bipolar RF is supported.");
        }

        PhysicalChannelId = physicalChannelId;
        SampleCount = sampleCount;
        SampleRateHz = sampleRateHz;
        FirstSampleOffsetSeconds = firstSampleOffsetSeconds;
        SignalMode = signalMode;
    }

    public PhysicalChannelId PhysicalChannelId { get; }
    public int SampleCount { get; }
    public double SampleRateHz { get; }
    public double FirstSampleOffsetSeconds { get; }
    public UtSignalMode SignalMode { get; }
}
