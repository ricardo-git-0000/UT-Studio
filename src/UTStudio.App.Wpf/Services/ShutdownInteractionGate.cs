using UTStudio.Presentation;

namespace UTStudio.App.Wpf.Services;

/// <summary>Blocks shell interaction as the first shutdown stage from UI or non-UI callers.</summary>
public sealed class ShutdownInteractionGate(IUiDispatcher dispatcher)
{
    private readonly object _gate = new();
    private Action? _blockShell;
    private bool _blocked;

    internal void Register(Action blockShell)
    {
        ArgumentNullException.ThrowIfNull(blockShell);
        bool apply;
        lock (_gate) { _blockShell = blockShell; apply = _blocked; }
        if (apply) { _ = ObserveAsync(dispatcher.InvokeAsync(blockShell)); }
    }

    internal Task BlockAsync()
    {
        Action? block;
        lock (_gate) { _blocked = true; block = _blockShell; }
        return block is null ? Task.CompletedTask : dispatcher.InvokeAsync(block);
    }

    private static async Task ObserveAsync(Task task)
    { try { await task.ConfigureAwait(false); } catch { } }
}
