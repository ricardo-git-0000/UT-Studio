using UTStudio.Application;
using UTStudio.Contracts.Application;
using UTStudio.Contracts.Inspection;
using UTStudio.Contracts.Processing;
using UTStudio.Domain.Acquisition;
using UTStudio.Domain.Inspection;
using UTStudio.Inspection.Core;
using UTStudio.Processing.Core;
using UTStudio.Core.Tests.Processing;

namespace UTStudio.Core.Tests.Application;

[TestClass]
public sealed class ApplicationSessionInspectionStoreTests
{
    private static readonly ConventionalAcquisitionConfiguration Configuration =
        new(new PhysicalChannelId(0), 8, 50_000_000, firstSampleOffsetSeconds: 0.000_001);

    [TestMethod]
    [DataRow(1, 1_000_000d, 0.000_003d)]
    [DataRow(8, 2_000_000d, -0.000_002d)]
    public async Task ProductivePlanCoversTheCompleteConfiguredTrace(int samples, double rate, double offset)
    {
        var inspection = new InspectionId(Guid.NewGuid());
        await using var stores = new InspectionDataStoreRegistry(inspection, new(1024, 8));
        var configuration = new ConventionalAcquisitionConfiguration(new(0), samples, rate, offset);
        await using ProductiveGateProcessing processing = ProductiveGateProcessingFactory.Create(stores.Store, configuration);
        GateWindowDefinition window = processing.Plan.DeclaredGates.Single().Definition.Window;
        Assert.AreEqual(offset, window.StartOffsetSeconds);
        Assert.AreEqual(samples / rate, window.DurationSeconds);
    }

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
        Assert.AreEqual(2UL, stores.Store.Revision.Value);
        Assert.AreEqual(stores.Store.Revision, gaps[0].ConfirmedRevision);
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
        { AfterGapPrepared = _ => throw new InvalidOperationException("synthetic") };
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

    [TestMethod]
    public async Task AScanNotifyFailureDoesNotPreventBScanNotification()
    {
        await using var source = new ManualSessionSource();
        var inspectionId = new InspectionId(Guid.NewGuid());
        await using var stores = new InspectionDataStoreRegistry(inspectionId,
            new InMemoryInspectionStoreOptions(1024, 10));
        var bScan = new CountingSink();
        var aScan = new FailingNotifySink();
        await using var session = new ApplicationSession(source, inspectionId, stores.OpenRun,
            bScan, aScan);
        await session.StartAsync(Configuration, new(Guid.NewGuid())).WaitAsync(TimeSpan.FromSeconds(10));

        var owner = source.QueueFrame(0);
        await owner.Returned.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await session.StopAsync().WaitAsync(TimeSpan.FromSeconds(10));

        Assert.AreEqual(1, bScan.AcceptCount);
        Assert.AreEqual(1, aScan.NotifyAttempts);
        Assert.AreEqual(1, session.Snapshot.CleanupErrors.Count(
            static error => error.Code == "session.visual.ascan"));
    }

    [TestMethod]
    public async Task BScanNotifyFailureDoesNotPreventAScanNotification()
    {
        await using var source = new ManualSessionSource();
        var inspectionId = new InspectionId(Guid.NewGuid());
        await using var stores = new InspectionDataStoreRegistry(inspectionId,
            new InMemoryInspectionStoreOptions(1024, 10));
        var bScan = new FailingNotifySink();
        var aScan = new CountingSink();
        await using var session = new ApplicationSession(source, inspectionId, stores.OpenRun,
            bScan, aScan);
        await session.StartAsync(Configuration, new(Guid.NewGuid())).WaitAsync(TimeSpan.FromSeconds(10));

        var owner = source.QueueFrame(0);
        await owner.Returned.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await session.StopAsync().WaitAsync(TimeSpan.FromSeconds(10));

        Assert.AreEqual(1, aScan.AcceptCount);
        Assert.AreEqual(1, bScan.NotifyAttempts);
        Assert.AreEqual(1, session.Snapshot.CleanupErrors.Count(
            static error => error.Code == "session.visual.bscan"));
    }

