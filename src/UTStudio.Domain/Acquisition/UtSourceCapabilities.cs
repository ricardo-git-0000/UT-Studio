using System.Collections.ObjectModel;

namespace UTStudio.Domain.Acquisition;

/// <summary>Independent RF source limits, not a guarantee that their maxima work together.</summary>
/// <remarks>Queue capacity and buffer counts are private source configuration, not capabilities.</remarks>
public sealed class UtSourceCapabilities
{
    public UtSourceCapabilities(
        IEnumerable<PhysicalChannelId> physicalChannels,
        int maxSampleCount,
        double maxSampleRateHz,
        bool canPauseProduction)
    {
        ArgumentNullException.ThrowIfNull(physicalChannels);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxSampleCount, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(
            maxSampleCount, ConventionalAcquisitionConfiguration.MaximumSampleCount);
        if (!double.IsFinite(maxSampleRateHz) || maxSampleRateHz <= 0 ||
            maxSampleRateHz > ConventionalAcquisitionConfiguration.MaximumSampleRateHz)
        {
            throw new ArgumentOutOfRangeException(nameof(maxSampleRateHz));
        }

        var channels = physicalChannels.ToArray();
        if (channels.Length == 0 || channels.Distinct().Count() != channels.Length)
        {
            throw new ArgumentException("Channels must be nonempty and unique.", nameof(physicalChannels));
        }

        PhysicalChannels = Array.AsReadOnly(channels);
        MaxSampleCount = maxSampleCount;
        MaxSampleRateHz = maxSampleRateHz;
        CanPauseProduction = canPauseProduction;
    }

    public ReadOnlyCollection<PhysicalChannelId> PhysicalChannels { get; }
    public int MaxSampleCount { get; }
    public double MaxSampleRateHz { get; }

    /// <summary>Whether production can safely wait for downstream capacity.</summary>
    public bool CanPauseProduction { get; }
}
