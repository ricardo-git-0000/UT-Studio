using System.Buffers;
using UTStudio.Domain.Acquisition;
using UTStudio.Visualization.Core;

namespace UTStudio.Core.Tests.Visualization;

[TestClass]
public sealed class BScanVisualProtocolTests
{
    [TestMethod]
    public async Task FirstPublicationHasExactInitialReason()
    {
        await using Harness harness = await Harness.CreateAsync();
        using BScanRasterRegion region = await harness.RenderAsync(harness.Request());
        AssertFull(region.Snapshot, BScanFullRefreshReason.Initial, 0, 1, harness.Metadata.RunId);
    }

    [TestMethod]
    public async Task IncompatibleBaseHasExactReasonAndNeverProducesIncrementalRegion()
    {
        await using Harness harness = await Harness.CreateAsync();
        using BScanRasterRegion baseline = await harness.RenderAsync(harness.Request());
        BScanRenderRequest request = harness.From(baseline.Snapshot) with { DisplayedStoreRevision = 0 };
        using BScanRasterRegion region = await harness.RenderAsync(request);
        AssertFull(region.Snapshot, BScanFullRefreshReason.BaseRevisionMismatch, 0, 1, harness.Metadata.RunId);
        Assert.AreEqual(region.Snapshot.RasterWidth, region.Width);
    }

    [TestMethod]
    public async Task SnapshotVersionGapHasExactReason()
    {
        await using Harness harness = await Harness.CreateAsync();
        using BScanRasterRegion baseline = await harness.RenderAsync(harness.Request());
        using BScanRasterRegion region = await harness.RenderAsync(harness.From(baseline.Snapshot) with
        { DisplayedSnapshotVersion = baseline.Snapshot.SnapshotVersion + 1 });
        AssertFull(region.Snapshot, BScanFullRefreshReason.SnapshotVersionGap,
            baseline.Snapshot.StoreRevision, baseline.Snapshot.StoreRevision, harness.Metadata.RunId);
    }

    [TestMethod]
    public async Task IncompatibleWrapHasExactReason()
    {
        await using Harness harness = await Harness.CreateAsync();
        using BScanRasterRegion baseline = await harness.RenderAsync(harness.Request());
        using BScanRasterRegion region = await harness.RenderAsync(harness.From(baseline.Snapshot) with
        { DisplayedWrapGeneration = baseline.Snapshot.WrapGeneration + 1 });
        AssertFull(region.Snapshot, BScanFullRefreshReason.WrapChanged,
            baseline.Snapshot.StoreRevision, baseline.Snapshot.StoreRevision, harness.Metadata.RunId);
    }

    [TestMethod]
    public async Task SpatialViewportChangeHasExactReason()
    {
        await using Harness harness = await Harness.CreateAsync(columns: 4);
        using BScanRasterRegion baseline = await harness.RenderAsync(harness.Request());
        BScanSpatialViewport changed = BScanSpatialViewportOperations.ZoomAt(
            baseline.Snapshot.SpatialViewport, new(0, 3), .5m, 2m);
        using BScanRasterRegion region = await harness.RenderAsync(harness.From(baseline.Snapshot) with
        { SpatialViewport = changed });
        AssertFull(region.Snapshot, BScanFullRefreshReason.ViewportChanged,
            baseline.Snapshot.StoreRevision, baseline.Snapshot.StoreRevision, harness.Metadata.RunId);
    }

    [TestMethod]
    public async Task TemporalViewportChangeHasExactReason()
    {
        await using Harness harness = await Harness.CreateAsync();
        using BScanRasterRegion baseline = await harness.RenderAsync(harness.Request());
        ScanTimeViewport changed = ScanTimeViewportOperations.Zoom(baseline.Snapshot.TimeViewport, .000002, 2);
        using BScanRasterRegion region = await harness.RenderAsync(harness.From(baseline.Snapshot) with
        { TimeViewport = changed });
        AssertFull(region.Snapshot, BScanFullRefreshReason.ViewportChanged,
            baseline.Snapshot.StoreRevision, baseline.Snapshot.StoreRevision, harness.Metadata.RunId);
    }

