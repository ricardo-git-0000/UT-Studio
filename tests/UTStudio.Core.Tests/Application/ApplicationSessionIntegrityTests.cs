using System.Collections.Concurrent;
using UTStudio.Application;
using UTStudio.Contracts.Application;
using UTStudio.Contracts.Presentation;
using UTStudio.Domain.Acquisition;

namespace UTStudio.Core.Tests.Application;

[TestClass]
public sealed class ApplicationSessionIntegrityTests
{
    private static readonly ConventionalAcquisitionConfiguration Configuration =
        new(new PhysicalChannelId(0), 8, 50_000_000);

    [TestMethod]
    public async Task InvalidInitialAcquisitionIndexCanBeFollowedByZero()
    {
        await using var source = new ManualSessionSource();
        var sink = new RecordingSink();
        await using var session = new ApplicationSession(source, sink);
        await session.StartAsync(Configuration, new(Guid.NewGuid())).WaitAsync(TimeSpan.FromSeconds(10));
        var invalid = source.QueueFrame(0, acquisitionIndex: 9, elapsed: TimeSpan.Zero);
        await invalid.Returned.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var valid = source.QueueFrame(1, acquisitionIndex: 0, elapsed: TimeSpan.Zero);
        await valid.Returned.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await session.StopAsync().WaitAsync(TimeSpan.FromSeconds(10));

        CollectionAssert.AreEqual(new ulong[] { 1 }, sink.Sequences.ToArray());
        Assert.AreEqual(1UL, session.Snapshot.Integrity.InvalidAcquisitionIndexFramesRejected);
        Assert.AreEqual(0UL, session.Snapshot.Integrity.AcquisitionMissingFrames);
        Assert.AreEqual(1, invalid.DisposeCount);
        Assert.AreEqual(1, valid.DisposeCount);
    }

    [TestMethod]
    public async Task NewRunResetsWatermarksAndIntegrityCountersOnSameSession()
    {
        await using var source = new ManualSessionSource();
        var sink = new RecordingSink();
        await using var session = new ApplicationSession(source, sink);
        await session.StartAsync(Configuration, new(Guid.NewGuid())).WaitAsync(TimeSpan.FromSeconds(10));
        var first = source.QueueFrame(0, acquisitionIndex: 0, elapsed: TimeSpan.Zero);
        await first.Returned.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var gap = source.QueueFrame(2, acquisitionIndex: 1, elapsed: TimeSpan.FromTicks(1));
        await gap.Returned.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await session.StopAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.AreEqual(1UL, session.Snapshot.Integrity.AcquisitionMissingFrames);

        var secondRun = new AcquisitionRunId(Guid.NewGuid());
        await session.StartAsync(Configuration, secondRun).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.AreEqual(secondRun, session.Snapshot.RunId);
        Assert.AreEqual(SessionIntegrityStatistics.Empty, session.Snapshot.Integrity);
        var restarted = source.QueueFrame(0, acquisitionIndex: 0, elapsed: TimeSpan.Zero);
        await restarted.Returned.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await session.StopAsync().WaitAsync(TimeSpan.FromSeconds(10));

        Assert.AreEqual(SessionIntegrityStatistics.Empty, session.Snapshot.Integrity);
        Assert.AreEqual(1, first.DisposeCount);
        Assert.AreEqual(1, gap.DisposeCount);
        Assert.AreEqual(1, restarted.DisposeCount);
    }

    [TestMethod]
    public async Task SessionForwardsExactPerFrameIndexCoordinateAndBorrowedSamples()
    {
        await using var source = new ManualSessionSource();
        var sink = new RecordingSink();
        await using var session = new ApplicationSession(source, sink);
        await session.StartAsync(Configuration, new(Guid.NewGuid())).WaitAsync(TimeSpan.FromSeconds(10));
        var coordinate = new ScanSpatialCoordinate(12.5, ScanSpatialUnit.Millimetres, new ScanAxisId("scan-x"));

        var owner = source.QueueFrame(0, acquisitionIndex: 0, elapsed: TimeSpan.FromTicks(17),
            spatialCoordinate: coordinate);
        await owner.Returned.Task.WaitAsync(TimeSpan.FromSeconds(10));
        owner.Memory.Span[0] = 123;
        await session.StopAsync().WaitAsync(TimeSpan.FromSeconds(10));

        Assert.AreEqual(0UL, sink.AcquisitionIndices.Single());
        Assert.AreEqual(TimeSpan.FromTicks(17), sink.Elapsed.Single());
        Assert.AreEqual(coordinate, sink.Coordinates.Single());
        Assert.AreEqual(0, sink.FirstSamples.Single()); // Source samples were zero when synchronously borrowed.
        Assert.AreEqual(1, owner.DisposeCount);
    }

