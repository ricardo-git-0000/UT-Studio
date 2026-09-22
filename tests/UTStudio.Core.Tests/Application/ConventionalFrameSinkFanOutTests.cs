using System.Collections.Concurrent;
using UTStudio.Application;
using UTStudio.Contracts.Presentation;
using UTStudio.Domain.Acquisition;

namespace UTStudio.Core.Tests.Application;

[TestClass]
public sealed class ConventionalFrameSinkFanOutTests
{
    private static readonly AcquisitionRunId RunId = new(Guid.Parse("10000000-0000-0000-0000-000000000001"));
    private static readonly ConventionalUtFrameMetadata Metadata = new(new UtSourceId("fanout"), RunId,
        new ConventionalAcquisitionConfiguration(default, 3, 50_000_000), DateTimeOffset.UnixEpoch);

    [TestMethod]
    [DataRow("A")]
    [DataRow("B")]
    [DataRow("Both")]
    public async Task ApplicationSessionWithFailingFanOutReleasesFramesAndKeepsAcquisitionRunning(string failing)
    {
        await using var source = new ManualSessionSource();
        var calls = new ConcurrentQueue<string>();
        var a = new ManualSink("A", calls) { FailAccept = failing is "A" or "Both" };
        var b = new ManualSink("B", calls) { FailAccept = failing is "B" or "Both" };
        var fanOut = new ConventionalFrameSinkFanOut(a, b);
        await using var session = new ApplicationSession(source, fanOut);
        await session.StartAsync(Metadata.Configuration, RunId).WaitAsync(TimeSpan.FromSeconds(10));

        var first = source.QueueFrame(0);
        await first.Returned.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var second = source.QueueFrame(1);
        await second.Returned.Task.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.AreEqual(UTStudio.Contracts.Application.SessionPhase.Running, session.Snapshot.Phase);
        Assert.IsNull(session.Snapshot.PrimaryError);
        Assert.IsNull(session.Snapshot.VisualError);
        Assert.AreEqual(failing is "A" or "Both" ? 1 : 2, a.AcceptCount);
        Assert.AreEqual(failing is "B" or "Both" ? 1 : 2, b.AcceptCount);
        await session.StopAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.AreEqual(1, first.DisposeCount);
        Assert.AreEqual(1, second.DisposeCount);
        Assert.AreEqual(2L, session.Snapshot.ReleasedFrames);
        Assert.AreEqual(failing is "Both" ? 2 : 1, fanOut.Diagnostics.Count(item => item.FailureCount != 0));
    }

    [TestMethod]
    public void InvokesAThenBOnceWithExactPerFrameDataAndNoCopyingLayer()
    {
        var calls = new ConcurrentQueue<string>();
        var a = new ManualSink("A", calls);
        var b = new ManualSink("B", calls);
        var fanOut = new ConventionalFrameSinkFanOut(a, b);
        var coordinate = new ScanSpatialCoordinate(3.5, ScanSpatialUnit.Millimetres, new ScanAxisId("scan-x"));
        short[] samples = [1, -2, 3];

        fanOut.OpenRun(Metadata);
        fanOut.Accept(Metadata, 7, 11, TimeSpan.FromTicks(13), coordinate, samples);
        fanOut.CloseRun(RunId);
        fanOut.CloseRun(RunId);

        CollectionAssert.AreEqual(new[] { "A.Open", "B.Open", "A.Accept", "B.Accept", "A.Close", "B.Close" },
            calls.ToArray());
        foreach (var sink in new[] { a, b })
        {
            Assert.AreEqual(7UL, sink.Sequence);
            Assert.AreEqual(11UL, sink.AcquisitionIndex);
            Assert.AreEqual(TimeSpan.FromTicks(13), sink.Elapsed);
            Assert.AreEqual(coordinate, sink.Coordinate);
            CollectionAssert.AreEqual(samples, sink.CopiedForAssertion);
            Assert.AreEqual(1, sink.CloseCount);
        }
        Assert.IsTrue(fanOut.Diagnostics.All(item => item.FailureCount == 0 && item.IsClosed));
    }