    [TestMethod]
    public async Task RasterDimensionChangeHasExactReason()
    {
        await using Harness harness = await Harness.CreateAsync();
        using BScanRasterRegion baseline = await harness.RenderAsync(harness.Request());
        using BScanRasterRegion region = await harness.RenderAsync(harness.From(baseline.Snapshot) with { PixelWidth = 3 });
        AssertFull(region.Snapshot, BScanFullRefreshReason.ResolutionChanged,
            baseline.Snapshot.StoreRevision, baseline.Snapshot.StoreRevision, harness.Metadata.RunId);
    }

    [TestMethod]
    public async Task RenderConfigurationChangeHasExactReason()
    {
        await using Harness harness = await Harness.CreateAsync();
        using BScanRasterRegion baseline = await harness.RenderAsync(harness.Request());
        using BScanRasterRegion region = await harness.RenderAsync(harness.From(baseline.Snapshot) with
        { RenderConfigurationVersion = 2 });
        AssertFull(region.Snapshot, BScanFullRefreshReason.RenderConfigurationChanged,
            baseline.Snapshot.StoreRevision, baseline.Snapshot.StoreRevision, harness.Metadata.RunId);
    }

    [TestMethod]
    public async Task PartiallyEvictedDirtyRangeHasExactReasonAndNoGenerationMix()
    {
        await using Harness harness = await Harness.CreateAsync(columns: 2);
        await harness.AddAsync(1, 1, [11, 11, 11, 11]);
        using BScanRasterRegion baseline = await harness.RenderAsync(harness.Request());
        await harness.AddAsync(2, 2, [22, 22, 22, 22]);
        using BScanRasterRegion region = await harness.RenderAsync(harness.From(baseline.Snapshot) with
        { DisplayedWrapGeneration = baseline.Snapshot.WrapGeneration + 1 });

        AssertFull(region.Snapshot, BScanFullRefreshReason.DirtyRangeEvicted,
            baseline.Snapshot.StoreRevision, 3, harness.Metadata.RunId);
        Assert.AreEqual(new BScanAcquisitionRange(1, 2), region.Snapshot.ResidentRange);
        Assert.AreEqual(0, region.Pixels.Span[0]);
    }

    [TestMethod]
    public async Task CompletelyEvictedDirtyRangeForcesFullAndLeavesNoOwners()
    {
        await using Harness harness = await Harness.CreateAsync(columns: 2);
        await harness.AddAsync(1, 1, [11, 11, 11, 11]);
        using BScanRasterRegion baseline = await harness.RenderAsync(harness.Request());
        BScanVisualSnapshot baselineSnapshot = baseline.Snapshot;
        baseline.Dispose();
        await harness.AddAsync(2, 2, [22, 22, 22, 22]);
        await harness.AddAsync(3, 3, [33, 33, 33, 33]);
        using (BScanRasterRegion region = await harness.RenderAsync(harness.From(baselineSnapshot) with
        { DisplayedWrapGeneration = baselineSnapshot.WrapGeneration + 1 }))
        {
            Assert.IsTrue(region.Snapshot.FullRefreshReason.HasFlag(BScanFullRefreshReason.DirtyRangeEvicted));
            Assert.AreEqual(new BScanAcquisitionRange(2, 3), region.Snapshot.ResidentRange);
            Assert.IsTrue(region.Snapshot.RequiresFullRefresh);
        }
        Assert.AreEqual(0, harness.Delivery.Diagnostics.ActiveRasterOwners);
        Assert.AreEqual(0L, harness.Delivery.Diagnostics.VisualBytesRented);
    }

