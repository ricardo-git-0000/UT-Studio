using UTStudio.Contracts.Processing;

namespace UTStudio.Application;

/// <summary>
/// Owns bootstrap and explicitly-capable recovery of a process-local gate-plan authority. Dispose closes event
/// admission but does not wait for admitted observer callbacks; completion remains FIFO behind admitted values and
/// may run later. Unsubscribe prevents new admission but cannot cancel a callback already running. Each queued work
/// item owns its subscription until its single drainer finishes; slow and throwing observers are isolated.
/// </summary>
public sealed class InMemoryGatePlanAuthorityOwner : IDisposable
{
    private readonly AuthorityReader _reader;
    private int _recoveryCapabilityTaken;
    private readonly object _lifecycleGate = new();
    private AuthorityLifecycle _lifecycle = AuthorityLifecycle.Recovering;

    public InMemoryGatePlanAuthorityOwner(IEnumerable<CommittedGatePlanSnapshot> initial,
        GateAuthorityGeneration initialGeneration = default)
    {
        ArgumentNullException.ThrowIfNull(initial);
        CommittedGatePlanSnapshot[] captured = initial.ToArray();
        if (captured.Length == 0 || captured.Any(snapshot => snapshot is null))
            throw new ArgumentException("At least one initial committed snapshot is required.", nameof(initial));
        _reader = new(captured, initialGeneration.IsValid ? initialGeneration.Value : 1, MarkDisposed);
    }

    public IGatePlanAuthorityReader Reader
    {
        get
        {
            lock (_lifecycleGate)
                return _lifecycle == AuthorityLifecycle.Operational ? _reader :
                    throw new InvalidOperationException("The authority reader is available only after Seal.");
        }
    }

    /// <summary>Completes after automatic transition to Disposed and all close-time observer drainers finish.</summary>
    public Task Completion => _reader.CloseCompletion;

    public IGatePlanAuthorityReader Seal()
    {
        lock (_lifecycleGate)
        {
            if (_lifecycle != AuthorityLifecycle.Recovering)
                throw new InvalidOperationException("The authority can be sealed exactly once.");
            _lifecycle = AuthorityLifecycle.Operational;
            return _reader;
        }
    }

    /// <summary>Transfers the sole explicit recovery capability. It is never part of the reader port.</summary>
    public GatePlanAuthorityRecoveryCapability TakeRecoveryCapability()
    {
        if (Interlocked.Exchange(ref _recoveryCapabilityTaken, 1) != 0)
            throw new InvalidOperationException("The recovery capability was already transferred.");
        lock (_lifecycleGate)
        {
            if (_lifecycle != AuthorityLifecycle.Recovering)
                throw new InvalidOperationException("Recovery is available only before Seal.");
            return new(this);
        }
    }

    public void Dispose()
    {
        lock (_lifecycleGate)
        {
            if (_lifecycle is AuthorityLifecycle.Closing or AuthorityLifecycle.Disposed) return;
            _lifecycle = AuthorityLifecycle.Closing;
        }
        _reader.Dispose();
    }

    /// <summary>Observes automatic close completion. Cancelling the wait does not alter the lifecycle.</summary>
    public Task WaitForCloseAsync(CancellationToken cancellationToken = default) =>
        Completion.WaitAsync(cancellationToken);

    private GatePlanRecoveryInstallResult InstallRecovered(CommittedGatePlanSnapshot snapshot)
    {
        lock (_lifecycleGate)
        {
            if (_lifecycle is AuthorityLifecycle.Closing or AuthorityLifecycle.Disposed)
                return new(GatePlanRecoveryInstallStatus.OwnerClosingOrDisposed);
            if (_lifecycle != AuthorityLifecycle.Recovering)
                return new(GatePlanRecoveryInstallStatus.OwnerSealed);
            return _reader.InstallRecovered(snapshot);
        }
    }

    private void MarkDisposed()
    {
        lock (_lifecycleGate)
            if (_lifecycle == AuthorityLifecycle.Closing) _lifecycle = AuthorityLifecycle.Disposed;
    }

    private enum AuthorityLifecycle { Recovering, Operational, Closing, Disposed }

    public sealed class GatePlanAuthorityRecoveryCapability : IDisposable
    {
        private readonly object _gate = new();
        private InMemoryGatePlanAuthorityOwner? _owner;
        internal GatePlanAuthorityRecoveryCapability(InMemoryGatePlanAuthorityOwner owner) => _owner = owner;

        public GatePlanRecoveryInstallResult InstallRecovered(CommittedGatePlanSnapshot snapshot)
        {
            ArgumentNullException.ThrowIfNull(snapshot);
            lock (_gate)
            {
                InMemoryGatePlanAuthorityOwner? owner = _owner;
                if (owner is null) return new(GatePlanRecoveryInstallStatus.CapabilityDisposed);
                return owner.InstallRecovered(snapshot);
            }
        }

