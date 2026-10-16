using UTStudio.Contracts.Processing;

namespace UTStudio.Core.Tests.Processing;

internal readonly record struct ReferenceDetection(GateResultStatus Status, GateDetectionEventKind EventKind,
    int? Index, double? Position, double? Amplitude, short? SignedRf, double? Prominence, double? Margin);

internal static class GateScalarReference
{
    internal static ReferenceDetection Evaluate(short[] samples, GateDetectorKind detector, GateSignalDomain domain,
        double? threshold = null, GateHysteresis? hysteresis = null, GateLocalExtremaParameters? local = null)
    {
        double[] values = samples.Select(x => Transform(x, domain)).ToArray();
        return detector switch
        {
            GateDetectorKind.GlobalMaximum => Global(samples, values, domain, true, threshold),
            GateDetectorKind.GlobalMinimum => Global(samples, values, domain, false, threshold),
            GateDetectorKind.AbsoluteMaximum => Global(samples, samples.Select(x => (double)Math.Abs((int)x)).ToArray(),
                GateSignalDomain.Absolute, true, threshold, GateDetectionEventKind.AbsoluteMaximum),
            GateDetectorKind.FirstRisingCrossing => Crossing(samples, values, domain, true, true, threshold, hysteresis),
            GateDetectorKind.LastRisingCrossing => Crossing(samples, values, domain, true, false, threshold, hysteresis),
            GateDetectorKind.FirstFallingCrossing => Crossing(samples, values, domain, false, true, threshold, hysteresis),
            GateDetectorKind.LastFallingCrossing => Crossing(samples, values, domain, false, false, threshold, hysteresis),
            GateDetectorKind.FirstLocalMaximum => Local(samples, values, domain, true, threshold, local!),
            GateDetectorKind.LastLocalMaximum => Local(samples, values, domain, false, threshold, local!),
            _ => throw new ArgumentOutOfRangeException(nameof(detector))
        };
    }

    internal static ReferenceDetection EvaluateWindow(short[] samples, int firstSample, int sampleCount,
        GateDetectorKind detector, GateSignalDomain domain, double? threshold = null,
        GateHysteresis? hysteresis = null, GateLocalExtremaParameters? local = null)
    {
        ReferenceDetection localResult = Evaluate(samples.AsSpan(firstSample, sampleCount).ToArray(), detector,
            domain, threshold, hysteresis, local);
        return localResult with
        {
            Index = localResult.Index + firstSample,
            Position = localResult.Position + firstSample
        };
    }

    internal static (int Index, double Amplitude) GlobalMaximum(ReadOnlySpan<short> samples, GateSignalDomain domain)
    {
        ReferenceDetection result = Evaluate(samples.ToArray(), GateDetectorKind.GlobalMaximum, domain);
        return (result.Index!.Value, result.Amplitude!.Value);
    }

    private static ReferenceDetection Global(short[] samples, double[] values, GateSignalDomain domain,
        bool maximum, double? normalized, GateDetectionEventKind? forcedKind = null)
    {
        int selected = 0;
        for (int i = 1; i < values.Length; i++)
            if (maximum ? values[i] > values[selected] : values[i] < values[selected]) { selected = i; }
        double? margin = normalized.HasValue ? (maximum
            ? values[selected] - Threshold(domain, normalized.Value)
            : Threshold(domain, normalized.Value) - values[selected]) : null;
        GateDetectionEventKind kind = forcedKind ?? (maximum ? GateDetectionEventKind.GlobalMaximum : GateDetectionEventKind.GlobalMinimum);
        return new(margin < 0 ? GateResultStatus.BelowThreshold : GateResultStatus.Detected, kind,
            selected, selected, values[selected], samples[selected], null, margin);
    }

