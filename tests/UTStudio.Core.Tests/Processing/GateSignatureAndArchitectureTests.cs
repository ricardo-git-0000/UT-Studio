using System.Xml.Linq;
using UTStudio.Contracts.Processing;
using UTStudio.Processing.Core;

namespace UTStudio.Core.Tests.Processing;

[TestClass]
public sealed class GateSignatureAndArchitectureTests
{
    [TestMethod]
    public void StructurallyEqualPlansHaveEqualSignaturesAndRevisionChangesSignature()
    {
        GatePlanCompiler compiler = new();
        string first = compiler.Compile(GateTestPlan.Plan([GateTestPlan.Gate("A")])).CompiledPlan!.Signature.Hex;
        string same = compiler.Compile(GateTestPlan.Plan([GateTestPlan.Gate("A")])).CompiledPlan!.Signature.Hex;
        string revised = compiler.Compile(GateTestPlan.Plan([GateTestPlan.Gate("A")], revision: 2)).CompiledPlan!.Signature.Hex;
        Assert.AreEqual(first, same); Assert.AreNotEqual(first, revised); Assert.AreEqual(64, first.Length);
    }

    [TestMethod]
    public void EverySemanticFieldChangesSignature()
    {
        GatePlanCompiler compiler = new(new(16));
        GatePlan baseline = GateTestPlan.Plan([GateTestPlan.Gate("A")]);
        string expected = compiler.Compile(baseline).CompiledPlan!.Signature.Hex;
        GatePlan[] variants =
        [
            GateTestPlan.Plan([GateTestPlan.Gate("A")], id: "other"),
            GateTestPlan.Plan([GateTestPlan.Gate("A")], revision: 2),
            GateTestPlan.Plan([GateTestPlan.Gate("A")], schema: 2),
            GateTestPlan.Plan([GateTestPlan.Gate("A")], algorithm: 2),
            GateTestPlan.Plan([GateTestPlan.Gate("B")]),
            GateTestPlan.Plan([GateTestPlan.Gate("A", revision: 2)]),
            GateTestPlan.Plan([GateTestPlan.Gate("A", offsetSeconds: 0.0000001)]),
            GateTestPlan.Plan([GateTestPlan.Gate("A", durationSeconds: 0.0000011)]),
            GateTestPlan.Plan([GateTestPlan.Gate("A", domain: GateSignalDomain.Positive)]),
            GateTestPlan.Plan([GateTestPlan.Gate("A", detector: GateDetectorKind.GlobalMinimum)]),
            GateTestPlan.Plan([GateTestPlan.Gate("A", threshold: .5)]),
            GateTestPlan.Plan([GateTestPlan.Gate("A", detector: GateDetectorKind.FirstRisingCrossing,
                hysteresis: new(.2, .8))]),
            GateTestPlan.Plan([GateTestPlan.Gate("A", detector: GateDetectorKind.FirstLocalMaximum,
                local: new(2, .3, 4))]),
            GateTestPlan.Plan([GateTestPlan.Gate("A", measures: [GateMeasureKind.Status])]),
            GateTestPlan.Plan([GateTestPlan.Gate("A", measures: [GateMeasureKind.Status, GateMeasureKind.Amplitude])]),
            GateTestPlan.Plan([GateTestPlan.Gate("A", quality: GateAcceptedTraceQuality.GoodOnly)]),
            GateTestPlan.Plan([GateTestPlan.Gate("A"), GateTestPlan.Gate("B", "A")]),
            GateTestPlan.Plan([GateTestPlan.Gate("B"), GateTestPlan.Gate("A")])
        ];
        foreach (GatePlan variant in variants)
        {
            GatePlanCompilationResult result = compiler.Compile(variant);
            Assert.IsTrue(result.IsCompiled, string.Join(";", result.Errors.Select(x => x.Message)));
            Assert.AreNotEqual(expected, result.CompiledPlan!.Signature.Hex);
        }
    }

    [TestMethod]
    public void DependencyAndSignedZeroAreCanonicalSignatureInputs()
    {
        GatePlanCompiler compiler = new();
        string origin = compiler.Compile(GateTestPlan.Plan([GateTestPlan.Gate("A"), GateTestPlan.Gate("B")])).CompiledPlan!.Signature.Hex;
        string dependency = compiler.Compile(GateTestPlan.Plan([GateTestPlan.Gate("A"), GateTestPlan.Gate("B", "A")])).CompiledPlan!.Signature.Hex;
        Assert.AreNotEqual(origin, dependency);
        string positiveZero = compiler.Compile(GateTestPlan.Plan([GateTestPlan.Gate("A", offsetSeconds: 0d)])).CompiledPlan!.Signature.Hex;
        string negativeZero = compiler.Compile(GateTestPlan.Plan([GateTestPlan.Gate("A", offsetSeconds: -0d)])).CompiledPlan!.Signature.Hex;
        Assert.AreEqual(positiveZero, negativeZero);
    }

