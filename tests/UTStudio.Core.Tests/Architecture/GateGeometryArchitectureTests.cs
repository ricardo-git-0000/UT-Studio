using System.Reflection;
using UTStudio.Contracts.Inspection;
using UTStudio.Contracts.Processing;
using UTStudio.Processing.Core;
using UTStudio.Visualization.Core;

namespace UTStudio.Core.Tests.Architecture;

[TestClass]
public sealed class GateGeometryArchitectureTests
{
    [TestMethod]
    public void NeutralGeometrySnapshotsDoNotRetainPlansStoresLeasesSamplesOrEvaluators()
    {
        Type[] snapshots = [typeof(GateGeometrySnapshot), typeof(GateGeometryItemSnapshot),
            typeof(GateAnchorSnapshot), typeof(GateGeometryEvaluationResult)];
        Type[] forbidden = [typeof(CompiledGatePlan), typeof(IInspectionDataStore),
            typeof(IInspectionTraceLease), typeof(IReadOnlyUtSamples), typeof(GateEvaluator)];
        foreach (Type snapshot in snapshots)
        {
            Type[] exposed = snapshot.GetProperties(BindingFlags.Instance | BindingFlags.Public)
                .Select(property => Unwrap(property.PropertyType)).ToArray();
            foreach (Type type in forbidden)
                Assert.IsFalse(exposed.Contains(type), $"{snapshot.Name} exposes {type.Name}.");
        }
    }

    [TestMethod]
    public void VisualizationCoreRemainsIndependentOfApplicationProcessingCoreAndWpf()
    {
        string[] references = typeof(GateGeometryProjector).Assembly.GetReferencedAssemblies()
            .Select(reference => reference.Name!).ToArray();
        CollectionAssert.DoesNotContain(references, "UTStudio.Application");
        CollectionAssert.DoesNotContain(references, "UTStudio.Processing.Core");
        Assert.IsFalse(references.Any(name => name.Contains("Wpf", StringComparison.OrdinalIgnoreCase) ||
            name is "PresentationFramework" or "PresentationCore" or "WindowsBase"));
    }

    private static Type Unwrap(Type type)
    {
        if (type.IsArray) return type.GetElementType()!;
        if (type.IsGenericType) return type.GetGenericArguments().Last();
        return type;
    }
}