        public void Dispose() { lock (_gate) _owner = null; }
    }

    internal sealed class AuthorityReader : IGatePlanAuthorityReader, IDisposable
    {
        private readonly object _gate = new();
        private readonly Dictionary<GatePlanAuthorityKey, Entry> _entries = [];
        private ulong _generation;
        private bool _closing;
        private int _closingDrainers;
        private readonly Action _closed;
        private readonly TaskCompletionSource _closeCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal Task CloseCompletion => _closeCompletion.Task;

        internal AuthorityReader(IEnumerable<CommittedGatePlanSnapshot> initial, ulong firstGeneration, Action closed)
        {
            _closed = closed;
            _generation = firstGeneration - 1;
            foreach (CommittedGatePlanSnapshot snapshot in initial)
            {
                if (_entries.ContainsKey(snapshot.AuthorityKey))
                    throw new ArgumentException("Bootstrap cannot contain duplicate authority keys.", nameof(initial));
                _entries.Add(snapshot.AuthorityKey, new(new(snapshot, NextGeneration())));
            }
        }

        public GatePlanAuthoritySnapshot Read(GatePlanAuthorityKey authority)
        { ValidateKey(authority); lock (_gate) return EntryForRead(authority).Current; }

        public IObservable<GatePlanAuthoritySnapshot> Observe(GatePlanAuthorityKey authority)
        { ValidateKey(authority); lock (_gate) return EntryForRead(authority); }

        public GatePlanAuthorityReadStampResult TryReadStamp(GatePlanAuthorityKey authority,
            GateAuthorityGeneration expectedGeneration)
        {
            ValidateKey(authority);
            if (!expectedGeneration.IsValid)
                throw new ArgumentException("A valid expected generation is required.", nameof(expectedGeneration));
            lock (_gate)
            {
                if (_closing) return new(GatePlanAuthorityReadStampStatus.Closed);
                if (!_entries.TryGetValue(authority, out Entry? entry))
                    return new(GatePlanAuthorityReadStampStatus.NotFound);
                GatePlanAuthoritySnapshot current = entry.Current;
                if (current.Generation != expectedGeneration)
                    return new(GatePlanAuthorityReadStampStatus.GenerationChanged, current);
                var stamp = new GatePlanAuthorityReadStamp(authority, current, current.Generation,
                    current.PublicationSequence);
                return new(GatePlanAuthorityReadStampStatus.Acquired, current, stamp);
            }
        }

        internal GatePlanRecoveryInstallResult InstallRecovered(CommittedGatePlanSnapshot committed)
        {
            ArgumentNullException.ThrowIfNull(committed);
            GatePlanAuthoritySnapshot next;
            List<Entry.Subscription> drains;
            lock (_gate)
            {
                if (_closing) return new(GatePlanRecoveryInstallStatus.OwnerClosingOrDisposed);
                if (!_entries.TryGetValue(committed.AuthorityKey, out Entry? entry))
                    return new(GatePlanRecoveryInstallStatus.IdentityNotBootstrapped);
                if (committed.Revision.Value <= entry.Current.Committed.Revision.Value)
                    return new(GatePlanRecoveryInstallStatus.RevisionNotIncreasing);
                if (_generation == ulong.MaxValue)
                    return new(GatePlanRecoveryInstallStatus.GenerationExhausted);
                GateAuthorityGeneration generation = NextGeneration();
                next = new(committed, generation, new(generation.Value));
                drains = entry.SetCurrentAndEnqueue(next);
            }
            foreach (Entry.Subscription drain in drains) drain.Schedule();
            return new(GatePlanRecoveryInstallStatus.Installed, next);
        }

        public void Dispose()
        {
            List<(Entry.Subscription Subscription, bool Schedule)> drains = [];
            bool completed;
            lock (_gate)
            {
                if (_closing) return;
                _closing = true;
                foreach (Entry entry in _entries.Values)
                    drains.AddRange(entry.EnqueueCompletion(DrainerCompleted));
                _entries.Clear();
                _closingDrainers = drains.Count;
                completed = _closingDrainers == 0;
            }
            foreach ((Entry.Subscription drain, bool schedule) in drains)
                if (schedule) drain.Schedule();
            if (completed) CompleteClose();
        }

        private void DrainerCompleted()
        {
            bool completed;
            lock (_gate)
            {
                if (_closingDrainers == 0) return;
                completed = --_closingDrainers == 0;
            }
            if (completed) CompleteClose();
        }

        private void CompleteClose()
        {
            _closed();
            _closeCompletion.TrySetResult();
        }

