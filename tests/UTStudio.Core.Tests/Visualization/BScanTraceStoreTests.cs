using UTStudio.Domain.Acquisition;
using UTStudio.Visualization.Core;

namespace UTStudio.Core.Tests.Visualization;

[TestClass]
public sealed class BScanTraceStoreTests
{
    [TestMethod]
    public void OptionsValidateBounds()
    {
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new BScanOptions(1));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new BScanOptions(maximumColumns: 0));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new BScanOptions(maximumColumns: 1025));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new BScanOptions(maximumColumns: 2, minimumColumns: 3));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new BScanOptions(visualMemoryBudgetBytes: 0));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new BScanOptions(indexBucketSamples: 0));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new BScanOptions(acceptDiagnosticBudget: TimeSpan.Zero));
    }

    [TestMethod]
    public void VisualBudgetIsAppliedBeforeRentingRfBlocks()
    {
        var pool = new DeterministicShortPool();

        Assert.ThrowsExactly<InvalidOperationException>(() => new BScanTraceStore(65_535,
            new BScanOptions(visualMemoryBudgetBytes: 100, maximumColumns: 128, minimumColumns: 128), pool));

        Assert.AreEqual(0, pool.RentCount);
    }

    [TestMethod]
    public async Task DefaultBudgetReservesExpectedEffectiveMemoryForSmallAndMaximumTraces()
    {
        var smallPool = new DeterministicShortPool();
        var small = new BScanTraceStore(2048, new BScanOptions(), smallPool);
        Assert.AreEqual(1024, small.Capacity);
        Assert.AreEqual(16, small.BlocksRented);
        Assert.AreEqual(4L * 1024 * 1024, small.RentedBytes);
        await small.CloseAsync();
        Assert.AreEqual(16, smallPool.ReturnCount);

        var largePool = new DeterministicShortPool();
        var large = new BScanTraceStore(65_535, new BScanOptions(), largePool);
        Assert.AreEqual(256, large.Capacity);
        Assert.AreEqual(32, large.BlocksRented);
        Assert.AreEqual(32L * 65_535 * 8 * sizeof(short), large.RentedBytes);
        Assert.AreEqual(33, largePool.RentCount);
        Assert.AreEqual(1, largePool.ReturnCount, "The first block outside the budget must be returned immediately.");
        await large.CloseAsync();
        Assert.AreEqual(33, largePool.ReturnCount);
    }

    [TestMethod]
    public void OversizedRentAndIntermediateFailureRollBackTransactionally()
    {
        var oversized = new DeterministicShortPool(requested => requested * 2);
        Assert.ThrowsExactly<InvalidOperationException>(() => new BScanTraceStore(16,
            new BScanOptions(traceMemoryBudgetBytes: 256, maximumColumns: 8, minimumColumns: 8), oversized));
        Assert.AreEqual(oversized.RentCount, oversized.ReturnCount);
        Assert.IsEmpty(oversized.Outstanding);

        var throwing = new DeterministicShortPool(throwOnRent: 2);
        Assert.ThrowsExactly<InvalidOperationException>(() => new BScanTraceStore(16,
            new BScanOptions(traceMemoryBudgetBytes: 4096, maximumColumns: 128, minimumColumns: 128), throwing));
        Assert.AreEqual(1, throwing.ReturnCount);
        Assert.IsEmpty(throwing.Outstanding);

        var cleanupFailure = new DeterministicShortPool(throwOnRent: 2);
        cleanupFailure.ThrowOnReturns.Add(1);
        AggregateException aggregate = Assert.ThrowsExactly<AggregateException>(() => new BScanTraceStore(16,
            new BScanOptions(traceMemoryBudgetBytes: 4096, maximumColumns: 128, minimumColumns: 128), cleanupFailure));
        Assert.HasCount(2, aggregate.InnerExceptions);
        Assert.IsEmpty(cleanupFailure.Outstanding);
    }

    [TestMethod]
    public async Task SuccessfulOversizedRentChargesPhysicalLengthAndPartialLastBlock()
    {
        var pool = new DeterministicShortPool(requested => requested + 17);
        var store = new BScanTraceStore(10,
            new BScanOptions(traceMemoryBudgetBytes: 4096, maximumColumns: 65, minimumColumns: 1), pool);

        Assert.AreEqual(65, store.Capacity);
        Assert.AreEqual(2, store.BlocksRented);
        Assert.AreEqual((640L + 17 + 10 + 17) * sizeof(short), store.RentedBytes);
        Assert.AreEqual(65L * 10 * sizeof(short), store.LogicalBytes);
        Assert.IsLessThanOrEqualTo(16L * 1024 * 1024, store.VisualLogicalBytes);

        await store.CloseAsync();
        Assert.AreEqual(pool.RentCount, pool.ReturnCount);
    }

    [TestMethod]
    public async Task CloseAttemptsEveryBlockWhenPoolReturnFails()
    {
        var pool = new DeterministicShortPool();
        pool.ThrowOnReturns.Add(1);
        var store = new BScanTraceStore(10,
            new BScanOptions(traceMemoryBudgetBytes: 4096, maximumColumns: 65, minimumColumns: 1), pool);

        await Assert.ThrowsExactlyAsync<AggregateException>(() => store.CloseAsync());

        Assert.AreEqual(2, pool.ReturnCount);
        Assert.IsEmpty(pool.Outstanding);
        await Assert.ThrowsExactlyAsync<AggregateException>(() => store.CloseAsync());
        Assert.AreEqual(2, pool.ReturnCount);
    }

    [TestMethod]
    public async Task PinRejectsNextSlotWithoutWaitingAndGenerationPreventsAba()
    {
        var pool = new DeterministicShortPool();
        var store = new BScanTraceStore(3,
            new BScanOptions(traceMemoryBudgetBytes: 1024, maximumColumns: 1, minimumColumns: 1), pool);
        var first = Identity(0);
        Commit(store, first, [1, 2, 3]);
        using var lease = store.TryAcquire(first)!;

        Assert.AreEqual(StoreWriteKind.Pinned, store.BeginWrite(3).Kind);
        Assert.AreEqual(1UL, store.Statistics.Accepted);
        lease.Dispose();
        var second = Identity(1);
        Commit(store, second, [4, 5, 6]);

        Assert.IsNull(store.TryAcquire(first));
        Assert.AreEqual(BScanResidence.Evicted, store.GetResidence(first));
        using var secondLease = store.TryAcquire(second)!;
        CollectionAssert.AreEqual(new short[] { 4, 5, 6 }, secondLease.Samples.ToArray());
        secondLease.Dispose();
        await store.CloseAsync();
        Assert.AreEqual(pool.RentCount, pool.ReturnCount);
    }

    [TestMethod]
    public async Task CloseWaitsForOutstandingLeaseBeforeReturningBlocks()
    {
        var pool = new DeterministicShortPool();
        var store = new BScanTraceStore(2,
            new BScanOptions(traceMemoryBudgetBytes: 128, maximumColumns: 1, minimumColumns: 1), pool);
        var identity = Identity(0);
        Commit(store, identity, [7, 8]);
        var lease = store.TryAcquire(identity)!;

        Task close = store.CloseAsync();
        Assert.IsFalse(close.IsCompleted);
        Assert.AreEqual(0, pool.ReturnCount);
        Assert.IsNull(store.TryAcquire(identity));
        lease.Dispose();
        await close.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.AreEqual(pool.RentCount, pool.ReturnCount);
        Assert.AreEqual(0, store.Statistics.ActiveLeases);
    }

    [TestMethod]
    public async Task CloseWaitsForAdmittedWriterAndDoesNotCommitIt()
    {
        var pool = new DeterministicShortPool();
        var store = new BScanTraceStore(2,
            new BScanOptions(traceMemoryBudgetBytes: 128, maximumColumns: 1, minimumColumns: 1), pool);
        var reservation = store.BeginWrite(2);

        Task close = store.CloseAsync();
        Assert.IsFalse(close.IsCompleted);
        reservation.Destination.Span.Fill(4);
        BScanTraceIdentity identity = Identity(0);
        var metadata = new ConventionalUtFrameMetadata(identity.SourceId, identity.RunId,
            new(identity.PhysicalChannelId, 2, 1), DateTimeOffset.UnixEpoch);
        Assert.AreEqual(StoreWriteKind.Closed, reservation.Commit(new(metadata, identity, 0, TimeSpan.Zero,
            2, 1, 0, null, 0, 0)).Kind);
        await close.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.AreEqual(0UL, store.Statistics.Accepted);
        Assert.AreEqual(pool.RentCount, pool.ReturnCount);
    }

    private static void Commit(BScanTraceStore store, BScanTraceIdentity identity, short[] samples)
    {
        var reservation = store.BeginWrite(samples.Length);
        Assert.AreEqual(StoreWriteKind.Accepted, reservation.Kind);
        samples.CopyTo(reservation.Destination);
        var metadata = new ConventionalUtFrameMetadata(identity.SourceId, identity.RunId,
            new(identity.PhysicalChannelId, samples.Length, 10_000_000), DateTimeOffset.UnixEpoch);
        var descriptor = new BScanTraceDescriptor(metadata, identity, identity.Sequence, TimeSpan.Zero,
            samples.Length, 50_000_000, 0, null, 0, 0);
        Assert.AreEqual(StoreWriteKind.Accepted, reservation.Commit(descriptor).Kind);
    }

    private static BScanTraceIdentity Identity(ulong sequence) => new(new UtSourceId("bscan"),
        new AcquisitionRunId(Guid.Parse("10000000-0000-0000-0000-000000000001")), default, sequence);
}
