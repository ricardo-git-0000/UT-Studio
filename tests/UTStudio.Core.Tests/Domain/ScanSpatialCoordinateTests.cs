using UTStudio.Domain.Acquisition;

namespace UTStudio.Core.Tests.Domain;

[TestClass]
public sealed class ScanSpatialCoordinateTests
{
    [TestMethod]
    public void CoordinateAcceptsFiniteSignedValuesWithTypedUnitAndAxis()
    {
        var axis = new ScanAxisId("scan-x");
        var coordinate = new ScanSpatialCoordinate(-1.25, ScanSpatialUnit.Millimetres, axis);

        Assert.AreEqual(-1.25, coordinate.Value);
        Assert.AreEqual(ScanSpatialUnit.Millimetres, coordinate.Unit);
        Assert.AreEqual(axis, coordinate.AxisId);
        Assert.IsTrue(coordinate.IsValid);
    }

    [TestMethod]
    public void CoordinateRejectsNonFiniteValueInvalidUnitAndInvalidAxis()
    {
        var axis = new ScanAxisId("scan-x");
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            new ScanSpatialCoordinate(double.NaN, ScanSpatialUnit.Metres, axis));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            new ScanSpatialCoordinate(0, (ScanSpatialUnit)0, axis));
        Assert.ThrowsExactly<ArgumentException>(() =>
            new ScanSpatialCoordinate(0, ScanSpatialUnit.Metres, default));
        Assert.ThrowsExactly<ArgumentException>(() => new ScanAxisId(" "));
        Assert.IsFalse(default(ScanSpatialCoordinate).IsValid);
    }
}
