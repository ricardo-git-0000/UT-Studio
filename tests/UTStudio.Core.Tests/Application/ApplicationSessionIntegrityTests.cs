using System.Collections.Concurrent;
using UTStudio.Contracts.Application;
using UTStudio.Contracts.Inspection;
using UTStudio.Domain.Acquisition;
using UTStudio.Domain.Inspection;

namespace UTStudio.Core.Tests.Application;

[TestClass]
public sealed class ApplicationSessionIntegrityTests
{
    private static readonly ConventionalAcquisitionConfiguration Configuration =
        new(new PhysicalChannelId(0), 8, 50_000_000);

    [TestMethod]
    public async Task InvalidInitialIndexCanBeFollowedByZeroAndOnlyCommitIsNotified()
    {
        await using var source = new ManualSessionSource();
        var sink = new RecordingCommittedSink();
        await using var session = TestInspectionAuthority.CreateSession(source, sink);
        await session.StartAsync(Configuration, new(Guid.NewGuid())).WaitAsync(TimeSpan.FromSeconds(10));
        var invalid = source.QueueFrame(0, acquisitionIndex: 9, elapsed: TimeSpan.Zero);
        var valid = source.QueueFrame(1, acquisitionIndex: 0, elapsed: TimeSpan.Zero);
        await ReturnAll(invalid, valid);
        await session.StopAsync().WaitAsync(TimeSpan.FromSeconds(10));

        CollectionAssert.AreEqual(new ulong[] { 0 }, sink.Indices.ToArray());
        Assert.AreEqual(1UL, session.Snapshot.Integrity.InvalidAcquisitionIndexFramesRejected);
        Assert.AreEqual(0UL, session.Snapshot.Integrity.AcquisitionMissingFrames);
        Assert.IsTrue(sink.Revisions.All(revision => revision != InspectionRevision.Initial));
    }

    [TestMethod]
    public async Task NewRunResetsWatermarksAndIntegrityCounters()
    {
        await using var source = new ManualSessionSource();
        await using var session = TestInspectionAuthority.CreateSession(source, new RecordingCommittedSink());
        await session.StartAsync(Configuration, new(Guid.NewGuid())).WaitAsync(TimeSpan.FromSeconds(10));
        var first = source.QueueFrame(0, acquisitionIndex: 0, elapsed: TimeSpan.Zero);
        var gap = source.QueueFrame(2, acquisitionIndex: 1, elapsed: TimeSpan.FromTicks(1));
        await ReturnAll(first, gap);
        await session.StopAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.AreEqual(1UL, session.Snapshot.Integrity.AcquisitionMissingFrames);

        AcquisitionRunId secondRun = new(Guid.NewGuid());
        await session.StartAsync(Configuration, secondRun).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.AreEqual(secondRun, session.Snapshot.RunId);
        Assert.AreEqual(SessionIntegrityStatistics.Empty, session.Snapshot.Integrity);
        var restarted = source.QueueFrame(0, acquisitionIndex: 0, elapsed: TimeSpan.Zero);
        await ReturnAll(restarted);
        await session.StopAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.AreEqual(SessionIntegrityStatistics.Empty, session.Snapshot.Integrity);
    }

    [TestMethod]
    public async Task SequenceGapsPreserveAcquisitionIndicesWithoutSyntheticNotifications()
    {
        await using var source = new ManualSessionSource();
        var sink = new RecordingCommittedSink();
        await using var session = TestInspectionAuthority.CreateSession(source, sink);
        await session.StartAsync(Configuration, new(Guid.NewGuid())).WaitAsync(TimeSpan.FromSeconds(10));
        var owners = new[]
        {
            source.QueueFrame(0, acquisitionIndex: 0, elapsed: TimeSpan.Zero),
            source.QueueFrame(2, acquisitionIndex: 1, elapsed: TimeSpan.FromTicks(1)),
            source.QueueFrame(5, acquisitionIndex: 2, elapsed: TimeSpan.FromTicks(2))
        };
        await ReturnAll(owners);
        await session.StopAsync().WaitAsync(TimeSpan.FromSeconds(10));

        CollectionAssert.AreEqual(new ulong[] { 0, 1, 2 }, sink.Indices.ToArray());
        Assert.AreEqual(3UL, session.Snapshot.Integrity.AcquisitionMissingFrames);
        Assert.IsTrue(owners.All(owner => owner.DisposeCount == 1));
    }

    [TestMethod]
    public async Task RejectionsUseIndependentCountersAndNextValidFrameIsCommitted()
    {
        await using var source = new ManualSessionSource();
        var sink = new RecordingCommittedSink();
        await using var session = TestInspectionAuthority.CreateSession(source, sink);
        await session.StartAsync(Configuration, new(Guid.NewGuid())).WaitAsync(TimeSpan.FromSeconds(10));
        var owners = new List<ManualSessionSource.CountingOwner>();
        async Task Add(ulong sequence, ulong index, long ticks)
        {
            ManualSessionSource.CountingOwner owner = source.QueueFrame(sequence,
                acquisitionIndex: index, elapsed: TimeSpan.FromTicks(ticks));
            owners.Add(owner);
            await owner.Returned.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }
        await Add(0, 0, 0);
        await Add(1, 1, 1);
        await Add(1, 50, 50);
        await Add(2, 2, 2);
        await Add(3, 3, 1);
        await Add(4, 3, 4);
        await Add(5, 3, 5);
        await Add(6, 4, 6);
        await session.StopAsync().WaitAsync(TimeSpan.FromSeconds(10));

        CollectionAssert.AreEqual(new ulong[] { 0, 1, 2, 3, 4 }, sink.Indices.ToArray());
        Assert.AreEqual(1UL, session.Snapshot.Integrity.DuplicateFramesRejected);
        Assert.AreEqual(1UL, session.Snapshot.Integrity.TimestampRegressionsRejected);
        Assert.AreEqual(1UL, session.Snapshot.Integrity.InvalidAcquisitionIndexFramesRejected);
        Assert.IsTrue(owners.All(owner => owner.DisposeCount == 1));
    }

    private static async Task ReturnAll(params ManualSessionSource.CountingOwner[] owners)
    {
        foreach (ManualSessionSource.CountingOwner owner in owners)
        { await owner.Returned.Task.WaitAsync(TimeSpan.FromSeconds(10)); }
    }

    private sealed class RecordingCommittedSink : ICommittedTraceSink
    {
        internal ConcurrentQueue<ulong> Indices { get; } = [];
        internal ConcurrentQueue<InspectionRevision> Revisions { get; } = [];
        public void OpenRun(AcquisitionRunId runId) { }
        public void NotifyCommitted(TraceIdentity identity, InspectionRevision revision)
        { Indices.Enqueue(identity.AcquisitionIndex); Revisions.Enqueue(revision); }
        public void CloseRun(AcquisitionRunId runId) { }
        public ValueTask WaitForCloseAsync(AcquisitionRunId runId) => ValueTask.CompletedTask;
    }
}
