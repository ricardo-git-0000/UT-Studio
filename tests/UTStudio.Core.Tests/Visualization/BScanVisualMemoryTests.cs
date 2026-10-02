using System.Buffers;
using UTStudio.Visualization.Core;

namespace UTStudio.Core.Tests.Visualization;

[TestClass]
public sealed class BScanVisualMemoryTests
{
    private const long Limit = 16L * 1024 * 1024;

    [TestMethod]
    public async Task TwoConcurrentCallersAreAdmittedLinearlyWhenExactCapacityFits()
    {
        var pool = new BoundedBScanVisualBufferPool(Limit);
        using var start = new Barrier(3);
        Task<IBScanVisualBufferOwner<byte>> first = Task.Run(() =>
        {
            start.SignalAndWait();
            return pool.Rent<byte>(4 * 1024 * 1024, BScanVisualBufferKind.Raster);
        });
        Task<IBScanVisualBufferOwner<byte>> second = Task.Run(() =>
        {
            start.SignalAndWait();
            return pool.Rent<byte>(4 * 1024 * 1024, BScanVisualBufferKind.Raster);
        });
        start.SignalAndWait();
        IBScanVisualBufferOwner<byte>[] owners = await Task.WhenAll(first, second)
            .WaitAsync(TimeSpan.FromSeconds(10));
        try
        {
            BScanVisualMemorySnapshot snapshot = pool.Snapshot;
            Assert.AreEqual(8L * 1024 * 1024, snapshot.OwnedBytes);
            Assert.AreEqual(snapshot.OwnedBytes, snapshot.ActiveBytes + snapshot.RetainedBytes);
            Assert.IsLessThanOrEqualTo(Limit, snapshot.MaximumOwnedBytes);
            Assert.AreEqual(2, snapshot.ActiveOwners);
        }
        finally
        {
            foreach (IBScanVisualBufferOwner<byte> owner in owners) { owner.Dispose(); }
        }
        await pool.DisposeAsync();
        AssertDrained(pool);
    }

