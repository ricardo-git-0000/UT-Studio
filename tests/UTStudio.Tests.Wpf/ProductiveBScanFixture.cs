using System.Collections.Concurrent;
using System.Buffers;
using System.ComponentModel;
using System.Windows;
using System.Windows.Threading;
using UTStudio.ScanVisualization.BScan.Wpf;
using UTStudio.App.Wpf.Services;
using UTStudio.Contracts.Inspection;
using UTStudio.Domain.Acquisition;
using UTStudio.Domain.Inspection;
using UTStudio.Inspection.Core;
using UTStudio.Presentation;
using UTStudio.Visualization.Core;
using UTStudio.Visualization.Core.Scan2D;
using UTStudio.Visualization.Wpf.Controls;

namespace UTStudio.Tests.Wpf;

internal sealed class ProductiveBScanFixture : IAsyncDisposable
{
    private readonly TrackingBytePool _rasters = new();
    private readonly Window _host;
    private readonly InspectionDataStoreRegistry _inspectionStores;
    private IInspectionRunWriter _inspectionWriter;
    private ulong _sequence;
    private TaskCompletionSource? _bootstrapRequested;
    private AcquisitionRunId? _expectedBootstrapRun;
    private TaskCompletionSource? _metadataCandidateReady;
    private ManualResetEventSlim? _releaseMetadataCandidate;
    private bool _compositionDetached;

    private ProductiveBScanFixture(int preloadedColumns, bool useSystemDeliveryClock,
        int initialRasterWidth, IAuthoritativeAScanSelector? selectionAuthority)
    {
        Clock = new();
        var options = new BScanOptions(16 * 1024 * 1024, 512, 8, 2);
        Metadata = new(new("productive-bscan"), new(Guid.Parse("99000000-0000-0000-0000-000000000099")),
            new(new(3), 16, 1_000_000, 0, UtSignalMode.Rf), DateTimeOffset.UnixEpoch);
        _inspectionStores = new(new InspectionId(Guid.Parse("98000000-0000-0000-0000-000000000098")),
            new InMemoryInspectionStoreOptions(4 * 1024 * 1024, 4096));
        _inspectionWriter = _inspectionStores.OpenRun(Metadata.RunId);
        Ingress = new(_inspectionStores.Store, _inspectionStores.Store, options, Clock);
        Ingress.OpenRun(Metadata.RunId);
        for (int column = 0; column < preloadedColumns; column++)
        {
            ulong sequence = _sequence++;
            short[] samples = Enumerable.Range(0, Metadata.Configuration.SampleCount)
                .Select(value => (short)(value + column)).ToArray();
            InspectionAppendResult commit = Commit(sequence, (ulong)column, samples);
            Ingress.NotifyCommitted(new TraceIdentity(new(_inspectionStores.InspectionId, Metadata.RunId,
                Metadata.SourceId, Metadata.Configuration.PhysicalChannelId), (ulong)column), commit.Revision);
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
        Selection = new(Dispatcher, selectionAuthority ??
            new UtTraceAScanSelector(_inspectionStores.Store, new AScanProjector()));
        ViewModel = new(Delivery, SharedViewport, Selection, Dispatcher);
        Control = new();
        Adapter = new(ViewModel, Control);
        Adapter.Activate(static () => { });
        _host = new() { Content = Control, Width = 420, Height = 300, ShowInTaskbar = false };
        _host.Show();
        _host.UpdateLayout();
        // Productive ring tests require one neutral raster column per configured live trace.
        // Do not depend on a deferred WPF measure pass to establish that initial contract.
        ViewModel.SetRasterSize(initialRasterWidth, 16, 1);
    }

    internal ManualClock Clock { get; }
    internal UTStudio.App.Wpf.Services.WpfUiDispatcher Dispatcher { get; }
    internal BScanIngress Ingress { get; }
    internal BScanVisualDelivery Delivery { get; }
    internal ConventionalUtFrameMetadata Metadata { get; private set; }
    internal SharedScanTimeViewport SharedViewport { get; }
    internal TraceSelectionScope Selection { get; }
    internal BScanViewModel ViewModel { get; }
    internal BScanControl Control { get; }
    internal BScanControlAdapter Adapter { get; }
    internal IUtTraceProvider TraceProvider => _inspectionStores.Store;
    internal Action<TraceIdentity, InspectionRevision>? AfterCommitted { get; set; }
    internal int BlocksOutstanding => 0;
    internal int TraceOwnersOutstanding => 0;
    internal int RasterOwnersOutstanding => _rasters.Outstanding;

    internal async Task DetachLegacyCompositionAsync()
    {
        if (_compositionDetached) { return; }
        Adapter.Dispose();
        await ViewModel.DisposeAsync();
        _host.Close();
        _compositionDetached = true;
        Assert.AreEqual(0, Delivery.ActiveConsumerCount);
        Assert.AreEqual(0, RasterOwnersOutstanding);
    }

    internal static Task<ProductiveBScanFixture> CreateAsync(int preloadedColumns = 0,
        bool useSystemDeliveryClock = false, int initialRasterWidth = 128,
        IAuthoritativeAScanSelector? selectionAuthority = null) =>
        Task.FromResult(new ProductiveBScanFixture(preloadedColumns, useSystemDeliveryClock,
            initialRasterWidth, selectionAuthority));

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
        short[] samples = Enumerable.Range(0, Metadata.Configuration.SampleCount)
            .Select(value => (short)((ulong)value + acquisitionIndex)).ToArray();
        InspectionAppendResult commit = Commit(sequence, acquisitionIndex, samples);
        var identity = new TraceIdentity(new(_inspectionStores.InspectionId, Metadata.RunId,
            Metadata.SourceId, Metadata.Configuration.PhysicalChannelId), acquisitionIndex);
        Ingress.NotifyCommitted(identity, commit.Revision);
        AfterCommitted?.Invoke(identity, commit.Revision);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (Ingress.CurrentVisualState?.StoreState.StoreRevision != commit.Revision.Value)
        { await Task.Delay(10, timeout.Token); }
    }

