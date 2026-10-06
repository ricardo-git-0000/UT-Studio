using UTStudio.Contracts.Inspection;
using UTStudio.Inspection.Core;

namespace UTStudio.Core.Tests.Inspection;

[TestClass]
public sealed class InMemoryInspectionDataStoreTests
{
    [TestMethod]
    public async Task CommitCopiesBorrowedMemoryAndAdvancesRevisionOnce()
    {
        await using var store = Store(1024);
        short[] caller = [1, 2, 3, 4];
        InspectionAppendResult result = await store.AppendAsync(Input(0, caller));
        caller[0] = 99;
        InspectionTraceReadResult read = await store.ReadAsync(Identity(0));
        using IInspectionTraceLease lease = read.Lease!;
        Assert.AreEqual(InspectionAppendStatus.Committed, result.Status);
        Assert.IsTrue(result.WasAccepted);
        Assert.IsTrue(result.IsCommitted);
        Assert.AreEqual<ulong>(1, result.Revision.Value);
        CollectionAssert.AreEqual(new short[] { 1, 2, 3, 4 }, Copy(lease.Samples));
        Assert.IsFalse(result.Durable);
    }

    [TestMethod]
    public async Task ReservationMovesToOwnedCapacityAndDiagnosticsBalance()
    {
        await using var store = Store(1024);
        store.TestHooks = new()
        {
            Allocate = count =>
            {
                Assert.AreEqual(count * sizeof(short), store.Diagnostics.ReservedSampleCapacityBytes);
                Assert.AreEqual(0L, store.Diagnostics.OwnedSampleCapacityBytes);
                Assert.IsLessThanOrEqualTo(store.Diagnostics.SampleCapacityBudgetBytes,
                    store.Diagnostics.ReservedSampleCapacityBytes + store.Diagnostics.OwnedSampleCapacityBytes);
                return new short[count];
            }
        };
        await store.AppendAsync(Input(0, [1, 2, 3, 4]));
        Assert.AreEqual(0L, store.Diagnostics.ReservedSampleCapacityBytes);
        Assert.AreEqual(8L, store.Diagnostics.OwnedSampleCapacityBytes);
        Assert.AreEqual(8L, store.Diagnostics.LogicalSampleBytes);
        Assert.IsLessThanOrEqualTo(store.Diagnostics.SampleCapacityBudgetBytes,
            store.Diagnostics.ReservedSampleCapacityBytes + store.Diagnostics.OwnedSampleCapacityBytes);
        Assert.AreEqual(0, store.Diagnostics.ActiveWriters);
        Assert.AreEqual(1, store.Diagnostics.MaximumActiveWriters);
    }

    [TestMethod]
    public async Task DuplicateAndConflictsNeverOverwriteOrAdvanceRevision()
    {
        await using var store = Store(1024);
        Assert.AreEqual(InspectionAppendStatus.Committed, (await store.AppendAsync(Input(0, [1, 2, 3, 4]))).Status);
        Assert.AreEqual(InspectionAppendStatus.Duplicate, (await store.AppendAsync(Input(0, [1, 2, 3, 4]))).Status);
        Assert.AreEqual(InspectionAppendStatus.Conflict, (await store.AppendAsync(Input(0, [1, 2, 3, 5]))).Status);
        Assert.AreEqual(InspectionAppendStatus.Conflict, (await store.AppendAsync(Input(0, [1, 2, 3, 4], sequence: 9))).Status);
        Assert.AreEqual<ulong>(1, store.Revision.Value);
        Assert.AreEqual<ulong>(1, store.Diagnostics.DuplicateCount);
        Assert.AreEqual<ulong>(2, store.Diagnostics.ConflictCount);
    }

    [TestMethod]
    public async Task ExactCapacitySucceedsAndOneTraceBeyondIsRejectedWithoutDataLoss()
    {
        await using var store = Store(8, maximumEntries: 2);
        Assert.AreEqual(InspectionAppendStatus.Committed, (await store.AppendAsync(Input(0, [1, 2, 3, 4]))).Status);
        InspectionAppendResult rejected = await store.AppendAsync(Input(1, [5]));
        Assert.AreEqual(InspectionAppendStatus.CapacityExceeded, rejected.Status);
        Assert.IsTrue(rejected.WasAccepted);
        Assert.AreEqual(8L, store.Diagnostics.OwnedSampleCapacityBytes);
        InspectionTraceReadResult read = await store.ReadAsync(Identity(0));
        Assert.AreEqual(InspectionReadStatus.Available, read.Status);
        read.Lease!.Dispose();
    }

