using System.ComponentModel;
using System.Windows;
using System.Windows.Threading;
using UTStudio.Contracts.Application;
using UTStudio.Contracts.Inspection;
using UTStudio.Domain.Acquisition;
using UTStudio.Domain.Inspection;
using UTStudio.Inspection.Core;
using UTStudio.Presentation;
using UTStudio.ScanVisualization.BScan.Wpf;
using UTStudio.Visualization.Core;

namespace UTStudio.ScanVisualization.Wpf.Demo;

public partial class MainWindow : Window
{
    private readonly InspectionDataStoreRegistry _stores;
    private readonly IInspectionRunWriter _writer;
    private readonly AScanVisualDelivery _aVisuals;
    private readonly BScanIngress _bIngress;
    private readonly BScanVisualDelivery _bVisuals;
    private readonly SharedScanTimeViewport _viewport;
    private readonly TraceSelectionScope _selection;
    private readonly ObservableFeed<SessionSnapshot> _sessions = new();
    private readonly DispatcherTimer _timer;
    private readonly ConventionalUtFrameMetadata _metadata;
    private readonly UtStreamIdentity _stream;
    private BScanView? _bScan;
    private ulong _sequence = 10_000;
    private ulong _acquisitionIndex;
    private bool _running;
    private bool _closing;
    private bool _stopping;
    private bool _togglingB;
    private Task _activeTick = Task.CompletedTask;
    private Task _toggleBTask = Task.CompletedTask;

    public MainWindow()
    {
        InitializeComponent();
        var inspection = new InspectionId(Guid.NewGuid());
        var run = new AcquisitionRunId(Guid.NewGuid());
        _metadata = new(new("joint-scan-demo"), run,
            new(new(2), 256, 10_000_000, 0, UtSignalMode.Rf), DateTimeOffset.UtcNow);
        _stores = new(inspection, new InMemoryInspectionStoreOptions(16 * 1024 * 1024, 4096));
        _writer = _stores.OpenRun(run);
        _stream = new(inspection, run, _metadata.SourceId, _metadata.Configuration.PhysicalChannelId);
        _aVisuals = new(_stores.Store);
        _aVisuals.OpenRun(run);
        _bIngress = new(_stores.Store, _stores.Store);
        _bIngress.OpenRun(run);
        _bVisuals = new(_bIngress);
        var dispatcher = new DemoDispatcher(Dispatcher);
        _viewport = new(dispatcher);
        _selection = new(dispatcher, new UtTraceAScanSelector(_stores.Store));
        SessionSnapshot initial = Session(SessionPhase.Idle, canStart: true, canStop: false);
        AScan.Context = new(_sessions, initial, _aVisuals, _aVisuals.StatusChanges,
            () => _aVisuals.Statistics, _viewport, _selection);
        OpenBScan();
        _timer = new(DispatcherPriority.Background, Dispatcher) { Interval = TimeSpan.FromMilliseconds(80) };
        _timer.Tick += OnTick;
        Loaded += (_, _) => _sessions.Publish(initial);
        Closing += OnClosing;
        UpdateStatus();
    }

    private void OnStart(object sender, RoutedEventArgs args)
    {
        if (_closing || _stopping || _running) { return; }
        _running = true;
        _sessions.Publish(Session(SessionPhase.Running, canStart: false, canStop: true));
        _timer.Start();
        UpdateStatus();
    }

    private async void OnStop(object sender, RoutedEventArgs args)
    {
        if (!_running || _stopping) { return; }
        _running = false;
        _stopping = true;
        _timer.Stop();
        UpdateStatus();
        await _activeTick;
        _sessions.Publish(Session(SessionPhase.Idle, canStart: true, canStop: false));
        _stopping = false;
        UpdateStatus();
    }

    private async void OnToggleB(object sender, RoutedEventArgs args)
    {
        if (_closing || _togglingB) { return; }
        _toggleBTask = ToggleBAsync();
        await _toggleBTask;
    }

    private async Task ToggleBAsync()
    {
        _togglingB = true;
        ToggleBButton.IsEnabled = false;
        try
        {
            if (_bScan is null) { OpenBScan(); }
            else { await CloseBScanAsync(); }
            UpdateStatus();
        }
        finally
        {
            _togglingB = false;
            ToggleBButton.IsEnabled = !_closing;
        }
    }

    private async void OnTick(object? sender, EventArgs args)
    {
        _timer.Stop();
        _activeTick = ProduceOneAsync();
        try
        {
            await _activeTick;
        }
        finally { if (_running && !_closing) { _timer.Start(); } }
    }

    private async Task ProduceOneAsync()
    {
        ulong index = _acquisitionIndex++;
        ulong sequence = _sequence++;
        short[] samples = Enumerable.Range(0, _metadata.Configuration.SampleCount)
            .Select(sample => (short)(Math.Sin(sample * Math.PI / 24 + index / 7d) * 28_000)).ToArray();
        var identity = new TraceIdentity(_stream, index);
        var descriptor = new TraceDescriptor(identity, sequence, TimeSpan.FromMilliseconds(index * 2),
            DateTimeOffset.UtcNow, null, samples.Length, _metadata.Configuration.SampleRateHz,
            _metadata.Configuration.FirstSampleOffsetSeconds, _metadata.Configuration.SignalMode,
            provenance: UtTraceProvenance.Simulator, traceQuality: UtTraceQuality.Good);
        InspectionAppendResult result = await _writer.CommitAsync(
            new InspectionCommitInput(new InspectionTraceInput(descriptor, samples)));
        if (result.IsCommitted)
        {
            _aVisuals.NotifyCommitted(identity, result.Revision);
            _bIngress.NotifyCommitted(identity, result.Revision);
        }
        Status.Text = $"Running · Sequence {sequence} · AcquisitionIndex {index} · B→A selection enabled";
    }

