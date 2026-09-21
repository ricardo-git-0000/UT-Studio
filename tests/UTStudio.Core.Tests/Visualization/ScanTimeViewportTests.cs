using UTStudio.Visualization.Core;

namespace UTStudio.Core.Tests.Visualization;

[TestClass]
public sealed class ScanTimeViewportTests
{
    [TestMethod]
    public void ZoomKeepsPhysicalAnchorAndPanClampsToDomain()
    {
        var initial = ScanTimeViewportOperations.Create(0, 100e-6);
        var zoomed = ScanTimeViewportOperations.Zoom(initial, 25e-6, 2);
        Assert.AreEqual(50e-6, zoomed.VisibleSpanSeconds, 1e-15);
        Assert.AreEqual(12.5e-6, zoomed.VisibleMinimumSeconds, 1e-15);
        Assert.AreEqual(62.5e-6, zoomed.VisibleMaximumSeconds, 1e-15);

        var right = ScanTimeViewportOperations.Pan(zoomed, 1);
        Assert.AreEqual(50e-6, right.VisibleMinimumSeconds, 1e-15);
        Assert.AreEqual(100e-6, right.VisibleMaximumSeconds, 1e-15);
        var left = ScanTimeViewportOperations.Pan(right, -1);
        Assert.AreEqual(0, left.VisibleMinimumSeconds, 1e-15);
        Assert.AreEqual(50e-6, left.VisibleMaximumSeconds, 1e-15);
    }

    [TestMethod]
    public void ResetAndDomainReconciliationAreImmutableAndBounded()
    {
        var initial = ScanTimeViewportOperations.Create(10e-6, 110e-6);
        var zoomed = ScanTimeViewportOperations.Zoom(initial, 60e-6, 4);
        var reconciled = ScanTimeViewportOperations.Reconcile(zoomed, 40e-6, 80e-6);
        Assert.AreEqual(25e-6, reconciled.VisibleSpanSeconds, 1e-15);
        Assert.IsGreaterThanOrEqualTo(40e-6, reconciled.VisibleMinimumSeconds);
        Assert.IsLessThanOrEqualTo(80e-6, reconciled.VisibleMaximumSeconds);

        var reset = ScanTimeViewportOperations.Reset(reconciled);
        Assert.IsTrue(reset.IsReset);
        Assert.AreEqual(40e-6, reset.VisibleMinimumSeconds, 1e-15);
        Assert.AreEqual(80e-6, reset.VisibleMaximumSeconds, 1e-15);
        Assert.IsFalse(zoomed.IsReset);
    }

    [TestMethod]
    public void InvalidDomainsFactorsAndDeltasAreRejected()
    {
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => ScanTimeViewportOperations.Create(1, 1));
        var viewport = ScanTimeViewportOperations.Create(0, 1);
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => ScanTimeViewportOperations.Zoom(viewport, 0.5, 0));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => ScanTimeViewportOperations.Pan(viewport, double.NaN));
    }
}
