using System.Runtime.CompilerServices;
using UTStudio.Contracts.Inspection;
using UTStudio.Domain.Acquisition;
using UTStudio.Domain.Inspection;

[assembly: InternalsVisibleTo("UTStudio.Core.Tests")]
[assembly: InternalsVisibleTo("UTStudio.App.Wpf")]
[assembly: InternalsVisibleTo("UTStudio.Tests.Wpf")]

namespace UTStudio.Application;

internal enum ApplicationCommitOperationClass { RfCommit, GapCommit, GatePlanApply, GateRunTransition }
internal enum ApplicationCommitTerminalStatus
{
    Completed, Confirmed, DuplicateUnverifiable, WriterRejected, WriterFaulted, OperationFaulted, RevisionRegression,
    NonAdvancingRevision, InvariantFaulted, SkippedAfterInvariantFault
}
internal enum ApplicationCommitCoordinatorLifecycle
{ Accepting, Running, PublishingTerminal, Closing, Draining, Closed, Faulted }
internal enum ApplicationCommitAdmissionStatus
{ Admitted, CancelledBeforeAdmission, AdmissionClosed, CapacityExceeded, OrdinalExhausted }

internal readonly record struct ApplicationCommitOperationIdentity(string Value)
{
    internal bool IsValid => !string.IsNullOrWhiteSpace(Value) && Value.Length <= 256;
}

internal readonly record struct ApplicationCommitClassCounters(
    ulong RfCommit, ulong GapCommit, ulong GatePlanApply, ulong GateRunTransition)
{
    internal ulong For(ApplicationCommitOperationClass operationClass) => operationClass switch
    {
        ApplicationCommitOperationClass.RfCommit => RfCommit,
        ApplicationCommitOperationClass.GapCommit => GapCommit,
        ApplicationCommitOperationClass.GatePlanApply => GatePlanApply,
        ApplicationCommitOperationClass.GateRunTransition => GateRunTransition,
        _ => throw new ArgumentOutOfRangeException(nameof(operationClass))
    };

    internal ApplicationCommitClassCounters Increment(ApplicationCommitOperationClass operationClass) =>
        operationClass switch
        {
            ApplicationCommitOperationClass.RfCommit => this with { RfCommit = checked(RfCommit + 1) },
            ApplicationCommitOperationClass.GapCommit => this with { GapCommit = checked(GapCommit + 1) },
            ApplicationCommitOperationClass.GatePlanApply => this with { GatePlanApply = checked(GatePlanApply + 1) },
            ApplicationCommitOperationClass.GateRunTransition => this with
            { GateRunTransition = checked(GateRunTransition + 1) },
            _ => throw new ArgumentOutOfRangeException(nameof(operationClass))
        };
}

internal sealed record ApplicationCommitOperationDescriptor(ulong AdmissionOrdinal,
    ApplicationCommitOperationClass OperationClass, InspectionId InspectionId, AcquisitionRunId? RunId,
    ApplicationCommitOperationIdentity Identity);

internal sealed record ApplicationCommitTerminalResult(ulong AdmissionOrdinal,
    ApplicationCommitOperationClass OperationClass, InspectionId InspectionId, AcquisitionRunId? RunId,
    ApplicationCommitOperationIdentity Identity, ApplicationCommitTerminalStatus Status,
    InspectionAppendResult? AppendResult, InspectionRevision PreviousRevision,
    InspectionRevision ObservedRevision, Exception? Error)
{
    public bool IsSuccessful => Status is ApplicationCommitTerminalStatus.Completed or
        ApplicationCommitTerminalStatus.Confirmed;
}

internal sealed record ApplicationCommitCoordinatorSnapshot(ApplicationCommitCoordinatorLifecycle Lifecycle,
    int Capacity, int Occupancy, int Queued, ulong NextAdmissionOrdinal, ulong LastAdmissionOrdinal,
    ulong LastTerminalOrdinal, ApplicationCommitOperationDescriptor? RunningOperation,
    bool AcquisitionAdmissionOpen, bool ApplyAdmissionOpen, bool RunTransitionAdmissionOpen,
    ApplicationCommitClassCounters CapacityRejections, ApplicationCommitClassCounters ClosedRejections,
    AcquisitionRunId RevisionRun, InspectionRevision LastConfirmedRevision);

