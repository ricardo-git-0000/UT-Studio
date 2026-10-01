using System.ComponentModel;
using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;

namespace UTStudio.Presentation;

public abstract class SafeObservableObject : INotifyPropertyChanged
{
    private readonly object _observerGate = new();
    private PropertyChangedEventHandler? _propertyChanged;

    public event PropertyChangedEventHandler? PropertyChanged
    {
        add { lock (_observerGate) { _propertyChanged += value; } }
        remove { lock (_observerGate) { _propertyChanged -= value; } }
    }

    protected void OnPropertyChanged(string propertyName)
    {
        PropertyChangedEventHandler? handlers;
        lock (_observerGate) { handlers = _propertyChanged; }
        if (handlers is null) { return; }
        var args = new PropertyChangedEventArgs(propertyName);
        foreach (PropertyChangedEventHandler handler in handlers.GetInvocationList())
        {
            try { handler(this, args); }
            catch (Exception error)
            {
                lock (_observerGate) { _propertyChanged -= handler; }
                OnObserverFailure(error);
            }
        }
    }

    protected virtual void OnObserverFailure(Exception error) { }
}

internal sealed class SafeRelayCommand(Action execute, Func<bool> canExecute,
    Action<Exception>? observerFailure = null) : IRelayCommand
{
    private readonly object _gate = new();
    private EventHandler? _canExecuteChanged;
    internal long ObserverErrorCount { get; private set; }
    public event EventHandler? CanExecuteChanged
    {
        add { lock (_gate) { _canExecuteChanged += value; } }
        remove { lock (_gate) { _canExecuteChanged -= value; } }
    }
    public bool CanExecute(object? parameter) => canExecute();
    public void Execute(object? parameter) { if (CanExecute(parameter)) { execute(); } }
    public void NotifyCanExecuteChanged()
    {
        EventHandler? handlers;
        lock (_gate) { handlers = _canExecuteChanged; }
        if (handlers is null) { return; }
        foreach (EventHandler handler in handlers.GetInvocationList())
        {
            try { handler(this, EventArgs.Empty); }
            catch (Exception error)
            {
                lock (_gate) { _canExecuteChanged -= handler; ObserverErrorCount++; }
                observerFailure?.Invoke(error);
            }
        }
    }
}

internal sealed class SafeParameterRelayCommand(Action<object?> execute, Func<object?, bool> canExecute,
    Action<Exception>? observerFailure = null) : IRelayCommand
{
    private readonly object _gate = new();
    private EventHandler? _canExecuteChanged;
    public event EventHandler? CanExecuteChanged
    { add { lock (_gate) _canExecuteChanged += value; } remove { lock (_gate) _canExecuteChanged -= value; } }
    public bool CanExecute(object? parameter) => canExecute(parameter);
    public void Execute(object? parameter) { if (CanExecute(parameter)) execute(parameter); }
    public void NotifyCanExecuteChanged()
    {
        EventHandler? handlers; lock (_gate) handlers = _canExecuteChanged;
        foreach (EventHandler handler in handlers?.GetInvocationList() ?? [])
        { try { handler(this, EventArgs.Empty); } catch (Exception error) { observerFailure?.Invoke(error); } }
    }
}
