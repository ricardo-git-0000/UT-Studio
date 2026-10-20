using UTStudio.Contracts.Acquisition;
using UTStudio.Contracts.Application;
using UTStudio.Contracts.Inspection;
using UTStudio.Contracts.Processing;
using UTStudio.Domain.Acquisition;
using UTStudio.Domain.Inspection;

namespace UTStudio.Application;

/// <summary>Coordinates one active execution and is the exclusive reader of its frames.</summary>
/// <remarks>
/// The injected source is borrowed: this session stops/disconnects it, but its external owner disposes it.
/// It must have no active execution and must be used exclusively through this session during the loan.
/// Stop preserves a healthy connection. Failed startup/operation and disposal disconnect after safe cleanup.
/// Presentation uses IApplicationSession from Contracts, never a reference to this implementation.
/// </remarks>
public sealed class ApplicationSession : IAsyncDisposable, IApplicationSession
{
    // Capacity counts one runner plus at most 63 queued operations. The current sole reader can own only one RF/gap
    // admission, so this route retains at most one pending frame. Any future concurrent Application consumer must
    // budget its captured buffers and closures explicitly; cancelling a waiter never releases an admitted slot.
    private const int CommitCoordinatorCapacity = 64;
    private readonly object _gate = new();
    private readonly IUtFrameSource _source;
    private readonly InspectionId _inspectionId;
    private readonly Func<AcquisitionRunId, IInspectionRunWriter> _inspectionWriterFactory;
    private readonly ICommittedTraceSink? _bScanSink;
    private readonly ICommittedTraceSink? _aScanSink;
    private readonly ICommittedGateProcessingIngress? _processingIngress;
    private readonly CompiledGatePlan? _processingPlan;
    private readonly SessionSnapshotPublisher _publisher;
    private SessionSnapshot _snapshot;
    private Operation? _active;
    private Operation? _lastOperation;
    private Task _lastStop = Task.CompletedTask;
    private Task? _disposeTask;
    private bool _disposing;

