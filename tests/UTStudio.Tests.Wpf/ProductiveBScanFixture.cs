using System.Buffers;
using System.ComponentModel;
using System.Windows;
using System.Windows.Threading;
using UTStudio.App.Wpf.Adapters;
using UTStudio.App.Wpf.Services;
using UTStudio.Domain.Acquisition;
using UTStudio.Presentation;
using UTStudio.Visualization.Core;
using UTStudio.Visualization.Wpf.Controls;

namespace UTStudio.Tests.Wpf;

internal sealed class ProductiveBScanFixture : IAsyncDisposable
{
    private readonly TrackingShortPool _blocks = new();
    private readonly TrackingTracePool _traces = new();
    private readonly TrackingBytePool _rasters = new();
    private readonly Window _host;
    private ulong _sequence;

    private ProductiveBScanFixture(int preloadedColumns)
    {
        Clock = new();
        var options = new BScanOptions(4 * 1024 * 1024, 16 * 1024 * 1024, 512, 8, 2);
        Ingress = new(options, Clock, _blocks, _traces);
        Metadata = new(new("productive-bscan"), new(Guid.Parse("99000000-0000-0000-0000-000000000099")),
            new(new(3), 16, 1_000_000, 0, UtSignalMode.Rf), DateTimeOffset.UnixEpoch);
        Ingress.OpenRun(Metadata);
        for (int column = 0; column < preloadedColumns; column++)
        {
            using var indexed = new ManualResetEventSlim();
            ulong sequence = _sequence++;
            Ingress.AfterIndexCommit = identity => { if (identity.Sequence == sequence) { indexed.Set(); } };
            Ingress.Accept(Metadata, sequence, (ulong)column, TimeSpan.FromTicks((long)sequence), null,
                Enumerable.Range(0, Metadata.Configuration.SampleCount).Select(value => (short)(value + column)).ToArray());
            if (!indexed.Wait(TimeSpan.FromSeconds(10))) { throw new InvalidOperationException("B-Scan preload did not index."); }
        }
        Delivery = new(Ingress, Clock, _rasters);
        Dispatcher = new(System.Windows.Threading.Dispatcher.CurrentDispatcher);
        SharedViewport = new(Dispatcher);
        Selection = new(Dispatcher, Ingress, new AScanProjector());
        ViewModel = new(Delivery, SharedViewport, Selection, Dispatcher);
        Control = new();
        Adapter = new(ViewModel, Control);
        _host = new() { Content = Control, Width = 420, Height = 300, ShowInTaskbar = false };
        _host.Show();
        _host.UpdateLayout();
        ViewModel.SetRasterSize(128, 16, 1);
    }

    internal ManualClock Clock { get; }
    internal WpfUiDispatcher Dispatcher { get; }
    internal BScanIngress Ingress { get; }
    internal BScanVisualDelivery Delivery { get; }
    internal ConventionalUtFrameMetadata Metadata { get; private set; }
    internal SharedScanTimeViewport SharedViewport { get; }
    internal BScanSelectionCoordinator Selection { get; }
    internal BScanViewModel ViewModel { get; }
    internal BScanControl Control { get; }
    internal BScanControlAdapter Adapter { get; }
    internal int BlocksOutstanding => _blocks.Outstanding;
    internal int TraceOwnersOutstanding => _traces.Outstanding;
    internal int RasterOwnersOutstanding => _rasters.Outstanding;

    internal static Task<ProductiveBScanFixture> CreateAsync(int preloadedColumns = 0) =>
        Task.FromResult(new ProductiveBScanFixture(preloadedColumns));

    internal async Task SeedAsync(ulong acquisitionIndex = 0)
    {
        await AddIndexedAsync(acquisitionIndex);
        Delivery.RequestRender(Request());
        await WaitForRevisionAsync(1);
    }

    internal async Task AddAndPublishAsync(ulong acquisitionIndex)
    {
        ulong before = ViewModel.State.StoreRevision.GetValueOrDefault();
        await AddIndexedAsync(acquisitionIndex);
        await Clock.NextTimerAsync();
        Clock.Advance(BScanVisualDelivery.MinimumPublicationInterval);
        await WaitForRevisionAsync(before + 1);
    }

    internal async Task AddIndexedAsync(ulong acquisitionIndex)
    {
        ulong sequence = _sequence++;
        var indexed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Ingress.AfterIndexCommit = identity =>
        { if (identity.Sequence == sequence) { indexed.TrySetResult(); } };
        short[] samples = Enumerable.Range(0, Metadata.Configuration.SampleCount)
            .Select(value => (short)((ulong)value + acquisitionIndex)).ToArray();
        Ingress.Accept(Metadata, sequence, acquisitionIndex, TimeSpan.FromTicks((long)sequence), null, samples);
        await indexed.Task;
    }

    internal async Task StartNewRunAsync(Guid runId)
    {
        AcquisitionRunId previous = Metadata.RunId;
        Ingress.CloseRun(previous);
        await Ingress.WaitForCloseAsync(previous);
        Metadata = new(Metadata.SourceId, new(runId), Metadata.Configuration,
            Metadata.RunStartedAtUtc.AddSeconds(1));
        Ingress.OpenRun(Metadata);
    }

    internal BScanRenderRequest Request() => new(Metadata.RunId,
        BScanSpatialViewportOperations.Reset(new(0, 127)), ScanTimeViewportOperations.Create(0, .000016),
        128, 16, 1, ConsumerId: ViewModel.ConsumerId);