    [TestMethod]
    [DataRow("A")]
    [DataRow("B")]
    [DataRow("Both")]
    public void AcceptFailureDisablesAndClosesOnlyFailingBranches(string failing)
    {
        var calls = new ConcurrentQueue<string>();
        var a = new ManualSink("A", calls) { FailAccept = failing is "A" or "Both" };
        var b = new ManualSink("B", calls) { FailAccept = failing is "B" or "Both" };
        var fanOut = new ConventionalFrameSinkFanOut(a, b);
        fanOut.OpenRun(Metadata);

        fanOut.Accept(Metadata, 0, 0, TimeSpan.Zero, null, new short[3]);
        fanOut.Accept(Metadata, 1, 1, TimeSpan.FromTicks(1), null, new short[3]);
        fanOut.CloseRun(RunId);

        Assert.AreEqual(failing is "A" or "Both" ? 1 : 2, a.AcceptCount);
        Assert.AreEqual(failing is "B" or "Both" ? 1 : 2, b.AcceptCount);
        Assert.AreEqual(1, a.CloseCount);
        Assert.AreEqual(1, b.CloseCount);
        var diagnostics = fanOut.Diagnostics;
        Assert.AreEqual(failing is "A" or "Both" ? 1UL : 0UL, diagnostics[0].FailureCount);
        Assert.AreEqual(failing is "B" or "Both" ? 1UL : 0UL, diagnostics[1].FailureCount);
    }

    [TestMethod]
    public void OpenAndCloseFailuresAreIsolatedAndMultipleBranchErrorsAreRetained()
    {
        var calls = new ConcurrentQueue<string>();
        var a = new ManualSink("A", calls) { FailOpen = true, FailClose = true };
        var b = new ManualSink("B", calls) { FailClose = true };
        var fanOut = new ConventionalFrameSinkFanOut(a, b);

        fanOut.OpenRun(Metadata);
        fanOut.Accept(Metadata, 0, 0, TimeSpan.Zero, null, new short[3]);
        fanOut.CloseRun(RunId);
        fanOut.CloseRun(RunId);

        Assert.AreEqual(0, a.AcceptCount);
        Assert.AreEqual(1, b.AcceptCount);
        Assert.AreEqual(1, a.CloseCount);
        Assert.AreEqual(1, b.CloseCount);
        var diagnostics = fanOut.Diagnostics;
        Assert.AreEqual(2UL, diagnostics[0].FailureCount);
        Assert.AreEqual(VisualBranchOperation.Open, diagnostics[0].FirstFailure!.Operation);
        Assert.AreEqual(VisualBranchOperation.Close, diagnostics[0].LatestFailure!.Operation);
        Assert.AreEqual(1UL, diagnostics[1].FailureCount);
        Assert.AreEqual("BScan", diagnostics[1].LatestFailure!.BranchId);
    }

    [TestMethod]
    public void ClosedFanOutCanOpenAnotherRunAndResetsBranchState()
    {
        var calls = new ConcurrentQueue<string>();
        var a = new ManualSink("A", calls);
        var b = new ManualSink("B", calls);
        var fanOut = new ConventionalFrameSinkFanOut(a, b);
        fanOut.OpenRun(Metadata);
        fanOut.CloseRun(RunId);
        var second = new AcquisitionRunId(Guid.Parse("20000000-0000-0000-0000-000000000002"));

        var secondMetadata = new ConventionalUtFrameMetadata(new UtSourceId("fanout"), second,
            Metadata.Configuration, DateTimeOffset.UnixEpoch);
        fanOut.OpenRun(secondMetadata);
        fanOut.Accept(secondMetadata, 0, 0, TimeSpan.Zero, null, new short[3]);
        fanOut.CloseRun(second);

        Assert.AreEqual(1, a.AcceptCount);
        Assert.AreEqual(1, b.AcceptCount);
        Assert.AreEqual(2, a.CloseCount);
        Assert.AreEqual(2, b.CloseCount);
        Assert.IsTrue(fanOut.Diagnostics.All(item => item.IsClosed && item.FailureCount == 0));
    }

