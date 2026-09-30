using System.Diagnostics;
using System.Threading.Channels;
using UTStudio.Domain.Acquisition;
using UTStudio.Visualization.Core;
using UTStudio.Visualization.Core.Scan2D;

namespace UTStudio.Core.Tests.Visualization;

[TestClass]
[DoNotParallelize]
public sealed class BScanLanczosResponsivenessTests
{
    private static readonly TimeSpan DiagnosticTimeout = TimeSpan.FromSeconds(10);

    [TestMethod]
    public async Task StopCancelsActiveLanczosAtNextRowAndDrainsEveryResource()
    {
        await using var harness = await Harness.CreateAsync();
        using var barrier = new KernelRowBarrier();
        harness.Delivery.AfterLanczosKernelRowProcessed = barrier.Callback;
        Task<BScanRasterResult> render = Task.Run(async () => await harness.Delivery.GetRegionAsync(harness.Lanczos));
        barrier.WaitUntilEntered();
        Assert.AreEqual(1, harness.Delivery.Diagnostics.ActiveBuilds);

        harness.Delivery.Stop();
        Task drain = harness.Delivery.WaitForDrainAsync();
        Assert.IsFalse(drain.IsCompleted);
        Assert.AreEqual(BScanVisualCloseState.Closing, harness.Delivery.Diagnostics.CloseState);
        Assert.AreEqual(BScanRasterResultKind.NotAvailable,
            (await harness.Delivery.GetRegionAsync(harness.Lanczos)).Kind);
        Assert.ThrowsExactly<ObjectDisposedException>(() => harness.Delivery.RequestRender(harness.Lanczos));

        Stopwatch cancellation = Stopwatch.StartNew();
        barrier.Release();
        Assert.AreEqual(BScanRasterResultKind.NotAvailable,
            (await render.WaitAsync(DiagnosticTimeout)).Kind);
        await drain.WaitAsync(DiagnosticTimeout);
        cancellation.Stop();
        Console.WriteLine($"LANCZOS_STOP_CHECKPOINT_LATENCY_MS={cancellation.Elapsed.TotalMilliseconds:F3}");
        Assert.AreEqual(1, barrier.RowsObserved);
        AssertDeliveryDrained(harness.Delivery);
        await harness.DisposeIngressAsync();
        AssertIngressDrained(harness.Ingress);
    }

