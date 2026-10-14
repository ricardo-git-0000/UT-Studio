using System.ComponentModel;
using System.Windows;
using System.Windows.Threading;
using UTStudio.Contracts.Inspection;
using UTStudio.Domain.Acquisition;
using UTStudio.Domain.Inspection;
using UTStudio.Inspection.Core;
using UTStudio.Presentation;
using UTStudio.Visualization.Core;

namespace UTStudio.BScan.Wpf.Demo;

public partial class MainWindow : Window
{
    private readonly InspectionDataStoreRegistry _stores;
    private readonly IInspectionRunWriter _writer;
    private readonly BScanIngress _ingress;
    private readonly BScanVisualDelivery _visuals;
    private readonly SharedScanTimeViewport _viewport;
    private readonly TraceSelectionScope _selection;
    private readonly DispatcherTimer _timer;
    private readonly ConventionalUtFrameMetadata _metadata;
    private ulong _index;
    private bool _closing;

    public MainWindow()
    {
        InitializeComponent();
        var inspection = new InspectionId(Guid.NewGuid());
        var run = new AcquisitionRunId(Guid.NewGuid());
        _metadata = new(new("bscan-demo"), run,
            new(new(0), 256, 10_000_000, 0, UtSignalMode.Rf), DateTimeOffset.UtcNow);
        _stores = new(inspection, new InMemoryInspectionStoreOptions(16 * 1024 * 1024, 2048));
        _writer = _stores.OpenRun(run);
        _ingress = new(_stores.Store, _stores.Store);
        _ingress.OpenRun(run);
        _visuals = new(_ingress);
        var ui = new WpfDemoDispatcher(Dispatcher);
        _viewport = new(ui);
        _selection = new(ui, new UtTraceAScanSelector(_stores.Store));
        Scan.Context = new(_visuals, _viewport, _selection);
        _timer = new(DispatcherPriority.Background, Dispatcher) { Interval = TimeSpan.FromMilliseconds(80) };
        _timer.Tick += OnTick;
        Loaded += (_, _) => _timer.Start();
        Closing += OnClosing;
    }

    private async void OnTick(object? sender, EventArgs args)
    {
        _timer.Stop();
        try
        {
            ulong index = _index++;
            short[] samples = Enumerable.Range(0, _metadata.Configuration.SampleCount)
                .Select(sample => (short)(Math.Sin(sample * Math.PI / 24 + index / 7d) * 28000)).ToArray();
            var stream = new UtStreamIdentity(_stores.InspectionId, _metadata.RunId,
                _metadata.SourceId, _metadata.Configuration.PhysicalChannelId);
            var descriptor = new TraceDescriptor(new(stream, index), index, TimeSpan.FromMilliseconds(index * 2),
                DateTimeOffset.UtcNow, null, samples.Length, _metadata.Configuration.SampleRateHz,
                _metadata.Configuration.FirstSampleOffsetSeconds, _metadata.Configuration.SignalMode);
            InspectionAppendResult result = await _writer.CommitAsync(
                new InspectionCommitInput(new InspectionTraceInput(descriptor, samples)));
            if (result.IsCommitted) { _ingress.NotifyCommitted(descriptor.Identity, result.Revision); }
        }
        finally { if (!_closing) { _timer.Start(); } }
    }

    private async void OnClosing(object? sender, CancelEventArgs args)
    {
        if (_closing) { return; }
        args.Cancel = true;
        _closing = true;
        _timer.Stop();
        await Scan.CloseAsync();
        await _selection.DisposeAsync();
        await _viewport.DisposeAsync();
        await _visuals.DisposeAsync();
        await _ingress.DisposeAsync();
        await _writer.DisposeAsync();
        await _stores.DisposeAsync();
        Closing -= OnClosing;
        Close();
    }

    private sealed class WpfDemoDispatcher(Dispatcher dispatcher) : IUiDispatcher
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
