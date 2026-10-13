using System.Windows.Threading;
using UTStudio.Presentation;

namespace UTStudio.ScanVisualization.AScan.Wpf;

internal sealed class WpfUiDispatcher(Dispatcher dispatcher) : IUiDispatcher
{
    public bool CheckAccess() => dispatcher.CheckAccess();
    public Task InvokeAsync(Action action, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (cancellationToken.IsCancellationRequested) { return Task.FromCanceled(cancellationToken); }
        return dispatcher.CheckAccess() ? InvokeInline(action) : dispatcher.InvokeAsync(action,
            DispatcherPriority.Normal, cancellationToken).Task;
    }

    private static Task InvokeInline(Action action)
    { try { action(); return Task.CompletedTask; } catch (Exception error) { return Task.FromException(error); } }
}