    [TestMethod]
    public async Task ConcurrentDisposeCancelsActiveLanczosAndSharesTheCloseTask()
    {
        await using var harness = await Harness.CreateAsync();
        using var barrier = new KernelRowBarrier();
        var observer = new CountingObserver();
        using IDisposable subscription = harness.Delivery.Subscribe(observer);
        harness.Delivery.AfterLanczosKernelRowProcessed = barrier.Callback;
        Task<BScanRasterResult> render = Task.Run(async () => await harness.Delivery.GetRegionAsync(harness.Lanczos));
        barrier.WaitUntilEntered();

        using var callersReady = new CountdownEvent(2);
        using var releaseCallers = new ManualResetEventSlim();
        Task<Task> firstCaller = Task.Factory.StartNew(() =>
        {
            callersReady.Signal();
            if (!releaseCallers.Wait(DiagnosticTimeout)) throw new TimeoutException("Dispose caller was not released.");
            return harness.Delivery.DisposeAsync().AsTask();
        }, CancellationToken.None, TaskCreationOptions.DenyChildAttach, TaskScheduler.Default);
        Task<Task> secondCaller = Task.Factory.StartNew(() =>
        {
            callersReady.Signal();
            if (!releaseCallers.Wait(DiagnosticTimeout)) throw new TimeoutException("Dispose caller was not released.");
            return harness.Delivery.DisposeAsync().AsTask();
        }, CancellationToken.None, TaskCreationOptions.DenyChildAttach, TaskScheduler.Default);
        Assert.IsTrue(callersReady.Wait(DiagnosticTimeout));
        releaseCallers.Set();
        Task first = await firstCaller.WaitAsync(DiagnosticTimeout);
        Task second = await secondCaller.WaitAsync(DiagnosticTimeout);
        Assert.AreSame(first, second);
        Assert.IsFalse(first.IsCompleted);
        Assert.AreEqual(BScanRasterResultKind.NotAvailable,
            (await harness.Delivery.GetRegionAsync(harness.Lanczos)).Kind);
        Assert.ThrowsExactly<ObjectDisposedException>(() => harness.Delivery.RequestRender(harness.Lanczos));

        Stopwatch cancellation = Stopwatch.StartNew();
        barrier.Release();
        Assert.AreEqual(BScanRasterResultKind.NotAvailable,
            (await render.WaitAsync(DiagnosticTimeout)).Kind);
        await Task.WhenAll(first.WaitAsync(DiagnosticTimeout), second.WaitAsync(DiagnosticTimeout));
        cancellation.Stop();
        Console.WriteLine($"LANCZOS_DISPOSE_CHECKPOINT_LATENCY_MS={cancellation.Elapsed.TotalMilliseconds:F3}");
        Assert.AreEqual(BScanVisualCloseState.Closed, harness.Delivery.Diagnostics.CloseState);
        Assert.AreEqual(harness.Delivery.Diagnostics.ObserverCallbacksStarted,
            harness.Delivery.Diagnostics.ObserverCallbacksCompleted);
        Assert.AreEqual(0, observer.Count);
        AssertDeliveryDrained(harness.Delivery);
        await harness.DisposeIngressAsync();
        AssertIngressDrained(harness.Ingress);
    }

    [TestMethod]
    public async Task SupersededLanczosNeverTransfersAndOnlyLatestCompatibleSignaturePublishes()
    {
        await using var harness = await Harness.CreateAsync();
        using BScanRasterRegion confirmed = (await harness.Delivery.GetRegionAsync(harness.None)).Region!;
        byte confirmedPixel = confirmed.Pixels.Span[0];
        using var barrier = new KernelRowBarrier();
        harness.Delivery.AfterLanczosKernelRowProcessed = barrier.Callback;
        Task<BScanRasterResult> obsolete = Task.Run(async () =>
            await harness.Delivery.GetRegionAsync(harness.Lanczos));
        barrier.WaitUntilEntered();
        Assert.AreEqual(confirmedPixel, confirmed.Pixels.Span[0]);

        var latest = harness.Lanczos with { RenderConfigurationVersion = 7 };
        var observer = new ChannelObserver();
        using IDisposable subscription = harness.Delivery.Subscribe(observer);
        BScanVisualUpdate replay = await observer.NextAsync();
        Assert.AreEqual(Scan2DInterpolationMode.None, replay.Snapshot.Resampling.Mode);
        harness.Delivery.RequestRender(latest);
        BScanVisualUpdate update = await observer.NextAsync();
        Assert.AreEqual(7UL, update.Snapshot.RenderConfigurationVersion);
        Assert.AreEqual(Scan2DInterpolationMode.Lanczos, update.Snapshot.Resampling.Mode);
        Assert.AreEqual(confirmedPixel, confirmed.Pixels.Span[0]);

        barrier.Release();
        Assert.AreEqual(BScanRasterResultKind.Stale,
            (await obsolete.WaitAsync(DiagnosticTimeout)).Kind);
        using BScanRasterRegion accepted = (await harness.Delivery.GetRegionAsync(latest)).Region!;
        Assert.AreEqual(7UL, accepted.Snapshot.RenderConfigurationVersion);
        Assert.AreEqual(Scan2DInterpolationMode.Lanczos, accepted.Snapshot.Resampling.Mode);
        Assert.IsTrue(accepted.Snapshot.RequiresFullRefresh);
        Assert.IsTrue(accepted.Snapshot.FullRefreshReason.HasFlag(BScanFullRefreshReason.Recovery));
        Assert.IsGreaterThanOrEqualTo(1UL, harness.Delivery.Diagnostics.VisualBuildsSuperseded);
        accepted.Dispose();
        using BScanRasterRegion compatible = (await harness.Delivery.GetRegionAsync(latest)).Region!;
        Assert.IsFalse(compatible.Snapshot.FullRefreshReason.HasFlag(BScanFullRefreshReason.Recovery));
    }

