using UTStudio.Application;
using UTStudio.Contracts.Presentation;
using UTStudio.Domain.Acquisition;
using UTStudio.Visualization.Core;

namespace UTStudio.Core.Tests.Application;

[TestClass]
public sealed class BScanSessionLifecycleTests
{
    [TestMethod]
    public async Task StopWaitsForBScanCloseBarrierBeforeAllowingNextRun()
    {
        using var workerEntered = new ManualResetEventSlim();
        using var releaseWorker = new ManualResetEventSlim();
        var bscan = new BScanIngress(new BScanOptions(traceMemoryBudgetBytes: 4096,
            maximumColumns: 2, minimumColumns: 1))
        {
            BeforeIndexBatch = () => { workerEntered.Set(); releaseWorker.Wait(); }
        };
        var ascan = new PassiveSink();
        var fanOut = new ConventionalFrameSinkFanOut(ascan, bscan);
        await using var source = new ManualSessionSource();
        await using var session = TestInspectionAuthority.CreateSession(source, fanOut);
        var configuration = new ConventionalAcquisitionConfiguration(default, 3, 50_000_000);
        var firstRun = new AcquisitionRunId(Guid.NewGuid());
        await session.StartAsync(configuration, firstRun).WaitAsync(TimeSpan.FromSeconds(10));
        var frame = source.QueueFrame(0);
        await frame.Returned.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.IsTrue(workerEntered.Wait(TimeSpan.FromSeconds(10)));

        Task stop = session.StopAsync();
        Assert.IsFalse(stop.IsCompleted);
        releaseWorker.Set();
        await stop.WaitAsync(TimeSpan.FromSeconds(10));

        var secondRun = new AcquisitionRunId(Guid.NewGuid());
        await session.StartAsync(configuration, secondRun).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.AreEqual(secondRun, session.Snapshot.RunId);
        await session.StopAsync().WaitAsync(TimeSpan.FromSeconds(10));
        await bscan.DisposeAsync();
    }

    private sealed class PassiveSink : IConventionalFrameSink
    {
        public void OpenRun(ConventionalUtFrameMetadata metadata) { }
        public void Accept(ConventionalUtFrameMetadata metadata, ulong sequence, ulong acquisitionIndex,
            TimeSpan elapsedSinceRunStart, ScanSpatialCoordinate? spatialCoordinate, ReadOnlySpan<short> samples)
        { }
        public void CloseRun(AcquisitionRunId runId) { }
    }
}