        private Entry EntryForRead(GatePlanAuthorityKey authority)
        {
            ObjectDisposedException.ThrowIf(_closing, this);
            return _entries.TryGetValue(authority, out Entry? entry) ? entry :
                throw new KeyNotFoundException($"No committed gate plan exists for '{authority}'.");
        }

        private GateAuthorityGeneration NextGeneration()
        {
            if (_generation == ulong.MaxValue) throw new InvalidOperationException("Authority generation is exhausted.");
            return new(++_generation);
        }

        private static void ValidateKey(GatePlanAuthorityKey authority)
        { if (!authority.IsValid) throw new ArgumentException("A valid authority key is required.", nameof(authority)); }

    }

    private sealed class Entry(GatePlanAuthoritySnapshot initial) : IObservable<GatePlanAuthoritySnapshot>
    {
        private readonly List<Subscription> _subscriptions = [];
        private bool _completed;
        internal GatePlanAuthoritySnapshot Current { get; private set; } = initial;

        public IDisposable Subscribe(IObserver<GatePlanAuthoritySnapshot> observer)
        {
            ArgumentNullException.ThrowIfNull(observer);
            Subscription subscription;
            bool drain;
            lock (_subscriptions)
            {
                subscription = new(this, observer);
                if (_completed) drain = subscription.EnqueueCompleted();
                else { _subscriptions.Add(subscription); drain = subscription.EnqueueNext(Current); }
            }
            if (drain) subscription.Schedule();
            return subscription;
        }

        internal List<Subscription> SetCurrentAndEnqueue(GatePlanAuthoritySnapshot snapshot)
        {
            lock (_subscriptions)
            {
                Current = snapshot;
                return _subscriptions.Where(subscription => subscription.EnqueueNext(snapshot)).ToList();
            }
        }

        internal List<(Subscription Subscription, bool Schedule)> EnqueueCompletion(Action drainerCompleted)
        {
            lock (_subscriptions)
            {
                if (_completed) return [];
                _completed = true;
                var drains = new List<(Subscription, bool)>();
                foreach (Subscription subscription in _subscriptions)
                    if (subscription.EnqueueCompleted(drainerCompleted, out bool schedule))
                        drains.Add((subscription, schedule));
                _subscriptions.Clear();
                return drains;
            }
        }

        private void Remove(Subscription subscription) { lock (_subscriptions) _subscriptions.Remove(subscription); }

        internal sealed class Subscription(Entry owner, IObserver<GatePlanAuthoritySnapshot> observer) : IDisposable
        {
            private readonly object _gate = new();
            private readonly Queue<GatePlanAuthoritySnapshot> _pending = [];
            private bool _completionPending;
            private bool _draining;
            private bool _disposed;
            private Action? _terminal;

            internal bool EnqueueNext(GatePlanAuthoritySnapshot snapshot)
            {
                lock (_gate)
                {
                    if (_disposed || _completionPending) return false;
                    _pending.Enqueue(snapshot);
                    if (_draining) return false;
                    return _draining = true;
                }
            }

            internal bool EnqueueCompleted()
            {
                lock (_gate)
                {
                    if (_disposed || _completionPending) return false;
                    _completionPending = true;
                    if (_draining) return false;
                    return _draining = true;
                }
            }

            internal bool EnqueueCompleted(Action terminal, out bool schedule)
            {
                lock (_gate)
                {
                    if (_disposed || _completionPending) { schedule = false; return false; }
                    _terminal = terminal;
                    _completionPending = true;
                    schedule = !_draining;
                    if (schedule) _draining = true;
                    return true;
                }
            }

            internal void Drain()
            {
                while (true)
                {
                    GatePlanAuthoritySnapshot? next;
                    bool complete;
                    lock (_gate)
                    {
                        if (_disposed) { _draining = false; return; }
                        if (_pending.TryDequeue(out next)) complete = false;
                        else if (_completionPending) { _completionPending = false; _disposed = true; complete = true; }
                        else { _draining = false; return; }
                    }
                    try
                    {
                        if (complete)
                        {
                            try { observer.OnCompleted(); }
                            finally { CompleteTerminal(); }
                            return;
                        }
                        observer.OnNext(next!);
                    }
                    catch { Dispose(); return; }
                }
            }

            internal void Schedule() => ThreadPool.UnsafeQueueUserWorkItem(
                static subscription => subscription.Drain(), this, preferLocal: false);

            public void Dispose()
            {
                Action? terminal;
                lock (_gate)
                {
                    if (_disposed) return;
                    _disposed = true; _pending.Clear(); _completionPending = false;
                    terminal = _terminal; _terminal = null;
                }
                owner.Remove(this);
                terminal?.Invoke();
            }

            private void CompleteTerminal()
            {
                Action? terminal;
                lock (_gate) { terminal = _terminal; _terminal = null; }
                terminal?.Invoke();
            }
        }
    }
}