    private static ReferenceDetection Crossing(short[] samples, double[] values, GateSignalDomain domain,
        bool rising, bool first, double? normalized, GateHysteresis? hysteresis)
    {
        double low = Threshold(domain, hysteresis?.LowNormalized ?? normalized!.Value);
        double high = Threshold(domain, hysteresis?.HighNormalized ?? normalized!.Value);
        bool armed = rising ? values[0] <= low : values[0] >= high;
        ReferenceDetection result = new(GateResultStatus.NoEvent, GateDetectionEventKind.Unknown, null, null, null, null, null, null);
        for (int i = 0; i < values.Length - 1; i++)
        {
            if (!armed) { if (rising ? values[i + 1] <= low : values[i + 1] >= high) { armed = true; } continue; }
            double level = rising ? high : low;
            bool hit = rising ? values[i] < level && values[i + 1] >= level : values[i] > level && values[i + 1] <= level;
            if (!hit) { continue; }
            double position = i + ((level - values[i]) / (values[i + 1] - values[i]));
            result = new(GateResultStatus.Detected, rising ? GateDetectionEventKind.RisingCrossing : GateDetectionEventKind.FallingCrossing,
                i, position, level, samples[i], null, null);
            if (first) { return result; }
            armed = false;
        }
        return result;
    }

    private static ReferenceDetection Local(short[] samples, double[] values, GateSignalDomain domain,
        bool first, double? normalized, GateLocalExtremaParameters p)
    {
        List<(int Index, double Amplitude, double Prominence)> candidates = [];
        for (int a = 0; a < values.Length;)
        {
            int b = a; while (b + 1 < values.Length && values[b + 1] == values[a]) { b++; }
            if (a - p.NeighborhoodRadiusSamples >= 0 && b + p.NeighborhoodRadiusSamples < values.Length &&
                values.Skip(a - p.NeighborhoodRadiusSamples).Take(p.NeighborhoodRadiusSamples).All(x => x < values[a]) &&
                values.Skip(b + 1).Take(p.NeighborhoodRadiusSamples).All(x => x < values[a]))
            {
                double left = values[a]; for (int i = a - 1; i >= 0 && values[i] <= values[a]; i--) { left = Math.Min(left, values[i]); }
                double right = values[a]; for (int i = b + 1; i < values.Length && values[i] <= values[a]; i++) { right = Math.Min(right, values[i]); }
                double prominence = values[a] - Math.Max(left, right);
                if (prominence >= p.MinimumProminenceCounts)
                { candidates.Add((a + ((b - a) / 2), values[a], prominence)); }
            }
            a = b + 1;
        }
        List<(int Index, double Amplitude, double Prominence)> accepted = [];
        foreach (var candidate in candidates.OrderByDescending(x => x.Amplitude).ThenByDescending(x => x.Prominence).ThenBy(x => x.Index))
            if (accepted.All(x => Math.Abs(x.Index - candidate.Index) >= p.MinimumDistanceSamples)) { accepted.Add(candidate); }
        if (accepted.Count == 0) { return new(GateResultStatus.NoEvent, GateDetectionEventKind.Unknown, null, null, null, null, null, null); }
        var selected = first ? accepted.MinBy(x => x.Index) : accepted.MaxBy(x => x.Index);
        double? margin = normalized.HasValue ? selected.Amplitude - Threshold(domain, normalized.Value) : null;
        return new(margin < 0 ? GateResultStatus.BelowThreshold : GateResultStatus.Detected,
            GateDetectionEventKind.LocalMaximum, selected.Index, selected.Index, selected.Amplitude,
            samples[selected.Index], selected.Prominence, margin);
    }

    private static double Threshold(GateSignalDomain domain, double normalized) => domain switch
    {
        GateSignalDomain.RawSigned => -32768d + 65535d * normalized,
        GateSignalDomain.Positive => 32767d * normalized,
        GateSignalDomain.NegativeMagnitude or GateSignalDomain.Absolute => 32768d * normalized,
        _ => throw new ArgumentOutOfRangeException(nameof(domain))
    };

    private static double Transform(short value, GateSignalDomain domain) => domain switch
    {
        GateSignalDomain.RawSigned => value,
        GateSignalDomain.Positive => Math.Max((int)value, 0),
        GateSignalDomain.NegativeMagnitude => Math.Max(-(int)value, 0),
        GateSignalDomain.Absolute => Math.Abs((int)value),
        _ => throw new ArgumentOutOfRangeException(nameof(domain))
    };
}
