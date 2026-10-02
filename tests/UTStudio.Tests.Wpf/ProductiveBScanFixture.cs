using System.Collections.Concurrent;
using System.Buffers;
using System.ComponentModel;
using System.Windows;
using System.Windows.Threading;
using UTStudio.App.Wpf.Adapters;
using UTStudio.App.Wpf.Services;
using UTStudio.Domain.Acquisition;
using UTStudio.Presentation;
using UTStudio.Visualization.Core;
using UTStudio.Visualization.Core.Scan2D;
using UTStudio.Visualization.Wpf.Controls;

namespace UTStudio.Tests.Wpf;

internal sealed class ProductiveBScanFixture : IAsyncDisposable
{
    private readonly TrackingShortPool _blocks = new();
    private readonly TrackingTracePool _traces = new();
    private readonly TrackingBytePool _rasters = new();
    private readonly Window _host;
    private ulong _sequence;
    private TaskCompletionSource? _bootstrapRequested;
    private AcquisitionRunId? _expectedBootstrapRun;
    private TaskCompletionSource? _metadataCandidateReady;
    private ManualResetEventSlim? _releaseMetadataCandidate;

    private ProductiveBScanFixture(int preloadedColumns, bool useSystemDeliveryClock)
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
        Delivery = new(Ingress, useSystemDeliveryClock ? TimeProvider.System : Clock, _rasters);
        Delivery.AfterBootstrapRequestQueued = (_, runId) =>
        {
            if (runId == _expectedBootstrapRun) { _bootstrapRequested?.TrySetResult(); }
        };
        Delivery.BeforeMetadataCandidateCommit = snapshot =>
        {
            if (snapshot.RunId != _expectedBootstrapRun) { return; }
            _metadataCandidateReady?.TrySetResult();
            if (_releaseMetadataCandidate?.Wait(TimeSpan.FromSeconds(10)) == false)
            { throw new TimeoutException("The metadata candidate barrier was not released."); }
        };
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

    internal static Task<ProductiveBScanFixture> CreateAsync(int preloadedColumns = 0,
        bool useSystemDeliveryClock = false) =>
        Task.FromResult(new ProductiveBScanFixture(preloadedColumns, useSystemDeliveryClock));

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
        _expectedBootstrapRun = Metadata.RunId;
        _bootstrapRequested = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _metadataCandidateReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _releaseMetadataCandidate = new();
        Ingress.OpenRun(Metadata);
    }

    internal Task WaitForBootstrapRequestAsync() =>
        (_bootstrapRequested ?? throw new InvalidOperationException("No bootstrap is expected."))
        .Task.WaitAsync(TimeSpan.FromSeconds(10));

    internal Task WaitForMetadataCandidateAsync() =>
        (_metadataCandidateReady ?? throw new InvalidOperationException("No metadata candidate is expected."))
        .Task.WaitAsync(TimeSpan.FromSeconds(10));

    internal void ReleaseMetadataCandidate() => _releaseMetadataCandidate?.Set();

    internal async Task WaitForPublicationAsync(AcquisitionRunId runId)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using IDisposable subscription = Delivery.Subscribe(new PublicationObserver(update =>
        {
            if (update.Snapshot.ConsumerId == ViewModel.ConsumerId && update.Snapshot.RunId == runId)
            { completion.TrySetResult(); }
        }));
        await completion.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }

    internal PublicationProbe ObservePublications(AcquisitionRunId runId) =>
        new(Delivery, ViewModel.ConsumerId, runId);

    internal BScanRenderRequest Request() => new(Metadata.RunId,
        BScanSpatialViewportOperations.Reset(new(0, 127)), ScanTimeViewportOperations.Create(0, .000016),
        128, 16, 1, ConsumerId: ViewModel.ConsumerId);

    internal async Task WaitForRevisionAsync(ulong revision)
    {
        await WaitForStateAsync(() => ViewModel.State.DisplayedRevision?.StoreRevision >= revision);
    }

    internal async Task WaitForRunAsync(AcquisitionRunId runId, ulong revision)
    {
        await WaitForStateAsync(() => ViewModel.State is
        {
            RunId: var currentRun,
            StoreRevision: var currentRevision,
            Region.Snapshot.RunId: var regionRun
        } && currentRun == runId && currentRevision >= revision && regionRun == runId);
    }

    internal async Task WaitForTokenChangeAsync(BScanPresentedRegionToken token)
    {
        await WaitForStateAsync(() =>
            ViewModel.State.Region is { Token: var current } && current != token);
    }

    internal Task WaitForPresentedModeAsync(Scan2DInterpolationMode mode) =>
        WaitForStateAsync(() => ViewModel.State.PresentedResamplingMode == mode &&
            Control.AppliedSnapshot?.Resampling.Mode == mode);

    private async Task WaitForStateAsync(Func<bool> predicate)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        PropertyChangedEventHandler handler = (_, args) =>
        {
            if (args.PropertyName == nameof(BScanViewModel.State) && predicate())
            { completion.TrySetResult(); }
        };
        ViewModel.PropertyChanged += handler;
        try
        {
            if (predicate()) { completion.TrySetResult(); }
            await completion.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }
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
        Delivery.AfterBootstrapRequestQueued = null;
        Delivery.BeforeMetadataCandidateCommit = null;
        Delivery.BeforeRasterCandidateCommit = null;
        _releaseMetadataCandidate?.Set();
        _releaseMetadataCandidate?.Dispose();
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

    private sealed class PublicationObserver(Action<BScanVisualUpdate> onNext) : IObserver<BScanVisualUpdate>
    {
        public void OnNext(BScanVisualUpdate value) => onNext(value);
        public void OnError(Exception error) { }
        public void OnCompleted() { }
    }

    internal sealed class PublicationProbe : IObserver<BScanVisualUpdate>, IDisposable
    {
        private readonly string _consumerId;
        private readonly AcquisitionRunId _runId;
        private readonly IDisposable _subscription;
        private readonly TaskCompletionSource _first =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _count;
        private readonly ConcurrentQueue<Scan2DInterpolationMode> _publishedModes = new();

        internal PublicationProbe(BScanVisualDelivery delivery, string consumerId,
            AcquisitionRunId runId)
        {
            _consumerId = consumerId;
            _runId = runId;
            _subscription = delivery.Subscribe(this);
        }

        internal int Count => Volatile.Read(ref _count);
        internal Scan2DInterpolationMode[] PublishedModes => _publishedModes.ToArray();
        internal Task WaitForFirstAsync() => _first.Task.WaitAsync(TimeSpan.FromSeconds(10));
        public void OnNext(BScanVisualUpdate value)
        {
            if (value.Snapshot.ConsumerId != _consumerId || value.Snapshot.RunId != _runId) { return; }
            _publishedModes.Enqueue(value.Snapshot.Resampling.Mode);
            Interlocked.Increment(ref _count);
            _first.TrySetResult();
        }
        public void OnError(Exception error) { }
        public void OnCompleted() { }
        public void Dispose() => _subscription.Dispose();
    }
}
