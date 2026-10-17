using System.Security.Cryptography;
using System.Text;
using UTStudio.Contracts.Inspection;
using UTStudio.Contracts.Processing;
using UTStudio.Domain.Processing;

namespace UTStudio.Processing.Core;

public sealed class GateEvaluator
{
    private const int CancellationStride = 256;
    private static readonly GateScratchPool SharedScratchPool = new();
    private readonly IGateScratchPool _scratchPool;
    private readonly Action<GateEvaluationCheckpoint>? _checkpoint;

    public GateEvaluator() : this(SharedScratchPool, null) { }
    public GateEvaluator(GateScratchPool scratchPool) : this(scratchPool, null) { }
    internal GateEvaluator(IGateScratchPool scratchPool, Action<GateEvaluationCheckpoint>? checkpoint = null)
    { _scratchPool = scratchPool ?? throw new ArgumentNullException(nameof(scratchPool)); _checkpoint = checkpoint; }
    public GateScratchDiagnostics ScratchDiagnostics => _scratchPool.Snapshot;

    public GateEvaluationResult Evaluate(CompiledGatePlan plan, ConfirmedGateTraceDescriptor trace,
        ReadOnlySpan<short> samples, CancellationToken cancellationToken = default)
    {
        if (plan is null || trace is null)
        { return GateEvaluationResult.Failed(GateEvaluationErrorCode.InvalidInput, "A compiled plan and confirmed trace are required."); }
        TraceDescriptor descriptor = trace.Descriptor;
        if (trace.EffectiveQuality == UtTraceQuality.Invalid) { return CompletedTerminal(plan, trace, GateResultStatus.InvalidTrace); }
        if (samples.Length != descriptor.SampleCount)
        { return GateEvaluationResult.Failed(GateEvaluationErrorCode.InvalidInput, "Sample length must match the confirmed descriptor."); }
        if (IsCancelled(GateEvaluationCheckpoint.BeforeStart, cancellationToken)) { return Cancelled(plan, trace); }

        bool needsScratch = plan.DeclaredGates.Any(x => x.Definition.Detector.Kind is
            GateDetectorKind.FirstLocalMaximum or GateDetectorKind.LastLocalMaximum);
        GateScratchLease? scratch = null;
        GateEvaluationResult? outcome = null;
        try
        {
            if (needsScratch && !_scratchPool.TryRent(samples.Length, out scratch))
            { return GateEvaluationResult.Failed(GateEvaluationErrorCode.ScratchUnavailable, "Bounded local-extrema scratch is unavailable."); }
            ThrowIfCancelled(GateEvaluationCheckpoint.AfterScratchRent, cancellationToken);
            GateEvaluationEntry?[] entries = new GateEvaluationEntry?[plan.DeclaredGates.Count];
            double?[] detectionTimes = new double?[plan.DeclaredGates.Count];
            foreach (int declaredIndex in plan.TopologicalOrder)
            {
                ThrowIfCancelled(GateEvaluationCheckpoint.BetweenGates, cancellationToken);
                CompiledGate gate = plan.DeclaredGates[declaredIndex];
                if (gate.DependencyIndex >= 0 && entries[gate.DependencyIndex] is { Status: not GateResultStatus.Detected } causal)
                {
                    entries[declaredIndex] = Empty(plan, trace, gate, GateResultStatus.DependencyNotDetected,
                        causal.GateId, causal.Status);
                    continue;
                }
                entries[declaredIndex] = EvaluateGate(plan, trace, gate, samples, scratch?.Buffer, entries,
                    detectionTimes, cancellationToken, out detectionTimes[declaredIndex]);
            }
            ThrowIfCancelled(GateEvaluationCheckpoint.BeforePublish, cancellationToken);
            var batch = CreateBatch(plan, trace, entries.Select(x => x ?? throw new InvalidOperationException("Incomplete evaluation.")));
            ThrowIfCancelled(GateEvaluationCheckpoint.BeforePublish, cancellationToken);
            outcome = GateEvaluationResult.Completed(batch);
        }
        catch (OperationCanceledException) { outcome = Cancelled(plan, trace); }
        catch (Exception error)
        {
            outcome = GateEvaluationResult.Failed(GateEvaluationErrorCode.InternalFailure,
                $"Gate evaluation failed: {error.GetType().Name}.");
        }
        finally
        {
            if (scratch is not null)
            {
                try { _scratchPool.Return(scratch); }
                catch (Exception error) { outcome = ComposeCleanupFailure(outcome, error); }
            }
        }
        return outcome ?? GateEvaluationResult.Failed(GateEvaluationErrorCode.InternalFailure, "No evaluation outcome was produced.");
    }

