using CommunityToolkit.Mvvm.ComponentModel;
using UTStudio.Visualization.Core;

namespace UTStudio.Presentation;

/// <summary>UI-affine owner shared by scan ViewModels that present the same physical time axis.</summary>
public sealed class SharedScanTimeViewport(IUiDispatcher dispatcher) : ObservableObject
{
    private readonly UiLifetime _lifetime = new(dispatcher);
    private ScanTimeViewport? _viewport;

    public ScanTimeViewport? Viewport => _viewport;

    public void ReconcileDomain(double minimumSeconds, double maximumSeconds) => Post(current => current is null
        ? ScanTimeViewportOperations.Create(minimumSeconds, maximumSeconds)
        : ScanTimeViewportOperations.Reconcile(current, minimumSeconds, maximumSeconds));

    public void Zoom(double anchorSeconds, double factor) =>
        Post(current => current is null ? null : ScanTimeViewportOperations.Zoom(current, anchorSeconds, factor));

    public void Pan(double deltaSeconds) =>
        Post(current => current is null ? null : ScanTimeViewportOperations.Pan(current, deltaSeconds));

    public void Reset() => Post(current => current is null ? null : ScanTimeViewportOperations.Reset(current));

    private void Post(Func<ScanTimeViewport?, ScanTimeViewport?> change) => _lifetime.Post(() =>
    {
        ScanTimeViewport? next = change(_viewport);
        if (next == _viewport) { return; }
        _viewport = next;
        OnPropertyChanged(nameof(Viewport));
    });
}