    [TestMethod]
    public async Task CancellationAfterAcquireRollsBackTheReservationAndAllowsRetry()
    {
        var pool = new BoundedBScanVisualBufferPool(Limit);
        using var cancellation = new CancellationTokenSource();
        pool.AfterBackingStorageAcquired = _ =>
        {
            cancellation.Cancel();
            cancellation.Token.ThrowIfCancellationRequested();
        };
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => Task.Run(() =>
            pool.Rent<byte>(1024, BScanVisualBufferKind.Raster)));
        AssertDrained(pool);
        pool.AfterBackingStorageAcquired = null;
        using IMemoryOwner<byte> admitted = pool.Rent<byte>(1024, BScanVisualBufferKind.Raster);
        admitted.Dispose();
        await pool.DisposeAsync();
        AssertDrained(pool);
    }

    [TestMethod]
    public async Task FailureBetweenAcquireAndTransferCleansUpAndAllowsRetry()
    {
        var pool = new BoundedBScanVisualBufferPool(Limit);
        pool.AfterBackingStorageAcquired = _ => throw new InjectedFailureException();
        Assert.ThrowsExactly<InjectedFailureException>(() =>
            pool.Rent<int>(4097, BScanVisualBufferKind.KernelIndices));
        AssertDrained(pool);
        pool.AfterBackingStorageAcquired = null;
        using IBScanVisualBufferOwner<int> admitted =
            pool.Rent<int>(4097, BScanVisualBufferKind.KernelIndices);
        Assert.AreEqual(8192, admitted.Capacity);
        admitted.Dispose();
        await pool.DisposeAsync();
        AssertDrained(pool);
    }

    [TestMethod]
    public async Task ExactLimitIsAdmittedAndOneByteOverIsRejectedBeforeAllocation()
    {
        var pool = new BoundedBScanVisualBufferPool(Limit);
        using IBScanVisualBufferOwner<byte> exact =
            pool.Rent<byte>((int)Limit, BScanVisualBufferKind.Raster);
        Assert.AreEqual(Limit, pool.Snapshot.OwnedBytes);
        Assert.ThrowsExactly<InvalidOperationException>(() =>
            pool.Rent<byte>((int)Limit + 1, BScanVisualBufferKind.Raster));
        Assert.AreEqual(Limit, pool.Snapshot.MaximumOwnedBytes);
        exact.Dispose();
        await pool.DisposeAsync();
        AssertDrained(pool);
    }

    [TestMethod]
    public async Task DimensionOverflowDoesNotAlterTheLedger()
    {
        var pool = new BoundedBScanVisualBufferPool(Limit);
        Assert.ThrowsExactly<OverflowException>(() =>
            pool.Rent<double>(int.MaxValue, BScanVisualBufferKind.KernelWeights));
        AssertDrained(pool);
        await pool.DisposeAsync();
    }

    [TestMethod]
    public async Task DifferentBucketsRemainChargedWhileRetainedAndAreReusedWithoutGrowth()
    {
        var pool = new BoundedBScanVisualBufferPool(32 * 1024);
        pool.Rent<byte>(4097, BScanVisualBufferKind.Scratch).Dispose();
        pool.Rent<byte>(8193, BScanVisualBufferKind.Scratch).Dispose();
        BScanVisualMemorySnapshot fragmented = pool.Snapshot;
        Assert.AreEqual(24L * 1024, fragmented.OwnedBytes);
        Assert.AreEqual(fragmented.OwnedBytes, fragmented.RetainedBytes);
        Assert.AreEqual(2, fragmented.RetainedBuffers);
        long maximum = fragmented.MaximumOwnedBytes;
        pool.Rent<byte>(4097, BScanVisualBufferKind.Scratch).Dispose();
        Assert.AreEqual(maximum, pool.Snapshot.MaximumOwnedBytes);
        using IBScanVisualBufferOwner<byte> large =
            pool.Rent<byte>(20_000, BScanVisualBufferKind.Raster);
        Assert.AreEqual(32L * 1024, pool.Snapshot.OwnedBytes,
            "Retained buckets must be evicted when a new active bucket needs their capacity.");
        large.Dispose();
        await pool.DisposeAsync();
        AssertDrained(pool);
    }

    [TestMethod]
    public async Task BucketKeyChurnEvictsRetainedEntriesAndDoesNotCausePermanentExhaustion()
    {
        var pool = new BoundedBScanVisualBufferPool(1024 * 1024);
        foreach (BScanVisualBufferKind kind in Enum.GetValues<BScanVisualBufferKind>())
        {
            for (int exponent = 0; exponent < 5; exponent++)
            {
                pool.Rent<byte>(1 << exponent, kind).Dispose();
            }
        }
        Assert.IsLessThanOrEqualTo(32, pool.Snapshot.RetainedBuffers);
        using IBScanVisualBufferOwner<byte> admitted =
            pool.Rent<byte>(32 * 1024, BScanVisualBufferKind.Raster);
        Assert.IsLessThanOrEqualTo(1024L * 1024, pool.Snapshot.MaximumOwnedBytes);
        admitted.Dispose();
        await pool.DisposeAsync();
        AssertDrained(pool);
    }

    [TestMethod]
    public async Task CloseDropsRetainedBuffersButWaitsForBorrowedOwner()
    {
        var pool = new BoundedBScanVisualBufferPool(Limit);
        pool.Rent<byte>(1024, BScanVisualBufferKind.Scratch).Dispose();
        IBScanVisualBufferOwner<byte> borrowed =
            pool.Rent<byte>(2048, BScanVisualBufferKind.Raster);
        ValueTask close = pool.DisposeAsync();
        Assert.IsFalse(close.IsCompleted);
        BScanVisualMemorySnapshot closing = pool.Snapshot;
        Assert.AreEqual(0L, closing.RetainedBytes);
        Assert.AreEqual(borrowed.CapacityBytes, closing.ActiveBytes);
        borrowed.Dispose();
        await close.AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        AssertDrained(pool);
    }

    [TestMethod]
    public async Task ConcurrentFailuresNeverMakeCountersNegativeOrIncreaseMaximumPastLimit()
    {
        var pool = new BoundedBScanVisualBufferPool(Limit);
        using var start = new Barrier(3);
        pool.AfterBackingStorageAcquired = _ => throw new InjectedFailureException();
        Task[] failures = Enumerable.Range(0, 2).Select(_ => Task.Run(() =>
        {
            start.SignalAndWait();
            Assert.ThrowsExactly<InjectedFailureException>(() =>
                pool.Rent<byte>(4 * 1024 * 1024, BScanVisualBufferKind.Raster));
        })).ToArray();
        start.SignalAndWait();
        await Task.WhenAll(failures).WaitAsync(TimeSpan.FromSeconds(10));
        BScanVisualMemorySnapshot snapshot = pool.Snapshot;
        AssertDrained(pool);
        Assert.IsGreaterThanOrEqualTo(0L, snapshot.MaximumOwnedBytes);
        Assert.IsLessThanOrEqualTo(Limit, snapshot.MaximumOwnedBytes);
        await pool.DisposeAsync();
    }

    private static void AssertDrained(BoundedBScanVisualBufferPool pool)
    {
        BScanVisualMemorySnapshot snapshot = pool.Snapshot;
        Assert.AreEqual(0L, snapshot.OwnedBytes);
        Assert.AreEqual(0L, snapshot.ActiveBytes);
        Assert.AreEqual(0L, snapshot.RetainedBytes);
        Assert.AreEqual(0, snapshot.ActiveOwners);
        Assert.AreEqual(0, snapshot.RetainedBuffers);
    }

    private sealed class InjectedFailureException : Exception;
}