    public static double ConvertNormalizedThreshold(GateSignalDomain domain, double normalized)
    {
        if (!double.IsFinite(normalized) || normalized is < 0 or > 1) { throw new ArgumentOutOfRangeException(nameof(normalized)); }
        return domain switch
        {
            GateSignalDomain.RawSigned => -32768d + 65535d * normalized,
            GateSignalDomain.Positive => 32767d * normalized,
            GateSignalDomain.NegativeMagnitude or GateSignalDomain.Absolute => 32768d * normalized,
            _ => throw new ArgumentOutOfRangeException(nameof(domain))
        };
    }

    private GateEvaluationEntry EvaluateGate(CompiledGatePlan plan, ConfirmedGateTraceDescriptor trace,
        CompiledGate gate, ReadOnlySpan<short> samples, GateScratchBuffer? scratch,
        GateEvaluationEntry?[] entries, double?[] detectionTimes, CancellationToken token,
        out double? internalDetectionTime)
    {
        internalDetectionTime = null;
        double start = gate.DependencyIndex < 0
            ? gate.Definition.Window.StartOffsetSeconds
            : detectionTimes[gate.DependencyIndex]!.Value + gate.Definition.Window.StartOffsetSeconds;
        double end = start + gate.Definition.Window.DurationSeconds;
        WindowResolution resolution = TryResolveWindow(trace.Descriptor, samples.Length, start, end,
            out int first, out int endExclusive);
        if (resolution != WindowResolution.Resolved)
        {
            return Empty(plan, trace, gate,
                resolution == WindowResolution.Empty ? GateResultStatus.EmptyResolvedWindow : GateResultStatus.WindowOutOfRange);
        }

        Detection detection = gate.Definition.Detector.Kind switch
        {
            GateDetectorKind.GlobalMaximum => Global(samples, first, endExclusive, gate.Definition.SignalDomain, true, gate.Definition.Detector.Threshold, token),
            GateDetectorKind.GlobalMinimum => Global(samples, first, endExclusive, gate.Definition.SignalDomain, false, gate.Definition.Detector.Threshold, token),
            GateDetectorKind.AbsoluteMaximum => AbsoluteMaximum(samples, first, endExclusive, gate.Definition.Detector.Threshold, token),
            GateDetectorKind.FirstRisingCrossing => Crossing(samples, first, endExclusive, gate.Definition, true, true, token),
            GateDetectorKind.LastRisingCrossing => Crossing(samples, first, endExclusive, gate.Definition, true, false, token),
            GateDetectorKind.FirstFallingCrossing => Crossing(samples, first, endExclusive, gate.Definition, false, true, token),
            GateDetectorKind.LastFallingCrossing => Crossing(samples, first, endExclusive, gate.Definition, false, false, token),
            GateDetectorKind.FirstLocalMaximum => LocalMaximum(samples, first, endExclusive, gate.Definition, true, scratch!, token),
            GateDetectorKind.LastLocalMaximum => LocalMaximum(samples, first, endExclusive, gate.Definition, false, scratch!, token),
            _ => throw new InvalidOperationException("Unsupported compiled detector.")
        };
        if (!detection.Detected)
        { return Empty(plan, trace, gate, detection.Status, metrics: new(detection.Prominence, detection.Margin)); }
        double position = detection.Position;
        double absoluteTime = trace.Descriptor.FirstSampleOffsetSeconds + position / trace.Descriptor.SampleRateHz;
        internalDetectionTime = absoluteTime;
        double tof = absoluteTime;
        return Create(plan, trace, gate, GateResultStatus.Detected, detection.EventKind, detection.SampleIndex,
            position, absoluteTime, detection.Amplitude, detection.SignedRfAmplitude, tof, absoluteTime - start,
            gate.DependencyIndex >= 0 ? entries[gate.DependencyIndex]!.GateId : null,
            gate.DependencyIndex >= 0 ? entries[gate.DependencyIndex]!.Status : null,
            new(detection.Prominence, detection.Margin));
    }

