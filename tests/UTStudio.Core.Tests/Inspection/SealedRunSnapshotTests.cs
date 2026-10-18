using UTStudio.Contracts.Inspection;
using UTStudio.Domain.Acquisition;
using UTStudio.Domain.Inspection;
using UTStudio.Inspection.Core;

namespace UTStudio.Core.Tests.Inspection;

[TestClass]
public sealed class SealedRunSnapshotTests
{
    private static readonly InspectionId Inspection = new(Guid.Parse("11111111-1111-1111-1111-111111111111"));
    private static readonly AcquisitionRunId Run = new(Guid.Parse("22222222-2222-2222-2222-222222222222"));
    private static readonly UtStreamIdentity Stream = new(Inspection, Run, new UtSourceId("snapshot"), new PhysicalChannelId(1));

    [TestMethod]
    public async Task OpenRunIsRejectedAndSealedRunPinsPagesToFinalRevision()
    {
        await using var store = Store();
        IInspectionRunWriter writer = await store.OpenRunAsync(Run);
        await CommitAsync(writer, 1); await CommitAsync(writer, 3); await CommitAsync(writer, 7);
        var premature = await store.OpenSealedRunSnapshotAsync(Request(new(3), 2));
        Assert.AreEqual(InspectionSealedRunSnapshotOpenStatus.RunNotSealed, premature.Status);
        InspectionRunSealResult seal = await writer.SealRunAsync();

        InspectionSealedRunSnapshotOpenResult opened = await store.OpenSealedRunSnapshotAsync(Request(seal.FinalRevision, 2));
        Assert.AreEqual(InspectionSealedRunSnapshotOpenStatus.Available, opened.Status);
        using IInspectionSealedRunSnapshot snapshot = opened.Snapshot!;
        InspectionSealedRunIdentityPage first = snapshot.ReadIdentities(Stream);
        InspectionSealedRunIdentityPage second = snapshot.ReadIdentities(Stream, first.NextCursor);
        CollectionAssert.AreEqual(new ulong[] { 1, 3 }, first.Identities.Select(x => x.AcquisitionIndex).ToArray());
        CollectionAssert.AreEqual(new ulong[] { 7 }, second.Identities.Select(x => x.AcquisitionIndex).ToArray());
        Assert.AreEqual(seal.FinalRevision, snapshot.SourceFinalRevision);
        Assert.AreEqual(3, snapshot.Streams.Single().ConfirmedTraceCount);
        Assert.AreEqual(64, snapshot.Streams.Single().IdentityDigest.Length);
    }

    [TestMethod]
    public async Task RevisionMismatchAndForeignCursorAreRejected()
    {
        await using var store = Store();
        IInspectionRunWriter writer = await store.OpenRunAsync(Run);
        await CommitAsync(writer, 0); await CommitAsync(writer, 1);
        InspectionRunSealResult seal = await writer.SealRunAsync();
        Assert.AreEqual(InspectionSealedRunSnapshotOpenStatus.RevisionMismatch,
            (await store.OpenSealedRunSnapshotAsync(Request(new(seal.FinalRevision.Value + 1), 1))).Status);
        using IInspectionSealedRunSnapshot one = (await store.OpenSealedRunSnapshotAsync(Request(seal.FinalRevision, 1))).Snapshot!;
        using IInspectionSealedRunSnapshot two = (await store.OpenSealedRunSnapshotAsync(Request(seal.FinalRevision, 1))).Snapshot!;
        InspectionSealedRunCursor cursor = one.ReadIdentities(Stream).NextCursor!.Value;
        Assert.ThrowsExactly<ArgumentException>(() => two.ReadIdentities(Stream, cursor));
        Assert.ThrowsExactly<ArgumentException>(() => one.ReadIdentities(Stream, default(InspectionSealedRunCursor)));
        const int changedCharacter = 10;
        char replacement = cursor.Value[changedCharacter] == 'A' ? 'B' : 'A';
        var manipulated = new InspectionSealedRunCursor(cursor.Value[..changedCharacter] + replacement +
            cursor.Value[(changedCharacter + 1)..]);
        Assert.ThrowsExactly<ArgumentException>(() => one.ReadIdentities(Stream, manipulated));
        one.Dispose();
        Assert.ThrowsExactly<ObjectDisposedException>(() => one.ReadIdentities(Stream));
    }