    [TestMethod]
    public async Task ContiguousGapsAndLimitsAreDeliveredWithExactMissingCount()
    {
        await using var source = new ManualSessionSource();
        var sink = new RecordingSink();
        await using var session = new ApplicationSession(source, sink);
        await session.StartAsync(Configuration, new(Guid.NewGuid())).WaitAsync(TimeSpan.FromSeconds(10));

        var owners = new[]
        {
            source.QueueFrame(0, acquisitionIndex: 0, elapsed: TimeSpan.Zero),
            source.QueueFrame(2, acquisitionIndex: 1, elapsed: TimeSpan.FromTicks(1)),
            source.QueueFrame(5, acquisitionIndex: 2, elapsed: TimeSpan.FromTicks(2)),
            source.QueueFrame(ulong.MaxValue, acquisitionIndex: 3, elapsed: TimeSpan.FromTicks(3))
        };
        await ReturnAll(owners);
        await session.StopAsync().WaitAsync(TimeSpan.FromSeconds(10));

        CollectionAssert.AreEqual(new ulong[] { 0, 2, 5, ulong.MaxValue }, sink.Sequences.ToArray());
        Assert.AreEqual(ulong.MaxValue - 3, session.Snapshot.Integrity.AcquisitionMissingFrames);
        Assert.AreEqual(ulong.MaxValue, session.Snapshot.LastSequence);
        Assert.IsTrue(owners.All(owner => owner.DisposeCount == 1));
    }

    [TestMethod]
    public async Task RejectionsUseIndependentCountersAndNextValidFrameIsDelivered()
    {
        await using var source = new ManualSessionSource();
        var sink = new RecordingSink();
        await using var session = new ApplicationSession(source, sink);
        await session.StartAsync(Configuration, new(Guid.NewGuid())).WaitAsync(TimeSpan.FromSeconds(10));

        var owners = new List<ManualSessionSource.CountingOwner>();
        async Task Add(ulong sequence, ulong acquisitionIndex, long elapsedTicks)
        {
            var owner = source.QueueFrame(sequence, acquisitionIndex: acquisitionIndex,
                elapsed: TimeSpan.FromTicks(elapsedTicks));
            owners.Add(owner);
            await owner.Returned.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }
        await Add(0, 0, 0);
        await Add(1, 1, 1);
        await Add(1, 50, 50); // duplicate sequence
        await Add(2, 2, 2);
        await Add(0, 50, 50); // out of order
        await Add(3, 3, 3);
        await Add(4, 4, 2); // timestamp regression
        await Add(5, 5, 3); // equality is valid
        await Add(6, 5, 4); // repeated index
        await Add(7, 6, 5);
        await Add(8, 5, 6); // descending index
        await Add(9, 7, 7);
        await session.StopAsync().WaitAsync(TimeSpan.FromSeconds(10));

        CollectionAssert.AreEqual(new ulong[] { 0, 1, 2, 3, 5, 7, 9 }, sink.Sequences.ToArray());
        var integrity = session.Snapshot.Integrity;
        Assert.AreEqual(0UL, integrity.AcquisitionMissingFrames);
        Assert.AreEqual(1UL, integrity.DuplicateFramesRejected);
        Assert.AreEqual(1UL, integrity.OutOfOrderFramesRejected);
        Assert.AreEqual(1UL, integrity.TimestampRegressionsRejected);
        Assert.AreEqual(2UL, integrity.InvalidAcquisitionIndexFramesRejected);
        Assert.IsTrue(owners.All(owner => owner.DisposeCount == 1));
    }