    private Detection Global(ReadOnlySpan<short> samples, int first, int end, GateSignalDomain domain,
        bool maximum, GateThreshold? acceptance, CancellationToken token)
    {
        int selected = first; double value = Transform(samples[first], domain);
        for (int i = first + 1; i < end; i++)
        {
            CheckCancellation(i, token); double candidate = Transform(samples[i], domain);
            if (maximum ? candidate > value : candidate < value) { value = candidate; selected = i; }
        }
        if (acceptance is not null)
        {
            double threshold = ConvertNormalizedThreshold(domain, acceptance.NormalizedValue);
            double margin = maximum ? value - threshold : threshold - value;
            if (margin < 0)
            {
                return Detection.Rejected(maximum ? GateDetectionEventKind.GlobalMaximum : GateDetectionEventKind.GlobalMinimum,
                selected, value, samples[selected], null, margin);
            }
            return Detection.Found(maximum ? GateDetectionEventKind.GlobalMaximum : GateDetectionEventKind.GlobalMinimum,
                selected, selected, value, samples[selected], null, margin);
        }
        return Detection.Found(maximum ? GateDetectionEventKind.GlobalMaximum : GateDetectionEventKind.GlobalMinimum,
            selected, selected, value, samples[selected]);
    }

    private Detection AbsoluteMaximum(ReadOnlySpan<short> samples, int first, int end,
        GateThreshold? acceptance, CancellationToken token)
    {
        int selected = first; double value = Math.Abs((int)samples[first]);
        for (int i = first + 1; i < end; i++)
        {
            CheckCancellation(i, token); double candidate = Math.Abs((int)samples[i]);
            if (candidate > value) { value = candidate; selected = i; }
        }
        if (acceptance is not null)
        {
            double margin = value - ConvertNormalizedThreshold(GateSignalDomain.Absolute, acceptance.NormalizedValue);
            if (margin < 0)
            {
                return Detection.Rejected(GateDetectionEventKind.AbsoluteMaximum,
                selected, value, samples[selected], null, margin);
            }
            return Detection.Found(GateDetectionEventKind.AbsoluteMaximum, selected, selected, value,
                samples[selected], null, margin);
        }
        return Detection.Found(GateDetectionEventKind.AbsoluteMaximum, selected, selected, value, samples[selected]);
    }

    private Detection Crossing(ReadOnlySpan<short> samples, int first, int end, GateDefinition gate,
        bool rising, bool takeFirst, CancellationToken token)
    {
        if (end - first < 2) { return Detection.None(GateResultStatus.NoEvent); }
        double low, high;
        if (gate.Detector.Hysteresis is { } hysteresis)
        {
            low = ConvertNormalizedThreshold(gate.SignalDomain, hysteresis.LowNormalized);
            high = ConvertNormalizedThreshold(gate.SignalDomain, hysteresis.HighNormalized);
        }
        else
        {
            low = high = ConvertNormalizedThreshold(gate.SignalDomain, gate.Detector.Threshold!.NormalizedValue);
        }
        bool armed = rising ? Transform(samples[first], gate.SignalDomain) <= low : Transform(samples[first], gate.SignalDomain) >= high;
        Detection last = Detection.None(GateResultStatus.NoEvent);
        for (int i = first; i < end - 1; i++)
        {
            CheckCancellation(i, token);
            double left = Transform(samples[i], gate.SignalDomain);
            double right = Transform(samples[i + 1], gate.SignalDomain);
            if (!armed)
            {
                if (rising ? right <= low : right >= high) { armed = true; }
                continue;
            }
            double crossing = rising ? high : low;
            bool hit = rising ? left < crossing && right >= crossing : left > crossing && right <= crossing;
            if (!hit) { continue; }
            double fraction = (crossing - left) / (right - left);
            last = Detection.Found(rising ? GateDetectionEventKind.RisingCrossing : GateDetectionEventKind.FallingCrossing,
                i, i + fraction, crossing, samples[i]);
            if (takeFirst) { return last; }
            armed = false;
        }
        return last;
    }