    [TestMethod]
    [DataRow(Scan2DInterpolationMode.Bicubic)]
    [DataRow(Scan2DInterpolationMode.None)]
    public async Task ModeChangeMakesActiveLanczosStaleAndPublishesOnlyTheNewMode(
        Scan2DInterpolationMode newMode)
    {
        await using var harness = await Harness.CreateAsync();
        BScanRasterRegion confirmed = (await harness.Delivery.GetRegionAsync(harness.None)).Region!;
        confirmed.Dispose();
        using var barrier = new KernelRowBarrier();
        harness.Delivery.AfterLanczosKernelRowProcessed = barrier.Callback;
        Task<BScanRasterResult> obsolete = Task.Run(async () =>
            await harness.Delivery.GetRegionAsync(harness.Lanczos));
        barrier.WaitUntilEntered();

        BScanRenderRequest latest = harness.Lanczos with
        {
            RenderConfigurationVersion = 11,
            Resampling = new(newMode)
        };
        var observer = new ChannelObserver();
        using IDisposable subscription = harness.Delivery.Subscribe(observer);
        BScanVisualUpdate replay = await observer.NextAsync();
        Assert.AreEqual(Scan2DInterpolationMode.None, replay.Snapshot.Resampling.Mode);
        harness.Delivery.RequestRender(latest);
        Task<BScanRasterResult> latestRaster = Task.Run(async () =>
            await harness.Delivery.GetRegionAsync(latest));
        BScanVisualUpdate update = await observer.NextAsync();
        Assert.AreEqual(newMode, update.Snapshot.Resampling.Mode);
        Assert.AreEqual(11UL, update.Snapshot.RenderConfigurationVersion);
        using BScanRasterRegion accepted = (await latestRaster.WaitAsync(DiagnosticTimeout)).Region!;
        Assert.AreEqual(newMode, accepted.Snapshot.Resampling.Mode);
        Assert.AreEqual(11UL, accepted.Snapshot.RenderConfigurationVersion);

        barrier.Release();
        Assert.AreEqual(BScanRasterResultKind.Stale,
            (await obsolete.WaitAsync(DiagnosticTimeout)).Kind);
    }

    private static void AssertDeliveryDrained(BScanVisualDelivery delivery)
    {
        BScanVisualDiagnostics diagnostics = delivery.Diagnostics;
        Assert.AreEqual(0, diagnostics.ActiveBuilds);
        Assert.AreEqual(0, diagnostics.ActiveRasterOwners);
        Assert.AreEqual(0L, diagnostics.VisualBytesRented);
    }

    private static void AssertIngressDrained(BScanIngress ingress)
    {
        BScanDiagnostics diagnostics = ingress.Diagnostics;
        Assert.AreEqual(0, diagnostics.ActiveReadLeases);
        Assert.AreEqual(diagnostics.BlocksRented, diagnostics.BlocksReturned);
    }

    private sealed class KernelRowBarrier : IDisposable
    {
        private readonly ManualResetEventSlim _entered = new();
        private readonly ManualResetEventSlim _release = new();
        private int _armed = 1;
        private int _rowsObserved;
        internal int RowsObserved => Volatile.Read(ref _rowsObserved);
        internal Action<int> Callback => OnRow;
        internal void WaitUntilEntered() => Assert.IsTrue(_entered.Wait(DiagnosticTimeout));
        internal void Release() => _release.Set();
        private void OnRow(int row)
        {
            Interlocked.Increment(ref _rowsObserved);
            if (Interlocked.Exchange(ref _armed, 0) == 0) return;
            Assert.AreEqual(0, row);
            _entered.Set();
            if (!_release.Wait(DiagnosticTimeout)) throw new TimeoutException("Lanczos row barrier was not released.");
        }
        public void Dispose() { _release.Set(); _entered.Dispose(); _release.Dispose(); }
    }