    public ApplicationSession(IUtFrameSource source, InspectionId inspectionId,
        Func<AcquisitionRunId, IInspectionRunWriter> inspectionWriterFactory,
        ICommittedTraceSink? bScanSink = null, ICommittedTraceSink? aScanSink = null,
        ICommittedGateProcessingIngress? processingIngress = null, CompiledGatePlan? processingPlan = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!source.SourceId.IsValid) { throw new ArgumentException("The source identifier is invalid.", nameof(source)); }
        _source = source;
        if (!inspectionId.IsValid)
        { throw new ArgumentException("The inspection identifier is invalid.", nameof(inspectionId)); }
        ArgumentNullException.ThrowIfNull(inspectionWriterFactory);
        _inspectionId = inspectionId;
        _inspectionWriterFactory = inspectionWriterFactory;
        _bScanSink = bScanSink;
        _aScanSink = aScanSink;
        if ((processingIngress is null) != (processingPlan is null))
        { throw new ArgumentException("Processing ingress and plan must be supplied together."); }
        _processingIngress = processingIngress;
        _processingPlan = processingPlan;
        _snapshot = new SessionSnapshot(0, SessionPhase.Idle, source.SourceId, null, 0, 0, null, null, null, [], 0, canStart: true);
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
            operation.InspectionWriter = _inspectionWriterFactory(operation.RunId) ??
                throw new InvalidOperationException("The inspection writer factory returned null.");
            var run = await _source.StartAsync(operation.RunId, token).ConfigureAwait(false);
            lock (_gate)
            {
                // Receiving a descriptor transfers its reader even if the token is already cancelled.
                operation.Run = run;
                if (run.Metadata.SourceId != _source.SourceId || run.Metadata.RunId != operation.RunId ||
                    !SameConfiguration(run.Metadata.Configuration, operation.Configuration))
                {
                    throw new InvalidOperationException("The execution descriptor does not match the request.");
                }
                if (!operation.CleanupRequested && _processingIngress is not null)
                {
                    var stream = new UtStreamIdentity(_inspectionId, operation.RunId, run.Metadata.SourceId,
                        run.Metadata.Configuration.PhysicalChannelId);
                    GateProcessingAdmissionStatus admission = _processingIngress.OpenRun(
                        new(_inspectionId, operation.RunId, [stream], _processingPlan!));
                    operation.ProcessingOpened = admission == GateProcessingAdmissionStatus.Accepted;
                    if (!operation.ProcessingOpened)
                    { AddBranchError(operation, new("session.processing.open", $"Processing admission was {admission}.")); }
                }
                if (!operation.CleanupRequested && _bScanSink is not null)
                {
                    operation.VisualOpened = true;
                    operation.VisualRunRequiresDrain = true;
                    try { _bScanSink.OpenRun(operation.RunId); }
                    catch (Exception error) { RecordBScanError(operation, error); CloseVisual(operation); }
                }
                if (!operation.CleanupRequested && _aScanSink is not null)
                {
                    operation.AScanOpened = true;
                    try { _aScanSink.OpenRun(operation.RunId); }
                    catch (Exception error) { RecordAScanError(operation, error); CloseAScan(operation); }
                }
                operation.Consumer = Task.Run(() => ConsumeAsync(operation, run));
                operation.Supervisor = Task.Run(() => SuperviseProducerAsync(operation, run));
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
                        if (!drainOnly)
                        {
                            FrameInspection inspection = await InspectFrameAsync(operation, run, frame)
                                .ConfigureAwait(false);
                            if (inspection.IsValid)
                            {
                                InspectionAppendResult commit = await CommitInspectionAsync(operation, frame,
                                    inspection.Gap).ConfigureAwait(false);
                                if (commit.IsCommitted)
                                {
                                    DeliverCommittedProcessing(operation, frame, commit.Revision);
                                    DeliverCommittedAScan(operation, frame, commit.Revision);
                                    DeliverCommittedBScan(operation, frame, commit.Revision);
                                }
                            }
                        }
                    }
                    catch (Exception error)
                    {
                        drainOnly = true;
                        lock (_gate)
                        {
                            RecordError(operation, error, "session.frame"); RequestCleanup(operation, true);
                        }
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

    /// <summary>Only immutable metadata is retained; sample ownership remains with this reader.</summary>
    private async ValueTask<FrameInspection> InspectFrameAsync(Operation operation, UtAcquisitionRun run,
        ConventionalUtFrame frame)
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

        PendingGap? confirmedGap;
        lock (_gate)
        {
            confirmedGap = null;
            if (operation.LastObservedSequence is not { } previousSequence)
            {
                if (frame.Sequence != 0)
                {
                    operation.OutOfOrderFramesRejected++;
                    Publish(operation, _snapshot.Phase);
                    return default;
                }
            }
            else if (frame.Sequence == previousSequence)
            {
                operation.DuplicateFramesRejected++;
                Publish(operation, _snapshot.Phase);
                return default;
            }
            else if (frame.Sequence < previousSequence)
            {
                operation.OutOfOrderFramesRejected++;
                Publish(operation, _snapshot.Phase);
                return default;
            }
            else
            {
                ulong missing = frame.Sequence - previousSequence - 1;
                if (missing != 0)
                {
                    _ = checked(operation.AcquisitionMissingFrames + missing);
                    confirmedGap = new(previousSequence + 1, frame.Sequence - 1,
                        operation.LastSequence ?? previousSequence, frame.Sequence);
                }
            }

        }

        bool rejected;
        lock (_gate)
        {
            rejected = false;
            if (operation.LastObservedAcquisitionIndex is not { } previousIndex)
            {
                if (frame.AcquisitionIndex != 0)
                {
                    operation.InvalidAcquisitionIndexFramesRejected++;
                    Publish(operation, _snapshot.Phase);
                    rejected = true;
                }
            }
            else if (frame.AcquisitionIndex <= previousIndex)
            {
                operation.InvalidAcquisitionIndexFramesRejected++;
                Publish(operation, _snapshot.Phase);
                rejected = true;
            }

            if (!rejected && frame.ElapsedSinceRunStart < operation.LastElapsed)
            {
                operation.TimestampRegressionsRejected++;
                Publish(operation, _snapshot.Phase);
                rejected = true;
            }
        }
        if (!rejected) { return new(true, confirmedGap); }
        if (confirmedGap is { } rejectedGap)
        { await CommitRejectedFrameGapAsync(operation, frame, rejectedGap).ConfigureAwait(false); }
        else
        {
            lock (_gate) { operation.LastObservedSequence = frame.Sequence; }
        }
        return default;
    }