    private Detection LocalMaximum(ReadOnlySpan<short> samples, int first, int end, GateDefinition gate,
        bool takeFirst, GateScratchBuffer scratch, CancellationToken token)
    {
        GateLocalExtremaParameters p = gate.Detector.LocalExtrema!;
        int length = end - first;
        for (int i = 0; i < length; i++)
        { CheckCancellation(i, token, GateEvaluationCheckpoint.LocalTreeBuild); scratch.Values[i] = Transform(samples[first + i], gate.SignalDomain); }
        BuildTree(scratch, length, maximum: true, token);
        int count = 0;
        for (int a = 0; a < length;)
        {
            CheckCancellation(a, token, GateEvaluationCheckpoint.LocalNeighborhood);
            int b = a; double peak = scratch.Values[a];
            while (b + 1 < length && scratch.Values[b + 1] == peak)
            { b++; CheckCancellation(b, token, GateEvaluationCheckpoint.LocalNeighborhood); }
            int radius = p.NeighborhoodRadiusSamples;
            if (a - radius >= 0 && b + radius < length &&
                peak > QueryTree(scratch.Tree, scratch.SampleCapacity, a - radius, a, maximum: true) &&
                peak > QueryTree(scratch.Tree, scratch.SampleCapacity, b + 1, b + radius + 1, maximum: true))
            {
                if (count >= scratch.Candidates.Length) { throw new InvalidOperationException("Local candidate scratch capacity exceeded."); }
                scratch.Candidates[count++] = new GateCandidate
                { Start = a, End = b, Index = a + (b - a) / 2, Amplitude = peak, LeftGreater = -1, RightGreater = length };
            }
            a = b + 1;
        }
        if (count == 0) { return Detection.None(GateResultStatus.NoEvent); }

        AssignGreaterBoundaries(scratch, count, length, token);
        BuildTree(scratch, length, maximum: false, token);
        int retained = 0;
        for (int i = 0; i < count; i++)
        {
            CheckCancellation(i, token, GateEvaluationCheckpoint.LocalProminence);
            GateCandidate candidate = scratch.Candidates[i];
            double leftMin = QueryTree(scratch.Tree, scratch.SampleCapacity, candidate.LeftGreater + 1, candidate.Start, maximum: false);
            double rightMin = QueryTree(scratch.Tree, scratch.SampleCapacity, candidate.End + 1, candidate.RightGreater, maximum: false);
            candidate.Prominence = candidate.Amplitude - Math.Max(leftMin, rightMin);
            if (candidate.Prominence >= p.MinimumProminenceCounts) { scratch.Candidates[retained++] = candidate; }
        }
        if (retained == 0) { return Detection.None(GateResultStatus.NoEvent); }

        SortCandidates(scratch.Candidates, retained, token);
        ClearFenwick(scratch.Fenwick, length + 1, token);
        for (int i = 0; i < retained; i++)
        {
            CheckCancellation(i, token, GateEvaluationCheckpoint.LocalSuppression);
            ref GateCandidate candidate = ref scratch.Candidates[i];
            int from = Math.Max(0, candidate.Index - p.MinimumDistanceSamples + 1);
            int to = Math.Min(length - 1, candidate.Index + p.MinimumDistanceSamples - 1);
            candidate.Suppressed = FenwickSum(scratch.Fenwick, to + 1) - FenwickSum(scratch.Fenwick, from) > 0;
            if (!candidate.Suppressed) { FenwickAdd(scratch.Fenwick, candidate.Index + 1, length, 1); }
        }
        int selected = -1;
        for (int i = 0; i < retained; i++)
        {
            CheckCancellation(i, token, GateEvaluationCheckpoint.LocalSuppression);
            if (scratch.Candidates[i].Suppressed) { continue; }
            if (selected < 0 || (takeFirst ? scratch.Candidates[i].Index < scratch.Candidates[selected].Index : scratch.Candidates[i].Index > scratch.Candidates[selected].Index))
            { selected = i; }
        }
        GateCandidate result = scratch.Candidates[selected];
        double? margin = gate.Detector.Threshold is { } acceptance
            ? result.Amplitude - ConvertNormalizedThreshold(gate.SignalDomain, acceptance.NormalizedValue) : null;
        if (margin < 0)
        {
            return Detection.Rejected(GateDetectionEventKind.LocalMaximum,
            first + result.Index, result.Amplitude, samples[first + result.Index], result.Prominence, margin);
        }
        return Detection.Found(GateDetectionEventKind.LocalMaximum, first + result.Index, first + result.Index,
            result.Amplitude, samples[first + result.Index], result.Prominence, margin);
    }

