using UTStudio.Application;
using UTStudio.Contracts.Inspection;
using UTStudio.Contracts.Presentation;
using UTStudio.Domain.Acquisition;
using UTStudio.Domain.Inspection;
using UTStudio.Inspection.Core;

namespace UTStudio.Core.Tests.Application;

[TestClass]
public sealed class ApplicationSessionInspectionStoreTests
{
    private static readonly ConventionalAcquisitionConfiguration Configuration =
        new(new PhysicalChannelId(0), 8, 50_000_000, firstSampleOffsetSeconds: 0.000_001);

    [TestMethod]
    public async Task CommittedFrameIsAuthoritativeBeforeVisualProjectionAndSurvivesOwnerReturn()
    {
        await using var source = new ManualSessionSource();
        var inspectionId = new InspectionId(Guid.NewGuid());
        await using var stores = new InspectionDataStoreRegistry(inspectionId,
            new InMemoryInspectionStoreOptions(1024, 10));
        var runId = new AcquisitionRunId(Guid.NewGuid());
        IInspectionDataStore store = stores.Store;
        var sink = new StoreCheckingSink(store, inspectionId, runId, source.SourceId);
        await using var session = new ApplicationSession(source, inspectionId, run => stores.OpenRun(run), sink);

        await session.StartAsync(Configuration, runId).WaitAsync(TimeSpan.FromSeconds(10));
        var (frame, owner) = source.CreateFrame(0, acquisitionIndex: 0, elapsed: TimeSpan.FromTicks(17));
        for (short index = 0; index < Configuration.SampleCount; index++) { owner.Memory.Span[index] = (short)(index + 1); }
        Assert.IsTrue(source.Channel.Writer.TryWrite(frame));
        await owner.Returned.Task.WaitAsync(TimeSpan.FromSeconds(10));
        owner.Memory.Span.Fill(99);
        await session.StopAsync().WaitAsync(TimeSpan.FromSeconds(10));

        Assert.AreEqual(1, sink.AcceptCount);
        Assert.AreEqual(1, owner.DisposeCount);
        var identity = new TraceIdentity(new UtStreamIdentity(inspectionId, runId, source.SourceId,
            Configuration.PhysicalChannelId), 0);
        InspectionTraceReadResult read = await store.ReadAsync(identity);
        using IInspectionTraceLease lease = read.Lease!;
        Assert.AreEqual(InspectionReadStatus.Available, read.Status);
        Assert.AreEqual((short)1, lease.Samples[0]);
        Assert.AreEqual(DateTimeOffset.UnixEpoch + TimeSpan.FromTicks(17), lease.Descriptor.TimestampUtc);
        Assert.AreEqual(Configuration.FirstSampleOffsetSeconds, lease.Descriptor.FirstSampleOffsetSeconds);
    }

    [TestMethod]
    public async Task CapacityRejectionIsObservableSkipsVisualAndReleasesFrameOnce()
    {
        await using var source = new ManualSessionSource();
        var inspectionId = new InspectionId(Guid.NewGuid());
        await using var stores = new InspectionDataStoreRegistry(inspectionId,
            new InMemoryInspectionStoreOptions(2, 10));
        var sink = new CountingSink();
        var session = new ApplicationSession(source, inspectionId,
            run => stores.OpenRun(run), sink);

        await session.StartAsync(Configuration, new(Guid.NewGuid())).WaitAsync(TimeSpan.FromSeconds(10));
        var owner = source.QueueFrame(0);
        await owner.Returned.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await ApplicationSessionTests.WaitForPhaseAsync(session, UTStudio.Contracts.Application.SessionPhase.Faulted);

        Assert.AreEqual(0, sink.AcceptCount);
        Assert.AreEqual(1, owner.DisposeCount);
        Assert.AreEqual("session.frame", session.Snapshot.PrimaryError?.Code);
        StringAssert.Contains(session.Snapshot.PrimaryError?.Message, "CapacityExceeded");
        await Assert.ThrowsExactlyAsync<InspectionAppendException>(
            () => session.DisposeAsync().AsTask());
    }

    [TestMethod]
    public async Task SequenceGapIsConfirmedWithLaterFrameBeforeVisualProjection()
    {
        await using var source = new ManualSessionSource();
        var inspectionId = new InspectionId(Guid.NewGuid());
        await using var stores = new InspectionDataStoreRegistry(inspectionId,
            new InMemoryInspectionStoreOptions(1024, 10));
        var sink = new CountingSink();
        await using var session = new ApplicationSession(source, inspectionId, stores.OpenRun, sink);
        var runId = new AcquisitionRunId(Guid.NewGuid());
        await session.StartAsync(Configuration, runId).WaitAsync(TimeSpan.FromSeconds(10));
        var first = source.QueueFrame(0);
        await first.Returned.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var later = source.QueueFrame(3);
        await later.Returned.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await session.StopAsync().WaitAsync(TimeSpan.FromSeconds(10));

        var stream = new UtStreamIdentity(inspectionId, runId, source.SourceId, Configuration.PhysicalChannelId);
        IReadOnlyList<GapRecord> gaps = await stores.Store.ReadGapsAsync(stream, 10);
        Assert.HasCount(1, gaps);
        Assert.AreEqual(1UL, gaps[0].FirstMissingSequence);
        Assert.AreEqual(2UL, gaps[0].LastMissingSequence);
        Assert.AreEqual(2, sink.AcceptCount);
        Assert.AreEqual(1, first.DisposeCount);
        Assert.AreEqual(1, later.DisposeCount);
    }

