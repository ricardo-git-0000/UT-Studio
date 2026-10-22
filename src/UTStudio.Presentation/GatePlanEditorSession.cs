using UTStudio.Contracts.Processing;

namespace UTStudio.Presentation;

/// <summary>
/// Headless draft owner for Preview and Cancel. Committed state remains borrowed from Application. Dispose closes
/// event admission without waiting for admitted callbacks; completion remains FIFO behind admitted values and may
/// run later. Unsubscribe prevents new admission but cannot cancel a callback already running. A queued work item
/// retains its subscription through its single drainer; slow and throwing observers are isolated.
/// </summary>
public sealed class GatePlanEditorSession : IObservable<GatePlanEditorSnapshot>, IDisposable
{
    private readonly object _gate = new();
    private readonly GatePlanAuthorityKey _authorityKey;
    private readonly IGatePlanAuthorityReader _authority;
    private readonly IGatePlanPreviewCompiler _previewCompiler;
    private readonly SnapshotStream _snapshots;
    private readonly GatePreviewWorkspace _previewWorkspace = new();
    private readonly IDisposable _authoritySubscription;
    private readonly Queue<GatePlanEditorSnapshot> _publicationQueue = [];
    private GatePlanEditorSnapshot _current;
    private GatePlanAuthoritySnapshot _authorityCurrent;
    private ulong _editorGeneration;
    private EditorialInvalidation _lastInvalidation;
    private bool _publishing;
    private bool _completeAfterPublish;
    private bool _disposed;

    public GatePlanEditorSession(GatePlanAuthorityKey authorityKey, IGatePlanAuthorityReader authority,
        IGatePlanPreviewCompiler previewCompiler)
    {
        if (!authorityKey.IsValid) throw new ArgumentException("A valid authority key is required.", nameof(authorityKey));
        ArgumentNullException.ThrowIfNull(authority); ArgumentNullException.ThrowIfNull(previewCompiler);
        _authorityKey = authorityKey; _authority = authority; _previewCompiler = previewCompiler;
        _authorityCurrent = authority.Read(authorityKey);
        CommittedGatePlanSnapshot committed = _authorityCurrent.Committed;
        _current = new(committed, state: GatePlanEditorState.Committed);
        _snapshots = new(_current);
        _authoritySubscription = authority.Observe(authorityKey).Subscribe(new AuthorityObserver(this));
    }

    public GatePlanEditorSnapshot Snapshot { get { lock (_gate) return _current; } }
    internal event Action? AuthorityInvalidated;
    internal event Action<GatePlanEditorSnapshot>? EditorialChanged;
    public IDisposable Subscribe(IObserver<GatePlanEditorSnapshot> observer) => _snapshots.Subscribe(observer);

    public GatePlanEditorSnapshot BeginEdit()
    {
        GatePlanEditorSnapshot next;
        bool publish;
        lock (_gate)
        {
            ThrowIfDisposed();
            _editorGeneration++;
            _lastInvalidation = EditorialInvalidation.Superseded;
            _previewWorkspace.Clear();
            CommittedGatePlanSnapshot committed = _current.Committed;
            var draft = new GatePlanDraft(committed.AuthorityKey, committed.Revision,
                committed.Plan.SchemaVersion, committed.Plan.AlgorithmVersion, committed.Plan.Gates);
            next = new(committed, draft, state: GatePlanEditorState.Editing);
            publish = EnqueueUnderLock(next);
        }
        if (publish) DrainPublications();
        EditorialChanged?.Invoke(next);
        return next;
    }

    public GatePlanEditorSnapshot UpdateDraft(GatePlanDraft draft)
    {
        ArgumentNullException.ThrowIfNull(draft);
        GatePlanEditorSnapshot next;
        bool publish;
        lock (_gate)
        {
            ThrowIfDisposed();
            _editorGeneration++;
            _lastInvalidation = EditorialInvalidation.Superseded;
            _previewWorkspace.ClearCandidate();
            GatePlanDraft existing = _current.Draft ??
                throw new InvalidOperationException("BeginEdit must create a draft before it can be updated.");
            if (draft.Authority != _authorityKey || draft.BaseRevision != existing.BaseRevision)
                throw new ArgumentException("The draft does not belong to this editorial session.", nameof(draft));
            if (draft.BaseRevision != _current.Committed.Revision)
                throw new InvalidOperationException("A stale draft cannot be edited.");
            EnsureOnlyPreviewFieldsChanged(_current.Committed.Plan, draft);
            GatePlanEditorState state = draft.BaseRevision == _current.Committed.Revision ?
                GatePlanEditorState.Editing : GatePlanEditorState.Stale;
            next = new(_current.Committed, draft, state: state);
            publish = EnqueueUnderLock(next);
        }
        if (publish) DrainPublications();
        EditorialChanged?.Invoke(next);
        return next;
    }