    [TestMethod]
    public async Task KeyContinuationVisitsEachSparseIdentityOnceWithoutPrefixRescan()
    {
        await using var store = new InMemoryInspectionDataStore(Inspection,
            new InMemoryInspectionStoreOptions(32_768, 1_024));
        IInspectionRunWriter writer = await store.OpenRunAsync(Run);
        const int count = 512;
        for (int item = 0; item < count - 1; item++)
            await CommitAsync(writer, checked((ulong)item * 17));
        await CommitAsync(writer, ulong.MaxValue);
        InspectionRunSealResult seal = await writer.SealRunAsync();
        using IInspectionSealedRunSnapshot snapshot =
            (await store.OpenSealedRunSnapshotAsync(Request(seal.FinalRevision, 1))).Snapshot!;

        var actual = new List<ulong>(count);
        InspectionSealedRunCursor? cursor = null;
        do
        {
            InspectionSealedRunIdentityPage page = snapshot.ReadIdentities(Stream, cursor);
            actual.AddRange(page.Identities.Select(x => x.AcquisitionIndex));
            cursor = page.NextCursor;
        } while (cursor is not null);

        Assert.HasCount(count, actual);
        Assert.HasCount(count, actual.Distinct());
        Assert.AreEqual(ulong.MaxValue, actual[^1]);
        Assert.IsTrue(actual.SequenceEqual(actual.Order()));
        InspectionSealedRunEnumerationDiagnostics diagnostics = snapshot.EnumerationDiagnostics;
        Assert.AreEqual(count, diagnostics.PagesRead);
        Assert.AreEqual(count, diagnostics.IdentitiesVisited);
        Assert.AreEqual(count, diagnostics.EntriesReturned);
        Assert.AreEqual(2, diagnostics.ContinuationSearches);
        Assert.AreEqual(1, diagnostics.LowerBoundSearches);
        Assert.AreEqual(1, diagnostics.UpperBoundSearches);
        Assert.AreEqual(count - 1, diagnostics.CursorResumes);
        Assert.AreEqual(0, diagnostics.PrefixRescans);
        Assert.IsLessThanOrEqualTo(20, diagnostics.ContinuationSearchComparisons,
            $"Unexpected continuation search cost: {diagnostics.ContinuationSearchComparisons} comparisons.");
    }

    [TestMethod]
    public async Task RegionalQueryNormalizesRangesAndBindsCursorToExactQuery()
    {
        await using var store = new InMemoryInspectionDataStore(Inspection,
            new InMemoryInspectionStoreOptions(4096, 64));
        IInspectionRunWriter writer = await store.OpenRunAsync(Run);
        foreach (ulong index in new ulong[] { 0, 1, 2, 10, 20, ulong.MaxValue })
            await CommitAsync(writer, index);
        InspectionRunSealResult seal = await writer.SealRunAsync();
        using IInspectionSealedRunSnapshot snapshot =
            (await store.OpenSealedRunSnapshotAsync(Request(seal.FinalRevision, 2))).Snapshot!;
        var query = new InspectionSealedRunRangeQuery(Stream,
            [new(20, 20), new(0, 1), new(1, 2), new(10, 10), new(ulong.MaxValue, ulong.MaxValue)], 2);
        Assert.HasCount(4, query.Ranges);

        InspectionSealedRunIdentityPage first = snapshot.ReadIdentities(query);
        InspectionSealedRunIdentityPage second = snapshot.ReadIdentities(query, first.NextCursor);
        InspectionSealedRunIdentityPage third = snapshot.ReadIdentities(query, second.NextCursor);
        CollectionAssert.AreEqual(new ulong[] { 0, 1 }, first.Identities.Select(x => x.AcquisitionIndex).ToArray());
        CollectionAssert.AreEqual(new ulong[] { 2, 10 }, second.Identities.Select(x => x.AcquisitionIndex).ToArray());
        CollectionAssert.AreEqual(new ulong[] { 20, ulong.MaxValue }, third.Identities.Select(x => x.AcquisitionIndex).ToArray());
        Assert.IsNull(third.NextCursor);

        var differentRanges = new InspectionSealedRunRangeQuery(Stream, [new(0, 10)], 2);
        var differentPage = new InspectionSealedRunRangeQuery(Stream, query.Ranges, 1);
        Assert.ThrowsExactly<ArgumentException>(() => snapshot.ReadIdentities(differentRanges, first.NextCursor));
        Assert.ThrowsExactly<ArgumentException>(() => snapshot.ReadIdentities(differentPage, first.NextCursor));
        InspectionSealedRunEnumerationDiagnostics diagnostics = snapshot.EnumerationDiagnostics;
        Assert.AreEqual(8, diagnostics.ContinuationSearches);
        Assert.AreEqual(6, diagnostics.IdentitiesVisited);
        Assert.AreEqual(6, diagnostics.EntriesReturned);
        Assert.AreEqual(4, diagnostics.LowerBoundSearches);
        Assert.AreEqual(4, diagnostics.UpperBoundSearches);
        Assert.AreEqual(2, diagnostics.CursorResumes);
        Assert.AreEqual(0, diagnostics.PrefixRescans);
    }

