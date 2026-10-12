using UTStudio.Contracts.Inspection;
using UTStudio.Domain.Acquisition;
using UTStudio.Domain.Inspection;
using UTStudio.Inspection.Core;
using UTStudio.Visualization.Core;
using UTStudio.Visualization.Core.Scan2D;
using System.Reflection;

namespace UTStudio.Core.Tests.Visualization;

[TestClass]
[DoNotParallelize]
public sealed class BScanRangeProjectionTests
{
    [TestMethod]
    public void ProjectionOptionsRejectEveryOutOfContractBound()
    {
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new BScanOptions(0));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            new BScanOptions(BScanOptions.MaximumVisualMemoryBudgetBytes + 1));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new BScanOptions(maximumColumns: 0));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new BScanOptions(maximumColumns: 1025));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new BScanOptions(maximumRasterConsumers: 0));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            new BScanOptions(maximumRasterOwnersPerConsumer: 0));
    }

    [TestMethod]
    public async Task DirectRasterHandlesPartialTimeBucketsAndInt16Extremes()
    {
        await using var fixture = await Fixture.CreateAsync(maximumColumns: 256, sampleCount: 3);
        InspectionAppendResult commit = await fixture.AppendSamplesAsync(0, 0,
            [short.MinValue, 7, short.MaxValue]);
        fixture.Ingress.NotifyCommitted(fixture.Identity(0), commit.Revision);
        await fixture.WaitForRevisionAsync(commit.Revision);

        using BScanRasterRegion region = await fixture.RenderAsync(0, 0, 1, 2);

        Assert.AreEqual(BScanBipolarIntensity8.Map(short.MinValue), region.Pixels.Span[0]);
        Assert.AreEqual(BScanBipolarIntensity8.Map(short.MaxValue), region.Pixels.Span[1]);
    }

    [TestMethod]
    public async Task CoalescedNotificationReconcilesSparseAuthoritativeRangeInBoundedPages()
    {
        await using var fixture = await Fixture.CreateAsync(maximumColumns: 1024);
        InspectionAppendResult last = default;
        for (ulong index = 0; index <= 300; index++)
        {
            if (index == 150) { continue; }
            last = await fixture.AppendAsync(index, sequence: index + 1000);
        }

        fixture.Ingress.NotifyCommitted(fixture.Identity(300), last.Revision);
        await fixture.WaitForRevisionAsync(last.Revision);
        using BScanRasterRegion region = await fixture.RenderAsync(0, 300, 301, 8);

        Assert.AreEqual(new BScanAcquisitionRange(0, 300), region.Snapshot.ResidentRange);
        Assert.AreEqual(new BScanAcquisitionRange(0, 300), region.Snapshot.RepresentedRange);
        Assert.IsTrue(fixture.Provider.Queries.All(query => query.MaximumCount <= 256));
        Assert.AreEqual(0, region.ValidityMask.Span[150]);
        Assert.IsFalse(region.Snapshot.AvailableTraceIdentities.ContainsKey(150));
        Assert.AreEqual(fixture.Identity(300), region.Snapshot.AvailableTraceIdentities[300]);
    }

    [TestMethod]
    public async Task SequenceGapCreatesNoSyntheticColumnAndNotAvailableRemainsNoData()
    {
        await using var fixture = await Fixture.CreateAsync(maximumColumns: 256);
        _ = await fixture.AppendAsync(10, sequence: 0);
        InspectionAppendResult last = await fixture.AppendAsync(12, sequence: 100);

        fixture.Ingress.NotifyCommitted(fixture.Identity(12), last.Revision);
        await fixture.WaitForRevisionAsync(last.Revision);
        using BScanRasterRegion region = await fixture.RenderAsync(10, 12, 3, 4);

        CollectionAssert.AreEqual(new byte[] { 1, 0, 1 }, region.ValidityMask.Span[..3].ToArray());
        Assert.AreEqual(3, region.Width);
        Assert.IsTrue(region.Snapshot.HasDiscontinuities);
        Assert.IsFalse(region.Snapshot.AvailableTraceIdentities.ContainsKey(11));
        Assert.AreEqual(10UL, region.Snapshot.AvailableTraceIdentities[10].AcquisitionIndex);
        Assert.AreEqual(12UL, region.Snapshot.AvailableTraceIdentities[12].AcquisitionIndex);
    }

    [TestMethod]
    public async Task MultipleTracesInOnePixelExposeOneDeterministicAcquiredIdentity()
    {
        await using var fixture = await Fixture.CreateAsync(maximumColumns: 256);
        _ = await fixture.AppendAsync(10, sequence: 0);
        InspectionAppendResult last = await fixture.AppendAsync(11, sequence: 1);
        fixture.Ingress.NotifyCommitted(fixture.Identity(11), last.Revision);
        await fixture.WaitForRevisionAsync(last.Revision);

        using BScanRasterRegion region = await fixture.RenderAsync(10, 11, 1, 4);

        Assert.AreEqual(fixture.Identity(10), region.Snapshot.SelectableTraceIdentities[0]);
    }

    [TestMethod]
    public async Task RangeLeasesAreBalancedAfterRasterAndClose()
    {
        await using var fixture = await Fixture.CreateAsync(maximumColumns: 256);
        InspectionAppendResult commit = await fixture.AppendAsync(0, sequence: 50);
        fixture.Ingress.NotifyCommitted(fixture.Identity(0), commit.Revision);
        await fixture.WaitForRevisionAsync(commit.Revision);
        using (BScanRasterRegion region = await fixture.RenderAsync(0, 0, 1, 4))
        { Assert.HasCount(1, region.Snapshot.AvailableTraceIdentities); }

        fixture.Ingress.CloseRun(fixture.RunId);
        await fixture.Ingress.WaitForCloseAsync(fixture.RunId);
        Assert.AreEqual(fixture.Provider.LeasesCreated, fixture.Provider.LeasesDisposed);
        Assert.AreEqual(0, fixture.Ingress.Diagnostics.ActiveReadLeases);
    }

    [TestMethod]
    public async Task DirectProjectionDoesNotRetainDerivedIndexOrRfLease()
    {
        await using var fixture = await Fixture.CreateAsync(maximumColumns: 256);
        InspectionAppendResult commit = await fixture.AppendAsync(0, sequence: 0);
        fixture.Ingress.NotifyCommitted(fixture.Identity(0), commit.Revision);
        await fixture.WaitForRevisionAsync(commit.Revision);
        using (BScanRasterRegion first = await fixture.RenderAsync(0, 0, 1, 4)) { }
        using (BScanRasterRegion second = await fixture.RenderAsync(0, 0, 1, 4)) { }

        Assert.AreEqual(fixture.Provider.LeasesCreated, fixture.Provider.LeasesDisposed);
        Assert.AreEqual(0, fixture.Provider.ActiveLeases);
    }

    [TestMethod]
    public async Task HistoricalViewportReadsProviderWhileResidentRangeRemainsAuthoritative()
    {
        await using var fixture = await Fixture.CreateAsync(maximumColumns: 256);
        _ = await fixture.AppendAsync(0, sequence: 0);
        InspectionAppendResult last = await fixture.AppendAsync(600, sequence: 1);
        fixture.Ingress.NotifyCommitted(fixture.Identity(600), last.Revision);
        await fixture.WaitForRevisionAsync(last.Revision);

        using BScanRasterRegion region = await fixture.RenderAsync(0, 0, 1, 4);

        Assert.AreEqual(new BScanAcquisitionRange(0, 600), region.Snapshot.ResidentRange);
        Assert.AreEqual(new BScanAcquisitionRange(0, 0), region.Snapshot.RepresentedRange);
        Assert.AreEqual(fixture.Identity(0), region.Snapshot.AvailableTraceIdentities[0]);
        Assert.IsTrue(fixture.Provider.Queries.Any(query => query.StartAcquisitionIndex == 0));
    }

    [TestMethod]
    public async Task ValidAllNotAvailableViewportConfirmsAllNoDataRaster()
    {
        await using var fixture = await Fixture.CreateAsync(maximumColumns: 1024);
        _ = await fixture.AppendAsync(0, sequence: 0);
        InspectionAppendResult last = await fixture.AppendAsync(600, sequence: 1);
        fixture.Ingress.NotifyCommitted(fixture.Identity(600), last.Revision);
        await fixture.WaitForRevisionAsync(last.Revision);

        using BScanRasterRegion region = await fixture.RenderAsync(100, 103, 4, 4);

        Assert.AreEqual(new BScanAcquisitionRange(100, 103), region.Snapshot.RepresentedRange);
        Assert.IsTrue(region.Snapshot.HasDiscontinuities);
        Assert.IsTrue(region.ValidityMask.Span.ToArray().All(static value => value == 0));
        Assert.IsTrue(region.Pixels.Span.ToArray().All(static value => value == 0));
        Assert.HasCount(0, region.Snapshot.AvailableTraceIdentities);
        Assert.IsTrue(region.Snapshot.SelectableTraceIdentities.All(static identity => identity is null));
        Assert.Contains(InspectionReadStatus.NotAvailable, region.Snapshot.NoDataStatuses);
    }

    [TestMethod]
    public async Task CloseWaitsForAdmittedStateReadWithoutForcingCancellation()
    {
        await using var fixture = await Fixture.CreateAsync(maximumColumns: 256);
        InspectionAppendResult commit = await fixture.AppendAsync(0, sequence: 0);
        fixture.Provider.BlockNextStateRead();
        fixture.Ingress.NotifyCommitted(fixture.Identity(0), commit.Revision);
        await fixture.Provider.StateReadEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));

        fixture.Ingress.CloseRun(fixture.RunId);
        ValueTask drain = fixture.Ingress.WaitForCloseAsync(fixture.RunId);
        Assert.IsFalse(drain.IsCompleted);
        fixture.Provider.ReleaseBlockedStateRead();
        await drain.AsTask().WaitAsync(TimeSpan.FromSeconds(10));

        Assert.IsFalse(fixture.Provider.BlockedStateReadCancelled);
    }

    [TestMethod]
    public async Task NewRunIsRejectedUntilPreviousReconciliationHasStopped()
    {
        await using var fixture = await Fixture.CreateAsync(maximumColumns: 256);
        InspectionAppendResult commit = await fixture.AppendAsync(0, sequence: 0);
        fixture.Provider.BlockNextStateRead();
        fixture.Ingress.NotifyCommitted(fixture.Identity(0), commit.Revision);
        await fixture.Provider.StateReadEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));

        fixture.Ingress.CloseRun(fixture.RunId);
        Assert.ThrowsExactly<InvalidOperationException>(() =>
            fixture.Ingress.OpenRun(new AcquisitionRunId(Guid.NewGuid())));

        fixture.Provider.ReleaseBlockedStateRead();
        await fixture.Ingress.WaitForCloseAsync(fixture.RunId).AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        fixture.Ingress.FreezeRun(fixture.RunId);
        fixture.Ingress.OpenRun(new AcquisitionRunId(Guid.NewGuid()));
    }

    [TestMethod]
    public async Task FinalReconciliationContinuesAfterDrainPhaseBegins()
    {
        await using var fixture = await Fixture.CreateAsync(maximumColumns: 256);
        InspectionAppendResult first = await fixture.AppendAsync(0, sequence: 0);
        fixture.Ingress.NotifyCommitted(fixture.Identity(0), first.Revision);
        await fixture.WaitForRevisionAsync(first.Revision);
        InspectionAppendResult final = await fixture.AppendAsync(1, sequence: 1);
        fixture.Provider.BlockNextStateRead();

        fixture.Ingress.RequestFinalReconciliation(fixture.RunId, final.Revision);
        await fixture.Provider.StateReadEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        fixture.Ingress.StopAdmission(fixture.RunId);
        ValueTask drain = fixture.Ingress.WaitForDrainAsync(fixture.RunId);
        Assert.IsFalse(drain.IsCompleted);
        fixture.Provider.ReleaseBlockedStateRead();
        await drain.AsTask().WaitAsync(TimeSpan.FromSeconds(10));

        Assert.AreEqual(final.Revision.Value,
            fixture.Ingress.CurrentVisualState!.StoreState.StoreRevision);
        fixture.Ingress.FreezeRun(fixture.RunId);
    }

    [TestMethod]
    public async Task GapOnlyRunWithoutTraceStreamDrainsAndFreezesAtFinalRevision()
    {
        await using var fixture = await Fixture.CreateAsync(maximumColumns: 256);

        fixture.Ingress.RequestFinalReconciliation(fixture.RunId, new(1));
        fixture.Ingress.StopAdmission(fixture.RunId);
        await fixture.Ingress.WaitForDrainAsync(fixture.RunId).AsTask()
            .WaitAsync(TimeSpan.FromSeconds(10));
        fixture.Ingress.FreezeRun(fixture.RunId);

        Assert.IsNull(fixture.Ingress.CurrentVisualState);
    }

    [TestMethod]
    public async Task MaximumWindowKeepsReadsAndLeasesWithinConfiguredBounds()
    {
        await using var fixture = await Fixture.CreateAsync(maximumColumns: 1024);
        InspectionAppendResult last = default;
        for (ulong index = 0; index < 1024; index++)
        { last = await fixture.AppendAsync(index, sequence: index); }
        fixture.Ingress.NotifyCommitted(fixture.Identity(1023), last.Revision);
        await fixture.WaitForRevisionAsync(last.Revision);

        using BScanRasterRegion region = await fixture.RenderAsync(0, 1023, 1024, 4);

        Assert.IsTrue(fixture.Provider.Queries.All(query => query.MaximumCount <= 256));
        Assert.IsLessThanOrEqualTo(256, fixture.Provider.MaximumActiveLeases);
        Assert.AreEqual(0, fixture.Ingress.Diagnostics.ActiveReadLeases);
        Assert.AreEqual(fixture.Provider.LeasesCreated, fixture.Provider.LeasesDisposed);
    }

    [TestMethod]
    [DataRow(255)]
    [DataRow(256)]
    [DataRow(257)]
    [DataRow(1023)]
    public async Task PageBoundariesNeverExceedTwoHundredFiftySixLeases(int columns)
    {
        await using var fixture = await Fixture.CreateAsync(maximumColumns: 1024);
        InspectionAppendResult last = default;
        for (ulong index = 0; index < (ulong)columns; index++)
        { last = await fixture.AppendAsync(index, sequence: index); }
        fixture.Ingress.NotifyCommitted(fixture.Identity((ulong)columns - 1), last.Revision);
        await fixture.WaitForRevisionAsync(last.Revision);

        using BScanRasterRegion region = await fixture.RenderAsync(0, (ulong)columns - 1,
            Math.Min(columns, 1024), 4);

        Assert.IsLessThanOrEqualTo(256, fixture.Provider.MaximumActiveLeases);
        Assert.AreEqual(Math.Min(columns, 256), fixture.Ingress.Diagnostics.MaximumReadLeases);
        Assert.AreEqual(fixture.Provider.LeasesCreated, fixture.Provider.LeasesDisposed);
    }

    [TestMethod]
    public async Task ViewportWiderThanConfiguredCapacityIsRejectedWithoutFalseRepresentedRange()
    {
        await using var fixture = await Fixture.CreateAsync(maximumColumns: 256);
        InspectionAppendResult last = default;
        for (ulong index = 0; index <= 600; index++)
        { last = await fixture.AppendAsync(index, sequence: index); }
        fixture.Ingress.NotifyCommitted(fixture.Identity(600), last.Revision);
        await fixture.WaitForRevisionAsync(last.Revision);

        BScanRasterResult result = await fixture.RenderResultAsync(0, 600, 601, 4);

        Assert.AreEqual(BScanRasterResultKind.ResourceLimit, result.Kind);
        Assert.IsNull(result.Region);
    }

    [TestMethod]
    public async Task ProductiveWindowStreamsFourPagesWithinNeutralBudgetWithoutRfCopies()
    {
        await using var fixture = await Fixture.CreateAsync(1024, sampleCount: 2048);
        InspectionAppendResult last = default;
        for (ulong index = 0; index < 1024; index++)
        { last = await fixture.AppendAsync(index, index); }
        fixture.Ingress.NotifyCommitted(fixture.Identity(1023), last.Revision);
        await fixture.WaitForRevisionAsync(last.Revision);
        fixture.Provider.Queries.Clear();

        using BScanRasterRegion region = await fixture.RenderAsync(0, 1023, 1024, 32);

        Assert.HasCount(4, fixture.Provider.Queries);
        Assert.IsTrue(fixture.Provider.Queries.All(static query => query.MaximumCount == 256));
        Assert.IsLessThanOrEqualTo(256, fixture.Provider.MaximumActiveLeases);
        Assert.AreEqual(fixture.Provider.LeasesCreated, fixture.Provider.LeasesDisposed);
        Assert.IsLessThanOrEqualTo(BScanOptions.MaximumVisualMemoryBudgetBytes,
            fixture.Delivery.VisualMemorySnapshot.MaximumOwnedBytes);
    }

    [TestMethod]
    public async Task ProjectionMemoryDoesNotGrowWithSixtyFiveThousandFiveHundredThirtyFiveSamples()
    {
        long small = await RenderAndMeasureAsync(2048);
        long maximum = await RenderAndMeasureAsync(65_535);

        Assert.AreEqual(small, maximum);

        static async Task<long> RenderAndMeasureAsync(int sampleCount)
        {
            await using var fixture = await Fixture.CreateAsync(1, sampleCount);
            InspectionAppendResult commit = await fixture.AppendAsync(0, 0);
            fixture.Ingress.NotifyCommitted(fixture.Identity(0), commit.Revision);
            await fixture.WaitForRevisionAsync(commit.Revision);
            using BScanRasterRegion region = await fixture.RenderAsync(0, 0, 1, 32);
            return fixture.Delivery.VisualMemorySnapshot.MaximumOwnedBytes;
        }
    }

    [TestMethod]
    public async Task TenThousandNotificationsCoalesceToLatestAuthoritativeRevision()
    {
        await using var fixture = await Fixture.CreateAsync(maximumColumns: 256);
        InspectionAppendResult commit = await fixture.AppendAsync(0, sequence: 0);
        fixture.Provider.BlockNextStateRead();
        fixture.Ingress.NotifyCommitted(fixture.Identity(0), commit.Revision);
        await fixture.Provider.StateReadEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        for (int notification = 0; notification < 10_000; notification++)
        { fixture.Ingress.NotifyCommitted(fixture.Identity(0), commit.Revision); }

        fixture.Provider.ReleaseBlockedStateRead();
        await fixture.WaitForRevisionAsync(commit.Revision);

        Assert.AreEqual(commit.Revision.Value,
            fixture.Ingress.CurrentVisualState!.StoreState.StoreRevision);
    }

    [TestMethod]
    public async Task IncreasingRevisionsPublishCoherentIntermediateSnapshotsBeforeConverging()
    {
        await using var fixture = await Fixture.CreateAsync(maximumColumns: 256);
        var published = new System.Collections.Concurrent.ConcurrentQueue<ulong>();
        fixture.Ingress.VisualStateChanged += () =>
        {
            if (fixture.Ingress.CurrentVisualState is { } state)
            { published.Enqueue(state.StoreState.StoreRevision); }
        };
        int advances = 0;
        using var candidateEntered = new SemaphoreSlim(0);
        using var releaseCandidate = new SemaphoreSlim(0);
        fixture.Ingress.BeforeVisualStateCommit = () =>
        {
            int advance = Interlocked.Increment(ref advances);
            if (advance > 3) { return; }
            candidateEntered.Release();
            if (!releaseCandidate.Wait(TimeSpan.FromSeconds(10)))
            { throw new TimeoutException("The intermediate candidate gate was not released."); }
        };
        InspectionAppendResult first = await fixture.AppendAsync(0, sequence: 0);

        fixture.Ingress.NotifyCommitted(fixture.Identity(0), first.Revision);
        for (ulong advance = 1; advance <= 3; advance++)
        {
            Assert.IsTrue(await candidateEntered.WaitAsync(TimeSpan.FromSeconds(10)));
            InspectionAppendResult next = await fixture.AppendAsync(advance, advance);
            fixture.Ingress.NotifyCommitted(fixture.Identity(advance), next.Revision);
            releaseCandidate.Release();
        }
        await fixture.WaitForRevisionAsync(new(first.Revision.Value + 3));

        ulong[] revisions = published.Distinct().Order().ToArray();
        Assert.IsGreaterThanOrEqualTo(2, revisions.Length);
        Assert.AreEqual(first.Revision.Value, revisions[0]);
        Assert.AreEqual(first.Revision.Value + 3, revisions[^1]);
    }

    [TestMethod]
    public async Task RevisionAdvancingBetweenReconciliationPagesPublishesIntermediateAndConverges()
    {
        await using var fixture = await Fixture.CreateAsync(maximumColumns: 1024);
        InspectionAppendResult initial = default;
        for (ulong index = 0; index <= 300; index++)
        { initial = await fixture.AppendAsync(index, sequence: index); }
        InspectionAppendResult appended = default;
        var appendCompleted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        int injected = 0;
        fixture.Provider.RevisionOverride = read =>
        {
            if (read != 1 || Interlocked.Exchange(ref injected, 1) != 0) { return null; }
            appendCompleted.TrySetResult();
            return null;
        };

        fixture.Ingress.NotifyCommitted(fixture.Identity(300), initial.Revision);
        await appendCompleted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        appended = await fixture.AppendAsync(301, sequence: 301);
        fixture.Ingress.NotifyCommitted(fixture.Identity(301), appended.Revision);
        await fixture.WaitForRevisionAsync(appended.Revision);

        Assert.AreEqual(appended.Revision.Value,
            fixture.Ingress.CurrentVisualState!.StoreState.StoreRevision);
        Assert.AreEqual(new BScanAcquisitionRange(0, 301),
            fixture.Ingress.CurrentVisualState.StoreState.ResidentRange);
        Assert.IsGreaterThanOrEqualTo(4, fixture.Provider.Queries.Count);
        Assert.IsTrue(fixture.Provider.Queries.All(static query => query.MaximumCount <= 256));
    }

    [TestMethod]
    public async Task ContinuouslyAdvancingPageRevisionsStillPublishCapturedBoundedSnapshot()
    {
        await using var fixture = await Fixture.CreateAsync(maximumColumns: 1024);
        InspectionAppendResult initial = default;
        for (ulong index = 0; index <= 300; index++)
        { initial = await fixture.AppendAsync(index, sequence: index); }
        ulong baseline = initial.Revision.Value;
        fixture.Provider.RevisionOverride = read => new(baseline + checked((ulong)read));

        fixture.Ingress.NotifyCommitted(fixture.Identity(300), initial.Revision);
        await fixture.WaitForRevisionAsync(initial.Revision);

        Assert.AreEqual(initial.Revision.Value,
            fixture.Ingress.CurrentVisualState!.StoreState.StoreRevision);
        Assert.AreEqual(new BScanAcquisitionRange(0, 300),
            fixture.Ingress.CurrentVisualState.StoreState.ResidentRange);
        Assert.IsGreaterThanOrEqualTo(2, fixture.Provider.Queries.Count);
        Assert.IsTrue(fixture.Provider.Queries.All(static query => query.MaximumCount <= 256));
    }

    [TestMethod]
    public async Task NotificationDuringWorkerExitRearmsLatestOnlyReconciliation()
    {
        await using var fixture = await Fixture.CreateAsync(maximumColumns: 256);
        InspectionAppendResult first = await fixture.AppendAsync(0, sequence: 0);
        InspectionAppendResult second = default;
        int injected = 0;
        var exitEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var releaseExit = new ManualResetEventSlim();
        fixture.Ingress.BeforeReconcileWorkerExit = () =>
        {
            if (Interlocked.Exchange(ref injected, 1) != 0) { return; }
            exitEntered.TrySetResult();
            if (!releaseExit.Wait(TimeSpan.FromSeconds(10)))
            { throw new TimeoutException("The worker-exit gate was not released."); }
        };

        fixture.Ingress.NotifyCommitted(fixture.Identity(0), first.Revision);
        await exitEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        ValueTask drain;
        try
        {
            second = await fixture.AppendAsync(1, sequence: 1);
            fixture.Ingress.NotifyCommitted(fixture.Identity(1), second.Revision);
            fixture.Ingress.StopAdmission(fixture.RunId);
            drain = fixture.Ingress.WaitForDrainAsync(fixture.RunId);
            Assert.IsFalse(drain.IsCompleted);
        }
        finally { releaseExit.Set(); }
        await drain.AsTask().WaitAsync(TimeSpan.FromSeconds(10));

        Assert.AreEqual(second.Revision.Value,
            fixture.Ingress.CurrentVisualState!.StoreState.StoreRevision);
    }

    [TestMethod]
    [DataRow(Scan2DInterpolationMode.NearestNeighbor)]
    [DataRow(Scan2DInterpolationMode.Bilinear)]
    [DataRow(Scan2DInterpolationMode.Bicubic)]
    [DataRow(Scan2DInterpolationMode.Lanczos)]
    public async Task ResamplingKeepsFutureLiveColumnsAsNoData(Scan2DInterpolationMode mode)
    {
        await using var fixture = await Fixture.CreateAsync(maximumColumns: 256);
        InspectionAppendResult last = default;
        for (ulong index = 0; index < 4; index++)
        { last = await fixture.AppendAsync(index, sequence: index); }
        fixture.Ingress.NotifyCommitted(fixture.Identity(3), last.Revision);
        await fixture.WaitForRevisionAsync(last.Revision);

        BScanRasterResult result = await fixture.Delivery.GetRegionAsync(
            fixture.Request(0, 7, 8, 4, resamplingMode: mode));
        Assert.AreEqual(BScanRasterResultKind.Available, result.Kind, result.Error);
        using BScanRasterRegion region = result.Region!;
        Assert.AreEqual(new BScanAcquisitionRange(0, 3), region.Snapshot.RepresentedRange);
        for (int y = 0; y < region.Height; y++)
        {
            for (int x = 4; x < region.Width; x++)
            {
                int cell = y * region.Width + x;
                Assert.AreEqual(0, region.Pixels.Span[cell]);
                Assert.AreEqual(0, region.ValidityMask.Span[cell]);
                Assert.AreEqual((byte)Scan2DSampleProvenance.NoData, region.Provenance.Span[cell]);
            }
        }
        Assert.IsTrue(region.Snapshot.SelectableTraceIdentities.Skip(4)
            .All(static identity => identity is null));
    }

    [TestMethod]
    public async Task BilinearLiveAppendBuildsOnlyDirtyColumnsAndHalo()
    {
        await using var fixture = await Fixture.CreateAsync(maximumColumns: 256);
        InspectionAppendResult first = await fixture.AppendAsync(0, sequence: 0);
        fixture.Ingress.NotifyCommitted(fixture.Identity(0), first.Revision);
        await fixture.WaitForRevisionAsync(first.Revision);
        BScanRenderRequest request = fixture.Request(0, 7, 8, 4,
            resamplingMode: Scan2DInterpolationMode.Bilinear);
        BScanRasterResult initial = await fixture.Delivery.GetRegionAsync(request);
        Assert.AreEqual(BScanRasterResultKind.Available, initial.Kind, initial.Error);
        using BScanRasterRegion initialRegion = initial.Region!;
        Assert.IsTrue(initialRegion.Snapshot.RequiresFullRefresh);
        var displayed = new BScanDisplayedVisualIdentity(initialRegion.Snapshot.Stream,
            new(initialRegion.Snapshot.SnapshotVersion, initialRegion.Snapshot.StoreRevision,
                initialRegion.Snapshot.WrapGeneration),
            new(initialRegion.Snapshot.SpatialViewport, initialRegion.Snapshot.TimeViewport,
                initialRegion.Snapshot.RasterWidth, initialRegion.Snapshot.RasterHeight,
                initialRegion.Snapshot.RenderConfigurationVersion, initialRegion.Snapshot.Resampling));
        fixture.Delivery.AcknowledgeDisplayed("default", displayed);

        InspectionAppendResult second = await fixture.AppendAsync(1, sequence: 1);
        fixture.Ingress.NotifyCommitted(fixture.Identity(1), second.Revision);
        await fixture.WaitForRevisionAsync(second.Revision);
        BScanRasterResult incremental = await fixture.Delivery.GetRegionAsync(request with
        {
            DisplayedSnapshotVersion = displayed.Revision.SnapshotVersion,
            DisplayedStoreRevision = displayed.Revision.StoreRevision,
            DisplayedWrapGeneration = displayed.Revision.WrapGeneration,
            Displayed = displayed
        });
        Assert.AreEqual(BScanRasterResultKind.Available, incremental.Kind, incremental.Error);
        using BScanRasterRegion incrementalRegion = incremental.Region!;

        Assert.IsFalse(incrementalRegion.Snapshot.RequiresFullRefresh);
        Assert.AreEqual(0, incrementalRegion.X);
        Assert.AreEqual(2, incrementalRegion.Width);
        Assert.AreEqual(new BScanRasterRectangle(0, 0, 2, 4),
            incrementalRegion.Snapshot.AffectedRegion);
        Assert.AreEqual(fixture.Identity(0),
            incrementalRegion.Snapshot.SelectableTraceIdentities[0]);
        Assert.AreEqual(fixture.Identity(1),
            incrementalRegion.Snapshot.SelectableTraceIdentities[1]);
        Assert.IsTrue(fixture.Provider.Queries.All(static query => query.MaximumCount <= 256));
    }

    [TestMethod]
    public async Task LateDisplayAcknowledgementPreservesNewerPendingRenderGeometry()
    {
        await using var fixture = await Fixture.CreateAsync(maximumColumns: 256);
        InspectionAppendResult commit = await fixture.AppendAsync(0, sequence: 0);
        fixture.Ingress.NotifyCommitted(fixture.Identity(0), commit.Revision);
        await fixture.WaitForRevisionAsync(commit.Revision);
        BScanRenderRequest original = fixture.Request(0, 3, 4, 4);
        fixture.Delivery.RequestRender(original);
        BScanRenderRequest resized = fixture.Request(0, 1, 2, 4);
        fixture.Delivery.RequestRender(resized);
        BScanIngressVisualState state = fixture.Ingress.CurrentVisualState!;
        var displayed = new BScanDisplayedVisualIdentity(
            new(fixture.SourceId, fixture.RunId, new(0)),
            new(checked((ulong)state.Epoch), state.StoreState.StoreRevision,
                state.StoreState.WrapGeneration),
            new(original.SpatialViewport, original.TimeViewport, original.PixelWidth,
                original.PixelHeight, original.RenderConfigurationVersion,
                original.EffectiveResampling));

        fixture.Delivery.AcknowledgeDisplayed("default", displayed);

        BScanRenderRequest pending = fixture.Delivery.GetLastRequest("default")!;
        Assert.AreEqual(resized.SpatialViewport, pending.SpatialViewport);
        Assert.AreEqual(resized.PixelWidth, pending.PixelWidth);
        Assert.AreEqual(resized.PixelHeight, pending.PixelHeight);
        Assert.AreEqual(displayed.Revision.SnapshotVersion, pending.DisplayedSnapshotVersion);
        Assert.AreEqual(displayed.Revision.StoreRevision, pending.DisplayedStoreRevision);
        Assert.AreSame(displayed, pending.Displayed);
    }

    [TestMethod]
    public async Task BilinearFullLiveWindowScrollsIncrementallyWithHalo()
    {
        await using var fixture = await Fixture.CreateAsync(maximumColumns: 4);
        InspectionAppendResult last = default;
        for (ulong index = 0; index < 4; index++)
        { last = await fixture.AppendAsync(index, sequence: index); }
        fixture.Ingress.NotifyCommitted(fixture.Identity(3), last.Revision);
        await fixture.WaitForRevisionAsync(last.Revision);

        BScanRenderRequest request = fixture.Request(0, 3, 4, 4,
            resamplingMode: Scan2DInterpolationMode.Bilinear) with
        { FollowLiveWindow = true };
        BScanRasterResult initial = await fixture.Delivery.GetRegionAsync(request);
        Assert.AreEqual(BScanRasterResultKind.Available, initial.Kind, initial.Error);
        using BScanRasterRegion initialRegion = initial.Region!;
        var displayed = new BScanDisplayedVisualIdentity(initialRegion.Snapshot.Stream,
            new(initialRegion.Snapshot.SnapshotVersion, initialRegion.Snapshot.StoreRevision,
                initialRegion.Snapshot.WrapGeneration),
            new(initialRegion.Snapshot.SpatialViewport, initialRegion.Snapshot.TimeViewport,
                initialRegion.Snapshot.RasterWidth, initialRegion.Snapshot.RasterHeight,
                initialRegion.Snapshot.RenderConfigurationVersion, initialRegion.Snapshot.Resampling));
        fixture.Delivery.AcknowledgeDisplayed("default", displayed);

        InspectionAppendResult appended = await fixture.AppendAsync(4, sequence: 4);
        fixture.Ingress.NotifyCommitted(fixture.Identity(4), appended.Revision);
        await fixture.WaitForRevisionAsync(appended.Revision);
        BScanRasterResult incremental = await fixture.Delivery.GetRegionAsync(request with
        {
            DisplayedSnapshotVersion = displayed.Revision.SnapshotVersion,
            DisplayedStoreRevision = displayed.Revision.StoreRevision,
            DisplayedWrapGeneration = displayed.Revision.WrapGeneration,
            Displayed = displayed
        });
        Assert.AreEqual(BScanRasterResultKind.Available, incremental.Kind, incremental.Error);
        using BScanRasterRegion region = incremental.Region!;

        Assert.IsFalse(region.Snapshot.RequiresFullRefresh);
        Assert.AreEqual(1, region.Snapshot.HorizontalPixelShift);
        Assert.AreEqual(new BScanAcquisitionRange(1, 4), region.Snapshot.RepresentedRange);
        Assert.AreEqual(new BScanRasterRectangle(2, 0, 2, 4),
            region.Snapshot.AffectedRegion);
        for (int pixel = 0; pixel < 4; pixel++)
        {
            Assert.AreEqual(fixture.Identity((ulong)pixel + 1),
                region.Snapshot.SelectableTraceIdentities[pixel]);
        }
        Assert.IsFalse(region.Snapshot.HasDiscontinuities);
        Assert.IsTrue(fixture.Provider.Queries.All(static query => query.MaximumCount <= 4));
    }

    [TestMethod]
    public async Task SingleColumnEndingAtUlongMaximumDoesNotOverflow()
    {
        await using var fixture = await Fixture.CreateAsync(maximumColumns: 256);
        InspectionAppendResult commit = await fixture.AppendAsync(ulong.MaxValue, sequence: 0);
        fixture.Ingress.NotifyCommitted(fixture.Identity(ulong.MaxValue), commit.Revision);
        await fixture.WaitForRevisionAsync(commit.Revision);

        using BScanRasterRegion region = await fixture.RenderAsync(ulong.MaxValue,
            ulong.MaxValue, 1, 4);

        Assert.AreEqual(new BScanAcquisitionRange(ulong.MaxValue, ulong.MaxValue),
            region.Snapshot.ResidentRange);
        Assert.AreEqual(ulong.MaxValue,
            region.Snapshot.SelectableTraceIdentities[0]!.Value.AcquisitionIndex);
    }

    [TestMethod]
    public async Task InitialRevisionCannotBeNotifiedAsCommitted()
    {
        await using var fixture = await Fixture.CreateAsync(maximumColumns: 256);
        Assert.ThrowsExactly<ArgumentException>(() =>
            fixture.Ingress.NotifyCommitted(fixture.Identity(0), InspectionRevision.Initial));
    }

    [TestMethod]
    public void BScanStorageAndCacheTypesCannotOwnRfShortBuffers()
    {
        Type shortOwner = typeof(System.Buffers.IMemoryOwner<short>);
        string[] violations = typeof(BScanIngress).Assembly.GetTypes()
            .Where(static type => type.Namespace == typeof(BScanIngress).Namespace &&
                type.Name.Contains("BScan", StringComparison.Ordinal))
            .SelectMany(static type => type.GetFields(BindingFlags.Instance | BindingFlags.NonPublic |
                BindingFlags.Public).Select(field => (type, field)))
            .Where(item => item.field.FieldType == typeof(short[]) ||
                item.field.FieldType == typeof(Memory<short>) || shortOwner.IsAssignableFrom(item.field.FieldType))
            .Select(static item => $"{item.type.FullName}.{item.field.Name}")
            .ToArray();

        Assert.IsEmpty(violations, string.Join(", ", violations));
    }

    [TestMethod]
    public async Task RevisionChangingExactlyBetweenPagesRejectsCandidate()
    {
        await using var fixture = await Fixture.CreateAsync(maximumColumns: 1024);
        InspectionAppendResult last = default;
        for (ulong index = 0; index <= 256; index++)
        { last = await fixture.AppendAsync(index, sequence: index); }
        fixture.Ingress.NotifyCommitted(fixture.Identity(256), last.Revision);
        await fixture.WaitForRevisionAsync(last.Revision);
        fixture.Provider.ResetRangeReadCount();
        fixture.Provider.RevisionOverride = read => read == 2
            ? new(last.Revision.Value + 1)
            : null;

        BScanRasterResult result = await fixture.RenderResultAsync(0, 256, 257, 4);

        Assert.AreEqual(BScanRasterResultKind.Stale, result.Kind);
        Assert.IsNull(result.Region);
    }

    [TestMethod]
    public async Task NewRevisionImmediatelyBeforeCandidateValidationRejectsStaleRaster()
    {
        await using var fixture = await Fixture.CreateAsync(maximumColumns: 256);
        InspectionAppendResult first = await fixture.AppendAsync(0, sequence: 0);
        fixture.Ingress.NotifyCommitted(fixture.Identity(0), first.Revision);
        await fixture.WaitForRevisionAsync(first.Revision);
        var candidateReached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var releaseCandidate = new ManualResetEventSlim();
        fixture.Delivery.BeforeRasterCandidateValidation = () =>
        {
            candidateReached.TrySetResult();
            if (!releaseCandidate.Wait(TimeSpan.FromSeconds(10)))
            { throw new TimeoutException("The raster candidate gate was not released."); }
        };

        Task<BScanRasterResult> render = Task.Run(async () =>
            await fixture.RenderResultAsync(0, 0, 1, 4));
        await candidateReached.Task.WaitAsync(TimeSpan.FromSeconds(10));
        try
        {
            InspectionAppendResult second = await fixture.AppendAsync(1, sequence: 1);
            fixture.Ingress.NotifyCommitted(fixture.Identity(1), second.Revision);
        }
        finally { releaseCandidate.Set(); }
        BScanRasterResult result = await render.WaitAsync(TimeSpan.FromSeconds(10));
        fixture.Delivery.BeforeRasterCandidateValidation = null;

        Assert.AreEqual(BScanRasterResultKind.Stale, result.Kind);
        Assert.IsNull(result.Region);
    }

    [TestMethod]
    public async Task UnnotifiedRevisionAfterTicketKeepsCoherentHistoricalRasterUntilSignalConverges()
    {
        await using var fixture = await Fixture.CreateAsync(maximumColumns: 256);
        InspectionAppendResult first = await fixture.AppendAsync(0, sequence: 0);
        fixture.Ingress.NotifyCommitted(fixture.Identity(0), first.Revision);
        await fixture.WaitForRevisionAsync(first.Revision);
        InspectionAppendResult second = default;
        var candidateReached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var releaseCandidate = new ManualResetEventSlim();
        fixture.Delivery.BeforeRasterCandidateValidation = () =>
        {
            candidateReached.TrySetResult();
            if (!releaseCandidate.Wait(TimeSpan.FromSeconds(10)))
            { throw new TimeoutException("The historical raster candidate gate was not released."); }
        };
        Task<BScanRasterResult> render = Task.Run(async () =>
            await fixture.RenderResultAsync(0, 0, 1, 4));
        await candidateReached.Task.WaitAsync(TimeSpan.FromSeconds(10));
        try { second = await fixture.AppendAsync(1, sequence: 1); }
        finally { releaseCandidate.Set(); }
        BScanRasterResult result = await render.WaitAsync(TimeSpan.FromSeconds(10));
        fixture.Delivery.BeforeRasterCandidateValidation = null;

        Assert.AreEqual(BScanRasterResultKind.Available, result.Kind);
        Assert.AreEqual(first.Revision.Value, result.Region!.Snapshot.StoreRevision);
        result.Region.Dispose();
        fixture.Ingress.NotifyCommitted(fixture.Identity(1), second.Revision);
        await fixture.WaitForRevisionAsync(second.Revision);
    }

    [TestMethod]
    public async Task AdmissionCloseImmediatelyBeforeCandidateValidationRejectsRaster()
    {
        await using var fixture = await Fixture.CreateAsync(maximumColumns: 256);
        InspectionAppendResult first = await fixture.AppendAsync(0, sequence: 0);
        fixture.Ingress.NotifyCommitted(fixture.Identity(0), first.Revision);
        await fixture.WaitForRevisionAsync(first.Revision);
        fixture.Delivery.BeforeRasterCandidateValidation = () =>
            fixture.Ingress.StopAdmission(fixture.RunId);

        BScanRasterResult result = await fixture.RenderResultAsync(0, 0, 1, 4);

        Assert.AreEqual(BScanRasterResultKind.Stale, result.Kind);
        await fixture.Ingress.WaitForDrainAsync(fixture.RunId);
        fixture.Ingress.FreezeRun(fixture.RunId);
    }

    [TestMethod]
    public async Task DrainWaitsForAlreadyAdmittedObserverCallback()
    {
        await using var fixture = await Fixture.CreateAsync(maximumColumns: 256);
        InspectionAppendResult first = await fixture.AppendAsync(0, sequence: 0);
        fixture.Ingress.NotifyCommitted(fixture.Identity(0), first.Revision);
        await fixture.WaitForRevisionAsync(first.Revision);
        var observer = new BlockingObserver();
        using IDisposable subscription = fixture.Delivery.Subscribe(observer);
        Assert.IsTrue(fixture.Delivery.RegisterConsumer("callback-drain"));
        fixture.Delivery.RequestRender(fixture.Request(0, 0, 1, 4, "callback-drain"));
        await observer.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));

        fixture.Ingress.StopAdmission(fixture.RunId);
        ValueTask drain = fixture.Ingress.WaitForDrainAsync(fixture.RunId);
        Assert.IsFalse(drain.IsCompleted);
        observer.Release.Set();
        await drain.AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        fixture.Ingress.FreezeRun(fixture.RunId);
    }

    [TestMethod]
    public async Task DisposeWaitsForAlreadyAdmittedObserverCallback()
    {
        await using var fixture = await Fixture.CreateAsync(maximumColumns: 256);
        InspectionAppendResult first = await fixture.AppendAsync(0, sequence: 0);
        fixture.Ingress.NotifyCommitted(fixture.Identity(0), first.Revision);
        await fixture.WaitForRevisionAsync(first.Revision);
        var observer = new BlockingObserver();
        using IDisposable subscription = fixture.Delivery.Subscribe(observer);
        Assert.IsTrue(fixture.Delivery.RegisterConsumer("callback-dispose"));
        fixture.Delivery.RequestRender(fixture.Request(0, 0, 1, 4, "callback-dispose"));
        await observer.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));

        ValueTask dispose = fixture.Ingress.DisposeAsync();
        Assert.IsFalse(dispose.IsCompleted);
        observer.Release.Set();
        await dispose.AsTask().WaitAsync(TimeSpan.FromSeconds(10));
    }

    [TestMethod]
    public async Task StopDuringActiveLanczosReturnsImmediatelyAndDrainsAfterNextRow()
    {
        await using var fixture = await Fixture.CreateAsync(256, 2048);
        InspectionAppendResult commit = await fixture.AppendAsync(0, 0);
        fixture.Ingress.NotifyCommitted(fixture.Identity(0), commit.Revision);
        await fixture.WaitForRevisionAsync(commit.Revision);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        fixture.Delivery.AfterLanczosKernelRowProcessed = _ =>
        { entered.TrySetResult(); Assert.IsTrue(release.Wait(TimeSpan.FromSeconds(10))); };
        BScanRenderRequest request = fixture.Request(0, 0, 64, 128) with
        { Resampling = new(Scan2DInterpolationMode.Lanczos) };
        Task<BScanRasterResult> render = Task.Run(async () => await fixture.Delivery.GetRegionAsync(request));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));

        fixture.Ingress.StopAdmission(fixture.RunId);
        ValueTask drain = fixture.Ingress.WaitForDrainAsync(fixture.RunId);
        Assert.IsFalse(drain.IsCompleted);
        release.Set();
        BScanRasterResult result = await render.WaitAsync(TimeSpan.FromSeconds(10));
        await drain.AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        fixture.Ingress.FreezeRun(fixture.RunId);

        Assert.AreEqual(BScanRasterResultKind.Stale, result.Kind);
        Assert.AreEqual(fixture.Provider.LeasesCreated, fixture.Provider.LeasesDisposed);
    }

    [TestMethod]
    public async Task DisposeDuringActiveLanczosCancelsAndBalancesAtNextRow()
    {
        await using var fixture = await Fixture.CreateAsync(256, 2048);
        InspectionAppendResult commit = await fixture.AppendAsync(0, 0);
        fixture.Ingress.NotifyCommitted(fixture.Identity(0), commit.Revision);
        await fixture.WaitForRevisionAsync(commit.Revision);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        fixture.Delivery.AfterLanczosKernelRowProcessed = _ =>
        { entered.TrySetResult(); Assert.IsTrue(release.Wait(TimeSpan.FromSeconds(10))); };
        BScanRenderRequest request = fixture.Request(0, 0, 64, 128) with
        { Resampling = new(Scan2DInterpolationMode.Lanczos) };
        Task<BScanRasterResult> render = Task.Run(async () => await fixture.Delivery.GetRegionAsync(request));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));

        ValueTask dispose = fixture.Delivery.DisposeAsync();
        Assert.IsFalse(dispose.IsCompleted);
        release.Set();
        BScanRasterResult result = await render.WaitAsync(TimeSpan.FromSeconds(10));
        await dispose.AsTask().WaitAsync(TimeSpan.FromSeconds(10));

        Assert.AreEqual(BScanRasterResultKind.NotAvailable, result.Kind);
        Assert.AreEqual(fixture.Provider.LeasesCreated, fixture.Provider.LeasesDisposed);
    }

    [TestMethod]
    public async Task ModeChangeSupersedesActiveLanczosAndOnlyNewModeCanCommit()
    {
        await using var fixture = await Fixture.CreateAsync(256, 2048);
        InspectionAppendResult commit = await fixture.AppendAsync(0, 0);
        fixture.Ingress.NotifyCommitted(fixture.Identity(0), commit.Revision);
        await fixture.WaitForRevisionAsync(commit.Revision);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        fixture.Delivery.AfterLanczosKernelRowProcessed = _ =>
        { entered.TrySetResult(); Assert.IsTrue(release.Wait(TimeSpan.FromSeconds(10))); };
        BScanRenderRequest lanczos = fixture.Request(0, 0, 64, 128) with
        { Resampling = new(Scan2DInterpolationMode.Lanczos) };
        Task<BScanRasterResult> oldRender = Task.Run(async () =>
            await fixture.Delivery.GetRegionAsync(lanczos));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        BScanRenderRequest none = lanczos with
        { Resampling = new(Scan2DInterpolationMode.None) };
        fixture.Delivery.RequestRender(none);
        release.Set();

        BScanRasterResult oldResult = await oldRender.WaitAsync(TimeSpan.FromSeconds(10));
        fixture.Delivery.AfterLanczosKernelRowProcessed = null;
        using BScanRasterRegion current = await fixture.RenderAsync(0, 0, 64, 128);

        Assert.AreEqual(BScanRasterResultKind.Stale, oldResult.Kind);
        Assert.AreEqual(Scan2DInterpolationMode.None, current.Snapshot.Resampling.Mode);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly InspectionDataStoreRegistry _registry;
        private readonly IInspectionRunWriter _writer;
        private readonly ConventionalAcquisitionConfiguration _configuration;
        internal AcquisitionRunId RunId { get; }
        internal InspectionId InspectionId { get; }
        internal UtSourceId SourceId { get; } = new("bscan-range-test");
        internal TrackingProvider Provider { get; }
        internal BScanIngress Ingress { get; }
        internal BScanVisualDelivery Delivery { get; }

        private Fixture(InspectionDataStoreRegistry registry, IInspectionRunWriter writer,
            TrackingProvider provider, BScanIngress ingress, BScanVisualDelivery delivery,
            InspectionId inspectionId, AcquisitionRunId runId, int sampleCount)
        {
            _registry = registry; _writer = writer; Provider = provider; Ingress = ingress;
            Delivery = delivery; InspectionId = inspectionId; RunId = runId;
            _configuration = new(new(0), sampleCount, 10);
        }

        internal static async Task<Fixture> CreateAsync(int maximumColumns, int sampleCount = 4)
        {
            var inspectionId = new InspectionId(Guid.NewGuid());
            var runId = new AcquisitionRunId(Guid.NewGuid());
            var registry = new InspectionDataStoreRegistry(inspectionId,
                new InMemoryInspectionStoreOptions(16 * 1024 * 1024, 4096));
            IInspectionRunWriter writer = await registry.Store.OpenRunAsync(runId);
            var provider = new TrackingProvider(registry.Store);
            var options = new BScanOptions(maximumColumns: maximumColumns);
            var ingress = new BScanIngress(provider, provider, options);
            ingress.OpenRun(runId);
            return new Fixture(registry, writer, provider, ingress,
                new BScanVisualDelivery(ingress), inspectionId, runId, sampleCount);
        }
        internal TraceIdentity Identity(ulong index) => new(new(InspectionId, RunId,
            SourceId, _configuration.PhysicalChannelId), index);

        internal ValueTask<InspectionAppendResult> AppendAsync(ulong index, ulong sequence)
        {
            long ticks = index > (ulong)TimeSpan.TicksPerDay ? 0 : (long)index;
            var descriptor = new TraceDescriptor(Identity(index), sequence, TimeSpan.FromTicks(ticks),
                DateTimeOffset.UnixEpoch + TimeSpan.FromTicks(ticks), null, _configuration.SampleCount,
                _configuration.SampleRateHz, _configuration.FirstSampleOffsetSeconds, _configuration.SignalMode);
            var samples = new short[_configuration.SampleCount];
            samples[0] = (short)(index + 1);
            if (samples.Length > 2) { samples[2] = -1; }
            if (samples.Length > 3) { samples[3] = 2; }
            return _writer.CommitAsync(new InspectionCommitInput(new InspectionTraceInput(descriptor,
                samples)));
        }

        internal ValueTask<InspectionAppendResult> AppendSamplesAsync(ulong index, ulong sequence,
            short[] samples)
        {
            if (samples.Length != _configuration.SampleCount)
            { throw new ArgumentException("The sample count must match the fixture configuration.", nameof(samples)); }
            long ticks = index > (ulong)TimeSpan.TicksPerDay ? 0 : (long)index;
            var descriptor = new TraceDescriptor(Identity(index), sequence, TimeSpan.FromTicks(ticks),
                DateTimeOffset.UnixEpoch + TimeSpan.FromTicks(ticks), null, _configuration.SampleCount,
                _configuration.SampleRateHz, _configuration.FirstSampleOffsetSeconds,
                _configuration.SignalMode);
            return _writer.CommitAsync(new InspectionCommitInput(new InspectionTraceInput(descriptor,
                samples)));
        }

        internal async Task WaitForRevisionAsync(InspectionRevision revision)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (Ingress.CurrentVisualState?.StoreState.StoreRevision != revision.Value)
            { await Task.Delay(10, timeout.Token); }
        }

        internal async Task<BScanRasterRegion> RenderAsync(ulong first, ulong last, int width, int height)
        {
            BScanRasterResult result = await RenderResultAsync(first, last, width, height);
            Assert.AreEqual(BScanRasterResultKind.Available, result.Kind, result.Error);
            return result.Region!;
        }

        internal ValueTask<BScanRasterResult> RenderResultAsync(ulong first, ulong last, int width, int height)
        {
            return Delivery.GetRegionAsync(Request(first, last, width, height));
        }

        internal BScanRenderRequest Request(ulong first, ulong last, int width, int height,
            string consumerId = "default",
            Scan2DInterpolationMode resamplingMode = Scan2DInterpolationMode.None) => new(RunId,
                BScanSpatialViewportOperations.Reset(new(first, last)),
                ScanTimeViewportOperations.Create(0,
                    _configuration.SampleCount / _configuration.SampleRateHz), width, height, 1,
                ConsumerId: consumerId, Resampling: new(resamplingMode));

        public async ValueTask DisposeAsync()
        {
            Delivery.BeforeRasterCandidateCommit = null;
            Delivery.BeforeRasterCandidateValidation = null;
            Ingress.BeforeReconcileWorkerExit = null;
            Ingress.BeforeVisualStateCommit = null;
            var failures = new List<Exception>();
            await Capture("delivery", () => Delivery.DisposeAsync().AsTask());
            await Capture("ingress", () => Ingress.DisposeAsync().AsTask());
            await Capture("writer", () => _writer.DisposeAsync().AsTask());
            await Capture("registry", () => _registry.DisposeAsync().AsTask());
            if (failures.Count != 0)
            { throw new AggregateException("B-Scan fixture cleanup failed.", failures); }

            async Task Capture(string stage, Func<Task> dispose)
            {
                try { await dispose().WaitAsync(TimeSpan.FromSeconds(10)); }
                catch (Exception error)
                {
                    failures.Add(new InvalidOperationException(
                        $"{stage} cleanup failed; ingress={Ingress.Diagnostics}; delivery={Delivery.Diagnostics}", error));
                }
            }
        }

    }

    private sealed class BlockingObserver : IObserver<BScanVisualUpdate>
    {
        internal TaskCompletionSource Entered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal ManualResetEventSlim Release { get; } = new();
        public void OnNext(BScanVisualUpdate value)
        { Entered.TrySetResult(); Release.Wait(TimeSpan.FromSeconds(10)); }
        public void OnError(Exception error) { }
        public void OnCompleted() { }
    }

    private sealed class TrackingProvider(IInspectionDataStore inner) :
        IInspectionRangeProvider, IInspectionStreamStateProvider
    {
        internal System.Collections.Concurrent.ConcurrentQueue<InspectionRangeQuery> Queries { get; } = [];
        internal int LeasesCreated;
        internal int LeasesDisposed;
        internal int ActiveLeases;
        internal int MaximumActiveLeases;
        internal Func<int, InspectionRevision?>? RevisionOverride { get; set; }
        private int _rangeReads;
        private int _blockState;
        private TaskCompletionSource? _releaseBlockedStateRead;
        internal TaskCompletionSource StateReadEntered { get; private set; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal bool BlockedStateReadCancelled { get; private set; }
        internal void BlockNextStateRead()
        {
            _blockState = 1;
            StateReadEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _releaseBlockedStateRead = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        internal void ReleaseBlockedStateRead() => _releaseBlockedStateRead?.TrySetResult();
        internal void ResetRangeReadCount() => Interlocked.Exchange(ref _rangeReads, 0);
        public async ValueTask<InspectionRangeReadResult> ReadRangeAsync(InspectionRangeQuery query,
            CancellationToken cancellationToken = default)
        {
            Queries.Enqueue(query);
            InspectionRangeReadResult result = await inner.ReadRangeAsync(query, cancellationToken);
            int read = Interlocked.Increment(ref _rangeReads);
            var items = result.Items.Select(item => item.Lease is null
                ? InspectionRangeItem.Unavailable(item.Identity, item.Status)
                : InspectionRangeItem.Available(new CountingLease(item.Lease, this))).ToArray();
            return InspectionRangeReadResult.Page(
                RevisionOverride?.Invoke(read) ?? result.Revision, items);
        }
        public async ValueTask<InspectionStreamStateReadResult> ReadStreamStateAsync(UtStreamIdentity stream,
            CancellationToken cancellationToken = default)
        {
            if (Interlocked.Exchange(ref _blockState, 0) != 0)
            {
                StateReadEntered.TrySetResult();
                try { await _releaseBlockedStateRead!.Task.WaitAsync(cancellationToken); }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    BlockedStateReadCancelled = true;
                    throw;
                }
            }
            return await inner.ReadStreamStateAsync(stream, cancellationToken);
        }
        private sealed class CountingLease(IInspectionTraceLease inner, TrackingProvider owner) : IInspectionTraceLease
        {
            private IInspectionTraceLease? _inner = inner;
            public TraceDescriptor Descriptor => _inner!.Descriptor;
            public IReadOnlyUtSamples Samples => _inner!.Samples;
            public void Dispose()
            {
                IInspectionTraceLease? lease = Interlocked.Exchange(ref _inner, null);
                if (lease is null) { return; }
                lease.Dispose();
                Interlocked.Increment(ref owner.LeasesDisposed);
                Interlocked.Decrement(ref owner.ActiveLeases);
            }
            private readonly int _counted = Count(owner);
            private static int Count(TrackingProvider owner)
            {
                Interlocked.Increment(ref owner.LeasesCreated);
                int active = Interlocked.Increment(ref owner.ActiveLeases);
                int observed;
                while (active > (observed = Volatile.Read(ref owner.MaximumActiveLeases)) &&
                    Interlocked.CompareExchange(ref owner.MaximumActiveLeases, active, observed) != observed) { }
                return active;
            }
        }
    }
}
