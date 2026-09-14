using System.Collections.Concurrent;
using UTStudio.Contracts.Application;
using UTStudio.Domain.Acquisition;
using UTStudio.Presentation;

namespace UTStudio.Core.Tests.Presentation;

internal sealed class ManualUiDispatcher : IUiDispatcher
{
    [ThreadStatic] private static ManualUiDispatcher? _executing;
    private readonly ConcurrentQueue<(Action Action, CancellationToken Token, TaskCompletionSource Done)> _queue = new();
    private readonly object _executionGate = new();
    internal int Pending => _queue.Count;
    internal bool FailDispatch { get; set; }
    public bool CheckAccess() => ReferenceEquals(_executing, this);
    public Task InvokeAsync(Action action, CancellationToken cancellationToken = default)
    {
        if (FailDispatch) { return Task.FromException(new InvalidOperationException("UI unavailable")); }
        if (CheckAccess()) { cancellationToken.ThrowIfCancellationRequested(); action(); return Task.CompletedTask; }
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _queue.Enqueue((action, cancellationToken, done));
        return done.Task;
    }

    internal bool RunNext()
    {
        lock (_executionGate)
        {
            if (!_queue.TryDequeue(out var item)) { return false; }
            var previous = _executing;
            _executing = this;
            try { item.Token.ThrowIfCancellationRequested(); item.Action(); item.Done.TrySetResult(); }
            catch (OperationCanceledException) { item.Done.TrySetCanceled(); }
            catch (Exception error) { item.Done.TrySetException(error); }
            finally { _executing = previous; }
            return true;
        }
    }

    internal async Task DriveAsync(Task completion)
    {
        await DriveUntilAsync(() => completion.IsCompleted);
        await completion;
    }

    internal async Task DriveUntilAsync(Func<bool> condition)
    {
        using var watchdog = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!condition())
        {
            watchdog.Token.ThrowIfCancellationRequested();
            if (!RunNext()) { await Task.Yield(); }
        }
    }

    internal static async Task UntilAsync(Func<bool> condition)
    {
        using var watchdog = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!condition()) { watchdog.Token.ThrowIfCancellationRequested(); await Task.Yield(); }
    }
}

internal sealed class ManualObservable<T> : IObservable<T>
{
    private readonly object _gate = new();
    private readonly List<IObserver<T>> _observers = [];
    internal IObserver<T>? LastObserver { get; private set; }
    internal int Unsubscriptions { get; private set; }
    internal int Subscribers { get { lock (_gate) { return _observers.Count; } } }
    internal bool FailUnsubscribe { get; set; }
    public IDisposable Subscribe(IObserver<T> observer)
    {
        lock (_gate) { _observers.Add(observer); LastObserver = observer; }
        return new Subscription(this, observer);
    }
    internal void Emit(T value)
    {
        IObserver<T>[] observers;
        lock (_gate) { observers = _observers.ToArray(); }
        foreach (var observer in observers) { observer.OnNext(value); }
    }
    internal void Fail(Exception error)
    {
        IObserver<T>[] observers;
        lock (_gate) { observers = _observers.ToArray(); }
        foreach (var observer in observers) { observer.OnError(error); }
    }
    private sealed class Subscription(ManualObservable<T> owner, IObserver<T> observer) : IDisposable
    {
        private int _disposed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) { return; }
            lock (owner._gate) { owner._observers.Remove(observer); owner.Unsubscriptions++; }
            if (owner.FailUnsubscribe) { throw new InvalidOperationException("unsubscribe failed"); }
        }
    }
}

internal sealed class ManualApplicationSession : IApplicationSession
{
    private readonly object _gate = new();
    private SessionSnapshot _snapshot;
    private int _startCalls, _stopCalls;
    internal static readonly UtSourceId Source = new("presentation-tests");
    internal ManualObservable<SessionSnapshot> Updates { get; } = new();
    internal Func<CancellationToken, Task>? StartAction { get; set; }
    internal Func<CancellationToken, Task>? StopAction { get; set; }
    internal int StartCalls => Volatile.Read(ref _startCalls);
    internal int StopCalls => Volatile.Read(ref _stopCalls);
    internal AcquisitionRunId? LastRun { get; private set; }
    internal ConventionalAcquisitionConfiguration? LastConfiguration { get; private set; }
    internal ManualApplicationSession() => _snapshot = Create(0, SessionPhase.Idle, canStart: true);
    public SessionSnapshot Snapshot { get { lock (_gate) { return _snapshot; } } }
    public IDisposable Subscribe(IObserver<SessionSnapshot> observer)
    {
        var subscription = Updates.Subscribe(observer);
        observer.OnNext(Snapshot);
        return subscription;
    }

    public async Task StartAsync(ConventionalAcquisitionConfiguration configuration, AcquisitionRunId runId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        LastRun = runId;
        LastConfiguration = configuration;
        Interlocked.Increment(ref _startCalls);
        Set(Create(Snapshot.Version + 1, SessionPhase.Starting, runId, canStop: true));
        try
        {
            if (StartAction is not null) { await StartAction(cancellationToken); }
            Set(Create(Snapshot.Version + 1, SessionPhase.Running, runId, canStop: true));
        }
        catch
        {
            Set(Create(Snapshot.Version + 1, SessionPhase.Idle, runId, canStart: true));
            throw;
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _stopCalls);
        if (StopAction is not null) { await StopAction(cancellationToken); }
        Set(Create(Snapshot.Version + 1, SessionPhase.Idle, Snapshot.RunId, canStart: true));
    }

    internal void Set(SessionSnapshot snapshot)
    {
        lock (_gate) { _snapshot = snapshot; }
        Updates.Emit(snapshot);
    }
    internal static SessionSnapshot Create(ulong version, SessionPhase phase, AcquisitionRunId? run = null,
        bool canStart = false, bool canStop = false, long received = 0, long released = 0,
        UtSourceError? error = null, UtSourceError? visualError = null) =>
        new(version, phase, Source, run, received, released, null, null, error, [], 0, visualError, canStart, canStop);
}
