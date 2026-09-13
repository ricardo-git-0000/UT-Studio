using UTStudio.Contracts.Presentation;
using UTStudio.Domain.Acquisition;

namespace UTStudio.Visualization.Core;

/// <summary>Independent-snapshot mailbox of capacity one, with publication limited by a monotonic clock.</summary>
/// <remarks>
/// Explicit adaptation of ADR 0008 for the borrowed-span port: projection happens synchronously per
/// interested input, not at 30 Hz. Only independent data crosses the async boundary. This adds O(N)
/// work/allocation per input. It does not implement Presentation coordination or the future UI refresh limit.
/// Subscribers have one admitted callback and one latest pending snapshot; slow/failing user code cannot
/// block Accept, CloseRun or disposal. An already admitted callback may finish after invalidation.
/// </remarks>
public sealed class AScanVisualDelivery : IConventionalFrameSink, IObservable<AScanSnapshot>, IAsyncDisposable
{
    public static readonly TimeSpan MinimumPublicationInterval = TimeSpan.FromTicks(333_334);
    private readonly object _gate = new();
    private readonly IAScanProjector _projector;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _wake = new(0, 1);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly List<Subscription> _subscriptions = [];
    private readonly Task _worker;
    private Task? _disposal;
    private bool _disposed;
    private bool _failed;
    private AcquisitionRunId? _runId;
    private long _generation;
    private ulong _version;
    private ulong? _lastSequence;
    private TimeSpan _lastElapsed;
    private long? _lastPublication;
    private AScanSnapshot? _pending;
    private AScanSnapshot? _current;
    private long _received, _published, _replaced, _dropped, _observerCoalesced, _observerErrors;
    private UtSourceError? _lastError;

    public AScanVisualDelivery(IAScanProjector? projector = null, TimeProvider? timeProvider = null)
    {
        _projector = projector ?? new AScanProjector();
        _timeProvider = timeProvider ?? TimeProvider.System;
        _worker = Task.Run(PublishLoopAsync);
    }

    public AScanSnapshot? Current { get { lock (_gate) { return _current; } } }
    public AScanDeliveryStatistics Statistics
    {
        get
        {
            lock (_gate)
            {
                return new(_received, _published, _replaced, _dropped, _observerCoalesced,
                    _observerErrors, _pending is not null, _lastError);
            }
        }
    }

