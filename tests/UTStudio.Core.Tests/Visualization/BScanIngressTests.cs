using UTStudio.Domain.Acquisition;
using UTStudio.Visualization.Core;

namespace UTStudio.Core.Tests.Visualization;

[TestClass]
public sealed class BScanIngressTests
{
    private static readonly AcquisitionRunId RunId = new(Guid.Parse("30000000-0000-0000-0000-000000000003"));

    [TestMethod]
    public async Task AcceptProviderAndIndexPreserveExactSamplesAndLogicalSlice()
    {
        var arrays = new DeterministicShortPool();
        var owners = new DeterministicOwnerPool();
        var ingress = new BScanIngress(Options(4), null, arrays, owners);
        var metadata = Metadata(5);
        var indexed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ingress.AfterIndexCommit = _ => indexed.TrySetResult();
        ingress.OpenRun(metadata);
        short[] samples = [short.MinValue, -1, 0, 1, short.MaxValue];

        ingress.Accept(metadata, 0, 0, TimeSpan.Zero, null, samples);
        samples.AsSpan().Fill(9);
        await indexed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        BScanTraceIdentity identity = Identity(metadata, 0);
        BScanTraceResult result = await ingress.GetTraceAsync(identity);

        Assert.AreEqual(BScanTraceResultKind.Available, result.Kind);
        Assert.AreEqual(5, result.Trace!.Samples.Length);
        CollectionAssert.AreEqual(new short[] { short.MinValue, -1, 0, 1, short.MaxValue }, result.Trace.Samples.ToArray());
        CollectionAssert.AreEqual(new[] { new BScanMinMax(short.MinValue, short.MaxValue) },
            ingress.CopyIndexForDiagnostics(identity));
        result.Trace.Dispose();
        Assert.AreEqual(1, owners.RentCount);
        Assert.AreEqual(1, owners.ReturnCount);
        ingress.CloseRun(RunId);
        await ingress.DisposeAsync();
        Assert.AreEqual(arrays.RentCount, arrays.ReturnCount);
    }

    [TestMethod]
    public async Task ProviderDistinguishesMissingEvictedCancelledAndOtherRun()
    {
        var ingress = new BScanIngress(Options(1), null, new DeterministicShortPool(), new DeterministicOwnerPool());
        var metadata = Metadata(3);
        ingress.OpenRun(metadata);
        ingress.Accept(metadata, 0, 0, TimeSpan.Zero, null, new short[] { 1, 2, 3 });
        ingress.Accept(metadata, 1, 1, TimeSpan.Zero, null, new short[] { 4, 5, 6 });

        Assert.AreEqual(BScanTraceResultKind.Evicted, (await ingress.GetTraceAsync(Identity(metadata, 0))).Kind);
        Assert.AreEqual(BScanTraceResultKind.NotAvailable, (await ingress.GetTraceAsync(Identity(metadata, 9))).Kind);
        var otherRun = Identity(metadata, 1) with { RunId = new(Guid.NewGuid()) };
        Assert.AreEqual(BScanTraceResultKind.NotAvailable, (await ingress.GetTraceAsync(otherRun)).Kind);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        Assert.AreEqual(BScanTraceResultKind.Cancelled,
            (await ingress.GetTraceAsync(Identity(metadata, 1), cancelled.Token)).Kind);
        ingress.CloseRun(RunId);
        await ingress.DisposeAsync();
    }

    [TestMethod]
    public async Task ConfigurationAndPostCloseRejectionsRemainSeparate()
    {
        var ingress = new BScanIngress(Options(2), null, new DeterministicShortPool(), new DeterministicOwnerPool());
        var metadata = Metadata(3);
        var incompatible = Metadata(4);
        ingress.OpenRun(metadata);

        ingress.Accept(incompatible, 0, 0, TimeSpan.Zero, null, new short[4]);
        ingress.CloseRun(RunId);
        ingress.Accept(metadata, 1, 1, TimeSpan.Zero, null, new short[3]);
        await ingress.DisposeAsync();

        Assert.AreEqual(1UL, ingress.Diagnostics.BScanColumnsRejectedConfiguration);
        Assert.AreEqual(1UL, ingress.Diagnostics.BScanColumnsRejectedClosed);
        Assert.AreEqual(0UL, ingress.Diagnostics.BScanColumnsAccepted);
    }

