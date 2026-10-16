using UTStudio.Contracts.Processing;
using UTStudio.Domain.Processing;
using UTStudio.Processing.Core;

namespace UTStudio.Core.Tests.Processing;

[TestClass]
public sealed class GatePlanCompilerTests
{
    private readonly GatePlanCompiler _compiler = new();

    [TestMethod]
    public void OneGateAndIndependentGatesCompileInDeclaredOrder()
    {
        GatePlanCompilationResult one = _compiler.Compile(GateTestPlan.Plan([GateTestPlan.Gate("A")]));
        Assert.IsTrue(one.IsCompiled);
        GatePlanCompilationResult three = _compiler.Compile(GateTestPlan.Plan(
            [GateTestPlan.Gate("C"), GateTestPlan.Gate("A"), GateTestPlan.Gate("B")]));
        Assert.IsTrue(three.IsCompiled);
        CollectionAssert.AreEqual(new[] { 0, 1, 2 }, three.CompiledPlan!.TopologicalOrder.ToArray());
    }

    [TestMethod]
    public void ChainCompilesDependenciesToIndices()
    {
        GatePlanCompilationResult result = _compiler.Compile(GateTestPlan.Plan(
            [GateTestPlan.Gate("A"), GateTestPlan.Gate("B", "A"), GateTestPlan.Gate("C", "B")]));
        Assert.IsTrue(result.IsCompiled);
        CollectionAssert.AreEqual(new[] { -1, 0, 1 }, result.CompiledPlan!.DeclaredGates.Select(x => x.DependencyIndex).ToArray());
        CollectionAssert.AreEqual(new[] { 0, 1, 2 }, result.CompiledPlan.TopologicalOrder.ToArray());
    }

    [TestMethod]
    public void FanOutUsesDeclaredOrderAsReadyTieBreaker()
    {
        GatePlanCompilationResult result = _compiler.Compile(GateTestPlan.Plan(
            [GateTestPlan.Gate("A"), GateTestPlan.Gate("C", "A"), GateTestPlan.Gate("B", "A")]));
        Assert.IsTrue(result.IsCompiled);
        CollectionAssert.AreEqual(new[] { 0, 1, 2 }, result.CompiledPlan!.TopologicalOrder.ToArray());
    }

    [TestMethod]
    public void DuplicateMissingAndSelfDependenciesHaveSpecificFailures()
    {
        AssertStatus(GatePlanCompilationStatus.DuplicateGate,
            [GateTestPlan.Gate("A"), GateTestPlan.Gate("A")]);
        AssertStatus(GatePlanCompilationStatus.MissingDependency, [GateTestPlan.Gate("A", "missing")]);
        AssertStatus(GatePlanCompilationStatus.SelfDependency, [GateTestPlan.Gate("A", "A")]);
    }

    [TestMethod]
    public void TwoAndThreeGateCyclesReportClosedPaths()
    {
        GatePlanCompilationResult two = _compiler.Compile(GateTestPlan.Plan(
            [GateTestPlan.Gate("A", "B"), GateTestPlan.Gate("B", "A")]));
        Assert.AreEqual(GatePlanCompilationStatus.CycleDetected, two.Status);
        CollectionAssert.AreEqual(new[] { new GateId("A"), new GateId("B"), new GateId("A") },
            two.Errors.Single().CyclePath.ToArray());

        GatePlanCompilationResult three = _compiler.Compile(GateTestPlan.Plan(
            [GateTestPlan.Gate("A", "C"), GateTestPlan.Gate("B", "A"), GateTestPlan.Gate("C", "B")]));
        Assert.AreEqual(GatePlanCompilationStatus.CycleDetected, three.Status);
        Assert.HasCount(4, three.Errors.Single().CyclePath);
    }

    [TestMethod]
    public void MultipleCyclesProduceMultipleDiagnostics()
    {
        GatePlanCompilationResult result = new GatePlanCompiler(new(8)).Compile(GateTestPlan.Plan([
            GateTestPlan.Gate("A", "B"), GateTestPlan.Gate("B", "A"),
            GateTestPlan.Gate("C", "D"), GateTestPlan.Gate("D", "C") ]));
        Assert.AreEqual(GatePlanCompilationStatus.CycleDetected, result.Status);
        Assert.HasCount(2, result.Errors);
    }