    [TestMethod]
    public void CloseMayOverlapAdmittedAcceptAndClosesEachBranchExactlyOnce()
    {
        using var acceptEntered = new ManualResetEventSlim();
        using var allowAcceptToReturn = new ManualResetEventSlim();
        var calls = new ConcurrentQueue<string>();
        var a = new BlockingSink("A", calls, acceptEntered, allowAcceptToReturn);
        var b = new ManualSink("B", calls);
        var fanOut = new ConventionalFrameSinkFanOut(a, b);
        fanOut.OpenRun(Metadata);
        Exception? acceptError = null;
        var acceptThread = new Thread(() =>
        {
            try { fanOut.Accept(Metadata, 0, 0, TimeSpan.Zero, null, new short[3]); }
            catch (Exception error) { acceptError = error; }
        });

        acceptThread.Start();
        Assert.IsTrue(acceptEntered.Wait(TimeSpan.FromSeconds(10)), "Accept did not enter the child sink.");
        fanOut.CloseRun(RunId);
        allowAcceptToReturn.Set();
        Assert.IsTrue(acceptThread.Join(TimeSpan.FromSeconds(10)), "Accept did not finish.");

        Assert.IsNull(acceptError);
        Assert.AreEqual(1, a.AcceptCount);
        Assert.AreEqual(1, a.CloseCount);
        Assert.AreEqual(0, b.AcceptCount);
        Assert.AreEqual(1, b.CloseCount);
        Assert.IsTrue(fanOut.Diagnostics.All(item => item.IsClosed && item.FailureCount == 0));
    }

    private sealed class ManualSink(string id, ConcurrentQueue<string> calls) : IConventionalFrameSink
    {
        internal bool FailOpen { get; init; }
        internal bool FailAccept { get; init; }
        internal bool FailClose { get; init; }
        internal int AcceptCount { get; private set; }
        internal int CloseCount { get; private set; }
        internal ulong Sequence { get; private set; }
        internal ulong AcquisitionIndex { get; private set; }
        internal TimeSpan Elapsed { get; private set; }
        internal ScanSpatialCoordinate? Coordinate { get; private set; }
        internal short[] CopiedForAssertion { get; private set; } = [];

        public void OpenRun(ConventionalUtFrameMetadata metadata)
        {
            calls.Enqueue($"{id}.Open");
            if (FailOpen) { throw new InvalidOperationException($"{id} open"); }
        }

        public void Accept(ConventionalUtFrameMetadata metadata, ulong sequence, ulong acquisitionIndex,
            TimeSpan elapsedSinceRunStart, ScanSpatialCoordinate? spatialCoordinate, ReadOnlySpan<short> samples)
        {
            calls.Enqueue($"{id}.Accept");
            AcceptCount++;
            Sequence = sequence;
            AcquisitionIndex = acquisitionIndex;
            Elapsed = elapsedSinceRunStart;
            Coordinate = spatialCoordinate;
            CopiedForAssertion = samples.ToArray(); // Test evidence only; the fan-out itself performs no copy.
            if (FailAccept) { throw new InvalidOperationException($"{id} accept"); }
        }

        public void CloseRun(AcquisitionRunId runId)
        {
            calls.Enqueue($"{id}.Close");
            CloseCount++;
            if (FailClose) { throw new InvalidOperationException($"{id} close"); }
        }
    }

    private sealed class BlockingSink(
        string id,
        ConcurrentQueue<string> calls,
        ManualResetEventSlim acceptEntered,
        ManualResetEventSlim allowAcceptToReturn) : IConventionalFrameSink
    {
        internal int AcceptCount;
        internal int CloseCount;

        public void OpenRun(ConventionalUtFrameMetadata metadata) => calls.Enqueue($"{id}.Open");

        public void Accept(ConventionalUtFrameMetadata metadata, ulong sequence, ulong acquisitionIndex,
            TimeSpan elapsedSinceRunStart, ScanSpatialCoordinate? spatialCoordinate, ReadOnlySpan<short> samples)
        {
            Interlocked.Increment(ref AcceptCount);
            calls.Enqueue($"{id}.Accept");
            acceptEntered.Set();
            allowAcceptToReturn.Wait();
        }

        public void CloseRun(AcquisitionRunId runId)
        {
            Interlocked.Increment(ref CloseCount);
            calls.Enqueue($"{id}.Close");
        }
    }
}
