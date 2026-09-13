namespace UTStudio.Application;

/// <summary>One active callback and one replaceable pending snapshot per subscription.</summary>
internal sealed class SessionSnapshotPublisher(SessionSnapshot initial) : IObservable<SessionSnapshot>
{
    private readonly object _gate = new();
    private readonly List<Subscription> _subscriptions = [];
    private SessionSnapshot _current = initial;
    private bool _completed;

    public IDisposable Subscribe(IObserver<SessionSnapshot> observer)
    {
        ArgumentNullException.ThrowIfNull(observer);
        lock (_gate)
        {
            var subscription = new Subscription(this, observer);
            if (!_completed) { _subscriptions.Add(subscription); }
            subscription.Offer(_current, _completed);
            return subscription;
        }
    }

    internal void Publish(SessionSnapshot snapshot, bool complete = false)
    {
        lock (_gate)
        {
            if (_completed) { return; }
            _current = snapshot;
            _completed = complete;
            foreach (var subscription in _subscriptions) { subscription.Offer(snapshot, complete); }
            if (complete) { _subscriptions.Clear(); }
        }
    }

    private void Remove(Subscription subscription)
    {
        lock (_gate) { _subscriptions.Remove(subscription); }
    }

    private sealed class Subscription(SessionSnapshotPublisher publisher, IObserver<SessionSnapshot> observer) : IDisposable
    {
        private readonly object _gate = new();
        private SessionSnapshot? _pending;
        private bool _scheduled;
        private bool _disposed;
        private bool _complete;

        internal void Offer(SessionSnapshot snapshot, bool complete)
        {
            lock (_gate)
            {
                if (_disposed) { return; }
                _pending = snapshot;
                _complete |= complete;
                if (_scheduled) { return; }
                _scheduled = true;
                _ = Task.Run(Pump);
            }
        }

        private void Pump()
        {
            while (true)
            {
                SessionSnapshot? next;
                bool complete;
                lock (_gate)
                {
                    if (_disposed) { _scheduled = false; return; }
                    next = _pending;
                    _pending = null;
                    complete = _complete;
                    if (next is null && !complete) { _scheduled = false; return; }
                }

                try
                {
                    // Callback admission above is serialized with disposal. An admitted callback may finish;
                    // disposal never waits for user code and no callback owns sample buffers.
                    if (next is not null) { observer.OnNext(next); }
                    if (complete) { observer.OnCompleted(); Dispose(); return; }
                }
                catch
                {
                    // A broken observer is detached, never propagated into acquisition or other observers.
                    Dispose();
                    return;
                }
            }
        }

        public void Dispose()
        {
            lock (_gate) { _disposed = true; _pending = null; }
            publisher.Remove(this);
        }
    }
}