    [TestMethod]
    public async Task InvalidFirstSequenceCanBeFollowedByZeroAndGapIsNotCountedTwiceByDuplicate()
    {
        await using var source = new ManualSessionSource();
        var sink = new RecordingSink();
        await using var session = new ApplicationSession(source, sink);
        await session.StartAsync(Configuration, new(Guid.NewGuid())).WaitAsync(TimeSpan.FromSeconds(10));

        var owners = new List<ManualSessionSource.CountingOwner>();
        async Task Add(ulong sequence, ulong acquisitionIndex, long elapsedTicks)
        {
            var owner = source.QueueFrame(sequence, acquisitionIndex: acquisitionIndex,
                elapsed: TimeSpan.FromTicks(elapsedTicks));
            owners.Add(owner);
            await owner.Returned.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }
        await Add(4, 4, 0);
        await Add(0, 0, 0);
        await Add(3, 1, 1);
        await Add(3, 99, 99);
        await Add(4, 2, 2);
        await session.StopAsync().WaitAsync(TimeSpan.FromSeconds(10));

        CollectionAssert.AreEqual(new ulong[] { 0, 3, 4 }, sink.Sequences.ToArray());
        Assert.AreEqual(2UL, session.Snapshot.Integrity.AcquisitionMissingFrames);
        Assert.AreEqual(1UL, session.Snapshot.Integrity.DuplicateFramesRejected);
        Assert.AreEqual(1UL, session.Snapshot.Integrity.OutOfOrderFramesRejected);
        Assert.IsTrue(owners.All(owner => owner.DisposeCount == 1));
    }

    [TestMethod]
    public async Task MaximumSequenceRejectsDuplicateAndAnyFollowingLowerValueWithoutOverflow()
    {
        await using var source = new ManualSessionSource();
        var sink = new RecordingSink();
        await using var session = new ApplicationSession(source, sink);
        await session.StartAsync(Configuration, new(Guid.NewGuid())).WaitAsync(TimeSpan.FromSeconds(10));

        var owners = new[]
        {
            source.QueueFrame(0, acquisitionIndex: 0, elapsed: TimeSpan.Zero),
            source.QueueFrame(ulong.MaxValue, acquisitionIndex: 1, elapsed: TimeSpan.FromTicks(1)),
            source.QueueFrame(ulong.MaxValue, acquisitionIndex: 2, elapsed: TimeSpan.FromTicks(2)),
            source.QueueFrame(ulong.MaxValue - 1, acquisitionIndex: 3, elapsed: TimeSpan.FromTicks(3))
        };
        await ReturnAll(owners);
        await session.StopAsync().WaitAsync(TimeSpan.FromSeconds(10));

        Assert.AreEqual(ulong.MaxValue - 1, session.Snapshot.Integrity.AcquisitionMissingFrames);
        Assert.AreEqual(1UL, session.Snapshot.Integrity.DuplicateFramesRejected);
        Assert.AreEqual(1UL, session.Snapshot.Integrity.OutOfOrderFramesRejected);
        Assert.IsTrue(owners.All(owner => owner.DisposeCount == 1));
    }

    private static async Task ReturnAll(IEnumerable<ManualSessionSource.CountingOwner> owners)
    {
        foreach (var owner in owners) { await owner.Returned.Task.WaitAsync(TimeSpan.FromSeconds(10)); }
    }

    private sealed class RecordingSink : IConventionalFrameSink
    {
        internal ConcurrentQueue<ulong> Sequences { get; } = new();
        internal ConcurrentQueue<ulong> AcquisitionIndices { get; } = new();
        internal ConcurrentQueue<TimeSpan> Elapsed { get; } = new();
        internal ConcurrentQueue<ScanSpatialCoordinate?> Coordinates { get; } = new();
        internal ConcurrentQueue<short> FirstSamples { get; } = new();
        public void OpenRun(ConventionalUtFrameMetadata metadata) { }
        public void Accept(ConventionalUtFrameMetadata metadata, ulong sequence, ulong acquisitionIndex,
            TimeSpan elapsedSinceRunStart, ScanSpatialCoordinate? spatialCoordinate, ReadOnlySpan<short> samples)
        {
            Sequences.Enqueue(sequence);
            AcquisitionIndices.Enqueue(acquisitionIndex);
            Elapsed.Enqueue(elapsedSinceRunStart);
            Coordinates.Enqueue(spatialCoordinate);
            FirstSamples.Enqueue(samples[0]);
        }
        public void CloseRun(AcquisitionRunId runId) { }
    }
}