    [TestMethod]
    public void CanonicalSignatureMatchesKnownVector()
    {
        string actual = new GatePlanCompiler().Compile(
            GateTestPlan.Plan([GateTestPlan.Gate("A")])).CompiledPlan!.Signature.Hex;
        Assert.AreEqual("EBAFDCA922CBD8E1510762B9F24E251D3383E5778BC20266FF8BA662C586AA9D", actual);
    }

    [TestMethod]
    public void DetectorSubfieldsIndependentlyChangeSignature()
    {
        GatePlanCompiler compiler = new();
        string Crossing(double threshold) => compiler.Compile(GateTestPlan.Plan([GateTestPlan.Gate("A",
            detector: GateDetectorKind.FirstRisingCrossing, threshold: threshold)])).CompiledPlan!.Signature.Hex;
        string Hysteresis(double low, double high) => compiler.Compile(GateTestPlan.Plan([GateTestPlan.Gate("A",
            detector: GateDetectorKind.FirstRisingCrossing, hysteresis: new(low, high))])).CompiledPlan!.Signature.Hex;
        string Local(int radius, double prominence, int distance) => compiler.Compile(GateTestPlan.Plan([GateTestPlan.Gate("A",
            detector: GateDetectorKind.FirstLocalMaximum, local: new(radius, prominence, distance))])).CompiledPlan!.Signature.Hex;

        Assert.AreNotEqual(Crossing(.4), Crossing(.5));
        Assert.AreNotEqual(Hysteresis(.2, .8), Hysteresis(.3, .8));
        Assert.AreNotEqual(Hysteresis(.2, .8), Hysteresis(.2, .9));
        Assert.AreNotEqual(Local(1, 10, 2), Local(2, 10, 2));
        Assert.AreNotEqual(Local(1, 10, 2), Local(1, 11, 2));
        Assert.AreNotEqual(Local(1, 10, 2), Local(1, 10, 3));
    }

    [TestMethod]
    public void ProcessingCoreReferencesOnlyNeutralProjectsAndAddsNoPackage()
    {
        string root = FindRepositoryRoot();
        AssertProjectReferences(root, "UTStudio.Domain", []);
        AssertProjectReferences(root, "UTStudio.Contracts", ["UTStudio.Domain"]);
        AssertProjectReferences(root, "UTStudio.Processing.Core", ["UTStudio.Contracts", "UTStudio.Domain"]);
    }

    [TestMethod]
    public void ProcessingAssembliesDoNotReferenceWpfApplicationHardwareOrStore()
    {
        string root = FindRepositoryRoot();
        foreach (string relative in new[]
        {
            "src/UTStudio.Domain/Processing", "src/UTStudio.Contracts/Processing", "src/UTStudio.Processing.Core"
        })
        {
            foreach (string file in Directory.GetFiles(Path.Combine(root, relative), "*.cs", SearchOption.AllDirectories))
            {
                string text = File.ReadAllText(file);
                foreach (string forbidden in new[] { "System.Windows", "UTStudio.Application", "Acquisition.Simulator", "Inspection.Core", "Dispatcher", "BitmapSource" })
                { Assert.IsFalse(text.Contains(forbidden, StringComparison.Ordinal), $"{file} contains {forbidden}."); }
            }
        }
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current is not null && !File.Exists(Path.Combine(current.FullName, "UTStudio.sln"))) { current = current.Parent; }
        return current?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }

    private static void AssertProjectReferences(string root, string projectName, string[] expected)
    {
        XDocument project = XDocument.Load(Path.Combine(root, "src", projectName, $"{projectName}.csproj"));
        string[] references = project.Descendants("ProjectReference")
            .Select(x => Path.GetFileNameWithoutExtension((string)x.Attribute("Include")!))
            .Order(StringComparer.Ordinal).ToArray();
        CollectionAssert.AreEqual(expected, references);
        Assert.AreEqual(0, project.Descendants("PackageReference").Count(), $"{projectName} must not add packages.");
    }
}
