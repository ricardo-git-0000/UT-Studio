using CommunityToolkit.Mvvm.ComponentModel;
using UTStudio.Domain.Acquisition;
using UTStudio.Visualization.Core;

namespace UTStudio.Presentation;

/// <summary>UI-affine owner shared by scan ViewModels that present the same physical time axis.</summary>
public sealed class SharedScanTimeViewport(IUiDispatcher dispatcher) : ObservableObject, IAsyncDisposable
{
    private readonly UiLifetime _lifetime = new(dispatcher);
    private readonly Dictionary<string, DomainRegistration> _domains = [];
    private readonly HashSet<AcquisitionRunId> _retiredRuns = [];
    private readonly Queue<AcquisitionRunId> _retiredRunOrder = [];
    private const int RetiredRunCapacity = 64;
    private ScanTimeViewport? _viewport;
    private AcquisitionRunId? _runId;
    private bool _deferNotifications, _deferredViewportChanged, _deferredRunChanged;

    public ScanTimeViewport? Viewport => _viewport;
    public AcquisitionRunId? RunId => _runId;
    public bool IsClosed => _lifetime.IsClosed;

    public void ReconcileDomain(double minimumSeconds, double maximumSeconds) => _lifetime.Post(() =>
    {
        if (_domains.Count != 0)
        { throw new InvalidOperationException("Unidentified domains cannot be mixed with a synchronized scan group."); }
        SetViewport(_viewport is null ? ScanTimeViewportOperations.Create(minimumSeconds, maximumSeconds)
            : ScanTimeViewportOperations.Reconcile(_viewport, minimumSeconds, maximumSeconds));
    });