    private async ValueTask CommitRejectedFrameGapAsync(Operation operation, ConventionalUtFrame frame,
        PendingGap gap)
    {
        var configuration = frame.Metadata.Configuration;
        var stream = new UtStreamIdentity(_inspectionId, operation.RunId, frame.Metadata.SourceId,
            configuration.PhysicalChannelId);
        IInspectionRunWriter writer = operation.InspectionWriter ??
            throw new InvalidOperationException("The inspection writer is unavailable.");
        ApplicationCommitAdmission admission = operation.CommitCoordinator.AdmitCommit(operation.RunId,
            ApplicationCommitKind.Gap, () => writer.CommitGapAsync(ToGapRecord(stream, gap)));
        ApplicationCommitTerminalResult terminal = await admission.Completion.ConfigureAwait(false);
        _ = CompleteCommitAdmission(operation, admission, terminal);
        lock (_gate)
        {
            operation.LastObservedSequence = frame.Sequence;
            ApplyConfirmedGapState(operation, gap);
        }
        lock (_gate) { Publish(operation, _snapshot.Phase); }
    }

    private static bool SameConfiguration(ConventionalAcquisitionConfiguration actual,
        ConventionalAcquisitionConfiguration expected) =>
        actual.PhysicalChannelId == expected.PhysicalChannelId && actual.SignalMode == expected.SignalMode &&
        actual.SampleCount == expected.SampleCount && actual.SampleRateHz == expected.SampleRateHz &&
        actual.FirstSampleOffsetSeconds == expected.FirstSampleOffsetSeconds;

    private void DeliverCommittedBScan(Operation operation, ConventionalUtFrame frame,
        InspectionRevision revision)
    {
        lock (_gate)
        {
            if (!operation.VisualOpened || operation.BScanError is not null) { return; }
        }

        try
        {
            var configuration = frame.Metadata.Configuration;
            var stream = new UtStreamIdentity(_inspectionId, operation.RunId, frame.Metadata.SourceId,
                configuration.PhysicalChannelId);
            _bScanSink!.NotifyCommitted(new TraceIdentity(stream, frame.AcquisitionIndex), revision);
        }
        catch (Exception error)
        {
            lock (_gate)
            {
                RecordBScanError(operation, error);
                CloseVisual(operation);
                Publish(operation, _snapshot.Phase);
            }
        }
    }

    private void DeliverCommittedProcessing(Operation operation, ConventionalUtFrame frame,
        InspectionRevision revision)
    {
        if (!operation.ProcessingOpened || _processingIngress is null) { return; }
        try
        {
            var configuration = frame.Metadata.Configuration;
            var stream = new UtStreamIdentity(_inspectionId, operation.RunId, frame.Metadata.SourceId,
                configuration.PhysicalChannelId);
            GateProcessingAdmissionStatus status = _processingIngress.NotifyCommitted(
                new(new TraceIdentity(stream, frame.AcquisitionIndex), revision));
            if (status is GateProcessingAdmissionStatus.Rejected or GateProcessingAdmissionStatus.Closed)
            { lock (_gate) { AddBranchError(operation, new("session.processing.signal", $"Processing signal was {status}.")); } }
        }
        catch (Exception error)
        { lock (_gate) { AddBranchError(operation, Describe("session.processing.signal", error)); } }
    }

    private async ValueTask<InspectionAppendResult> CommitInspectionAsync(Operation operation,
        ConventionalUtFrame frame, PendingGap? gap)
    {
        var configuration = frame.Metadata.Configuration;
        var stream = new UtStreamIdentity(_inspectionId, operation.RunId, frame.Metadata.SourceId,
            configuration.PhysicalChannelId);
        var descriptor = new TraceDescriptor(new TraceIdentity(stream, frame.AcquisitionIndex), frame.Sequence,
            frame.ElapsedSinceRunStart, frame.Metadata.RunStartedAtUtc + frame.ElapsedSinceRunStart,
            frame.SpatialCoordinate, configuration.SampleCount, configuration.SampleRateHz,
            configuration.FirstSampleOffsetSeconds, configuration.SignalMode);
        IInspectionRunWriter writer = operation.InspectionWriter ??
            throw new InvalidOperationException("The inspection writer is unavailable.");
        IReadOnlyList<GapRecord> gaps = gap is { } confirmed ? [ToGapRecord(stream, confirmed)] : [];
        ApplicationCommitAdmission admission = operation.CommitCoordinator.AdmitCommit(operation.RunId,
            ApplicationCommitKind.Rf,
            () => writer.CommitAsync(new InspectionCommitInput(new InspectionTraceInput(descriptor, frame.Samples), gaps)));
        ApplicationCommitTerminalResult terminal = await admission.Completion.ConfigureAwait(false);
        InspectionAppendResult result = CompleteCommitAdmission(operation, admission, terminal);
        if (result.Status != InspectionAppendStatus.Duplicate) ApplyCommittedFrame(operation, frame, gap);
        if (result.IsCommitted && gap is not null) { lock (_gate) { Publish(operation, _snapshot.Phase); } }
        return result;
    }