    [TestMethod]
    public async Task ConstructionErrorForcesExactRecoveryOnNextCandidate()
    {
        var pool = new SwitchableBytePool();
        await using Harness harness = await Harness.CreateAsync(pool: pool);
        using BScanRasterRegion baseline = await harness.RenderAsync(harness.Request());
        BScanVisualSnapshot baselineSnapshot = baseline.Snapshot;
        baseline.Dispose();
        await harness.AddAsync(1, 1, [4, 5, 6, 7]);
        BScanRenderRequest request = harness.From(baselineSnapshot);
        pool.FailNext = true;
        BScanRasterResult failed = await harness.Delivery.GetRegionAsync(request);
        Assert.AreEqual(BScanRasterResultKind.Faulted, failed.Kind);
        using BScanRasterRegion recovered = await harness.RenderAsync(request);
        AssertFull(recovered.Snapshot, BScanFullRefreshReason.Recovery,
            baselineSnapshot.StoreRevision, 2, harness.Metadata.RunId);
        Assert.AreEqual(0, pool.ActiveOwners - 1); // only recovered remains active here
    }

    [TestMethod]
    public async Task StoreRevisionChangeDisposesStaleCandidateAndForcesRecovery()
    {
        await using Harness harness = await Harness.CreateAsync(columns: 4);
        using BScanRasterRegion baseline = await harness.RenderAsync(harness.Request());
        BScanVisualSnapshot baselineSnapshot = baseline.Snapshot;
        baseline.Dispose();
        await harness.AddAsync(1, 1, [4, 5, 6, 7]);
        BScanRenderRequest request = harness.From(baselineSnapshot);
        var entered = new ManualResetEventSlim();
        var release = new ManualResetEventSlim();
        harness.Delivery.BeforeRasterCandidateValidation = () => { entered.Set(); release.Wait(); };
        Task<BScanRasterResult> candidate = Task.Run(async () => await harness.Delivery.GetRegionAsync(request));
        Assert.IsTrue(entered.Wait(TimeSpan.FromSeconds(10)));
        await harness.AddAsync(2, 2, [8, 9, 10, 11]);
        release.Set();
        Assert.AreEqual(BScanRasterResultKind.Stale, (await candidate.WaitAsync(TimeSpan.FromSeconds(10))).Kind);
        harness.Delivery.BeforeRasterCandidateValidation = null;
        using BScanRasterRegion recovered = await harness.RenderAsync(request);
        AssertFull(recovered.Snapshot, BScanFullRefreshReason.Recovery,
            baselineSnapshot.StoreRevision, 3, harness.Metadata.RunId);
        Assert.AreEqual(1, harness.Delivery.Diagnostics.ActiveRasterOwners);
    }

    [TestMethod]
    public async Task NewRunForcesExactRunChangedReason()
    {
        await using Harness harness = await Harness.CreateAsync();
        using BScanRasterRegion baseline = await harness.RenderAsync(harness.Request());
        AcquisitionRunId nextRun = new(Guid.Parse("43000000-0000-0000-0000-000000000043"));
        await harness.ReopenAsync(nextRun);
        BScanRenderRequest request = harness.Request(nextRun) with
        {
            DisplayedSnapshotVersion = baseline.Snapshot.SnapshotVersion,
            DisplayedStoreRevision = baseline.Snapshot.StoreRevision,
            DisplayedWrapGeneration = baseline.Snapshot.WrapGeneration
        };
        using BScanRasterRegion region = await harness.RenderAsync(request);
        AssertFull(region.Snapshot, BScanFullRefreshReason.RunChanged,
            baseline.Snapshot.StoreRevision, 1, nextRun);
    }

    private static void AssertFull(BScanVisualSnapshot snapshot, BScanFullRefreshReason exactReason,
        ulong expectedBase, ulong expectedResult, AcquisitionRunId runId)
    {
        Assert.IsTrue(snapshot.RequiresFullRefresh);
        Assert.AreEqual(exactReason, snapshot.FullRefreshReason);
        Assert.AreEqual(expectedBase, snapshot.BaseStoreRevision);
        Assert.AreEqual(expectedResult, snapshot.StoreRevision);
        Assert.AreEqual(runId, snapshot.RunId);
        Assert.IsGreaterThan(0UL, snapshot.SnapshotVersion);
        Assert.IsGreaterThanOrEqualTo(snapshot.ResidentRange.First, snapshot.ResidentRange.Last);
    }

