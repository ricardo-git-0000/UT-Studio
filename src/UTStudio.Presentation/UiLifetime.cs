namespace UTStudio.Presentation;

/// <summary>Invalidates queued work immediately and provides a serial UI barrier for close.</summary>
internal sealed class UiLifetime(IUiDispatcher dispatcher)
{
    private int _closed;
    internal bool IsClosed => Volatile.Read(ref _closed) != 0;
    internal void Close() => Interlocked.Exchange(ref _closed, 1);
    internal bool CheckAccess() => dispatcher.CheckAccess();

    internal Task InvokeAsync(Action action)
    {
        if (IsClosed) { return Task.CompletedTask; }
        return dispatcher.InvokeAsync(() =>
        {
            if (IsClosed) { return; }
            if (!dispatcher.CheckAccess()) { throw new InvalidOperationException("The UI dispatcher violated its access contract."); }
            action();
        });
    }

    internal Task BarrierAsync() => dispatcher.CheckAccess() ? Task.CompletedTask : dispatcher.InvokeAsync(() => { });
}
