using UTStudio.App.Wpf.Controls;
using UTStudio.App.Wpf.Services;
using UTStudio.Visualization.Core;

namespace UTStudio.Tests.Wpf;

[TestClass]
public sealed class CoordinatesTests
{
    [TestMethod]
    public void RfExtremesAndOffsetMapToViewportAndResize()
    {
        AScanPoint[] points = [new(10, 100), new(15, 0), new(20, -100)];
        CollectionAssert.AreEqual(new[] { new PlotCoordinate(0, 0), new(100, 50), new(200, 100) },
            AScanCoordinates.Map(points, 10, 20, 200, 100));
        CollectionAssert.AreEqual(new[] { new PlotCoordinate(0, 0), new(200, 100), new(400, 200) },
            AScanCoordinates.Map(points, 10, 20, 400, 200));
    }

    [TestMethod]
    public void ConstantAndSingleSignalsKeepFixedSignedScale()
    {
        var constant = AScanCoordinates.Map([new(0, 50), new(1, 50)], 0, 1, 100, 80);
        Assert.AreEqual(20d, constant[0].Y);
        Assert.AreEqual(20d, constant[1].Y);
        Assert.AreEqual(new PlotCoordinate(50, 40), AScanCoordinates.Map([new(1, 0)], .5, 1.5, 100, 80)[0]);
    }

    [TestMethod]
    [DataRow(0d, 100d)]
    [DataRow(100d, 0d)]
    [DataRow(-1d, 100d)]
    [DataRow(double.NaN, 100d)]
    public void DegenerateDimensionsProduceNoCoordinates(double width, double height) =>
        Assert.IsEmpty(AScanCoordinates.Map([new(0, 0)], 0, 1, width, height));

    [TestMethod]
    public void InvalidTimeRangeAndInvalidPointsAreRejected()
    {
        Assert.IsEmpty(AScanCoordinates.Map([new(0, 0)], 0, 0, 100, 100));
        Assert.IsEmpty(AScanCoordinates.Map([new(double.NaN, 0)], 0, 1, 100, 100));
        Assert.IsEmpty(AScanCoordinates.Map([], 0, 1, 100, 100));
    }

    [TestMethod]
    public void RefreshAdmissionUsesActualTimeWithoutCatchUp()
    {
        var clock = new ManualClock();
        var gate = new RefreshGate(clock, AScanVisualDelivery.MinimumPublicationInterval);
        Assert.IsTrue(gate.TryEnter());
        clock.Advance(TimeSpan.FromTicks(333_333));
        Assert.IsFalse(gate.TryEnter());
        clock.Advance(TimeSpan.FromTicks(1));
        Assert.IsTrue(gate.TryEnter());
        clock.Advance(TimeSpan.FromSeconds(10));
        Assert.IsTrue(gate.TryEnter());
        Assert.IsFalse(gate.TryEnter());
        var metrics = new RefreshGate(clock, TimeSpan.FromMilliseconds(200));
        Assert.IsTrue(metrics.TryEnter());
        clock.Advance(TimeSpan.FromMilliseconds(199));
        Assert.IsFalse(metrics.TryEnter());
        clock.Advance(TimeSpan.FromMilliseconds(1));
        Assert.IsTrue(metrics.TryEnter());
    }
}
