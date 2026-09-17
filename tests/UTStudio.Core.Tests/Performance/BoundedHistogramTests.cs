using UTStudio.LoadTests;

namespace UTStudio.Core.Tests.Performance;

[TestClass]
public sealed class BoundedHistogramTests
{
    [TestMethod]
    public void EmptySnapshotReportsNoPopulation()
    {
        var snapshot = new BoundedHistogram(4).Snapshot();
        Assert.AreEqual(0, snapshot.Population);
        Assert.AreEqual(0, snapshot.WindowPopulation);
        Assert.IsTrue(double.IsNaN(snapshot.P50));
    }

    [TestMethod]
    public void NearestRankPercentilesAreDeterministic()
    {
        var histogram = new BoundedHistogram(16);
        foreach (double value in new[] { 10d, 1, 9, 2, 8, 3, 7, 4, 6, 5 }) { histogram.Record(value); }
        var snapshot = histogram.Snapshot();
        Assert.AreEqual(10, snapshot.Population);
        Assert.AreEqual(10, snapshot.WindowPopulation);
        Assert.AreEqual(5.5, snapshot.Mean);
        Assert.AreEqual(5d, snapshot.P50);
        Assert.AreEqual(10d, snapshot.P95);
        Assert.AreEqual(10d, snapshot.P99);
    }

    [TestMethod]
    public void StorageIsBoundedToMostRecentValues()
    {
        var histogram = new BoundedHistogram(3);
        foreach (double value in new[] { 1d, 2, 3, 100 }) { histogram.Record(value); }
        var snapshot = histogram.Snapshot();
        Assert.AreEqual(4, snapshot.Population);
        Assert.AreEqual(3, snapshot.WindowPopulation);
        Assert.AreEqual(35d, snapshot.Mean);
        Assert.AreEqual(3d, snapshot.P50);
        Assert.AreEqual(100d, snapshot.P99);
    }
}