    private sealed class Harness : IAsyncDisposable
    {
        private ulong _sequence;
        private Harness(BScanIngress ingress, BScanVisualDelivery delivery,
            ConventionalUtFrameMetadata metadata, int columns)
        { Ingress = ingress; Delivery = delivery; Metadata = metadata; Columns = columns; }
        internal BScanIngress Ingress { get; }
        internal BScanVisualDelivery Delivery { get; }
        internal ConventionalUtFrameMetadata Metadata { get; private set; }
        internal int Columns { get; }

        internal static async Task<Harness> CreateAsync(int columns = 4, IByteOwnerPool? pool = null)
        {
            ConventionalUtFrameMetadata metadata = CreateMetadata(
                new(Guid.Parse("41000000-0000-0000-0000-000000000041")));
            var options = new BScanOptions(1_000_000, 16 * 1024 * 1024, columns, columns, 2);
            var ingress = new BScanIngress(options);
            ingress.OpenRun(metadata);
            var delivery = pool is null ? new BScanVisualDelivery(ingress) : new BScanVisualDelivery(ingress, null, pool);
            var harness = new Harness(ingress, delivery, metadata, columns);
            await harness.AddAsync(0, 0, [0, -100, 100, 1]);
            return harness;
        }

        internal async Task AddAsync(ulong sequence, ulong acquisitionIndex, short[] samples)
        {
            var indexed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Ingress.AfterIndexCommit = _ => indexed.TrySetResult();
            Ingress.Accept(Metadata, sequence, acquisitionIndex, TimeSpan.FromTicks((long)sequence), null, samples);
            await indexed.Task.WaitAsync(TimeSpan.FromSeconds(10));
            _sequence = sequence + 1;
        }

        internal BScanRenderRequest Request() => Request(Metadata.RunId);
        internal BScanRenderRequest Request(AcquisitionRunId runId) => new(runId,
            BScanSpatialViewportOperations.Reset(new(0, (ulong)Columns - 1)),
            ScanTimeViewportOperations.Create(0, .000004), Columns, 4, 1);
        internal BScanRenderRequest From(BScanVisualSnapshot snapshot) => Request(snapshot.RunId) with
        {
            DisplayedSnapshotVersion = snapshot.SnapshotVersion,
            DisplayedStoreRevision = snapshot.StoreRevision,
            DisplayedWrapGeneration = snapshot.WrapGeneration
        };
        internal async Task<BScanRasterRegion> RenderAsync(BScanRenderRequest request)
        {
            BScanRasterResult result = await Delivery.GetRegionAsync(request);
            Assert.AreEqual(BScanRasterResultKind.Available, result.Kind, result.Error);
            return result.Region!;
        }
        internal async Task ReopenAsync(AcquisitionRunId runId)
        {
            Ingress.CloseRun(Metadata.RunId);
            await Ingress.WaitForCloseAsync(Metadata.RunId);
            Metadata = CreateMetadata(runId);
            _sequence = 0;
            Ingress.OpenRun(Metadata);
            await AddAsync(0, 0, [1, 2, 3, 4]);
        }
        public async ValueTask DisposeAsync()
        {
            await Delivery.DisposeAsync();
            await Ingress.DisposeAsync();
        }
        private static ConventionalUtFrameMetadata CreateMetadata(AcquisitionRunId runId) => new(
            new UtSourceId("bscan-protocol"), runId,
            new(new(1), 4, 1_000_000, 0, UtSignalMode.Rf), DateTimeOffset.UnixEpoch);
    }

    private sealed class SwitchableBytePool : IByteOwnerPool
    {
        internal bool FailNext { get; set; }
        internal int ActiveOwners;
        public IMemoryOwner<byte> Rent(int minimumLength)
        {
            if (FailNext) { FailNext = false; throw new InvalidOperationException("synthetic raster failure"); }
            Interlocked.Increment(ref ActiveOwners);
            return new Owner(new byte[minimumLength], () => Interlocked.Decrement(ref ActiveOwners));
        }
        private sealed class Owner(byte[] bytes, Action release) : IMemoryOwner<byte>
        {
            private byte[]? _bytes = bytes;
            public Memory<byte> Memory => _bytes ?? throw new ObjectDisposedException(nameof(Owner));
            public void Dispose() { if (Interlocked.Exchange(ref _bytes, null) is not null) { release(); } }
        }
    }
}
