namespace UTStudio.Presentation;

/// <summary>Publishes the single gate-editing graph created when a deferred inspection is prepared.</summary>
public sealed class GateEditingContextSource
{
    private readonly object _gate = new();
    private GateEditingComponentContext? _current;
    public event Action<GateEditingComponentContext>? Available;
    public GateEditingComponentContext? Current { get { lock (_gate) return _current; } }
    public void Publish(GateEditingComponentContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        Action<GateEditingComponentContext>? handlers;
        lock (_gate)
        {
            if (_current is not null) throw new InvalidOperationException("The gate editing context is one-shot.");
            _current = context; handlers = Available;
        }
        handlers?.Invoke(context);
    }
}