    public void OpenRun(AcquisitionRunId runId)
    {
        if (!runId.IsValid) { throw new ArgumentException("Invalid run identifier.", nameof(runId)); }
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_runId is not null) { throw new InvalidOperationException("A visual execution is already open."); }
            if (_failed) { throw new InvalidOperationException("The visual publisher has failed."); }
            Invalidate();
            _runId = runId;
            _lastSequence = null;
            _lastElapsed = TimeSpan.Zero;
        }
    }

    public void Accept(ConventionalUtFrameMetadata metadata, ulong sequence, TimeSpan elapsedSinceRunStart,
        ReadOnlySpan<short> samples)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        long generation;
        ulong version;
        lock (_gate)
        {
            _received++;
            if (_disposed || _failed || _runId != metadata.RunId || _subscriptions.Count == 0)
            {
                _dropped++;
                return;
            }

            if ((_lastSequence is { } previous && sequence <= previous) || elapsedSinceRunStart < _lastElapsed)
            {
                _dropped++;
                throw new ArgumentException("Visual input must be ordered within its execution.", nameof(sequence));
            }
            _lastSequence = sequence;
            _lastElapsed = elapsedSinceRunStart;
            generation = _generation;
            version = ++_version;
        }

        AScanSnapshot snapshot;
        try { snapshot = _projector.Project(metadata, sequence, elapsedSinceRunStart, samples, version); }
        catch (Exception error)
        {
            lock (_gate)
            {
                _dropped++;
                _lastError = Describe("visual.projection", error);
                if (_generation == generation) { Invalidate(); _runId = null; }
            }
            throw;
        }

        lock (_gate)
        {
            if (_disposed || _generation != generation || _subscriptions.Count == 0)
            {
                _dropped++;
                return;
            }
            if (_pending is not null) { _replaced++; }
            _pending = snapshot;
            if (_wake.CurrentCount == 0) { _wake.Release(); }
        }
    }

    public void CloseRun(AcquisitionRunId runId)
    {
        lock (_gate)
        {
            if (_runId != runId) { return; }
            Invalidate();
            _runId = null;
        }
    }

    public IDisposable Subscribe(IObserver<AScanSnapshot> observer)
    {
        ArgumentNullException.ThrowIfNull(observer);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var subscription = new Subscription(this, observer);
            _subscriptions.Add(subscription);
            if (_current is not null) { subscription.Offer(_current, _generation); }
            return subscription;
        }
    }

    /// <summary>Stops this publisher permanently. Cancellation abandons only the caller's wait.</summary>
    public Task StopAsync(CancellationToken cancellationToken = default) => DisposeAsync().AsTask().WaitAsync(cancellationToken);

    public ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (_disposal is null)
            {
                _disposed = true;
                Invalidate();
                _runId = null;
                var cancellations = _subscriptions.Select(subscription => subscription.Detach()).ToArray();
                _subscriptions.Clear();
                var cancellation = _shutdown.CancelAsync();
                _disposal = DisposeCoreAsync(Task.WhenAll(cancellations.Append(cancellation)));
            }
            return new ValueTask(_disposal);
        }
    }

    private async Task DisposeCoreAsync(Task cancellation)
    {
        try { await Task.WhenAll(cancellation, _worker).ConfigureAwait(false); }
        catch (Exception error)
        {
            lock (_gate) { _lastError = Describe("visual.cleanup", error); }
        }
        finally { _shutdown.Dispose(); _wake.Dispose(); }
    }

    private async Task PublishLoopAsync()
    {
        try
        {
            while (true)
            {
                await _wake.WaitAsync(_shutdown.Token).ConfigureAwait(false);
                while (true)
                {
                    TimeSpan remaining;
                    lock (_gate)
                    {
                        if (_disposed || _pending is null) { break; }
                        remaining = _lastPublication is { } previous
                            ? MinimumPublicationInterval - _timeProvider.GetElapsedTime(previous, _timeProvider.GetTimestamp())
                            : TimeSpan.Zero;
                        if (remaining <= TimeSpan.Zero)
                        {
                            _current = _pending;
                            _pending = null;
                            _lastPublication = _timeProvider.GetTimestamp();
                            _published++;
                            foreach (var subscription in _subscriptions) { subscription.Offer(_current, _generation); }
                            break;
                        }
                    }
                    // Recheck the monotonic time after every wake, including early timer callbacks.
                    await DelayAsync(remaining).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { }
        catch (Exception error)
        {
            lock (_gate)
            {
                _lastError = Describe("visual.publication", error);
                _failed = true;
                Invalidate();
                _runId = null;
            }
        }
    }

    private void Invalidate()
    {
        _generation++;
        if (_pending is not null) { _dropped++; }
        _pending = null;
        _current = null;
        foreach (var subscription in _subscriptions) { subscription.ClearPending(); }
    }

    private static UtSourceError Describe(string code, Exception error) =>
        new(code, string.IsNullOrWhiteSpace(error.Message) ? error.GetType().Name : error.Message);

    // Timer disposal executes in this async operation, outside locks and cancellation callbacks.
    // Waiting on the signal instead of a cancellable Task.Delay also lets us observe disposal failures.
    private Task DelayAsync(TimeSpan remaining) => DelayAsync(remaining, _shutdown.Token);
    private async Task DelayAsync(TimeSpan remaining, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var elapsed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var timer = _timeProvider.CreateTimer(_ => elapsed.TrySetResult(), null, remaining, Timeout.InfiniteTimeSpan);
        await elapsed.Task.WaitAsync(token).ConfigureAwait(false);
    }

    // All subscription admission/state uses the publisher lock. User code never holds that lock.
    private sealed class Subscription(AScanVisualDelivery owner, IObserver<AScanSnapshot> observer) : IDisposable
    {
        private AScanSnapshot? _pending;
        private long _generation;
        private long? _lastAdmission;
        private bool _scheduled, _detached;
        private readonly CancellationTokenSource _cancellation = new();
        private Task _pump = Task.CompletedTask;
        private Task? _cancellationCompletion;

        internal void Offer(AScanSnapshot snapshot, long generation)
        {
            if (_detached) { return; }
            if (_pending is not null) { owner._observerCoalesced++; }
            _pending = snapshot;
            _generation = generation;
            if (_scheduled) { return; }
            _scheduled = true;
            _pump = Task.Run(Pump);
        }

        internal void ClearPending() => _pending = null;
        internal Task Detach()
        {
            if (_detached) { return _cancellationCompletion!; }
            _detached = true;
            _pending = null;
            _cancellationCompletion = _cancellation.CancelAsync();
            var pump = _pump;
            _ = Task.Run(async () =>
            {
                try { await Task.WhenAll(_cancellationCompletion, pump).ConfigureAwait(false); }
                catch (Exception error)
                {
                    lock (owner._gate) { owner._lastError = Describe("visual.subscription_cleanup", error); }
                }
                finally { _cancellation.Dispose(); }
            });
            return _cancellationCompletion;
        }

        private async Task Pump()
        {
            CancellationToken token;
            lock (owner._gate)
            {
                if (_detached) { _scheduled = false; return; }
                token = _cancellation.Token;
            }
            try
            {
                while (true)
                {
                    AScanSnapshot? next = null;
                    TimeSpan remaining;
                    lock (owner._gate)
                    {
                        if (_detached || _pending is null || _generation != owner._generation)
                        {
                            _pending = null;
                            _scheduled = false;
                            return;
                        }
                        remaining = _lastAdmission is { } previous
                            ? MinimumPublicationInterval - owner._timeProvider.GetElapsedTime(previous, owner._timeProvider.GetTimestamp())
                            : TimeSpan.Zero;
                        if (remaining <= TimeSpan.Zero)
                        {
                            next = _pending;
                            _pending = null;
                            _lastAdmission = owner._timeProvider.GetTimestamp();
                        }
                    }
                    if (next is null)
                    {
                        await owner.DelayAsync(remaining, token).ConfigureAwait(false);
                        continue;
                    }
                    observer.OnNext(next);
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch (Exception error)
            {
                lock (owner._gate)
                {
                    if (token.IsCancellationRequested)
                    {
                        owner._lastError = Describe("visual.timer_cleanup", error);
                    }
                    else if (!owner._disposed)
                    {
                        owner._observerErrors++;
                        owner._lastError = Describe("visual.observer", error);
                    }
                    Detach();
                    owner._subscriptions.Remove(this);
                    if (owner._subscriptions.Count == 0) { owner.Invalidate(); }
                }
            }
        }

        public void Dispose()
        {
            lock (owner._gate)
            {
                Detach();
                owner._subscriptions.Remove(this);
                if (owner._subscriptions.Count == 0) { owner.Invalidate(); }
            }
        }
    }
}
