namespace UTStudio.Presentation;

/// <summary>Invalidates queued work immediately and provides a serial UI barrier for close.</summary>
internal sealed class UiLifetime(IUiDispatcher dispatcher)
{
    private readonly object _intentGate = new();
    private readonly Queue<Action> _intentions = new();
    private int _closed;
    internal bool IsClosed => Volatile.Read(ref _closed) != 0;
    internal void Close()
    {
        Interlocked.Exchange(ref _closed, 1);
        lock (_intentGate) { _intentions.Clear(); }
    }
    internal bool CheckAccess() => dispatcher.CheckAccess();

    internal void Post(Action action)
    {
        if (IsClosed) { return; }
        if (dispatcher.CheckAccess())
        {
            Action[] earlier;
            lock (_intentGate)
            {
                if (IsClosed) { return; }
                earlier = _intentions.ToArray();
                _intentions.Clear();
            }
            foreach (var intention in earlier)
            {
                if (IsClosed) { return; }
                intention();
            }
            if (!IsClosed) { action(); }
            return;
        }
        lock (_intentGate)
        {
            if (IsClosed) { return; }
            _intentions.Enqueue(action);
        }
        _ = Observe(InvokeAsync(ApplyNextIntention));
    }

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

    private void ApplyNextIntention()
    {
        Action? action;
        lock (_intentGate)
        {
            if (IsClosed || !_intentions.TryDequeue(out action)) { return; }
        }
        action();
    }

    private static async Task Observe(Task task)
    {
        try { await task.ConfigureAwait(false); }
        catch { } // No safe observable mutation is possible if the UI dispatcher itself is unavailable.
    }
}
