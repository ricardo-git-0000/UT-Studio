using UTStudio.Contracts.Inspection;
using UTStudio.Contracts.Processing;
using UTStudio.Domain.Acquisition;
using UTStudio.Domain.Inspection;
using UTStudio.Inspection.Core;
using UTStudio.Domain.Processing;
using UTStudio.Processing.Core;

namespace UTStudio.Core.Tests.Processing;

[TestClass]
public sealed class InMemoryGateDerivedStoreTests
{
    [TestMethod]
    public async Task FirstBatchDuplicateConflictAndRevisionAreDeterministic()
    {
        await using var store = new InMemoryGateDerivedStore();
        GateEvaluationBatch first = Batch([1, 4, 2]);
        GateDerivedMutationResult committed = await store.CommitAsync(first);
        GateDerivedMutationResult duplicate = await store.CommitAsync(first);
        GateDerivedMutationResult conflict = await store.CommitAsync(Batch([9, 4, 2]));

        Assert.AreEqual(GateDerivedMutationStatus.Committed, committed.Status);
        Assert.AreEqual(new DerivedStoreRevision(1), committed.Revision);
        Assert.AreEqual(GateDerivedMutationStatus.Duplicate, duplicate.Status);
        Assert.AreEqual(committed.Revision, duplicate.Revision);
        Assert.AreEqual(GateDerivedMutationStatus.Conflict, conflict.Status);
        Assert.AreEqual(committed.Revision, conflict.Revision);
        Assert.AreEqual(1, store.Diagnostics.BatchCount);
    }

    [TestMethod]
    public async Task SameKeyWithDifferentConfirmedDescriptorIsConflict()
    {
        await using var store = new InMemoryGateDerivedStore(); GateEvaluationBatch original = Batch([1, 4, 2]);
        Assert.AreEqual(GateDerivedMutationStatus.Committed, (await store.CommitAsync(original)).Status);
        TraceDescriptor source = original.Trace.Descriptor;
        var changedDescriptor = new TraceDescriptor(source.Identity, source.Sequence + 1,
            source.ElapsedSinceRunStart, source.TimestampUtc, source.SpatialCoordinate, source.SampleCount,
            source.SampleRateHz, source.FirstSampleOffsetSeconds, source.SignalMode, source.ClockQuality,
            source.SampleEncoding, source.Provenance, source.TraceQuality);
        var changedTrace = new ConfirmedGateTraceDescriptor(changedDescriptor, original.Trace.ConfirmedIdentity,
            original.Trace.SourceInspectionRevision, original.Trace.EffectiveQuality);
        CompiledGatePlan plan = GateEvaluationTestData.Compile(GateEvaluationTestData.Gate("A", 3));
        GateEvaluationBatch changed = new GateEvaluator(new GateScratchPool()).Evaluate(plan, changedTrace, [1, 4, 2]).Batch!;
        Assert.AreEqual(GateDerivedMutationStatus.Conflict, (await store.CommitAsync(changed)).Status);
        Assert.AreEqual(new DerivedStoreRevision(1), store.Revision);
    }