    private void BuildTree(GateScratchBuffer scratch, int length, bool maximum, CancellationToken token)
    {
        int capacity = scratch.SampleCapacity;
        double identity = maximum ? double.NegativeInfinity : double.PositiveInfinity;
        for (int i = 0; i < capacity; i++)
        { CheckCancellation(i, token, GateEvaluationCheckpoint.LocalTreeBuild); scratch.Tree[capacity + i] = i < length ? scratch.Values[i] : identity; }
        for (int i = capacity - 1; i > 0; i--)
        {
            CheckCancellation(capacity - i, token, GateEvaluationCheckpoint.LocalTreeBuild);
            scratch.Tree[i] = maximum ? Math.Max(scratch.Tree[i << 1], scratch.Tree[(i << 1) | 1])
                : Math.Min(scratch.Tree[i << 1], scratch.Tree[(i << 1) | 1]);
        }
    }

    private static double QueryTree(double[] tree, int length, int from, int to, bool maximum)
    {
        double result = maximum ? double.NegativeInfinity : double.PositiveInfinity;
        for (int left = from + length, right = to + length; left < right; left >>= 1, right >>= 1)
        {
            if ((left & 1) != 0) { result = maximum ? Math.Max(result, tree[left]) : Math.Min(result, tree[left]); left++; }
            if ((right & 1) != 0) { --right; result = maximum ? Math.Max(result, tree[right]) : Math.Min(result, tree[right]); }
        }
        return result;
    }

    private void AssignGreaterBoundaries(GateScratchBuffer scratch, int count, int length, CancellationToken token)
    {
        for (int i = 0; i < count; i++)
        {
            CheckCancellation(i, token, GateEvaluationCheckpoint.LocalProminence);
            ref GateCandidate candidate = ref scratch.Candidates[i];
            candidate.LeftGreater = FindGreater(scratch.Tree, scratch.SampleCapacity, 1, 0, scratch.SampleCapacity,
                0, candidate.Start, candidate.Amplitude, rightmost: true);
            candidate.RightGreater = FindGreater(scratch.Tree, scratch.SampleCapacity, 1, 0, scratch.SampleCapacity,
                candidate.End + 1, length, candidate.Amplitude, rightmost: false);
            if (candidate.RightGreater > length) { candidate.RightGreater = length; }
        }
    }

    private static int FindGreater(double[] tree, int length, int node, int nodeStart, int nodeEnd,
        int queryStart, int queryEnd, double threshold, bool rightmost)
    {
        if (nodeEnd <= queryStart || nodeStart >= queryEnd || tree[node] <= threshold) { return rightmost ? -1 : length; }
        if (nodeEnd - nodeStart == 1) { return nodeStart; }
        int middle = nodeStart + ((nodeEnd - nodeStart) / 2);
        if (rightmost)
        {
            int right = FindGreater(tree, length, (node << 1) | 1, middle, nodeEnd,
                queryStart, queryEnd, threshold, true);
            return right >= 0 ? right : FindGreater(tree, length, node << 1, nodeStart, middle,
                queryStart, queryEnd, threshold, true);
        }
        int left = FindGreater(tree, length, node << 1, nodeStart, middle,
            queryStart, queryEnd, threshold, false);
        return left < length ? left : FindGreater(tree, length, (node << 1) | 1, middle, nodeEnd,
            queryStart, queryEnd, threshold, false);
    }

    private void ClearFenwick(int[] fenwick, int count, CancellationToken token)
    { for (int i = 0; i < count; i++) { CheckCancellation(i, token, GateEvaluationCheckpoint.LocalSuppression); fenwick[i] = 0; } }
    private static int FenwickSum(int[] tree, int index)
    { int sum = 0; for (; index > 0; index -= index & -index) { sum += tree[index]; } return sum; }
    private static void FenwickAdd(int[] tree, int index, int length, int value)
    { for (; index <= length; index += index & -index) { tree[index] += value; } }

