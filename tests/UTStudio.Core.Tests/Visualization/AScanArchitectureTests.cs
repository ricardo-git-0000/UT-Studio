using System.Reflection;
using UTStudio.Contracts.Inspection;
using UTStudio.Visualization.Core;

namespace UTStudio.Core.Tests.Visualization;

[TestClass]
public sealed class AScanArchitectureTests
{
    [TestMethod]
    public void DeliveryExposesOnlyConfirmedIdentityIngress()
    {
        MethodInfo[] declaredMethods = typeof(AScanVisualDelivery).GetMethods(
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance |
            BindingFlags.DeclaredOnly);

        Assert.IsFalse(declaredMethods.Any(method => method.Name == "Accept"),
            "AScanVisualDelivery must not expose a sample-based Accept seam.");

        MethodInfo notify = declaredMethods.Single(method => method.Name == nameof(
            AScanVisualDelivery.NotifyCommitted));
        CollectionAssert.AreEqual(
            new[] { typeof(TraceIdentity), typeof(InspectionRevision) },
            notify.GetParameters().Select(parameter => parameter.ParameterType).ToArray());
    }

    [TestMethod]
    public void EveryPublicDeliveryCompositionRequiresTraceAuthority()
    {
        ConstructorInfo[] constructors = typeof(AScanVisualDelivery).GetConstructors();
        Assert.IsNotEmpty(constructors);
        Assert.IsTrue(constructors.All(constructor =>
            constructor.GetParameters().Any(parameter =>
                parameter.ParameterType == typeof(IUtTraceProvider))),
            "Every public A-Scan composition must require IUtTraceProvider.");
        Assert.IsTrue(constructors.SelectMany(constructor => constructor.GetParameters())
            .Where(parameter => parameter.ParameterType == typeof(IUtTraceProvider))
            .All(parameter => !parameter.IsOptional));
    }
}