    [TestMethod]
    public async Task FailedFrameGapCommitPublishesNeitherTraceGapNorVisual()
    {
        await using var source = new ManualSessionSource();
        var inspectionId = new InspectionId(Guid.NewGuid());
        await using var stores = new InspectionDataStoreRegistry(inspectionId,
            new InMemoryInspectionStoreOptions(1024, 10));
        var sink = new CountingSink();
        var session = new ApplicationSession(source, inspectionId, stores.OpenRun, sink);
        var runId = new AcquisitionRunId(Guid.NewGuid());
        await session.StartAsync(Configuration, runId).WaitAsync(TimeSpan.FromSeconds(10));
        var first = source.QueueFrame(0);
        await first.Returned.Task.WaitAsync(TimeSpan.FromSeconds(10));
        ((InMemoryInspectionDataStore)stores.Store).TestHooks = new()
        { BeforeGapOnlyCommit = () => throw new InvalidOperationException("synthetic") };
        var later = source.QueueFrame(2);
        await later.Returned.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await ApplicationSessionTests.WaitForPhaseAsync(session, UTStudio.Contracts.Application.SessionPhase.Faulted);

        var stream = new UtStreamIdentity(inspectionId, runId, source.SourceId, Configuration.PhysicalChannelId);
        Assert.IsEmpty(await stores.Store.ReadGapsAsync(stream, 10));
        Assert.AreEqual(1UL, stores.Store.Revision.Value);
        Assert.AreEqual(0UL, session.Snapshot.Integrity.AcquisitionMissingFrames);
        Assert.AreEqual(1, sink.AcceptCount);
        Assert.AreEqual(1, later.DisposeCount);
        await Assert.ThrowsExactlyAsync<InspectionAppendException>(() => session.DisposeAsync().AsTask());
    }

    [TestMethod]
    public async Task RejectedConfirmingFrameCommitsGapBeforeLaterAcceptedTrace()
    {
        await using var source = new ManualSessionSource();
        var inspectionId = new InspectionId(Guid.NewGuid());
        await using var stores = new InspectionDataStoreRegistry(inspectionId,
            new InMemoryInspectionStoreOptions(1024, 10));
        var sink = new CountingSink();
        await using var session = new ApplicationSession(source, inspectionId, stores.OpenRun, sink);
        var runId = new AcquisitionRunId(Guid.NewGuid());
        await session.StartAsync(Configuration, runId).WaitAsync(TimeSpan.FromSeconds(10));
        var first = source.QueueFrame(0, acquisitionIndex: 0, elapsed: TimeSpan.Zero);
        await first.Returned.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var rejected = source.QueueFrame(3, acquisitionIndex: 0, elapsed: TimeSpan.FromTicks(1));
        await rejected.Returned.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var next = source.QueueFrame(4, acquisitionIndex: 1, elapsed: TimeSpan.FromTicks(2));
        await next.Returned.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await session.StopAsync().WaitAsync(TimeSpan.FromSeconds(10));

        var stream = new UtStreamIdentity(inspectionId, runId, source.SourceId, Configuration.PhysicalChannelId);
        IReadOnlyList<GapRecord> gaps = await stores.Store.ReadGapsAsync(stream, 10);
        Assert.HasCount(1, gaps);
        Assert.AreEqual(1UL, gaps[0].FirstMissingSequence);
        Assert.AreEqual(2UL, gaps[0].LastMissingSequence);
        Assert.AreEqual(3UL, gaps[0].SubsequentObservedSequence);
        Assert.AreEqual(2, stores.Store.Diagnostics.CommittedEntries);
        Assert.AreEqual(1, stores.Store.Diagnostics.ConfirmedGaps);
        Assert.AreEqual(3UL, stores.Store.Revision.Value);
        Assert.AreEqual(1UL, session.Snapshot.Integrity.InvalidAcquisitionIndexFramesRejected);
        Assert.AreEqual(2, sink.AcceptCount);
        Assert.AreEqual(1, first.DisposeCount);
        Assert.AreEqual(1, rejected.DisposeCount);
        Assert.AreEqual(1, next.DisposeCount);
    }

