using System.Runtime.CompilerServices;
using UTStudio.Contracts.Inspection;
using UTStudio.Domain.Acquisition;
using UTStudio.Domain.Inspection;

[assembly: InternalsVisibleTo("UTStudio.Core.Tests")]

namespace UTStudio.Application;

internal enum ApplicationCommitKind { Rf, Gap }
internal enum ApplicationCommitTerminalStatus
{
    Confirmed, DuplicateUnverifiable, WriterRejected, WriterFaulted, RevisionRegression,
    NonAdvancingRevision
}
internal enum ApplicationCommitCoordinatorLifecycle { Accepting, Running, Closing, Draining, Closed }

internal sealed record ApplicationCommitTerminalResult(ulong AdmissionOrdinal, ApplicationCommitKind Kind,
    AcquisitionRunId RunId, ApplicationCommitTerminalStatus Status, InspectionAppendResult? AppendResult,
    InspectionRevision PreviousRevision, InspectionRevision ObservedRevision, Exception? Error)
{
    public bool IsSuccessful => Status == ApplicationCommitTerminalStatus.Confirmed;
}

internal sealed record ApplicationCommitCoordinatorSnapshot(ApplicationCommitCoordinatorLifecycle Lifecycle,
    int Capacity, int Occupancy, int Queued, ulong? RunningOrdinal, ulong LastAdmissionOrdinal,
    ulong LastCompletionOrdinal, ulong CapacityRejections, ulong ClosedRejections, AcquisitionRunId RevisionRun,
    InspectionRevision LastConfirmedRevision);

/// <summary>An admitted commit whose coordinator-owned terminal completion is never caller-cancelled.</summary>
internal sealed class ApplicationCommitAdmission
{
    internal ApplicationCommitAdmission(ulong ordinal, ApplicationCommitKind kind, AcquisitionRunId runId,
        Task<ApplicationCommitTerminalResult> completion)
    { AdmissionOrdinal = ordinal; Kind = kind; RunId = runId; Completion = completion; }

    public ulong AdmissionOrdinal { get; }
    public ApplicationCommitKind Kind { get; }
    public AcquisitionRunId RunId { get; }
    public Task<ApplicationCommitTerminalResult> Completion { get; }
    public Task<ApplicationCommitTerminalResult> WaitAsync(CancellationToken token = default) =>
        Completion.WaitAsync(token);
}

