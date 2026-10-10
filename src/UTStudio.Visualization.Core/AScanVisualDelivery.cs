using UTStudio.Contracts.Inspection;
using UTStudio.Domain.Acquisition;

namespace UTStudio.Visualization.Core;

/// <summary>Independent-snapshot mailbox of capacity one, with publication limited by a monotonic clock.</summary>
/// <remarks>
/// Receives only confirmed trace identities. Its bounded latest-only reader acquires the authoritative
/// trace lease, projects an independent snapshot while that lease is valid, and then releases the lease.
/// Subscribers have one executing callback and one latest pending snapshot. Slow user code cannot
/// block identity notification, CloseRun or peers. External subscription disposal waits for its current callback;
/// self-cancellation is reentrant. No new callback can begin after subscription disposal returns.
/// </remarks>
public sealed class AScanVisualDelivery : ICommittedTraceSink, IObservable<AScanSnapshot>, IAsyncDisposable
{
    public static readonly TimeSpan MinimumPublicationInterval = TimeSpan.FromTicks(333_334);
    private readonly object _gate = new();
    private readonly IAScanProjector _projector;
    private readonly IUtTraceProvider _traceProvider;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _wake = new(0, 1);
    private readonly SemaphoreSlim _requestWake = new(0, 1);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly List<Subscription> _subscriptions = [];
    private readonly DeliveryStatusStore _status = new();
    private List<DiagnosticWaiter>? _diagnosticWaiters;
    // Deterministic race seam: selection is not callback entry. Never runs under a lock.
    internal Action? BeforeCallbackAttempt { get; set; }
    internal Action? AfterCallbackAttempt { get; set; }
    internal Action? BeforeSubscriptionWait { get; set; }
    private readonly Task _worker;
    private readonly Task _readerWorker;
    private TraceRequest? _pendingRequest;
    private CancellationTokenSource? _activeRead;
    private Task? _disposal;
    private Task? _drain;
    private Subscription[]? _stoppedSubscriptions;
    private bool _disposed;
    private bool _failed;
    private AcquisitionRunId? _runId;
    private long _generation;
    private ulong _version;
    private long? _lastPublication;
    private AScanSnapshot? _pending;
    private AScanSnapshot? _current;
    private long _received, _published, _replaced, _dropped, _observerCoalesced, _observerErrors;
    private UtSourceError? _lastError;
    private VisualDiagnosticWorkerState _diagnosticWorkerState = VisualDiagnosticWorkerState.Starting;
    private Task? _diagnosticWorkerWait;
    private int _projectionsInFlight;
    private bool _diagnosticFullCleanupRequested;
    private TaskCompletionSource? _diagnosticProjectionDrain;
    private TaskCompletionSource? _runReadDrain;

    public AScanVisualDelivery(IUtTraceProvider traceProvider, IAScanProjector? projector = null,
        TimeProvider? timeProvider = null)
    {
        _traceProvider = traceProvider ?? throw new ArgumentNullException(nameof(traceProvider));
        _projector = projector ?? new AScanProjector();
        _timeProvider = timeProvider ?? TimeProvider.System;
        _worker = Task.Run(PublishLoopAsync);
        _readerWorker = Task.Run(ReadLoopAsync);
    }


    public AScanSnapshot? Current { get { lock (_gate) { return _current; } } }
    public IObservable<AScanDeliveryStatus> StatusChanges => _status;
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

    /// <summary>
    /// Diagnostic-only barrier for tests and benchmarks. It completes after the publisher has registered the
    /// requested asynchronous wait, the expected mailbox state is visible, and every subscriber pump/callback
    /// admitted before that observation has finished. The caller must serialize this diagnostic operation with
    /// identity notification. Cancellation abandons only this waiter.
    /// </summary>
    internal async Task WaitForDiagnosticStateAsync(VisualDiagnosticExpectation expectation, TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeout, TimeSpan.Zero);
        using var timeoutSource = new CancellationTokenSource(timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutSource.Token);
        try { await WaitForDiagnosticStateCoreAsync(expectation, linked.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (timeoutSource.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        { throw new TimeoutException($"Visual diagnostic state {expectation} was not reached within {timeout}."); }
    }

    /// <summary>Diagnostic cleanup that additionally observes detached subscriber pump tasks.</summary>
    internal async Task DisposeForDiagnosticsAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeout, TimeSpan.Zero);
        Task projectionDrain;
        Task disposal;
        lock (_gate)
        {
            _diagnosticFullCleanupRequested = true;
            disposal = DisposeAsync().AsTask();
            projectionDrain = _projectionsInFlight == 0
                ? Task.CompletedTask
                : (_diagnosticProjectionDrain ??= new(TaskCreationOptions.RunContinuationsAsynchronously)).Task;
        }
        await Task.WhenAll(disposal, projectionDrain)
            .WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
    }