    private InspectionAppendResult CompleteCommitAdmission(Operation operation, ApplicationCommitAdmission admission,
        ApplicationCommitTerminalResult terminal)
    {
        if (terminal.AdmissionOrdinal != admission.AdmissionOrdinal || terminal.Kind != admission.Kind ||
            terminal.RunId != admission.RunId)
            throw new InvalidOperationException("The terminal commit identity does not match its admission.");

        lock (_gate)
        {
            ulong expected = checked(operation.LastReconciledCommitOrdinal + 1);
            if (admission.AdmissionOrdinal != expected)
                throw new InvalidOperationException("Commit reconciliation did not preserve admission order.");
        }

        if (terminal.IsSuccessful && terminal.AppendResult is { } successful)
        {
            lock (_gate) { operation.LastReconciledCommitOrdinal = admission.AdmissionOrdinal; }
            return successful;
        }
        if (terminal.Error is not null)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(terminal.Error).Throw();
        if (terminal.AppendResult is { } rejected && terminal.Status == ApplicationCommitTerminalStatus.WriterRejected)
            throw new InspectionAppendException(rejected.Status, rejected.ErrorCode);
        throw new InvalidOperationException($"Commit admission {terminal.AdmissionOrdinal} ended as {terminal.Status}; " +
            $"previous revision {terminal.PreviousRevision.Value}, observed revision {terminal.ObservedRevision.Value}.");
    }

    private void DeliverCommittedAScan(Operation operation, ConventionalUtFrame frame, InspectionRevision revision)
    {
        lock (_gate)
        {
            if (!operation.AScanOpened || operation.AScanError is not null) { return; }
        }
        try
        {
            var configuration = frame.Metadata.Configuration;
            var stream = new UtStreamIdentity(_inspectionId, operation.RunId, frame.Metadata.SourceId,
                configuration.PhysicalChannelId);
            _aScanSink!.NotifyCommitted(new TraceIdentity(stream, frame.AcquisitionIndex), revision);
        }
        catch (Exception error)
        {
            lock (_gate) { RecordAScanError(operation, error); CloseAScan(operation); Publish(operation, _snapshot.Phase); }
        }
    }

    private void ApplyCommittedFrame(Operation operation, ConventionalUtFrame frame, PendingGap? gap)
    {
        lock (_gate)
        {
            operation.LastObservedSequence = frame.Sequence;
            operation.LastObservedAcquisitionIndex = frame.AcquisitionIndex;
            operation.LastSequence = frame.Sequence;
            operation.LastElapsed = frame.ElapsedSinceRunStart;
            operation.LastMetadata = frame.Metadata;
            if (gap is { } confirmed) { ApplyConfirmedGapState(operation, confirmed); }
        }
    }

    private static GapRecord ToGapRecord(UtStreamIdentity stream, PendingGap gap) =>
        new(stream, gap.FirstMissing, gap.LastMissing, gap.PreviousAdmitted,
            gap.SubsequentObserved, InspectionGapCause.SourceSequenceDiscontinuity);

    private static void ApplyConfirmedGapState(Operation operation, PendingGap gap)
    {
        ulong missingCount = checked(gap.LastMissing - gap.FirstMissing + 1);
        operation.AcquisitionMissingFrames = checked(operation.AcquisitionMissingFrames + missingCount);
    }

    private void CloseVisual(Operation operation)
    {
        if (!operation.VisualOpened) { return; }
        operation.VisualOpened = false;
        try { _bScanSink!.CloseRun(operation.RunId); }
        catch (Exception error) { RecordBScanError(operation, error); }
    }