    [TestMethod]
    public async Task CancellationBeforeCommitRollsBackReservationAndVisibility()
    {
        await using var store = Store(1024);
        using var cancellation = new CancellationTokenSource();
        store.TestHooks = new() { AfterCopy = cancellation.Cancel };
        InspectionAppendResult result = await store.AppendAsync(Input(0, [1, 2, 3, 4]), cancellation.Token);
        Assert.AreEqual(InspectionAppendStatus.Cancelled, result.Status);
        Assert.IsTrue(result.WasAccepted);
        Assert.AreEqual<ulong>(1, store.Diagnostics.CancelledAppends);
        Assert.AreEqual(0L, store.Diagnostics.OwnedSampleCapacityBytes);
        Assert.AreEqual(0L, store.Diagnostics.ReservedSampleCapacityBytes);
        Assert.AreEqual(InspectionReadStatus.NotAvailable, (await store.ReadAsync(Identity(0))).Status);
    }

    [TestMethod]
    public async Task CancellationAfterCommitStillReturnsCommitted()
    {
        await using var store = Store(1024);
        using var cancellation = new CancellationTokenSource();
        store.TestHooks = new() { AfterCommit = cancellation.Cancel };
        InspectionAppendResult result = await store.AppendAsync(Input(0, [1, 2, 3, 4]), cancellation.Token);
        Assert.AreEqual(InspectionAppendStatus.Committed, result.Status);
        Assert.AreEqual<ulong>(1, store.Revision.Value);
    }

    [TestMethod]
    public async Task AllocationFailureLeavesNoPartialEntry()
    {
        await using var store = Store(1024);
        store.TestHooks = new() { Allocate = _ => throw new OutOfMemoryException("injected") };
        InspectionAppendResult result = await store.AppendAsync(Input(0, [1, 2, 3, 4]));
        Assert.AreEqual(InspectionAppendStatus.Faulted, result.Status);
        Assert.AreEqual(0L, store.Diagnostics.OwnedSampleCapacityBytes);
        Assert.AreEqual(0L, store.Diagnostics.ReservedSampleCapacityBytes);
        Assert.AreEqual(0, store.Diagnostics.ActiveWriters);
        Assert.AreEqual<ulong>(0, store.Revision.Value);
    }

    [TestMethod]
    public async Task CopyFailureLeavesNoPartialEntryOrReservation()
    {
        await using var store = Store(1024);
        store.TestHooks = new() { Copy = (_, _) => throw new InvalidOperationException("copy") };
        Assert.AreEqual(InspectionAppendStatus.Faulted, (await store.AppendAsync(Input(0, [1, 2, 3, 4]))).Status);
        Assert.AreEqual(0L, store.Diagnostics.OwnedSampleCapacityBytes);
        Assert.AreEqual<ulong>(0, store.Revision.Value);
    }