    private sealed class CountingObserver : IObserver<BScanVisualUpdate>
    {
        private int _count;
        internal int Count => Volatile.Read(ref _count);
        public void OnNext(BScanVisualUpdate value) => Interlocked.Increment(ref _count);
        public void OnError(Exception error) { }
        public void OnCompleted() { }
    }

    private sealed class ChannelObserver : IObserver<BScanVisualUpdate>
    {
        private readonly Channel<BScanVisualUpdate> _updates = Channel.CreateUnbounded<BScanVisualUpdate>();
        public void OnNext(BScanVisualUpdate value) => _updates.Writer.TryWrite(value);
        public void OnError(Exception error) => _updates.Writer.TryComplete(error);
        public void OnCompleted() => _updates.Writer.TryComplete();
        internal Task<BScanVisualUpdate> NextAsync() =>
            _updates.Reader.ReadAsync().AsTask().WaitAsync(DiagnosticTimeout);
    }

    private sealed class Harness : IAsyncDisposable
    {
        private bool _ingressDisposed;
        private Harness(BScanIngress ingress, BScanVisualDelivery delivery,
            ConventionalUtFrameMetadata metadata)
        {
            Ingress = ingress;
            Delivery = delivery;
            None = Request(metadata, Scan2DInterpolationMode.None);
            Lanczos = Request(metadata, Scan2DInterpolationMode.Lanczos);
        }

        internal BScanIngress Ingress { get; }
        internal BScanVisualDelivery Delivery { get; }
        internal BScanRenderRequest None { get; }
        internal BScanRenderRequest Lanczos { get; }

        internal static async Task<Harness> CreateAsync()
        {
            const int columns = 128, samples = 2048;
            var ingress = new BScanIngress(new(8L * 1024 * 1024, 16L * 1024 * 1024,
                columns, columns, 64));
            var metadata = new ConventionalUtFrameMetadata(new("bscan-lanczos-response"),
                new(Guid.Parse("55000000-0000-0000-0000-000000000055")),
                new(new(1), samples, 1_000_000, 0, UtSignalMode.Rf), DateTimeOffset.UnixEpoch);
            var indexed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            int remaining = columns;
            ingress.AfterIndexCommit = _ =>
            { if (Interlocked.Decrement(ref remaining) == 0) indexed.TrySetResult(); };
            ingress.OpenRun(metadata);
            short[] samplesData = Enumerable.Range(0, samples).Select(x => checked((short)(x % 1024 - 512))).ToArray();
            for (int column = 0; column < columns; column++)
                ingress.Accept(metadata, (ulong)column, (ulong)column, TimeSpan.FromTicks(column), null, samplesData);
            await indexed.Task.WaitAsync(DiagnosticTimeout);
            return new(ingress, new BScanVisualDelivery(ingress), metadata);
        }

        internal async Task DisposeIngressAsync()
        {
            if (_ingressDisposed) return;
            _ingressDisposed = true;
            await Ingress.DisposeAsync();
        }

        public async ValueTask DisposeAsync()
        {
            Delivery.AfterLanczosKernelRowProcessed = null;
            await Delivery.DisposeAsync();
            await DisposeIngressAsync();
            AssertDeliveryDrained(Delivery);
            AssertIngressDrained(Ingress);
        }

        private static BScanRenderRequest Request(ConventionalUtFrameMetadata metadata,
            Scan2DInterpolationMode mode) => new(metadata.RunId,
            BScanSpatialViewportOperations.Reset(new(0, 127)),
            ScanTimeViewportOperations.Create(0, .002048), 1024, 1024, 1,
            Resampling: new(mode));
    }
}