    [TestMethod]
    public async Task SealedIndexIsBuiltOnceAndSharedBySnapshotsIncludingEmptyRegion()
    {
        await using var store = Store();
        IInspectionRunWriter writer = await store.OpenRunAsync(Run);
        await CommitAsync(writer, 5);
        InspectionRunSealResult seal = await writer.SealRunAsync();
        using IInspectionSealedRunSnapshot first =
            (await store.OpenSealedRunSnapshotAsync(Request(seal.FinalRevision, 1))).Snapshot!;
        using IInspectionSealedRunSnapshot second =
            (await store.OpenSealedRunSnapshotAsync(Request(seal.FinalRevision, 1))).Snapshot!;
        long owned = store.Diagnostics.OwnedSealedIndexCapacityBytes;
        Assert.IsGreaterThan(0, owned);
        var empty = new InspectionSealedRunRangeQuery(Stream, [new(100, 200)], 1);
        Assert.IsEmpty(first.ReadIdentities(empty).Identities);
        Assert.IsEmpty(second.ReadIdentities(empty).Identities);
        Assert.AreEqual(owned, store.Diagnostics.OwnedSealedIndexCapacityBytes);
    }

    [TestMethod]
    public async Task CloseWaitsForActiveSnapshotAndDoesNotForceIt()
    {
        var store = Store();
        IInspectionRunWriter writer = await store.OpenRunAsync(Run);
        await CommitAsync(writer, 0); InspectionRunSealResult seal = await writer.SealRunAsync();
        IInspectionSealedRunSnapshot snapshot = (await store.OpenSealedRunSnapshotAsync(Request(seal.FinalRevision, 1))).Snapshot!;
        Task close = store.DisposeAsync().AsTask();
        await Task.Yield();
        Assert.IsFalse(close.IsCompleted);
        snapshot.Dispose();
        await close;
    }

    [TestMethod]
    public async Task SnapshotOwnershipIsAdmittedOnceAndConcurrentDisposeReleasesExactlyOnce()
    {
        var store = Store();
        IInspectionRunWriter writer = await store.OpenRunAsync(Run);
        await CommitAsync(writer, 0);
        InspectionRevision revision = (await writer.SealRunAsync()).FinalRevision;
        Assert.AreEqual(0, store.Diagnostics.ActiveLeases);
        IInspectionSealedRunSnapshot snapshot =
            (await store.OpenSealedRunSnapshotAsync(Request(revision, 1))).Snapshot!;
        Assert.AreEqual(1, store.Diagnostics.ActiveLeases);
        Parallel.Invoke(snapshot.Dispose, snapshot.Dispose);
        Assert.AreEqual(0, store.Diagnostics.ActiveLeases);
        await store.DisposeAsync();
    }