    private void OpenBScan()
    {
        _bScan = new() { Context = new(_bVisuals, _viewport, _selection) };
        BScanHost.Content = _bScan;
        ToggleBButton.Content = "Cerrar B-Scan";
    }

    private async Task CloseBScanAsync()
    {
        BScanView? scan = _bScan;
        if (scan is null) { return; }
        _bScan = null;
        await scan.CloseAsync();
        BScanHost.Content = null;
        ToggleBButton.Content = "Reabrir B-Scan";
    }

    private SessionSnapshot Session(SessionPhase phase, bool canStart, bool canStop) =>
        new(checked(_acquisitionIndex + 1), phase, _metadata.SourceId, _metadata.RunId,
            checked((long)_acquisitionIndex), checked((long)_acquisitionIndex),
            _sequence == 10_000 ? null : _sequence - 1, _metadata, null, [], 0,
            canStart: canStart, canStop: canStop);

    private void UpdateStatus()
    {
        StartButton.IsEnabled = !_running && !_stopping && !_closing;
        StopButton.IsEnabled = _running && !_stopping && !_closing;
        Status.Text = $"{(_stopping ? "Stopping" : _running ? "Running" : "Stopped")} · A active · B {(_bScan is null ? "closed" : "active")}";
    }

    private async void OnClosing(object? sender, CancelEventArgs args)
    {
        if (_closing) { return; }
        args.Cancel = true;
        _closing = true;
        _running = false;
        _timer.Stop();
        _timer.Tick -= OnTick;
        ToggleBButton.IsEnabled = false;
        List<Exception> failures = [];
        InspectionRunSealResult? final = null;
        await CaptureAsync("active acquisition callback", () => _activeTick);
        await CaptureAsync("B toggle", () => _toggleBTask);
        await CaptureAsync("seal", async () => final = await _writer.SealRunAsync());
        if (final is not null)
        { Capture("final B reconciliation", () => _bIngress.RequestFinalReconciliation(_metadata.RunId, final.Value.FinalRevision)); }
        Capture("stop B admission", () => _bIngress.StopAdmission(_metadata.RunId));
        await CaptureAsync("drain B", () => _bIngress.WaitForDrainAsync(_metadata.RunId).AsTask());
        Capture("freeze B", () => _bIngress.FreezeRun(_metadata.RunId));
        Capture("close A run", () => _aVisuals.CloseRun(_metadata.RunId));
        await CaptureAsync("stop A", () => _aVisuals.StopAsync());
        await CaptureAsync("close B component", CloseBScanAsync);
        await CaptureAsync("close A component", AScan.CloseAsync);
        await CaptureAsync("selection scope", () => _selection.DisposeAsync().AsTask());
        await CaptureAsync("viewport", () => _viewport.DisposeAsync().AsTask());
        await CaptureAsync("A delivery", () => _aVisuals.DisposeAsync().AsTask());
        await CaptureAsync("B delivery", () => _bVisuals.DisposeAsync().AsTask());
        await CaptureAsync("B ingress", () => _bIngress.DisposeAsync().AsTask());
        await CaptureAsync("writer", () => _writer.DisposeAsync().AsTask());
        await CaptureAsync("store", () => _stores.DisposeAsync().AsTask());
        Closing -= OnClosing;
        if (failures.Count > 0) { Status.Text = $"Cleanup completed with {failures.Count} error(s)."; }
        Close();

        void Capture(string stage, Action action)
        {
            try { action(); }
            catch (Exception error) { failures.Add(new InvalidOperationException($"{stage} failed", error)); }
        }

        async Task CaptureAsync(string stage, Func<Task> action)
        {
            try { await action(); }
            catch (Exception error) { failures.Add(new InvalidOperationException($"{stage} failed", error)); }
        }
    }

    private sealed class ObservableFeed<T> : IObservable<T>
    {
        private readonly List<IObserver<T>> _observers = [];
        public IDisposable Subscribe(IObserver<T> observer)
        { _observers.Add(observer); return new Subscription(_observers, observer); }
        public void Publish(T value)
        { foreach (IObserver<T> observer in _observers.ToArray()) { observer.OnNext(value); } }
        private sealed class Subscription(List<IObserver<T>> observers, IObserver<T> observer) : IDisposable
        { public void Dispose() => observers.Remove(observer); }
    }

    private sealed class DemoDispatcher(Dispatcher dispatcher) : IUiDispatcher
    {
        public bool CheckAccess() => dispatcher.CheckAccess();
        public async Task InvokeAsync(Action action, CancellationToken cancellationToken = default)
        {
            if (dispatcher.CheckAccess()) { cancellationToken.ThrowIfCancellationRequested(); action(); return; }
            await dispatcher.InvokeAsync(action, DispatcherPriority.DataBind, cancellationToken);
        }
        public async Task EnqueueAsync(Action action, CancellationToken cancellationToken = default) =>
            await dispatcher.InvokeAsync(action, DispatcherPriority.DataBind, cancellationToken);
    }
}
