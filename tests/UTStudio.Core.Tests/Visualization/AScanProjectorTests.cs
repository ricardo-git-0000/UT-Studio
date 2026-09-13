using System.Buffers;
using UTStudio.Contracts.Acquisition;
using UTStudio.Visualization.Core;
using static UTStudio.Core.Tests.Visualization.VisualTestSupport;

namespace UTStudio.Core.Tests.Visualization;

[TestClass]
public sealed class AScanProjectorTests
{
    [TestMethod]
    [DataRow(1)]
    [DataRow(32)]
    [DataRow(1023)]
    [DataRow(1024)]
    public void SignalsWithinLimitPreserveEverySample(int count)
    {
        var metadata = Metadata(count, offset: -0.00001);
        var samples = Enumerable.Range(0, count).Select(i => (short)(i - 512)).ToArray();
        var snapshot = new AScanProjector().Project(metadata, 17, TimeSpan.FromMilliseconds(2), samples, 23);
        Assert.HasCount(count, snapshot.Points);
        Assert.AreSame(metadata, snapshot.Metadata);
        Assert.AreEqual(17UL, snapshot.Sequence);
        Assert.AreEqual(23UL, snapshot.Version);
        Assert.AreEqual(TimeSpan.FromMilliseconds(2), snapshot.ElapsedSinceRunStart);
        for (int i = 0; i < count; i++)
        {
            Assert.AreEqual(metadata.Configuration.FirstSampleOffsetSeconds + i / 50_000_000.0, snapshot.Points[i].TimeSeconds);
            Assert.AreEqual(100.0 * samples[i] / 32768, snapshot.Points[i].AmplitudePercent);
        }
        Assert.IsLessThan(snapshot.MaximumTimeSeconds, snapshot.MinimumTimeSeconds);
    }

    [TestMethod]
    [DataRow(1025)]
    [DataRow(2048)]
    [DataRow(65535)]
    public void ReductionPreservesEveryBucketExtremaAndLastInterval(int count)
    {
        var metadata = Metadata(count);
        var samples = Enumerable.Range(0, count).Select(i => (short)((i * 7919L % 60001) - 30000)).ToArray();
        samples[count - 3] = short.MinValue;
        samples[count - 2] = short.MaxValue;
        var snapshot = new AScanProjector().Project(metadata, 0, TimeSpan.Zero, samples);
        Assert.IsLessThanOrEqualTo(1024, snapshot.Points.Count);
        Assert.AreEqual(0.0, snapshot.Points[0].TimeSeconds);
        Assert.AreEqual((count - 1) / 50_000_000.0, snapshot.Points[^1].TimeSeconds);
        Assert.AreEqual(snapshot.Points[^1].TimeSeconds, snapshot.MaximumTimeSeconds);
        for (int index = 1; index < snapshot.Points.Count; index++)
        {
            Assert.IsGreaterThan(snapshot.Points[index - 1].TimeSeconds, snapshot.Points[index].TimeSeconds);
        }
        for (int group = 0; group < 511; group++)
        {
            int start = 1 + group * (count - 2) / 511;
            int end = 1 + (group + 1) * (count - 2) / 511;
            var bucket = samples[start..end];
            var emitted = snapshot.Points.Where(point => point.TimeSeconds >= start / 50_000_000.0 && point.TimeSeconds < end / 50_000_000.0).ToArray();
            Assert.AreEqual(100.0 * bucket.Min() / 32768, emitted.Min(point => point.AmplitudePercent));
            Assert.AreEqual(100.0 * bucket.Max() / 32768, emitted.Max(point => point.AmplitudePercent));
        }
        Assert.IsTrue(snapshot.Points.Any(point => point.TimeSeconds == (count - 2) / 50_000_000.0));
    }

    [TestMethod]
    [DataRow(4)]
    [DataRow(5)]
    [DataRow(128)]
    [DataRow(1024)]
    public void ConfigurableBudgetAndConstantSignalChooseFirstTie(int limit)
    {
        short[] samples = Enumerable.Repeat((short)100, 2048).ToArray();
        var snapshot = new AScanProjector(limit).Project(Metadata(), 0, TimeSpan.Zero, samples);
        int groups = (limit - 2) / 2;
        Assert.HasCount(groups + 2, snapshot.Points);
        for (int group = 0; group < groups; group++)
        {
            Assert.AreEqual((1 + group * 2046 / groups) / 50_000_000.0, snapshot.Points[group + 1].TimeSeconds);
        }
        Assert.AreEqual(2047 / 50_000_000.0, snapshot.Points[^1].TimeSeconds);
    }

    [TestMethod]
    public void SnapshotSurvivesOwnerReturnAndBufferOverwrite()
    {
        var samples = new short[] { short.MinValue, 0, short.MaxValue, -100 };
        var owner = new ArrayOwner(samples);
        var frame = new ConventionalUtFrame(Metadata(4), 0, TimeSpan.Zero, owner);
        AScanSnapshot snapshot;
        try { snapshot = new AScanProjector().Project(frame.Metadata, frame.Sequence, frame.ElapsedSinceRunStart, frame.Samples.Span); }
        finally { frame.Dispose(); }
        Array.Fill(samples, (short)42);
        Assert.AreEqual(-100.0, snapshot.Points[0].AmplitudePercent);
        Assert.AreEqual(100.0 * short.MaxValue / 32768, snapshot.Points[2].AmplitudePercent);
        Assert.AreEqual(-100.0, snapshot.MinimumAmplitudePercent);
        Assert.AreEqual(100.0, snapshot.MaximumAmplitudePercent);
        Assert.Throws<NotSupportedException>(() => ((IList<AScanPoint>)snapshot.Points)[0] = new(0, 0));
        Assert.Throws<ObjectDisposedException>(() => _ = frame.Samples);
        Assert.AreEqual(1, owner.Returns);
    }

    [TestMethod]
    public void SingleSampleHasNominalCenteredViewport()
    {
        var snapshot = new AScanProjector().Project(Metadata(1), 0, TimeSpan.Zero, new short[] { 42 });
        Assert.AreEqual(-1 / 100_000_000.0, snapshot.MinimumTimeSeconds);
        Assert.AreEqual(1 / 100_000_000.0, snapshot.MaximumTimeSeconds);
        Assert.HasCount(1, snapshot.Points);
    }

    [TestMethod]
    public void InvalidBudgetLengthAndUnrepresentableAxisAreRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new AScanProjector(3));
        Assert.Throws<ArgumentOutOfRangeException>(() => new AScanProjector(1025));
        var projector = new AScanProjector();
        Assert.Throws<ArgumentException>(() => projector.Project(Metadata(4), 0, TimeSpan.Zero, new short[3]));
        Assert.Throws<ArgumentException>(() => projector.Project(Metadata(4, double.Epsilon), 0, TimeSpan.Zero, new short[4]));
        Assert.Throws<ArgumentException>(() => projector.Project(Metadata(4, offset: double.MaxValue), 0, TimeSpan.Zero, new short[4]));
    }

    private sealed class ArrayOwner(short[] samples) : IMemoryOwner<short>
    {
        internal int Returns { get; private set; }
        public Memory<short> Memory => samples;
        public void Dispose() { Returns++; }
    }
}