    private void CloseAScan(Operation operation)
    {
        if (!operation.AScanOpened) { return; }
        operation.AScanOpened = false;
        try { _aScanSink!.CloseRun(operation.RunId); }
        catch (Exception error) { RecordAScanError(operation, error); }
    }

    private static void RecordBScanError(Operation operation, Exception error)
    {
        operation.BScanError ??= Describe("session.visual.bscan", error);
        operation.VisualError ??= operation.BScanError;
        AddVisualBranchError(operation, operation.BScanError);
    }

    private static void RecordAScanError(Operation operation, Exception error)
    {
        operation.AScanError ??= Describe("session.visual.ascan", error);
        operation.VisualError ??= operation.AScanError;
        AddVisualBranchError(operation, operation.AScanError);
    }

    private static void AddVisualBranchError(Operation operation, UtSourceError error)
    {
        if (operation.CleanupErrors.Any(existing => existing.Code == error.Code)) { return; }
        if (operation.CleanupErrors.Count < 8) { operation.CleanupErrors.Add(error); }
        else { operation.AdditionalErrorCount++; }
    }

    private async Task WaitForVisualCloseAsync(Operation operation)
    {
        if (_bScanSink is null) { return; }
        try { await _bScanSink.WaitForCloseAsync(operation.RunId).ConfigureAwait(false); }
        catch (Exception error) { lock (_gate) { RecordBScanError(operation, error); } }
    }