    [TestMethod]
    public async Task StopPublishesFinalRevisionThenDrainsAndFreezesBScan()
    {
        await using var source = new ManualSessionSource();
        var inspectionId = new InspectionId(Guid.NewGuid());
        await using var stores = new InspectionDataStoreRegistry(inspectionId,
            new InMemoryInspectionStoreOptions(1024, 10));
        var sink = new RecordingLifecycleSink(stores.Store);
        await using var session = new ApplicationSession(source, inspectionId, stores.OpenRun, sink);
        await session.StartAsync(Configuration, new(Guid.NewGuid())).WaitAsync(TimeSpan.FromSeconds(10));
        var owner = source.QueueFrame(0);
        await owner.Returned.Task.WaitAsync(TimeSpan.FromSeconds(10));

        await session.StopAsync().WaitAsync(TimeSpan.FromSeconds(10));

        CollectionAssert.AreEqual(new[] { "final:1", "stop", "drain", "freeze" }, sink.Events);
        Assert.AreEqual(1, stores.Store.Diagnostics.SealedRuns);
    }

    [TestMethod]
    public async Task Scenarios01_02_08_CommitPrecedesSampleFreeProcessingAndThenVisualBranches()
    {
        await using var source = new ManualSessionSource(); var inspection = new InspectionId(Guid.NewGuid());
        await using var stores = new InspectionDataStoreRegistry(inspection, new(1024, 10));
        var events = new List<string>(); var processing = new ProcessingProbe(stores.Store, events);
        var b = new OrderedSink("B", events); var a = new OrderedSink("A", events);
        await using var session = new ApplicationSession(source, inspection, stores.OpenRun, b, a,
            processing, ProcessingPlan());
        await session.StartAsync(Configuration, new(Guid.NewGuid())).WaitAsync(TimeSpan.FromSeconds(10));
        var owner = source.QueueFrame(0); await owner.Returned.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await session.StopAsync().WaitAsync(TimeSpan.FromSeconds(10));
        CollectionAssert.AreEqual(new[] { "processing", "A", "B" }, events.Take(3).ToArray());
        Assert.IsTrue(processing.StoreWasAuthoritative);
        Assert.IsFalse(typeof(GateProcessingSignal).GetProperties().Any(x =>
            x.Name.Contains("Sample", StringComparison.OrdinalIgnoreCase)));
    }