    [TestMethod]
    public async Task TimestampRejectedConfirmingFrameStillCommitsGapWithoutTraceOrVisual()
    {
        await using var source = new ManualSessionSource();
        var inspectionId = new InspectionId(Guid.NewGuid());
        await using var stores = new InspectionDataStoreRegistry(inspectionId,
            new InMemoryInspectionStoreOptions(1024, 10));
        var sink = new CountingSink();
        await using var session = new ApplicationSession(source, inspectionId, stores.OpenRun, sink);
        var runId = new AcquisitionRunId(Guid.NewGuid());
        await session.StartAsync(Configuration, runId).WaitAsync(TimeSpan.FromSeconds(10));
        var first = source.QueueFrame(0, acquisitionIndex: 0, elapsed: TimeSpan.FromTicks(5));
        await first.Returned.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var rejected = source.QueueFrame(3, acquisitionIndex: 1, elapsed: TimeSpan.FromTicks(4));
        await rejected.Returned.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await session.StopAsync().WaitAsync(TimeSpan.FromSeconds(10));

        var stream = new UtStreamIdentity(inspectionId, runId, source.SourceId, Configuration.PhysicalChannelId);
        IReadOnlyList<GapRecord> gaps = await stores.Store.ReadGapsAsync(stream, 10);
        Assert.HasCount(1, gaps);
        Assert.AreEqual(3UL, gaps[0].SubsequentObservedSequence);
        Assert.AreEqual(1, stores.Store.Diagnostics.CommittedEntries);
        Assert.AreEqual(1, sink.AcceptCount);
        Assert.AreEqual(1UL, session.Snapshot.Integrity.TimestampRegressionsRejected);
    }

    [TestMethod]
    public void EveryPublicSessionConstructorRequiresInspectionAuthority()
    {
        var constructors = typeof(ApplicationSession).GetConstructors();
        Assert.IsNotEmpty(constructors);
        Assert.IsTrue(constructors.All(constructor =>
            constructor.GetParameters().Any(parameter => parameter.ParameterType == typeof(InspectionId)) &&
            constructor.GetParameters().Any(parameter => parameter.ParameterType ==
                typeof(Func<AcquisitionRunId, IInspectionRunWriter>))));
    }

    [TestMethod]
    public async Task StopWaitsForAdmittedCommitThenSealsAndReleasesFrameOnce()
    {
        await using var source = new ManualSessionSource();
        var inspectionId = new InspectionId(Guid.NewGuid());
        await using var stores = new InspectionDataStoreRegistry(inspectionId,
            new InMemoryInspectionStoreOptions(1024, 10));
        await using var session = new ApplicationSession(source, inspectionId, stores.OpenRun);
        var runId = new AcquisitionRunId(Guid.NewGuid());
        await session.StartAsync(Configuration, runId).WaitAsync(TimeSpan.FromSeconds(10));
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        ((InMemoryInspectionDataStore)stores.Store).TestHooks = new()
        { BeforeCommit = () => { entered.Set(); release.Wait(); } };
        var owner = source.QueueFrame(0);
        Assert.IsTrue(entered.Wait(TimeSpan.FromSeconds(10)));
        Task stop = session.StopAsync();
        try { Assert.IsFalse(stop.IsCompleted); }
        finally { release.Set(); }
        await stop.WaitAsync(TimeSpan.FromSeconds(10));
        await owner.Returned.Task.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.AreEqual(1, owner.DisposeCount);
        Assert.AreEqual(1, stores.Store.Diagnostics.SealedRuns);
        Assert.AreEqual(0, stores.Store.Diagnostics.ActiveWriters);
        Assert.AreEqual(0, stores.Store.Diagnostics.PendingSealOperations);
        Assert.AreEqual(0, stores.Store.Diagnostics.Runs![0].PendingOperations);
    }

    private sealed class StoreCheckingSink(IInspectionDataStore store, InspectionId inspectionId,
        AcquisitionRunId runId, UtSourceId sourceId) : IConventionalFrameSink
    {
        public int AcceptCount { get; private set; }
        public void OpenRun(ConventionalUtFrameMetadata metadata) { }
        public void Accept(ConventionalUtFrameMetadata metadata, ulong sequence, ulong acquisitionIndex,
            TimeSpan elapsedSinceRunStart, ScanSpatialCoordinate? spatialCoordinate, ReadOnlySpan<short> samples)
        {
            var identity = new TraceIdentity(new UtStreamIdentity(inspectionId, runId, sourceId,
                metadata.Configuration.PhysicalChannelId), acquisitionIndex);
            InspectionTraceReadResult read = store.ReadAsync(identity).AsTask().GetAwaiter().GetResult();
            using IInspectionTraceLease lease = read.Lease!;
            Assert.AreEqual(InspectionReadStatus.Available, read.Status);
            Assert.AreEqual(samples[0], lease.Samples[0]);
            AcceptCount++;
        }
        public void CloseRun(AcquisitionRunId runId) { }
    }

    private sealed class CountingSink : IConventionalFrameSink
    {
        public int AcceptCount { get; private set; }
        public void OpenRun(ConventionalUtFrameMetadata metadata) { }
        public void Accept(ConventionalUtFrameMetadata metadata, ulong sequence, ulong acquisitionIndex,
            TimeSpan elapsedSinceRunStart, ScanSpatialCoordinate? spatialCoordinate, ReadOnlySpan<short> samples) =>
            AcceptCount++;
        public void CloseRun(AcquisitionRunId runId) { }
    }
}
