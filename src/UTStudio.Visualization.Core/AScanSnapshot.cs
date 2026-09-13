using System.Collections.ObjectModel;
using UTStudio.Domain.Acquisition;

namespace UTStudio.Visualization.Core;

/// <summary>Independent immutable visual data; contains no UT memory or frame owner.</summary>
public sealed class AScanSnapshot
{
    // Only the projector supplies a newly allocated array, never shared with a caller.
    internal AScanSnapshot(ConventionalUtFrameMetadata metadata, ulong sequence, TimeSpan elapsed,
        ulong version, AScanPoint[] ownedPoints, double minimumTimeSeconds, double maximumTimeSeconds)
    {
        Metadata = metadata;
        Sequence = sequence;
        ElapsedSinceRunStart = elapsed;
        Version = version;
        Points = Array.AsReadOnly(ownedPoints);
        MinimumTimeSeconds = minimumTimeSeconds;
        MaximumTimeSeconds = maximumTimeSeconds;
    }

    public ConventionalUtFrameMetadata Metadata { get; }
    public ulong Sequence { get; }
    public TimeSpan ElapsedSinceRunStart { get; }
    /// <summary>Increasing visual input version; replacement can produce gaps between publications.</summary>
    public ulong Version { get; }
    public int OriginalSampleCount => Metadata.Configuration.SampleCount;
    public ReadOnlyCollection<AScanPoint> Points { get; }
    public double MinimumTimeSeconds { get; }
    public double MaximumTimeSeconds { get; }
    public double MinimumAmplitudePercent => -100;
    public double MaximumAmplitudePercent => 100;
}
