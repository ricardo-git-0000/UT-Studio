using UTStudio.Contracts.Inspection;
using UTStudio.Contracts.Processing;
using UTStudio.Domain.Acquisition;
using UTStudio.Domain.Inspection;
using UTStudio.Domain.Processing;
using UTStudio.Inspection.Core;
using UTStudio.Processing.Core;

namespace UTStudio.Core.Tests.Processing;

[TestClass]
public sealed class GateReprocessingSchedulerTests
{
    private static readonly InspectionId Inspection = new(Guid.Parse("33333333-3333-3333-3333-333333333333"));
    private static readonly AcquisitionRunId Run = new(Guid.Parse("44444444-4444-4444-4444-444444444444"));
    private static readonly UtStreamIdentity Stream = new(Inspection, Run, new UtSourceId("reprocess"), new PhysicalChannelId(2));

    [TestMethod]
    public void HistoricalSampleBufferBudgetReservesRejectsAndBalancesExactly()
    {
        var pool = new GateReprocessingSampleBufferPool(
            2L * GateReprocessingSampleBufferPool.BucketBytes);
        GateReprocessingSampleBufferLease first = pool.Rent();
        GateReprocessingSampleBufferLease second = pool.Rent();
        short[] firstBuffer = first.Buffer;
        short[] secondBuffer = second.Buffer;
        GateReprocessingSampleBufferDiagnostics peak = pool.Diagnostics;
        Assert.AreEqual(2, peak.ActiveBuffers);
        Assert.AreEqual(2, peak.PeakBuffers);
        Assert.AreEqual(2L * GateReprocessingSampleBufferPool.BucketBytes, peak.OwnedBytes);
        Assert.ThrowsExactly<InvalidOperationException>(() => pool.Rent());
        first.Dispose();
        Assert.ThrowsExactly<InvalidOperationException>(() => first.Dispose());
        second.Dispose();
        GateReprocessingSampleBufferLease reused = pool.Rent();
        Assert.IsTrue(ReferenceEquals(reused.Buffer, firstBuffer) || ReferenceEquals(reused.Buffer, secondBuffer));
        reused.Dispose();
        GateReprocessingSampleBufferDiagnostics final = pool.Diagnostics;
        Assert.AreEqual(0, final.ActiveBuffers);
        Assert.AreEqual(0L, final.OwnedBytes);
        Assert.AreEqual(0L, final.ReservedBytes);
        Assert.AreEqual(3L, final.Rents);
        Assert.AreEqual(3L, final.Returns);
        Assert.AreEqual(1L, final.Rejections);
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            new GateReprocessingSampleBufferPool(GateReprocessingSampleBufferPool.BucketBytes - 1));
    }

    [TestMethod]
    public void HistoricalBufferInvariantBudgetsOnePerWorkerAndRejectsForeignAndDoubleReturn()
    {
        const int workers = 3;
        long exactBudget = workers * GateReprocessingSampleBufferPool.BucketBytes;
        var options = new GateReprocessingSchedulerOptions(workers, workers, exactBudget);
        Assert.AreEqual(exactBudget, options.SampleBufferBudgetBytes);
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            new GateReprocessingSchedulerOptions(workers, workers, exactBudget - 1));

        using var shared = new GateReprocessingSampleBufferPool(exactBudget);
        var owner = new JobReprocessingBufferPool(shared);
        var foreignOwner = new JobReprocessingBufferPool(shared);
        JobReprocessingBufferLease[] leases = Enumerable.Range(0, workers).Select(_ => owner.Rent()).ToArray();
        Assert.ThrowsExactly<InvalidOperationException>(() => owner.Rent());

        leases[0].Dispose();
        JobReprocessingBufferLease foreign = foreignOwner.Rent();
        Assert.ThrowsExactly<InvalidOperationException>(() => owner.Return(foreign));
        foreign.Dispose();
        foreach (JobReprocessingBufferLease lease in leases.Skip(1)) lease.Dispose();
        Assert.ThrowsExactly<InvalidOperationException>(() => leases[0].Dispose());

        JobReprocessingBufferDiagnostics job = owner.Diagnostics;
        GateReprocessingSampleBufferDiagnostics physical = shared.Diagnostics;
        Assert.AreEqual(0, job.ActiveBuffers);
        Assert.AreEqual(0L, job.OwnedBytes);
        Assert.AreEqual(job.RentSucceeded, job.ReturnSucceeded);
        Assert.AreEqual(0, physical.ActiveBuffers);
        Assert.AreEqual(0L, physical.OwnedBytes);
        Assert.AreEqual(0L, physical.ReservedBytes);
        Assert.AreEqual(physical.Rents, physical.Returns);
        Assert.AreEqual(exactBudget, physical.CapacityBytes);
    }

    [TestMethod]
    public void CoverageAccumulatorOwnsOnlyLocalCryptographicStateAndNoExternalResources()
    {
        Type accumulator = typeof(GateReprocessingScheduler).GetNestedType(
            "CoverageAccumulator", System.Reflection.BindingFlags.NonPublic)!;
        Type[] forbidden =
        [
            typeof(IInspectionTraceLease), typeof(GateScratchLease), typeof(JobReprocessingBufferLease),
            typeof(Delegate), typeof(Stream), typeof(IGateDerivedStore)
        ];
        System.Reflection.FieldInfo[] fields = accumulator.GetFields(
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);

        Assert.IsFalse(fields.Any(field => forbidden.Any(type => type.IsAssignableFrom(field.FieldType))),
            string.Join(",", fields.Select(field => field.FieldType.FullName)));
        Assert.HasCount(1, fields.Where(field =>
            field.FieldType == typeof(System.Security.Cryptography.IncrementalHash)).ToArray());
        System.Reflection.MethodInfo dispose = accumulator.GetMethod("Dispose",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public)!;
        Assert.AreEqual(typeof(void), dispose.ReturnType);
        Assert.HasCount(0, dispose.GetParameters());
        var selection = new GateReprocessingStreamSelection(Stream, [new(0, 0)]);
        object instance = Activator.CreateInstance(accumulator,
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic,
            binder: null, args: [selection, 0], culture: null)!;
        dispose.Invoke(instance, null);
        dispose.Invoke(instance, null);
    }

    [TestMethod]
    public void SelectionNormalizesOverlapAdjacencyDuplicatesAndMaxValue()
    {
        var selection = new GateReprocessingStreamSelection(Stream,
        [
            new(5, 8), new(1, 3), new(3, 5), new(1, 3), new(ulong.MaxValue, ulong.MaxValue)
        ]);
        Assert.HasCount(2, selection.Ranges);
        Assert.AreEqual(new AcquisitionIndexRange(1, 8), selection.Ranges[0]);
        Assert.AreEqual(new AcquisitionIndexRange(ulong.MaxValue, ulong.MaxValue), selection.Ranges[1]);
    }

    [TestMethod]
    public async Task SparseRunCompletesStoreOnlyAndSecondAttemptReusesDuplicates()
    {
        await using var rf = new InMemoryInspectionDataStore(Inspection, new(32_000, 64));
        IInspectionRunWriter writer = await rf.OpenRunAsync(Run);
        await CommitAsync(writer, 1); await CommitAsync(writer, 3); await CommitAsync(writer, 9);
        InspectionRunSealResult seal = await writer.SealRunAsync();
        await using var derived = new InMemoryGateDerivedStore(new(32, 1_000_000));
        CompiledGatePlan plan = Compile();

        await using (var scheduler = new GateReprocessingScheduler(rf, derived,
            new(maximumWorkers: 1, maximumAdmittedJobs: 2), new GateScratchPool(1, 300_000)))
        {
            IGateReprocessingJob first = (await scheduler.AdmitAsync(Descriptor(plan, seal.FinalRevision,
                Guid.NewGuid(), Guid.NewGuid()))).Job!;
            GateReprocessingProgressSnapshot completed = await first.Completion;
            Assert.AreEqual(GateReprocessingLifecycle.Completed, completed.Lifecycle);
            Assert.AreEqual(3L, completed.SelectedTraceCount);
            Assert.AreEqual(3L, completed.NewlyCommittedBatchCount);
            Assert.AreEqual(0L, completed.ReusedBatchCount);
            Assert.AreEqual(3L, completed.BufferRents);
            Assert.AreEqual(3L, completed.BufferReturns);
            Assert.AreEqual(3L, completed.LeasesAcquired);
            Assert.AreEqual(3L, completed.LeasesReleased);
            Assert.IsTrue(completed.CleanupConfirmed);
            Assert.AreEqual(0, completed.ActiveWorkers);
            Assert.AreEqual(0, completed.ActiveCallbacks);
        }

        await using (var scheduler = new GateReprocessingScheduler(rf, derived,
            new(maximumWorkers: 1, maximumAdmittedJobs: 2), new GateScratchPool(1, 300_000)))
        {
            IGateReprocessingJob second = (await scheduler.AdmitAsync(Descriptor(plan, seal.FinalRevision,
                Guid.NewGuid(), Guid.NewGuid()))).Job!;
            GateReprocessingProgressSnapshot completed = await second.Completion;
            Assert.AreEqual(GateReprocessingLifecycle.Completed, completed.Lifecycle);
            Assert.AreEqual(0L, completed.NewlyCommittedBatchCount);
            Assert.AreEqual(3L, completed.ReusedBatchCount);
        }
        Assert.AreEqual(3, derived.Diagnostics.BatchCount);
    }

    [TestMethod]
    public async Task CancellationBeforeWorkLeavesNoTerminalCoverageOrView()
    {
        await using var rf = new InMemoryInspectionDataStore(Inspection, new(32_000, 64));
        IInspectionRunWriter writer = await rf.OpenRunAsync(Run);
        for (ulong index = 0; index < 8; index++) await CommitAsync(writer, index);
        InspectionRunSealResult seal = await writer.SealRunAsync();
        await using var derived = new InMemoryGateDerivedStore(new(32, 1_000_000));
        await using var scheduler = new GateReprocessingScheduler(rf, derived,
            new(maximumWorkers: 1, maximumAdmittedJobs: 2), new GateScratchPool(1, 300_000));
        GateReprocessingJobDescriptor descriptor = Descriptor(Compile(), seal.FinalRevision,
            Guid.NewGuid(), Guid.NewGuid(), GateReprocessingPublicationPolicy.ActivateOnComplete);
        IGateReprocessingJob job = (await scheduler.AdmitAsync(descriptor)).Job!;
        job.Cancel();
        GateReprocessingProgressSnapshot terminal = await job.Completion;
        Assert.AreEqual(GateReprocessingLifecycle.Cancelled, terminal.Lifecycle);
        Assert.IsNull(derived.ActiveView);
    }

    [TestMethod]
    public async Task ActivateOnCompletePublishesExactSparseViewOnlyAtTerminalState()
    {
        await using var rf = new InMemoryInspectionDataStore(Inspection, new(32_000, 64));
        IInspectionRunWriter writer = await rf.OpenRunAsync(Run);
        await CommitAsync(writer, 2); await CommitAsync(writer, 6);
        InspectionRunSealResult seal = await writer.SealRunAsync();
        await using var derived = new InMemoryGateDerivedStore(new(32, 1_000_000));
        await using var scheduler = new GateReprocessingScheduler(rf, derived,
            new(maximumWorkers: 1, maximumAdmittedJobs: 2), new GateScratchPool(1, 300_000));
        IGateReprocessingJob job = (await scheduler.AdmitAsync(Descriptor(Compile(), seal.FinalRevision,
            Guid.NewGuid(), Guid.NewGuid(), GateReprocessingPublicationPolicy.ActivateOnComplete))).Job!;

        GateReprocessingProgressSnapshot terminal = await job.Completion;

        Assert.AreEqual(GateReprocessingLifecycle.Completed, terminal.Lifecycle, terminal.PrimaryError);
        Assert.AreEqual(2, terminal.BlocksTotal);
        Assert.AreEqual(2, terminal.BlocksCompleted);
        Assert.AreEqual(new DerivedViewId("reprocessed"), derived.ActiveView!.ViewId);
        Assert.AreEqual(2, derived.ActiveView.Manifest.TotalEligibleTraceCount);
    }

    [TestMethod]
    public async Task TwoRegionalViewsWithSamePlanUseIndependentCoverageScopes()
    {
        await using var rf = new InMemoryInspectionDataStore(Inspection, new(32_000, 64));
        IInspectionRunWriter writer = await rf.OpenRunAsync(Run);
        await CommitAsync(writer, 1); await CommitAsync(writer, 3);
        InspectionRunSealResult seal = await writer.SealRunAsync();
        await using var derived = new InMemoryGateDerivedStore(new(32, 1_000_000));
        CompiledGatePlan plan = Compile();

        await RunRegionAsync(1, "region-1");
        await RunRegionAsync(3, "region-3");

        Assert.AreEqual(new DerivedViewId("region-3"), derived.ActiveView!.ViewId);
        Assert.AreEqual(1, derived.ActiveView.Manifest.TotalEligibleTraceCount);
        Assert.AreNotEqual("full", derived.ActiveView.Manifest.CoverageScope);

        async Task RunRegionAsync(ulong index, string view)
        {
            await using var scheduler = new GateReprocessingScheduler(rf, derived,
                new(maximumWorkers: 1, maximumAdmittedJobs: 2), new GateScratchPool(1, 300_000));
            var descriptor = new GateReprocessingJobDescriptor(new(Guid.NewGuid()), new(Guid.NewGuid()),
                Inspection, Run, seal.FinalRevision,
                [new GateReprocessingStreamSelection(Stream, [new(index, index)])], plan, 1,
                GateReprocessingPublicationPolicy.ActivateOnComplete, new DerivedViewId(view), 1);
            Assert.AreEqual(GateReprocessingLifecycle.Completed,
                (await (await scheduler.AdmitAsync(descriptor)).Job!.Completion).Lifecycle);
        }
    }

    [TestMethod]
    [DataRow(InspectionReadStatus.NotAvailable, "coverage-mismatch")]
    [DataRow(InspectionReadStatus.Gap, "coverage-mismatch")]
    [DataRow(InspectionReadStatus.OutOfRange, "coverage-mismatch")]
    [DataRow(InspectionReadStatus.Cancelled, "provider-fault")]
    [DataRow(InspectionReadStatus.Closed, "provider-fault")]
    [DataRow(InspectionReadStatus.Faulted, "provider-fault")]
    public async Task ProviderTerminalResultsAreClassifiedBeforeEvaluation(
        InspectionReadStatus status, string expectedError)
    {
        InspectionTraceReadResult result = InspectionTraceReadResult.Unavailable(status, new(1),
            status == InspectionReadStatus.Faulted ? "injected" : null);
        var provider = new ScriptedSnapshotProvider(result);
        await using var derived = new InMemoryGateDerivedStore();
        await using var scheduler = new GateReprocessingScheduler(provider, derived);
        IGateReprocessingJob job = (await scheduler.AdmitAsync(Descriptor(
            Compile(), new(1), Guid.NewGuid(), Guid.NewGuid()))).Job!;
        GateReprocessingProgressSnapshot terminal = await job.Completion;
        Assert.AreEqual(GateReprocessingLifecycle.Faulted, terminal.Lifecycle);
        StringAssert.StartsWith(terminal.PrimaryError, expectedError);
        Assert.AreEqual(0, terminal.ActiveLeases);
        Assert.AreEqual(0, terminal.ActiveBuffers);
    }

    [TestMethod]
    public async Task IncompatibleProviderIdentityIsCoverageMismatchAndLeaseIsReleased()
    {
        var wrong = new TraceIdentity(Stream, 99);
        var descriptor = new TraceDescriptor(wrong, 0, TimeSpan.Zero, null, null,
            4, 10_000_000, 0, UtSignalMode.Rf, traceQuality: UtTraceQuality.Good);
        var lease = new ScriptedLease(descriptor);
        var provider = new ScriptedSnapshotProvider(InspectionTraceReadResult.Available(new(1), lease));
        await using var derived = new InMemoryGateDerivedStore();
        await using var scheduler = new GateReprocessingScheduler(provider, derived);
        GateReprocessingProgressSnapshot terminal = await (await scheduler.AdmitAsync(Descriptor(
            Compile(), new(1), Guid.NewGuid(), Guid.NewGuid()))).Job!.Completion;
        Assert.AreEqual(GateReprocessingLifecycle.Faulted, terminal.Lifecycle);
        StringAssert.StartsWith(terminal.PrimaryError, "coverage-mismatch");
        Assert.AreEqual(1, lease.DisposeCount);
        Assert.AreEqual(0, derived.Diagnostics.BatchCount);
    }

    [TestMethod]
    public async Task IncompatibleReadRevisionIsCoverageMismatchAndLeaseIsReleased()
    {
        var identity = new TraceIdentity(Stream, 0);
        var descriptor = new TraceDescriptor(identity, 0, TimeSpan.Zero, null, null,
            4, 10_000_000, 0, UtSignalMode.Rf, traceQuality: UtTraceQuality.Good);
        var lease = new ScriptedLease(descriptor);
        var provider = new ScriptedSnapshotProvider(InspectionTraceReadResult.Available(new(2), lease));
        await using var derived = new InMemoryGateDerivedStore();
        await using var scheduler = new GateReprocessingScheduler(provider, derived);

        GateReprocessingProgressSnapshot terminal = await (await scheduler.AdmitAsync(Descriptor(
            Compile(), new(1), Guid.NewGuid(), Guid.NewGuid()))).Job!.Completion;

        Assert.AreEqual(GateReprocessingLifecycle.Faulted, terminal.Lifecycle);
        Assert.AreEqual("coverage-mismatch:descriptor-or-revision", terminal.PrimaryError);
        Assert.AreEqual(1, lease.DisposeCount);
        Assert.AreEqual(terminal.LeasesAcquired, terminal.LeasesReleased);
        Assert.IsTrue(terminal.CleanupConfirmed);
    }

    [TestMethod]
    public async Task DescriptorFailureTransfersOwnershipAndReleasesLeaseExactlyOnce()
    {
        var lease = new ThrowingDescriptorLease();
        var provider = new ScriptedSnapshotProvider(InspectionTraceReadResult.Available(new(1), lease));
        await using var derived = new InMemoryGateDerivedStore();
        await using var scheduler = new GateReprocessingScheduler(provider, derived);

        GateReprocessingProgressSnapshot terminal = await (await scheduler.AdmitAsync(Descriptor(
            Compile(), new(1), Guid.NewGuid(), Guid.NewGuid()))).Job!.Completion;

        Assert.AreEqual(GateReprocessingLifecycle.Faulted, terminal.Lifecycle);
        Assert.AreEqual(nameof(InvalidOperationException), terminal.PrimaryError);
        Assert.AreEqual(1, lease.DisposeCount);
        Assert.AreEqual(1L, terminal.LeasesAcquired);
        Assert.AreEqual(1L, terminal.LeasesReleased);
        Assert.AreEqual(0, terminal.ActiveLeases);
        Assert.AreEqual(0, terminal.BufferRents);
        Assert.IsTrue(terminal.CleanupConfirmed);
    }

    [TestMethod]
    public async Task DescriptorFailureRemainsPrimaryWhenLeaseCleanupAlsoFails()
    {
        var lease = new ThrowingDescriptorAndCleanupLease();
        var provider = new ScriptedSnapshotProvider(InspectionTraceReadResult.Available(new(1), lease));
        await using var derived = new InMemoryGateDerivedStore();
        await using var scheduler = new GateReprocessingScheduler(provider, derived);

        GateReprocessingProgressSnapshot terminal = await (await scheduler.AdmitAsync(Descriptor(
            Compile(), new(1), Guid.NewGuid(), Guid.NewGuid()))).Job!.Completion;

        Assert.AreEqual(GateReprocessingLifecycle.Faulted, terminal.Lifecycle);
        Assert.AreEqual(nameof(InvalidOperationException), terminal.PrimaryError);
        CollectionAssert.AreEqual(new[] { "lease-cleanup-InvalidOperationException" },
            terminal.SecondaryErrors.ToArray());
        Assert.AreEqual(1L, terminal.LeaseReleaseFailed);
        Assert.AreEqual(1, terminal.ActiveLeases);
        Assert.IsFalse(terminal.CleanupConfirmed);
        lease.RecoverDispose();
    }

    [TestMethod]
    public async Task SampleFailureReturnsBufferWithoutInventingUnrepresentableCleanupFailure()
    {
        var descriptor = new TraceDescriptor(new TraceIdentity(Stream, 0), 0, TimeSpan.Zero, null, null,
            4, 10_000_000, 0, UtSignalMode.Rf, traceQuality: UtTraceQuality.Good);
        var provider = new ScriptedSnapshotProvider(InspectionTraceReadResult.Available(new(1),
            new ThrowingSamplesLease(descriptor)));
        await using var derived = new InMemoryGateDerivedStore();
        await using var scheduler = new GateReprocessingScheduler(provider, derived);

        GateReprocessingProgressSnapshot terminal = await (await scheduler.AdmitAsync(Descriptor(
            Compile(), new(1), Guid.NewGuid(), Guid.NewGuid()))).Job!.Completion;

        Assert.AreEqual(GateReprocessingLifecycle.Faulted, terminal.Lifecycle);
        Assert.AreEqual(nameof(FormatException), terminal.PrimaryError);
        Assert.IsEmpty(terminal.SecondaryErrors);
        Assert.AreEqual(0L, terminal.BufferReturnFailed);
        Assert.AreEqual(0, terminal.ActiveBuffers);
        Assert.AreEqual(terminal.BufferRents, terminal.BufferReturns);
        Assert.IsTrue(terminal.CleanupConfirmed);
    }

    [TestMethod]
    public async Task SampleCopyFailureReturnsBufferAndLeaseExactlyOnce()
    {
        var descriptor = new TraceDescriptor(new TraceIdentity(Stream, 0), 0, TimeSpan.Zero, null, null,
            4, 10_000_000, 0, UtSignalMode.Rf, traceQuality: UtTraceQuality.Good);
        var lease = new ThrowingSamplesLease(descriptor);
        var provider = new ScriptedSnapshotProvider(InspectionTraceReadResult.Available(new(1), lease));
        await using var derived = new InMemoryGateDerivedStore();
        await using var scheduler = new GateReprocessingScheduler(provider, derived);

        GateReprocessingProgressSnapshot terminal = await (await scheduler.AdmitAsync(Descriptor(
            Compile(), new(1), Guid.NewGuid(), Guid.NewGuid()))).Job!.Completion;

        Assert.AreEqual(GateReprocessingLifecycle.Faulted, terminal.Lifecycle);
        Assert.AreEqual(nameof(FormatException), terminal.PrimaryError);
        Assert.IsEmpty(terminal.SecondaryErrors);
        Assert.AreEqual(1L, terminal.BufferRents);
        Assert.AreEqual(1L, terminal.BufferReturns);
        Assert.AreEqual(terminal.LeasesAcquired, terminal.LeasesReleased);
        Assert.AreEqual(0, terminal.ActiveBuffers);
        Assert.AreEqual(0, terminal.ActiveLeases);
        Assert.IsTrue(terminal.CleanupConfirmed);
    }

    [TestMethod]
    public async Task ScratchContentionIsReportedByHistoricalSchedulerAndRemainsBalanced()
    {
        await using var rf = new InMemoryInspectionDataStore(Inspection, new(32_000, 64));
        IInspectionRunWriter writer = await rf.OpenRunAsync(Run);
        await CommitAsync(writer, 0);
        InspectionRevision revision = (await writer.SealRunAsync()).FinalRevision;
        var scratch = new GateScratchPool(1, 300_000);
        Assert.IsTrue(scratch.TryRent(4, out GateScratchLease? held));
        await using var derived = new InMemoryGateDerivedStore();
        await using var scheduler = new GateReprocessingScheduler(rf, derived,
            new(maximumWorkers: 1, maximumAdmittedJobs: 1), scratch);
        CompiledGatePlan plan = GateEvaluationTestData.Compile(GateEvaluationTestData.Gate("scratch", 4,
            rate: 10_000_000, detector: GateDetectorKind.FirstLocalMaximum, local: new(1, 0, 1)));

        GateReprocessingProgressSnapshot terminal = await (await scheduler.AdmitAsync(Descriptor(
            plan, revision, Guid.NewGuid(), Guid.NewGuid()))).Job!.Completion;
        scratch.Return(held!);

        Assert.AreEqual(GateReprocessingLifecycle.Faulted, terminal.Lifecycle);
        Assert.AreEqual(GateEvaluationErrorCode.ScratchUnavailable.ToString(), terminal.PrimaryError);
        Assert.AreEqual(1L, terminal.ScratchProgress!.AcquireAttempts);
        Assert.AreEqual(1L, terminal.ScratchProgress.Rejected);
        Assert.AreEqual(0, terminal.ActiveScratch);
        Assert.AreEqual(0, scratch.Snapshot.ActiveRentals);
        Assert.AreEqual(scratch.Snapshot.RentCount, scratch.Snapshot.ReturnCount);
        Assert.IsTrue(terminal.CleanupConfirmed);
    }

    [TestMethod]
    [DataRow(GateDerivedMutationStatus.Conflict)]
    [DataRow(GateDerivedMutationStatus.CapacityExceeded)]
    [DataRow(GateDerivedMutationStatus.Closed)]
    [DataRow(GateDerivedMutationStatus.Faulted)]
    [DataRow(GateDerivedMutationStatus.CoverageMismatch)]
    public async Task TypedCommitFailureBecomesExactSchedulerTerminalResult(GateDerivedMutationStatus status)
    {
        var descriptor = new TraceDescriptor(new TraceIdentity(Stream, 0), 0, TimeSpan.Zero, null, null,
            4, 10_000_000, 0, UtSignalMode.Rf, traceQuality: UtTraceQuality.Good);
        var provider = new ScriptedSnapshotProvider(InspectionTraceReadResult.Available(new(1),
            new ScriptedLease(descriptor)));
        await using var store = new ScriptedCommitResultStore(status);
        await using var scheduler = new GateReprocessingScheduler(provider, store);

        GateReprocessingProgressSnapshot terminal = await (await scheduler.AdmitAsync(Descriptor(
            Compile(), new(1), Guid.NewGuid(), Guid.NewGuid()))).Job!.Completion;

        Assert.AreEqual(GateReprocessingLifecycle.Faulted, terminal.Lifecycle);
        Assert.AreEqual($"derived-{status}:scripted-{status}", terminal.PrimaryError);
        Assert.AreEqual(1L, terminal.CommitProgress!.Attempts);
        Assert.AreEqual(status == GateDerivedMutationStatus.Closed ? 0L : 1L, terminal.CommitProgress.Admitted);
        Assert.AreEqual(status == GateDerivedMutationStatus.Closed ? 0L : 1L, terminal.CommitProgress.Completed);
        Assert.AreEqual(status == GateDerivedMutationStatus.Closed ? 0L : 1L, terminal.CommitProgress.Failed);
        Assert.AreEqual(status == GateDerivedMutationStatus.Closed ? 1L : 0L,
            terminal.CommitProgress.RejectedBeforeAdmission);
        Assert.AreEqual(0L, terminal.NewlyCommittedBatchCount);
        Assert.AreEqual(0L, terminal.ReusedBatchCount);
        Assert.IsTrue(terminal.CleanupConfirmed);
    }

    [TestMethod]
    public async Task CommitCancellationBoundariesTraverseSchedulerOneHundredTimes()
    {
        for (int repetition = 0; repetition < 100; repetition++)
        {
            var descriptor = new TraceDescriptor(new TraceIdentity(Stream, 0), 0, TimeSpan.Zero, null, null,
                4, 10_000_000, 0, UtSignalMode.Rf, traceQuality: UtTraceQuality.Good);
            var provider = new ScriptedSnapshotProvider(InspectionTraceReadResult.Available(new(1),
                new ScriptedLease(descriptor)));
            await using var store = new ScriptedCommitResultStore(GateDerivedMutationStatus.Cancelled);
            await using var scheduler = new GateReprocessingScheduler(provider, store);
            GateReprocessingProgressSnapshot terminal = await (await scheduler.AdmitAsync(Descriptor(
                Compile(), new(1), Guid.NewGuid(), Guid.NewGuid()))).Job!.Completion;
            Assert.AreEqual(GateReprocessingLifecycle.Faulted, terminal.Lifecycle);
            Assert.AreEqual(1L, terminal.CommitProgress!.Admitted);
            Assert.AreEqual(1L, terminal.CommitProgress.Completed);
            Assert.IsTrue(terminal.CleanupConfirmed);
        }
    }

    [TestMethod]
    public async Task BufferAndLeaseCleanupFailuresPreservePrimaryAcrossOneHundredRuns()
    {
        for (int repetition = 0; repetition < 100; repetition++)
        {
            var descriptor = new TraceDescriptor(new TraceIdentity(Stream, 0), 0, TimeSpan.Zero, null, null,
                4, 10_000_000, 0, UtSignalMode.Rf, traceQuality: UtTraceQuality.Good);
            var bufferProvider = new ScriptedSnapshotProvider(InspectionTraceReadResult.Available(new(1),
                new ThrowingSamplesLease(descriptor)));
            await using (var derived = new InMemoryGateDerivedStore())
            await using (var scheduler = new GateReprocessingScheduler(bufferProvider, derived))
            {
                GateReprocessingProgressSnapshot terminal = await (await scheduler.AdmitAsync(Descriptor(
                    Compile(), new(1), Guid.NewGuid(), Guid.NewGuid()))).Job!.Completion;
                Assert.AreEqual(nameof(FormatException), terminal.PrimaryError);
                Assert.IsEmpty(terminal.SecondaryErrors);
                Assert.AreEqual(terminal.BufferRents, terminal.BufferReturns);
                Assert.IsTrue(terminal.CleanupConfirmed);
            }

            var lease = new ThrowingDescriptorAndCleanupLease();
            var leaseProvider = new ScriptedSnapshotProvider(InspectionTraceReadResult.Available(new(1), lease));
            await using (var derived = new InMemoryGateDerivedStore())
            await using (var scheduler = new GateReprocessingScheduler(leaseProvider, derived))
            {
                GateReprocessingProgressSnapshot terminal = await (await scheduler.AdmitAsync(Descriptor(
                    Compile(), new(1), Guid.NewGuid(), Guid.NewGuid()))).Job!.Completion;
                Assert.AreEqual(nameof(InvalidOperationException), terminal.PrimaryError);
                CollectionAssert.AreEqual(new[] { "lease-cleanup-InvalidOperationException" },
                    terminal.SecondaryErrors.ToArray());
                Assert.AreEqual(1L, terminal.LeaseReleaseFailed);
                Assert.IsFalse(terminal.CleanupConfirmed);
            }
            lease.RecoverDispose();
        }
    }

    [TestMethod]
    public async Task PrimaryAndMultipleCleanupFailuresAreFlattenedInStableOrderAcrossOneHundredRuns()
    {
        for (int repetition = 0; repetition < 100; repetition++)
        {
            var provider = new MultipleCleanupFailureProvider();
            await using var derived = new InMemoryGateDerivedStore();
            await using var scheduler = new GateReprocessingScheduler(provider, derived);

            GateReprocessingProgressSnapshot terminal = await (await scheduler.AdmitAsync(Descriptor(
                Compile(), new(1), Guid.NewGuid(), Guid.NewGuid()))).Job!.Completion;

            Assert.AreEqual(GateReprocessingLifecycle.Faulted, terminal.Lifecycle);
            Assert.AreEqual(nameof(FormatException), terminal.PrimaryError);
            CollectionAssert.AreEqual(new[]
            {
                "lease-cleanup-InvalidOperationException",
                "snapshot-cleanup-InvalidOperationException",
                "snapshot-diagnostics-InvalidOperationException"
            }, terminal.SecondaryErrors.ToArray());
            Assert.AreEqual(0L, terminal.BufferReturnFailed);
            Assert.AreEqual(1L, terminal.LeaseReleaseFailed);
            Assert.AreEqual(1L, terminal.SnapshotReleaseFailed);
            Assert.IsFalse(terminal.CleanupConfirmed);
            provider.Recover();
        }
    }

    [TestMethod]
    public async Task AdmissionFailureRemainsPrimaryWhenSnapshotCleanupAlsoFails()
    {
        await using var derived = new InMemoryGateDerivedStore();
        await using var scheduler = new GateReprocessingScheduler(new ThrowingAdmissionProvider(), derived);

        GateReprocessingAdmissionResult result = await scheduler.AdmitAsync(Descriptor(
            Compile(), new(1), Guid.NewGuid(), Guid.NewGuid()));

        Assert.AreEqual(GateReprocessingAdmissionStatus.Faulted, result.Status);
        Assert.AreEqual(nameof(InvalidDataException), result.ErrorCode);
        CollectionAssert.AreEqual(new[] { "snapshot-cleanup-InvalidOperationException" },
            result.SecondaryErrors.ToArray());
    }

    [TestMethod]
    public async Task StopDuringAdmissionRemainsClosedWhenSnapshotCleanupFails()
    {
        var provider = new GatedThrowingCleanupProvider();
        await using var derived = new InMemoryGateDerivedStore();
        await using var scheduler = new GateReprocessingScheduler(provider, derived);
        Task<GateReprocessingAdmissionResult> admission = scheduler.AdmitAsync(Descriptor(
            Compile(), new(1), Guid.NewGuid(), Guid.NewGuid())).AsTask();
        await provider.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Task stop = scheduler.StopAsync();
        provider.Release.TrySetResult();
        GateReprocessingAdmissionResult result = await admission.WaitAsync(TimeSpan.FromSeconds(5));
        await stop.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.AreEqual(GateReprocessingAdmissionStatus.Closed, result.Status);
        Assert.AreEqual("scheduler-stopped", result.ErrorCode);
        CollectionAssert.AreEqual(new[] { "snapshot-cleanup-InvalidOperationException" },
            result.SecondaryErrors.ToArray());
    }

    [TestMethod]
    public async Task SnapshotHeaderMismatchIsRejectedBeforeRegionalQueryPreparation()
    {
        var provider = new HeaderMismatchProvider();
        await using var derived = new InMemoryGateDerivedStore();
        await using var scheduler = new GateReprocessingScheduler(provider, derived);

        GateReprocessingAdmissionResult result = await scheduler.AdmitAsync(Descriptor(
            Compile(), new(1), Guid.NewGuid(), Guid.NewGuid()));

        Assert.AreEqual(GateReprocessingAdmissionStatus.Faulted, result.Status);
        Assert.AreEqual("snapshot-header-mismatch", result.ErrorCode);
        Assert.AreEqual(1, provider.DisposeCount);
        Assert.AreEqual(0, provider.QueryCount);
    }

    private static GateReprocessingJobDescriptor Descriptor(CompiledGatePlan plan, InspectionRevision revision,
        Guid job, Guid attempt, GateReprocessingPublicationPolicy policy = GateReprocessingPublicationPolicy.StoreOnly) =>
        new(new(job), new(attempt), Inspection, Run, revision,
            [new GateReprocessingStreamSelection(Stream, [new(0, ulong.MaxValue)])], plan, 1, policy,
            policy == GateReprocessingPublicationPolicy.ActivateOnComplete ? new DerivedViewId("reprocessed") : null,
            snapshotPageSize: 1);

    private static CompiledGatePlan Compile() => new GatePlanCompiler().Compile(
        GateTestPlan.Plan([GateTestPlan.Gate("A", durationSeconds: 0.000001)])).CompiledPlan!;

    private static async Task CommitAsync(IInspectionRunWriter writer, ulong index)
    {
        var identity = new TraceIdentity(Stream, index);
        var descriptor = new TraceDescriptor(identity, index, TimeSpan.FromTicks((long)index), null, null,
            4, 10_000_000, 0, UtSignalMode.Rf, traceQuality: UtTraceQuality.Good);
        Assert.AreEqual(InspectionAppendStatus.Committed,
            (await writer.CommitAsync(new(new(descriptor, new short[] { 1, 7, 2, 0 })))).Status);
    }

    private sealed class ScriptedSnapshotProvider(InspectionTraceReadResult read) :
        IInspectionSealedRunSnapshotProvider
    {
        public ValueTask<InspectionSealedRunSnapshotOpenResult> OpenSealedRunSnapshotAsync(
            InspectionSealedRunSnapshotRequest request, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(InspectionSealedRunSnapshotOpenResult.Available(new Snapshot(read)));

        private sealed class Snapshot(InspectionTraceReadResult read) : IInspectionSealedRunSnapshot
        {
            public ushort ContractVersion => 2;
            public InspectionId InspectionId => Inspection;
            public AcquisitionRunId RunId => Run;
            public InspectionRevision SourceFinalRevision => new(1);
            public InspectionRunState RunState => InspectionRunState.Sealed;
            public int MaximumPageSize => 256;
            public IReadOnlyList<InspectionSealedStreamSnapshot> Streams { get; } =
                [new(Stream, 0, 0, 1, new string('0', 64))];
            public InspectionSealedRunEnumerationDiagnostics EnumerationDiagnostics =>
                new(1, 1, 1, 1);
            public InspectionSealedRunIdentityPage ReadIdentities(UtStreamIdentity stream,
                InspectionSealedRunCursor? cursor = null) => ReadIdentities(
                    new InspectionSealedRunRangeQuery(stream, [new(0, ulong.MaxValue)], 1), cursor);
            public InspectionSealedRunIdentityPage ReadIdentities(InspectionSealedRunRangeQuery query,
                InspectionSealedRunCursor? cursor = null)
            {
                if (cursor is not null) return new([], null);
                return new([new TraceIdentity(Stream, 0)], null);
            }
            public ValueTask<InspectionTraceReadResult> ReadAsync(TraceIdentity identity,
                CancellationToken cancellationToken = default) => ValueTask.FromResult(read);
            public void Dispose() { }
        }
    }

    private sealed class ScriptedCommitResultStore(GateDerivedMutationStatus status) :
        IGateDerivedStore, IGateProcessingTerminalCoverageSink
    {
        private readonly InMemoryGateDerivedStore _inner = new();
        public DerivedStoreRevision Revision => _inner.Revision;
        public ActiveDerivedViewDescriptor? ActiveView => _inner.ActiveView;
        public GateDerivedStoreDiagnostics Diagnostics => _inner.Diagnostics;
        public ValueTask<GateBatchDeliveryResult> DeliverAsync(GateEvaluationBatch batch,
            CancellationToken exceptionalCancellation = default) =>
            _inner.DeliverAsync(batch, exceptionalCancellation);
        public ValueTask<GateDerivedMutationResult> CommitAsync(GateEvaluationBatch batch,
            CancellationToken cancellationToken = default) => ValueTask.FromResult(
                new GateDerivedMutationResult(status, Revision, $"scripted-{status}", wasAccepted: status != GateDerivedMutationStatus.Closed));
        public GateDerivedMutationResult Activate(GateDerivedCoverageManifest manifest, DerivedViewId viewId) =>
            _inner.Activate(manifest, viewId);
        public ValueTask<GateDerivedMutationResult> ActivateAsync(GateDerivedCoverageManifest manifest,
            DerivedViewId viewId, CancellationToken cancellationToken = default) =>
            _inner.ActivateAsync(manifest, viewId, cancellationToken);
        public GateDerivedMutationResult Invalidate(DerivedViewId viewId) => _inner.Invalidate(viewId);
        public ValueTask<GateDerivedMutationResult> ReclaimInvalidatedAsync(
            CancellationToken cancellationToken = default) => _inner.ReclaimInvalidatedAsync(cancellationToken);
        public IGateDerivedReadLease OpenActiveView() => _inner.OpenActiveView();
        public GateProcessingTerminalCoverageStatus ConfirmTerminalCoverage(
            IReadOnlyList<GateProcessingTerminalCoverage> coverage) => _inner.ConfirmTerminalCoverage(coverage);
        public ValueTask<GateDerivedMutationResult> ConfirmTerminalCoverageAsync(
            IReadOnlyList<GateProcessingTerminalCoverage> coverage,
            CancellationToken cancellationToken = default) => _inner.ConfirmTerminalCoverageAsync(coverage, cancellationToken);
        public ValueTask DisposeAsync() => _inner.DisposeAsync();
    }

    private sealed class MultipleCleanupFailureProvider :
        IInspectionSealedRunSnapshotProvider, IInspectionSealedRunSnapshot
    {
        private readonly MultipleCleanupFailureLease _lease;
        private int _disposeAttempts;
        internal MultipleCleanupFailureProvider()
        {
            var descriptor = new TraceDescriptor(new TraceIdentity(Stream, 0), 0, TimeSpan.Zero, null, null,
                4, 10_000_000, 0, UtSignalMode.Rf, traceQuality: UtTraceQuality.Good);
            _lease = new(descriptor);
        }
        public ValueTask<InspectionSealedRunSnapshotOpenResult> OpenSealedRunSnapshotAsync(
            InspectionSealedRunSnapshotRequest request, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(InspectionSealedRunSnapshotOpenResult.Available(this));
        public ushort ContractVersion => IInspectionSealedRunSnapshot.CurrentContractVersion;
        public InspectionId InspectionId => Inspection;
        public AcquisitionRunId RunId => Run;
        public InspectionRevision SourceFinalRevision => new(1);
        public InspectionRunState RunState => InspectionRunState.Sealed;
        public int MaximumPageSize => 256;
        public IReadOnlyList<InspectionSealedStreamSnapshot> Streams { get; } =
            [new(Stream, 0, 0, 1, new string('0', 64))];
        public InspectionSealedRunEnumerationDiagnostics EnumerationDiagnostics =>
            throw new InvalidOperationException("diagnostics");
        public long CountIdentities(InspectionSealedRunRangeQuery query) => 1;
        public InspectionSealedRunIdentityPage ReadIdentities(UtStreamIdentity stream,
            InspectionSealedRunCursor? cursor = null) => ReadIdentities(
                new InspectionSealedRunRangeQuery(stream, [new(0, 0)], 1), cursor);
        public InspectionSealedRunIdentityPage ReadIdentities(InspectionSealedRunRangeQuery query,
            InspectionSealedRunCursor? cursor = null) => cursor is null
                ? new([new TraceIdentity(Stream, 0)], null) : new([], null);
        public ValueTask<InspectionTraceReadResult> ReadAsync(TraceIdentity identity,
            CancellationToken cancellationToken = default) => ValueTask.FromResult(
                InspectionTraceReadResult.Available(new(1), _lease));
        public void Dispose()
        {
            if (Interlocked.Increment(ref _disposeAttempts) == 1)
                throw new InvalidOperationException("snapshot");
        }
        internal void Recover() { _lease.Recover(); Dispose(); }

        private sealed class MultipleCleanupFailureLease(TraceDescriptor descriptor) : IInspectionTraceLease
        {
            private int _disposeAttempts;
            public TraceDescriptor Descriptor => descriptor;
            public IReadOnlyUtSamples Samples { get; } = new ThrowingSamples();
            public void Dispose()
            {
                if (Interlocked.Increment(ref _disposeAttempts) == 1)
                    throw new InvalidOperationException("lease");
            }
            internal void Recover() => Dispose();
            private sealed class ThrowingSamples : IReadOnlyUtSamples
            {
                public int Count => 4;
                public short this[int index] => 0;
                public void CopyTo(Span<short> destination) => throw new FormatException("samples");
            }
        }
    }

    private sealed class SynchronousGate
    {
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Entered => _entered;
        internal void Block() { _entered.TrySetResult(); _release.Task.GetAwaiter().GetResult(); }
        internal void Release() => _release.TrySetResult();
    }

    private sealed class ScriptedLease(TraceDescriptor descriptor) : IInspectionTraceLease
    {
        public TraceDescriptor Descriptor { get; } = descriptor;
        public IReadOnlyUtSamples Samples { get; } = new ScriptedSamples();
        public int DisposeCount { get; private set; }
        public void Dispose() => DisposeCount++;
        private sealed class ScriptedSamples : IReadOnlyUtSamples
        {
            public int Count => 4;
            public short this[int index] => new short[] { 1, 7, 2, 0 }[index];
            public void CopyTo(Span<short> destination) => new short[] { 1, 7, 2, 0 }.CopyTo(destination);
        }
    }

    private sealed class ThrowingAdmissionProvider : IInspectionSealedRunSnapshotProvider
    {
        public ValueTask<InspectionSealedRunSnapshotOpenResult> OpenSealedRunSnapshotAsync(
            InspectionSealedRunSnapshotRequest request, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(InspectionSealedRunSnapshotOpenResult.Available(new Snapshot()));

        private sealed class Snapshot : IInspectionSealedRunSnapshot
        {
            public ushort ContractVersion => 2;
            public InspectionId InspectionId => Inspection;
            public AcquisitionRunId RunId => Run;
            public InspectionRevision SourceFinalRevision => new(1);
            public InspectionRunState RunState => InspectionRunState.Sealed;
            public int MaximumPageSize => 256;
            public IReadOnlyList<InspectionSealedStreamSnapshot> Streams { get; } =
                [new(Stream, 0, 0, 1, new string('0', 64))];
            public InspectionSealedRunEnumerationDiagnostics EnumerationDiagnostics => default;
            public InspectionSealedRunIdentityPage ReadIdentities(UtStreamIdentity stream,
                InspectionSealedRunCursor? cursor = null) => throw new InvalidDataException("count");
            public InspectionSealedRunIdentityPage ReadIdentities(InspectionSealedRunRangeQuery query,
                InspectionSealedRunCursor? cursor = null) => throw new InvalidDataException("count");
            public ValueTask<InspectionTraceReadResult> ReadAsync(TraceIdentity identity,
                CancellationToken cancellationToken = default) => throw new InvalidOperationException();
            public void Dispose() => throw new InvalidOperationException("cleanup");
        }
    }

    private sealed class GatedThrowingCleanupProvider : IInspectionSealedRunSnapshotProvider
    {
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async ValueTask<InspectionSealedRunSnapshotOpenResult> OpenSealedRunSnapshotAsync(
            InspectionSealedRunSnapshotRequest request, CancellationToken cancellationToken = default)
        {
            Entered.TrySetResult();
            await Release.Task;
            return InspectionSealedRunSnapshotOpenResult.Available(new Snapshot());
        }

        private sealed class Snapshot : IInspectionSealedRunSnapshot
        {
            public ushort ContractVersion => 2;
            public InspectionId InspectionId => Inspection;
            public AcquisitionRunId RunId => Run;
            public InspectionRevision SourceFinalRevision => new(1);
            public InspectionRunState RunState => InspectionRunState.Sealed;
            public int MaximumPageSize => 256;
            public IReadOnlyList<InspectionSealedStreamSnapshot> Streams { get; } =
                [new(Stream, 0, 0, 1, new string('0', 64))];
            public InspectionSealedRunEnumerationDiagnostics EnumerationDiagnostics => default;
            public InspectionSealedRunIdentityPage ReadIdentities(UtStreamIdentity stream,
                InspectionSealedRunCursor? cursor = null) => new([new TraceIdentity(Stream, 0)], null);
            public InspectionSealedRunIdentityPage ReadIdentities(InspectionSealedRunRangeQuery query,
                InspectionSealedRunCursor? cursor = null) => new([new TraceIdentity(Stream, 0)], null);
            public ValueTask<InspectionTraceReadResult> ReadAsync(TraceIdentity identity,
                CancellationToken cancellationToken = default) => throw new InvalidOperationException();
            public void Dispose() => throw new InvalidOperationException("cleanup");
        }
    }

    private sealed class HeaderMismatchProvider : IInspectionSealedRunSnapshotProvider, IInspectionSealedRunSnapshot
    {
        public int DisposeCount { get; private set; }
        public int QueryCount { get; private set; }
        public ValueTask<InspectionSealedRunSnapshotOpenResult> OpenSealedRunSnapshotAsync(
            InspectionSealedRunSnapshotRequest request, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(InspectionSealedRunSnapshotOpenResult.Available(this));
        public ushort ContractVersion => IInspectionSealedRunSnapshot.CurrentContractVersion;
        public InspectionId InspectionId => Inspection;
        public AcquisitionRunId RunId => new(Guid.Parse("99999999-9999-9999-9999-999999999999"));
        public InspectionRevision SourceFinalRevision => new(1);
        public InspectionRunState RunState => InspectionRunState.Sealed;
        public int MaximumPageSize => 256;
        public IReadOnlyList<InspectionSealedStreamSnapshot> Streams { get; } =
            [new(Stream, 0, 0, 1, new string('0', 64))];
        public InspectionSealedRunEnumerationDiagnostics EnumerationDiagnostics => default;
        public long CountIdentities(InspectionSealedRunRangeQuery query) { QueryCount++; return 0; }
        public InspectionSealedRunIdentityPage ReadIdentities(UtStreamIdentity stream,
            InspectionSealedRunCursor? cursor = null) => throw new InvalidOperationException();
        public InspectionSealedRunIdentityPage ReadIdentities(InspectionSealedRunRangeQuery query,
            InspectionSealedRunCursor? cursor = null) => throw new InvalidOperationException();
        public ValueTask<InspectionTraceReadResult> ReadAsync(TraceIdentity identity,
            CancellationToken cancellationToken = default) => throw new InvalidOperationException();
        public void Dispose() => DisposeCount++;
    }

    private sealed class ThrowingDescriptorLease : IInspectionTraceLease
    {
        public TraceDescriptor Descriptor => throw new InvalidOperationException("injected-descriptor-failure");
        public IReadOnlyUtSamples Samples => throw new InvalidOperationException("samples-must-not-be-read");
        public int DisposeCount { get; private set; }
        public void Dispose() => DisposeCount++;
    }

    private sealed class ThrowingDescriptorAndCleanupLease : IInspectionTraceLease
    {
        private int _disposeAttempts;
        public TraceDescriptor Descriptor => throw new InvalidOperationException("descriptor");
        public IReadOnlyUtSamples Samples => throw new InvalidOperationException("samples");
        public void Dispose()
        {
            if (Interlocked.Increment(ref _disposeAttempts) == 1)
                throw new InvalidOperationException("lease-cleanup");
        }
        internal void RecoverDispose() => Dispose();
    }

    private sealed class ThrowingSamplesLease(TraceDescriptor descriptor) : IInspectionTraceLease
    {
        public TraceDescriptor Descriptor => descriptor;
        public IReadOnlyUtSamples Samples { get; } = new ThrowingSamples();
        public void Dispose() { }
        private sealed class ThrowingSamples : IReadOnlyUtSamples
        {
            public int Count => 4;
            public short this[int index] => 0;
            public void CopyTo(Span<short> destination) => throw new FormatException("samples");
        }
    }
}