    [TestMethod]
    public async Task RejectedAndCancelledSnapshotOpenNeverCreateOwnership()
    {
        await using var store = Store();
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        Assert.AreEqual(InspectionSealedRunSnapshotOpenStatus.Cancelled,
            (await store.OpenSealedRunSnapshotAsync(Request(new(1), 1), cancelled.Token)).Status);
        Assert.AreEqual(InspectionSealedRunSnapshotOpenStatus.NotAvailable,
            (await store.OpenSealedRunSnapshotAsync(Request(new(1), 1))).Status);
        Assert.AreEqual(0, store.Diagnostics.ActiveLeases);
        Assert.AreEqual(0, store.Diagnostics.OwnedPreparedQueryCapacityBytes);
        Assert.AreEqual(0, store.Diagnostics.ReservedPreparedQueryCapacityBytes);
    }

    [TestMethod]
    public async Task SealedIndexBudgetIsReservedOnceAndRollsBackAtomically()
    {
        long required;
        await using (var measured = new InMemoryInspectionDataStore(Inspection,
            new InMemoryInspectionStoreOptions(4096, 64, sealedIndexCapacityBudgetBytes: 1_000_000)))
        {
            IInspectionRunWriter writer = await measured.OpenRunAsync(Run);
            await CommitAsync(writer, 1); await CommitAsync(writer, 2);
            await writer.SealRunAsync();
            StoreDiagnostics diagnostics = measured.Diagnostics;
            required = diagnostics.OwnedSealedIndexCapacityBytes;
            Assert.IsGreaterThan(0, required);
            Assert.AreEqual(0, diagnostics.ReservedSealedIndexCapacityBytes);
            Assert.AreEqual(required, diagnostics.PeakSealedIndexCapacityBytes);
        }

        var rejected = new InMemoryInspectionDataStore(Inspection,
            new InMemoryInspectionStoreOptions(4096, 64,
                sealedIndexCapacityBudgetBytes: required - 1));
        IInspectionRunWriter rejectedWriter = await rejected.OpenRunAsync(Run);
        await CommitAsync(rejectedWriter, 1); await CommitAsync(rejectedWriter, 2);
        InspectionSealedIndexCapacityException error = await Assert.ThrowsExactlyAsync<InspectionSealedIndexCapacityException>(
            async () => await rejectedWriter.SealRunAsync());
        Assert.AreEqual("sealed-index-capacity-exceeded", error.ErrorCode);
        StoreDiagnostics after = rejected.Diagnostics;
        Assert.AreEqual(0, after.ReservedSealedIndexCapacityBytes);
        Assert.AreEqual(0, after.OwnedSealedIndexCapacityBytes);
        RunStoreDiagnostics run = after.Runs!.Single();
        Assert.AreEqual(InspectionRunState.Faulted, run.State);
        Assert.IsNull(run.FinalRevision);
        await Assert.ThrowsExactlyAsync<InspectionStoreCloseException>(async () => await rejected.DisposeAsync());
    }

    [TestMethod]
    public async Task SealedIndexPublicationIsAtomicAndDoesNotDoubleAccountOwnership()
    {
        await using var store = Store();
        IInspectionRunWriter writer = await store.OpenRunAsync(Run);
        await CommitAsync(writer, 1); await CommitAsync(writer, 2);
        InspectionRunSealResult seal = await writer.SealRunAsync();
        StoreDiagnostics diagnostics = store.Diagnostics;
        Assert.AreEqual(0L, diagnostics.ReservedSealedIndexCapacityBytes);
        Assert.IsGreaterThan(0L, diagnostics.OwnedSealedIndexCapacityBytes);
        RunStoreDiagnostics run = diagnostics.Runs!.Single();
        Assert.AreEqual(InspectionRunState.Sealed, run.State);
        Assert.AreEqual(seal.FinalRevision, run.FinalRevision);
        using IInspectionSealedRunSnapshot snapshot =
            (await store.OpenSealedRunSnapshotAsync(Request(seal.FinalRevision, 1))).Snapshot!;
        Assert.AreEqual(2L, snapshot.CountIdentities(
            new InspectionSealedRunRangeQuery(Stream, [new(0, ulong.MaxValue)], 1)));
    }