    public GatePlanEditResult Preview()
    {
        GatePlanDraft draft;
        CommittedGatePlanSnapshot observedCommitted;
        GateAuthorityGeneration authorityGeneration;
        ulong operationGeneration;
        GatePlanEditorSnapshot? conflictSnapshot = null;
        GatePlanEditResult? conflictResult = null;
        bool publish = false;
        lock (_gate)
        {
            ThrowIfDisposed();
            draft = _current.Draft ?? throw new InvalidOperationException("Preview requires an active draft.");
            observedCommitted = _current.Committed;
            authorityGeneration = _authorityCurrent.Generation;
            operationGeneration = ++_editorGeneration;
            _lastInvalidation = EditorialInvalidation.None;
            if (draft.BaseRevision != observedCommitted.Revision)
            {
                (conflictSnapshot, conflictResult) = CreateConflict(draft, observedCommitted);
                publish = EnqueueUnderLock(conflictSnapshot);
            }
        }
        if (conflictResult is not null)
        { if (publish) DrainPublications(); EditorialChanged?.Invoke(conflictResult.Snapshot); return conflictResult; }

        GatePlanCompilationResult compilation = _previewCompiler.Compile(draft);
        GatePlanAuthorityReadStampResult stampResult = _authority.TryReadStamp(_authorityKey, authorityGeneration);
        GatePlanEditResult? result = null;
        GatePlanAuthoritySnapshot? authoritative = stampResult.Current;
        lock (_gate)
        {
            GatePlanEditorSnapshot next;
            if (_disposed)
            {
                next = _current.State == GatePlanEditorState.Closed ? _current :
                    new(_current.Committed, state: GatePlanEditorState.Closed);
                result = GatePlanEditResult.Closed(next, draft.BaseRevision);
            }
            else if (_editorGeneration != operationGeneration || !ReferenceEquals(_current.Draft, draft))
                result = _lastInvalidation == EditorialInvalidation.Cancelled ?
                    GatePlanEditResult.Cancelled(new(_current.Committed, state: GatePlanEditorState.Cancelled)) :
                    GatePlanEditResult.Superseded(_current, draft.BaseRevision);
            else if (stampResult.Status == GatePlanAuthorityReadStampStatus.Closed)
            {
                next = new(_current.Committed, state: GatePlanEditorState.Closed);
                result = GatePlanEditResult.Closed(next, draft.BaseRevision);
                publish = EnqueueUnderLock(next);
            }
            else if (stampResult.Status != GatePlanAuthorityReadStampStatus.Acquired || authoritative is null ||
                authoritative.Generation != authorityGeneration || !authoritative.Committed.Equals(observedCommitted) ||
                stampResult.Stamp.PublicationSequence.Value < _authorityCurrent.PublicationSequence.Value)
            {
                authoritative = _authorityCurrent.PublicationSequence.Value > (authoritative?.PublicationSequence.Value ?? 0) ?
                    _authorityCurrent : authoritative ?? _authorityCurrent;
                _authorityCurrent = authoritative;
                (next, result) = CreateConflict(draft, authoritative.Committed);
                publish = EnqueueUnderLock(next);
            }
            else if (compilation.IsCompiled)
            {
                next = new(_current.Committed, draft, state: GatePlanEditorState.Previewed);
                _previewWorkspace.Install(draft, compilation.CompiledPlan!);
                result = GatePlanEditResult.Previewed(next);
                publish = EnqueueUnderLock(next);
            }
            else
            {
                next = new(_current.Committed, draft, compilation.Errors, GatePlanEditorState.ValidationFailed);
                result = GatePlanEditResult.ValidationFailed(next, compilation.Errors);
                publish = EnqueueUnderLock(next);
            }
        }
        if (publish) DrainPublications();
        EditorialChanged?.Invoke(result!.Snapshot);
        return result!;
    }

    public GatePlanEditResult Cancel()
    {
        GatePlanEditorSnapshot next;
        bool publish;
        lock (_gate)
        {
            ThrowIfDisposed();
            _editorGeneration++;
            _lastInvalidation = EditorialInvalidation.Cancelled;
            _previewWorkspace.Clear();
            next = new(_current.Committed, state: GatePlanEditorState.Cancelled);
            publish = EnqueueUnderLock(next);
        }
        if (publish) DrainPublications();
        EditorialChanged?.Invoke(next);
        return GatePlanEditResult.Cancelled(next);
    }

