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
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new BScanOptions(visualMemoryBudgetBytes: -1));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new BScanOptions(
            visualMemoryBudgetBytes: checked(BScanOptions.MaximumVisualMemoryBudgetBytes + 1)));
        Assert.AreEqual(1, new BScanOptions(visualMemoryBudgetBytes: 1).VisualMemoryBudgetBytes);
        Assert.AreEqual(BScanOptions.MaximumVisualMemoryBudgetBytes,
            new BScanOptions().VisualMemoryBudgetBytes);
        Assert.AreEqual(BScanOptions.MaximumVisualMemoryBudgetBytes,
            new BScanOptions(visualMemoryBudgetBytes: BScanOptions.MaximumVisualMemoryBudgetBytes)
                .VisualMemoryBudgetBytes);
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
        Assert.IsFalse(store.Statistics.Closed);
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

    [TestMethod]
    public async Task Visual_state_is_not_current_while_slot_is_writing_or_waiting_for_index()
    {
        var pool = new DeterministicShortPool();
        var store = new BScanTraceStore(2,
            new BScanOptions(traceMemoryBudgetBytes: 128, maximumColumns: 1, minimumColumns: 1), pool);
        BScanTraceIdentity first = Identity(0);
        Commit(store, first, [7, 8]);
        Assert.IsTrue(store.CommitIndex(first, 1, [new(7, 0, 8, 1)]));
        Assert.IsTrue(store.IsVisualStateCurrent(1, 0));

        BScanTraceStore.StoreWriteReservation writer = store.BeginWrite(2);
        Assert.AreEqual(StoreWriteKind.Accepted, writer.Kind);
        Assert.IsFalse(store.IsVisualStateCurrent(1, 0));
        writer.Abort();
        Assert.IsTrue(store.IsVisualStateCurrent(1, 0));

        BScanTraceIdentity second = Identity(1);
        Commit(store, second, [9, 10]);
        Assert.IsFalse(store.IsVisualStateCurrent(1, 0));
        Assert.IsTrue(store.CommitIndex(second, 2, [new(9, 0, 10, 1)]));
        Assert.IsTrue(store.IsVisualStateCurrent(2, 1));

        await store.CloseAsync();
        Assert.AreEqual(pool.RentCount, pool.ReturnCount);
    }

    [TestMethod]
    public async Task Visual_capture_keeps_next_wrap_slots_writable()
    {
        var pool = new DeterministicShortPool();
        var store = new BScanTraceStore(2,
            new BScanOptions(traceMemoryBudgetBytes: 256, maximumColumns: 4, minimumColumns: 4), pool);
        for (ulong sequence = 0; sequence < 4; sequence++)
        {
            BScanTraceIdentity identity = Identity(sequence);
            Commit(store, identity, [checked((short)(sequence + 1)), checked((short)(sequence + 2))]);
            Assert.IsTrue(store.CommitIndex(identity, 1,
                [new(checked((short)(sequence + 1)), 0, checked((short)(sequence + 2)), 1)]));
        }
        using (BScanVisualLeaseBatch batch = store.CaptureVisualLeases(CancellationToken.None, out _)!)
        {
            Assert.AreEqual(4, batch.Count);
            Assert.AreEqual(0, store.Statistics.ActiveLeases);

            BScanTraceStore.StoreWriteReservation writer = store.BeginWrite(2);
            Assert.AreEqual(StoreWriteKind.Accepted, writer.Kind);
            writer.Abort();
        }

        await store.CloseAsync();
        Assert.AreEqual(pool.RentCount, pool.ReturnCount);
    }

    [TestMethod]
    public async Task Second_capture_rent_failure_rolls_back_transactionally_one_hundred_times()
    {
        for (int iteration = 0; iteration < 100; iteration++)
        {
            var visual = new BoundedBScanVisualBufferPool(BScanOptions.MaximumVisualMemoryBudgetBytes);
            var store = CreateIndexedStore(4, visual);
            BScanVisualMemorySnapshot baseline = visual.Snapshot;
            int detachedRent = 0;
            visual.AfterBackingStorageAcquired = kind =>
            {
                if ((kind is BScanVisualBufferKind.DetachedSamples or BScanVisualBufferKind.DetachedIndex) &&
                    Interlocked.Increment(ref detachedRent) == 3)
                { throw new InvalidOperationException("synthetic second-capture rent failure"); }
            };

            Assert.ThrowsExactly<InvalidOperationException>(() =>
                store.CaptureVisualLeases(CancellationToken.None, out _));
            Assert.AreEqual(0, store.Statistics.ActiveLeases);
            Assert.AreEqual(baseline.ActiveOwners, visual.Snapshot.ActiveOwners);
            Assert.AreEqual(baseline.ActiveBytes, visual.Snapshot.ActiveBytes);

            visual.AfterBackingStorageAcquired = null;
            if ((iteration & 1) == 0)
            {
                using BScanVisualLeaseBatch recovered =
                    store.CaptureVisualLeases(CancellationToken.None, out _)!;
                Assert.AreEqual(4, recovered.Count);
            }
            await store.CloseAsync().WaitAsync(TimeSpan.FromSeconds(10));
            await visual.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
            AssertVisualPoolDrained(visual);
        }
    }

    [TestMethod]
    public async Task Capture_failure_matrix_cancels_cleans_and_recovers_twenty_times()
    {
        for (int iteration = 0; iteration < 100; iteration++)
        {
            var visual = new BoundedBScanVisualBufferPool(BScanOptions.MaximumVisualMemoryBudgetBytes);
            var store = CreateIndexedStore(5, visual);
            BScanVisualMemorySnapshot baseline = visual.Snapshot;
            int scenario = iteration / 20;
            using var cancellation = new CancellationTokenSource();
            int detachedRent = 0;
            if (scenario <= 2)
            {
                int failAt = scenario switch { 0 => 1, 1 => 3, _ => 8 };
                visual.AfterBackingStorageAcquired = kind =>
                {
                    if ((kind is BScanVisualBufferKind.DetachedSamples or BScanVisualBufferKind.DetachedIndex) &&
                        Interlocked.Increment(ref detachedRent) == failAt)
                    { throw new InvalidOperationException($"synthetic rent failure {failAt}"); }
                };
                Assert.ThrowsExactly<InvalidOperationException>(() =>
                    store.CaptureVisualLeases(CancellationToken.None, out _));
            }
            else if (scenario == 3)
            {
                store.VisualLeaseCaptureProbe = (stage, count) =>
                {
                    if (stage == BScanVisualLeaseCaptureStage.AfterCapture && count == 2)
                    { cancellation.Cancel(); }
                };
                Assert.ThrowsExactly<OperationCanceledException>(() =>
                    store.CaptureVisualLeases(cancellation.Token, out _));
            }
            else
            {
                store.VisualLeaseCaptureProbe = (stage, count) =>
                {
                    if (stage == BScanVisualLeaseCaptureStage.AfterLeaseAcquired && count == 4)
                    { throw new InvalidOperationException("synthetic post-lease failure"); }
                };
                Assert.ThrowsExactly<InvalidOperationException>(() =>
                    store.CaptureVisualLeases(CancellationToken.None, out _));
            }

            Assert.AreEqual(0, store.Statistics.ActiveLeases);
            Assert.AreEqual(baseline.ActiveOwners, visual.Snapshot.ActiveOwners);
            Assert.AreEqual(baseline.ActiveBytes, visual.Snapshot.ActiveBytes);
            visual.AfterBackingStorageAcquired = null;
            store.VisualLeaseCaptureProbe = null;
            using (BScanVisualLeaseBatch recovered =
                store.CaptureVisualLeases(CancellationToken.None, out _)!)
            { Assert.AreEqual(5, recovered.Count); }
            await store.CloseAsync().WaitAsync(TimeSpan.FromSeconds(10));
            await visual.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
            AssertVisualPoolDrained(visual);
        }
    }

    [TestMethod]
    public async Task Capture_preserves_primary_and_attempts_all_cleanup_after_secondary_failures()
    {
        var visual = new BoundedBScanVisualBufferPool(BScanOptions.MaximumVisualMemoryBudgetBytes);
        var store = CreateIndexedStore(4, visual);
        int returns = 0;
        visual.AfterOwnerReturned = kind =>
        {
            if (kind is BScanVisualBufferKind.DetachedSamples or BScanVisualBufferKind.DetachedIndex)
            {
                Interlocked.Increment(ref returns);
                throw new ApplicationException("synthetic cleanup failure");
            }
        };
        store.VisualLeaseCaptureProbe = (stage, count) =>
        {
            if (stage == BScanVisualLeaseCaptureStage.AfterCapture && count == 2)
            { throw new InvalidOperationException("primary capture failure"); }
        };

        AggregateException error = Assert.ThrowsExactly<AggregateException>(() =>
            store.CaptureVisualLeases(CancellationToken.None, out _));
        Assert.IsInstanceOfType<InvalidOperationException>(error.InnerExceptions[0]);
        Assert.AreEqual("primary capture failure", error.InnerExceptions[0].Message);
        Assert.AreEqual(4, returns);
        Assert.AreEqual(0, store.Statistics.ActiveLeases);

        visual.AfterOwnerReturned = null;
        store.VisualLeaseCaptureProbe = null;
        await store.CloseAsync().WaitAsync(TimeSpan.FromSeconds(10));
        await visual.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        AssertVisualPoolDrained(visual);
    }

    [TestMethod]
    public async Task Concurrent_close_waits_for_capture_rollback_and_then_completes()
    {
        var visual = new BoundedBScanVisualBufferPool(BScanOptions.MaximumVisualMemoryBudgetBytes);
        var store = CreateIndexedStore(4, visual);
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        store.VisualLeaseCaptureProbe = (stage, count) =>
        {
            if (stage == BScanVisualLeaseCaptureStage.BeforeOwnerTransfer && count == 1)
            {
                entered.Set();
                Assert.IsTrue(release.Wait(TimeSpan.FromSeconds(10)));
                throw new InvalidOperationException("synthetic capture failure");
            }
        };
        Task capture = Task.Run(() => Assert.ThrowsExactly<InvalidOperationException>(() =>
            store.CaptureVisualLeases(CancellationToken.None, out _)));
        Assert.IsTrue(entered.Wait(TimeSpan.FromSeconds(10)));
        var closeEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task close = Task.Run(async () =>
        {
            closeEntered.SetResult();
            await store.CloseAsync();
        });
        await closeEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.IsFalse(close.IsCompleted);
        release.Set();
        await capture.WaitAsync(TimeSpan.FromSeconds(10));
        await close.WaitAsync(TimeSpan.FromSeconds(10));
        await visual.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.AreEqual(0, store.Statistics.ActiveLeases);
        AssertVisualPoolDrained(visual);
    }

    private static BScanTraceStore CreateIndexedStore(int columns,
        BoundedBScanVisualBufferPool visual)
    {
        var store = new BScanTraceStore(2,
            new BScanOptions(traceMemoryBudgetBytes: 4096, maximumColumns: columns,
                minimumColumns: columns, indexBucketSamples: 2),
            new DeterministicShortPool(), visual);
        for (ulong sequence = 0; sequence < (ulong)columns; sequence++)
        {
            BScanTraceIdentity identity = Identity(sequence);
            short first = checked((short)(sequence + 1));
            short second = checked((short)(sequence + 2));
            Commit(store, identity, [first, second]);
            Assert.IsTrue(store.CommitIndex(identity, 1, [new(first, 0, second, 1)]));
        }
        return store;
    }

    private static void AssertVisualPoolDrained(BoundedBScanVisualBufferPool visual)
    {
        BScanVisualMemorySnapshot snapshot = visual.Snapshot;
        Assert.AreEqual(0, snapshot.ActiveOwners);
        Assert.AreEqual(0, snapshot.RetainedBuffers);
        Assert.AreEqual(0L, snapshot.OwnedBytes);
        Assert.AreEqual(0L, snapshot.ActiveBytes);
        Assert.AreEqual(0L, snapshot.RetainedBytes);
    }

    private static void Commit(BScanTraceStore store, BScanTraceIdentity identity, short[] samples)
    {
        var reservation = store.BeginWrite(samples.Length);
        Assert.AreEqual(StoreWriteKind.Accepted, reservation.Kind);
        samples.CopyTo(reservation.Destination);
        var metadata = new ConventionalUtFrameMetadata(identity.SourceId, identity.RunId,
            new(identity.PhysicalChannelId, samples.Length, 10_000_000), DateTimeOffset.UnixEpoch);
        var descriptor = new BScanTraceDescriptor(metadata, identity, identity.AcquisitionIndex, TimeSpan.Zero,
            samples.Length, 50_000_000, 0, null, 0, 0);
        Assert.AreEqual(StoreWriteKind.Accepted, reservation.Commit(descriptor).Kind);
    }

    private static BScanTraceIdentity Identity(ulong sequence) => new(new UtSourceId("bscan"),
        new AcquisitionRunId(Guid.Parse("10000000-0000-0000-0000-000000000001")), default, sequence, sequence);
}