    private static WindowResolution TryResolveWindow(TraceDescriptor descriptor, int sampleCount, double start, double end,
        out int first, out int endExclusive)
    {
        first = endExclusive = 0;
        double axisStart = descriptor.FirstSampleOffsetSeconds;
        double axisEnd = axisStart + sampleCount / descriptor.SampleRateHz;
        if (!double.IsFinite(start) || !double.IsFinite(end) || end <= start || start < axisStart || end > axisEnd)
        { return WindowResolution.OutOfRange; }
        double firstValue = Math.Ceiling((start - axisStart) * descriptor.SampleRateHz);
        double endValue = Math.Ceiling((end - axisStart) * descriptor.SampleRateHz);
        if (!double.IsFinite(firstValue) || !double.IsFinite(endValue) || firstValue < 0 || endValue > sampleCount)
        { return WindowResolution.OutOfRange; }
        if (firstValue >= endValue) { return WindowResolution.Empty; }
        try { first = checked((int)firstValue); endExclusive = checked((int)endValue); }
        catch (OverflowException) { return WindowResolution.OutOfRange; }
        return first < endExclusive ? WindowResolution.Resolved : WindowResolution.Empty;
    }

    private static double Transform(short value, GateSignalDomain domain) => domain switch
    {
        GateSignalDomain.RawSigned => value,
        GateSignalDomain.Positive => Math.Max((int)value, 0),
        GateSignalDomain.NegativeMagnitude => Math.Max(-(int)value, 0),
        GateSignalDomain.Absolute => Math.Abs((int)value),
        _ => throw new InvalidOperationException("Unsupported compiled domain.")
    };

    private void CheckCancellation(int index, CancellationToken token,
        GateEvaluationCheckpoint checkpoint = GateEvaluationCheckpoint.InWindow)
    { if ((index & (CancellationStride - 1)) == 0) { ThrowIfCancelled(checkpoint, token); } }

    private bool IsCancelled(GateEvaluationCheckpoint point, CancellationToken token)
    { _checkpoint?.Invoke(point); return token.IsCancellationRequested; }
    private void ThrowIfCancelled(GateEvaluationCheckpoint point, CancellationToken token)
    { _checkpoint?.Invoke(point); token.ThrowIfCancellationRequested(); }

    private static GateEvaluationResult Cancelled(CompiledGatePlan plan, ConfirmedGateTraceDescriptor trace) =>
        GateEvaluationResult.Cancelled(CreateBatch(plan, trace, plan.DeclaredGates.Select(g =>
            Empty(plan, trace, g, GateResultStatus.Cancelled))));
    private static GateEvaluationResult CompletedTerminal(CompiledGatePlan plan, ConfirmedGateTraceDescriptor trace,
        GateResultStatus status) => GateEvaluationResult.Completed(CreateBatch(plan, trace,
            plan.DeclaredGates.Select(g => Empty(plan, trace, g, status))));

    private static GateEvaluationBatch CreateBatch(CompiledGatePlan plan, ConfirmedGateTraceDescriptor trace,
        IEnumerable<GateEvaluationEntry> entries) => new(trace, CreateEvaluationSignature(plan, trace), entries);