/// <summary>
/// Bounded FIFO boundary for RF and gap commits. Capacity includes the running operation and queued operations,
/// but excludes reconciliation and publication after terminal completion.
/// </summary>
internal sealed class ApplicationCommitCoordinator : IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly Queue<CommitWorkItem> _queue = [];
    private readonly int _capacity;
    private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private ApplicationCommitCoordinatorLifecycle _lifecycle = ApplicationCommitCoordinatorLifecycle.Accepting;
    private DrainRegistration _drain = DrainRegistration.Completed;
    private CommitWorkItem? _running;
    private ulong _lastAdmissionOrdinal;
    private ulong _lastCompletionOrdinal;
    private ulong _capacityRejections;
    private ulong _closedRejections;
    private AcquisitionRunId _admissionRun;
    private AcquisitionRunId _revisionRun;
    private InspectionRevision _lastConfirmedRevision = InspectionRevision.Initial;

    internal ApplicationCommitCoordinator(int capacity)
    {
        if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
        _capacity = capacity;
    }

    internal ApplicationCommitAdmission AdmitCommit(AcquisitionRunId runId, ApplicationCommitKind kind,
        Func<ValueTask<InspectionAppendResult>> commit, CancellationToken cancellationToken = default)
    {
        if (!runId.IsValid) throw new ArgumentException("A valid run is required.", nameof(runId));
        if (!Enum.IsDefined(kind)) throw new ArgumentOutOfRangeException(nameof(kind));
        ArgumentNullException.ThrowIfNull(commit);
        cancellationToken.ThrowIfCancellationRequested();
        CommitWorkItem item;
        DrainRegistration? drainToStart = null;
        lock (_gate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_lifecycle is ApplicationCommitCoordinatorLifecycle.Closing or
                ApplicationCommitCoordinatorLifecycle.Draining or ApplicationCommitCoordinatorLifecycle.Closed)
            {
                _closedRejections = checked(_closedRejections + 1);
                throw new ObjectDisposedException(nameof(ApplicationCommitCoordinator));
            }
            if (OccupancyUnderLock() >= _capacity)
            {
                _capacityRejections = checked(_capacityRejections + 1);
                throw new InvalidOperationException("The bounded commit coordinator is at capacity.");
            }
            if (_admissionRun.IsValid && _admissionRun != runId)
                throw new InvalidOperationException("A commit coordinator cannot mix acquisition runs.");
            if (_lastAdmissionOrdinal == ulong.MaxValue)
                throw new OverflowException("The commit admission ordinal is exhausted.");

            _admissionRun = runId;
            item = new(++_lastAdmissionOrdinal, kind, runId, commit);
            bool startDrain = _running is null;
            if (startDrain)
            {
                _running = item;
                _lifecycle = ApplicationCommitCoordinatorLifecycle.Running;
                drainToStart = new();
                _drain = drainToStart;
            }
            else _queue.Enqueue(item);
        }
        // The drain owner is visible before leaving the gate, but no writer/delegate is invoked until after it.
        // The uncontended fast path executes directly without dispatching to the ThreadPool. The registration unwraps
        // and exposes the actual loop task, so Dispose observes its real terminal state even before Start publishes it.
        if (drainToStart is { } drain)
        {
            Task execution = DrainAsync(item);
            drain.Start(execution);
        }
        return item.Admission;
    }

    internal ApplicationCommitCoordinatorSnapshot Snapshot
    {
        get
        {
            lock (_gate)
            {
                return new(_lifecycle, _capacity, OccupancyUnderLock(), _queue.Count, _running?.Ordinal,
                    _lastAdmissionOrdinal, _lastCompletionOrdinal, _capacityRejections, _closedRejections,
                    _revisionRun, _lastConfirmedRevision);
            }
        }
    }

    internal void CloseAdmission()
    {
        bool complete = false;
        lock (_gate)
        {
            if (_lifecycle is ApplicationCommitCoordinatorLifecycle.Closed or
                ApplicationCommitCoordinatorLifecycle.Closing or ApplicationCommitCoordinatorLifecycle.Draining)
                return;
            if (_running is null) { _lifecycle = ApplicationCommitCoordinatorLifecycle.Closed; complete = true; }
            else _lifecycle = ApplicationCommitCoordinatorLifecycle.Closing;
        }
        if (complete) _completion.TrySetResult();
    }

    public async ValueTask DisposeAsync()
    {
        CloseAdmission();
        DrainRegistration drain;
        lock (_gate) { drain = _drain; }
        await drain.Completion.ConfigureAwait(false);
        await _completion.Task.ConfigureAwait(false);
    }

    private async Task DrainAsync(CommitWorkItem first)
    {
        CommitWorkItem? current = first;
        // All supported writer failures are operation results produced by ExecuteAsync; 3A has no global Faulted
        // lifecycle. An unexpected internal exception faults the registered drain task and is observed by Dispose.
        while (current is not null)
        {
            ApplicationCommitTerminalResult terminal = await ExecuteAsync(current).ConfigureAwait(false);
            CommitWorkItem completed = current;
            lock (_gate)
            {
                _lastCompletionOrdinal = completed.Ordinal;
                if (_queue.TryDequeue(out current))
                {
                    _running = current;
                    if (_lifecycle == ApplicationCommitCoordinatorLifecycle.Closing)
                        _lifecycle = ApplicationCommitCoordinatorLifecycle.Draining;
                }
                else
                {
                    current = null;
                    _running = null;
                    bool closing = _lifecycle is ApplicationCommitCoordinatorLifecycle.Closing or
                        ApplicationCommitCoordinatorLifecycle.Draining;
                    _lifecycle = closing ? ApplicationCommitCoordinatorLifecycle.Closed :
                        ApplicationCommitCoordinatorLifecycle.Accepting;
                    if (closing) _completion.TrySetResult();
                }
                // RunContinuationsAsynchronously makes terminal publication safe under the state gate and keeps
                // completion, slot release, diagnostics and selection of the next ordinal atomic to observers.
                completed.Complete(terminal);
            }
        }
    }

    private async ValueTask<ApplicationCommitTerminalResult> ExecuteAsync(CommitWorkItem item)
    {
        InspectionAppendResult append;
        try { append = await item.Commit().ConfigureAwait(false); }
        catch (Exception error)
        {
            return new(item.Ordinal, item.Kind, item.RunId, ApplicationCommitTerminalStatus.WriterFaulted, null,
                LastRevision(), InspectionRevision.Initial, error);
        }

        lock (_gate)
        {
            InspectionRevision previous = _lastConfirmedRevision;
            if (append.Status == InspectionAppendStatus.Duplicate)
                return new(item.Ordinal, item.Kind, item.RunId,
                    ApplicationCommitTerminalStatus.DuplicateUnverifiable, append, previous, append.Revision, null);
            if (append.Status != InspectionAppendStatus.Committed)
                return new(item.Ordinal, item.Kind, item.RunId, ApplicationCommitTerminalStatus.WriterRejected,
                    append, previous, append.Revision, null);
            if (_revisionRun.IsValid && _revisionRun != item.RunId)
                return new(item.Ordinal, item.Kind, item.RunId, ApplicationCommitTerminalStatus.WriterRejected,
                    append, previous, append.Revision,
                    new InvalidOperationException("A commit coordinator cannot mix acquisition runs."));
            if (append.Revision.Value < previous.Value)
                return new(item.Ordinal, item.Kind, item.RunId, ApplicationCommitTerminalStatus.RevisionRegression,
                    append, previous, append.Revision, null);
            if (append.Revision == previous && append.Status == InspectionAppendStatus.Committed)
                return new(item.Ordinal, item.Kind, item.RunId, ApplicationCommitTerminalStatus.NonAdvancingRevision,
                    append, previous, append.Revision, null);

            _revisionRun = item.RunId;
            if (append.Revision.Value > previous.Value) _lastConfirmedRevision = append.Revision;
            return new(item.Ordinal, item.Kind, item.RunId, ApplicationCommitTerminalStatus.Confirmed, append,
                previous, append.Revision, null);
        }
    }

    private InspectionRevision LastRevision() { lock (_gate) { return _lastConfirmedRevision; } }
    private int OccupancyUnderLock() => _queue.Count + (_running is null ? 0 : 1);

    private sealed class CommitWorkItem
    {
        private readonly TaskCompletionSource<ApplicationCommitTerminalResult> _completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal CommitWorkItem(ulong ordinal, ApplicationCommitKind kind, AcquisitionRunId runId,
            Func<ValueTask<InspectionAppendResult>> commit)
        {
            Ordinal = ordinal; Kind = kind; RunId = runId; Commit = commit;
            Admission = new(ordinal, kind, runId, _completion.Task);
        }
        internal ulong Ordinal { get; }
        internal ApplicationCommitKind Kind { get; }
        internal AcquisitionRunId RunId { get; }
        internal Func<ValueTask<InspectionAppendResult>> Commit { get; }
        internal ApplicationCommitAdmission Admission { get; }
        internal void Complete(ApplicationCommitTerminalResult result) => _completion.TrySetResult(result);
    }

    private sealed class DrainRegistration
    {
        private readonly TaskCompletionSource<Task> _started =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Task _completion;

        private DrainRegistration(bool completed)
        {
            _completion = _started.Task.Unwrap();
            if (completed) _started.TrySetResult(Task.CompletedTask);
        }

        internal DrainRegistration() : this(completed: false) { }
        internal static DrainRegistration Completed { get; } = new(completed: true);
        internal Task Completion => _completion;

        internal void Start(Task execution) => _started.TrySetResult(execution);
    }
}