internal sealed class ApplicationCommitCoordinatorTestSynchronization
{
    private readonly TaskCompletionSource _publishingTerminalReached =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _releasePublishingTerminal =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal Task PublishingTerminalReached => _publishingTerminalReached.Task;
    internal void ReleasePublishingTerminal() => _releasePublishingTerminal.TrySetResult();
    internal async ValueTask PausePublishingTerminalAsync()
    {
        _publishingTerminalReached.TrySetResult();
        await _releasePublishingTerminal.Task.ConfigureAwait(false);
    }
}

internal sealed class ApplicationCommitAdmission
{
    internal ApplicationCommitAdmission(ApplicationCommitOperationDescriptor descriptor,
        Task<ApplicationCommitTerminalResult> completion)
    { Descriptor = descriptor; Completion = completion; }
    internal ApplicationCommitOperationDescriptor Descriptor { get; }
    public ulong AdmissionOrdinal => Descriptor.AdmissionOrdinal;
    public ApplicationCommitOperationClass OperationClass => Descriptor.OperationClass;
    public InspectionId InspectionId => Descriptor.InspectionId;
    public AcquisitionRunId? RunId => Descriptor.RunId;
    public ApplicationCommitOperationIdentity Identity => Descriptor.Identity;
    public Task<ApplicationCommitTerminalResult> Completion { get; }
    public Task<ApplicationCommitTerminalResult> WaitAsync(CancellationToken token = default) =>
        Completion.WaitAsync(token);
}

internal readonly record struct ApplicationCommitAdmissionResult(
    ApplicationCommitAdmissionStatus Status, ApplicationCommitAdmission? Admission)
{
    internal bool IsAdmitted => Status == ApplicationCommitAdmissionStatus.Admitted && Admission is not null;
    internal static ApplicationCommitAdmissionResult Admitted(ApplicationCommitAdmission admission) =>
        new(ApplicationCommitAdmissionStatus.Admitted, admission);
    internal static ApplicationCommitAdmissionResult Rejected(ApplicationCommitAdmissionStatus status) =>
        status == ApplicationCommitAdmissionStatus.Admitted ? throw new ArgumentOutOfRangeException(nameof(status)) :
            new(status, null);
}

internal sealed class ApplicationCommitInvariantException : Exception
{
    internal ApplicationCommitInvariantException(string message) : base(message) { }
    internal ApplicationCommitInvariantException(string message, Exception innerException) : base(message, innerException) { }
}

