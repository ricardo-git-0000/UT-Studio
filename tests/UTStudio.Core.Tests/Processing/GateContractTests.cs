using UTStudio.Contracts.Processing;
using UTStudio.Domain.Processing;

namespace UTStudio.Core.Tests.Processing;

[TestClass]
public sealed class GateContractTests
{
    [TestMethod]
    public void IdentityDefaultsAreInvalidAndConstructorsRejectEmptyValues()
    {
        Assert.IsFalse(default(GateId).IsValid);
        Assert.IsFalse(default(GatePlanId).IsValid);
        Assert.IsFalse(default(GateRevision).IsValid);
        Assert.IsFalse(default(GatePlanRevision).IsValid);
        Assert.IsFalse(default(GateSchemaVersion).IsValid);
        Assert.IsFalse(default(GateAlgorithmVersion).IsValid);
        Assert.ThrowsExactly<ArgumentException>(() => new GateId(" "));
        Assert.ThrowsExactly<ArgumentException>(() => new GatePlanId(string.Empty));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new GateRevision(0));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new GatePlanRevision(0));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new GateSchemaVersion(0));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new GateAlgorithmVersion(0));
    }

    [TestMethod]
    public void IdentitiesHaveStructuralEqualityStableHashAndSafeDiagnosticFormat()
    {
        var first = new GateId("A"); var same = new GateId("A"); var other = new GateId("a");
        Assert.AreEqual(first, same); Assert.AreEqual(first.GetHashCode(), same.GetHashCode());
        Assert.AreNotEqual(first, other); Assert.AreEqual("A", first.ToString());
        Assert.AreEqual(new GateRevision(2), new GateRevision(2));
        Assert.AreNotEqual(new GateRevision(2), new GateRevision(3));
        Assert.IsFalse(first.ToString().Contains("sample", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public void PlansAndMeasuresCaptureInputCollections()
    {
        List<GateDefinition> gates = [GateTestPlan.Gate("A")];
        GatePlan plan = GateTestPlan.Plan(gates);
        gates.Add(GateTestPlan.Gate("B"));
        Assert.HasCount(1, plan.Gates);

        List<GateMeasureKind> measures = [GateMeasureKind.Status];
        GateDefinition gate = GateTestPlan.Gate("M", measures: measures);
        measures.Add(GateMeasureKind.Quality);
        CollectionAssert.AreEqual(new[] { GateMeasureKind.Status }, gate.RequestedMeasures.ToArray());
        Assert.ThrowsExactly<NotSupportedException>(() => ((IList<GateDefinition>)plan.Gates).Add(gate));
    }

    [TestMethod]
    public void CompilationResultsAreDiscriminatedAndCannotPretendSuccess()
    {
        var error = new GateCompilationError(GateCompilationErrorCode.EmptyPlan, "empty");
        GatePlanCompilationResult failed = GatePlanCompilationResult.Failed(GatePlanCompilationStatus.InvalidPlan, [error]);
        Assert.IsFalse(failed.IsCompiled); Assert.IsNull(failed.CompiledPlan); Assert.HasCount(1, failed.Errors);
        Assert.ThrowsExactly<ArgumentException>(() => GatePlanCompilationResult.Failed(
            GatePlanCompilationStatus.InvalidPlan, []));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => GatePlanCompilationResult.Failed(
            GatePlanCompilationStatus.Compiled, [error]));
    }

    [TestMethod]
    public void PublicProcessingContractsAreNeutralAndRecordsCannotBeMutatedWithInitializers()
    {
        Type[] publicTypes = [.. typeof(GatePlan).Assembly.GetExportedTypes()
            .Where(x => x.Namespace == "UTStudio.Contracts.Processing"),
            .. typeof(GateId).Assembly.GetExportedTypes().Where(x => x.Namespace == "UTStudio.Domain.Processing")];
        Assert.IsFalse(publicTypes.Any(x => x.FullName!.Contains("System.Windows", StringComparison.Ordinal)));
        foreach (Type type in publicTypes)
        {
            foreach (var property in type.GetProperties())
            { Assert.IsNull(property.SetMethod, $"{type.Name}.{property.Name} must not expose a setter."); }
        }
    }

    [TestMethod]
    public void EveryPublicEnumReservesUnknownZero()
    {
        Type[] enums = typeof(GatePlan).Assembly.GetExportedTypes()
            .Where(x => x.Namespace == "UTStudio.Contracts.Processing" && x.IsEnum).ToArray();
        Assert.IsGreaterThan(0, enums.Length);
        foreach (Type type in enums)
        { Assert.AreEqual("Unknown", Enum.GetName(type, 0), $"{type.Name} must reserve zero as Unknown."); }
    }

    [TestMethod]
    public void FirstIncrementVocabularyIsExactAndContainsNoElementIdentityOrRectifiedDomain()
    {
        CollectionAssert.AreEqual(new[] { "Unknown", "RawSigned", "Positive", "NegativeMagnitude", "Absolute" },
            Enum.GetNames<GateSignalDomain>());
        CollectionAssert.AreEqual(new[] { "Unknown", "Amplitude", "DetectionTime", "TimeOfFlight", "Status", "Quality" },
            Enum.GetNames<GateMeasureKind>());
        Type[] processingTypes = [.. typeof(GatePlan).Assembly.GetExportedTypes()
            .Where(x => x.Namespace == "UTStudio.Contracts.Processing"),
            .. typeof(GateId).Assembly.GetExportedTypes().Where(x => x.Namespace == "UTStudio.Domain.Processing")];
        Assert.IsFalse(processingTypes.Any(x => x.Name.Contains("ElementId", StringComparison.Ordinal) ||
            x.Name.Contains("Rectified", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void FutureResultDescriptorCapturesReproducibilityAndOutputSemantics()
    {
        List<GateMeasureKind> measures = [GateMeasureKind.Amplitude, GateMeasureKind.TimeOfFlight];
        var descriptor = new GateFutureResultDescriptor(new("plan"), new(2), new(2), new(2), new("A"), new(4),
            GateSignalDomain.RawSigned, measures,
            GateAmplitudeUnit.DigitalCounts, GateTimeBasis.TraceOrigin, GateTofReferenceKind.DependencyGateEvent,
            new GateId("parent"));
        measures.Clear();
        Assert.HasCount(2, descriptor.Measures);
        Assert.AreEqual(new GatePlanRevision(2), descriptor.PlanRevision);
        Assert.AreEqual(GateTimeBasis.TraceOrigin, descriptor.TimeBasis);
        Assert.AreEqual(new GateId("parent"), descriptor.TofReferenceGateId);
        Assert.ThrowsExactly<ArgumentException>(() => new GateFutureResultDescriptor(new("plan"), new(2), new(1),
            new(2), new("A"), new(4), GateSignalDomain.RawSigned, [GateMeasureKind.Status],
            GateAmplitudeUnit.DigitalCounts, GateTimeBasis.TraceOrigin,
            GateTofReferenceKind.DependencyGateEvent));
        Assert.ThrowsExactly<ArgumentException>(() => new GateFutureResultDescriptor(new("plan"), new(2), new(1),
            new(2), new("A"), new(4), GateSignalDomain.RawSigned, [GateMeasureKind.Status],
            GateAmplitudeUnit.DigitalCounts, GateTimeBasis.TraceOrigin,
            GateTofReferenceKind.TraceOrigin, default(GateId)));
        Assert.ThrowsExactly<ArgumentException>(() => new GateFutureResultDescriptor(new("plan"), new(2), new(1),
            new(2), new("A"), new(4), GateSignalDomain.RawSigned, [GateMeasureKind.Status],
            GateAmplitudeUnit.DigitalCounts, GateTimeBasis.TraceOrigin,
            GateTofReferenceKind.DependencyGateEvent, new GateId("A")));
    }
}
