using UTStudio.Domain.Acquisition;

namespace UTStudio.Visualization.Core;

/// <summary>O(N) min/max envelope for display only, not measurement or signal processing.</summary>
public sealed class AScanProjector : IAScanProjector
{
    public AScanProjector(int maximumPoints = 1024)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumPoints, 4);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maximumPoints, 1024);
        MaximumPoints = maximumPoints;
    }

    public int MaximumPoints { get; }

    public AScanSnapshot Project(ConventionalUtFrameMetadata metadata, ulong sequence, TimeSpan elapsedSinceRunStart,
        ReadOnlySpan<short> samples, ulong version = 0)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        ArgumentOutOfRangeException.ThrowIfLessThan(elapsedSinceRunStart, TimeSpan.Zero);
        var configuration = metadata.Configuration;
        if (samples.Length != configuration.SampleCount) { throw new ArgumentException("Expected exactly SampleCount samples.", nameof(samples)); }
        int count = samples.Length;
        double offset = configuration.FirstSampleOffsetSeconds;
        double period = 1 / configuration.SampleRateHz;
        double lastTime = offset + (count - 1) / configuration.SampleRateHz;
        double minimum = count == 1 ? offset - period / 2 : offset;
        double maximum = count == 1 ? offset + period / 2 : lastTime;
        if (!double.IsFinite(period) || !double.IsFinite(minimum) || !double.IsFinite(maximum) ||
            maximum <= minimum || (count > 1 && (offset + period <= offset || lastTime - period >= lastTime)))
        {
            throw new ArgumentException("The sample time axis is not representable for visualization.", nameof(metadata));
        }

        var points = new List<AScanPoint>(Math.Min(count, MaximumPoints));
        if (count <= MaximumPoints)
        {
            for (int index = 0; index < count; index++) { points.Add(Point(index, samples[index])); }
        }
        else
        {
            points.Add(Point(0, samples[0]));
            int groups = (MaximumPoints - 2) / 2;
            int interior = count - 2;
            for (int group = 0; group < groups; group++)
            {
                int start = 1 + group * interior / groups;
                int end = 1 + (group + 1) * interior / groups;
                int minIndex = start;
                int maxIndex = start;
                for (int index = start + 1; index < end; index++)
                {
                    if (samples[index] < samples[minIndex]) { minIndex = index; }
                    if (samples[index] > samples[maxIndex]) { maxIndex = index; }
                }

                int first = Math.Min(minIndex, maxIndex);
                int second = Math.Max(minIndex, maxIndex);
                points.Add(Point(first, samples[first]));
                if (first != second) { points.Add(Point(second, samples[second])); }
            }
            points.Add(Point(count - 1, samples[count - 1]));
        }

        return new AScanSnapshot(metadata, sequence, elapsedSinceRunStart, version, points.ToArray(), minimum, maximum);

        AScanPoint Point(int index, short sample) => new(offset + index / configuration.SampleRateHz, 100.0 * sample / 32768);
    }
}