    [TestMethod]
    public async Task CancellationDuringCopyReturnsOwnerAndLease()
    {
        var owners = new DeterministicOwnerPool();
        var ingress = new BScanIngress(Options(2), null, new DeterministicShortPool(), owners);
        var metadata = Metadata(8192);
        ingress.OpenRun(metadata);
        ingress.Accept(metadata, 0, 0, TimeSpan.Zero, null, new short[8192]);
        using var cancellation = new CancellationTokenSource();
        ingress.BeforeProviderCopy = cancellation.Cancel;

        BScanTraceResult result = await ingress.GetTraceAsync(Identity(metadata, 0), cancellation.Token);

        Assert.AreEqual(BScanTraceResultKind.Cancelled, result.Kind);
        Assert.AreEqual(1, owners.RentCount);
        Assert.AreEqual(1, owners.ReturnCount);
        Assert.AreEqual(0, ingress.Diagnostics.ActiveReadLeases);
        ingress.CloseRun(RunId);
        await ingress.DisposeAsync();
    }

    [TestMethod]
    public async Task AtMostTwoOwnedCopiesExistAndCancellationDoesNotAccumulateOwners()
    {
        var owners = new DeterministicOwnerPool();
        var ingress = new BScanIngress(Options(1), null, new DeterministicShortPool(), owners);
        var metadata = Metadata(3);
        ingress.OpenRun(metadata);
        ingress.Accept(metadata, 0, 0, TimeSpan.Zero, null, new short[] { 1, 2, 3 });
        BScanTraceIdentity identity = Identity(metadata, 0);
        BScanTraceResult first = await ingress.GetTraceAsync(identity);
        BScanTraceResult second = await ingress.GetTraceAsync(identity);
        using var cancellation = new CancellationTokenSource();
        ValueTask<BScanTraceResult> third = ingress.GetTraceAsync(identity, cancellation.Token);
        Assert.IsFalse(third.IsCompleted);
        cancellation.Cancel();

        Assert.AreEqual(BScanTraceResultKind.Cancelled, (await third).Kind);
        Assert.AreEqual(2, owners.RentCount);
        first.Trace!.Dispose();
        second.Trace!.Dispose();
        Assert.AreEqual(2, owners.ReturnCount);
        ingress.CloseRun(RunId);
        await ingress.DisposeAsync();
    }

    [TestMethod]
    public async Task ThrowingSnapshotDisposeStillReleasesCopyQuotaAndRevokesSamples()
    {
        var owners = new DeterministicOwnerPool();
        var ingress = new BScanIngress(Options(1), null, new DeterministicShortPool(), owners);
        var metadata = Metadata(3);
        ingress.OpenRun(metadata);
        ingress.Accept(metadata, 0, 0, TimeSpan.Zero, null, new short[] { 1, 2, 3 });
        BScanTraceIdentity identity = Identity(metadata, 0);
        BScanTraceSnapshot trace = (await ingress.GetTraceAsync(identity)).Trace!;
        owners.ThrowOnReturn = true;

        Assert.ThrowsExactly<InvalidOperationException>(trace.Dispose);
        Assert.ThrowsExactly<ObjectDisposedException>(() => _ = trace.Samples);
        owners.ThrowOnReturn = false;
        using BScanTraceSnapshot replacement = (await ingress.GetTraceAsync(identity)).Trace!;

        ingress.CloseRun(RunId);
        await ingress.DisposeAsync();
    }

    [TestMethod]
    public async Task ConcurrentSnapshotDisposeDoesNotReleaseQuotaBeforeOwnerReturnFinishes()
    {
        using var ownerReturnEntered = new ManualResetEventSlim();
        using var releaseOwnerReturn = new ManualResetEventSlim();
        var owners = new DeterministicOwnerPool();
        var ingress = new BScanIngress(Options(1), null, new DeterministicShortPool(), owners);
        var metadata = Metadata(3);
        ingress.OpenRun(metadata);
        ingress.Accept(metadata, 0, 0, TimeSpan.Zero, null, new short[] { 1, 2, 3 });
        BScanTraceIdentity identity = Identity(metadata, 0);
        BScanTraceSnapshot first = (await ingress.GetTraceAsync(identity)).Trace!;
        BScanTraceSnapshot second = (await ingress.GetTraceAsync(identity)).Trace!;
        owners.BeforeReturn = () => { ownerReturnEntered.Set(); releaseOwnerReturn.Wait(); };
        var disposeThread = new Thread(first.Dispose) { IsBackground = true };
        disposeThread.Start();
        Assert.IsTrue(ownerReturnEntered.Wait(TimeSpan.FromSeconds(10)));
        first.Dispose();

        ValueTask<BScanTraceResult> third = ingress.GetTraceAsync(identity);
        Assert.IsFalse(third.IsCompleted);
        owners.BeforeReturn = null;
        releaseOwnerReturn.Set();
        Assert.IsTrue(disposeThread.Join(TimeSpan.FromSeconds(10)));
        using BScanTraceSnapshot thirdTrace = (await third).Trace!;
        second.Dispose();
        ingress.CloseRun(RunId);
        await ingress.DisposeAsync();
    }