    private async Task WaitForAScanCloseAsync(Operation operation)
    {
        if (_aScanSink is null) { return; }
        try { await _aScanSink.WaitForCloseAsync(operation.RunId).ConfigureAwait(false); }
        catch (Exception error) { lock (_gate) { RecordAScanError(operation, error); } }
    }

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
                operation.CommitCoordinator.CloseAdmission();
                lock (_gate) { Publish(operation, SessionPhase.AwaitingFramesReleased); }
                operation.ResourcesReleased = await ObserveAsync(operation, () => run.AllFramesReleased, "session.barrier").ConfigureAwait(false);
            }
            else
            {
                // A source that has not returned a run owns and has completed startup rollback.
                operation.ResourcesReleased = true;
                operation.CommitCoordinator.CloseAdmission();
            }

            InspectionRunSealResult? sealedRun = null;
            await operation.CommitCoordinator.DisposeAsync().ConfigureAwait(false);
            if (operation.InspectionWriter is not null)
            {
                try
                {
                    sealedRun = await operation.InspectionWriter.SealRunAsync().ConfigureAwait(false);
                }
                catch (Exception error)
                {
                    lock (_gate) { RecordError(operation, error, "session.seal"); }
                }
            }

            await FinalizeProcessingAsync(operation, sealedRun).ConfigureAwait(false);
            await FinalizeBScanAsync(operation, sealedRun).ConfigureAwait(false);

            lock (_gate) { CloseAScan(operation); }
            await WaitForAScanCloseAsync(operation).ConfigureAwait(false);

            bool disconnect;
            lock (_gate) { disconnect = operation.DisconnectRequested || _disposing || operation.PrimaryException is not null; }
            if (operation.ResourcesReleased && disconnect)
            {
                lock (_gate) { Publish(operation, SessionPhase.Disconnecting); }
                operation.DisconnectAttempted = true;
                operation.Disconnected = await ObserveAsync(operation, () => _source.DisconnectAsync(), "session.disconnect").ConfigureAwait(false);
            }

            if (operation.InspectionWriter is not null)
            { await operation.InspectionWriter.DisposeAsync().ConfigureAwait(false); }

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

    private async Task FinalizeBScanAsync(Operation operation, InspectionRunSealResult? sealedRun)
    {
        if (_bScanSink is null || !operation.VisualRunRequiresDrain) { return; }
        if (_bScanSink is ICommittedTraceLifecycleSink lifecycle && sealedRun is { } final)
        {
            try
            {
                lifecycle.RequestFinalReconciliation(operation.RunId, final.FinalRevision);
                lifecycle.StopAdmission(operation.RunId);
                await lifecycle.WaitForDrainAsync(operation.RunId).ConfigureAwait(false);
                lifecycle.FreezeRun(operation.RunId);
                operation.VisualOpened = false;
                operation.VisualRunRequiresDrain = false;
                return;
            }
            catch (Exception error)
            {
                lock (_gate) { RecordBScanError(operation, error); }
            }
        }

        lock (_gate) { CloseVisual(operation); }
        await WaitForVisualCloseAsync(operation).ConfigureAwait(false);
        operation.VisualRunRequiresDrain = false;
    }

    private async Task FinalizeProcessingAsync(Operation operation, InspectionRunSealResult? sealedRun)
    {
        if (!operation.ProcessingOpened || _processingIngress is null) { return; }
        try { _processingIngress.StopAdmission(operation.RunId); }
        catch (Exception error)
        { lock (_gate) { AddBranchError(operation, Describe("session.processing.stop", error)); } }

        if (sealedRun is { } final)
        {
            try
            {
                _processingIngress.RequestFinalReconciliation(operation.RunId, final.FinalRevision);
            }
            catch (Exception error)
            { lock (_gate) { AddBranchError(operation, Describe("session.processing.final", error)); } }
        }
        else
        { lock (_gate) { AddBranchError(operation, new("session.processing.final", "Processing cannot drain without FinalRevision.")); } }

        try
        {
            await _processingIngress.WaitForDrainAsync(operation.RunId).ConfigureAwait(false);
            operation.ProcessingOpened = false;
        }
        catch (Exception error)
        { lock (_gate) { AddBranchError(operation, Describe("session.processing.drain", error)); } }
    }

    private static void AddBranchError(Operation operation, UtSourceError error)
    {
        if (operation.CleanupErrors.Any(existing => existing.Code == error.Code)) { return; }
        if (operation.CleanupErrors.Count < 8) { operation.CleanupErrors.Add(error); }
        else { operation.AdditionalErrorCount++; }
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
            operation.PrimaryError, operation.CleanupErrors, operation.AdditionalErrorCount, operation.VisualError,
            canStart: _active is null && !_disposing, canStop: _active is { CleanupRequested: false } && !_disposing,
            integrity: operation.Integrity);
        _publisher.Publish(_snapshot, complete);
    }

    private void PublishStandalone(SessionPhase phase, UtSourceError? error, bool complete = false)
    {
        _snapshot = new SessionSnapshot(_snapshot.Version + 1, phase, _source.SourceId, _snapshot.RunId,
            _snapshot.ReceivedFrames, _snapshot.ReleasedFrames, _snapshot.LastSequence, _snapshot.LastMetadata,
            error, _snapshot.CleanupErrors, _snapshot.AdditionalErrorCount, _snapshot.VisualError,
            canStart: _active is null && !_disposing, canStop: _active is { CleanupRequested: false } && !_disposing,
            integrity: _snapshot.Integrity);
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
        internal ulong? LastObservedSequence;
        internal ulong? LastObservedAcquisitionIndex;
        internal TimeSpan LastElapsed;
        internal ConventionalUtFrameMetadata? LastMetadata;
        internal bool VisualOpened;
        internal bool VisualRunRequiresDrain;
        internal bool AScanOpened;
        internal bool ProcessingOpened;
        internal UtSourceError? BScanError;
        internal UtSourceError? AScanError;
        internal UtSourceError? VisualError;
        internal IInspectionRunWriter? InspectionWriter;
        internal ApplicationCommitCoordinator CommitCoordinator { get; } = new(CommitCoordinatorCapacity);
        internal ulong LastReconciledCommitOrdinal;
        internal ulong AcquisitionMissingFrames;
        internal ulong DuplicateFramesRejected;
        internal ulong OutOfOrderFramesRejected;
        internal ulong TimestampRegressionsRejected;
        internal ulong InvalidAcquisitionIndexFramesRejected;
        internal SessionIntegrityStatistics Integrity => new(AcquisitionMissingFrames, DuplicateFramesRejected,
            OutOfOrderFramesRejected, TimestampRegressionsRejected, InvalidAcquisitionIndexFramesRejected);
    }

    private readonly record struct PendingGap(ulong FirstMissing, ulong LastMissing,
        ulong PreviousAdmitted, ulong SubsequentObserved);
    private readonly record struct FrameInspection(bool IsValid, PendingGap? Gap);
}

public sealed class InspectionAppendException : Exception
{
    public InspectionAppendException(InspectionAppendStatus status, string? errorCode)
        : base($"Inspection append did not commit: {status}{(errorCode is null ? string.Empty : $" ({errorCode})")}.")
    {
        Status = status;
        ErrorCode = errorCode;
    }

    public InspectionAppendStatus Status { get; }
    public string? ErrorCode { get; }
}
