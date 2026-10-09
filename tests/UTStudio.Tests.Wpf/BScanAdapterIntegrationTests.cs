using System.Buffers;
using System.ComponentModel;
using System.Windows;
using System.Windows.Threading;
using UTStudio.App.Wpf.Adapters;
using UTStudio.App.Wpf.Services;
using UTStudio.Contracts.Inspection;
using UTStudio.Domain.Inspection;
using UTStudio.Domain.Acquisition;
using UTStudio.Presentation;
using UTStudio.Visualization.Core;
using UTStudio.Visualization.Wpf.Controls;

namespace UTStudio.Tests.Wpf;

[TestClass]
public sealed class BScanAdapterIntegrationTests
{
    [TestMethod]
    public Task Delivery_viewmodel_adapter_applies_full_and_unsubscribes_on_close() => StaTest.Run(async () =>
    {
        var source = new Source();
        var dispatcher = new WpfUiDispatcher(Dispatcher.CurrentDispatcher);
        await using var viewport = new SharedScanTimeViewport(dispatcher);
        await using var selection = new BScanSelectionCoordinator(dispatcher, new NoTraceProvider(), new AScanProjector());
        await using var viewModel = new BScanViewModel(source, viewport, selection, dispatcher);
        var control = new BScanControl();
        using var window = new Host(control);
        using var adapter = new BScanControlAdapter(viewModel, control);
        var appliedSignal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        viewModel.PropertyChanged += (_, args) =>
        { if (args.PropertyName == nameof(BScanViewModel.State) && viewModel.State.Region is not null) appliedSignal.TrySetResult(); };
        BScanVisualSnapshot snapshot = Snapshot(source.ConsumerId!);
        source.RegionFactory = _ => Region(snapshot, 1);
        source.Emit(snapshot);
        await appliedSignal.Task;
        Assert.IsNotNull(control.RasterSource);
        Assert.AreEqual(BScanRasterFormat.BipolarIntensity8, viewModel.State.Region!.Format);
        var applied = control.RasterSource;
        adapter.Dispose();
        source.RegionFactory = _ => Region(snapshot with
        {
            SnapshotVersion = 2,
            BaseStoreRevision = 1,
            StoreRevision = 2,
            RequiresFullRefresh = false,
            FullRefreshReason = BScanFullRefreshReason.None
        }, 255);
        source.Emit(snapshot with
        {
            SnapshotVersion = 2,
            BaseStoreRevision = 1,
            StoreRevision = 2,
            RequiresFullRefresh = false,
            FullRefreshReason = BScanFullRefreshReason.None
        });
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Assert.AreSame(applied, control.RasterSource);
    });

    [TestMethod]
    public Task Adapter_commits_accumulated_1_8_33_100_column_regions_without_replacing_bitmap() => StaTest.Run(async () =>
    {
        var source = new Source();
        var dispatcher = new WpfUiDispatcher(Dispatcher.CurrentDispatcher);
        await using var viewport = new SharedScanTimeViewport(dispatcher);
        await using var selection = new BScanSelectionCoordinator(dispatcher, new NoTraceProvider(), new AScanProjector());
        await using var viewModel = new BScanViewModel(source, viewport, selection, dispatcher);
        var control = new BScanControl();
        using var window = new Host(control);
        using var adapter = new BScanControlAdapter(viewModel, control);
        BScanVisualSnapshot current = LargeSnapshot(source.ConsumerId!, 1, 0, true, 128);
        source.RegionFactory = _ => Region(current, 1);
        await EmitAndWaitAsync(source, viewModel, current);
        var bitmap = control.RasterSource;

        foreach (int columns in new[] { 1, 8, 33, 100 })
        {
            ulong basis = current.StoreRevision;
            current = LargeSnapshot(source.ConsumerId!, basis + 1, basis, false, columns);
            source.RegionFactory = _ => Region(current, (byte)(columns + 1));
            await EmitAndWaitAsync(source, viewModel, current);
            Assert.AreSame(bitmap, control.RasterSource);
            Assert.AreEqual(current.StoreRevision, control.AppliedSnapshot!.StoreRevision);
            Assert.AreEqual(0, control.ActivePooledRasterBytes);
        }
    });