/// <summary>One bounded FIFO and ordering boundary for acquisition and future gate-control operations.</summary>
internal sealed class ApplicationCommitCoordinator : IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly Queue<WorkItem> _queue = [];
    private readonly InspectionId _inspectionId;
    private readonly int _capacity;
    private readonly ApplicationCommitCoordinatorTestSynchronization? _testSynchronization;
    private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private ApplicationCommitCoordinatorLifecycle _lifecycle = ApplicationCommitCoordinatorLifecycle.Accepting;
    private DrainRegistration _drain = DrainRegistration.Completed;
    private WorkItem? _running;
    private bool _runnerRegistered;
    private ulong _lastAdmissionOrdinal, _lastTerminalOrdinal;
    private ApplicationCommitClassCounters _capacityRejections, _closedRejections;
    private AcquisitionRunId _admissionRun, _revisionRun;
    private InspectionRevision _lastConfirmedRevision = InspectionRevision.Initial;
    private bool _acquisitionOpen = true, _applyOpen = true, _transitionOpen = true;
    private Exception? _primaryInvariantError;

    internal ApplicationCommitCoordinator(InspectionId inspectionId, int capacity,
        ApplicationCommitCoordinatorTestSynchronization? testSynchronization = null)
    {
        if (!inspectionId.IsValid) throw new ArgumentException("A valid inspection is required.", nameof(inspectionId));
        if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
        _inspectionId = inspectionId; _capacity = capacity; _testSynchronization = testSynchronization;
    }

    internal ApplicationCommitAdmission AdmitCommit(AcquisitionRunId runId,
        ApplicationCommitOperationClass operationClass, Func<ValueTask<InspectionAppendResult>> commit,
        CancellationToken cancellationToken = default)
    {
        if (operationClass is not (ApplicationCommitOperationClass.RfCommit or
            ApplicationCommitOperationClass.GapCommit)) throw new ArgumentOutOfRangeException(nameof(operationClass));
        if (!runId.IsValid) throw new ArgumentException("A valid run is required.", nameof(runId));
        ArgumentNullException.ThrowIfNull(commit);
        return Admit(operationClass, runId, new($"{operationClass}:{Guid.NewGuid():D}"),
            async () => new(await commit().ConfigureAwait(false)), cancellationToken);
    }

    internal ApplicationCommitAdmission AdmitGatePlanApply(ApplicationCommitOperationIdentity identity,
        Func<ValueTask> operation, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        return Admit(ApplicationCommitOperationClass.GatePlanApply, null, identity, async () =>
        { await operation().ConfigureAwait(false); return default; }, cancellationToken);
    }

    internal ApplicationCommitAdmissionResult TryAdmitGatePlanApply(AcquisitionRunId runId,
        ApplicationCommitOperationIdentity identity, Func<ValueTask<Action?>> operation,
        CancellationToken cancellationToken = default)
    {
        if (!runId.IsValid) throw new ArgumentException("A valid run is required.", nameof(runId));
        ArgumentNullException.ThrowIfNull(operation);
        return TryAdmit(ApplicationCommitOperationClass.GatePlanApply, runId, identity, async () =>
            new(null, await operation().ConfigureAwait(false)), cancellationToken);
    }

    internal ApplicationCommitAdmission AdmitGateRunTransition(AcquisitionRunId runId,
        ApplicationCommitOperationIdentity identity, Func<ValueTask> operation,
        CancellationToken cancellationToken = default)
    {
        if (!runId.IsValid) throw new ArgumentException("A valid run is required.", nameof(runId));
        ArgumentNullException.ThrowIfNull(operation);
        return Admit(ApplicationCommitOperationClass.GateRunTransition, runId, identity, async () =>
        { await operation().ConfigureAwait(false); return default; }, cancellationToken);
    }

    private ApplicationCommitAdmission Admit(ApplicationCommitOperationClass operationClass,
        AcquisitionRunId? runId, ApplicationCommitOperationIdentity identity, Func<ValueTask<BodyResult>> body,
        CancellationToken cancellationToken)
    {
        if (!identity.IsValid) throw new ArgumentException("A valid bounded identity is required.", nameof(identity));
        cancellationToken.ThrowIfCancellationRequested();
        WorkItem item; DrainRegistration? drainToStart = null;
        lock (_gate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            bool globalClosed = IsGloballyClosedUnderLock();
            if (globalClosed || !IsOpenUnderLock(operationClass))
            {
                _closedRejections = _closedRejections.Increment(operationClass);
                if (globalClosed) throw new ObjectDisposedException(nameof(ApplicationCommitCoordinator));
                throw new InvalidOperationException($"{operationClass} admission is closed.");
            }
            if (OccupancyUnderLock() >= _capacity)
            {
                _capacityRejections = _capacityRejections.Increment(operationClass);
                throw new InvalidOperationException("The bounded commit coordinator is at capacity.");
            }
            if (operationClass is ApplicationCommitOperationClass.RfCommit or
                ApplicationCommitOperationClass.GapCommit)
            {
                if (_admissionRun.IsValid && _admissionRun != runId)
                    throw new InvalidOperationException("A commit coordinator cannot mix acquisition runs.");
                _admissionRun = runId!.Value;
            }
            if (_lastAdmissionOrdinal == ulong.MaxValue)
                throw new OverflowException("The commit admission ordinal is exhausted.");
            var descriptor = new ApplicationCommitOperationDescriptor(++_lastAdmissionOrdinal, operationClass,
                _inspectionId, runId, identity);
            item = new(descriptor, body);
            if (!_runnerRegistered)
            {
                _runnerRegistered = true; _running = item;
                _lifecycle = ApplicationCommitCoordinatorLifecycle.Running;
                drainToStart = new(); _drain = drainToStart;
            }
            else _queue.Enqueue(item);
        }
        if (drainToStart is { } drain) { Task execution = DrainAsync(item); drain.Start(execution); }
        return item.Admission;
    }

    private ApplicationCommitAdmissionResult TryAdmit(ApplicationCommitOperationClass operationClass,
        AcquisitionRunId? runId, ApplicationCommitOperationIdentity identity, Func<ValueTask<BodyResult>> body,
        CancellationToken cancellationToken)
    {
        if (!identity.IsValid) throw new ArgumentException("A valid bounded identity is required.", nameof(identity));
        if (cancellationToken.IsCancellationRequested)
            return ApplicationCommitAdmissionResult.Rejected(ApplicationCommitAdmissionStatus.CancelledBeforeAdmission);
        WorkItem item; DrainRegistration? drainToStart = null;
        lock (_gate)
        {
            if (cancellationToken.IsCancellationRequested)
                return ApplicationCommitAdmissionResult.Rejected(ApplicationCommitAdmissionStatus.CancelledBeforeAdmission);
            if (IsGloballyClosedUnderLock() || !IsOpenUnderLock(operationClass))
            {
                _closedRejections = _closedRejections.Increment(operationClass);
                return ApplicationCommitAdmissionResult.Rejected(ApplicationCommitAdmissionStatus.AdmissionClosed);
            }
            if (OccupancyUnderLock() >= _capacity)
            {
                _capacityRejections = _capacityRejections.Increment(operationClass);
                return ApplicationCommitAdmissionResult.Rejected(ApplicationCommitAdmissionStatus.CapacityExceeded);
            }
            if (_lastAdmissionOrdinal == ulong.MaxValue)
                return ApplicationCommitAdmissionResult.Rejected(ApplicationCommitAdmissionStatus.OrdinalExhausted);
            var descriptor = new ApplicationCommitOperationDescriptor(++_lastAdmissionOrdinal, operationClass,
                _inspectionId, runId, identity);
            item = new(descriptor, body);
            if (!_runnerRegistered)
            {
                _runnerRegistered = true; _running = item;
                _lifecycle = ApplicationCommitCoordinatorLifecycle.Running;
                drainToStart = new(); _drain = drainToStart;
            }
            else _queue.Enqueue(item);
        }
        if (drainToStart is { } drain) { Task execution = DrainAsync(item); drain.Start(execution); }
        return ApplicationCommitAdmissionResult.Admitted(item.Admission);
    }

    internal ApplicationCommitCoordinatorSnapshot Snapshot
    {
        get
        {
            lock (_gate)
            {
                ulong next = _lastAdmissionOrdinal == ulong.MaxValue ? ulong.MaxValue : _lastAdmissionOrdinal + 1;
                return new(_lifecycle, _capacity, OccupancyUnderLock(), _queue.Count, next,
                    _lastAdmissionOrdinal, _lastTerminalOrdinal, _running?.Descriptor,
                    _acquisitionOpen, _applyOpen, _transitionOpen, _capacityRejections, _closedRejections,
                    _revisionRun, _lastConfirmedRevision);
            }
        }
    }

    internal void CloseAcquisitionAdmission() { lock (_gate) { if (!IsGloballyClosedUnderLock()) _acquisitionOpen = false; } }
    internal void CloseApplyAdmission() { lock (_gate) { if (!IsGloballyClosedUnderLock()) _applyOpen = false; } }
    internal void CloseRunTransitionAdmission() { lock (_gate) { if (!IsGloballyClosedUnderLock()) _transitionOpen = false; } }

    internal void CloseAdmission()
    {
        bool complete = false;
        lock (_gate)
        {
            if (IsGloballyClosedUnderLock()) return;
            _acquisitionOpen = _applyOpen = _transitionOpen = false;
            if (!_runnerRegistered) { _lifecycle = ApplicationCommitCoordinatorLifecycle.Closed; complete = true; }
            else _lifecycle = ApplicationCommitCoordinatorLifecycle.Closing;
        }
        if (complete) _completion.TrySetResult();
    }

    public async ValueTask DisposeAsync()
    {
        CloseAdmission(); DrainRegistration drain;
        lock (_gate) { drain = _drain; }
        await drain.Completion.ConfigureAwait(false);
        await _completion.Task.ConfigureAwait(false);
    }

    private async Task DrainAsync(WorkItem first)
    {
        WorkItem? current = first;
        while (current is not null)
        {
            ExecutionResult execution = _primaryInvariantError is null ?
                await ExecuteAsync(current).ConfigureAwait(false) :
                new(Terminal(current, ApplicationCommitTerminalStatus.SkippedAfterInvariantFault, null,
                    LastRevision(), default, _primaryInvariantError), null);
            ApplicationCommitTerminalResult terminal = execution.Terminal;
            WorkItem completed = current;
            bool publishingTerminal; bool invariantFault = terminal.Status == ApplicationCommitTerminalStatus.InvariantFaulted;
            lock (_gate)
            {
                ulong expected = checked(_lastTerminalOrdinal + 1);
                if (completed.Descriptor.AdmissionOrdinal != expected)
                    throw new InvalidOperationException("Global terminal reconciliation did not preserve admission order.");
                _lastTerminalOrdinal = completed.Descriptor.AdmissionOrdinal;
                if (invariantFault && _primaryInvariantError is null)
                {
                    _primaryInvariantError = terminal.Error ?? new ApplicationCommitInvariantException(
                        "An aggregate commit invariant failed.");
                    _acquisitionOpen = _applyOpen = _transitionOpen = false;
                }
                current = null; _running = null;
                bool closing = _lifecycle is ApplicationCommitCoordinatorLifecycle.Closing or
                    ApplicationCommitCoordinatorLifecycle.Draining;
                _lifecycle = invariantFault ? ApplicationCommitCoordinatorLifecycle.Faulted :
                    closing ? ApplicationCommitCoordinatorLifecycle.Draining :
                    ApplicationCommitCoordinatorLifecycle.PublishingTerminal;
                publishingTerminal = _lifecycle == ApplicationCommitCoordinatorLifecycle.PublishingTerminal;
            }
            if (publishingTerminal && _testSynchronization is { } testSynchronization)
                await testSynchronization.PausePublishingTerminalAsync().ConfigureAwait(false);
            try { execution.Publish?.Invoke(); }
            catch (Exception error)
            {
                var invariant = error as ApplicationCommitInvariantException ??
                    new ApplicationCommitInvariantException("Post-commit publication failed.", error);
                terminal = Terminal(completed, ApplicationCommitTerminalStatus.InvariantFaulted,
                    terminal.AppendResult, terminal.PreviousRevision, terminal.ObservedRevision, invariant);
                lock (_gate)
                {
                    _primaryInvariantError ??= invariant;
                    _acquisitionOpen = _applyOpen = _transitionOpen = false;
                    _lifecycle = ApplicationCommitCoordinatorLifecycle.Faulted;
                }
            }
            completed.Complete(terminal);
            bool closeCompleted = false;
            lock (_gate)
            {
                if (_queue.TryDequeue(out current))
                {
                    _running = current;
                    _lifecycle = _primaryInvariantError is not null ? ApplicationCommitCoordinatorLifecycle.Faulted :
                        _lifecycle is ApplicationCommitCoordinatorLifecycle.Closing or
                        ApplicationCommitCoordinatorLifecycle.Draining ? ApplicationCommitCoordinatorLifecycle.Draining :
                        ApplicationCommitCoordinatorLifecycle.Running;
                }
                else
                {
                    _runnerRegistered = false;
                    if (_primaryInvariantError is not null) { _lifecycle = ApplicationCommitCoordinatorLifecycle.Faulted; closeCompleted = true; }
                    else if (_lifecycle is ApplicationCommitCoordinatorLifecycle.Closing or ApplicationCommitCoordinatorLifecycle.Draining)
                    { _lifecycle = ApplicationCommitCoordinatorLifecycle.Closed; closeCompleted = true; }
                    else _lifecycle = ApplicationCommitCoordinatorLifecycle.Accepting;
                }
            }
            if (closeCompleted) _completion.TrySetResult();
        }
    }

    private async ValueTask<ExecutionResult> ExecuteAsync(WorkItem item)
    {
        BodyResult body;
        try { body = await item.Body().ConfigureAwait(false); }
        catch (ApplicationCommitInvariantException error)
        { return new(Terminal(item, ApplicationCommitTerminalStatus.InvariantFaulted, null, LastRevision(), default, error), null); }
        catch (Exception error)
        {
            ApplicationCommitTerminalStatus status = item.Descriptor.OperationClass is
                ApplicationCommitOperationClass.RfCommit or ApplicationCommitOperationClass.GapCommit ?
                ApplicationCommitTerminalStatus.WriterFaulted : ApplicationCommitTerminalStatus.OperationFaulted;
            return new(Terminal(item, status, null, LastRevision(), default, error), null);
        }
        if (body.AppendResult is not { } append)
            return new(Terminal(item, ApplicationCommitTerminalStatus.Completed, null, LastRevision(), default, null), body.Publish);
        lock (_gate)
        {
            InspectionRevision previous = _lastConfirmedRevision;
            if (append.Status == InspectionAppendStatus.Duplicate)
                return new(Terminal(item, ApplicationCommitTerminalStatus.DuplicateUnverifiable, append, previous,
                    append.Revision, null), body.Publish);
            if (append.Status != InspectionAppendStatus.Committed)
                return new(Terminal(item, ApplicationCommitTerminalStatus.WriterRejected, append, previous,
                    append.Revision, null), body.Publish);
            AcquisitionRunId runId = item.Descriptor.RunId!.Value;
            if (_revisionRun.IsValid && _revisionRun != runId)
                return new(Terminal(item, ApplicationCommitTerminalStatus.WriterRejected, append, previous,
                    append.Revision, new InvalidOperationException("A commit coordinator cannot mix acquisition runs.")), body.Publish);
            if (append.Revision.Value < previous.Value)
                return new(Terminal(item, ApplicationCommitTerminalStatus.RevisionRegression, append, previous,
                    append.Revision, null), body.Publish);
            if (append.Revision == previous)
                return new(Terminal(item, ApplicationCommitTerminalStatus.NonAdvancingRevision, append, previous,
                    append.Revision, null), body.Publish);
            _revisionRun = runId; _lastConfirmedRevision = append.Revision;
            return new(Terminal(item, ApplicationCommitTerminalStatus.Confirmed, append, previous, append.Revision, null), body.Publish);
        }
    }

    private ApplicationCommitTerminalResult Terminal(WorkItem item, ApplicationCommitTerminalStatus status,
        InspectionAppendResult? append, InspectionRevision previous, InspectionRevision observed, Exception? error) =>
        new(item.Descriptor.AdmissionOrdinal, item.Descriptor.OperationClass, item.Descriptor.InspectionId,
            item.Descriptor.RunId, item.Descriptor.Identity, status, append, previous, observed, error);

    private InspectionRevision LastRevision() { lock (_gate) return _lastConfirmedRevision; }
    private int OccupancyUnderLock() => _queue.Count + (_running is null ? 0 : 1);
    private bool IsGloballyClosedUnderLock() => _lifecycle is ApplicationCommitCoordinatorLifecycle.Closing or
        ApplicationCommitCoordinatorLifecycle.Draining or ApplicationCommitCoordinatorLifecycle.Closed or
        ApplicationCommitCoordinatorLifecycle.Faulted;
    private bool IsOpenUnderLock(ApplicationCommitOperationClass operationClass) => operationClass switch
    {
        ApplicationCommitOperationClass.RfCommit or ApplicationCommitOperationClass.GapCommit => _acquisitionOpen,
        ApplicationCommitOperationClass.GatePlanApply => _applyOpen,
        ApplicationCommitOperationClass.GateRunTransition => _transitionOpen,
        _ => false
    };

    private readonly record struct BodyResult(InspectionAppendResult? AppendResult, Action? Publish = null);
    private readonly record struct ExecutionResult(ApplicationCommitTerminalResult Terminal, Action? Publish);
    private sealed class WorkItem
    {
        private readonly TaskCompletionSource<ApplicationCommitTerminalResult> _completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal WorkItem(ApplicationCommitOperationDescriptor descriptor, Func<ValueTask<BodyResult>> body)
        { Descriptor = descriptor; Body = body; Admission = new(descriptor, _completion.Task); }
        internal ApplicationCommitOperationDescriptor Descriptor { get; }
        internal Func<ValueTask<BodyResult>> Body { get; }
        internal ApplicationCommitAdmission Admission { get; }
        internal void Complete(ApplicationCommitTerminalResult result) => _completion.TrySetResult(result);
    }

    private sealed class DrainRegistration
    {
        private readonly TaskCompletionSource<Task> _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Task _completion;
        private DrainRegistration(bool completed)
        { _completion = _started.Task.Unwrap(); if (completed) _started.TrySetResult(Task.CompletedTask); }
        internal DrainRegistration() : this(false) { }
        internal static DrainRegistration Completed { get; } = new(true);
        internal Task Completion => _completion;
        internal void Start(Task execution) => _started.TrySetResult(execution);
    }
}