    [TestMethod]
    public void DefaultAndAbsoluteCapacityLimitsAreEnforced()
    {
        Assert.IsTrue(_compiler.Compile(GateTestPlan.Plan(
            Enumerable.Range(0, 8).Select(i => GateTestPlan.Gate($"G{i}")))).IsCompiled);
        GateDefinition[] nine = Enumerable.Range(0, 9).Select(i => GateTestPlan.Gate($"G{i}")).ToArray();
        Assert.AreEqual(GatePlanCompilationStatus.CapacityExceeded, _compiler.Compile(GateTestPlan.Plan(nine)).Status);
        Assert.IsTrue(new GatePlanCompiler(new(4)).Compile(GateTestPlan.Plan(
            Enumerable.Range(0, 4).Select(i => GateTestPlan.Gate($"G{i}")))).IsCompiled);
        Assert.AreEqual(GatePlanCompilationStatus.CapacityExceeded, new GatePlanCompiler(new(4)).Compile(GateTestPlan.Plan(
            Enumerable.Range(0, 5).Select(i => GateTestPlan.Gate($"G{i}")))).Status);
        Assert.IsTrue(new GatePlanCompiler(new(16)).Compile(GateTestPlan.Plan(
            Enumerable.Range(0, 16).Select(i => GateTestPlan.Gate($"G{i}")))).IsCompiled);
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new GatePlanCompilerOptions(17));
    }

    [TestMethod]
    public void EmptyPlanInvalidDefaultsAndTemporalLimitsFailWithoutPartialPlan()
    {
        AssertFailure(_compiler.Compile(GateTestPlan.Plan([])), GateCompilationErrorCode.EmptyPlan);
        AssertFailure(_compiler.Compile(new GatePlan(default, default, default, default,
            [GateTestPlan.Gate("A")])), GateCompilationErrorCode.InvalidIdentity);
        AssertFailure(_compiler.Compile(GateTestPlan.Plan([GateTestPlan.Gate("A", durationSeconds: 0)])),
            GateCompilationErrorCode.InvalidDuration);
        AssertFailure(_compiler.Compile(GateTestPlan.Plan([GateTestPlan.Gate("A",
            offsetSeconds: double.MaxValue, durationSeconds: double.MaxValue)])), GateCompilationErrorCode.TemporalOverflow);
    }

    [TestMethod]
    public void ThresholdHysteresisLocalParametersAndCompatibilityAreValidated()
    {
        AssertFailure(CompileDetector(GateDetectorKind.FirstRisingCrossing, threshold: 1.1), GateCompilationErrorCode.InvalidThreshold);
        AssertFailure(CompileDetector(GateDetectorKind.FirstRisingCrossing, threshold: .5,
            hysteresis: new(.8, .2)), GateCompilationErrorCode.InvalidHysteresis);
        AssertFailure(CompileDetector(GateDetectorKind.FirstRisingCrossing, threshold: .5,
            hysteresis: new(.2, .8)), GateCompilationErrorCode.UnsupportedCombination);
        AssertFailure(CompileDetector(GateDetectorKind.GlobalMaximum, hysteresis: new(.2, .8)),
            GateCompilationErrorCode.UnsupportedCombination);
        AssertFailure(CompileDetector(GateDetectorKind.FirstLocalMaximum,
            local: new(0, .2, 1)), GateCompilationErrorCode.InvalidLocalExtremaParameters);
        Assert.IsTrue(CompileDetector(GateDetectorKind.FirstLocalMaximum,
            local: new(1, 25_000, 1)).IsCompiled);
        AssertFailure(CompileDetector(GateDetectorKind.FirstLocalMaximum,
            local: new(1, .2, 0)), GateCompilationErrorCode.InvalidLocalExtremaParameters);
        AssertFailure(_compiler.Compile(GateTestPlan.Plan([GateTestPlan.Gate("A", detector: GateDetectorKind.AbsoluteMaximum,
            domain: GateSignalDomain.Positive)])), GateCompilationErrorCode.UnsupportedCombination);
        AssertFailure(_compiler.Compile(GateTestPlan.Plan([GateTestPlan.Gate("A",
            detector: (GateDetectorKind)999)])), GateCompilationErrorCode.UnsupportedCombination);
        AssertFailure(_compiler.Compile(GateTestPlan.Plan([GateTestPlan.Gate("A",
            domain: (GateSignalDomain)999)])), GateCompilationErrorCode.UnsupportedCombination);
    }

    [TestMethod]
    public void CancellationReturnsNoPartialStateAndPublishedPlanSurvivesLaterCancellation()
    {
        using CancellationTokenSource source = new(); source.Cancel();
        GatePlanCompilationResult cancelled = _compiler.Compile(GateTestPlan.Plan([GateTestPlan.Gate("A")]), source.Token);
        Assert.AreEqual(GatePlanCompilationStatus.Cancelled, cancelled.Status); Assert.IsNull(cancelled.CompiledPlan);
        GatePlanCompilationResult compiled = _compiler.Compile(GateTestPlan.Plan([GateTestPlan.Gate("A")]));
        source.Cancel();
        Assert.IsTrue(compiled.IsCompiled); Assert.IsNotNull(compiled.CompiledPlan);
    }

    [TestMethod]
    public async Task CompilerIsReusableConcurrently()
    {
        GatePlan plan = GateTestPlan.Plan([GateTestPlan.Gate("A"), GateTestPlan.Gate("B", "A")]);
        GatePlanCompilationResult[] results = await Task.WhenAll(Enumerable.Range(0, 64)
            .Select(_ => Task.Run(() => _compiler.Compile(plan))));
        Assert.IsTrue(results.All(x => x.IsCompiled));
        Assert.AreEqual(1, results.Select(x => x.CompiledPlan!.Signature.Hex).Distinct().Count());
    }

    private GatePlanCompilationResult CompileDetector(GateDetectorKind detector, double? threshold = null,
        GateHysteresis? hysteresis = null, GateLocalExtremaParameters? local = null) =>
        _compiler.Compile(GateTestPlan.Plan([GateTestPlan.Gate("A", detector: detector,
            threshold: threshold, hysteresis: hysteresis, local: local)]));

    private void AssertStatus(GatePlanCompilationStatus status, GateDefinition[] gates)
    { GatePlanCompilationResult result = _compiler.Compile(GateTestPlan.Plan(gates)); Assert.AreEqual(status, result.Status); Assert.IsNull(result.CompiledPlan); }
    private static void AssertFailure(GatePlanCompilationResult result, GateCompilationErrorCode code)
    { Assert.IsFalse(result.IsCompiled); Assert.IsNull(result.CompiledPlan); Assert.IsTrue(result.Errors.Any(x => x.Code == code)); }
}
