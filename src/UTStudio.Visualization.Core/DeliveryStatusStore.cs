namespace UTStudio.Visualization.Core;

/// <summary>Latest immutable status with replay; external callbacks never hold the store lock.</summary>
internal sealed class DeliveryStatusStore : IObservable<AScanDeliveryStatus>, IDisposable
{
    private readonly object _gate = new();
    private readonly List<Subscription> _subscriptions = [];
    private AScanDeliveryStatus _current = new(0, null);
    private bool _closed;

    internal void Publish(AScanDeliveryStatus status)
    {
        lock (_gate)
        {
            if (_closed) { return; }
            _current = status;
            foreach (var subscription in _subscriptions) { subscription.Offer(status); }
        }
    }

    public IDisposable Subscribe(IObserver<AScanDeliveryStatus> observer)
    {
        ArgumentNullException.ThrowIfNull(observer);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_closed, this);
            var subscription = new Subscription(this, observer);
            _subscriptions.Add(subscription);
            subscription.Offer(_current);
            return subscription;
        }
    }

    public void Dispose()
    {
        Subscription[] subscriptions;
        lock (_gate) { _closed = true; subscriptions = _subscriptions.ToArray(); _subscriptions.Clear(); }
        foreach (var subscription in subscriptions) { subscription.Dispose(); }
    }

    private sealed class Subscription(DeliveryStatusStore store, IObserver<AScanDeliveryStatus> observer) : IDisposable
    {
        private readonly object _callbackGate = new();
        private AScanDeliveryStatus? _pending;
        private bool _scheduled, _closed;

        // Called with the store gate held. Does not wait for external callbacks.
        internal void Offer(AScanDeliveryStatus status)
        {
            if (_closed) { return; }
            _pending = status;
            if (_scheduled) { return; }
            _scheduled = true;
            _ = Task.Run(Pump);
        }

        private void Pump()
        {
            while (true)
            {
                AScanDeliveryStatus? next;
                lock (store._gate)
                {
                    next = _pending;
                    _pending = null;
                    if (_closed || next is null) { _scheduled = false; return; }
                }
                try
                {
                    lock (_callbackGate)
                    {
                        lock (store._gate) { if (_closed || store._closed) { return; } }
                        observer.OnNext(next);
                    }
                }
                catch { Dispose(); return; } // A failing observer cannot break delivery to its peers.
            }
        }

        public void Dispose()
        {
            lock (store._gate) { _closed = true; _pending = null; store._subscriptions.Remove(this); }
            lock (_callbackGate) { } // Reentrant for self-cancellation; no store lock while waiting.
        }
    }
}