    [TestMethod]
    public async Task MultipleCommitsAdvanceRevisionExactlyOnceEach()
    {
        await using var store = Store(1024);
        for (ulong index = 0; index < 5; index++)
        { Assert.AreEqual(index + 1, (await store.AppendAsync(Input(index, [1, 2]))).Revision.Value); }
        Assert.AreEqual<ulong>(5, store.Diagnostics.CommittedTraces);
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task PublicationFailureRollsBackEveryIndexAndBudget(bool afterPrimary)
    {
        await using var store = Store(1024);
        Action failure = () => throw new InvalidOperationException("injected publication failure");
        store.TestHooks = afterPrimary
            ? new() { AfterPrimaryIndex = failure }
            : new() { AfterStreamIndex = failure };
        InspectionAppendResult result = await store.AppendAsync(Input(0, [1, 2, 3, 4]));
        Assert.AreEqual(InspectionAppendStatus.Faulted, result.Status);
        Assert.AreEqual<ulong>(0, store.Revision.Value);
        Assert.AreEqual(0L, store.Diagnostics.OwnedSampleCapacityBytes);
        Assert.AreEqual(0, store.Diagnostics.CommittedEntries);
        Assert.AreEqual(InspectionReadStatus.NotAvailable, (await store.ReadAsync(Identity(0))).Status);
    }

    [TestMethod]
    public async Task ChunkTransitionFailureDoesNotSealOrReplacePreviousChunk()
    {
        await using var store = Store(1024, targetChunkBytes: 8);
        Assert.AreEqual(InspectionAppendStatus.Committed, (await store.AppendAsync(Input(0, [1, 2, 3, 4]))).Status);
        store.TestHooks = new() { BeforeChunkAdd = () => throw new InvalidOperationException("chunk") };
        Assert.AreEqual(InspectionAppendStatus.Faulted, (await store.AppendAsync(Input(1, [5, 6, 7, 8]))).Status);
        Assert.AreEqual(1, store.Diagnostics.ActiveChunks);
        Assert.AreEqual(0, store.Diagnostics.SealedChunks);
        Assert.AreEqual<ulong>(1, store.Revision.Value);
    }

    [TestMethod]
    public async Task ConcurrentSameIdentityProducesOneCommitAndOneDuplicate()
    {
        await using var store = Store(1024);
        var start = new Barrier(3);
        Task<InspectionAppendResult> first = Task.Run(async () => { start.SignalAndWait(); return await store.AppendAsync(Input(0, [1, 2, 3, 4])); });
        Task<InspectionAppendResult> second = Task.Run(async () => { start.SignalAndWait(); return await store.AppendAsync(Input(0, [1, 2, 3, 4])); });
        start.SignalAndWait();
        InspectionAppendStatus[] statuses = [(await first).Status, (await second).Status];
        CollectionAssert.AreEquivalent(new[] { InspectionAppendStatus.Committed, InspectionAppendStatus.Duplicate }, statuses);
        Assert.AreEqual<ulong>(1, store.Revision.Value);
    }

    [TestMethod]
    public async Task ConcurrentSameIdentityWithDifferentPayloadProducesOneCommitAndOneConflict()
    {
        await using var store = Store(1024);
        var start = new Barrier(3);
        Task<InspectionAppendResult> first = Task.Run(async () => { start.SignalAndWait(); return await store.AppendAsync(Input(0, [1, 2])); });
        Task<InspectionAppendResult> second = Task.Run(async () => { start.SignalAndWait(); return await store.AppendAsync(Input(0, [3, 4])); });
        start.SignalAndWait();
        InspectionAppendStatus[] statuses = [(await first).Status, (await second).Status];
        CollectionAssert.AreEquivalent(new[] { InspectionAppendStatus.Committed, InspectionAppendStatus.Conflict }, statuses);
        Assert.AreEqual<ulong>(1, store.Revision.Value);
    }

    [TestMethod]
    public async Task MaximumEntriesRejectsWithSampleCapacityRemaining()
    {
        await using var store = Store(1024, maximumEntries: 1);
        Assert.AreEqual(InspectionAppendStatus.Committed, (await store.AppendAsync(Input(0, [1]))).Status);
        Assert.AreEqual(InspectionAppendStatus.CapacityExceeded, (await store.AppendAsync(Input(1, [2]))).Status);
        Assert.AreEqual(1, store.Diagnostics.CommittedEntries);
        Assert.AreEqual(1, store.Diagnostics.MaximumEntries);
        Assert.IsLessThan(store.Diagnostics.SampleCapacityBudgetBytes, store.Diagnostics.OwnedSampleCapacityBytes);
    }

    [TestMethod]
    public async Task ConcurrentDifferentIdentitiesNeverOversubscribeBudget()
    {
        await using var store = Store(8, maximumEntries: 2);
        var start = new Barrier(3);
        Task<InspectionAppendResult> first = Task.Run(async () => { start.SignalAndWait(); return await store.AppendAsync(Input(0, [1, 2, 3, 4])); });
        Task<InspectionAppendResult> second = Task.Run(async () => { start.SignalAndWait(); return await store.AppendAsync(Input(1, [5, 6, 7, 8])); });
        start.SignalAndWait();
        InspectionAppendStatus[] statuses = [(await first).Status, (await second).Status];
        CollectionAssert.AreEquivalent(new[] { InspectionAppendStatus.Committed, InspectionAppendStatus.CapacityExceeded }, statuses);
        Assert.AreEqual(8L, store.Diagnostics.MaximumOwnedSampleCapacityBytes);
        Assert.IsLessThanOrEqualTo(store.Diagnostics.OwnedSampleCapacityBytes,
            store.Diagnostics.SampleCapacityBudgetBytes);
    }

    [TestMethod]
    public async Task RangeIsBoundedOrderedAndCapturesRevision()
    {
        await using var store = Store(4096);
        await store.AppendAsync(Input(3, [3, 3, 3, 3]));
        await store.AppendAsync(Input(1, [1, 1, 1, 1]));
        await store.AppendAsync(Input(2, [2, 2, 2, 2]));
        InspectionRangeReadResult result = await store.ReadRangeAsync(new(InspectionIdentityTests.Stream(), 1, 2));
        Assert.AreEqual(InspectionReadStatus.Available, result.Status);
        Assert.AreEqual<ulong>(3, result.Revision.Value);
        CollectionAssert.AreEqual(new ulong[] { 1, 2 }, result.Items.Select(x => x.Identity.AcquisitionIndex).ToArray());
        foreach (InspectionRangeItem item in result.Items) { item.Lease!.Dispose(); }
    }

    [TestMethod]
    public async Task RangeReportsAvailableAndNotAvailablePositionsWithoutInventingGaps()
    {
        await using var store = Store(4096);
        await store.AppendAsync(Input(1, [1, 1]));
        await store.AppendAsync(Input(3, [3, 3]));
        InspectionRangeReadResult result = await store.ReadRangeAsync(new(InspectionIdentityTests.Stream(), 1, 3));
        CollectionAssert.AreEqual(
            new[] { InspectionReadStatus.Available, InspectionReadStatus.NotAvailable, InspectionReadStatus.Available },
            result.Items.Select(x => x.Status).ToArray());
        foreach (InspectionRangeItem item in result.Items) { item.Lease?.Dispose(); }
    }

    [TestMethod]
    public async Task MissingTraceIsNotAvailableRatherThanInferredGap()
    {
        await using var store = Store(1024);
        Assert.AreEqual(InspectionReadStatus.NotAvailable, (await store.ReadAsync(Identity(99))).Status);
        InspectionRangeReadResult range = await store.ReadRangeAsync(new(InspectionIdentityTests.Stream(), 99, 4));
        Assert.AreEqual(InspectionReadStatus.NotAvailable, range.Status);
        Assert.HasCount(4, range.Items);
        Assert.IsTrue(range.Items.All(x => x.Status == InspectionReadStatus.NotAvailable));
    }

    [TestMethod]
    public async Task LeaseIsStableDisposeIsIdempotentAndPostDisposeThrows()
    {
        await using var store = Store(1024);
        await store.AppendAsync(Input(0, [1, 2, 3, 4]));
        IInspectionTraceLease lease = (await store.ReadAsync(Identity(0))).Lease!;
        Assert.AreEqual(1, store.Diagnostics.ActiveLeases);
        Parallel.Invoke(lease.Dispose, lease.Dispose);
        Assert.AreEqual(0, store.Diagnostics.ActiveLeases);
        Assert.ThrowsExactly<ObjectDisposedException>(() => _ = lease.Samples);
    }

    [TestMethod]
    public async Task LeaseExposesOnlyReadOnlySampleAbstraction()
    {
        await using var store = Store(1024);
        await store.AppendAsync(Input(0, [1, 2, 3, 4]));
        using IInspectionTraceLease lease = (await store.ReadAsync(Identity(0))).Lease!;
        Assert.AreEqual(4, lease.Samples.Count);
        Assert.AreEqual((short)1, lease.Samples[0]);
        CollectionAssert.AreEqual(new short[] { 1, 2, 3, 4 }, Copy(lease.Samples));
    }

    [TestMethod]
    public async Task MultipleReadersOwnIndependentLeases()
    {
        await using var store = Store(1024);
        await store.AppendAsync(Input(0, [1, 2]));
        IInspectionTraceLease first = (await store.ReadAsync(Identity(0))).Lease!;
        IInspectionTraceLease second = (await store.ReadAsync(Identity(0))).Lease!;
        Assert.AreEqual(2, store.Diagnostics.ActiveLeases);
        first.Dispose();
        Assert.AreEqual((short)2, second.Samples[1]);
        second.Dispose();
        Assert.AreEqual(0, store.Diagnostics.ActiveLeases);
        Assert.AreEqual(2, store.Diagnostics.MaximumActiveLeases);
    }

    [TestMethod]
    public async Task RangeConstructionFailureRollsBackEveryAcquiredLease()
    {
        await using var store = Store(4096);
        for (ulong index = 0; index < 3; index++) { await store.AppendAsync(Input(index, [1, 2])); }
        for (int failureOffset = 0; failureOffset < 3; failureOffset++)
        {
            int captured = failureOffset;
            store.TestHooks = new()
            {
                BeforeRangeLease = offset =>
                {
                    if (offset == captured) { throw new InvalidOperationException($"range-{captured}"); }
                }
            };
            InspectionRangeAcquisitionException error = await Assert.ThrowsExactlyAsync<InspectionRangeAcquisitionException>(
                () => store.ReadRangeAsync(new(InspectionIdentityTests.Stream(), 0, 3)).AsTask());
            Assert.IsInstanceOfType<InvalidOperationException>(error.PrimaryException);
            Assert.IsEmpty(error.CleanupExceptions);
            Assert.AreEqual(0, store.Diagnostics.ActiveLeases);
        }
    }

    [TestMethod]
    public async Task RangeRollbackPreservesPrimaryAndSecondaryCleanupErrors()
    {
        await using var store = Store(4096);
        await store.AppendAsync(Input(0, [1, 2]));
        store.TestHooks = new()
        {
            BeforeRangeLease = offset => { if (offset == 1) { throw new InvalidOperationException("primary"); } },
            BeforeRangeRollbackDispose = _ => throw new ApplicationException("secondary")
        };
        InspectionRangeAcquisitionException error = await Assert.ThrowsExactlyAsync<InspectionRangeAcquisitionException>(
            () => store.ReadRangeAsync(new(InspectionIdentityTests.Stream(), 0, 2)).AsTask());
        Assert.AreEqual("primary", error.PrimaryException.Message);
        Assert.HasCount(1, error.CleanupExceptions);
        Assert.AreEqual("secondary", error.CleanupExceptions[0].Message);
        Assert.AreEqual(0, store.Diagnostics.ActiveLeases);
    }

    [TestMethod]
    public async Task FailureAfterLeaseCreationReleasesTheUntransferredLeaseAtEveryPosition()
    {
        await using var store = Store(4096);
        for (ulong index = 0; index < 3; index++) { await store.AppendAsync(Input(index, [1, 2])); }
        for (int failureOffset = 0; failureOffset < 3; failureOffset++)
        {
            int captured = failureOffset;
            store.TestHooks = new()
            {
                AfterRangeLeaseCreated = offset =>
                {
                    if (offset == captured) { throw new InvalidOperationException("after-create"); }
                }
            };
            await Assert.ThrowsExactlyAsync<InspectionRangeAcquisitionException>(
                () => store.ReadRangeAsync(new(InspectionIdentityTests.Stream(), 0, 3)).AsTask());
            Assert.AreEqual(0, store.Diagnostics.ActiveLeases);
        }
    }

    [TestMethod]
    public async Task LeaseReadAdmittedBeforeDisposeCompletesAndLaterReadsFail()
    {
        var store = Store(1024);
        await store.AppendAsync(Input(0, [7, 8]));
        IInspectionTraceLease lease = (await store.ReadAsync(Identity(0))).Lease!;
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        store.TestHooks = new() { BeforeLeaseRead = () => { entered.Set(); Assert.IsTrue(release.Wait(TimeSpan.FromSeconds(5))); } };
        Task<short> read = Task.Run(() => lease.Samples[0]);
        Assert.IsTrue(entered.Wait(TimeSpan.FromSeconds(5)));
        Task dispose = Task.Run(lease.Dispose);
        Assert.IsFalse(dispose.IsCompleted);
        release.Set();
        Assert.AreEqual((short)7, await read.WaitAsync(TimeSpan.FromSeconds(5)));
        await dispose.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.ThrowsExactly<ObjectDisposedException>(() => _ = lease.Samples);
        await store.DisposeAsync();
    }

    [TestMethod]
    public async Task CloseWaitsForAdmittedWriterBeforeBlockingNewLeases()
    {
        var store = Store(1024);
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        store.TestHooks = new()
        {
            BeforeCopy = () =>
            { entered.Set(); Assert.IsTrue(release.Wait(TimeSpan.FromSeconds(5))); }
        };
        Task<InspectionAppendResult> append = Task.Run(async () => await store.AppendAsync(Input(0, [1, 2])));
        Assert.IsTrue(entered.Wait(TimeSpan.FromSeconds(5)));
        Task close = store.DisposeAsync().AsTask();
        Assert.IsFalse(close.IsCompleted);
        release.Set();
        Assert.AreEqual(InspectionAppendStatus.Committed, (await append).Status);
        await close.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [TestMethod]
    public async Task CloseDrainsWriterAlreadyAdmittedBehindAnotherWriterAndRejectsLaterWriter()
    {
        var store = Store(1024);
        using var firstEntered = new ManualResetEventSlim();
        using var releaseFirst = new ManualResetEventSlim();
        using var secondEntered = new ManualResetEventSlim();
        using var releaseSecond = new ManualResetEventSlim();
        using var twoAdmitted = new CountdownEvent(2);
        int copies = 0;
        store.TestHooks = new()
        {
            AfterWriterAdmitted = () => twoAdmitted.Signal(),
            BeforeCopy = () =>
            {
                if (Interlocked.Increment(ref copies) == 1)
                { firstEntered.Set(); Assert.IsTrue(releaseFirst.Wait(TimeSpan.FromSeconds(5))); }
                else
                { secondEntered.Set(); Assert.IsTrue(releaseSecond.Wait(TimeSpan.FromSeconds(5))); }
            }
        };
        Task<InspectionAppendResult> first = Task.Run(async () => await store.AppendAsync(Input(0, [1, 2])));
        Assert.IsTrue(firstEntered.Wait(TimeSpan.FromSeconds(5)));
        Task<InspectionAppendResult> second = Task.Run(async () => await store.AppendAsync(Input(1, [3, 4])));
        Assert.IsTrue(twoAdmitted.Wait(TimeSpan.FromSeconds(5)));
        Assert.AreEqual(2, store.Diagnostics.ActiveWriters);
        Task close = store.DisposeAsync().AsTask();
        Assert.AreEqual(2, store.Diagnostics.WritersPendingDuringClose);
        InspectionAppendResult late = await store.AppendAsync(Input(2, [5, 6]));
        Assert.AreEqual(InspectionAppendStatus.Closed, late.Status);
        Assert.IsFalse(late.WasAccepted);
        releaseFirst.Set();
        Assert.AreEqual(InspectionAppendStatus.Committed, (await first.WaitAsync(TimeSpan.FromSeconds(5))).Status);
        Assert.IsTrue(secondEntered.Wait(TimeSpan.FromSeconds(5)));
        Assert.IsFalse(close.IsCompleted);
        releaseSecond.Set();
        InspectionAppendResult secondResult = await second.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.AreEqual(InspectionAppendStatus.Committed, secondResult.Status);
        Assert.IsTrue(secondResult.WasAccepted);
        await close.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.AreEqual(0, store.Diagnostics.ActiveWriters);
        Assert.AreEqual<ulong>(1, store.Diagnostics.PreAdmissionClosedRejections);
    }

    [TestMethod]
    public async Task CloseWaitsForLeaseAndRejectsNewWork()
    {
        var store = Store(1024);
        await store.AppendAsync(Input(0, [1, 2, 3, 4]));
        IInspectionTraceLease lease = (await store.ReadAsync(Identity(0))).Lease!;
        Task close = store.DisposeAsync().AsTask();
        await Task.Yield();
        Assert.IsFalse(close.IsCompleted);
        Assert.AreEqual(InspectionAppendStatus.Closed, (await store.AppendAsync(Input(1, [1]))).Status);
        lease.Dispose();
        await close.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.IsTrue(store.Diagnostics.IsClosed);
        Assert.AreEqual(0L, store.Diagnostics.OwnedSampleCapacityBytes);
        Assert.AreSame(close, store.DisposeAsync().AsTask());
    }

    [TestMethod]
    public async Task ConcurrentDisposeAsyncCallsShareInProgressClose()
    {
        var store = Store(1024);
        await store.AppendAsync(Input(0, [1, 2]));
        IInspectionTraceLease lease = (await store.ReadAsync(Identity(0))).Lease!;
        Task<Task>[] callers = Enumerable.Range(0, 8)
            .Select(_ => Task.Factory.StartNew(() => store.DisposeAsync().AsTask())).ToArray();
        await Task.WhenAll(callers).WaitAsync(TimeSpan.FromSeconds(5));
        Task close = callers[0].Result;
        Assert.IsTrue(callers.All(call => ReferenceEquals(close, call.Result)));
        Assert.IsFalse(close.IsCompleted);
        lease.Dispose();
        await close.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [TestMethod]
    public async Task RangeCanBeDisposedRepeatedlyAndConcurrently()
    {
        await using var store = Store(1024);
        await store.AppendAsync(Input(0, [1, 2]));
        await store.AppendAsync(Input(1, [3, 4]));
        InspectionRangeReadResult range = await store.ReadRangeAsync(
            new(InspectionIdentityTests.Stream(), 0, 2));
        Parallel.Invoke(range.Dispose, range.Dispose, range.Dispose);
        Assert.AreEqual(0, store.Diagnostics.ActiveLeases);
        Assert.ThrowsExactly<ObjectDisposedException>(() => _ = range.Items[0].Lease!.Samples);
    }

    [TestMethod]
    public async Task ChunksSealByCapacityAndClose()
    {
        var store = Store(4096, targetChunkBytes: 8);
        await store.AppendAsync(Input(0, [1, 2, 3, 4]));
        await store.AppendAsync(Input(1, [1, 2, 3, 4]));
        Assert.AreEqual(1, store.Diagnostics.SealedChunks);
        Assert.AreEqual(1, store.Diagnostics.ActiveChunks);
        await store.DisposeAsync();
        Assert.AreEqual(0, store.Diagnostics.ActiveChunks);
    }

    [TestMethod]
    public async Task OversizedTraceUsesDedicatedChunkAndNeverOverwrites()
    {
        var store = Store(4096, targetChunkBytes: 4);
        await store.AppendAsync(Input(0, [1, 2, 3, 4]));
        Assert.AreEqual(1, store.Diagnostics.ActiveChunks);
        await store.AppendAsync(Input(1, [5, 6, 7, 8]));
        Assert.AreEqual(1, store.Diagnostics.SealedChunks);
        using IInspectionTraceLease first = (await store.ReadAsync(Identity(0))).Lease!;
        CollectionAssert.AreEqual(new short[] { 1, 2, 3, 4 }, Copy(first.Samples));
        first.Dispose();
        await store.DisposeAsync();
    }

    [TestMethod]
    public async Task DedicatedChunkIsIsolatedBetweenNormalChunks()
    {
        var store = Store(4096, targetChunkBytes: 8);
        await store.AppendAsync(Input(0, [1, 2]));
        await store.AppendAsync(Input(1, [1, 2, 3, 4, 5]));
        Assert.AreEqual(1, store.Diagnostics.SealedChunks);
        await store.AppendAsync(Input(2, [3, 4]));
        Assert.AreEqual(2, store.Diagnostics.SealedChunks);
        Assert.AreEqual(1, store.Diagnostics.ActiveChunks);
        await store.DisposeAsync();
    }

    [TestMethod]
    public async Task IncompatibleEncodingSealsCurrentChunk()
    {
        var store = Store(4096, targetChunkBytes: 64);
        await store.AppendAsync(Input(0, [1, 2]));
        TraceDescriptor incompatible = InspectionIdentityTests.Descriptor(Identity(1), 1, 2,
            sampleEncoding: UtSampleEncoding.Unknown);
        await store.AppendAsync(new(incompatible, new short[] { 3, 4 }));
        Assert.AreEqual(1, store.Diagnostics.SealedChunks);
        Assert.AreEqual(1, store.Diagnostics.ActiveChunks);
        await store.DisposeAsync();
    }

    [TestMethod]
    public async Task EveryDescriptorMetadataDifferenceIsConflict()
    {
        await using var store = Store(4096);
        short[] samples = [1, 2];
        TraceIdentity identity = Identity(0);
        TraceDescriptor baseline = InspectionIdentityTests.Descriptor(identity, samples: 2);
        Assert.AreEqual(InspectionAppendStatus.Committed, (await store.AppendAsync(new(baseline, samples))).Status);
        TraceDescriptor[] variants =
        [
            InspectionIdentityTests.Descriptor(identity, samples: 2, clockQuality: UtClockQuality.Synchronized),
            InspectionIdentityTests.Descriptor(identity, samples: 2, sampleEncoding: UtSampleEncoding.Unknown),
            InspectionIdentityTests.Descriptor(identity, samples: 2, provenance: UtTraceProvenance.Simulator),
            InspectionIdentityTests.Descriptor(identity, samples: 2, traceQuality: UtTraceQuality.Good)
        ];
        foreach (TraceDescriptor variant in variants)
        { Assert.AreEqual(InspectionAppendStatus.Conflict, (await store.AppendAsync(new(variant, samples))).Status); }
        Assert.AreEqual<ulong>(4, store.Diagnostics.ConflictCount);
    }

    [TestMethod]
    public async Task EveryNewDescriptorMetadataValueCanProduceDuplicate()
    {
        await using var store = Store(4096);
        TraceDescriptor[] descriptors =
        [
            InspectionIdentityTests.Descriptor(Identity(10), samples: 2, clockQuality: UtClockQuality.Synchronized),
            InspectionIdentityTests.Descriptor(Identity(11), samples: 2, sampleEncoding: UtSampleEncoding.Unknown),
            InspectionIdentityTests.Descriptor(Identity(12), samples: 2, provenance: UtTraceProvenance.Hardware),
            InspectionIdentityTests.Descriptor(Identity(13), samples: 2, traceQuality: UtTraceQuality.Degraded)
        ];
        foreach (TraceDescriptor descriptor in descriptors)
        {
            var input = new InspectionTraceInput(descriptor, new short[] { 1, 2 });
            Assert.AreEqual(InspectionAppendStatus.Committed, (await store.AppendAsync(input)).Status);
            Assert.AreEqual(InspectionAppendStatus.Duplicate, (await store.AppendAsync(input)).Status);
        }
    }

    [TestMethod]
    public async Task DescriptorValueEqualityDefinesDuplicateIncludingSignedZero()
    {
        await using var store = Store(1024);
        TraceIdentity identity = Identity(0);
        TraceDescriptor positive = new(identity, 0, TimeSpan.Zero, DateTimeOffset.UnixEpoch, null,
            2, 10_000_000, +0.0, UTStudio.Domain.Acquisition.UtSignalMode.Rf);
        TraceDescriptor negative = new(identity, 0, TimeSpan.Zero, DateTimeOffset.UnixEpoch, null,
            2, 10_000_000, -0.0, UTStudio.Domain.Acquisition.UtSignalMode.Rf);
        Assert.AreEqual(positive, negative);
        await store.AppendAsync(new(positive, new short[] { 1, 2 }));
        Assert.AreEqual(InspectionAppendStatus.Duplicate,
            (await store.AppendAsync(new(negative, new short[] { 1, 2 }))).Status);
    }

    [TestMethod]
    public void AppendResultRejectsImpossibleAcceptanceCombinations()
    {
        Assert.IsFalse(default(InspectionAppendResult).IsCommitted);
        Assert.ThrowsExactly<ArgumentException>(() => new InspectionAppendResult(
            InspectionAppendStatus.Committed, new(1), false));
        Assert.ThrowsExactly<ArgumentException>(() => new InspectionAppendResult(
            InspectionAppendStatus.Closed, default, true));
        Assert.ThrowsExactly<ArgumentException>(() => new InspectionAppendResult(
            InspectionAppendStatus.Committed, default, true));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new InspectionAppendResult(
            (InspectionAppendStatus)99, default, true));
        _ = new InspectionAppendResult(InspectionAppendStatus.Closed, default, false);
        _ = new InspectionAppendResult(InspectionAppendStatus.Committed, new(1), true);
    }

    [TestMethod]
    public async Task CleanupFailureDoesNotClaimClosed()
    {
        var store = Store(1024);
        store.TestHooks = new() { BeforeCleanup = () => throw new InvalidOperationException("cleanup") };
        await store.AppendAsync(Input(0, [1, 2, 3, 4]));
        InspectionStoreCloseException error = await Assert.ThrowsExactlyAsync<InspectionStoreCloseException>(
            () => store.DisposeAsync().AsTask());
        Assert.AreEqual("cleanup-failed", error.PrimaryCode);
        Assert.IsEmpty(error.SecondaryCodes);
        Assert.IsFalse(store.Diagnostics.IsClosed);
        Assert.AreSame(store.DisposeAsync().AsTask(), store.DisposeAsync().AsTask());
    }

    [TestMethod]
    public void RangeMaximumIsValidated()
    {
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new InspectionRangeQuery(InspectionIdentityTests.Stream(), 0, 0));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new InspectionRangeQuery(InspectionIdentityTests.Stream(), 0, -1));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new InspectionRangeQuery(InspectionIdentityTests.Stream(), 0, 4097));
        _ = new InspectionRangeQuery(InspectionIdentityTests.Stream(), ulong.MaxValue, 1);
        Assert.ThrowsExactly<OverflowException>(() => new InspectionRangeQuery(InspectionIdentityTests.Stream(), ulong.MaxValue, 2));
    }

    [TestMethod]
    public async Task DefaultRangeQueryIsRejectedAtProviderBoundary()
    {
        await using var store = Store(1024);
        Assert.ThrowsExactly<ArgumentException>(() => store.ReadRangeAsync(default));
    }

    private static InMemoryInspectionDataStore Store(long budget, int maximumEntries = 100,
        int targetChunkBytes = 64) => new(InspectionIdentityTests.Inspection, InspectionIdentityTests.Run,
            new(budget, maximumEntries, targetChunkBytes));

    private static TraceIdentity Identity(ulong index) => new(InspectionIdentityTests.Stream(), index);
    private static InspectionTraceInput Input(ulong index, short[] samples, ulong? sequence = null) =>
        new(InspectionIdentityTests.Descriptor(Identity(index), sequence ?? index, samples.Length), samples);

    private static short[] Copy(IReadOnlyUtSamples samples)
    {
        var result = new short[samples.Count];
        samples.CopyTo(result);
        return result;
    }
}