    [TestMethod]
    public Task Requested_column_is_visible_while_exact_trace_resolution_is_pending() => StaTest.Run(async () =>
    {
        var source = new Source();
        var dispatcher = new WpfUiDispatcher(Dispatcher.CurrentDispatcher);
        var provider = new PendingTraceProvider();
        await using var viewport = new SharedScanTimeViewport(dispatcher);
        await using var selection = new BScanSelectionCoordinator(dispatcher, provider, new AScanProjector());
        await using var viewModel = new BScanViewModel(source, viewport, selection, dispatcher);
        var control = new BScanControl();
        using var window = new Host(control);
        using var adapter = new BScanControlAdapter(viewModel, control);
        BScanVisualSnapshot snapshot = Snapshot(source.ConsumerId!);
        source.RegionFactory = _ => Region(snapshot, 1);
        await EmitAndWaitAsync(source, viewModel, snapshot);

        Task request = adapter.SelectColumnAsync(2);
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);

        Assert.AreEqual(2UL, control.SelectedAcquisitionIndex);
        Assert.IsFalse(request.IsCompleted);
        provider.Complete(BScanTraceResult.NotAvailable());
        await request;
        Assert.IsNull(control.SelectedAcquisitionIndex);
    });

    private static async Task EmitAndWaitAsync(Source source, BScanViewModel viewModel, BScanVisualSnapshot snapshot)
    {
        var signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        PropertyChangedEventHandler handler = (_, args) =>
        { if (args.PropertyName == nameof(BScanViewModel.State) && viewModel.State.DisplayedRevision?.StoreRevision == snapshot.StoreRevision) signal.TrySetResult(); };
        viewModel.PropertyChanged += handler;
        try { source.Emit(snapshot); await signal.Task; }
        finally { viewModel.PropertyChanged -= handler; }
    }

    private static BScanRasterRegion Region(BScanVisualSnapshot snapshot, byte value)
    {
        BScanRasterRectangle affected = snapshot.AffectedRegion;
        int cells = checked(affected.Width * affected.Height);
        byte[] storage = new byte[checked(cells * 3)];
        storage.AsSpan(0, cells).Fill(value);
        storage.AsSpan(cells, cells).Fill(1);
        storage.AsSpan(checked(cells * 2), cells).Fill(1);
        var owner = new Owner(storage);
        return new(snapshot, affected.X, affected.Y, affected.Width, affected.Height, owner, () => { });
    }
    private static BScanVisualSnapshot Snapshot(string consumer)
    {
        var run = new AcquisitionRunId(Guid.Parse("33333333-3333-3333-3333-333333333333"));
        var range = new BScanAcquisitionRange(0, 3); var time = ScanTimeViewportOperations.Create(0, 1);
        return new(run, 1, 0, 1, 0, range, range, time, BScanSpatialViewportOperations.Reset(range), time,
            4, 4, 0, new(0, 3, 0, 1), new(0, 0, 4, 4), false, true, BScanFullRefreshReason.Initial)
        { ConsumerId = consumer, Stream = new(new UtSourceId("adapter"), run, new PhysicalChannelId(0)) };
    }
    private static BScanVisualSnapshot LargeSnapshot(string consumer, ulong revision, ulong basis, bool full, int columns)
    {
        var run = new AcquisitionRunId(Guid.Parse("33333333-3333-3333-3333-333333333333"));
        var range = new BScanAcquisitionRange(0, 127); var time = ScanTimeViewportOperations.Create(0, 1);
        return new(run, revision, basis, revision, 0, range, range, time, BScanSpatialViewportOperations.Reset(range), time,
            128, 16, 0, new(0, 127, basis, revision), new(0, 0, columns, 16), false, full,
            full ? BScanFullRefreshReason.Initial : BScanFullRefreshReason.None)
        { ConsumerId = consumer, Stream = new(new UtSourceId("adapter"), run, new PhysicalChannelId(0)) };
    }
    private sealed class Owner(byte[] data) : IMemoryOwner<byte>
    { public Memory<byte> Memory { get; private set; } = data; public void Dispose() => Memory = Memory<byte>.Empty; }
    private sealed class NoTraceProvider : IAuthoritativeAScanSelector, IBScanTraceIdentityResolver
    {
        public bool TryResolveIdentity(AcquisitionRunId runId, ulong acquisitionIndex, out BScanTraceIdentity identity)
        { identity = new(new("adapter"), runId, new(0), acquisitionIndex + 100, acquisitionIndex); return true; }
        public ValueTask<AScanSelectionResult> ResolveAsync(TraceIdentity identity, ulong visualVersion,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new AScanSelectionResult(BScanTraceResultKind.NotAvailable));
    }
    private sealed class PendingTraceProvider : IAuthoritativeAScanSelector, IBScanTraceIdentityResolver
    {
        private readonly TaskCompletionSource<AScanSelectionResult> _completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal List<TraceIdentity> RequestedIdentities { get; } = [];
        internal int Callbacks;
        public bool TryResolveIdentity(AcquisitionRunId runId, ulong acquisitionIndex,
            out BScanTraceIdentity identity)
        {
            identity = new(new UtSourceId("adapter"), runId, new PhysicalChannelId(0),
                acquisitionIndex + 100, acquisitionIndex);
            return true;
        }
        public ValueTask<AScanSelectionResult> ResolveAsync(TraceIdentity identity, ulong visualVersion,
            CancellationToken cancellationToken = default)
        {
            RequestedIdentities.Add(identity);
            Interlocked.Increment(ref Callbacks);
            return new(_completion.Task.WaitAsync(cancellationToken));
        }
        internal void Complete(BScanTraceResult result) =>
            _completion.TrySetResult(new(result.Kind, Error: result.Error));
    }
    private sealed class Source : IBScanVisualSource
    {
        private IObserver<BScanVisualUpdate>? _observer;
        public string? ConsumerId { get; private set; }
        public Func<BScanRenderRequest, BScanRasterRegion>? RegionFactory { get; set; }
        public BScanVisualDiagnostics Diagnostics => new(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
            new(0, TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero), BScanVisualCloseState.Open, 0, null);
        public bool RegisterConsumer(string consumerId) { ConsumerId = consumerId; return true; }
        public void UnregisterConsumer(string consumerId) { if (ConsumerId == consumerId) ConsumerId = null; }
        public IDisposable Subscribe(IObserver<BScanVisualUpdate> observer) { _observer = observer; return new Subscription(() => _observer = null); }
        public void RequestRender(BScanRenderRequest request) { }
        public ValueTask<BScanRasterResult> GetRegionAsync(BScanRenderRequest request, CancellationToken cancellationToken = default) =>
            new(BScanRasterResult.Available(RegionFactory!(request)));
        public void Emit(BScanVisualSnapshot snapshot) => _observer!.OnNext(new(snapshot));
        private sealed class Subscription(Action action) : IDisposable { public void Dispose() => action(); }
    }
    private sealed class Host : IDisposable
    { private readonly Window _window; public Host(BScanControl control) { _window = new() { Content = control, Width = 400, Height = 300, ShowInTaskbar = false }; _window.Show(); _window.UpdateLayout(); } public void Dispose() => _window.Close(); }
}
