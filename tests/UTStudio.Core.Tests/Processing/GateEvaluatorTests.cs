using UTStudio.Contracts.Inspection;
using UTStudio.Contracts.Processing;
using UTStudio.Processing.Core;

namespace UTStudio.Core.Tests.Processing;

[TestClass]
public sealed class GateEvaluatorTests
{
    [TestMethod]
    public void ThresholdConversionIsExactForEveryDomainAndRejectsInvalidValues()
    {
        CollectionAssert.AreEqual(new[] { -32768d, -.5d, 32767d }, new[] { 0d, .5d, 1d }
            .Select(x => GateEvaluator.ConvertNormalizedThreshold(GateSignalDomain.RawSigned, x)).ToArray());
        CollectionAssert.AreEqual(new[] { 0d, 16383.5d, 32767d }, new[] { 0d, .5d, 1d }
            .Select(x => GateEvaluator.ConvertNormalizedThreshold(GateSignalDomain.Positive, x)).ToArray());
        foreach (GateSignalDomain domain in new[] { GateSignalDomain.NegativeMagnitude, GateSignalDomain.Absolute })
            CollectionAssert.AreEqual(new[] { 0d, 16384d, 32768d }, new[] { 0d, .5d, 1d }
                .Select(x => GateEvaluator.ConvertNormalizedThreshold(domain, x)).ToArray());
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => GateEvaluator.ConvertNormalizedThreshold(GateSignalDomain.RawSigned, double.NaN));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => GateEvaluator.ConvertNormalizedThreshold(GateSignalDomain.RawSigned, double.PositiveInfinity));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => GateEvaluator.ConvertNormalizedThreshold(GateSignalDomain.RawSigned, double.NegativeInfinity));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => GateEvaluator.ConvertNormalizedThreshold(GateSignalDomain.RawSigned, -double.Epsilon));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => GateEvaluator.ConvertNormalizedThreshold(GateSignalDomain.RawSigned, 1.00001));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => GateEvaluator.ConvertNormalizedThreshold(GateSignalDomain.Unknown, .5));
    }

    [TestMethod]
    public void GlobalDetectorsCoverDomainsExtremesAcceptanceAndTies()
    {
        short[] rf = [short.MinValue, -4, 0, 7, 7, short.MaxValue];
        GateEvaluationEntry max = EvaluateOne(rf, GateDetectorKind.GlobalMaximum, GateSignalDomain.RawSigned);
        AssertDetected(max, 5, 32767);
        GateEvaluationEntry min = EvaluateOne(rf, GateDetectorKind.GlobalMinimum, GateSignalDomain.RawSigned);
        AssertDetected(min, 0, -32768);
        GateEvaluationEntry absolute = EvaluateOne(rf, GateDetectorKind.AbsoluteMaximum, GateSignalDomain.RawSigned);
        AssertDetected(absolute, 0, 32768); Assert.AreEqual(short.MinValue, absolute.SignedRfAmplitudeCounts);
        AssertDetected(EvaluateOne([-3, 5, 5], GateDetectorKind.GlobalMaximum, GateSignalDomain.Positive), 1, 5);
        AssertDetected(EvaluateOne([-8, 4], GateDetectorKind.GlobalMaximum, GateSignalDomain.NegativeMagnitude), 0, 8);
        AssertDetected(EvaluateOne([-8, 8], GateDetectorKind.GlobalMaximum, GateSignalDomain.Absolute), 0, 8);
        GateEvaluationEntry below = EvaluateOne([0, 100], GateDetectorKind.GlobalMaximum,
            GateSignalDomain.Positive, threshold: .5);
        Assert.AreEqual(GateResultStatus.BelowThreshold, below.Status);
        Assert.AreEqual(GateDiagnosticCode.BelowAcceptanceThreshold, below.DiagnosticCode);
        Assert.AreEqual(100 - 16383.5, below.Metrics.ThresholdMarginCounts);
    }

    [TestMethod]
    public void ProductionGlobalsMatchIndependentScalarReferenceExhaustively()
    {
        short[] alphabet = [short.MinValue, -1, 0, 1, short.MaxValue];
        foreach (GateSignalDomain domain in new[] { GateSignalDomain.RawSigned, GateSignalDomain.Positive,
            GateSignalDomain.NegativeMagnitude, GateSignalDomain.Absolute })
        {
            foreach (short a in alphabet)
                foreach (short b in alphabet)
                    foreach (short c in alphabet)
                    {
                        short[] samples = [a, b, c];
                        (int index, double amplitude) = GateScalarReference.GlobalMaximum(samples, domain);
                        GateEvaluationEntry actual = EvaluateOne(samples, GateDetectorKind.GlobalMaximum, domain);
                        Assert.AreEqual(index, actual.SampleIndex);
                        Assert.AreEqual(amplitude, actual.AmplitudeCounts);
                    }
        }
    }

    [TestMethod]
    public void AllDetectorsAndDomainsMatchIndependentScalarReference()
    {
        foreach (GateSignalDomain domain in new[] { GateSignalDomain.RawSigned, GateSignalDomain.Positive,
            GateSignalDomain.NegativeMagnitude, GateSignalDomain.Absolute })
        {
            short low = 0;
            short high = domain == GateSignalDomain.NegativeMagnitude ? (short)-100 : (short)100;
            double threshold = domain == GateSignalDomain.RawSigned ? .5 : .001;
            short[] rising = [low, high, low, high];
            short[] falling = [high, low, high, low];
            short[] local = [low, high, high, low, (short)(domain == GateSignalDomain.NegativeMagnitude ? -50 : 50), low];
            foreach (GateDetectorKind detector in Enum.GetValues<GateDetectorKind>().Where(x => x != GateDetectorKind.Unknown))
            {
                if (detector == GateDetectorKind.AbsoluteMaximum && domain != GateSignalDomain.RawSigned) { continue; }
                short[] samples = detector switch
                {
                    GateDetectorKind.FirstRisingCrossing or GateDetectorKind.LastRisingCrossing => rising,
                    GateDetectorKind.FirstFallingCrossing or GateDetectorKind.LastFallingCrossing => falling,
                    GateDetectorKind.FirstLocalMaximum or GateDetectorKind.LastLocalMaximum => local,
                    _ => local
                };
                GateLocalExtremaParameters? localParameters = detector is GateDetectorKind.FirstLocalMaximum or GateDetectorKind.LastLocalMaximum
                    ? new(1, 1, 2) : null;
                double? detectorThreshold = detector is GateDetectorKind.FirstRisingCrossing or GateDetectorKind.LastRisingCrossing or
                    GateDetectorKind.FirstFallingCrossing or GateDetectorKind.LastFallingCrossing ? threshold : null;
                ReferenceDetection expected = GateScalarReference.Evaluate(samples, detector, domain,
                    detectorThreshold, local: localParameters);
                GateEvaluationEntry actual = EvaluateOne(samples, detector, domain,
                    detectorThreshold, local: localParameters);
                AssertComplete(expected, actual, domain, $"{domain}/{detector}");

                if (detector is GateDetectorKind.FirstRisingCrossing or GateDetectorKind.LastRisingCrossing or
                    GateDetectorKind.FirstFallingCrossing or GateDetectorKind.LastFallingCrossing)
                {
                    var hysteresis = domain == GateSignalDomain.RawSigned ? new GateHysteresis(.499, .501) : new(.001, .002);
                    ReferenceDetection hysteresisExpected = GateScalarReference.Evaluate(samples, detector, domain,
                        hysteresis: hysteresis);
                    GateEvaluationEntry hysteresisActual = EvaluateOne(samples, detector, domain, hysteresis: hysteresis);
                    AssertComplete(hysteresisExpected, hysteresisActual, domain, $"{domain}/{detector}/hysteresis");
                }
            }
        }

        static void AssertComplete(ReferenceDetection expected, GateEvaluationEntry actual,
            GateSignalDomain domain, string context)
        {
            Assert.AreEqual(expected.Status, actual.Status, context);
            Assert.AreEqual(expected.EventKind, actual.EventKind, context);
            Assert.AreEqual(expected.Index, actual.SampleIndex, context);
            Assert.AreEqual(expected.Position, actual.FractionalSamplePosition, context);
            Assert.AreEqual(expected.Position / 1_000_000d, actual.DetectionTimeSeconds, context);
            Assert.AreEqual(expected.Position / 1_000_000d, actual.TimeOfFlightSeconds, context);
            Assert.AreEqual(expected.Position / 1_000_000d, actual.OffsetWithinWindowSeconds, context);
            Assert.AreEqual(expected.Amplitude, actual.AmplitudeCounts, context);
            Assert.AreEqual(expected.SignedRf, actual.SignedRfAmplitudeCounts, context);
            Assert.AreEqual(expected.Prominence, actual.Metrics.ProminenceCounts, context);
            Assert.AreEqual(expected.Margin, actual.Metrics.ThresholdMarginCounts, context);
            Assert.AreEqual(expected.Status == GateResultStatus.Detected ? GateDiagnosticCode.None :
                expected.Status == GateResultStatus.BelowThreshold ? GateDiagnosticCode.BelowAcceptanceThreshold :
                GateDiagnosticCode.NoEvent, actual.DiagnosticCode, context);
            Assert.AreEqual(GateResultQuality.Valid, actual.Quality, context);
            Assert.AreEqual(domain, actual.SignalDomain, context);
            Assert.IsNull(actual.CausalGateId, context);
            Assert.IsNull(actual.CausalStatus, context);
            Assert.AreEqual(new InspectionRevision(5), actual.SourceInspectionRevision, context);
            Assert.AreEqual(UTStudio.Contracts.Inspection.UtTraceProvenance.Simulator, actual.Provenance, context);
            Assert.IsFalse(string.IsNullOrEmpty(actual.PlanSignature.Hex), context);
        }
    }

    [TestMethod]
    public void ScalarReferenceCoversIndependentWindowAndAdversarialFullLength()
    {
        short[] windowed = [100, 0, 4, 9, 3, 0, 100];
        GateDefinition gate = GateEvaluationTestData.Gate("A", 4, offsetSeconds: 2e-6);
        GateEvaluationEntry actual = new GateEvaluator().Evaluate(GateEvaluationTestData.Compile(gate),
            GateEvaluationTestData.Trace(windowed.Length), windowed).Batch!.Entries[0];
        ReferenceDetection expected = GateScalarReference.EvaluateWindow(windowed, 2, 4,
            GateDetectorKind.GlobalMaximum, GateSignalDomain.RawSigned);
        Assert.AreEqual(expected.Index, actual.SampleIndex);
        Assert.AreEqual(expected.Position, actual.FractionalSamplePosition);
        Assert.AreEqual(expected.Position / 1_000_000d, actual.DetectionTimeSeconds);
        Assert.AreEqual(expected.Position / 1_000_000d, actual.TimeOfFlightSeconds);
        Assert.AreEqual((expected.Position!.Value - 2) / 1_000_000d,
            actual.OffsetWithinWindowSeconds!.Value, 1e-15);
        Assert.AreEqual(expected.Amplitude, actual.AmplitudeCounts);
        Assert.AreEqual(expected.SignedRf, actual.SignedRfAmplitudeCounts);
        Assert.AreEqual(GateDiagnosticCode.None, actual.DiagnosticCode);
        Assert.AreEqual(GateResultQuality.Valid, actual.Quality);
        Assert.AreEqual(GateSignalDomain.RawSigned, actual.SignalDomain);
        Assert.AreEqual(new InspectionRevision(5), actual.SourceInspectionRevision);
        Assert.AreEqual(UTStudio.Contracts.Inspection.UtTraceProvenance.Simulator, actual.Provenance);
        Assert.IsNull(actual.CausalGateId);
        Assert.IsNull(actual.CausalStatus);

        short[] adverse = Enumerable.Range(0, 65_535).Select(i => (short)(i % 2 == 0 ? 0 : 100)).ToArray();
        var local = new GateLocalExtremaParameters(1, 100, 65_535);
        ReferenceDetection adverseExpected = GateScalarReference.Evaluate(adverse,
            GateDetectorKind.FirstLocalMaximum, GateSignalDomain.RawSigned, local: local);
        GateEvaluationEntry adverseActual = EvaluateOne(adverse, GateDetectorKind.FirstLocalMaximum,
            GateSignalDomain.RawSigned, local: local);
        Assert.AreEqual(adverseExpected, new ReferenceDetection(adverseActual.Status, adverseActual.EventKind,
            adverseActual.SampleIndex, adverseActual.FractionalSamplePosition, adverseActual.AmplitudeCounts,
            adverseActual.SignedRfAmplitudeCounts, adverseActual.Metrics.ProminenceCounts,
            adverseActual.Metrics.ThresholdMarginCounts));
    }

    [TestMethod]
    public void CrossingDetectorsHandleExactInterpolatedFirstLastAndHysteresis()
    {
        GateEvaluationEntry exact = EvaluateOne([-1, 0, 2], GateDetectorKind.FirstRisingCrossing,
            GateSignalDomain.RawSigned, threshold: 32768d / 65535d);
        Assert.AreEqual(0, exact.SampleIndex); Assert.AreEqual(1d, exact.FractionalSamplePosition);
        GateEvaluationEntry interpolated = EvaluateOne([-2, 1], GateDetectorKind.FirstRisingCrossing,
            GateSignalDomain.RawSigned, threshold: .5);
        Assert.AreEqual(.5d, interpolated.FractionalSamplePosition);
        GateEvaluationEntry lastRise = EvaluateOne([-2, 1, -2, 2], GateDetectorKind.LastRisingCrossing,
            GateSignalDomain.RawSigned, threshold: .5);
        Assert.AreEqual(2, lastRise.SampleIndex);
        GateEvaluationEntry firstFall = EvaluateOne([2, -2, 2, -1], GateDetectorKind.FirstFallingCrossing,
            GateSignalDomain.RawSigned, threshold: .5);
        Assert.AreEqual(0, firstFall.SampleIndex);
        GateEvaluationEntry lastFall = EvaluateOne([2, -2, 2, -1], GateDetectorKind.LastFallingCrossing,
            GateSignalDomain.RawSigned, threshold: .5);
        Assert.AreEqual(2, lastFall.SampleIndex);

        short[] armed = [20_000, 5_000, 30_000, 5_000, 32_000];
        GateEvaluationEntry hysteresis = EvaluateOne(armed, GateDetectorKind.LastRisingCrossing,
            GateSignalDomain.Positive, hysteresis: new(.25, .75));
        Assert.AreEqual(3, hysteresis.SampleIndex);
        Assert.AreEqual(GateResultStatus.NoEvent, EvaluateOne([20_000, 30_000],
            GateDetectorKind.FirstRisingCrossing, GateSignalDomain.Positive, hysteresis: new(.25, .75)).Status);
        GateEvaluationEntry fallingRearmed = EvaluateOne([5_000, 30_000, 5_000, 30_000, 1_000],
            GateDetectorKind.LastFallingCrossing, GateSignalDomain.Positive, hysteresis: new(.25, .75));
        Assert.AreEqual(3, fallingRearmed.SampleIndex);
    }

    [TestMethod]
    public void LocalMaximaHandlePlateausBordersProminenceDistanceAndFirstLast()
    {
        var local = new GateLocalExtremaParameters(1, 2, 1);
        AssertDetected(EvaluateOne([0, 5, 5, 0], GateDetectorKind.FirstLocalMaximum,
            GateSignalDomain.RawSigned, local: local), 1, 5);
        AssertDetected(EvaluateOne([0, 5, 5, 5, 0], GateDetectorKind.FirstLocalMaximum,
            GateSignalDomain.RawSigned, local: local), 2, 5);
        Assert.AreEqual(GateResultStatus.NoEvent, EvaluateOne([5, 0, 0], GateDetectorKind.FirstLocalMaximum,
            GateSignalDomain.RawSigned, local: local).Status);
        Assert.AreEqual(GateResultStatus.NoEvent, EvaluateOne([0, 1, 0], GateDetectorKind.FirstLocalMaximum,
            GateSignalDomain.RawSigned, local: new(1, 2, 1)).Status);
        short[] multiple = [0, 5, 0, 4, 0];
        Assert.AreEqual(1, EvaluateOne(multiple, GateDetectorKind.FirstLocalMaximum,
            GateSignalDomain.RawSigned, local: new(1, 1, 3)).SampleIndex);
        Assert.AreEqual(1, EvaluateOne(multiple, GateDetectorKind.LastLocalMaximum,
            GateSignalDomain.RawSigned, local: new(1, 1, 3)).SampleIndex);
        Assert.AreEqual(3, EvaluateOne(multiple, GateDetectorKind.LastLocalMaximum,
            GateSignalDomain.RawSigned, local: new(1, 1, 2)).SampleIndex);
        GateEvaluationEntry exactProminence = EvaluateOne([0, 5, 0], GateDetectorKind.FirstLocalMaximum,
            GateSignalDomain.RawSigned, local: new(1, 5, 1));
        Assert.AreEqual(5d, exactProminence.Metrics.ProminenceCounts);
    }

    [TestMethod]
    [DataRow(GateSignalDomain.RawSigned)]
    [DataRow(GateSignalDomain.Positive)]
    [DataRow(GateSignalDomain.NegativeMagnitude)]
    [DataRow(GateSignalDomain.Absolute)]
    public void LocalMaximumThresholdFiltersEligibilityBeforeTemporalSelection(GateSignalDomain domain)
    {
        double threshold = NormalizedThreshold(domain, 6);
        GateEvaluationEntry first = EvaluateOne(SignalForDomain(domain, [0, 5, 0, 8, 0]),
            GateDetectorKind.FirstLocalMaximum, domain, threshold: threshold, local: new(1, 1, 1));
        AssertDetected(first, 3, 8);
        Assert.IsGreaterThan(0, first.Metrics.ThresholdMarginCounts!.Value);

        GateEvaluationEntry last = EvaluateOne(SignalForDomain(domain, [0, 8, 0, 5, 0]),
            GateDetectorKind.LastLocalMaximum, domain, threshold: threshold, local: new(1, 1, 1));
        AssertDetected(last, 1, 8);
    }

    [TestMethod]
    [DataRow(GateSignalDomain.RawSigned)]
    [DataRow(GateSignalDomain.Positive)]
    [DataRow(GateSignalDomain.NegativeMagnitude)]
    [DataRow(GateSignalDomain.Absolute)]
    public void LocalMaximumRequiresStrictlyGreaterThanThreshold(GateSignalDomain domain)
    {
        double threshold = NormalizedThreshold(domain, 6);
        GateEvaluationEntry equal = EvaluateOne(SignalForDomain(domain, [0, 6, 0]),
            GateDetectorKind.FirstLocalMaximum, domain, threshold: threshold, local: new(1, 1, 1));
        Assert.AreEqual(GateResultStatus.BelowThreshold, equal.Status);
        Assert.IsNull(equal.SampleIndex);

        GateEvaluationEntry noneEligible = EvaluateOne(SignalForDomain(domain, [0, 5, 0]),
            GateDetectorKind.LastLocalMaximum, domain, threshold: threshold, local: new(1, 1, 1));
        Assert.AreEqual(GateResultStatus.BelowThreshold, noneEligible.Status);
        Assert.IsNull(noneEligible.SampleIndex);
    }

    [TestMethod]
    public void LocalMaximumV3RetainsPlateauProminenceAndDistanceRulesAfterThresholdFiltering()
    {
        double threshold = NormalizedThreshold(GateSignalDomain.RawSigned, 6);
        AssertDetected(EvaluateOne([0, 7, 7, 0], GateDetectorKind.FirstLocalMaximum,
            GateSignalDomain.RawSigned, threshold: threshold, local: new(1, 7, 1)), 1, 7);
        AssertDetected(EvaluateOne([0, 8, 7, 0], GateDetectorKind.FirstLocalMaximum,
            GateSignalDomain.RawSigned, threshold: threshold, local: new(1, 2, 1)), 1, 8);
        AssertDetected(EvaluateOne([0, 9, 0, 8, 0], GateDetectorKind.LastLocalMaximum,
            GateSignalDomain.RawSigned, threshold: threshold, local: new(1, 1, 3)), 1, 9);

        GateEvaluationEntry discardedCannotSuppress = EvaluateOne([0, 5, 0, 8, 0],
            GateDetectorKind.LastLocalMaximum, GateSignalDomain.RawSigned,
            threshold: threshold, local: new(1, 1, 3));
        AssertDetected(discardedCannotSuppress, 3, 8);
    }

    [TestMethod]
    public void LocalMaximumBelowThresholdParentPropagatesDependencyNotDetected()
    {
        double threshold = NormalizedThreshold(GateSignalDomain.RawSigned, 6);
        CompiledGatePlan plan = GateEvaluationTestData.Compile(
            GateEvaluationTestData.Gate("A", 3, detector: GateDetectorKind.FirstLocalMaximum,
                threshold: threshold, local: new(1, 1, 1)),
            GateEvaluationTestData.Gate("B", 1, dependency: "A"));
        GateEvaluationBatch batch = new GateEvaluator().Evaluate(plan,
            GateEvaluationTestData.Trace(3), new short[] { 0, 6, 0 }).Batch!;

        Assert.AreEqual(GateResultStatus.BelowThreshold, batch.Entries[0].Status);
        Assert.AreEqual(GateResultStatus.DependencyNotDetected, batch.Entries[1].Status);
        Assert.AreEqual(new UTStudio.Domain.Processing.GateId("A"), batch.Entries[1].CausalGateId);
        Assert.IsNull(batch.Entries[1].DetectionTimeSeconds);
    }

    [TestMethod]
    public void WindowsAreExactNeverClippedAndSupportSingleSample()
    {
        short[] samples = [1, 9, 3];
        AssertDetected(EvaluateOne(samples, GateDetectorKind.GlobalMaximum, GateSignalDomain.RawSigned,
            durationSamples: 1), 0, 1);
        CompiledGatePlan partial = GateEvaluationTestData.Compile(GateTestPlan.Gate("A", offsetSeconds: -1e-6,
            durationSeconds: 2e-6));
        GateEvaluationEntry partialResult = new GateEvaluator().Evaluate(partial,
            GateEvaluationTestData.Trace(samples.Length), samples).Batch!.Entries[0];
        Assert.AreEqual(GateResultStatus.WindowOutOfRange, partialResult.Status);
        CompiledGatePlan outside = GateEvaluationTestData.Compile(GateTestPlan.Gate("A", offsetSeconds: 10,
            durationSeconds: 1));
        Assert.AreEqual(GateResultStatus.WindowOutOfRange, new GateEvaluator().Evaluate(outside,
            GateEvaluationTestData.Trace(samples.Length), samples).Batch!.Entries[0].Status);
        CompiledGatePlan empty = GateEvaluationTestData.Compile(GateTestPlan.Gate("A",
            offsetSeconds: .1e-6, durationSeconds: .1e-6));
        GateEvaluationEntry emptyResult = new GateEvaluator().Evaluate(empty,
            GateEvaluationTestData.Trace(samples.Length), samples).Batch!.Entries[0];
        Assert.AreEqual(GateResultStatus.EmptyResolvedWindow, emptyResult.Status);
        Assert.AreEqual(GateDiagnosticCode.EmptyResolvedWindow, emptyResult.DiagnosticCode);
        Assert.IsNull(emptyResult.Metrics.ProminenceCounts);
        CompiledGatePlan nonIntegerAxis = GateEvaluationTestData.Compile(GateTestPlan.Gate("A",
            offsetSeconds: .25e-6, durationSeconds: 1e-6));
        GateEvaluationEntry nonIntegerResult = new GateEvaluator().Evaluate(nonIntegerAxis,
            GateEvaluationTestData.Trace(samples.Length, offset: .25e-6), samples).Batch!.Entries[0];
        Assert.AreEqual(GateResultStatus.Detected, nonIntegerResult.Status);
        Assert.AreEqual(1d, nonIntegerResult.AmplitudeCounts);
        CompiledGatePlan oneUlpOutside = GateEvaluationTestData.Compile(GateTestPlan.Gate("A",
            durationSeconds: Math.BitIncrement(3e-6)));
        Assert.AreEqual(GateResultStatus.WindowOutOfRange, new GateEvaluator().Evaluate(oneUlpOutside,
            GateEvaluationTestData.Trace(samples.Length), samples).Batch!.Entries[0].Status);
    }

    [TestMethod]
    public void IndependentAndChainedGatesUseTopologicalResultsAndAbsoluteTimes()
    {
        const double rate = 1_000_000;
        short[] samples = [0, 10, 0, 20, 0, 30];
        CompiledGatePlan independent = GateEvaluationTestData.Compile(
            GateEvaluationTestData.Gate("A", 6), GateEvaluationTestData.Gate("B", 6), GateEvaluationTestData.Gate("C", 6));
        Assert.HasCount(3, new GateEvaluator().Evaluate(independent, GateEvaluationTestData.Trace(6), samples).Batch!.Entries);

        CompiledGatePlan chain = GateEvaluationTestData.Compile(
            GateEvaluationTestData.Gate("A", 2, detector: GateDetectorKind.GlobalMaximum),
            GateEvaluationTestData.Gate("B", 2, dependency: "A", detector: GateDetectorKind.GlobalMaximum,
                offsetSeconds: 1 / rate),
            GateEvaluationTestData.Gate("C", 2, dependency: "B", detector: GateDetectorKind.GlobalMaximum,
                offsetSeconds: 1 / rate));
        GateEvaluationBatch batch = new GateEvaluator().Evaluate(chain, GateEvaluationTestData.Trace(6), samples).Batch!;
        CollectionAssert.AreEqual(new[] { 1, 3, 5 }, batch.Entries.Select(x => x.SampleIndex!.Value).ToArray());
        Assert.AreEqual(3 / rate, batch.Entries[1].TimeOfFlightSeconds!.Value, 1e-18);
        Assert.AreEqual(new UTStudio.Domain.Processing.GateId("A"), batch.Entries[1].CausalGateId);
    }

    [TestMethod]
    public void FailedDependencyPropagatesImmediateCause()
    {
        short[] samples = [0, 0, 0];
        CompiledGatePlan plan = GateEvaluationTestData.Compile(
            GateEvaluationTestData.Gate("A", 3, detector: GateDetectorKind.FirstRisingCrossing, threshold: .5),
            GateEvaluationTestData.Gate("B", 1, dependency: "A"),
            GateEvaluationTestData.Gate("C", 1, dependency: "B"));
        GateEvaluationBatch batch = new GateEvaluator().Evaluate(plan, GateEvaluationTestData.Trace(3), samples).Batch!;
        Assert.AreEqual(GateResultStatus.NoEvent, batch.Entries[0].Status);
        Assert.AreEqual(GateResultStatus.DependencyNotDetected, batch.Entries[1].Status);
        Assert.AreEqual(GateResultStatus.DependencyNotDetected, batch.Entries[2].Status);
        Assert.AreEqual(new UTStudio.Domain.Processing.GateId("B"), batch.Entries[2].CausalGateId);
    }

    [TestMethod]
    public void OnlyRequestedMeasuresPublishValuesWithoutBreakingDependencies()
    {
        short[] samples = [0, 10, 0, 20];
        CompiledGatePlan plan = GateEvaluationTestData.Compile(
            GateEvaluationTestData.Gate("A", 2, measures: [GateMeasureKind.Status]),
            GateEvaluationTestData.Gate("B", 2, dependency: "A", offsetSeconds: 1e-6,
                measures: [GateMeasureKind.TimeOfFlight, GateMeasureKind.Quality]));
        GateEvaluationBatch batch = new GateEvaluator().Evaluate(plan,
            GateEvaluationTestData.Trace(samples.Length), samples).Batch!;

        GateEvaluationEntry parent = batch.Entries[0];
        Assert.AreEqual(GateResultStatus.Detected, parent.Status);
        Assert.IsNull(parent.SampleIndex); Assert.IsNull(parent.FractionalSamplePosition);
        Assert.IsNull(parent.DetectionTimeSeconds); Assert.IsNull(parent.AmplitudeCounts);
        Assert.IsNull(parent.SignedRfAmplitudeCounts); Assert.IsNull(parent.TimeOfFlightSeconds);
        Assert.IsNull(parent.OffsetWithinWindowSeconds);

        GateEvaluationEntry child = batch.Entries[1];
        Assert.AreEqual(GateResultStatus.Detected, child.Status);
        Assert.AreEqual(3e-6, child.TimeOfFlightSeconds!.Value, 1e-18);
        Assert.IsNull(child.SampleIndex); Assert.IsNull(child.DetectionTimeSeconds);
        Assert.IsNull(child.AmplitudeCounts); Assert.IsNull(child.OffsetWithinWindowSeconds);
    }

    [TestMethod]
    public void QualityAndConfirmedDescriptorAreValidatedBeforeSamples()
    {
        short[] samples = [1, 2, 3]; CompiledGatePlan plan = GateEvaluationTestData.Compile(GateEvaluationTestData.Gate("A", 3));
        GateEvaluationEntry good = new GateEvaluator().Evaluate(plan, GateEvaluationTestData.Trace(3), samples).Batch!.Entries[0];
        Assert.AreEqual(GateResultQuality.Valid, good.Quality); Assert.AreEqual(new InspectionRevision(5), good.SourceInspectionRevision);
        GateEvaluationEntry degraded = new GateEvaluator().Evaluate(plan,
            GateEvaluationTestData.Trace(3, quality: UtTraceQuality.Degraded), samples).Batch!.Entries[0];
        Assert.AreEqual(GateResultQuality.Degraded, degraded.Quality); Assert.AreEqual(GateResultStatus.Detected, degraded.Status);
        GateEvaluationEntry invalid = new GateEvaluator().Evaluate(plan,
            GateEvaluationTestData.Trace(3, quality: UtTraceQuality.Invalid), samples).Batch!.Entries[0];
        Assert.AreEqual(GateResultStatus.InvalidTrace, invalid.Status); Assert.IsNull(invalid.SampleIndex);
        Assert.ThrowsExactly<ArgumentException>(() => GateEvaluationTestData.Trace(3, revision: InspectionRevision.Initial));
        Assert.ThrowsExactly<ArgumentException>(() => GateEvaluationTestData.Trace(3,
            quality: UtTraceQuality.Good, descriptorQuality: UtTraceQuality.Degraded));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => GateEvaluationTestData.Trace(3, quality: UtTraceQuality.Unknown));
        Assert.ThrowsExactly<ArgumentException>(() => GateEvaluationTestData.Trace(3,
            sampleEncoding: UtSampleEncoding.Unknown));
        TraceDescriptor descriptor = GateEvaluationTestData.Trace(3).Descriptor;
        var otherStream = new UTStudio.Contracts.Inspection.UtStreamIdentity(
            new UTStudio.Domain.Inspection.InspectionId(Guid.NewGuid()),
            new UTStudio.Domain.Acquisition.AcquisitionRunId(Guid.NewGuid()),
            new UTStudio.Domain.Acquisition.UtSourceId("other"), new UTStudio.Domain.Acquisition.PhysicalChannelId(1));
        Assert.ThrowsExactly<ArgumentException>(() => new ConfirmedGateTraceDescriptor(descriptor,
            new UTStudio.Contracts.Inspection.TraceIdentity(otherStream, 0), new InspectionRevision(1), UtTraceQuality.Good));
    }

    [TestMethod]
    public void ReproducibilitySignatureIncludesEffectiveAxisAndSourceRevision()
    {
        short[] samples = [1, 2, 3];
        CompiledGatePlan plan = GateEvaluationTestData.Compile(GateEvaluationTestData.Gate("A", 3));
        GateEvaluator evaluator = new();
        string baseline = evaluator.Evaluate(plan, GateEvaluationTestData.Trace(3), samples).Batch!.ReproducibilitySignature.Hex;
        string same = evaluator.Evaluate(plan, GateEvaluationTestData.Trace(3), samples).Batch!.ReproducibilitySignature.Hex;
        string rate = evaluator.Evaluate(plan, GateEvaluationTestData.Trace(3, rate: 2_000_000), samples).Batch!.ReproducibilitySignature.Hex;
        string offset = evaluator.Evaluate(plan, GateEvaluationTestData.Trace(3, offset: 1e-7), samples).Batch!.ReproducibilitySignature.Hex;
        string revision = evaluator.Evaluate(plan, GateEvaluationTestData.Trace(3, revision: new InspectionRevision(6)), samples).Batch!.ReproducibilitySignature.Hex;
        Assert.AreEqual(baseline, same);
        Assert.AreNotEqual(baseline, rate); Assert.AreNotEqual(baseline, offset); Assert.AreNotEqual(baseline, revision);

        var spatial = new UTStudio.Domain.Acquisition.ScanSpatialCoordinate(12,
            UTStudio.Domain.Acquisition.ScanSpatialUnit.Millimetres, new UTStudio.Domain.Acquisition.ScanAxisId("X"));
        string sequence = evaluator.Evaluate(plan, GateEvaluationTestData.Trace(3, sequence: 10), samples).Batch!.ReproducibilitySignature.Hex;
        string elapsed = evaluator.Evaluate(plan, GateEvaluationTestData.Trace(3, elapsed: TimeSpan.FromSeconds(1)), samples).Batch!.ReproducibilitySignature.Hex;
        string timestamp = evaluator.Evaluate(plan, GateEvaluationTestData.Trace(3, timestamp: DateTimeOffset.UnixEpoch.AddDays(1)), samples).Batch!.ReproducibilitySignature.Hex;
        string coordinate = evaluator.Evaluate(plan, GateEvaluationTestData.Trace(3, spatial: spatial), samples).Batch!.ReproducibilitySignature.Hex;
        string clock = evaluator.Evaluate(plan, GateEvaluationTestData.Trace(3, clockQuality: UtClockQuality.Estimated), samples).Batch!.ReproducibilitySignature.Hex;
        Assert.AreEqual(baseline, sequence); Assert.AreEqual(baseline, elapsed); Assert.AreEqual(baseline, timestamp);
        Assert.AreEqual(baseline, coordinate); Assert.AreEqual(baseline, clock);
        string degraded = evaluator.Evaluate(plan, GateEvaluationTestData.Trace(3, quality: UtTraceQuality.Degraded), samples).Batch!.ReproducibilitySignature.Hex;
        string provenance = evaluator.Evaluate(plan, GateEvaluationTestData.Trace(3, provenance: UtTraceProvenance.Hardware), samples).Batch!.ReproducibilitySignature.Hex;
        Assert.AreNotEqual(baseline, degraded); Assert.AreNotEqual(baseline, provenance);
    }

    [TestMethod]
    public void CancellationAtEveryCheckpointReturnsCompleteTerminalBatch()
    {
        foreach (GateEvaluationCheckpoint target in Enum.GetValues<GateEvaluationCheckpoint>())
        {
            using CancellationTokenSource source = new();
            var pool = new TestGateScratchPool();
            var evaluator = new GateEvaluator(pool, point => { if (point == target) { source.Cancel(); } });
            short[] samples = Enumerable.Range(0, 1024).Select(i => (short)(i % 2 == 0 ? 0 : 100)).ToArray();
            CompiledGatePlan plan = GateEvaluationTestData.Compile(
                GateEvaluationTestData.Gate("A", samples.Length, detector: GateDetectorKind.FirstLocalMaximum,
                    local: new(1, 0, 1)), GateEvaluationTestData.Gate("B", samples.Length));
            GateEvaluationResult result = evaluator.Evaluate(plan, GateEvaluationTestData.Trace(samples.Length), samples, source.Token);
            Assert.AreEqual(GateEvaluationStatus.Cancelled, result.Status, target.ToString());
            Assert.HasCount(2, result.Batch!.Entries); Assert.IsTrue(result.Batch.Entries.All(x => x.Status == GateResultStatus.Cancelled));
            Assert.AreEqual(0, pool.Snapshot.ActiveRentals);
        }
    }

    [TestMethod]
    public void CancellationInsideLongPlateauIsObservedAndScratchIsReturned()
    {
        using CancellationTokenSource source = new();
        int inWindowCheckpoints = 0;
        var pool = new TestGateScratchPool();
        var evaluator = new GateEvaluator(pool, point =>
        {
            if (point == GateEvaluationCheckpoint.LocalNeighborhood && ++inWindowCheckpoints == 2) { source.Cancel(); }
        });
        short[] samples = Enumerable.Repeat((short)10, 65_535).ToArray();
        CompiledGatePlan plan = GateEvaluationTestData.Compile(GateEvaluationTestData.Gate("A", samples.Length,
            detector: GateDetectorKind.FirstLocalMaximum, local: new(1, 0, 1)));
        GateEvaluationResult result = evaluator.Evaluate(plan, GateEvaluationTestData.Trace(samples.Length), samples, source.Token);
        Assert.AreEqual(GateEvaluationStatus.Cancelled, result.Status);
        Assert.AreEqual(0, pool.Snapshot.ActiveRentals);
    }

    [TestMethod]
    public async Task EvaluatorIsConcurrentDeterministicAndDoesNotRetainSamples()
    {
        short[] samples = Enumerable.Range(0, 2048).Select(i => (short)(i % 100)).ToArray();
        CompiledGatePlan plan = GateEvaluationTestData.Compile(GateEvaluationTestData.Gate("A", samples.Length));
        ConfirmedGateTraceDescriptor trace = GateEvaluationTestData.Trace(samples.Length);
        GateEvaluator evaluator = new(new GateScratchPool(4));
        GateEvaluationBatch[] batches = await Task.WhenAll(Enumerable.Range(0, 32)
            .Select(_ => Task.Run(() => evaluator.Evaluate(plan, trace, samples).Batch!)));
        Assert.IsTrue(batches.All(x => x.Equals(batches[0])));
        Assert.AreEqual(0, evaluator.ScratchDiagnostics.ActiveRentals);
        Assert.IsFalse(typeof(GateEvaluationEntry).GetProperties().Any(x =>
            x.PropertyType.IsArray || x.PropertyType.Name.Contains("Span", StringComparison.Ordinal) ||
            x.PropertyType.Name.Contains("Memory", StringComparison.Ordinal)));
        Assert.IsFalse(typeof(GateEvaluator).GetFields(System.Reflection.BindingFlags.Instance |
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public).Any(x =>
                x.FieldType.IsArray || x.FieldType.Name.Contains("Memory", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task RealScratchPoolRejectsContentionAndRemainsBalanced()
    {
        using ManualResetEventSlim rented = new(false);
        using ManualResetEventSlim release = new(false);
        var pool = new GateScratchPool(1);
        int blocked = 0;
        var evaluator = new GateEvaluator(pool, point =>
        {
            if (point == GateEvaluationCheckpoint.AfterScratchRent && Interlocked.Exchange(ref blocked, 1) == 0)
            { rented.Set(); release.Wait(TimeSpan.FromSeconds(10)); }
        });
        short[] samples = [0, 3, 0, 2, 0];
        CompiledGatePlan plan = GateEvaluationTestData.Compile(GateEvaluationTestData.Gate("A", samples.Length,
            detector: GateDetectorKind.FirstLocalMaximum, local: new(1, 0, 1)));
        ConfirmedGateTraceDescriptor trace = GateEvaluationTestData.Trace(samples.Length);

        Task<GateEvaluationResult> first = Task.Run(() => evaluator.Evaluate(plan, trace, samples));
        Assert.IsTrue(rented.Wait(TimeSpan.FromSeconds(10)));
        GateEvaluationResult rejected = evaluator.Evaluate(plan, trace, samples);
        release.Set();
        GateEvaluationResult completed = await first;

        Assert.AreEqual(GateEvaluationErrorCode.ScratchUnavailable, rejected.Error!.Code);
        Assert.AreEqual(GateEvaluationStatus.Completed, completed.Status);
        Assert.AreEqual(1, completed.Batch!.Entries[0].SampleIndex);
        Assert.AreEqual(1L, pool.Snapshot.RentCount);
        Assert.AreEqual(1L, pool.Snapshot.ReturnCount);
        Assert.AreEqual(1L, pool.Snapshot.RejectionCount);
        Assert.AreEqual(0, pool.Snapshot.ActiveRentals);
    }

    [TestMethod]
    public void ScratchFailuresAreExplicitAndBalancedWithoutFallback()
    {
        short[] samples = [0, 2, 0];
        CompiledGatePlan plan = GateEvaluationTestData.Compile(GateEvaluationTestData.Gate("A", 3,
            detector: GateDetectorKind.FirstLocalMaximum, local: new(1, 0, 1)));
        TestGateScratchPool rejected = new(reject: true);
        GateEvaluationResult unavailable = new GateEvaluator(rejected).Evaluate(plan, GateEvaluationTestData.Trace(3), samples);
        Assert.AreEqual(GateEvaluationErrorCode.ScratchUnavailable, unavailable.Error!.Code); Assert.IsNull(unavailable.Batch);
        TestGateScratchPool returnFailure = new(throwOnReturn: true);
        GateEvaluationResult failedReturn = new GateEvaluator(returnFailure).Evaluate(plan, GateEvaluationTestData.Trace(3), samples);
        Assert.AreEqual(GateEvaluationErrorCode.ScratchReturnFailed, failedReturn.Error!.Code);
        Assert.AreEqual(0, returnFailure.Snapshot.ActiveRentals);
    }

    [TestMethod]
    public void PrimaryAndCleanupErrorsAreComposedAndFlattenedInOrder()
    {
        short[] samples = [0, 2, 0];
        CompiledGatePlan plan = GateEvaluationTestData.Compile(GateEvaluationTestData.Gate("A", 3,
            detector: GateDetectorKind.FirstLocalMaximum, local: new(1, 0, 1)));
        var cleanup = new AggregateException(new InvalidOperationException("one"),
            new AggregateException(new ArgumentException("two"), new ApplicationException("three")));
        var pool = new TestGateScratchPool(returnError: cleanup);
        var evaluator = new GateEvaluator(pool, point =>
        { if (point == GateEvaluationCheckpoint.LocalTreeBuild) { throw new FormatException("primary"); } });

        GateEvaluationResult result = evaluator.Evaluate(plan, GateEvaluationTestData.Trace(3), samples);

        Assert.AreEqual(GateEvaluationStatus.Failed, result.Status);
        Assert.AreEqual(GateEvaluationErrorCode.InternalFailure, result.PrimaryError!.Code);
        CollectionAssert.AreEqual(new[] { "InvalidOperationException", "ArgumentException", "ApplicationException" },
            result.SecondaryErrors.Select(x => x.Message.Split(' ')[^1].TrimEnd('.')).ToArray());
        Assert.IsTrue(result.SecondaryErrors.All(x => x.Code == GateEvaluationErrorCode.ScratchReturnFailed));
    }

    [TestMethod]
    public void PrimaryFailureWithoutCleanupPreservesOnlyTheOriginalError()
    {
        var evaluator = new GateEvaluator(new TestGateScratchPool(), point =>
        { if (point == GateEvaluationCheckpoint.BeforePublish) { throw new FormatException("primary-only"); } });
        CompiledGatePlan plan = GateEvaluationTestData.Compile(GateEvaluationTestData.Gate("A", 3));

        GateEvaluationResult result = evaluator.Evaluate(plan, GateEvaluationTestData.Trace(3), [0, 1, 2]);

        Assert.AreEqual(GateEvaluationErrorCode.InternalFailure, result.PrimaryError!.Code);
        Assert.IsEmpty(result.SecondaryErrors);
    }

    [TestMethod]
    public void CancellationRemainsPrimaryWhenCleanupFails()
    {
        using CancellationTokenSource source = new();
        var pool = new TestGateScratchPool(throwOnReturn: true);
        var evaluator = new GateEvaluator(pool, point =>
        { if (point == GateEvaluationCheckpoint.AfterScratchRent) { source.Cancel(); } });
        short[] samples = [0, 2, 0];
        CompiledGatePlan plan = GateEvaluationTestData.Compile(GateEvaluationTestData.Gate("A", 3,
            detector: GateDetectorKind.FirstLocalMaximum, local: new(1, 0, 1)));

        GateEvaluationResult result = evaluator.Evaluate(plan, GateEvaluationTestData.Trace(3), samples, source.Token);

        Assert.AreEqual(GateEvaluationErrorCode.Cancellation, result.PrimaryError!.Code);
        Assert.HasCount(1, result.SecondaryErrors);
        Assert.AreEqual(GateEvaluationErrorCode.ScratchReturnFailed, result.SecondaryErrors[0].Code);
    }

    [TestMethod]
    public void ScratchLeaseRejectsForeignAndDoubleReturnWithoutCorruptingBalances()
    {
        var owner = new GateScratchPool(2);
        var foreign = new GateScratchPool(2);
        Assert.IsTrue(owner.TryRent(17, out GateScratchLease? lease));
        Assert.ThrowsExactly<InvalidOperationException>(() => foreign.Return(lease!));
        owner.Return(lease!);
        Assert.ThrowsExactly<InvalidOperationException>(() => owner.Return(lease!));
        GateScratchDiagnostics snapshot = owner.Snapshot;
        Assert.AreEqual(1L, snapshot.RentCount); Assert.AreEqual(1L, snapshot.ReturnCount);
        Assert.AreEqual(0, snapshot.ActiveRentals); Assert.AreEqual(snapshot.RentedElements, snapshot.ReturnedElements);
        Assert.AreEqual(1, snapshot.PeakActiveRentals);
    }

    [TestMethod]
    public void SharedPoolSupportsConcurrentLocalEvaluationsAndBalancesBothLeases()
    {
        var pool = new GateScratchPool(2);
        using var barrier = new Barrier(2);
        var evaluator = new GateEvaluator(pool, point =>
        { if (point == GateEvaluationCheckpoint.AfterScratchRent) { barrier.SignalAndWait(TimeSpan.FromSeconds(5)); } });
        CompiledGatePlan plan = GateEvaluationTestData.Compile(GateEvaluationTestData.Gate("A", 5,
            detector: GateDetectorKind.FirstLocalMaximum, local: new(1, 1, 1)));
        short[] samples = [0, 10, 0, 5, 0];

        Task<GateEvaluationResult>[] tasks = Enumerable.Range(0, 2).Select(_ => Task.Run(() =>
            evaluator.Evaluate(plan, GateEvaluationTestData.Trace(samples.Length), samples))).ToArray();
        Task.WaitAll(tasks);

        Assert.IsTrue(tasks.All(x => x.Result.Status == GateEvaluationStatus.Completed));
        Assert.IsTrue(tasks.All(x => x.Result.Batch!.Entries[0].SampleIndex == 1));
        GateScratchDiagnostics diagnostics = pool.Snapshot;
        Assert.AreEqual(2, diagnostics.PeakActiveRentals);
        Assert.AreEqual(2L, diagnostics.RentCount);
        Assert.AreEqual(diagnostics.RentCount, diagnostics.ReturnCount);
        Assert.AreEqual(diagnostics.RentedElements, diagnostics.ReturnedElements);
        Assert.AreEqual(0, diagnostics.ActiveRentals);
    }

    [TestMethod]
    public void ScratchBucketsAreProportionalAndRetainedCapacityIsBounded()
    {
        var pool = new GateScratchPool(2);
        Assert.IsTrue(pool.TryRent(17, out GateScratchLease? small));
        long smallCapacity = small!.Buffer.ElementCapacity;
        pool.Return(small);
        Assert.IsTrue(pool.TryRent(65_535, out GateScratchLease? maximum));
        long maximumCapacity = maximum!.Buffer.ElementCapacity;
        pool.Return(maximum);
        Assert.IsLessThan(maximumCapacity, smallCapacity * 100);
        Assert.AreEqual(smallCapacity + maximumCapacity, pool.Snapshot.RetainedCapacity);
        Assert.AreEqual(pool.Snapshot.RentedElements, pool.Snapshot.ReturnedElements);
    }

    [TestMethod]
    public void ScratchPoolEvictsIdleBucketsAndStillAdmitsConfiguredMaximumConcurrency()
    {
        long maximumBuffer = GateScratchBuffer.ElementCapacityFor(65_536);
        var pool = new GateScratchPool(2, maximumBuffer * 2);
        Assert.IsTrue(pool.TryRent(17, out GateScratchLease? small));
        Assert.IsTrue(pool.TryRent(4_000, out GateScratchLease? medium));
        pool.Return(small!);
        pool.Return(medium!);

        Assert.IsTrue(pool.TryRent(65_535, out GateScratchLease? firstMaximum));
        Assert.IsTrue(pool.TryRent(65_535, out GateScratchLease? secondMaximum));
        Assert.AreNotSame(firstMaximum!.Buffer, secondMaximum!.Buffer);
        pool.Return(firstMaximum);
        pool.Return(secondMaximum);

        GateScratchDiagnostics diagnostics = pool.Snapshot;
        Assert.AreEqual(0, diagnostics.ActiveRentals);
        Assert.AreEqual(diagnostics.RentCount, diagnostics.ReturnCount);
        Assert.AreEqual(diagnostics.RentedElements, diagnostics.ReturnedElements);
        Assert.AreEqual(maximumBuffer * 2, diagnostics.RetainedCapacity);
    }

    [TestMethod]
    public void LongWindowOfMaximumSupportedLengthEvaluatesAndBalancesScratch()
    {
        short[] samples = new short[65_535]; samples[32_767] = short.MaxValue;
        var evaluator = new GateEvaluator(new GateScratchPool());
        CompiledGatePlan plan = GateEvaluationTestData.Compile(GateEvaluationTestData.Gate("A", samples.Length,
            detector: GateDetectorKind.FirstLocalMaximum, local: new(1, 1, 1)));
        GateEvaluationEntry result = evaluator.Evaluate(plan, GateEvaluationTestData.Trace(samples.Length), samples).Batch!.Entries[0];
        AssertDetected(result, 32_767, short.MaxValue);
        Assert.AreEqual(evaluator.ScratchDiagnostics.RentCount, evaluator.ScratchDiagnostics.ReturnCount);
        Assert.AreEqual(0, evaluator.ScratchDiagnostics.ActiveRentals);
    }

    [TestMethod]
    public void AdversarialDenseAndRepeatedPlateauSignalsRemainDeterministicAtMaximumLength()
    {
        short[] saw = Enumerable.Range(0, 65_535).Select(i => (short)(i % 2 == 0 ? 0 : 100)).ToArray();
        GateEvaluationEntry dense = EvaluateOne(saw, GateDetectorKind.FirstLocalMaximum,
            GateSignalDomain.RawSigned, local: new(1, 100, 65_535));
        AssertDetected(dense, 1, 100);
        short[] plateaus = Enumerable.Range(0, 65_535).Select(i => (short)(i % 4 is 1 or 2 ? 50 : 0)).ToArray();
        GateEvaluationEntry repeated = EvaluateOne(plateaus, GateDetectorKind.LastLocalMaximum,
            GateSignalDomain.RawSigned, local: new(1, 50, 1));
        Assert.AreEqual(65_529, repeated.SampleIndex);
        Assert.AreEqual(50d, repeated.Metrics.ProminenceCounts);
    }

    private static GateEvaluationEntry EvaluateOne(short[] samples, GateDetectorKind detector,
        GateSignalDomain domain, double? threshold = null, GateHysteresis? hysteresis = null,
        GateLocalExtremaParameters? local = null, int? durationSamples = null)
    {
        GateDefinition gate = GateEvaluationTestData.Gate("A", durationSamples ?? samples.Length,
            detector: detector, domain: domain, threshold: threshold, hysteresis: hysteresis, local: local);
        return new GateEvaluator().Evaluate(GateEvaluationTestData.Compile(gate),
            GateEvaluationTestData.Trace(samples.Length), samples).Batch!.Entries[0];
    }

    private static void AssertDetected(GateEvaluationEntry entry, int index, double amplitude)
    { Assert.AreEqual(GateResultStatus.Detected, entry.Status); Assert.AreEqual(index, entry.SampleIndex); Assert.AreEqual(amplitude, entry.AmplitudeCounts); }

    private static double NormalizedThreshold(GateSignalDomain domain, double counts) => domain switch
    {
        GateSignalDomain.RawSigned => (counts + 32768d) / 65535d,
        GateSignalDomain.Positive => counts / 32767d,
        GateSignalDomain.NegativeMagnitude or GateSignalDomain.Absolute => counts / 32768d,
        _ => throw new ArgumentOutOfRangeException(nameof(domain))
    };

    private static short[] SignalForDomain(GateSignalDomain domain, short[] magnitudes) => domain switch
    {
        GateSignalDomain.RawSigned or GateSignalDomain.Positive => magnitudes,
        GateSignalDomain.NegativeMagnitude or GateSignalDomain.Absolute =>
            magnitudes.Select(x => checked((short)-x)).ToArray(),
        _ => throw new ArgumentOutOfRangeException(nameof(domain))
    };
}