    [TestMethod]
    [DataRow(GateProcessingAdmissionStatus.Rejected, false)]
    [DataRow(GateProcessingAdmissionStatus.Closed, false)]
    [DataRow(GateProcessingAdmissionStatus.Accepted, true)]
    public async Task Scenarios09_10_17_ProcessingFailureNeverUndoesCommitOrBlocksVisuals(
        GateProcessingAdmissionStatus status, bool throws)
    {
        await using var source = new ManualSessionSource(); var inspection = new InspectionId(Guid.NewGuid());
        await using var stores = new InspectionDataStoreRegistry(inspection, new(1024, 10));
        var processing = new ProcessingProbe(stores.Store, [], status, throws);
        var b = new CountingSink(); var a = new CountingSink();
        await using var session = new ApplicationSession(source, inspection, stores.OpenRun, b, a,
            processing, ProcessingPlan());
        AcquisitionRunId run = new(Guid.NewGuid());
        await session.StartAsync(Configuration, run).WaitAsync(TimeSpan.FromSeconds(10));
        var owner = source.QueueFrame(0); await owner.Returned.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await session.StopAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.AreEqual(1UL, stores.Store.Revision.Value);
        Assert.AreEqual(1, a.AcceptCount); Assert.AreEqual(1, b.AcceptCount);
        InspectionTraceReadResult read = await stores.Store.ReadAsync(new(new(inspection, run, source.SourceId,
            Configuration.PhysicalChannelId), 0));
        Assert.AreEqual(InspectionReadStatus.Available, read.Status); read.Lease!.Dispose();
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task ProcessingFinalPublicationFailureDefersDrainAndDisposeRetriesWithoutResealing(
        bool retrySucceeds)
    {
        await using var source = new ManualSessionSource(); var inspection = new InspectionId(Guid.NewGuid());
        await using var stores = new InspectionDataStoreRegistry(inspection, new(1024, 10));
        var processing = new FinalFailureProbe(retrySucceeds ? 1 : int.MaxValue);
        CountingSealWriter? writer = null;
        var session = new ApplicationSession(source, inspection, runId =>
        {
            writer = new(stores.OpenRun(runId));
            return writer;
        },
            new CountingSink(), new CountingSink(), processing, ProcessingPlan());
        await session.StartAsync(Configuration, new(Guid.NewGuid())).WaitAsync(TimeSpan.FromSeconds(10));
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => session.StopAsync().WaitAsync(TimeSpan.FromSeconds(10)));

        Assert.AreEqual(1, processing.StopCalls);
        Assert.AreEqual(1, writer!.SealCalls);
        Assert.AreEqual(1, processing.FinalCalls);
        Assert.AreEqual(0, processing.DrainCalls);
        Assert.AreEqual("session.processing.final", session.Snapshot.PrimaryError!.Code);
        Assert.AreEqual("final-1", session.Snapshot.PrimaryError.Message);

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => session.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10)));

        Assert.AreEqual(1, processing.StopCalls);
        Assert.AreEqual(1, writer.SealCalls);
        Assert.AreEqual(2, processing.FinalCalls);
        Assert.AreEqual(retrySucceeds ? 1 : 0, processing.DrainCalls);
        Assert.AreEqual(SessionPhase.Disposed, session.Snapshot.Phase);
        Assert.AreEqual("session.processing.final", session.Snapshot.PrimaryError!.Code);
        Assert.AreEqual("final-1", session.Snapshot.PrimaryError.Message);
        CollectionAssert.AreEqual(
            processing.FinalRevisions.Select(x => x.Value).Take(1).ToArray(),
            processing.FinalRevisions.Select(x => x.Value).Skip(1).ToArray());
        if (retrySucceeds)
            Assert.IsFalse(session.Snapshot.CleanupErrors.Any(x => x.Code == "session.processing.final"));
        else
        {
            UtSourceError secondary = session.Snapshot.CleanupErrors
                .Single(x => x.Code == "session.processing.final");
            Assert.AreEqual("final-2", secondary.Message);
        }
    }

    private sealed class StoreCheckingSink(IInspectionDataStore store, InspectionId inspectionId,
        AcquisitionRunId runId, UtSourceId sourceId) : ICommittedTraceSink
    {
        public int AcceptCount { get; private set; }
        public void OpenRun(AcquisitionRunId runId) { }
        public void NotifyCommitted(TraceIdentity identity, InspectionRevision revision)
        {
            InspectionTraceReadResult read = store.ReadAsync(identity).AsTask().GetAwaiter().GetResult();
            using IInspectionTraceLease lease = read.Lease!;
            Assert.AreEqual(InspectionReadStatus.Available, read.Status);
            Assert.AreEqual(identity.Stream.InspectionId, inspectionId);
            Assert.AreEqual(identity.Stream.AcquisitionRunId, runId);
            Assert.AreEqual(identity.Stream.SourceId, sourceId);
            AcceptCount++;
        }
        public void CloseRun(AcquisitionRunId runId) { }
    }

    private sealed class CountingSink : ICommittedTraceSink
    {
        public int AcceptCount { get; private set; }
        public void OpenRun(AcquisitionRunId runId) { }
        public void NotifyCommitted(TraceIdentity identity, InspectionRevision revision) => AcceptCount++;
        public void CloseRun(AcquisitionRunId runId) { }
    }

    private sealed class OrderedSink(string name, List<string> events) : ICommittedTraceSink
    {
        public void OpenRun(AcquisitionRunId runId) { }
        public void NotifyCommitted(TraceIdentity identity, InspectionRevision revision) => events.Add(name);
        public void CloseRun(AcquisitionRunId runId) { }
    }

    private sealed class ProcessingProbe(IInspectionDataStore store, List<string> events,
        GateProcessingAdmissionStatus notification = GateProcessingAdmissionStatus.Accepted,
        bool throws = false) : ICommittedGateProcessingIngress
    {
        internal bool StoreWasAuthoritative;
        public GateProcessingAdmissionStatus OpenRun(GateProcessingRunRegistration registration) => GateProcessingAdmissionStatus.Accepted;
        public GateProcessingAdmissionStatus NotifyCommitted(GateProcessingSignal signal)
        {
            InspectionTraceReadResult read = store.ReadAsync(signal.TraceIdentity).AsTask().GetAwaiter().GetResult();
            StoreWasAuthoritative = read.Status == InspectionReadStatus.Available && read.Revision == signal.Revision;
            read.Lease?.Dispose(); events.Add("processing");
            if (throws) { throw new InvalidOperationException("synthetic processing failure"); }
            return notification;
        }
        public void NotifyProviderProgress(AcquisitionRunId runId) { }
        public void RequestFinalReconciliation(AcquisitionRunId runId, InspectionRevision finalRevision) { }
        public void StopAdmission(AcquisitionRunId runId) { }
        public ValueTask WaitForDrainAsync(AcquisitionRunId runId) => ValueTask.CompletedTask;
    }

    private sealed class FinalFailureProbe(int failuresBeforeSuccess) : ICommittedGateProcessingIngress
    {
        private int _stopCalls;
        private int _finalCalls;
        private int _drainCalls;
        internal int StopCalls => Volatile.Read(ref _stopCalls);
        internal int FinalCalls => Volatile.Read(ref _finalCalls);
        internal int DrainCalls => Volatile.Read(ref _drainCalls);
        internal List<InspectionRevision> FinalRevisions { get; } = [];
        public GateProcessingAdmissionStatus OpenRun(GateProcessingRunRegistration registration) => GateProcessingAdmissionStatus.Accepted;
        public GateProcessingAdmissionStatus NotifyCommitted(GateProcessingSignal signal) => GateProcessingAdmissionStatus.Accepted;
        public void NotifyProviderProgress(AcquisitionRunId runId) { }
        public void RequestFinalReconciliation(AcquisitionRunId runId, InspectionRevision finalRevision)
        {
            FinalRevisions.Add(finalRevision);
            int call = Interlocked.Increment(ref _finalCalls);
            if (call <= failuresBeforeSuccess) { throw new InvalidOperationException($"final-{call}"); }
        }
        public void StopAdmission(AcquisitionRunId runId) => Interlocked.Increment(ref _stopCalls);
        public ValueTask WaitForDrainAsync(AcquisitionRunId runId)
        {
            Interlocked.Increment(ref _drainCalls);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class CountingSealWriter(IInspectionRunWriter inner) : IInspectionRunWriter
    {
        private int _sealCalls;
        internal int SealCalls => Volatile.Read(ref _sealCalls);
        public AcquisitionRunId RunId => inner.RunId;
        public InspectionRunState State => inner.State;
        public ValueTask<InspectionAppendResult> CommitAsync(
            InspectionCommitInput commit, CancellationToken cancellationToken = default) =>
            inner.CommitAsync(commit, cancellationToken);
        public ValueTask<InspectionAppendResult> CommitGapAsync(
            GapRecord gap, CancellationToken cancellationToken = default) =>
            inner.CommitGapAsync(gap, cancellationToken);
        public ValueTask<InspectionRunSealResult> SealRunAsync(CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _sealCalls);
            return inner.SealRunAsync(cancellationToken);
        }
        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }

    private static CompiledGatePlan ProcessingPlan() => new GatePlanCompiler().Compile(
        GateTestPlan.Plan([GateTestPlan.Gate("A", durationSeconds: 0.0000001)])).CompiledPlan!;

    private sealed class FailingNotifySink : ICommittedTraceSink
    {
        internal int NotifyAttempts { get; private set; }
        public void OpenRun(AcquisitionRunId runId) { }
        public void NotifyCommitted(TraceIdentity identity, InspectionRevision revision)
        { NotifyAttempts++; throw new InvalidOperationException("synthetic A failure"); }
        public void CloseRun(AcquisitionRunId runId) { }
    }

    private sealed class RecordingLifecycleSink(IInspectionDataStore store) : ICommittedTraceLifecycleSink
    {
        internal List<string> Events { get; } = [];
        public void OpenRun(AcquisitionRunId runId) { }
        public void NotifyCommitted(TraceIdentity identity, InspectionRevision revision) { }
        public void CloseRun(AcquisitionRunId runId) => StopAdmission(runId);
        public ValueTask WaitForCloseAsync(AcquisitionRunId runId) => WaitForDrainAsync(runId);
        public void RequestFinalReconciliation(AcquisitionRunId runId, InspectionRevision finalRevision)
        {
            Assert.AreEqual(finalRevision, store.Revision);
            Assert.AreEqual(1, store.Diagnostics.SealedRuns);
            Events.Add($"final:{finalRevision.Value}");
        }
        public void StopAdmission(AcquisitionRunId runId) => Events.Add("stop");
        public ValueTask WaitForDrainAsync(AcquisitionRunId runId)
        { Events.Add("drain"); return ValueTask.CompletedTask; }
        public void FreezeRun(AcquisitionRunId runId) => Events.Add("freeze");
    }
}