    /// <summary>Registers or updates one scan's physical time domain in a synchronized group.</summary>
    public void ReconcileDomain(string consumerId, AcquisitionRunId runId, ulong snapshotVersion,
        double minimumSeconds, double maximumSeconds)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(consumerId);
        _lifetime.Post(() => ReconcileDomainCore(consumerId, runId, snapshotVersion, minimumSeconds, maximumSeconds));
    }

    internal void ReconcileDomainDeferred(string consumerId, AcquisitionRunId runId, ulong snapshotVersion,
        double minimumSeconds, double maximumSeconds)
    {
        if (!_lifetime.CheckAccess()) { throw new InvalidOperationException("Deferred reconciliation requires UI access."); }
        _deferNotifications = true;
        try { ReconcileDomainCore(consumerId, runId, snapshotVersion, minimumSeconds, maximumSeconds); }
        catch
        {
            _deferNotifications = false;
            _deferredViewportChanged = false;
            _deferredRunChanged = false;
            throw;
        }
    }

    internal void RemoveConsumerDeferred(string consumerId)
    {
        if (!_lifetime.CheckAccess()) { throw new InvalidOperationException("Deferred removal requires UI access."); }
        _deferNotifications = true;
        try { RemoveConsumerCore(consumerId); }
        catch
        {
            _deferNotifications = false;
            _deferredViewportChanged = false;
            _deferredRunChanged = false;
            throw;
        }
    }

    internal void PublishDeferredChanges()
    {
        if (!_lifetime.CheckAccess()) { throw new InvalidOperationException("Deferred publication requires UI access."); }
        try
        {
            while (_deferredViewportChanged || _deferredRunChanged)
            {
                bool viewportChanged = _deferredViewportChanged, runChanged = _deferredRunChanged;
                _deferredViewportChanged = false;
                _deferredRunChanged = false;
                if (viewportChanged) { OnPropertyChanged(nameof(Viewport)); }
                if (runChanged) { OnPropertyChanged(nameof(RunId)); }
            }
        }
        finally { _deferNotifications = false; }
    }

    private void ReconcileDomainCore(string consumerId, AcquisitionRunId runId, ulong snapshotVersion,
        double minimumSeconds, double maximumSeconds)
    {
        if (_retiredRuns.Contains(runId)) { return; }
        bool newRun = _runId is not { } activeRun || activeRun != runId;
        if (newRun)
        {
            if (_runId is { } previousRun) { Retire(previousRun); }
            _runId = runId;
            _domains.Clear();
        }
        if (_domains.TryGetValue(consumerId, out var previous) && !newRun && snapshotVersion <= previous.Version) { return; }
        _domains[consumerId] = new(snapshotVersion, minimumSeconds, maximumSeconds);
        double minimum = _domains.Values.Max(domain => domain.MinimumSeconds);
        double maximum = _domains.Values.Min(domain => domain.MaximumSeconds);
        if (!double.IsFinite(minimum) || !double.IsFinite(maximum) || maximum <= minimum)
        {
            SetViewport(null);
            NotifyRunChanged(newRun);
            return;
        }
        ScanTimeViewport next = newRun || _viewport is null
            ? ScanTimeViewportOperations.Create(minimum, maximum)
            : ScanTimeViewportOperations.Reconcile(_viewport, minimum, maximum);
        SetViewport(next);
        NotifyRunChanged(newRun);
    }

    public void RemoveConsumer(string consumerId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(consumerId);
        _lifetime.Post(() => RemoveConsumerCore(consumerId));
    }

    public Task RemoveConsumerAsync(string consumerId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(consumerId);
        return _lifetime.InvokeAsync(() => RemoveConsumerCore(consumerId));
    }

    private void RemoveConsumerCore(string consumerId)
    {
        if (!_domains.Remove(consumerId)) { return; }
        if (_domains.Count == 0) { SetViewport(null); return; }
        double minimum = _domains.Values.Max(domain => domain.MinimumSeconds);
        double maximum = _domains.Values.Min(domain => domain.MaximumSeconds);
        if (maximum <= minimum) { SetViewport(null); return; }
        SetViewport(_viewport is null ? ScanTimeViewportOperations.Create(minimum, maximum)
            : ScanTimeViewportOperations.Reconcile(_viewport, minimum, maximum));
    }

    public void Zoom(double anchorSeconds, double factor) =>
        Post(current => current is null ? null : ScanTimeViewportOperations.Zoom(current, anchorSeconds, factor));

    public void Pan(double deltaSeconds) =>
        Post(current => current is null ? null : ScanTimeViewportOperations.Pan(current, deltaSeconds));

    public void Reset() => Post(current => current is null ? null : ScanTimeViewportOperations.Reset(current));

    private void Post(Func<ScanTimeViewport?, ScanTimeViewport?> change) => _lifetime.Post(() =>
    {
        ScanTimeViewport? next = change(_viewport);
        SetViewport(next);
    });

    private void SetViewport(ScanTimeViewport? next)
    {
        if (next == _viewport) { return; }
        _viewport = next;
        if (_deferNotifications) { _deferredViewportChanged = true; }
        else { OnPropertyChanged(nameof(Viewport)); }
    }

    private void NotifyRunChanged(bool changed)
    {
        if (!changed) { return; }
        if (_deferNotifications) { _deferredRunChanged = true; }
        else { OnPropertyChanged(nameof(RunId)); }
    }

    private void Retire(AcquisitionRunId runId)
    {
        if (!_retiredRuns.Add(runId)) { return; }
        _retiredRunOrder.Enqueue(runId);
        if (_retiredRunOrder.Count > RetiredRunCapacity)
        { _retiredRuns.Remove(_retiredRunOrder.Dequeue()); }
    }

    public async ValueTask DisposeAsync()
    {
        await _lifetime.InvokeAsync(() =>
        {
            _domains.Clear();
            _retiredRuns.Clear();
            _retiredRunOrder.Clear();
            _viewport = null;
            _lifetime.Close();
        }).ConfigureAwait(false);
        await _lifetime.BarrierAsync().ConfigureAwait(false);
    }

    private sealed record DomainRegistration(ulong Version, double MinimumSeconds, double MaximumSeconds);
}
