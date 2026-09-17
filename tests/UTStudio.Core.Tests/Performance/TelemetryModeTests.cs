using System.Diagnostics;
using System.Text.Json;
using UTStudio.Core.Tests.Simulator;
using UTStudio.LoadTests;
using UTStudio.Visualization.Core;
using static UTStudio.Core.Tests.Visualization.VisualTestSupport;

namespace UTStudio.Core.Tests.Performance;

[TestClass]
[DoNotParallelize]
public sealed class TelemetryModeTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void DetailedProbesAreGatedAtCollection(bool full)
    {
        var telemetry = new LoadTelemetry(1000, full ? TelemetryMode.Full : TelemetryMode.Minimal);
        var start = telemetry.BeginWindow();
        telemetry.PrepareFrame(7);
        telemetry.ObserveVisual(7);
        telemetry.Projection(start.Timestamp, Stopwatch.GetTimestamp());
        telemetry.EndWindow(out _);
        Assert.AreEqual(full ? 1L : 0L, telemetry.AcceptToCallbackMicroseconds.Count);
        Assert.AreEqual(full ? 1L : 0L, telemetry.ProjectionMicroseconds.Count);
        if (full)
        {
            Assert.IsNotNull(telemetry.Correlation);
            Assert.IsTrue(telemetry.Correlation.TryRead(7, out long timestamp));
            Assert.IsGreaterThanOrEqualTo(start.Timestamp, timestamp);
        }
        else
        {
            // No ring exists, so even a first write (which is not an overwrite) is impossible.
            Assert.IsNull(telemetry.Correlation);
            Assert.IsNull(telemetry.CorrelationMisses);
            Assert.IsNull(telemetry.CorrelationOverwrites);
        }
    }

    [TestMethod]
    public void MinimalRetainsDemandAndPacingWithoutDelayStatistics()
    {
        var minimal = new DemandSchedule(1000, 1_000_000, detailed: false);
        var full = new DemandSchedule(1000, 1_000_000);
        foreach (var demand in new[] { minimal, full }) { demand.Offer(0); demand.Offer(5700); }
        var snapshot = minimal.Snapshot(8000);
        Assert.AreEqual(9L, snapshot.Scheduled);
        Assert.AreEqual(2L, snapshot.Offered);
        Assert.AreEqual(7L, snapshot.Missed);
        Assert.AreEqual(full.UntilNext(5700), minimal.UntilNext(5700));
        Assert.IsNull(snapshot.MeanDelayMicroseconds);
        Assert.IsNull(snapshot.MaximumDelayMicroseconds);
        Assert.AreEqual(4700d, full.Snapshot(8000).MaximumDelayMicroseconds);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task TelemetryPreservesProjectionMailboxAndPublication(bool full)
    {
        var telemetry = new LoadTelemetry(mode: full ? TelemetryMode.Full : TelemetryMode.Minimal);
        var clock = new ManualSimulatorTimeProvider();
        await using var delivery = new AScanVisualDelivery(new InstrumentedProjector(new AScanProjector(4), telemetry), clock);
        var observer = new Observer(snapshot => telemetry.ObserveVisual(snapshot.Sequence));
        using var subscription = delivery.Subscribe(observer);
        var sink = new InstrumentedFrameSink(delivery, telemetry);
        var metadata = Metadata(8);
        short[] samples = [0, 100, -200, 300, -400, 500, -600, 700];
        sink.OpenRun(metadata.RunId);
        telemetry.BeginWindow();
        sink.Accept(metadata, 0, TimeSpan.Zero, samples);
        var first = await observer.NextAsync();
        sink.Accept(metadata, 1, TimeSpan.FromTicks(1), samples);
        var timer = await clock.NextTimerAsync();
        sink.Accept(metadata, 2, TimeSpan.FromTicks(2), samples);
        Assert.IsTrue(delivery.Statistics.HasPending);
        Assert.AreEqual(1L, delivery.Statistics.Replaced);
        timer.Fire();
        var latest = await observer.NextAsync();
        telemetry.EndWindow(out _);
        Assert.AreEqual(2UL, latest.Sequence);
        Assert.AreEqual(3UL, latest.Version);
        Assert.HasCount(4, latest.Points);
        CollectionAssert.AreEqual(first.Points.ToArray(), latest.Points.ToArray());
        Assert.AreEqual(3L, delivery.Statistics.Received);
        Assert.AreEqual(2L, delivery.Statistics.Published);
        Assert.AreEqual(full ? 3L : 0L, telemetry.ProjectionMicroseconds.Count);
        Assert.AreEqual(full ? 2L : 0L, telemetry.AcceptToCallbackMicroseconds.Count);
        sink.CloseRun(metadata.RunId);
        Assert.IsFalse(delivery.Statistics.HasPending);
        Assert.IsNull(delivery.Current);
    }

    [TestMethod]
    public async Task FullWithoutObservationsSerializesNullHistograms()
    {
        var options = new LoadOptions(LoadProfile.Smoke, 2048, 1000, TimeSpan.FromSeconds(1));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var result = await new LoadRunner().RunAsync(options, cancellation.Token).WaitAsync(TimeSpan.FromSeconds(15));
        Assert.IsNull(result.VisualDeliveryLatencyMicroseconds);
        Assert.IsNull(result.ProjectionMicroseconds);
        string path = Path.Combine(Path.GetTempPath(), $"ut-empty-telemetry-{Guid.NewGuid():N}.json");
        try
        {
            await ResultWriter.WriteAsync(path, options, result, FunctionalCriteria.Evaluate(result), FunctionalCriteria.ExitCode(result));
            using var json = JsonDocument.Parse(await File.ReadAllTextAsync(path));
            var values = json.RootElement.GetProperty("result");
            Assert.AreEqual(JsonValueKind.Null, values.GetProperty("VisualDeliveryLatencyMicroseconds").ValueKind);
            Assert.AreEqual(JsonValueKind.Null, values.GetProperty("ProjectionMicroseconds").ValueKind);
        }
        finally { File.Delete(path); }
    }

    [TestMethod]
    [DataRow(false, false)]
    [DataRow(false, true)]
    [DataRow(true, false)]
    [DataRow(true, true)]
    public async Task RunnerPreservesIntegrityAndSerializesAvailabilityRegardlessOfQuiet(bool full, bool quiet)
    {
        var options = new LoadOptions(LoadProfile.Smoke, 2048, 1000, TimeSpan.FromMilliseconds(650))
        {
            Telemetry = full ? TelemetryMode.Full : TelemetryMode.Minimal,
            Progress = quiet ? ProgressMode.Quiet : ProgressMode.Normal
        };
        int samples = 0;
        using var output = new StringWriter();
        var original = Console.Out;
        LoadResult result;
        try
        {
            Console.SetOut(output);
            result = await new LoadRunner().RunAsync(options, CancellationToken.None,
                telemetryInterval: TimeSpan.FromMilliseconds(10), sampleObserved: _ => samples++)
                .WaitAsync(TimeSpan.FromSeconds(15));
        }
        finally { Console.SetOut(original); }
        Assert.AreEqual(!quiet, output.ToString().Contains("progress offered=", StringComparison.Ordinal));
        Assert.AreEqual(full, samples > 0);
        Assert.AreEqual(0, FunctionalCriteria.ExitCode(result));
        Assert.IsEmpty(FunctionalCriteria.Evaluate(result));
        Assert.IsGreaterThan(0L, result.Consumed);
        Assert.AreEqual(result.Produced, result.Consumed);
        Assert.AreEqual(result.Consumed, result.Released);
        Assert.AreEqual(result.Counters.Rented, result.Counters.Returned);
        Assert.AreEqual(0, result.OutstandingBuffers);
        Assert.IsGreaterThan(0L, result.VisualPublished);
        // Stop can drain and release frames after closing the visual branch.
        // Exact visual work is asserted separately with the controlled mailbox clock.
        Assert.IsGreaterThan(0L, result.VisualReceived);
        Assert.IsLessThanOrEqualTo(result.Consumed, result.VisualReceived);
        Assert.IsNotNull(result.ManagedBytesAtActiveStart);
        Assert.IsNotNull(result.ManagedBytesAtActiveEnd);
        string path = Path.Combine(Path.GetTempPath(), $"ut-telemetry-{Guid.NewGuid():N}.json");
        try
        {
            await ResultWriter.WriteAsync(path, options, result, [], 0);
            using var json = JsonDocument.Parse(await File.ReadAllTextAsync(path));
            Assert.AreEqual(2, json.RootElement.GetProperty("schemaVersion").GetInt32());
            Assert.AreEqual(full ? "enabled" : "disabled", json.RootElement.GetProperty("options").GetProperty("detailedPerFrameInstrumentation").GetString());
            var values = json.RootElement.GetProperty("result");
            foreach (string field in new[] { "VisualDeliveryLatencyMicroseconds", "ProjectionMicroseconds", "CorrelationMisses", "CorrelationOverwrites", "MaximumWorkingSetBytes", "MaximumCpuPercent", "resourceSamples", "TotalResourceSamples" })
            { Assert.AreEqual(!full, values.GetProperty(field).ValueKind == JsonValueKind.Null, field); }
            Assert.AreEqual(!full, values.GetProperty("Demand").GetProperty("MeanDelayMicroseconds").ValueKind == JsonValueKind.Null);
            if (full)
            {
                Assert.IsGreaterThan(0L, values.GetProperty("ProjectionMicroseconds").GetProperty("Population").GetInt64());
                Assert.IsGreaterThan(0L, values.GetProperty("VisualDeliveryLatencyMicroseconds").GetProperty("Population").GetInt64());
                Assert.IsGreaterThan(0, values.GetProperty("resourceSamples").GetArrayLength());
            }
        }
        finally { File.Delete(path); }
    }
}
