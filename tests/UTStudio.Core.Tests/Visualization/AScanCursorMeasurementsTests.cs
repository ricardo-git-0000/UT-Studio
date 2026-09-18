using UTStudio.Domain.Acquisition;
using UTStudio.Visualization.Core;

namespace UTStudio.Core.Tests.Visualization;

[TestClass]
public sealed class AScanCursorMeasurementsTests
{
    [TestMethod]
    public void PhysicalTimeUsesSampleRateCountAndOffsetAndRfIsSignedPercent()
    {
        var snapshot = Snapshot([short.MinValue, 0, short.MaxValue, 16384], 2_000_000, 3e-6);
        Assert.AreEqual(3e-6, snapshot.MinimumTimeSeconds);
        Assert.AreEqual(4.5e-6, snapshot.MaximumTimeSeconds);
        CollectionAssert.AreEqual(new[] { 3e-6, 3.5e-6, 4e-6, 4.5e-6 },
            snapshot.Points.Select(point => point.TimeSeconds).ToArray());
        Assert.AreEqual(-100, snapshot.Points[0].AmplitudePercent);
        Assert.AreEqual(0, snapshot.Points[1].AmplitudePercent);
        Assert.AreEqual(100.0 * short.MaxValue / 32768, snapshot.Points[2].AmplitudePercent);
        Assert.AreEqual(50, snapshot.Points[3].AmplitudePercent);
    }

    [TestMethod]
    public void MoveClampsAndDeltasKeepTheirSign()
    {
        var snapshot = Snapshot([short.MinValue, 0, short.MaxValue, 16384], 1_000_000, 10e-6);
        var state = AScanCursorMeasurements.Create(snapshot);
        state = AScanCursorMeasurements.Move(state, snapshot, AScanCursorId.A, 20e-6);
        state = AScanCursorMeasurements.Move(state, snapshot, AScanCursorId.B, -1);
        Assert.AreEqual(snapshot.MaximumTimeSeconds, state.A.TimeSeconds);
        Assert.AreEqual(snapshot.MinimumTimeSeconds, state.B.TimeSeconds);
        Assert.IsLessThan(0, state.DeltaTimeSeconds);
        Assert.IsLessThan(0, state.DeltaAmplitudePercent);

        state = AScanCursorMeasurements.Move(state, snapshot, AScanCursorId.A, snapshot.MinimumTimeSeconds);
        state = AScanCursorMeasurements.Move(state, snapshot, AScanCursorId.B, snapshot.MaximumTimeSeconds);
        Assert.IsGreaterThan(0, state.DeltaTimeSeconds);
        Assert.IsGreaterThan(0, state.DeltaAmplitudePercent);
    }

    [TestMethod]
    public void ReducedSignalReportsNearestRetainedPointWithoutClaimingOriginalSample()
    {
        short[] samples = Enumerable.Range(0, 32).Select(index => (short)(index * 100)).ToArray();
        var snapshot = Snapshot(samples, 1_000_000, 0, maximumPoints: 4);
        Assert.IsLessThan(samples.Length, snapshot.Points.Count);
        double requested = 12.4e-6;
        var state = AScanCursorMeasurements.Move(AScanCursorMeasurements.Create(snapshot), snapshot,
            AScanCursorId.A, requested);
        Assert.AreEqual(requested, state.A.TimeSeconds);
        Assert.AreEqual(snapshot.Points[state.A.RepresentedPointIndex].TimeSeconds, state.A.RepresentedPointTimeSeconds);
        Assert.AreEqual(snapshot.Points[state.A.RepresentedPointIndex].AmplitudePercent, state.A.AmplitudePercent);
        Assert.AreNotEqual(state.A.TimeSeconds, state.A.RepresentedPointTimeSeconds);
    }

    [TestMethod]
    public void NewSnapshotPreservesRequestedTimesWhileRefreshingNearestAmplitudes()
    {
        var first = Snapshot([0, 100, 200, 300], 1_000_000, 5e-6);
        var state = AScanCursorMeasurements.Create(first);
        state = AScanCursorMeasurements.Move(state, first, AScanCursorId.A, 6.2e-6);
        state = AScanCursorMeasurements.Move(state, first, AScanCursorId.B, 7.4e-6);
        var second = Snapshot([300, 200, 100, 0], 1_000_000, 5e-6);
        var refreshed = AScanCursorMeasurements.Reconcile(state, second);
        Assert.AreEqual(state.A.TimeSeconds, refreshed.A.TimeSeconds);
        Assert.AreEqual(state.B.TimeSeconds, refreshed.B.TimeSeconds);
        Assert.AreNotEqual(state.A.AmplitudePercent, refreshed.A.AmplitudePercent);
        Assert.AreNotEqual(state.B.AmplitudePercent, refreshed.B.AmplitudePercent);
    }

    [TestMethod]
    public void VisibilityActiveCursorAndResetRemainImmutable()
    {
        var snapshot = Snapshot([0, 1, 2, 3], 1_000_000, 0);
        var original = AScanCursorMeasurements.Create(snapshot);
        var hidden = original with { IsVisible = false, ActiveCursor = AScanCursorId.B };
        var reset = AScanCursorMeasurements.Reset(hidden, snapshot);
        Assert.IsTrue(original.IsVisible);
        Assert.IsFalse(reset.IsVisible);
        Assert.AreEqual(AScanCursorId.B, reset.ActiveCursor);
        Assert.AreEqual(snapshot.MinimumTimeSeconds + (snapshot.MaximumTimeSeconds - snapshot.MinimumTimeSeconds) * .25,
            reset.A.TimeSeconds);
    }

    private static AScanSnapshot Snapshot(short[] samples, double sampleRate, double offset, int maximumPoints = 1024)
    {
        var configuration = new ConventionalAcquisitionConfiguration(new PhysicalChannelId(0), samples.Length, sampleRate,
            firstSampleOffsetSeconds: offset);
        var metadata = new ConventionalUtFrameMetadata(new UtSourceId("cursor-test"),
            new AcquisitionRunId(Guid.Parse("11111111-1111-1111-1111-111111111111")), configuration,
            DateTimeOffset.UnixEpoch);
        return new AScanProjector(maximumPoints).Project(metadata, 1, TimeSpan.Zero, samples);
    }
}
