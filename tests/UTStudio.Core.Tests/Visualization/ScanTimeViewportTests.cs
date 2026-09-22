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

    [TestMethod]
    public void ZoomAnchorIsInvariantAndLimitsSaturate()
    {
        var viewport = ScanTimeViewportOperations.Create(-50e-6, 50e-6);
        const double anchor = -17e-6;
        double ratio = (anchor - viewport.VisibleMinimumSeconds) / viewport.VisibleSpanSeconds;
        var zoomed = ScanTimeViewportOperations.Zoom(viewport, anchor, 2.75);
        double coordinateAfter = (anchor - zoomed.VisibleMinimumSeconds) / zoomed.VisibleSpanSeconds;
        Assert.AreEqual(ratio, coordinateAfter, 1e-12);

        for (int i = 0; i < 100; i++) { zoomed = ScanTimeViewportOperations.Zoom(zoomed, anchor, 2); }
        Assert.AreEqual(viewport.DomainSpanSeconds / ScanTimeViewportOperations.MaximumZoomFactor,
            zoomed.VisibleSpanSeconds, 1e-15);
        for (int i = 0; i < 100; i++) { zoomed = ScanTimeViewportOperations.Zoom(zoomed, anchor, .5); }
        Assert.IsTrue(zoomed.IsReset);
    }

    [TestMethod]
    public void LinearTransformRoundTripsTimeAndReversedAmplitudeAxis()
    {
        Assert.IsTrue(LinearAxisTransform.TryDataToCoordinate(-10e-6, -50e-6, 50e-6, 64, 500, out double x));
        Assert.IsTrue(LinearAxisTransform.TryCoordinateToData(x, 64, 500, -50e-6, 50e-6, out double time));
        Assert.AreEqual(-10e-6, time, 1e-15);

        Assert.IsTrue(LinearAxisTransform.TryDataToCoordinate(25, -100, 100, 200, -200, out double y));
        Assert.IsTrue(LinearAxisTransform.TryCoordinateToData(y, 200, -200, -100, 100, out double amplitude));
        Assert.AreEqual(25, amplitude, 1e-12);
        Assert.IsFalse(LinearAxisTransform.TryDataToCoordinate(0, 0, 1, 0, 0, out _));
    }
}