    [TestMethod]
    public void FormulaV2CountsMeasuresAndIdentifiersAreUtf8Bounded()
    {
        GateEvaluationBatch normal = Batch([1, 4, 2]);
        long normalSize = GateDerivedLogicalSize.Batch(normal);
        CompiledGatePlan fewer = GateEvaluationTestData.Compile(GateEvaluationTestData.Gate("A", 3,
            measures: [GateMeasureKind.Status]));
        ConfirmedGateTraceDescriptor trace = GateEvaluationTestData.Trace(3);
        GateEvaluationBatch fewerBatch = new GateEvaluator(new GateScratchPool()).Evaluate(fewer, trace, [1, 4, 2]).Batch!;
        Assert.AreEqual(4L * (normal.Entries[0].RequestedMeasures.Count - 1),
            normalSize - GateDerivedLogicalSize.Batch(fewerBatch));
        Assert.IsTrue(new GateId(new string('a', GateId.MaximumUtf8Bytes)).IsValid);
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new GateId(new string('á', GateId.MaximumUtf8Bytes)));
    }

    [TestMethod]
    public async Task SparseManifestActivatesAndLeaseNeverBecomesHybrid()
    {
        await using var store = new InMemoryGateDerivedStore();
        GateEvaluationBatch batch = Batch([1, 4, 2]);
        await store.CommitAsync(batch);
        GateDerivedCoverageManifest manifest = Manifest(batch, store.Revision);
        Assert.AreEqual(GateDerivedMutationStatus.Activated,
            Activate(store, manifest, "view-1").Status);
        using IGateDerivedReadLease old = store.OpenActiveView();
        Assert.AreEqual(GateDerivedMutationStatus.Activated,
            Activate(store, manifest, "view-2").Status);
        Assert.AreEqual(new DerivedViewId("view-1"), old.View.ViewId);
        Assert.AreEqual(new DerivedViewId("view-2"), store.ActiveView!.ViewId);

        GateEvaluationEntry entry = batch.Entries[0];
        var query = new GateDerivedRangeQuery(old.View.ViewId, batch.Trace.ConfirmedIdentity.Stream, 0, 10,
            entry.GateId, [GateMeasureKind.Amplitude], 1, GateDerivedNoDataMode.Omit);
        GateDerivedRangePage page = old.ReadRange(query);
        Assert.HasCount(1, page.Items);
        Assert.HasCount(1, page.Items[0].Measures);
        Assert.AreEqual(GateMeasureKind.Amplitude, page.Items[0].Measures[0].Key.Measure);
    }

    [TestMethod]
    public async Task ActivatedMembershipExcludesLaterCompatibleBatchAndNewViewCanIncludeIt()
    {
        await using var store = new InMemoryGateDerivedStore();
        GateEvaluationBatch first = BatchAt(7, [1, 4, 2]);
        await store.CommitAsync(first);
        GateDerivedCoverageManifest firstManifest = Manifest(first, store.Revision);
        Activate(store, firstManifest, "old");
        using IGateDerivedReadLease old = store.OpenActiveView();

        GateEvaluationBatch later = BatchAt(9, [2, 5, 1]);
        await store.CommitAsync(later);
        GateDerivedBatchKey laterKey = Key(later);
        Assert.IsNull(old.ReadExact(laterKey));
        using (IGateDerivedReadLease reopenedOld = Open(store, new DerivedViewId("old")))
        { Assert.IsNull(reopenedOld.ReadExact(laterKey)); }

        Assert.IsNull(old.ReadExact(laterKey));

        await using var expandedStore = new InMemoryGateDerivedStore();
        await expandedStore.CommitAsync(first); await expandedStore.CommitAsync(later);
        GateDerivedCoverageManifest expanded = Manifest([first, later], expandedStore.Revision);
        Assert.AreEqual(GateDerivedMutationStatus.Activated, Activate(expandedStore, expanded, "new").Status);
        using IGateDerivedReadLease current = expandedStore.OpenActiveView();
        Assert.IsNotNull(current.ReadExact(laterKey));
    }

    [TestMethod]
    public void CoverageDigestOwnsNoMutableBackingArray()
    {
        byte[] source = Enumerable.Range(0, 32).Select(x => (byte)x).ToArray();
        var digest = new GateSha256Digest(source);
        string expected = digest.ToString();
        Array.Fill(source, (byte)0);
        Span<byte> copy = stackalloc byte[32]; digest.CopyTo(copy);
        Assert.AreEqual(expected, Convert.ToHexString(copy));
        Assert.AreEqual(digest, new GateSha256Digest(copy));
    }

    [TestMethod]
    public async Task StagedViewMutationsFaultWithoutPublishingOrAdvancingRevision()
    {
        await using var store = new InMemoryGateDerivedStore();
        GateEvaluationBatch batch = Batch([1, 4, 2]); await store.CommitAsync(batch);
        GateDerivedCoverageManifest manifest = Manifest(batch, store.Revision); ConfirmTerminal(store, manifest);
        DerivedStoreRevision before = store.Revision;
        store.TestHooks = new() { AfterActivationStaging = () => throw new InvalidOperationException("activation") };
        Assert.AreEqual(GateDerivedMutationStatus.Faulted, store.Activate(manifest, new("view")).Status);
        Assert.AreEqual(before, store.Revision); Assert.IsNull(store.ActiveView);

        store.TestHooks = null; Assert.AreEqual(GateDerivedMutationStatus.Activated,
            store.Activate(manifest, new("view")).Status);
        before = store.Revision;
        store.TestHooks = new() { AfterInvalidationStaging = () => throw new InvalidOperationException("invalidate") };
        Assert.AreEqual(GateDerivedMutationStatus.Faulted, store.Invalidate(new("view")).Status);
        Assert.AreEqual(before, store.Revision); Assert.IsNotNull(store.ActiveView);

        store.TestHooks = null; store.Invalidate(new("view")); before = store.Revision;
        store.TestHooks = new() { AfterReclaimStaging = () => throw new InvalidOperationException("reclaim") };
        Assert.AreEqual(GateDerivedMutationStatus.Faulted, (await store.ReclaimInvalidatedAsync()).Status);
        Assert.AreEqual(before, store.Revision); Assert.AreEqual(1, store.Diagnostics.BatchCount);
    }

    [TestMethod]
    public async Task CapturedRevisionCannotUseBatchCommittedAfterCapture()
    {
        await using var store = new InMemoryGateDerivedStore();
        GateEvaluationBatch first = BatchAt(7, [1, 4, 2]); await store.CommitAsync(first);
        DerivedStoreRevision captured = store.Revision;
        GateEvaluationBatch later = BatchAt(9, [2, 5, 1]); await store.CommitAsync(later);
        GateDerivedCoverageManifest manifest = Manifest([first, later], captured); ConfirmTerminal(store, manifest);
        DerivedStoreRevision before = store.Revision;
        Assert.AreEqual(GateDerivedMutationStatus.CoverageMismatch,
            store.Activate(manifest, new("late")).Status);
        Assert.AreEqual(before, store.Revision); Assert.IsNull(store.ActiveView);
    }

    [TestMethod]
    public async Task IncorrectDigestAndDifferentIdentitySetCannotActivate()
    {
        await using var store = new InMemoryGateDerivedStore();
        GateEvaluationBatch batch = Batch([1, 4, 2]); await store.CommitAsync(batch);
        GateDerivedCoverageManifest valid = Manifest(batch, store.Revision);
        GateDerivedStreamCoverage item = valid.Streams[0];
        var wrong = new GateDerivedStreamCoverage(item.Stream, item.MinimumAcquisitionIndex,
            item.MaximumAcquisitionIndex, item.EligibleTraceCount, new byte[32]);
        var manifest = new GateDerivedCoverageManifest(valid.InspectionId, valid.RunId, [wrong],
            valid.SourceFinalRevision, valid.PlanId, valid.PlanRevision, valid.PlanSignature,
            valid.SchemaVersion, valid.AlgorithmVersion, 1, valid.CapturedStoreRevision);
        Assert.AreEqual(GateDerivedMutationStatus.CoverageMismatch,
            Activate(store, manifest, "wrong").Status);
        Assert.IsNull(store.ActiveView);
        Assert.AreEqual(new DerivedStoreRevision(1), store.Revision);
    }

    [TestMethod]
    public async Task InvalidationAndReclaimRespectActiveLease()
    {
        await using var store = new InMemoryGateDerivedStore();
        GateEvaluationBatch batch = Batch([1, 4, 2]); await store.CommitAsync(batch);
        Activate(store, Manifest(batch, store.Revision), "view");
        IGateDerivedReadLease lease = store.OpenActiveView();
        Assert.AreEqual(GateDerivedMutationStatus.Invalidated, store.Invalidate(new DerivedViewId("view")).Status);
        Assert.IsNull(store.ActiveView);
        Assert.AreEqual(GateDerivedMutationStatus.Busy, (await store.ReclaimInvalidatedAsync()).Status);
        lease.Dispose(); lease.Dispose();
        Assert.AreEqual(GateDerivedMutationStatus.Reclaimed, (await store.ReclaimInvalidatedAsync()).Status);
        Assert.AreEqual(0, store.Diagnostics.BatchCount);
        Assert.AreEqual(0, store.Diagnostics.ActiveLeases);
    }

    [TestMethod]
    public async Task ReclaimReleasesMembershipAndRetainsOnlyViewTombstone()
    {
        await using var store = new InMemoryGateDerivedStore(); GateEvaluationBatch batch = Batch([1, 4, 2]);
        await store.CommitAsync(batch); long batchBytes = GateDerivedLogicalSize.Batch(batch);
        GateDerivedCoverageManifest manifest = Manifest(batch, store.Revision); ConfirmTerminal(store, manifest);
        long terminalBytes = store.Diagnostics.LogicalPayloadBytes - batchBytes;
        Activate(store, manifest, "view"); store.Invalidate(new("view"));
        Assert.AreEqual(GateDerivedMutationStatus.Reclaimed, (await store.ReclaimInvalidatedAsync()).Status);
        long tombstone = 64 + System.Text.Encoding.UTF8.GetByteCount("view");
        Assert.AreEqual(terminalBytes + tombstone, store.Diagnostics.LogicalPayloadBytes);
        Assert.AreEqual(0, store.Diagnostics.MembershipCount);
        Assert.AreEqual(1, store.Diagnostics.TombstoneCount);
    }

    [TestMethod]
    public async Task LeaseOnDisjointViewDoesNotBlockSelectiveReclaim()
    {
        await using var store = new InMemoryGateDerivedStore();
        GateEvaluationBatch first = BatchAtPlan(7, [1, 4, 2], "first-plan");
        await store.CommitAsync(first); GateDerivedCoverageManifest firstManifest = Manifest(first, store.Revision);
        Activate(store, firstManifest, "first");
        GateEvaluationBatch second = BatchAtPlan(9, [2, 5, 1], "second-plan");
        await store.CommitAsync(second); GateDerivedCoverageManifest secondManifest = Manifest(second, store.Revision);
        Activate(store, secondManifest, "second");
        using IGateDerivedReadLease unrelated = store.OpenActiveView();
        store.Invalidate(new("first"));
        Assert.AreEqual(GateDerivedMutationStatus.Reclaimed, (await store.ReclaimInvalidatedAsync()).Status);
        Assert.IsNotNull(unrelated.ReadExact(Key(second)));
        Assert.AreEqual(1, store.Diagnostics.BatchCount);
    }

    [TestMethod]
    public async Task ExactReadAsyncDistinguishesAvailableMissingCancelledAndClosed()
    {
        await using var store = new InMemoryGateDerivedStore(); GateEvaluationBatch batch = Batch([1, 4, 2]);
        await store.CommitAsync(batch); Activate(store, Manifest(batch, store.Revision), "view");
        IGateDerivedReadLease lease = store.OpenActiveView();
        Assert.AreEqual(GateDerivedExactReadStatus.Available, (await lease.ReadExactAsync(Key(batch))).Status);
        Assert.AreEqual(GateDerivedExactReadStatus.NotAvailable,
            (await lease.ReadExactAsync(Key(BatchAt(99, [1, 4, 2])))).Status);
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        Assert.AreEqual(GateDerivedExactReadStatus.Cancelled,
            (await lease.ReadExactAsync(Key(batch), cancelled.Token)).Status);
        lease.Dispose();
        Assert.AreEqual(GateDerivedExactReadStatus.Closed, (await lease.ReadExactAsync(Key(batch))).Status);
        Assert.ThrowsExactly<ArgumentException>(() => lease.ReadExact(default));
    }

    [TestMethod]
    public async Task ExactByteAndBatchLimitsRejectOneOverWithoutMutation()
    {
        GateEvaluationBatch batch = Batch([1, 4, 2]); long size = GateDerivedLogicalSize.Batch(batch);
        await using var exact = new InMemoryGateDerivedStore(new(1, size));
        Assert.AreEqual(GateDerivedMutationStatus.Committed, (await exact.CommitAsync(batch)).Status);
        Assert.AreEqual(GateDerivedMutationStatus.Conflict, (await exact.CommitAsync(Batch([9, 4, 2]))).Status);
        await using var shortByOne = new InMemoryGateDerivedStore(new(1, size - 1));
        Assert.AreEqual(GateDerivedMutationStatus.CapacityExceeded, (await shortByOne.CommitAsync(batch)).Status);
        Assert.AreEqual(DerivedStoreRevision.Initial, shortByOne.Revision);
    }

    [TestMethod]
    public async Task EmptyRunManifestCanActivateWithoutInventedIdentities()
    {
        await using var store = new InMemoryGateDerivedStore();
        ConfirmedGateTraceDescriptor trace = GateEvaluationTestData.Trace(3);
        CompiledGatePlan plan = GateEvaluationTestData.Compile(GateEvaluationTestData.Gate("A", 3));
        var coverage = new GateDerivedStreamCoverage(trace.ConfirmedIdentity.Stream, null, null, 0,
            GateDerivedStreamCoverage.ComputeIdentityDigest([]));
        var manifest = new GateDerivedCoverageManifest(trace.ConfirmedIdentity.Stream.InspectionId,
            trace.ConfirmedIdentity.Stream.AcquisitionRunId, [coverage], new InspectionRevision(5),
            plan.Source.Id, plan.Source.Revision, plan.Signature.Hex, plan.Source.SchemaVersion,
            plan.Source.AlgorithmVersion, 0, DerivedStoreRevision.Initial);
        Assert.AreEqual(GateDerivedMutationStatus.Activated,
            Activate(store, manifest, "empty").Status);
    }

    [TestMethod]
    public async Task InvalidTraceIsAnExplicitTerminalBatch()
    {
        await using var store = new InMemoryGateDerivedStore();
        GateEvaluationBatch batch = BatchAt(21, [1, 2, 3], UtTraceQuality.Invalid);
        Assert.AreEqual(GateDerivedMutationStatus.Committed, (await store.CommitAsync(batch)).Status);
        Assert.IsTrue(batch.Entries.All(x => x.Status == GateResultStatus.InvalidTrace));
        Assert.AreEqual(GateDerivedMutationStatus.Activated,
            Activate(store, Manifest([batch], store.Revision), "invalid").Status);
    }

    [TestMethod]
    public async Task CursorCannotCrossViewAndDiesWithLease()
    {
        await using var store = new InMemoryGateDerivedStore();
        GateEvaluationBatch first = BatchAt(7, [1, 4, 2]); GateEvaluationBatch second = BatchAt(9, [2, 5, 1]);
        await store.CommitAsync(first); await store.CommitAsync(second);
        GateDerivedCoverageManifest manifest = Manifest([first, second], store.Revision);
        Activate(store, manifest, "one");
        IGateDerivedReadLease lease = store.OpenActiveView(); GateEvaluationEntry entry = first.Entries[0];
        var query = new GateDerivedRangeQuery(new DerivedViewId("one"), first.Trace.ConfirmedIdentity.Stream,
            0, 20, entry.GateId, [GateMeasureKind.Amplitude], 1, GateDerivedNoDataMode.Omit);
        GateDerivedRangePage page = lease.ReadRange(query); Assert.IsNotNull(page.NextCursor);
        Assert.ThrowsExactly<ArgumentException>(() => lease.ReadRange(new(new DerivedViewId("other"),
            query.Stream, 0, 20, query.GateId, query.Measures, 1, query.NoDataMode, page.NextCursor)));
        lease.Dispose();
        Assert.ThrowsExactly<ObjectDisposedException>(() => lease.ReadRange(new(query.ViewId, query.Stream,
            0, 20, query.GateId, query.Measures, 1, query.NoDataMode, page.NextCursor)));
    }

    [TestMethod]
    public async Task BatchCountLimitRejectsOneOverWithDifferentIdentity()
    {
        await using var store = new InMemoryGateDerivedStore(new(1, 4096));
        Assert.AreEqual(GateDerivedMutationStatus.Committed,
            (await store.CommitAsync(BatchAt(1, [1, 4, 2]))).Status);
        Assert.AreEqual(GateDerivedMutationStatus.CapacityExceeded,
            (await store.CommitAsync(BatchAt(2, [1, 4, 2]))).Status);
        Assert.AreEqual(1, store.Diagnostics.BatchCount);
        Assert.AreEqual(new DerivedStoreRevision(1), store.Revision);
    }

    [TestMethod]
    public async Task SourceFinalRevisionMustMatchTerminalEvidenceExactly()
    {
        await using var store = new InMemoryGateDerivedStore(); GateEvaluationBatch batch = Batch([1, 4, 2]);
        await store.CommitAsync(batch); GateDerivedCoverageManifest valid = Manifest(batch, store.Revision);
        ConfirmTerminal(store, valid);
        var wrong = new GateDerivedCoverageManifest(valid.InspectionId, valid.RunId, valid.Streams,
            new InspectionRevision(valid.SourceFinalRevision.Value + 1), valid.PlanId, valid.PlanRevision,
            valid.PlanSignature, valid.SchemaVersion, valid.AlgorithmVersion,
            valid.TotalEligibleTraceCount, valid.CapturedStoreRevision);
        Assert.AreEqual(GateDerivedMutationStatus.CoverageMismatch,
            store.Activate(wrong, new DerivedViewId("wrong-final")).Status);
    }

    [TestMethod]
    public async Task TerminalCoverageForSeveralPlanEpochsIsPublishedAtomically()
    {
        GateEvaluationBatch batch = Batch([1, 4, 2]);
        GateDerivedCoverageManifest manifest = Manifest(batch, new DerivedStoreRevision(1));
        var first = new GateProcessingTerminalCoverage(manifest.InspectionId, manifest.RunId,
            manifest.SourceFinalRevision, manifest.PlanId, manifest.PlanRevision, manifest.PlanSignature,
            manifest.SchemaVersion, manifest.AlgorithmVersion, manifest.Streams);
        var second = new GateProcessingTerminalCoverage(manifest.InspectionId, manifest.RunId,
            manifest.SourceFinalRevision, new GatePlanId("second-plan"), manifest.PlanRevision,
            manifest.PlanSignature, manifest.SchemaVersion, manifest.AlgorithmVersion, manifest.Streams);
        long budget = GateDerivedLogicalSize.TerminalCoverage(first) +
            GateDerivedLogicalSize.TerminalCoverage(second) - 1;
        await using var store = new InMemoryGateDerivedStore(new(1, budget));

        Assert.AreEqual(GateProcessingTerminalCoverageStatus.CapacityExceeded,
            store.ConfirmTerminalCoverage([first, second]));
        Assert.AreEqual(0L, store.Diagnostics.LogicalPayloadBytes);
        Assert.AreEqual(GateProcessingTerminalCoverageStatus.Accepted,
            store.ConfirmTerminalCoverage([first]));
    }

    [TestMethod]
    public async Task DuplicateTerminalKeyInsideOnePublicationCannotBecomePartiallyVisible()
    {
        GateEvaluationBatch batch = Batch([1, 4, 2]);
        GateDerivedCoverageManifest manifest = Manifest(batch, new DerivedStoreRevision(1));
        var coverage = new GateProcessingTerminalCoverage(manifest.InspectionId, manifest.RunId,
            manifest.SourceFinalRevision, manifest.PlanId, manifest.PlanRevision, manifest.PlanSignature,
            manifest.SchemaVersion, manifest.AlgorithmVersion, manifest.Streams);
        long size = GateDerivedLogicalSize.TerminalCoverage(coverage);
        await using var store = new InMemoryGateDerivedStore(new(1, size * 2));

        Assert.AreEqual(GateProcessingTerminalCoverageStatus.Conflict,
            store.ConfirmTerminalCoverage([coverage, coverage]));
        Assert.AreEqual(0L, store.Diagnostics.LogicalPayloadBytes);
        Assert.AreEqual(GateProcessingTerminalCoverageStatus.Accepted,
            store.ConfirmTerminalCoverage([coverage]));
        Assert.AreEqual(size, store.Diagnostics.LogicalPayloadBytes);
    }

    [TestMethod]
    public async Task TerminalCoverageFaultAfterStagingPublishesNothing()
    {
        GateEvaluationBatch batch = Batch([1, 4, 2]); GateDerivedCoverageManifest manifest = Manifest(batch, new(1));
        var coverage = new GateProcessingTerminalCoverage(manifest.InspectionId, manifest.RunId,
            manifest.SourceFinalRevision, manifest.PlanId, manifest.PlanRevision, manifest.PlanSignature,
            manifest.SchemaVersion, manifest.AlgorithmVersion, manifest.Streams);
        await using var store = new InMemoryGateDerivedStore
        { TestHooks = new() { AfterTerminalCoverageStaging = () => throw new InvalidOperationException("terminal") } };
        Assert.AreEqual(GateProcessingTerminalCoverageStatus.Faulted, store.ConfirmTerminalCoverage([coverage]));
        Assert.AreEqual(0L, store.Diagnostics.LogicalPayloadBytes);
        store.TestHooks = null;
        Assert.AreEqual(GateProcessingTerminalCoverageStatus.Accepted, store.ConfirmTerminalCoverage([coverage]));
    }

    [TestMethod]
    public async Task TwoBatchesForSameIdentityCannotSatisfySingleIdentityCoverage()
    {
        await using var store = new InMemoryGateDerivedStore();
        GateEvaluationBatch first = BatchAt(7, [1, 4, 2], sourceRevision: 5);
        GateEvaluationBatch second = BatchAt(7, [1, 4, 2], sourceRevision: 6);
        await store.CommitAsync(first); await store.CommitAsync(second);
        GateDerivedCoverageManifest basis = Manifest(first, store.Revision);
        var manifest = new GateDerivedCoverageManifest(basis.InspectionId, basis.RunId, basis.Streams,
            new InspectionRevision(6), basis.PlanId, basis.PlanRevision, basis.PlanSignature,
            basis.SchemaVersion, basis.AlgorithmVersion, basis.TotalEligibleTraceCount, basis.CapturedStoreRevision);
        ConfirmTerminal(store, manifest);
        Assert.AreEqual(GateDerivedMutationStatus.CoverageMismatch,
            store.Activate(manifest, new DerivedViewId("duplicate-identity")).Status);
    }

    [TestMethod]
    public async Task ViewIdentifierIsAppendOnlyAndCannotBeReplaced()
    {
        await using var store = new InMemoryGateDerivedStore(); GateEvaluationBatch batch = Batch([1, 4, 2]);
        await store.CommitAsync(batch); GateDerivedCoverageManifest manifest = Manifest(batch, store.Revision);
        Assert.AreEqual(GateDerivedMutationStatus.Activated, Activate(store, manifest, "fixed").Status);
        Assert.AreEqual(GateDerivedMutationStatus.Duplicate, store.Activate(manifest, new DerivedViewId("fixed")).Status);
        Assert.AreEqual(1L, store.Diagnostics.Activations);
    }

    [TestMethod]
    public async Task ConcurrentWritersPublishWholeBatchesAndBalanceAdmissions()
    {
        await using var store = new InMemoryGateDerivedStore(new(32, 64 * 1024));
        GateEvaluationBatch[] batches = Enumerable.Range(0, 32)
            .Select(x => BatchAt((ulong)x, [1, (short)(x + 2), 0])).ToArray();
        GateDerivedMutationResult[] results = await Task.WhenAll(batches.Select(x => store.CommitAsync(x).AsTask()));
        Assert.IsTrue(results.All(x => x.Status == GateDerivedMutationStatus.Committed));
        Assert.AreEqual(32, store.Diagnostics.BatchCount);
        Assert.AreEqual(32UL, store.Revision.Value);
        Assert.AreEqual(0, store.Diagnostics.AdmittedWriters);
        Assert.AreEqual(0, store.Diagnostics.ReservedBatchCount);
        Assert.AreEqual(0L, store.Diagnostics.ReservedLogicalPayloadBytes);
    }

    [TestMethod]
    [DataRow("reservation")]
    [DataRow("copy")]
    [DataRow("publish")]
    public async Task CommitFaultAtEachBoundaryRollsBackReservations(string boundary)
    {
        await using var store = new InMemoryGateDerivedStore();
        Action fail = () => throw new InvalidOperationException("injected");
        store.TestHooks = new GateDerivedStoreTestHooks
        {
            AfterReservation = boundary == "reservation" ? fail : null,
            AfterCopy = boundary == "copy" ? fail : null,
            BeforePublish = boundary == "publish" ? fail : null
        };
        GateDerivedMutationResult result = await store.CommitAsync(Batch([1, 4, 2]));
        Assert.AreEqual(GateDerivedMutationStatus.Faulted, result.Status);
        Assert.AreEqual(0, store.Diagnostics.BatchCount);
        Assert.AreEqual(0, store.Diagnostics.ReservedBatchCount);
        Assert.AreEqual(0L, store.Diagnostics.ReservedLogicalPayloadBytes);
        Assert.AreEqual(DerivedStoreRevision.Initial, store.Revision);
    }

    [TestMethod]
    public async Task DisposeWaitsForWriterAdmittedBeforeClose()
    {
        var admitted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var store = new InMemoryGateDerivedStore
        {
            TestHooks = new GateDerivedStoreTestHooks
            {
                AfterReservation = () => { admitted.TrySetResult(); release.Task.GetAwaiter().GetResult(); }
            }
        };
        Task<GateDerivedMutationResult> commit = Task.Run(async () => await store.CommitAsync(Batch([1, 4, 2])));
        await admitted.Task;
        Task close = store.DisposeAsync().AsTask(); Assert.IsFalse(close.IsCompleted);
        release.TrySetResult(); Assert.AreEqual(GateDerivedMutationStatus.Committed, (await commit).Status);
        await close; Assert.IsTrue(store.Diagnostics.Closed);
        Assert.AreEqual(0, store.Diagnostics.AdmittedWriters);
    }

    [TestMethod]
    public async Task DisposeWaitsForLeaseAndConcurrentCallsShareCompletion()
    {
        var store = new InMemoryGateDerivedStore(); GateEvaluationBatch batch = Batch([1, 4, 2]);
        await store.CommitAsync(batch); Activate(store, Manifest(batch, store.Revision), "view");
        IGateDerivedReadLease lease = store.OpenActiveView();
        Task first = store.DisposeAsync().AsTask(); Task second = store.DisposeAsync().AsTask();
        Assert.IsFalse(first.IsCompleted); Assert.AreSame(first, second);
        lease.Dispose(); await first;
        Assert.IsTrue(store.Diagnostics.Closed); Assert.AreEqual(0, store.Diagnostics.ActiveLeases);
    }

    [TestMethod]
    public async Task RealOrchestratorCommitsIntoDerivedStoreBeforeDrainCompletes()
    {
        var inspection = new InspectionId(Guid.NewGuid()); var run = new AcquisitionRunId(Guid.NewGuid());
        var stream = new UtStreamIdentity(inspection, run, new UtSourceId("derived-integration"), new PhysicalChannelId(0));
        await using var registry = new InspectionDataStoreRegistry(inspection,
            new InMemoryInspectionStoreOptions(4096, 4));
        await using IInspectionRunWriter writer = registry.OpenRun(run);
        await using var derived = new InMemoryGateDerivedStore();
        CompiledGatePlan plan = GateEvaluationTestData.Compile(GateEvaluationTestData.Gate("A", 3));
        await using var orchestrator = new CommittedGateProcessingOrchestrator(registry.Store, registry.Store,
            derived, new GateEvaluator(new GateScratchPool()));
        Assert.AreEqual(GateProcessingAdmissionStatus.Accepted,
            orchestrator.OpenRun(new(inspection, run, [stream], plan)));
        var identity = new TraceIdentity(stream, 17);
        var descriptor = new TraceDescriptor(identity, 1, TimeSpan.Zero, null, null, 3, 1_000_000, 0,
            UtSignalMode.Rf, sampleEncoding: UtSampleEncoding.SignedInt16,
            provenance: UtTraceProvenance.Simulator, traceQuality: UtTraceQuality.Good);
        InspectionAppendResult commit = await writer.CommitAsync(new(new(descriptor, new short[] { 1, 5, 2 })));
        orchestrator.NotifyCommitted(new(identity, commit.Revision));
        InspectionRunSealResult seal = await writer.SealRunAsync();
        orchestrator.StopAdmission(run); orchestrator.RequestFinalReconciliation(run, seal.FinalRevision);
        await orchestrator.WaitForDrainAsync(run);
        Assert.AreEqual(1, derived.Diagnostics.BatchCount);
        Assert.AreEqual(1L, orchestrator.Diagnostics.BatchesAccepted);
        Assert.AreEqual(0, derived.Diagnostics.AdmittedWriters);
    }

    private static GateEvaluationBatch Batch(short[] samples)
        => BatchAt(7, samples);

    private static GateEvaluationBatch BatchAt(ulong acquisitionIndex, short[] samples,
        UtTraceQuality quality = UtTraceQuality.Good, ulong? sourceRevision = null)
    {
        CompiledGatePlan plan = GateEvaluationTestData.Compile(GateEvaluationTestData.Gate("A", samples.Length));
        ConfirmedGateTraceDescriptor basis = GateEvaluationTestData.Trace(samples.Length);
        var identity = new TraceIdentity(basis.ConfirmedIdentity.Stream, acquisitionIndex);
        TraceDescriptor source = basis.Descriptor;
        var descriptor = new TraceDescriptor(identity, source.Sequence, source.ElapsedSinceRunStart,
            source.TimestampUtc, source.SpatialCoordinate, source.SampleCount, source.SampleRateHz,
            source.FirstSampleOffsetSeconds, source.SignalMode, source.ClockQuality, source.SampleEncoding,
            source.Provenance, quality);
        var trace = new ConfirmedGateTraceDescriptor(descriptor, identity,
            new InspectionRevision(sourceRevision ?? acquisitionIndex + 5), quality);
        ReadOnlySpan<short> input = quality == UtTraceQuality.Invalid ? ReadOnlySpan<short>.Empty : samples;
        GateEvaluationResult result = new GateEvaluator(new GateScratchPool()).Evaluate(plan, trace, input);
        return result.Batch!;
    }

    private static GateEvaluationBatch BatchAtPlan(ulong acquisitionIndex, short[] samples, string planId)
    {
        GateDefinition gate = GateEvaluationTestData.Gate("A", samples.Length);
        GatePlanCompilationResult compiled = new GatePlanCompiler().Compile(GateTestPlan.Plan([gate], planId));
        ConfirmedGateTraceDescriptor basis = GateEvaluationTestData.Trace(samples.Length);
        TraceDescriptor source = basis.Descriptor;
        var identity = new TraceIdentity(source.Identity.Stream, acquisitionIndex);
        var descriptor = new TraceDescriptor(identity, source.Sequence, source.ElapsedSinceRunStart,
            source.TimestampUtc, source.SpatialCoordinate, source.SampleCount, source.SampleRateHz,
            source.FirstSampleOffsetSeconds, source.SignalMode, source.ClockQuality, source.SampleEncoding,
            source.Provenance, source.TraceQuality);
        var trace = new ConfirmedGateTraceDescriptor(descriptor, identity, new(acquisitionIndex + 5), source.TraceQuality);
        return new GateEvaluator(new GateScratchPool()).Evaluate(compiled.CompiledPlan!, trace, samples).Batch!;
    }

    private static GateDerivedBatchKey Key(GateEvaluationBatch batch)
    {
        GateEvaluationEntry entry = batch.Entries[0];
        return new(batch.Trace.ConfirmedIdentity, batch.Trace.SourceInspectionRevision, entry.PlanId,
            entry.PlanRevision, entry.PlanSignature.Hex, entry.SchemaVersion, entry.AlgorithmVersion);
    }

    private static IGateDerivedReadLease Open(InMemoryGateDerivedStore store, DerivedViewId expected)
    {
        Assert.AreEqual(expected, store.ActiveView!.ViewId);
        return store.OpenActiveView();
    }

    private static GateDerivedCoverageManifest Manifest(GateEvaluationBatch batch, DerivedStoreRevision revision)
        => Manifest([batch], revision);

    private static GateDerivedCoverageManifest Manifest(IReadOnlyList<GateEvaluationBatch> batches,
        DerivedStoreRevision revision)
    {
        GateEvaluationBatch batch = batches[0]; GateEvaluationEntry entry = batch.Entries[0];
        TraceIdentity[] identities = batches.Select(x => x.Trace.ConfirmedIdentity).ToArray();
        TraceIdentity identity = identities[0];
        var coverage = new GateDerivedStreamCoverage(identity.Stream, identities.Min(x => x.AcquisitionIndex),
            identities.Max(x => x.AcquisitionIndex), identities.Length,
            GateDerivedStreamCoverage.ComputeIdentityDigest(identities));
        return new(identity.Stream.InspectionId, identity.Stream.AcquisitionRunId, [coverage],
            new InspectionRevision(batches.Max(x => x.Trace.SourceInspectionRevision.Value)), entry.PlanId,
            entry.PlanRevision, entry.PlanSignature.Hex, entry.SchemaVersion, entry.AlgorithmVersion,
            identities.Length, revision);
    }

    private static GateDerivedMutationResult Activate(InMemoryGateDerivedStore store,
        GateDerivedCoverageManifest manifest, string viewId)
    {
        ConfirmTerminal(store, manifest);
        return store.Activate(manifest, new DerivedViewId(viewId));
    }

    private static void ConfirmTerminal(InMemoryGateDerivedStore store, GateDerivedCoverageManifest manifest) =>
        store.ConfirmTerminalCoverage([new(manifest.InspectionId, manifest.RunId, manifest.SourceFinalRevision,
            manifest.PlanId, manifest.PlanRevision, manifest.PlanSignature, manifest.SchemaVersion,
            manifest.AlgorithmVersion, manifest.Streams)]);
}