    [TestMethod]
    public async Task PreparedQueriesEnforceIndependentCountAndByteBudgets()
    {
        await using var store = Store();
        IInspectionRunWriter writer = await store.OpenRunAsync(Run);
        await CommitAsync(writer, 1); await CommitAsync(writer, 10);
        InspectionRunSealResult seal = await writer.SealRunAsync();
        const long oneRangeBytes = 152;
        using IInspectionSealedRunSnapshot exact = (await store.OpenSealedRunSnapshotAsync(
            new(Inspection, Run, seal.FinalRevision, [Stream], 1, 2, oneRangeBytes * 2))).Snapshot!;
        Assert.AreEqual(1, exact.CountIdentities(new(Stream, [new(1, 1)], 1)));
        Assert.AreEqual(1, exact.CountIdentities(new(Stream, [new(10, 10)], 1)));
        InspectionSealedRunEnumerationDiagnostics full = exact.EnumerationDiagnostics;
        Assert.AreEqual(oneRangeBytes * 2, full.PreparedQueryOwnedBytes);
        Assert.AreEqual(2, full.ActivePreparedQueries);
        Assert.ThrowsExactly<InvalidOperationException>(() =>
            exact.CountIdentities(new(Stream, [new(0, ulong.MaxValue)], 1)));
        Assert.AreEqual(1, exact.EnumerationDiagnostics.PreparedQueryRejections);

        using IInspectionSealedRunSnapshot shortByOne = (await store.OpenSealedRunSnapshotAsync(
            new(Inspection, Run, seal.FinalRevision, [Stream], 1, 1, oneRangeBytes - 1))).Snapshot!;
        Assert.ThrowsExactly<InvalidOperationException>(() =>
            shortByOne.CountIdentities(new(Stream, [new(1, 1)], 1)));
        InspectionSealedRunEnumerationDiagnostics rejected = shortByOne.EnumerationDiagnostics;
        Assert.AreEqual(0, rejected.PreparedQueryOwnedBytes);
        Assert.AreEqual(0, rejected.PreparedQueryReservedBytes);
        Assert.AreEqual(1, rejected.PreparedQueryRejections);
    }

    [TestMethod]
    public async Task PreparedQueryBudgetIsAggregatedAcrossSnapshotsAndReleasedOnDispose()
    {
        const long queryBytes = 152;
        await using var store = new InMemoryInspectionDataStore(Inspection,
            new InMemoryInspectionStoreOptions(4096, 64,
                preparedQueryCapacityBudgetBytes: queryBytes));
        IInspectionRunWriter writer = await store.OpenRunAsync(Run);
        await CommitAsync(writer, 1);
        InspectionRunSealResult seal = await writer.SealRunAsync();
        var request = new InspectionSealedRunSnapshotRequest(
            Inspection, Run, seal.FinalRevision, [Stream], 1, 1, queryBytes);
        IInspectionSealedRunSnapshot first = (await store.OpenSealedRunSnapshotAsync(request)).Snapshot!;
        using IInspectionSealedRunSnapshot second = (await store.OpenSealedRunSnapshotAsync(request)).Snapshot!;
        var query = new InspectionSealedRunRangeQuery(Stream, [new(1, 1)], 1);
        Assert.AreEqual(1, first.CountIdentities(query));
        Assert.ThrowsExactly<InvalidOperationException>(() => second.CountIdentities(query));
        Assert.AreEqual(queryBytes, store.Diagnostics.OwnedPreparedQueryCapacityBytes);
        first.Dispose();
        Assert.AreEqual(0, store.Diagnostics.OwnedPreparedQueryCapacityBytes);
        Assert.AreEqual(1, second.CountIdentities(query));
    }

    private static InMemoryInspectionDataStore Store() => new(Inspection, new InMemoryInspectionStoreOptions(4096, 64));
    private static InspectionSealedRunSnapshotRequest Request(InspectionRevision revision, int pageSize) =>
        new(Inspection, Run, revision, [Stream], pageSize);
    private static async Task CommitAsync(IInspectionRunWriter writer, ulong index)
    {
        var identity = new TraceIdentity(Stream, index);
        var descriptor = new TraceDescriptor(identity, index, TimeSpan.Zero, null, null,
            4, 10_000_000, 0, UtSignalMode.Rf, traceQuality: UtTraceQuality.Good);
        Assert.AreEqual(InspectionAppendStatus.Committed,
            (await writer.CommitAsync(new(new(descriptor, new short[] { 1, 4, 2, 0 })))).Status);
    }
}