    private Task WaitForDiagnosticStateCoreAsync(VisualDiagnosticExpectation expectation, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        DiagnosticWaiter waiter;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            waiter = new(expectation, _generation);
            (_diagnosticWaiters ??= []).Add(waiter);
            foreach (var subscription in _subscriptions) { subscription.ArmDiagnosticCompletion(); }
            TryCompleteDiagnosticWaitersUnderLock();
        }
        waiter.RegisterCancellation(this, token);
        return waiter.Completion.Task;
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
        }
    }

    public void NotifyCommitted(TraceIdentity identity, InspectionRevision revision)
    {
        if (!identity.IsValid) { throw new ArgumentException("A valid trace identity is required.", nameof(identity)); }
        if (revision == InspectionRevision.Initial) { throw new ArgumentException("A committed revision is required.", nameof(revision)); }
        CancellationTokenSource? superseded;
        lock (_gate)
        {
            _received++;
            if (_disposed || _failed || _runId != identity.Stream.AcquisitionRunId || _subscriptions.Count == 0)
            {
                _dropped++;
                return;
            }
            if (_pendingRequest is not null) { _replaced++; }
            _pendingRequest = new(identity, revision, _generation, ++_version);
            superseded = _activeRead;
            if (_requestWake.CurrentCount == 0) { _requestWake.Release(); }
        }
        if (superseded is not null) { _ = superseded.CancelAsync(); }
    }

    private async Task ReadLoopAsync()
    {
        try
        {
            while (true)
            {
                await _requestWake.WaitAsync(_shutdown.Token).ConfigureAwait(false);
                TraceRequest? request;
                CancellationTokenSource readCancellation;
                lock (_gate)
                {
                    request = _pendingRequest;
                    _pendingRequest = null;
                    if (request is null || _disposed) { continue; }
                    readCancellation = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
                    _activeRead = readCancellation;
                    _projectionsInFlight++;
                }
                try { await ReadAndProjectAsync(request.Value, readCancellation.Token).ConfigureAwait(false); }
                finally
                {
                    lock (_gate)
                    {
                        if (ReferenceEquals(_activeRead, readCancellation)) { _activeRead = null; }
                        _projectionsInFlight--;
                        if (_projectionsInFlight == 0)
                        {
                            _diagnosticProjectionDrain?.TrySetResult();
                            _runReadDrain?.TrySetResult();
                            _runReadDrain = null;
                        }
                        TryCompleteDiagnosticWaitersUnderLock();
                    }
                    readCancellation.Dispose();
                }
            }
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { }
        catch (Exception error)
        {
            lock (_gate) { _lastError = Describe("visual.reader", error); _failed = true; Invalidate(); }
        }
    }

    private async Task ReadAndProjectAsync(TraceRequest request, CancellationToken token)
    {
        InspectionTraceReadResult result;
        try { result = await _traceProvider.ReadAsync(request.Identity, token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
        catch (Exception error) { RecordReadFailure(request, "visual.provider", error); return; }
        if (result.Status != InspectionReadStatus.Available || result.Lease is null)
        {
            if (result.Lease is not null) { result.Lease.Dispose(); }
            if (result.Status is not InspectionReadStatus.Cancelled)
            { RecordReadFailure(request, $"visual.provider.{result.Status.ToString().ToLowerInvariant()}", null); }
            return;
        }
        AScanSnapshot snapshot;
        using (result.Lease)
        {
            if (token.IsCancellationRequested || !IsCurrent(request)) { return; }
            TraceDescriptor descriptor = result.Lease.Descriptor;
            if (descriptor.Identity != request.Identity || result.Revision.Value < request.Revision.Value)
            { RecordReadFailure(request, "visual.provider.identity", null); return; }
            var samples = new short[descriptor.SampleCount];
            result.Lease.Samples.CopyTo(samples);
            if (token.IsCancellationRequested || !IsCurrent(request)) { return; }
            var configuration = new ConventionalAcquisitionConfiguration(descriptor.Identity.Stream.PhysicalChannelId,
                descriptor.SampleCount, descriptor.SampleRateHz, descriptor.FirstSampleOffsetSeconds,
                descriptor.SignalMode);
            DateTimeOffset started = descriptor.TimestampUtc is { } timestamp
                ? timestamp - descriptor.ElapsedSinceRunStart : DateTimeOffset.UnixEpoch;
            var metadata = new ConventionalUtFrameMetadata(descriptor.Identity.Stream.SourceId,
                descriptor.Identity.Stream.AcquisitionRunId, configuration, started);
            try
            {
                snapshot = _projector.Project(metadata, descriptor.Sequence, descriptor.ElapsedSinceRunStart,
                samples, request.Version);
            }
            catch (Exception error) { RecordReadFailure(request, "visual.projection", error); return; }
            snapshot.SetAuthority(request.Identity, request.Revision);
        }
        lock (_gate)
        {
            if (_disposed || _generation != request.Generation || _version != request.Version ||
                _runId != request.Identity.Stream.AcquisitionRunId ||
                _subscriptions.Count == 0)
            {
                _dropped++;
                return;
            }
            if (_pending is not null) { _replaced++; }
            _pending = snapshot;
            if (_wake.CurrentCount == 0) { _wake.Release(); }
        }
    }

    private bool IsCurrent(TraceRequest request)
    {
        lock (_gate)
        {
            return !_disposed && _generation == request.Generation &&
            _version == request.Version && _runId == request.Identity.Stream.AcquisitionRunId;
        }
    }

    private void RecordReadFailure(TraceRequest request, string code, Exception? error)
    {
        lock (_gate)
        {
            if (_generation != request.Generation) { _dropped++; return; }
            _dropped++;
            _lastError = new(code, error is null || string.IsNullOrWhiteSpace(error.Message)
                ? error?.GetType().Name ?? code : error.Message);
            _status.Publish(new AScanDeliveryStatus(1, _lastError));
        }
    }

    public void CloseRun(AcquisitionRunId runId)
    {
        CancellationTokenSource? activeRead;
        lock (_gate)
        {
            if (_runId != runId) { return; }
            activeRead = _activeRead;
            Invalidate();
            _runId = null;
            if (_projectionsInFlight != 0)
            { _runReadDrain ??= new(TaskCreationOptions.RunContinuationsAsynchronously); }
        }
        if (activeRead is not null) { _ = activeRead.CancelAsync(); }
    }

    public async ValueTask WaitForCloseAsync(AcquisitionRunId runId)
    {
        Task readDrain;
        Subscription[] subscriptions;
        lock (_gate)
        {
            readDrain = _projectionsInFlight == 0 ? Task.CompletedTask :
                (_runReadDrain ??= new(TaskCreationOptions.RunContinuationsAsynchronously)).Task;
            subscriptions = [.. _subscriptions];
        }
        await readDrain.ConfigureAwait(false);
        await Task.WhenAll(subscriptions.Select(subscription => Task.Run(subscription.WaitForCallback)))
            .ConfigureAwait(false);
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
    public Task StopAsync(CancellationToken cancellationToken = default) => WaitForDrainAsync(cancellationToken);

    public ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            _disposal ??= DisposeCoreAsync();
            return new ValueTask(_disposal);
        }
    }

    internal void Stop()
    {
        lock (_gate)
        {
            if (_disposed) { return; }
            _disposed = true;
            FailDiagnosticWaitersUnderLock(new ObjectDisposedException(nameof(AScanVisualDelivery)));
            Invalidate();
            _runId = null;
            _stoppedSubscriptions = [.. _subscriptions];
            _subscriptions.Clear();
            _drain = Task.Run(() => DrainCoreAsync(_stoppedSubscriptions));
        }
    }

    internal Task WaitForDrainAsync(CancellationToken cancellationToken = default)
    {
        Stop();
        Task drain;
        lock (_gate) { drain = _drain!; }
        return cancellationToken.CanBeCanceled ? drain.WaitAsync(cancellationToken) : drain;
    }

    private async Task DrainCoreAsync(Subscription[] subscriptions)
    {
        List<Exception> failures = [];
        try { await _shutdown.CancelAsync().ConfigureAwait(false); }
        catch (Exception error) { failures.Add(error); }
        foreach (Task callback in subscriptions.Select(subscription => subscription.Detach()))
        {
            try { await callback.ConfigureAwait(false); }
            catch (Exception error) { failures.Add(error); }
        }
        try { await _readerWorker.ConfigureAwait(false); }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { }
        catch (Exception error) { failures.Add(error); }
        try { await _worker.ConfigureAwait(false); }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { }
        catch (Exception error) { failures.Add(error); }
        foreach (var subscription in subscriptions)
        {
            try { subscription.WaitForCallback(); }
            catch (Exception error) { failures.Add(error); }
        }
        if (_diagnosticFullCleanupRequested)
        {
            foreach (Task cleanup in subscriptions.Select(subscription => subscription.DiagnosticCleanup))
            {
                try { await cleanup.ConfigureAwait(false); }
                catch (Exception error) { failures.Add(error); }
            }
        }
        if (failures.Count != 0)
        {
            lock (_gate) { _lastError = Describe("visual.cleanup", failures[0]); }
            throw new AggregateException("A-Scan visual drain failed.", failures);
        }
    }

    private async Task DisposeCoreAsync()
    {
        Stop();
        await WaitForDrainAsync().ConfigureAwait(false);
        _status.Dispose();
        _shutdown.Dispose();
        _wake.Dispose();
        _requestWake.Dispose();
    }

    private async Task PublishLoopAsync()
    {
        try
        {
            while (true)
            {
                Task wake = _wake.WaitAsync(_shutdown.Token);
                lock (_gate)
                {
                    _diagnosticWorkerWait = wake;
                    _diagnosticWorkerState = wake.IsCompleted
                        ? VisualDiagnosticWorkerState.Processing
                        : VisualDiagnosticWorkerState.WaitingForSignal;
                    if (_diagnosticWaiters is not null) { TryCompleteDiagnosticWaitersUnderLock(); }
                }
                await wake.ConfigureAwait(false);
                lock (_gate)
                {
                    _diagnosticWorkerWait = null;
                    _diagnosticWorkerState = VisualDiagnosticWorkerState.Processing;
                }
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
                    Task delay = DelayAsync(remaining);
                    lock (_gate)
                    {
                        _diagnosticWorkerWait = delay;
                        _diagnosticWorkerState = delay.IsCompleted
                            ? VisualDiagnosticWorkerState.Processing
                            : VisualDiagnosticWorkerState.WaitingForTimer;
                        if (_diagnosticWaiters is not null) { TryCompleteDiagnosticWaitersUnderLock(); }
                    }
                    await delay.ConfigureAwait(false);
                    lock (_gate)
                    {
                        _diagnosticWorkerWait = null;
                        _diagnosticWorkerState = VisualDiagnosticWorkerState.Processing;
                    }
                }
            }
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { }
        catch (Exception error)
        {
            lock (_gate)
            {
                _diagnosticWorkerWait = null;
                _lastError = Describe("visual.publication", error);
                _status.Publish(new AScanDeliveryStatus(1, _lastError));
                _failed = true;
                Invalidate();
                _runId = null;
                FailDiagnosticWaitersUnderLock(error);
            }
        }
        finally
        {
            lock (_gate)
            {
                _diagnosticWorkerState = VisualDiagnosticWorkerState.Stopped;
                if (_diagnosticWaiters is not null && !_disposed)
                { FailDiagnosticWaitersUnderLock(new InvalidOperationException("Visual publisher stopped.")); }
            }
        }
    }

    private void TryCompleteDiagnosticWaitersUnderLock()
    {
        if (_diagnosticWaiters is null) { return; }
        bool subscriptionsQuiescent = _subscriptions.All(subscription => subscription.IsQuiescent);
        for (int index = _diagnosticWaiters.Count - 1; index >= 0; index--)
        {
            var waiter = _diagnosticWaiters[index];
            if (_diagnosticWorkerState == waiter.Expectation.WorkerState &&
                _diagnosticWorkerWait is { IsCompleted: false } && _generation == waiter.Generation &&
                _projectionsInFlight == 0 &&
                (_pending is not null) == waiter.Expectation.HasPendingSnapshot && subscriptionsQuiescent)
            {
                _diagnosticWaiters.RemoveAt(index);
                waiter.Completion.TrySetResult();
            }
        }
        if (_diagnosticWaiters.Count == 0) { _diagnosticWaiters = null; }
    }

    private void CancelDiagnosticWaiter(DiagnosticWaiter waiter, CancellationToken token)
    {
        lock (_gate)
        {
            if (_diagnosticWaiters?.Remove(waiter) == true)
            {
                if (_diagnosticWaiters.Count == 0) { _diagnosticWaiters = null; }
                waiter.Completion.TrySetCanceled(token);
            }
        }
    }

    private void FailDiagnosticWaitersUnderLock(Exception error)
    {
        if (_diagnosticWaiters is null) { return; }
        foreach (var waiter in _diagnosticWaiters)
        {
            waiter.Completion.TrySetException(error);
        }
        _diagnosticWaiters = null;
    }

    private void Invalidate()
    {
        if (_diagnosticWaiters is not null)
        { FailDiagnosticWaitersUnderLock(new InvalidOperationException("Visual diagnostic generation was invalidated.")); }
        _generation++;
        if (_pending is not null) { _dropped++; }
        if (_pendingRequest is not null) { _dropped++; }
        _pending = null;
        _pendingRequest = null;
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
        private readonly object _callbackGate = new();
        private AScanSnapshot? _pending;
        private long _generation;
        private long? _lastAdmission;
        private bool _scheduled, _detached;
        private readonly CancellationTokenSource _cancellation = new();
        private Task _pump = Task.CompletedTask;
        private Task? _cancellationCompletion;
        private Task? _detachCompletion;
        private bool _diagnosticCompletionArmed;
        internal bool IsQuiescent => !_scheduled && _pending is null && _pump.IsCompleted;
        internal void ArmDiagnosticCompletion()
        {
            if (_pump.IsCompleted || _diagnosticCompletionArmed) { return; }
            _diagnosticCompletionArmed = true;
            var armedPump = _pump;
            _ = armedPump.ContinueWith(_ =>
            {
                lock (owner._gate)
                {
                    _diagnosticCompletionArmed = false;
                    if (owner._diagnosticWaiters is not null)
                    {
                        if (!ReferenceEquals(_pump, armedPump)) { ArmDiagnosticCompletion(); }
                        owner.TryCompleteDiagnosticWaitersUnderLock();
                    }
                }
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }

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
            _detachCompletion = Task.Run(async () =>
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
                    long selectedGeneration;
                    TimeSpan remaining;
                    lock (owner._gate)
                    {
                        if (_detached || _pending is null || _generation != owner._generation)
                        {
                            _pending = null;
                            _scheduled = false;
                            if (owner._diagnosticWaiters is not null) { ArmDiagnosticCompletion(); }
                            return;
                        }
                        remaining = _lastAdmission is { } previous
                            ? MinimumPublicationInterval - owner._timeProvider.GetElapsedTime(previous, owner._timeProvider.GetTimestamp())
                            : TimeSpan.Zero;
                        selectedGeneration = _generation;
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
                    owner.BeforeCallbackAttempt?.Invoke();
                    try
                    {
                        lock (_callbackGate)
                        {
                            lock (owner._gate)
                            {
                                if (_detached || owner._disposed || selectedGeneration != owner._generation) { continue; }
                            }
                            // Callback entry is this synchronous call while holding the per-subscriber gate.
                            // Dispose cannot return between the final check and entry (or during this call).
                            observer.OnNext(next);
                        }
                    }
                    finally { owner.AfterCallbackAttempt?.Invoke(); }
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
            owner.BeforeSubscriptionWait?.Invoke();
            WaitForCallback();
        }

        internal void WaitForCallback() { lock (_callbackGate) { } }
        internal Task DiagnosticCleanup => _detachCompletion ?? Task.CompletedTask;
    }

    private sealed class DiagnosticWaiter(VisualDiagnosticExpectation expectation, long generation)
    {
        internal VisualDiagnosticExpectation Expectation { get; } = expectation;
        internal long Generation { get; } = generation;
        internal TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal void RegisterCancellation(AScanVisualDelivery owner, CancellationToken token)
        {
            if (!token.CanBeCanceled) { return; }
            token.Register(() => owner.CancelDiagnosticWaiter(this, token));
        }
    }
}

internal readonly record struct TraceRequest(TraceIdentity Identity, InspectionRevision Revision,
    long Generation, ulong Version);


internal enum VisualDiagnosticWorkerState { Starting, Processing, WaitingForSignal, WaitingForTimer, Stopped }

internal readonly record struct VisualDiagnosticExpectation(
    VisualDiagnosticWorkerState WorkerState, bool HasPendingSnapshot);
