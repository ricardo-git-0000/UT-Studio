using System.Buffers;
using System.Diagnostics;
using System.Threading.Channels;
using UTStudio.Acquisition.Simulator;
using UTStudio.Contracts.Acquisition;
using UTStudio.Domain.Acquisition;
using UTStudio.LoadTests;
namespace UTStudio.Core.Tests.Performance;

[TestClass]
public sealed class MeasurementTests
{
    [TestMethod]
    public void LatePacingCountsOmissionsWithoutHidingDelay()
    {
        var demand = new DemandSchedule(1000, 1_000_000);
        demand.Offer(0); demand.Offer(5700);
        var snapshot = demand.Snapshot(8000);
        Assert.AreEqual(9, snapshot.Scheduled);
        Assert.AreEqual(2, snapshot.Offered);
        Assert.AreEqual(7, snapshot.Missed);
        Assert.AreEqual(4700d, snapshot.MaximumDelayMicroseconds);
        Assert.AreEqual(TimeSpan.FromMicroseconds(300), demand.UntilNext(5700));
    }
    [TestMethod]
    public void ConcurrentRingOverwriteCannotMixTimestampAndSequence()
    {
        var ring = new CorrelationRing(2);
        ring.Write(0, 100);
        Assert.IsFalse(ring.TryRead(0, out _, () => ring.Write(2, 200)));
        Assert.AreEqual(1, ring.Misses);
        Assert.AreEqual(1, ring.Overwrites);
        Assert.IsTrue(ring.TryRead(2, out long timestamp));
        Assert.AreEqual(200, timestamp);
    }
    [TestMethod]
    public void WatchdogTracksProgressAndCpuUsesActualInterval()
    {
        var watchdog = new ProgressWatchdog(0, 0, 10);
        Assert.IsFalse(watchdog.Expired(9, 0));
        Assert.IsFalse(watchdog.Expired(10, 1));
        Assert.IsTrue(watchdog.Expired(20, 1));
        Assert.AreEqual(25d, CpuMeasurement.Percent(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), 4));
        Assert.AreEqual(0d, CpuMeasurement.Percent(TimeSpan.Zero, TimeSpan.Zero, 4));
    }
    [TestMethod]
    public void SeriesRetainsChronologicalBoundedTail()
    {
        var series = new ResourceSeries(2);
        for (int i = 0; i < 5; i++) { series.Add(new(i, 1, 0, i, i, i, i)); }
        var values = series.Snapshot();
        Assert.AreEqual(5, series.TotalSamples);
        Assert.HasCount(2, values);
        Assert.AreEqual(3d, values[0].ElapsedSeconds);
        Assert.AreEqual(4d, values[1].ElapsedSeconds);
    }
    [TestMethod]
    public async Task LiveCountersAdvanceAndActiveWindowExcludesDrain()
    {
        var metrics = new AcquisitionMetrics();
        var channel = Channel.CreateBounded<ConventionalUtFrame>(new BoundedChannelOptions(4) { AllowSynchronousContinuations = false });
        var reader = metrics.Reader(channel.Reader);
        var metadata = new ConventionalUtFrameMetadata(new("test"), new(Guid.NewGuid()), new(new(0), 2048, 50_000_000), DateTimeOffset.UnixEpoch);
        var start = metrics.Snapshot();
        for (ulong i = 0; i < 2; i++)
        {
            metrics.Offer();
            var owner = metrics.Track(new Owner());
            var frame = new ConventionalUtFrame(metadata, i, TimeSpan.Zero, owner);
            owner.Generated();
            await metrics.WriteAsync(channel.Writer, frame, CancellationToken.None).AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        }
        Assert.IsTrue(reader.TryRead(out var first)); first.Dispose();
        var end = metrics.Snapshot();
        Assert.AreEqual(2, end.Accepted);
        Assert.AreEqual(1, end.Consumed);
        Assert.IsGreaterThan(0d, new RateWindow(start, end).ConsumedPerSecond);
        Assert.AreEqual(1, end.InTransit);
        Assert.IsTrue(reader.TryRead(out var second)); second.Dispose();
        Assert.AreEqual(1, metrics.Snapshot().Consumed - end.Consumed);
        Assert.AreEqual(1, end.Consumed - start.Consumed);
    }
    private sealed class Owner : IMemoryOwner<short>
    {
        public Memory<short> Memory { get; } = new short[2048];
        public void Dispose() { }
    }

    [TestMethod]
    public void ClosedWindowRejectsLateRecordsAndDemandSharesCounterCut()
    {
        var telemetry = new LoadTelemetry(1000);
        var start = telemetry.BeginWindow();
        telemetry.Counters.Offer();
        var end = telemetry.EndWindow(out var demand);
        telemetry.Counters.Offer();
        telemetry.Projection(start.Timestamp, end.Timestamp + 1);
        telemetry.Projection(start.Timestamp, end.Timestamp - 1);
        Assert.AreEqual(end.Offered, demand.Offered);
        Assert.AreEqual(1, end.Offered);
        Assert.AreEqual(2, telemetry.Counters.Snapshot().Offered);
        Assert.AreEqual(1, telemetry.ProjectionMicroseconds.Count);
    }
}
