using UTStudio.Contracts.Acquisition;
using UTStudio.Domain.Acquisition;

namespace UTStudio.Application;

/// <summary>Coordinates one active execution and is the exclusive reader of its frames.</summary>
/// <remarks>
/// The injected source is borrowed: this session stops/disconnects it, but its external owner disposes it.
/// It must have no active execution and must be used exclusively through this session during the loan.
/// Stop preserves a healthy connection. Failed startup/operation and disposal disconnect after safe cleanup.
/// Public types are local to Application for this stage; future Presentation must use a separately approved
/// Contracts port, never a reference to this implementation.
/// </remarks>
public sealed class ApplicationSession : IAsyncDisposable, IObservable<SessionSnapshot>
{
    private readonly object _gate = new();
    private readonly IUtFrameSource _source;
    private readonly SessionSnapshotPublisher _publisher;
    private SessionSnapshot _snapshot;
    private Operation? _active;
    private Operation? _lastOperation;
    private Task _lastStop = Task.CompletedTask;
    private Task? _disposeTask;
    private bool _disposing;

    public ApplicationSession(IUtFrameSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!source.SourceId.IsValid) { throw new ArgumentException("The source identifier is invalid.", nameof(source)); }
        _source = source;
        _snapshot = new SessionSnapshot(0, SessionPhase.Idle, source.SourceId, null, 0, 0, null, null, null, [], 0);
        _publisher = new SessionSnapshotPublisher(_snapshot);
    }

    public SessionSnapshot Snapshot { get { lock (_gate) { return _snapshot; } } }

    /// <summary>
    /// Latest-state delivery, serialized per observer on the thread pool; intermediate versions may coalesce.
    /// Observers never execute on acquisition paths. Dispose detaches without waiting for an admitted callback.
    /// </summary>
    public IDisposable Subscribe(IObserver<SessionSnapshot> observer) => _publisher.Subscribe(observer);

    /// <summary>Rejects overlapping starts. Cancellation applies only to pending preparation, not an established run.</summary>
    public Task StartAsync(ConventionalAcquisitionConfiguration configuration, AcquisitionRunId runId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        if (!runId.IsValid) { throw new ArgumentException("A valid run identifier is required.", nameof(runId)); }
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposing, this);
            if (_active is not null) { throw new InvalidOperationException("An execution or its cleanup is already active."); }
            var operation = new Operation(runId, configuration, cancellationToken);
            _active = operation;
            _lastOperation = operation;
            Publish(operation, SessionPhase.Connecting);
            // Reservation is installed before work starts. PreparationFinished provides asynchronous lifecycle
            // exclusion: cleanup cancels preparation first, then awaits it without holding _gate.
            _ = Task.Run(() => StartCoreAsync(operation));
            return operation.Started.Task;
        }
    }

    /// <summary>Idempotent cleanup. The caller token only cancels its wait, not the cleanup or the sole reader.</summary>
    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        Task completion;
        lock (_gate)
        {
            completion = _active is { } operation ? RequestCleanup(operation, false) : _lastStop;
        }

        return completion.WaitAsync(cancellationToken);
    }

    public ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (_disposeTask is null)
            {
                _disposing = true;
                var operation = _active ?? _lastOperation;
                var cleanup = _active is null ? _lastStop : RequestCleanup(_active, true);
                _disposeTask = Task.Run(() => DisposeCoreAsync(operation, cleanup));
            }

            return new ValueTask(_disposeTask);
        }
    }

    private async Task StartCoreAsync(Operation operation)
    {
        Exception? failure = null;
        var token = operation.StartCancellation.Token;
        try
        {
            await _source.ConnectAsync(token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            SetPreparationPhase(operation, SessionPhase.Configuring);
            await _source.ConfigureAsync(operation.Configuration, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            SetPreparationPhase(operation, SessionPhase.Starting);
            var run = await _source.StartAsync(operation.RunId, token).ConfigureAwait(false);
            lock (_gate)
            {
                // Receiving a descriptor transfers its reader even if the token is already cancelled.
                operation.Run = run;
                operation.Consumer = Task.Run(() => ConsumeAsync(operation, run));
                operation.Supervisor = Task.Run(() => SuperviseProducerAsync(operation, run));
                if (run.Metadata.SourceId != _source.SourceId || run.Metadata.RunId != operation.RunId ||
                    !SameConfiguration(run.Metadata.Configuration, operation.Configuration))
                {
                    throw new InvalidOperationException("The execution descriptor does not match the request.");
                }
                token.ThrowIfCancellationRequested();
                if (operation.CleanupRequested) { throw new OperationCanceledException(token); }
                Publish(operation, SessionPhase.Running);
                operation.Started.TrySetResult();
            }
        }
        catch (Exception error)
        {
            failure = error;
            lock (_gate)
            {
                if (!(error is OperationCanceledException && token.IsCancellationRequested))
                {
                    RecordError(operation, error, "session.start");
                }

                operation.DisconnectRequested = true;
            }
        }
        finally { operation.PreparationFinished.TrySetResult(); }

        if (failure is not null)
        {
            Task cleanup;
            lock (_gate) { cleanup = RequestCleanup(operation, true); }
            try { await cleanup.ConfigureAwait(false); }
            catch { } // Error retained in snapshots and cleanup task; Start preserves the original cause.
            if (failure is OperationCanceledException && token.IsCancellationRequested)
            {
                operation.Started.TrySetCanceled(token);
            }
            else { operation.Started.TrySetException(failure); _ = operation.Started.Task.Exception; }
        }
    }

    private void SetPreparationPhase(Operation operation, SessionPhase phase)
    {
        lock (_gate)
        {
            operation.StartCancellation.Token.ThrowIfCancellationRequested();
            if (!operation.CleanupRequested) { Publish(operation, phase); }
        }
    }

    private Task RequestCleanup(Operation operation, bool disconnect)
    {
        operation.DisconnectRequested |= disconnect;
        if (operation.CleanupRequested) { return operation.Cleanup.Task; }
        operation.CleanupRequested = true;
        Publish(operation, SessionPhase.Stopping);
        // Marks cancellation before waiting for lifecycle exclusion; callbacks execute outside _gate.
        operation.CancellationCallbacks = operation.StartCancellation.CancelAsync();
        _lastStop = operation.Cleanup.Task;
        _ = Task.Run(() => CleanupCoreAsync(operation));
        return operation.Cleanup.Task;
    }

    private async Task ConsumeAsync(Operation operation, UtAcquisitionRun run)
    {
        bool drainOnly = false;
        try
        {
            while (await run.Frames.WaitToReadAsync().ConfigureAwait(false))
            {
                while (run.Frames.TryRead(out var frame))
                {
                    try
                    {
                        lock (_gate) { operation.ReceivedFrames++; }
                        if (!drainOnly) { InspectFrame(operation, run, frame); }
                    }
                    catch (Exception error)
                    {
                        drainOnly = true;
                        lock (_gate) { RecordError(operation, error, "session.frame"); RequestCleanup(operation, true); }
                    }
                    finally
                    {
                        try
                        {
                            frame.Dispose();
                            lock (_gate) { operation.ReleasedFrames++; }
                        }
                        catch (Exception error)
                        {
                            drainOnly = true;
                            lock (_gate) { RecordError(operation, error, "session.release"); RequestCleanup(operation, true); }
                        }
                    }
                }
            }
        }
        catch (Exception error)
        {
            lock (_gate) { RecordError(operation, error, "session.reader"); RequestCleanup(operation, true); }
        }
        // Never await cleanup here: it waits for this sole reader.
    }

    /// <summary>Internal handoff boundary for the next stage. This stage retains metadata only.</summary>
    private void InspectFrame(Operation operation, UtAcquisitionRun run, ConventionalUtFrame frame)
    {
        var metadata = frame.Metadata;
        var expected = run.Metadata.Configuration;
        var actual = metadata.Configuration;
        if (metadata.SourceId != _source.SourceId || metadata.RunId != operation.RunId ||
            metadata.RunStartedAtUtc != run.Metadata.RunStartedAtUtc ||
            !SameConfiguration(actual, expected))
        {
            throw new InvalidOperationException("The frame metadata does not match its execution.");
        }

        lock (_gate)
        {
            bool sequenceValid = operation.LastSequence is { } previous
                ? previous != ulong.MaxValue && frame.Sequence == previous + 1
                : frame.Sequence == 0;
            if (!sequenceValid || frame.ElapsedSinceRunStart < operation.LastElapsed)
            {
                throw new InvalidOperationException("The frame sequence or monotonic timestamp is invalid.");
            }

            operation.LastSequence = frame.Sequence;
            operation.LastElapsed = frame.ElapsedSinceRunStart;
            operation.LastMetadata = metadata;
        }
    }

    private static bool SameConfiguration(ConventionalAcquisitionConfiguration actual,
        ConventionalAcquisitionConfiguration expected) =>
        actual.PhysicalChannelId == expected.PhysicalChannelId && actual.SignalMode == expected.SignalMode &&
        actual.SampleCount == expected.SampleCount && actual.SampleRateHz == expected.SampleRateHz &&
        actual.FirstSampleOffsetSeconds == expected.FirstSampleOffsetSeconds;

    private async Task SuperviseProducerAsync(Operation operation, UtAcquisitionRun run)
    {
        try { await run.ProducerCompletion.ConfigureAwait(false); }
        catch (Exception error)
        {
            lock (_gate) { RecordError(operation, error, "session.producer"); }
        }

        lock (_gate) { RequestCleanup(operation, operation.PrimaryException is not null); }
    }

    private async Task CleanupCoreAsync(Operation operation)
    {
        try
        {
            // The start worker never waits for cleanup before signaling preparation completion.
            await operation.PreparationFinished.Task.ConfigureAwait(false);
            await ObserveAsync(operation, () => operation.CancellationCallbacks!, "session.cancel").ConfigureAwait(false);
            await ObserveAsync(operation, () => _source.StopAsync(), "session.stop").ConfigureAwait(false);
            var run = operation.Run;
            if (run is not null)
            {
                await ObserveAsync(operation, () => run.ProducerCompletion, "session.producer").ConfigureAwait(false);
                await operation.Supervisor.ConfigureAwait(false);
                await ObserveAsync(operation, () => operation.Consumer, "session.reader").ConfigureAwait(false);
                lock (_gate) { Publish(operation, SessionPhase.AwaitingFramesReleased); }
                operation.ResourcesReleased = await ObserveAsync(operation, () => run.AllFramesReleased, "session.barrier").ConfigureAwait(false);
            }
            else
            {
                // A source that has not returned a run owns and has completed startup rollback.
                operation.ResourcesReleased = true;
            }

            bool disconnect;
            lock (_gate) { disconnect = operation.DisconnectRequested || _disposing || operation.PrimaryException is not null; }
            if (operation.ResourcesReleased && disconnect)
            {
                lock (_gate) { Publish(operation, SessionPhase.Disconnecting); }
                operation.DisconnectAttempted = true;
                operation.Disconnected = await ObserveAsync(operation, () => _source.DisconnectAsync(), "session.disconnect").ConfigureAwait(false);
            }

            lock (_gate)
            {
                bool safe = operation.ResourcesReleased && (!disconnect || operation.Disconnected);
                if (safe) { _active = null; }
                Publish(operation, operation.PrimaryException is null ? SessionPhase.Idle : SessionPhase.Faulted);
                operation.StartCancellation.Dispose();
                Complete(operation.Cleanup, operation.PrimaryException);
            }
        }
        catch (Exception error)
        {
            lock (_gate)
            {
                RecordError(operation, error, "session.cleanup");
                Publish(operation, SessionPhase.Faulted);
                Complete(operation.Cleanup, operation.PrimaryException);
            }
        }
    }

    private async Task DisposeCoreAsync(Operation? operation, Task cleanup)
    {
        Exception? failure = null;
        try { await cleanup.ConfigureAwait(false); }
        catch (Exception error) { failure = error; }
        if (operation is { ResourcesReleased: false })
        {
            throw failure ?? new InvalidOperationException("Frame release has not been confirmed.");
        }

        if (operation?.Disconnected != true)
        {
            if (operation is { DisconnectAttempted: true })
            {
                throw failure ?? new InvalidOperationException("Disconnection has not been confirmed.");
            }

            lock (_gate)
            {
                if (operation is not null)
                {
                    operation.DisconnectAttempted = true;
                    Publish(operation, SessionPhase.Disconnecting);
                }
                else { PublishStandalone(SessionPhase.Disconnecting, _snapshot.PrimaryError); }
            }

            try
            {
                await _source.DisconnectAsync().ConfigureAwait(false);
                if (operation is not null) { operation.Disconnected = true; }
            }
            catch (Exception error)
            {
                lock (_gate)
                {
                    if (operation is not null)
                    {
                        RecordError(operation, error, "session.disconnect");
                        Publish(operation, SessionPhase.Faulted);
                    }
                    else { PublishStandalone(SessionPhase.Faulted, Describe("session.disconnect", error)); }
                }

                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure ?? error).Throw();
            }
        }

        lock (_gate)
        {
            if (operation is not null) { Publish(operation, SessionPhase.Disposed, true); }
            else { PublishStandalone(SessionPhase.Disposed, _snapshot.PrimaryError, true); }
        }

        if (failure is not null) { System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw(); }
    }

    private async Task<bool> ObserveAsync(Operation operation, Func<Task> action, string code)
    {
        try { await action().ConfigureAwait(false); return true; }
        catch (Exception error)
        {
            lock (_gate) { RecordError(operation, error, code); }
            return false;
        }
    }

    private static UtSourceError Describe(string code, Exception error) =>
        new(code, string.IsNullOrWhiteSpace(error.Message) ? error.GetType().Name : error.Message);

    private static void RecordError(Operation operation, Exception error, string code)
    {
        if (ReferenceEquals(operation.PrimaryException, error) || operation.RecordedErrors.Contains(error)) { return; }
        if (operation.PrimaryException is null)
        {
            operation.PrimaryException = error;
            operation.PrimaryError = Describe(code, error);
        }
        else if (operation.CleanupErrors.Count < 8)
        {
            operation.RecordedErrors.Add(error);
            operation.CleanupErrors.Add(Describe(code, error));
        }
        else { operation.AdditionalErrorCount++; }
    }

    private static void Complete(TaskCompletionSource completion, Exception? error)
    {
        if (error is null) { completion.TrySetResult(); }
        else { completion.TrySetException(error); _ = completion.Task.Exception; }
    }

    private void Publish(Operation operation, SessionPhase phase, bool complete = false)
    {
        _snapshot = new SessionSnapshot(_snapshot.Version + 1, phase, _source.SourceId, operation.RunId,
            operation.ReceivedFrames, operation.ReleasedFrames, operation.LastSequence, operation.LastMetadata,
            operation.PrimaryError, operation.CleanupErrors, operation.AdditionalErrorCount);
        _publisher.Publish(_snapshot, complete);
    }

    private void PublishStandalone(SessionPhase phase, UtSourceError? error, bool complete = false)
    {
        _snapshot = new SessionSnapshot(_snapshot.Version + 1, phase, _source.SourceId, _snapshot.RunId,
            _snapshot.ReceivedFrames, _snapshot.ReleasedFrames, _snapshot.LastSequence, _snapshot.LastMetadata,
            error, _snapshot.CleanupErrors, _snapshot.AdditionalErrorCount);
        _publisher.Publish(_snapshot, complete);
    }

    private sealed class Operation(AcquisitionRunId runId, ConventionalAcquisitionConfiguration configuration, CancellationToken token)
    {
        internal AcquisitionRunId RunId { get; } = runId;
        internal ConventionalAcquisitionConfiguration Configuration { get; } = configuration;
        internal CancellationTokenSource StartCancellation { get; } = CancellationTokenSource.CreateLinkedTokenSource(token);
        internal TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource PreparationFinished { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Cleanup { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal UtAcquisitionRun? Run;
        internal Task Consumer = Task.CompletedTask;
        internal Task Supervisor = Task.CompletedTask;
        internal Task? CancellationCallbacks;
        internal bool CleanupRequested;
        internal bool DisconnectRequested;
        internal bool ResourcesReleased;
        internal bool Disconnected;
        internal bool DisconnectAttempted;
        internal Exception? PrimaryException;
        internal UtSourceError? PrimaryError;
        internal List<UtSourceError> CleanupErrors { get; } = [];
        internal HashSet<Exception> RecordedErrors { get; } = new(ReferenceEqualityComparer.Instance);
        internal long AdditionalErrorCount;
        internal long ReceivedFrames;
        internal long ReleasedFrames;
        internal ulong? LastSequence;
        internal TimeSpan LastElapsed;
        internal ConventionalUtFrameMetadata? LastMetadata;
    }
}
