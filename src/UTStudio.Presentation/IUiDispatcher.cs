namespace UTStudio.Presentation;

/// <summary>Serial UI execution, implementable by any UI framework.</summary>
/// <remarks>
/// Actions never overlap. InvokeAsync completes after execution or cancellation; exceptions are observed
/// through its task. CheckAccess is true inside actions. No SynchronizationContext capture is required.
/// Implementations must keep accepting the closing barrier until the ViewModel has been disposed.
/// </remarks>
public interface IUiDispatcher
{
    bool CheckAccess();
    Task InvokeAsync(Action action, CancellationToken cancellationToken = default);
}