    [TestMethod]
    public async Task CancellationAfterCopyReturnsCandidateOwner()
    {
        var owners = new DeterministicOwnerPool();
        using var cancellation = new CancellationTokenSource();
        var ingress = new BScanIngress(Options(1), null, new DeterministicShortPool(), owners)
        {
            AfterProviderCopy = cancellation.Cancel
        };
        var metadata = Metadata(3);
        ingress.OpenRun(metadata);
        ingress.Accept(metadata, 0, 0, TimeSpan.Zero, null, new short[] { 1, 2, 3 });

        BScanTraceResult result = await ingress.GetTraceAsync(Identity(metadata, 0), cancellation.Token);

        Assert.AreEqual(BScanTraceResultKind.Cancelled, result.Kind);
        Assert.AreEqual(1, owners.RentCount);
        Assert.AreEqual(1, owners.ReturnCount);
        ingress.CloseRun(RunId);
        await ingress.DisposeAsync();
    }

    [TestMethod]
    public async Task ConcurrentSelectionPinsSlotAndWrapResumesAfterCopy()
    {
        using var copyEntered = new ManualResetEventSlim();
        using var releaseCopy = new ManualResetEventSlim();
        var ingress = new BScanIngress(Options(1), null, new DeterministicShortPool(), new DeterministicOwnerPool())
        {
            BeforeProviderCopy = () => { copyEntered.Set(); releaseCopy.Wait(); }
        };
        var metadata = Metadata(3);
        ingress.OpenRun(metadata);
        ingress.Accept(metadata, 0, 0, TimeSpan.Zero, null, new short[] { 1, 2, 3 });
        var selected = new TaskCompletionSource<BScanTraceResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { selected.SetResult(ingress.GetTraceAsync(Identity(metadata, 0)).AsTask().GetAwaiter().GetResult()); }
            catch (Exception error) { selected.SetException(error); }
        })
        {
            IsBackground = true
        };
        thread.Start();
        Assert.IsTrue(copyEntered.Wait(TimeSpan.FromSeconds(10)));

        ingress.Accept(metadata, 1, 1, TimeSpan.Zero, null, new short[] { 4, 5, 6 });
        Assert.AreEqual(1UL, ingress.Diagnostics.BScanColumnsRejectedPinnedSlot);
        releaseCopy.Set();
        using BScanTraceSnapshot trace = (await selected.Task.WaitAsync(TimeSpan.FromSeconds(10))).Trace!;
        CollectionAssert.AreEqual(new short[] { 1, 2, 3 }, trace.Samples.ToArray());
        Assert.IsTrue(thread.Join(TimeSpan.FromSeconds(10)));

        ingress.BeforeProviderCopy = null;
        ingress.Accept(metadata, 2, 2, TimeSpan.Zero, null, new short[] { 7, 8, 9 });
        using BScanTraceSnapshot wrapped = (await ingress.GetTraceAsync(Identity(metadata, 2))).Trace!;
        CollectionAssert.AreEqual(new short[] { 7, 8, 9 }, wrapped.Samples.ToArray());
        ingress.CloseRun(RunId);
        await ingress.DisposeAsync();
    }

    [TestMethod]
    public async Task WorkerSignalCoalescesAndOverwrittenWorkIsSuperseded()
    {
        using var workerEntered = new ManualResetEventSlim();
        using var releaseWorker = new ManualResetEventSlim();
        var ingress = new BScanIngress(Options(2), null, new DeterministicShortPool(), new DeterministicOwnerPool())
        {
            BeforeIndexBatch = () => { workerEntered.Set(); releaseWorker.Wait(); }
        };
        var metadata = Metadata(3);
        ingress.OpenRun(metadata);
        ingress.Accept(metadata, 0, 0, TimeSpan.Zero, null, new short[3]);
        Assert.IsTrue(workerEntered.Wait(TimeSpan.FromSeconds(10)));
        ingress.Accept(metadata, 1, 1, TimeSpan.Zero, null, new short[3]);
        ingress.Accept(metadata, 2, 2, TimeSpan.Zero, null, new short[3]);
        ingress.Accept(metadata, 3, 3, TimeSpan.Zero, null, new short[3]);

        Assert.AreEqual(1, ingress.PendingWorkerSignals);
        Assert.IsGreaterThanOrEqualTo(2UL, ingress.Diagnostics.BScanIndexWorkSuperseded);
        releaseWorker.Set();
        ingress.CloseRun(RunId);
        await ingress.DisposeAsync();
    }

    [TestMethod]
    public async Task WorkerFailureDoesNotPreventCleanupAndCloseIsIdempotent()
    {
        using var workerFailed = new ManualResetEventSlim();
        var pool = new DeterministicShortPool();
        var ingress = new BScanIngress(Options(1), null, pool, new DeterministicOwnerPool())
        {
            WorkerFailure = () => new InvalidOperationException("worker failed"),
            AfterWorkerFailure = workerFailed.Set
        };
        var metadata = Metadata(3);
        ingress.OpenRun(metadata);
        ingress.Accept(metadata, 0, 0, TimeSpan.Zero, null, new short[3]);
        Assert.IsTrue(workerFailed.Wait(TimeSpan.FromSeconds(10)));
        Assert.AreEqual("worker failed", ingress.Diagnostics.WorkerError);
        ingress.CloseRun(RunId);
        ingress.CloseRun(RunId);

        Task first = ingress.DisposeAsync().AsTask();
        Task second = ingress.DisposeAsync().AsTask();
        await Assert.ThrowsExactlyAsync<AggregateException>(() => first.WaitAsync(TimeSpan.FromSeconds(10)));
        await Assert.ThrowsExactlyAsync<AggregateException>(() => second.WaitAsync(TimeSpan.FromSeconds(10)));

        Assert.AreEqual(pool.RentCount, pool.ReturnCount);
        Assert.IsTrue(ingress.Diagnostics.IsClosed);
    }

    [TestMethod]
    public async Task FailedBlockReturnFaultsSharedDrainAndIsNeverRetried()
    {
        var pool = new DeterministicShortPool();
        pool.ThrowOnReturns.Add(1);
        var ingress = new BScanIngress(Options(65), null, pool, new DeterministicOwnerPool());
        var metadata = Metadata(10);
        ingress.OpenRun(metadata);
        ingress.Accept(metadata, 0, 0, TimeSpan.Zero, null, new short[10]);

        ingress.Stop();
        Task first = ingress.WaitForDrainAsync();
        Task second = ingress.WaitForDrainAsync();
        AggregateException failure = await Assert.ThrowsExactlyAsync<AggregateException>(() => first);
        await Assert.ThrowsExactlyAsync<AggregateException>(() => second);

        Assert.AreSame(first, second);
        Assert.IsGreaterThanOrEqualTo(1, failure.Flatten().InnerExceptions.Count);
        Assert.AreEqual(pool.RentCount, pool.ReturnCount);
        Assert.IsFalse(ingress.Diagnostics.IsClosed);
        int attempts = pool.ReturnCount;
        await Assert.ThrowsExactlyAsync<AggregateException>(() => ingress.DisposeAsync().AsTask());
        Assert.AreEqual(attempts, pool.ReturnCount);
    }

    [TestMethod]
    public async Task CloseCancelsProviderCopyAndWaitsForItsLease()
    {
        using var copyEntered = new ManualResetEventSlim();
        using var releaseCopy = new ManualResetEventSlim();
        var pool = new DeterministicShortPool();
        var owners = new DeterministicOwnerPool();
        var ingress = new BScanIngress(Options(1), null, pool, owners)
        {
            BeforeProviderCopy = () => { copyEntered.Set(); releaseCopy.Wait(); }
        };
        var metadata = Metadata(4096);
        ingress.OpenRun(metadata);
        ingress.Accept(metadata, 0, 0, TimeSpan.Zero, null, new short[4096]);
        var copyResult = new TaskCompletionSource<BScanTraceResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var copyThread = new Thread(() =>
        {
            try { copyResult.TrySetResult(ingress.GetTraceAsync(Identity(metadata, 0)).AsTask().GetAwaiter().GetResult()); }
            catch (Exception error) { copyResult.TrySetException(error); }
        })
        {
            IsBackground = true
        };
        copyThread.Start();
        Assert.IsTrue(copyEntered.Wait(TimeSpan.FromSeconds(10)));

        ingress.CloseRun(RunId);
        Task dispose = ingress.DisposeAsync().AsTask();
        Assert.IsFalse(dispose.IsCompleted);
        releaseCopy.Set();

        Assert.AreEqual(BScanTraceResultKind.Cancelled,
            (await copyResult.Task.WaitAsync(TimeSpan.FromSeconds(10))).Kind);
        Assert.IsTrue(copyThread.Join(TimeSpan.FromSeconds(10)));
        await dispose.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.AreEqual(owners.RentCount, owners.ReturnCount);
        Assert.AreEqual(pool.RentCount, pool.ReturnCount);
        Assert.AreEqual(0, ingress.Diagnostics.ActiveReadLeases);
    }

    [TestMethod]
    public void MinMaxBucketsHandlePartialBucketAndInt16Extremes()
    {
        short[] samples = [short.MinValue, 0, short.MaxValue, -5, 5];
        var result = new BScanMinMax[3];

        BScanIngress.BuildIndex(samples, 2, result);

        CollectionAssert.AreEqual(new[]
        {
            new BScanMinMax(short.MinValue, 0),
            new BScanMinMax(-5, short.MaxValue),
            new BScanMinMax(5, 5)
        }, result);
    }

    [TestMethod]
    public async Task MetadataOriginIsValidatedAndAcceptDurationsAreBoundedAndDeterministic()
    {
        var clock = new IncrementingTimeProvider(TimeSpan.FromMilliseconds(2));
        var ingress = new BScanIngress(Options(1), clock, new DeterministicShortPool(), new DeterministicOwnerPool());
        var metadata = Metadata(3);
        var wrongOrigin = new ConventionalUtFrameMetadata(metadata.SourceId, metadata.RunId,
            metadata.Configuration, metadata.RunStartedAtUtc.AddTicks(1));
        ingress.OpenRun(metadata);

        ingress.Accept(wrongOrigin, 0, 0, TimeSpan.Zero, null, new short[3]);
        ingress.Accept(metadata, 0, 0, TimeSpan.Zero, null, new short[3]);

        BScanDiagnostics diagnostics = ingress.Diagnostics;
        Assert.AreEqual(1UL, diagnostics.BScanColumnsRejectedConfiguration);
        Assert.AreEqual(2UL, diagnostics.BScanAcceptBudgetExceeded);
        Assert.AreEqual(2, diagnostics.AcceptDuration.Count);
        Assert.AreEqual(TimeSpan.FromMilliseconds(2), diagnostics.AcceptDuration.P50);
        Assert.AreEqual(TimeSpan.FromMilliseconds(2), diagnostics.AcceptDuration.P99);
        ingress.CloseRun(RunId);
        await ingress.DisposeAsync();
    }

    [TestMethod]
    public async Task OpeningAnotherRunResetsRunScopedDiagnostics()
    {
        var clock = new IncrementingTimeProvider(TimeSpan.FromMilliseconds(2));
        var pool = new DeterministicShortPool();
        var ingress = new BScanIngress(Options(1), clock, pool, new DeterministicOwnerPool());
        var first = Metadata(3);
        ingress.OpenRun(first);
        ingress.Accept(new ConventionalUtFrameMetadata(first.SourceId, first.RunId, first.Configuration,
            first.RunStartedAtUtc.AddTicks(1)), 0, 0, TimeSpan.Zero, null, new short[3]);
        ingress.CloseRun(first.RunId);
        await ingress.WaitForCloseAsync(first.RunId);
        var second = new ConventionalUtFrameMetadata(first.SourceId, new AcquisitionRunId(Guid.NewGuid()),
            first.Configuration, first.RunStartedAtUtc.AddSeconds(1));

        ingress.OpenRun(second);
        BScanDiagnostics reset = ingress.Diagnostics;

        Assert.AreEqual(1, pool.ReturnCount);
        Assert.AreEqual(0UL, reset.BScanColumnsRejectedConfiguration);
        Assert.AreEqual(0UL, reset.BScanAcceptBudgetExceeded);
        Assert.AreEqual(0, reset.AcceptDuration.Count);
        ingress.CloseRun(second.RunId);
        await ingress.DisposeAsync();
        Assert.AreEqual(pool.RentCount, pool.ReturnCount);
    }

    private static BScanOptions Options(int columns) => new(traceMemoryBudgetBytes: 4L * 1024 * 1024,
        maximumColumns: columns, minimumColumns: 1, indexBucketSamples: 64);

    private static ConventionalUtFrameMetadata Metadata(int samples) => new(new UtSourceId("bscan"), RunId,
        new ConventionalAcquisitionConfiguration(default, samples, 50_000_000), DateTimeOffset.UnixEpoch);

    private static BScanTraceIdentity Identity(ConventionalUtFrameMetadata metadata, ulong sequence) =>
        new(metadata.SourceId, metadata.RunId, metadata.Configuration.PhysicalChannelId, sequence);

    private sealed class IncrementingTimeProvider(TimeSpan increment) : TimeProvider
    {
        private long _timestamp;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Interlocked.Add(ref _timestamp, increment.Ticks);
    }
}