    private static GateEvaluationSignature CreateEvaluationSignature(CompiledGatePlan plan, ConfirmedGateTraceDescriptor trace)
    {
        using MemoryStream stream = new();
        using (BinaryWriter writer = new(stream, new UTF8Encoding(false, true), true))
        {
            Write(writer, "UTSTUDIO-GATE-EVALUATION");
            writer.Write(GateEvaluationBatch.ProcessingContractVersion);
            writer.Write(plan.Signature.Bytes.Span);
            var identity = trace.ConfirmedIdentity;
            writer.Write(identity.Stream.InspectionId.Value.ToByteArray());
            writer.Write(identity.Stream.AcquisitionRunId.Value.ToByteArray());
            Write(writer, identity.Stream.SourceId.Value);
            writer.Write(identity.Stream.PhysicalChannelId.Value);
            writer.Write(identity.Stream.BeamId.HasValue);
            if (identity.Stream.BeamId is { } beam) { writer.Write(beam.Value); }
            writer.Write(identity.Stream.FocalLawId.HasValue);
            if (identity.Stream.FocalLawId is { } law) { writer.Write(law.Value); }
            writer.Write(identity.AcquisitionIndex);
            writer.Write(trace.SourceInspectionRevision.Value);
            writer.Write(trace.Descriptor.SampleCount);
            writer.Write(CanonicalDoubleBits(trace.Descriptor.SampleRateHz));
            writer.Write(CanonicalDoubleBits(trace.Descriptor.FirstSampleOffsetSeconds));
            writer.Write((int)trace.Descriptor.SampleEncoding);
            writer.Write((int)trace.EffectiveQuality);
            writer.Write((int)trace.Descriptor.Provenance);
        }
        return new(SHA256.HashData(stream.GetBuffer().AsSpan(0, checked((int)stream.Length))));
    }

    private static void Write(BinaryWriter writer, string value)
    { byte[] bytes = Encoding.UTF8.GetBytes(value); writer.Write(bytes.Length); writer.Write(bytes); }
    private static long CanonicalDoubleBits(double value) => BitConverter.DoubleToInt64Bits(value == 0d ? 0d : value);
    private static GateEvaluationEntry Empty(CompiledGatePlan plan, ConfirmedGateTraceDescriptor trace,
        CompiledGate gate, GateResultStatus status, GateId? causalGate = null, GateResultStatus? causalStatus = null,
        GateDetectorMetrics? metrics = null) =>
        Create(plan, trace, gate, status, GateDetectionEventKind.Unknown, null, null, null, null, null, null, null,
            causalGate, causalStatus, metrics ?? new(null, null));
    private static GateEvaluationEntry Create(CompiledGatePlan plan, ConfirmedGateTraceDescriptor trace,
        CompiledGate gate, GateResultStatus status, GateDetectionEventKind eventKind, int? sampleIndex,
        double? position, double? time, double? amplitude, short? signedAmplitude, double? tof, double? offset,
        GateId? causalGate, GateResultStatus? causalStatus, GateDetectorMetrics metrics)
    {
        IReadOnlyList<GateMeasureKind> measures = gate.Definition.RequestedMeasures;
        bool amplitudeRequested = measures.Contains(GateMeasureKind.Amplitude);
        bool timeRequested = measures.Contains(GateMeasureKind.DetectionTime);
        bool tofRequested = measures.Contains(GateMeasureKind.TimeOfFlight);
        return new(trace.ConfirmedIdentity,
            trace.SourceInspectionRevision, plan.Source.Id, plan.Source.Revision, plan.Signature,
            plan.Source.SchemaVersion, plan.Source.AlgorithmVersion, gate.Definition.Id, gate.Definition.Revision,
            measures, status, eventKind, timeRequested ? sampleIndex : null, timeRequested ? position : null,
            timeRequested ? time : null, amplitudeRequested ? amplitude : null,
            amplitudeRequested ? signedAmplitude : null, tofRequested ? tof : null,
            timeRequested ? offset : null,
            trace.EffectiveQuality switch { UtTraceQuality.Good => GateResultQuality.Valid, UtTraceQuality.Degraded => GateResultQuality.Degraded, _ => GateResultQuality.Invalid },
            gate.Definition.SignalDomain, trace.Descriptor.Provenance, causalGate, causalStatus,
            DiagnosticFor(status), metrics);
    }

    private readonly record struct Detection(bool Detected, GateResultStatus Status, GateDetectionEventKind EventKind,
        int SampleIndex, double Position, double Amplitude, short SignedRfAmplitude,
        double? Prominence, double? Margin)
    {
        internal static Detection None(GateResultStatus status) => new(false, status, GateDetectionEventKind.Unknown, 0, 0, 0, 0, null, null);
        internal static Detection Rejected(GateDetectionEventKind kind, int index, double amplitude,
            short signed, double? prominence, double? margin) =>
            new(false, GateResultStatus.BelowThreshold, kind, index, index, amplitude, signed, prominence, margin);
        internal static Detection Found(GateDetectionEventKind kind, int index, double position, double amplitude,
            short signed, double? prominence = null, double? margin = null) =>
            new(true, GateResultStatus.Detected, kind, index, position, amplitude, signed, prominence, margin);
    }