    public void Dispose()
    {
        GatePlanEditorSnapshot? closed = null;
        bool publish;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _editorGeneration++;
            _lastInvalidation = EditorialInvalidation.Closed;
            _previewWorkspace.Clear();
            closed = new(_current.Committed, state: GatePlanEditorState.Closed);
            _completeAfterPublish = true;
            publish = EnqueueUnderLock(closed);
        }
        _authoritySubscription.Dispose();
        if (publish) DrainPublications();
        EditorialChanged?.Invoke(closed);
    }

    private static (GatePlanEditorSnapshot Snapshot, GatePlanEditResult Result) CreateConflict(GatePlanDraft draft,
        CommittedGatePlanSnapshot committed)
    {
        var stale = new GatePlanEditorSnapshot(committed, draft, state: GatePlanEditorState.Stale);
        GatePlanEditResult result = GatePlanEditResult.Conflict(draft.BaseRevision, stale);
        return (stale, result);
    }

    private static void EnsureOnlyPreviewFieldsChanged(GatePlan committed, GatePlanDraft draft)
    {
        if (draft.SchemaVersion != committed.SchemaVersion || draft.AlgorithmVersion != committed.AlgorithmVersion ||
            draft.Gates.Count != committed.Gates.Count)
            throw new ArgumentException("Preview may change only gate windows and thresholds.", nameof(draft));
        for (int index = 0; index < committed.Gates.Count; index++)
        {
            GateDefinition baseline = committed.Gates[index];
            GateDefinition candidate = draft.Gates[index];
            if (candidate.Id != baseline.Id || candidate.Revision != baseline.Revision ||
                candidate.SignalDomain != baseline.SignalDomain ||
                !SameReference(candidate.Window.StartReference, baseline.Window.StartReference) ||
                candidate.Detector.Kind != baseline.Detector.Kind ||
                !SameHysteresis(candidate.Detector.Hysteresis, baseline.Detector.Hysteresis) ||
                !SameLocalExtrema(candidate.Detector.LocalExtrema, baseline.Detector.LocalExtrema) ||
                !candidate.RequestedMeasures.SequenceEqual(baseline.RequestedMeasures))
                throw new ArgumentException("Preview may change only gate windows and thresholds.", nameof(draft));
        }
    }

    private static bool SameReference(GateTimeReference left, GateTimeReference right) =>
        left.Kind == right.Kind && left.DependencyGateId == right.DependencyGateId;

    private static bool SameHysteresis(GateHysteresis? left, GateHysteresis? right) =>
        left is null ? right is null : right is not null &&
        left.LowNormalized.Equals(right.LowNormalized) && left.HighNormalized.Equals(right.HighNormalized);

    private static bool SameLocalExtrema(GateLocalExtremaParameters? left, GateLocalExtremaParameters? right) =>
        left is null ? right is null : right is not null &&
        left.NeighborhoodRadiusSamples == right.NeighborhoodRadiusSamples &&
        left.MinimumProminenceCounts.Equals(right.MinimumProminenceCounts) &&
        left.MinimumDistanceSamples == right.MinimumDistanceSamples && left.PlateauPolicy == right.PlateauPolicy;

    private void ReceiveCommitted(GatePlanAuthoritySnapshot authoritative)
    {
        CommittedGatePlanSnapshot committed = authoritative.Committed;
        GatePlanEditorSnapshot? next = null;
        bool publish = false;
        bool invalidate;
        lock (_gate)
        {
            if (_disposed || committed.AuthorityKey != _authorityKey ||
                authoritative.PublicationSequence.Value <= _authorityCurrent.PublicationSequence.Value) return;
            _authorityCurrent = authoritative;
            _previewWorkspace.Clear();
            invalidate = true;
            if (!committed.Equals(_current.Committed))
            {
                GatePlanDraft? draft = _current.Draft;
                next = draft is null ? new(committed, state: GatePlanEditorState.Committed) :
                    new(committed, draft, state: draft.BaseRevision == committed.Revision ?
                        GatePlanEditorState.Editing : GatePlanEditorState.Stale);
                publish = EnqueueUnderLock(next);
            }
        }
        if (invalidate) AuthorityInvalidated?.Invoke();
        if (publish) DrainPublications();
        if (next is not null) EditorialChanged?.Invoke(next);
    }

    private bool EnqueueUnderLock(GatePlanEditorSnapshot snapshot)
    {
        _current = snapshot; _publicationQueue.Enqueue(snapshot);
        if (_publishing) return false;
        _publishing = true; return true;
    }

    private void DrainPublications()
    {
        while (true)
        {
            GatePlanEditorSnapshot? next;
            bool complete;
            lock (_gate)
            {
                if (_publicationQueue.TryDequeue(out next)) complete = false;
                else { _publishing = false; complete = _completeAfterPublish; }
            }
            if (next is not null) { _snapshots.Publish(next); continue; }
            if (complete) _snapshots.Complete();
            return;
        }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    internal GatePreviewArtifact? BorrowPreviewCandidate()
    {
        lock (_gate)
        {
            if (_disposed || _current.State != GatePlanEditorState.Previewed ||
                _previewWorkspace.Candidate is not { } artifact ||
                !ReferenceEquals(_current.Draft, artifact.Checkpoint)) return null;
            return artifact;
        }
    }

    internal bool IsCurrentPreviewCandidate(GatePreviewArtifact artifact)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        lock (_gate) return !_disposed && _previewWorkspace.IsCurrentCandidate(artifact) &&
            ReferenceEquals(_current.Draft, artifact.Checkpoint);
    }

    internal bool PromotePreview(GatePreviewArtifact artifact)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        lock (_gate) return !_disposed && ReferenceEquals(_current.Draft, artifact.Checkpoint) &&
            _previewWorkspace.Promote(artifact);
    }

    internal void DiscardPreviewCandidate(GatePreviewArtifact artifact)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        lock (_gate)
            if (_previewWorkspace.IsCurrentCandidate(artifact)) _previewWorkspace.ClearCandidate();
    }

    internal bool RestoreStablePreview(GatePreviewArtifact artifact)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        GatePlanEditorSnapshot next; bool publish;
        lock (_gate)
        {
            if (_disposed || !ReferenceEquals(_previewWorkspace.Stable, artifact) ||
                artifact.Checkpoint.BaseRevision != _current.Committed.Revision) return false;
            _editorGeneration++; _lastInvalidation = EditorialInvalidation.Superseded;
            _previewWorkspace.ClearCandidate();
            next = new(_current.Committed, artifact.Checkpoint, state: GatePlanEditorState.Previewed);
            publish = EnqueueUnderLock(next);
        }
        if (publish) DrainPublications();
        return true;
    }

    private enum EditorialInvalidation { None, Cancelled, Superseded, Closed }

    private sealed class AuthorityObserver(GatePlanEditorSession owner) : IObserver<GatePlanAuthoritySnapshot>
    {
        public void OnNext(GatePlanAuthoritySnapshot value) => owner.ReceiveCommitted(value);
        public void OnError(Exception error) { }
        public void OnCompleted() { }
    }

    private sealed class SnapshotStream(GatePlanEditorSnapshot initial) : IObservable<GatePlanEditorSnapshot>
    {
        private readonly object _gate = new();
        private readonly List<Subscription> _subscriptions = [];
        private GatePlanEditorSnapshot _current = initial;
        private bool _completed;
        public IDisposable Subscribe(IObserver<GatePlanEditorSnapshot> observer)
        {
            ArgumentNullException.ThrowIfNull(observer);
            Subscription subscription;
            bool drain;
            lock (_gate)
            {
                subscription = new(this, observer);
                if (_completed) drain = subscription.EnqueueCompleted();
                else { _subscriptions.Add(subscription); drain = subscription.EnqueueNext(_current); }
            }
            if (drain) subscription.Schedule();
            return subscription;
        }
        internal void Publish(GatePlanEditorSnapshot snapshot)
        {
            List<Subscription> drains;
            lock (_gate)
            {
                if (_completed) return;
                _current = snapshot;
                drains = _subscriptions.Where(subscription => subscription.EnqueueNext(snapshot)).ToList();
            }
            foreach (Subscription drain in drains) drain.Schedule();
        }
        internal void Complete()
        {
            List<Subscription> drains;
            lock (_gate)
            {
                if (_completed) return;
                _completed = true;
                drains = _subscriptions.Where(subscription => subscription.EnqueueCompleted()).ToList();
                _subscriptions.Clear();
            }
            foreach (Subscription drain in drains) drain.Schedule();
        }
        private void Remove(Subscription subscription) { lock (_gate) _subscriptions.Remove(subscription); }

        private sealed class Subscription(SnapshotStream owner, IObserver<GatePlanEditorSnapshot> observer) : IDisposable
        {
            private readonly object _gate = new();
            private readonly Queue<GatePlanEditorSnapshot> _pending = [];
            private bool _completionPending;
            private bool _draining;
            private bool _disposed;
            internal bool EnqueueNext(GatePlanEditorSnapshot snapshot)
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
            internal void Drain()
            {
                while (true)
                {
                    GatePlanEditorSnapshot? next;
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
                        if (complete) { observer.OnCompleted(); return; }
                        observer.OnNext(next!);
                    }
                    catch { Dispose(); return; }
                }
            }
            internal void Schedule() => ThreadPool.UnsafeQueueUserWorkItem(
                static subscription => subscription.Drain(), this, preferLocal: false);
            public void Dispose()
            {
                lock (_gate)
                {
                    if (_disposed) return;
                    _disposed = true; _pending.Clear(); _completionPending = false;
                }
                owner.Remove(this);
            }
        }
    }
}