    internal async Task WaitForRevisionAsync(ulong revision)
    {
        if (ViewModel.State.DisplayedRevision?.StoreRevision >= revision) { return; }
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        PropertyChangedEventHandler handler = (_, args) =>
        {
            if (args.PropertyName == nameof(BScanViewModel.State) &&
                ViewModel.State.DisplayedRevision?.StoreRevision >= revision) { completion.TrySetResult(); }
        };
        ViewModel.PropertyChanged += handler;
        try { await completion.Task.WaitAsync(TimeSpan.FromSeconds(10)); }
        finally { ViewModel.PropertyChanged -= handler; }
    }

    internal async Task WaitForRunAsync(AcquisitionRunId runId, ulong revision)
    {
        if (ViewModel.State is
            {
                RunId: var currentRun,
                StoreRevision: var currentRevision,
                Region.Snapshot.RunId: var regionRun
            } && currentRun == runId && currentRevision >= revision && regionRun == runId) { return; }
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        PropertyChangedEventHandler handler = (_, args) =>
        {
            if (args.PropertyName == nameof(BScanViewModel.State) && ViewModel.State is
                {
                    RunId: var currentRun,
                    StoreRevision: var currentRevision,
                    Region.Snapshot.RunId: var regionRun
                } && currentRun == runId &&
                currentRevision >= revision && regionRun == runId) { completion.TrySetResult(); }
        };
        ViewModel.PropertyChanged += handler;
        try { await completion.Task.WaitAsync(TimeSpan.FromSeconds(10)); }
        finally { ViewModel.PropertyChanged -= handler; }
    }

    internal async Task WaitForTokenChangeAsync(BScanPresentedRegionToken token)
    {
        if (ViewModel.State.Region is { Token: var current } && current != token) { return; }
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        PropertyChangedEventHandler handler = (_, args) =>
        {
            if (args.PropertyName == nameof(BScanViewModel.State) &&
                ViewModel.State.Region is { Token: var current } && current != token) { completion.TrySetResult(); }
        };
        ViewModel.PropertyChanged += handler;
        try { await completion.Task.WaitAsync(TimeSpan.FromSeconds(10)); }
        finally { ViewModel.PropertyChanged -= handler; }
    }

    internal void AssertOpenBalances()
    {
        Assert.AreEqual(0, Control.ActivePooledRasterBytes);
        Assert.IsLessThanOrEqualTo(2, Delivery.Diagnostics.ActiveRasterOwners);
        Assert.AreEqual(Delivery.Diagnostics.ObserverCallbacksStarted, Delivery.Diagnostics.ObserverCallbacksCompleted);
    }

    public async ValueTask DisposeAsync()
    {
        Adapter.Dispose();
        await ViewModel.DisposeAsync();
        Assert.AreEqual(0, Delivery.ActiveConsumerCount);
        Assert.AreEqual(0, Delivery.PendingBootstrapCount);
        Assert.AreEqual(0, Delivery.RetainedSnapshotCount);
        await Selection.DisposeAsync();
        await SharedViewport.DisposeAsync();
        await Delivery.DisposeAsync();
        await Ingress.DisposeAsync();
        _host.Close();
        Assert.AreEqual(0, TraceOwnersOutstanding);
        Assert.AreEqual(0, RasterOwnersOutstanding);
        Assert.AreEqual(0, BlocksOutstanding);
        Assert.AreEqual(0, Control.ActivePooledRasterBytes);
        Assert.AreEqual(0, Control.ActiveCandidateBitmaps);
        Assert.AreEqual(0, Clock.RegisteredTimers);
        Assert.AreEqual(Delivery.Diagnostics.ObserverCallbacksStarted, Delivery.Diagnostics.ObserverCallbacksCompleted);
    }

    private sealed class TrackingShortPool : IShortArrayPool
    {
        private int _outstanding;
        internal int Outstanding => Volatile.Read(ref _outstanding);
        public short[] Rent(int minimumLength) { Interlocked.Increment(ref _outstanding); return new short[minimumLength]; }
        public void Return(short[] array) => Interlocked.Decrement(ref _outstanding);
    }

    private sealed class TrackingTracePool : ITraceCopyOwnerPool
    {
        private int _outstanding;
        internal int Outstanding => Volatile.Read(ref _outstanding);
        public IMemoryOwner<short> Rent(int minimumLength)
        { Interlocked.Increment(ref _outstanding); return new Owner<short>(new short[minimumLength], () => Interlocked.Decrement(ref _outstanding)); }
    }

    private sealed class TrackingBytePool : IByteOwnerPool
    {
        private int _outstanding;
        internal int Outstanding => Volatile.Read(ref _outstanding);
        public IMemoryOwner<byte> Rent(int minimumLength)
        { Interlocked.Increment(ref _outstanding); return new Owner<byte>(new byte[minimumLength], () => Interlocked.Decrement(ref _outstanding)); }
    }

    private sealed class Owner<T>(T[] buffer, Action release) : IMemoryOwner<T>
    {
        private T[]? _buffer = buffer;
        public Memory<T> Memory => _buffer ?? throw new ObjectDisposedException(nameof(Owner<T>));
        public void Dispose() { if (Interlocked.Exchange(ref _buffer, null) is not null) { release(); } }
    }
}