    internal async Task StartNewRunAsync(Guid runId)
    {
        AcquisitionRunId previous = Metadata.RunId;
        InspectionRunSealResult final = await _inspectionWriter.SealRunAsync();
        Ingress.RequestFinalReconciliation(previous, final.FinalRevision);
        Ingress.StopAdmission(previous);
        await Ingress.WaitForDrainAsync(previous);
        Ingress.FreezeRun(previous);
        await _inspectionWriter.DisposeAsync();
        Metadata = new(Metadata.SourceId, new(runId), Metadata.Configuration,
            Metadata.RunStartedAtUtc.AddSeconds(1));
        _inspectionWriter = _inspectionStores.OpenRun(Metadata.RunId);
        _expectedBootstrapRun = Metadata.RunId;
        _bootstrapRequested = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _metadataCandidateReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _releaseMetadataCandidate = new();
        Ingress.OpenRun(Metadata.RunId);
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
        try
        { await WaitForStateAsync(() => ViewModel.State.DisplayedRevision?.StoreRevision >= revision); }
        catch (TimeoutException error)
        {
            BScanMetadataCandidateAudit? audit = Delivery.LastMetadataCandidateAudit;
            throw new TimeoutException($"Expected revision {revision}; displayed=" +
                $"{ViewModel.State.DisplayedRevision?.StoreRevision}; metadata={ViewModel.State.StoreRevision}; " +
                $"visual={Ingress.CurrentVisualState?.StoreState.StoreRevision}; " +
                $"ingressError={Ingress.Diagnostics.WorkerError}; visualError={ViewModel.State.BScanVisualError}; " +
                $"updates={Delivery.Diagnostics.VisualUpdatesPublished}; " +
                $"superseded={Delivery.Diagnostics.VisualBuildsSuperseded}; " +
                $"audit=current:{audit?.CurrentGeneration},candidate:{audit?.CandidateGeneration}," +
                $"ingress:{audit?.IngressCurrent},store:{audit?.StoreCurrent}," +
                $"committed:{audit?.Committed},reason:{audit?.LastGenerationReason}.", error);
        }
    }

    internal async Task WaitForBootstrapDrainAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (Delivery.PendingBootstrapCount != 0)
        { await Task.Delay(10, timeout.Token); }
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

    private InspectionAppendResult Commit(ulong sequence, ulong acquisitionIndex, short[] samples)
    {
        var stream = new UtStreamIdentity(_inspectionStores.InspectionId, Metadata.RunId,
            Metadata.SourceId, Metadata.Configuration.PhysicalChannelId);
        var descriptor = new TraceDescriptor(new TraceIdentity(stream, acquisitionIndex), sequence,
            TimeSpan.FromTicks((long)sequence), Metadata.RunStartedAtUtc + TimeSpan.FromTicks((long)sequence),
            null, samples.Length, Metadata.Configuration.SampleRateHz,
            Metadata.Configuration.FirstSampleOffsetSeconds, Metadata.Configuration.SignalMode);
        InspectionAppendResult result = _inspectionWriter.CommitAsync(
            new InspectionCommitInput(new InspectionTraceInput(descriptor, samples))).AsTask().GetAwaiter().GetResult();
        Assert.IsTrue(result.IsCommitted || result.Status == InspectionAppendStatus.Duplicate);
        return result;
    }

    public async ValueTask DisposeAsync()
    {
        Delivery.AfterBootstrapRequestQueued = null;
        Delivery.BeforeMetadataCandidateCommit = null;
        Delivery.BeforeRasterCandidateCommit = null;
        Ingress.BeforeReconcileWorkerExit = null;
        Ingress.BeforeVisualStateCommit = null;
        _releaseMetadataCandidate?.Set();
        var failures = new List<Exception>();
        if (!_compositionDetached)
        {
            Capture("adapter", Adapter.Dispose);
            await CaptureAsync("view model", () => ViewModel.DisposeAsync().AsTask());
        }
        Capture("delivery registration balances", () =>
        {
            Assert.AreEqual(0, Delivery.ActiveConsumerCount);
            Assert.AreEqual(0, Delivery.PendingBootstrapCount);
            Assert.AreEqual(0, Delivery.RetainedSnapshotCount);
        });
        await CaptureAsync("selection", () => Selection.DisposeAsync().AsTask());
        await CaptureAsync("shared viewport", () => SharedViewport.DisposeAsync().AsTask());
        await CaptureAsync("delivery", () => Delivery.DisposeAsync().AsTask());
        await CaptureAsync("ingress", () => Ingress.DisposeAsync().AsTask());
        await CaptureAsync("writer seal", () => _inspectionWriter.SealRunAsync().AsTask());
        await CaptureAsync("writer", () => _inspectionWriter.DisposeAsync().AsTask());
        await CaptureAsync("inspection stores", () => _inspectionStores.DisposeAsync().AsTask());
        if (!_compositionDetached) { Capture("host", _host.Close); }
        Capture("resource balances", () =>
        {
            Assert.AreEqual(0, TraceOwnersOutstanding);
            Assert.AreEqual(0, RasterOwnersOutstanding);
            Assert.AreEqual(0, BlocksOutstanding);
            Assert.AreEqual(0, Control.ActivePooledRasterBytes);
            Assert.AreEqual(0, Control.ActiveCandidateBitmaps);
            Assert.AreEqual(0, Clock.RegisteredTimers);
            Assert.AreEqual(Delivery.Diagnostics.ObserverCallbacksStarted,
                Delivery.Diagnostics.ObserverCallbacksCompleted);
        });
        _releaseMetadataCandidate?.Dispose();
        if (failures.Count != 0)
        { throw new AggregateException("Productive B-Scan fixture cleanup failed.", failures); }

        async Task CaptureAsync(string stage, Func<Task> dispose)
        {
            try { await dispose().WaitAsync(TimeSpan.FromSeconds(10)); }
            catch (Exception error) { Record(stage, error); }
        }

        void Capture(string stage, Action dispose)
        {
            try { dispose(); }
            catch (Exception error) { Record(stage, error); }
        }

        void Record(string stage, Exception error) => failures.Add(new InvalidOperationException(
            $"{stage} cleanup failed; ingress={Ingress.Diagnostics}; delivery={Delivery.Diagnostics}", error));
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
