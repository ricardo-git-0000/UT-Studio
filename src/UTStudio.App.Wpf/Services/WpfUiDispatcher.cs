using System.Windows.Threading;
using UTStudio.Presentation;

namespace UTStudio.App.Wpf.Services;

public sealed class WpfUiDispatcher(Dispatcher dispatcher) : IUiDispatcher
{
    public bool CheckAccess() => dispatcher.CheckAccess();

    public Task InvokeAsync(Action action, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(action);
        return dispatcher.InvokeAsync(action, DispatcherPriority.DataBind, cancellationToken).Task;
    }
}