    private static GateDiagnosticCode DiagnosticFor(GateResultStatus status) => status switch
    {
        GateResultStatus.Detected => GateDiagnosticCode.None,
        GateResultStatus.BelowThreshold => GateDiagnosticCode.BelowAcceptanceThreshold,
        GateResultStatus.NoEvent => GateDiagnosticCode.NoEvent,
        GateResultStatus.DependencyNotDetected => GateDiagnosticCode.DependencyNotDetected,
        GateResultStatus.InvalidTrace => GateDiagnosticCode.InvalidTrace,
        GateResultStatus.WindowOutOfRange => GateDiagnosticCode.WindowOutsideTrace,
        GateResultStatus.EmptyResolvedWindow => GateDiagnosticCode.EmptyResolvedWindow,
        GateResultStatus.Cancelled => GateDiagnosticCode.Cancelled,
        _ => GateDiagnosticCode.InternalFailure
    };

    private static GateEvaluationResult ComposeCleanupFailure(GateEvaluationResult? outcome, Exception cleanup)
    {
        GateEvaluationError[] errors = FlattenCleanup(cleanup).ToArray();
        if (outcome is null || outcome.Status == GateEvaluationStatus.Completed)
        { return GateEvaluationResult.Failed(errors[0], errors.Skip(1)); }
        GateEvaluationError primary = outcome.Status == GateEvaluationStatus.Cancelled
            ? new(GateEvaluationErrorCode.Cancellation, "Gate evaluation was cancelled.")
            : outcome.PrimaryError ?? new GateEvaluationError(GateEvaluationErrorCode.InternalFailure, "Gate evaluation failed.");
        return GateEvaluationResult.Failed(primary, outcome.SecondaryErrors.Concat(errors));
    }

    private static IEnumerable<GateEvaluationError> FlattenCleanup(Exception error)
    {
        if (error is AggregateException aggregate)
        {
            foreach (Exception inner in aggregate.InnerExceptions)
                foreach (GateEvaluationError flattened in FlattenCleanup(inner)) { yield return flattened; }
            yield break;
        }
        yield return new GateEvaluationError(GateEvaluationErrorCode.ScratchReturnFailed,
            $"Local-extrema scratch return failed: {error.GetType().Name}.");
    }

    private void SortCandidates(GateCandidate[] candidates, int count, CancellationToken token)
    {
        int operations = 0;
        for (int root = count / 2 - 1; root >= 0; root--) { SiftDown(root, count); }
        for (int end = count - 1; end > 0; end--)
        {
            (candidates[0], candidates[end]) = (candidates[end], candidates[0]);
            SiftDown(0, end);
        }
        void SiftDown(int root, int end)
        {
            while (root * 2 + 1 < end)
            {
                CheckCancellation(operations++, token, GateEvaluationCheckpoint.LocalOrdering);
                int child = root * 2 + 1;
                if (child + 1 < end && GateCandidateComparer.Instance.Compare(candidates[child], candidates[child + 1]) < 0)
                { child++; }
                if (GateCandidateComparer.Instance.Compare(candidates[root], candidates[child]) >= 0) { return; }
                (candidates[root], candidates[child]) = (candidates[child], candidates[root]);
                root = child;
            }
        }
    }

    private sealed class GateCandidateComparer : IComparer<GateCandidate>
    {
        internal static readonly GateCandidateComparer Instance = new();
        public int Compare(GateCandidate x, GateCandidate y)
        {
            int amplitude = y.Amplitude.CompareTo(x.Amplitude);
            if (amplitude != 0) { return amplitude; }
            int prominence = y.Prominence.CompareTo(x.Prominence);
            return prominence != 0 ? prominence : x.Index.CompareTo(y.Index);
        }
    }
}

internal enum GateEvaluationCheckpoint
{
    BeforeStart, AfterScratchRent, BetweenGates, InWindow, LocalTreeBuild,
    LocalNeighborhood, LocalProminence, LocalOrdering, LocalSuppression, BeforePublish
}
internal enum WindowResolution { Resolved, OutOfRange, Empty }
