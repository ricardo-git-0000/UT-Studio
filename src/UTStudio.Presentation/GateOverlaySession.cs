using UTStudio.Contracts.Inspection;
using UTStudio.Contracts.Processing;
using UTStudio.Domain.Processing;
using UTStudio.Visualization.Core;

namespace UTStudio.Presentation;

public enum GateOverlayRequestOutcome { Completed, Superseded, NotAvailable, CapacityExceeded, Cancelled, Closed, Faulted }
public enum GateOverlayKeyIntent { FocusPrevious, FocusNext, Decrement, Increment, ConfirmPreview, CancelCapture }

/// <summary>
/// Portable, bounded overlay coordinator. It owns at most one active evaluation and one replaceable pending
/// request. Its cancellation tokens only cancel visual reads and never flow to productive processing.
/// </summary>
public sealed class GateOverlaySession : IAsyncDisposable
{
    private const double BoundaryHandleInsetDip = 8;
    private readonly object _gate = new();
    private readonly object _previewGate = new();
    private readonly GatePlanEditorSession _editor;
    private readonly IGateGeometryEvaluator _evaluator;
    private readonly Queue<GateOverlayVisualSnapshot> _publications = [];
    private AScanSnapshot? _signal;
    private AScanCursorState? _cursors;
    private GateVisualTransform? _transform;
    private GateOverlayVisualSnapshot _overlay;
    private GatePreviewArtifact? _stableArtifact;
    private GateCommittedArtifact? _stableCommittedArtifact;
    private GateGeometrySnapshot? _stableGeometry;
    private GateGeometrySnapshot? _committedGeometry;
    private EvaluationRequest? _pending;
    private CancellationTokenSource? _activeCancellation;
    private Task? _runner;
    private GateId? _selectedGate;
    private GateHandleKind _focusedHandle;
    private GateCaptureGeneration? _capture;
    private GateHandleVisualState? _capturedHandleState;
    private PortablePointerCaptureOwner _gestureOwner;
    private ulong _contextGeneration = 1, _captureNonce, _evaluationSequence;
    private bool _publishing, _closed;
    private GatePlanDraft? _ownedDraft;
    private GatePlanEditorSnapshot _lastEditorSnapshot;

    public GateOverlaySession(GatePlanEditorSession editor, IGateGeometryEvaluator evaluator)
    {
        _editor = editor ?? throw new ArgumentNullException(nameof(editor));
        _evaluator = evaluator ?? throw new ArgumentNullException(nameof(evaluator));
        _lastEditorSnapshot = editor.Snapshot;
        _overlay = Empty(GateOverlayGeometryStatus.NotRequested, _contextGeneration);
        _editor.AuthorityInvalidated += ReceiveAuthorityInvalidated;
        _editor.EditorialChanged += ReceiveEditorSnapshot;
    }

    public event Action<GateOverlayVisualSnapshot>? SnapshotChanged;
    public GateOverlayVisualSnapshot Snapshot { get { lock (_gate) return _overlay; } }
    public AScanCompositeVisualSnapshot? CompositeSnapshot
    {
        get
        {
            lock (_gate)
                return _signal is { } signal && _transform is { } transform
                    ? new(signal, _cursors, _overlay, transform) : null;
        }
    }
    public PortablePointerCaptureOwner GestureOwner { get { lock (_gate) return _gestureOwner; } }

    public bool SetVisualContext(AScanSnapshot signal, AScanCursorState? cursors, GateVisualTransform transform)
    {
        ArgumentNullException.ThrowIfNull(signal);
        GateOverlayVisualSnapshot publish;
        CancellationTokenSource? cancellation = null;
        lock (_gate)
        {
            ThrowIfClosed();
            bool invalidating = _signal?.TraceIdentity != signal.TraceIdentity ||
                _signal?.InspectionRevision != signal.InspectionRevision ||
                _signal?.Metadata.RunId != signal.Metadata.RunId || _signal?.Version != signal.Version ||
                _transform is null || !_transform.Value.Equals(transform);
            bool visualChanged = invalidating || !ReferenceEquals(_signal, signal) ||
                !Equals(_cursors, cursors);
            if (!visualChanged) return false;
            _signal = signal; _cursors = cursors; _transform = transform;
            if (!invalidating)
            {
                publish = BuildOverlayUnderLock(_overlay.Status, _overlay.Source,
                _overlay.TraceIdentity, _overlay.InspectionRevision, _stableGeometry, _overlay.EvaluationSequence,
                _overlay.ErrorCode);
            }
            else
            {
                cancellation = AdvanceContextUnderLock();
                GateOverlayGeometryStatus status = CompatibleGeometryUnderLock(_stableGeometry)
                    ? GateOverlayGeometryStatus.Current : GateOverlayGeometryStatus.Stale;
                publish = BuildOverlayUnderLock(status, _stableGeometry?.Source,
                    _stableGeometry?.TraceIdentity, _stableGeometry?.InspectionRevision ?? InspectionRevision.Initial,
                    status == GateOverlayGeometryStatus.Current ? _stableGeometry : null,
                    _evaluationSequence, null);
            }
            SetSnapshotUnderLock(publish);
        }
        RequestCancellation(cancellation);
        Publish(publish);
        return true;
    }

    public async ValueTask<GateOverlayRequestOutcome> PreviewAsync(GatePlanDraft draft,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(draft);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate) ThrowIfClosed();
        GatePlanEditResult preview;
        GatePreviewArtifact? artifact = null;
        lock (_previewGate)
        {
            lock (_gate)
            {
                if (_closed) return GateOverlayRequestOutcome.Closed;
                _ownedDraft = draft;
            }
            _editor.UpdateDraft(draft);
            preview = _editor.Preview();
            if (preview.Outcome == GatePlanEditOutcome.Previewed)
                artifact = _editor.BorrowPreviewCandidate() ??
                    throw new InvalidOperationException("A successful Preview must retain its exact compiled artifact.");
        }
        if (preview.Outcome != GatePlanEditOutcome.Previewed) return preview.Outcome switch
        {
            GatePlanEditOutcome.Closed => GateOverlayRequestOutcome.Closed,
            GatePlanEditOutcome.Superseded or GatePlanEditOutcome.Conflict => GateOverlayRequestOutcome.Superseded,
            _ => GateOverlayRequestOutcome.NotAvailable
        };
        return await EnqueueAsync(artifact!, cancellationToken).ConfigureAwait(false);
    }

    public ValueTask<GateOverlayRequestOutcome> RequestCommittedAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        GateCommittedArtifact? artifact = _editor.BorrowCommittedArtifact();
        if (artifact is null) return ValueTask.FromResult(GateOverlayRequestOutcome.Closed);
        return EnqueueAsync(artifact, cancellationToken);
    }

    public ValueTask<GateOverlayRequestOutcome> RequestEditingAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        GatePreviewArtifact? preview = _editor.BorrowApplicablePreview();
        if (preview is not null) return EnqueueAsync(preview, cancellationToken);
        return RequestCommittedAsync(cancellationToken);
    }

    public bool BeginEditing(GatePlanDraft draft)
    {
        ArgumentNullException.ThrowIfNull(draft);
        GateOverlayVisualSnapshot publish;
        lock (_gate)
        {
            if (_closed || !ReferenceEquals(_editor.Snapshot.Draft, draft)) return false;
            _ownedDraft = draft;
            GateDefinition? first = draft.Gates.FirstOrDefault();
            if (first is not null)
            {
                _selectedGate = first.Id;
                _focusedHandle = GateHandleKind.Start;
            }
            GateGeometrySnapshot? committed = CompatibleGeometryUnderLock(_committedGeometry)
                ? _committedGeometry : null;
            GateOverlayGeometryStatus status = committed is not null
                ? GateOverlayGeometryStatus.Current : GateOverlayGeometryStatus.Stale;
            _stableArtifact = null;
            _stableGeometry = committed;
            publish = BuildOverlayUnderLock(status, committed?.Source,
                committed?.TraceIdentity, committed?.InspectionRevision ?? InspectionRevision.Initial,
                committed,
                _evaluationSequence, null);
            SetSnapshotUnderLock(publish);
        }
        Publish(publish);
        return publish.Handles.Count != 0;
    }

    public async ValueTask<GateOverlayRequestOutcome> PreviewHandleValueAsync(GateId gateId,
        GateHandleKind kind, double value, CancellationToken cancellationToken = default)
    {
        if (!gateId.IsValid || kind == GateHandleKind.Unknown || !double.IsFinite(value))
            return GateOverlayRequestOutcome.NotAvailable;
        GatePlanDraft next;
        lock (_gate)
        {
            if (_closed) return GateOverlayRequestOutcome.Closed;
            GatePlanDraft? draft = _editor.Snapshot.Draft;
            GateDefinition? definition = draft?.Gates.FirstOrDefault(x => x.Id == gateId);
            if (draft is null || definition is null || !TryGetEditRangeUnderLock(definition, kind,
                out double minimum, out double maximum) || value < minimum || value > maximum)
                return GateOverlayRequestOutcome.NotAvailable;
            _selectedGate = gateId; _focusedHandle = kind;
            next = ChangeDraft(draft, gateId, kind, value);
        }
        return await PreviewAsync(next, cancellationToken).ConfigureAwait(false);
    }

    private bool TryGetEditRangeUnderLock(GateDefinition definition, GateHandleKind kind,
        out double minimum, out double maximum)
    {
        minimum = maximum = default;
        if (_transform is not { } transform) return false;
        double step = transform.TimeViewport.VisibleSpanSeconds / 1000d;
        double limit = transform.TimeViewport.DomainSpanSeconds;
        bool absolute = definition.Window.StartReference.Kind == GateTimeReferenceKind.TraceOrigin;
        double start = definition.Window.StartOffsetSeconds;
        double end = start + definition.Window.DurationSeconds;
        switch (kind)
        {
            case GateHandleKind.Start:
                minimum = absolute ? transform.TimeViewport.DomainMinimumSeconds : Math.Min(-limit, start);
                maximum = end - step; return true;
            case GateHandleKind.End:
                minimum = start + step;
                maximum = absolute ? transform.TimeViewport.DomainMaximumSeconds : Math.Max(limit, end);
                return true;
            case GateHandleKind.Threshold when definition.Detector.Threshold is not null:
                minimum = 0; maximum = 1; return true;
            default:
                return false;
        }
    }

    public bool Select(GateId gateId, GateHandleKind handle)
    {
        GateOverlayVisualSnapshot publish;
        CancellationTokenSource? cancellation = null;
        lock (_gate)
        {
            if (_closed || !gateId.IsValid || handle == GateHandleKind.Unknown ||
                !_overlay.Handles.Any(x => x.GateId == gateId && x.Kind == handle && x.IsEnabled)) return false;
            if (_selectedGate != gateId || _focusedHandle != handle)
                cancellation = AdvanceContextUnderLock();
            _selectedGate = gateId; _focusedHandle = handle;
            publish = RebuildCurrentUnderLock(); SetSnapshotUnderLock(publish);
        }
        RequestCancellation(cancellation);
        Publish(publish); return true;
    }

    public bool SelectGate(GateId gateId)
    {
        if (!gateId.IsValid) return false;
        GateOverlayVisualSnapshot publish;
        lock (_gate)
        {
            GatePlanDraft? draft = _editor.Snapshot.Draft;
            if (_closed || draft is null || !draft.Gates.Any(x => x.Id == gateId)) return false;
            _selectedGate = gateId; _focusedHandle = GateHandleKind.Start;
            _capture = null; _capturedHandleState = null; _gestureOwner = PortablePointerCaptureOwner.None;
            publish = BuildOverlayUnderLock(_overlay.Status, _overlay.Source, _overlay.TraceIdentity,
                _overlay.InspectionRevision, _stableGeometry, _evaluationSequence, _overlay.ErrorCode);
            SetSnapshotUnderLock(publish);
        }
        Publish(publish); return true;
    }

    public GateCaptureGeneration? BeginGateCapture()
    {
        GateOverlayVisualSnapshot? publish = null;
        GateCaptureGeneration? capture;
        lock (_gate)
        {
            if (_closed || _gestureOwner != PortablePointerCaptureOwner.None ||
                _selectedGate is not { } selected || _focusedHandle == GateHandleKind.Unknown ||
                _signal?.TraceIdentity is not { IsValid: true } trace || _transform is not { } transform ||
                ResolveCaptureSourceUnderLock() is not { } source) return null;
            GateGeometryItemSnapshot? gate = _stableGeometry?.Gates.FirstOrDefault(x => x.GateId == selected);
            GateId anchorId = gate?.Anchor?.GateId ?? default;
            GateRevision anchorRevision = gate?.Anchor?.GateRevision ?? default;
            _captureNonce = checked(_captureNonce + 1);
            capture = new(_contextGeneration, _captureNonce, _signal.Metadata.RunId, trace,
                source.Kind, source.Revision, source.Preview, selected, _focusedHandle,
                anchorId, anchorRevision, transform.Version);
            if (!capture.Value.IsValid) return null;
            _gestureOwner = PortablePointerCaptureOwner.Gate; _capture = capture;
            _capturedHandleState = _overlay.Handles.First(x => x.GateId == selected && x.Kind == _focusedHandle);
            publish = RebuildCurrentUnderLock(); SetSnapshotUnderLock(publish);
        }
        Publish(publish); return capture;
    }

    public bool TryBeginCursorCapture()
    {
        lock (_gate)
        {
            if (_closed || _gestureOwner != PortablePointerCaptureOwner.None) return false;
            _gestureOwner = PortablePointerCaptureOwner.Cursor; return true;
        }
    }

    public void EndCapture()
    {
        GateOverlayVisualSnapshot? publish = null;
        lock (_gate)
        {
            if (_gestureOwner == PortablePointerCaptureOwner.None) return;
            _gestureOwner = PortablePointerCaptureOwner.None; _capture = null; _capturedHandleState = null;
            publish = RebuildCurrentUnderLock(); SetSnapshotUnderLock(publish);
        }
        Publish(publish);
    }

    public async ValueTask<GateOverlayRequestOutcome> MoveCapturedHandleAsync(double value,
        CancellationToken cancellationToken = default)
    {
        GatePlanDraft next;
        lock (_gate)
        {
            if (_closed) return GateOverlayRequestOutcome.Closed;
            if (_gestureOwner != PortablePointerCaptureOwner.Gate || _capture is not { } capture ||
                capture.ContextGeneration != _contextGeneration || _editor.Snapshot.Draft is not { } draft)
                return GateOverlayRequestOutcome.Superseded;
            GateHandleVisualState? handle = _capturedHandleState is { } captured &&
                captured.GateId == capture.GateId && captured.Kind == capture.Handle ? captured :
                _overlay.Handles.FirstOrDefault(x => x.GateId == capture.GateId && x.Kind == capture.Handle);
            if (handle is null) return GateOverlayRequestOutcome.NotAvailable;
            double clamped = Math.Clamp(value, handle.Minimum, handle.Maximum);
            _capturedHandleState = new(handle.GateId, handle.GateRevision, handle.Kind, handle.X, handle.Y,
                clamped, handle.Unit, handle.Minimum, handle.Maximum, handle.Step,
                handle.IsSelected, handle.IsFocused, handle.IsEnabled, handle.AnchorGateId, handle.AnchorRevision);
            next = ChangeDraft(draft, capture.GateId, capture.Handle, clamped);
        }
        // Draft changes caused by this capture intentionally do not advance the external context or nonce.
        return await PreviewAsync(next, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<GateOverlayRequestOutcome> HandleKeyAsync(GateOverlayKeyIntent intent,
        CancellationToken cancellationToken = default)
    {
        if (!Enum.IsDefined(intent)) throw new ArgumentOutOfRangeException(nameof(intent));
        if (intent is GateOverlayKeyIntent.FocusPrevious or GateOverlayKeyIntent.FocusNext)
        {
            GateOverlayVisualSnapshot? publish = null;
            CancellationTokenSource? cancellation = null;
            lock (_gate)
            {
                if (_closed) return GateOverlayRequestOutcome.Closed;
                GateHandleVisualState[] ordered = _overlay.Handles.OrderBy(x => x.GateId.Value)
                    .ThenBy(x => x.Kind).ToArray();
                if (ordered.Length == 0) return GateOverlayRequestOutcome.NotAvailable;
                int current = Array.FindIndex(ordered, x => x.GateId == _selectedGate && x.Kind == _focusedHandle);
                int delta = intent == GateOverlayKeyIntent.FocusNext ? 1 : -1;
                int target = current < 0 ? (delta > 0 ? 0 : ordered.Length - 1) :
                    (current + delta + ordered.Length) % ordered.Length;
                if (_selectedGate != ordered[target].GateId || _focusedHandle != ordered[target].Kind)
                    cancellation = AdvanceContextUnderLock();
                _selectedGate = ordered[target].GateId; _focusedHandle = ordered[target].Kind;
                publish = RebuildCurrentUnderLock(); SetSnapshotUnderLock(publish);
            }
            RequestCancellation(cancellation);
            Publish(publish); return GateOverlayRequestOutcome.Completed;
        }
        if (intent == GateOverlayKeyIntent.CancelCapture)
            return RestoreStablePreview() ? GateOverlayRequestOutcome.Completed : GateOverlayRequestOutcome.NotAvailable;
        if (intent == GateOverlayKeyIntent.ConfirmPreview)
        {
            lock (_gate)
                if (_pending is not null || _runner is not null || _stableArtifact is null ||
                    !ReferenceEquals(_editor.Snapshot.Draft, _stableArtifact.Checkpoint))
                    return GateOverlayRequestOutcome.NotAvailable;
            EndCapture(); return GateOverlayRequestOutcome.Completed;
        }
        GateHandleVisualState? focused;
        lock (_gate) focused = _capturedHandleState is { IsEnabled: true } captured ? captured :
            _overlay.Handles.FirstOrDefault(x => x.GateId == _selectedGate && x.Kind == _focusedHandle && x.IsEnabled);
        if (focused is null) return GateOverlayRequestOutcome.NotAvailable;
        if (_capture is null && BeginGateCapture() is null) return GateOverlayRequestOutcome.NotAvailable;
        double deltaValue = intent == GateOverlayKeyIntent.Increment ? focused.Step : -focused.Step;
        return await MoveCapturedHandleAsync(focused.Value + deltaValue, cancellationToken).ConfigureAwait(false);
    }

    public bool RestoreStablePreview()
    {
        GatePreviewArtifact stable;
        lock (_gate)
        {
            if (_closed || _stableArtifact is not { } currentStable ||
                currentStable.Checkpoint.BaseRevision != _editor.Snapshot.Committed.Revision ||
                _signal?.TraceIdentity is not { IsValid: true } || _capture is { } capture &&
                    capture.ContextGeneration != _contextGeneration) return false;
            stable = currentStable;
        }
        bool restored = _editor.RestoreStablePreview(stable);
        EndCapture();
        return restored;
    }

    public async ValueTask DisposeAsync()
    {
        Task? runner; EvaluationRequest? pending;
        CancellationTokenSource? cancellation = null;
        GateOverlayVisualSnapshot closed;
        lock (_gate)
        {
            if (_closed) { runner = _runner; pending = null; }
            else
            {
                _closed = true; _capture = null; _capturedHandleState = null;
                _gestureOwner = PortablePointerCaptureOwner.None;
                cancellation = _activeCancellation;
                pending = _pending; _pending = null; runner = _runner;
                closed = BuildOverlayUnderLock(GateOverlayGeometryStatus.Closed, null, null,
                    InspectionRevision.Initial, null, _evaluationSequence, null);
                SetSnapshotUnderLock(closed);
            }
        }
        pending?.Completion.TrySetResult(GateOverlayRequestOutcome.Closed);
        RequestCancellation(cancellation);
        if (!_overlay.Equals(default) && _overlay.Status == GateOverlayGeometryStatus.Closed) Publish(_overlay);
        if (runner is not null) await runner.ConfigureAwait(false);
        _editor.AuthorityInvalidated -= ReceiveAuthorityInvalidated;
        _editor.EditorialChanged -= ReceiveEditorSnapshot;
        lock (_gate)
        {
            _activeCancellation?.Dispose(); _activeCancellation = null; _stableArtifact = null;
            _stableCommittedArtifact = null; _stableGeometry = null; _committedGeometry = null;
        }
    }

    private ValueTask<GateOverlayRequestOutcome> EnqueueAsync(GatePreviewArtifact artifact,
        CancellationToken cancellationToken)
    {
        EvaluationRequest request; EvaluationRequest? superseded;
        TaskCompletionSource? runnerCompletion = null;
        GateOverlayVisualSnapshot publish;
        lock (_gate)
        {
            if (_closed)
            {
                _editor.DiscardPreviewCandidate(artifact);
                return ValueTask.FromResult(GateOverlayRequestOutcome.Closed);
            }
            if (_signal?.TraceIdentity is not { IsValid: true } trace ||
                _signal.InspectionRevision == InspectionRevision.Initial || _transform is null)
                return ValueTask.FromResult(GateOverlayRequestOutcome.NotAvailable);
            request = new(checked(++_evaluationSequence), _contextGeneration, artifact, null,
                new(artifact.Source, trace, _signal.InspectionRevision, artifact.PreviewGuard),
                artifact.CompiledPlan, cancellationToken);
            superseded = _pending; _pending = request;
            if (_runner is null)
            {
                runnerCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
                _runner = runnerCompletion.Task;
            }
            publish = BuildOverlayUnderLock(GateOverlayGeometryStatus.Evaluating, artifact.Source, trace,
                _signal.InspectionRevision, null, request.Sequence, null);
            SetSnapshotUnderLock(publish);
        }
        superseded?.Completion.TrySetResult(GateOverlayRequestOutcome.Superseded);
        Publish(publish);
        if (runnerCompletion is not null) _ = CompleteRunnerAsync(runnerCompletion);
        return new(request.Completion.Task);
    }

    private ValueTask<GateOverlayRequestOutcome> EnqueueAsync(GateCommittedArtifact artifact,
        CancellationToken cancellationToken)
    {
        EvaluationRequest request; EvaluationRequest? superseded;
        TaskCompletionSource? runnerCompletion = null;
        GateOverlayVisualSnapshot publish;
        lock (_gate)
        {
            if (_closed) return ValueTask.FromResult(GateOverlayRequestOutcome.Closed);
            if (_signal?.TraceIdentity is not { IsValid: true } trace ||
                _signal.InspectionRevision == InspectionRevision.Initial || _transform is null)
                return ValueTask.FromResult(GateOverlayRequestOutcome.NotAvailable);
            request = new(checked(++_evaluationSequence), _contextGeneration, null, artifact,
                new(artifact.Source, trace, _signal.InspectionRevision), artifact.CompiledPlan, cancellationToken);
            superseded = _pending; _pending = request;
            if (_runner is null)
            {
                runnerCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
                _runner = runnerCompletion.Task;
            }
            publish = BuildOverlayUnderLock(GateOverlayGeometryStatus.Evaluating, artifact.Source, trace,
                _signal.InspectionRevision, null, request.Sequence, null);
            SetSnapshotUnderLock(publish);
        }
        superseded?.Completion.TrySetResult(GateOverlayRequestOutcome.Superseded);
        Publish(publish);
        if (runnerCompletion is not null) _ = CompleteRunnerAsync(runnerCompletion);
        return new(request.Completion.Task);
    }

    private async Task CompleteRunnerAsync(TaskCompletionSource completion)
    {
        try { await RunAsync().ConfigureAwait(false); }
        finally
        {
            completion.TrySetResult();
        }
    }

    private async Task RunAsync()
    {
        while (true)
        {
            EvaluationRequest? request; CancellationTokenSource cancellation;
            lock (_gate)
            {
                request = _pending; _pending = null;
                if (request is null) { _runner = null; return; }
                cancellation = CancellationTokenSource.CreateLinkedTokenSource(request.CallerToken);
                _activeCancellation = cancellation;
            }
            GateGeometryEvaluationResult? result = null;
            string? faultCode = null;
            try
            {
                result = await _evaluator.EvaluateAsync(request.Request, request.CompiledPlan,
                cancellation.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { }
            catch { faultCode = "gate-overlay-evaluation-fault"; }
            GateOverlayRequestOutcome outcome; GateOverlayVisualSnapshot? publish = null;
            lock (_gate)
            {
                if (ReferenceEquals(_activeCancellation, cancellation)) _activeCancellation = null;
                bool current = !_closed && request.ContextGeneration == _contextGeneration &&
                    request.Sequence == _evaluationSequence && (request.PreviewArtifact is { } preview
                        ? _editor.IsCurrentPreviewCandidate(preview)
                        : request.CommittedArtifact is { } committed && _editor.IsCurrentCommittedArtifact(committed)) &&
                    _signal?.TraceIdentity == request.Request.TraceIdentity &&
                    _signal?.InspectionRevision == request.Request.ExpectedInspectionRevision;
                if (!current) outcome = _closed ? GateOverlayRequestOutcome.Closed :
                    cancellation.IsCancellationRequested ? GateOverlayRequestOutcome.Cancelled :
                    GateOverlayRequestOutcome.Superseded;
                else if (result?.Status == GateGeometryEvaluationStatus.Completed)
                {
                    if (request.PreviewArtifact is { } completedPreview && !_editor.PromotePreview(completedPreview))
                        outcome = GateOverlayRequestOutcome.Superseded;
                    else
                    {
                        _stableArtifact = request.PreviewArtifact;
                        if (request.CommittedArtifact is { } installedCommitted)
                        {
                            _stableCommittedArtifact = installedCommitted;
                            _committedGeometry = result.Snapshot;
                        }
                        _stableGeometry = result.Snapshot;
                        outcome = GateOverlayRequestOutcome.Completed;
                        publish = BuildOverlayUnderLock(GateOverlayGeometryStatus.Current, result.Snapshot!.Source,
                            result.Snapshot.TraceIdentity, result.Snapshot.InspectionRevision, result.Snapshot,
                            request.Sequence, null); SetSnapshotUnderLock(publish);
                    }
                }
                else
                {
                    outcome = faultCode is not null ? GateOverlayRequestOutcome.Faulted :
                        Map(result?.Status, cancellation.IsCancellationRequested);
                    GateOverlayGeometryStatus status = outcome switch
                    {
                        GateOverlayRequestOutcome.CapacityExceeded => GateOverlayGeometryStatus.Unavailable,
                        GateOverlayRequestOutcome.Closed => GateOverlayGeometryStatus.Closed,
                        GateOverlayRequestOutcome.Faulted => GateOverlayGeometryStatus.Faulted,
                        _ => GateOverlayGeometryStatus.Stale
                    };
                    publish = BuildOverlayUnderLock(status, request.Request.Source,
                        request.Request.TraceIdentity, request.Request.ExpectedInspectionRevision,
                        null, request.Sequence, faultCode ?? result?.ErrorCode); SetSnapshotUnderLock(publish);
                }
            }
            cancellation.Dispose(); request.Completion.TrySetResult(outcome);
            if (publish is not null) Publish(publish);
        }
    }

    private CancellationTokenSource? AdvanceContextUnderLock()
    {
        _contextGeneration = checked(_contextGeneration + 1); _capture = null; _capturedHandleState = null;
        if (_gestureOwner == PortablePointerCaptureOwner.Gate) _gestureOwner = PortablePointerCaptureOwner.None;
        CancellationTokenSource? active = _activeCancellation;
        EvaluationRequest? pending = _pending; _pending = null;
        pending?.Completion.TrySetResult(GateOverlayRequestOutcome.Cancelled);
        return active;
    }

    private static void RequestCancellation(CancellationTokenSource? cancellation)
    {
        if (cancellation is null || cancellation.IsCancellationRequested) return;
        _ = ObserveCancellationAsync(cancellation);
    }

    private static async Task ObserveCancellationAsync(CancellationTokenSource cancellation)
    {
        try { await cancellation.CancelAsync().ConfigureAwait(false); }
        catch (ObjectDisposedException) { }
    }

    private void ReceiveEditorSnapshot(GatePlanEditorSnapshot snapshot)
    {
        GateOverlayVisualSnapshot? publish = null;
        CancellationTokenSource? cancellation = null;
        lock (_gate)
        {
            if (_closed || ReferenceEquals(snapshot, _lastEditorSnapshot)) return;
            _lastEditorSnapshot = snapshot;
            bool owned = snapshot.Draft is not null && (ReferenceEquals(snapshot.Draft, _ownedDraft) ||
                ReferenceEquals(snapshot.Draft, _stableArtifact?.Checkpoint));
            if (owned) return;
            cancellation = AdvanceContextUnderLock();
            publish = BuildOverlayUnderLock(GateOverlayGeometryStatus.Stale, null, null,
                InspectionRevision.Initial, null, _evaluationSequence, null);
            SetSnapshotUnderLock(publish);
        }
        RequestCancellation(cancellation);
        Publish(publish);
    }

    private void ReceiveAuthorityInvalidated()
    {
        GateOverlayVisualSnapshot? publish = null;
        CancellationTokenSource? cancellation = null;
        lock (_gate)
        {
            if (_closed) return;
            _stableArtifact = null; _stableCommittedArtifact = null; _stableGeometry = null;
            _committedGeometry = null;
            cancellation = AdvanceContextUnderLock();
            publish = BuildOverlayUnderLock(GateOverlayGeometryStatus.Stale, null, null,
                InspectionRevision.Initial, null, _evaluationSequence, null);
            SetSnapshotUnderLock(publish);
        }
        RequestCancellation(cancellation);
        Publish(publish);
    }

    private bool CompatibleGeometryUnderLock(GateGeometrySnapshot? geometry) => geometry is not null &&
        _signal?.TraceIdentity == geometry.TraceIdentity && _signal?.InspectionRevision == geometry.InspectionRevision;

    private GateOverlayVisualSnapshot RebuildCurrentUnderLock() => BuildOverlayUnderLock(_overlay.Status,
        _overlay.Source, _overlay.TraceIdentity, _overlay.InspectionRevision, _stableGeometry,
        _overlay.EvaluationSequence, _overlay.ErrorCode);

    private GateOverlayVisualSnapshot BuildOverlayUnderLock(GateOverlayGeometryStatus status,
        GateGeometrySourceIdentity? source, TraceIdentity? trace, InspectionRevision revision,
        GateGeometrySnapshot? geometry, ulong sequence, string? error)
    {
        IReadOnlyList<ProjectedGateGeometryItem> projected = geometry is not null && _transform is { } transform &&
            CompatibleGeometryUnderLock(geometry) ? GateGeometryProjector.Project(geometry, transform.ProjectionContext) : [];
        List<GateHandleVisualState> handles = BuildHandles(projected);
        return new(status, source, trace, revision, projected, handles, _selectedGate, _focusedHandle,
            _contextGeneration, sequence, _capture, error);
    }

    private List<GateHandleVisualState> BuildHandles(IReadOnlyList<ProjectedGateGeometryItem> projected)
    {
        var handles = new List<GateHandleVisualState>();
        GatePlanDraft? draft = _editor.Snapshot.Draft;
        if (draft is null || _transform is not { } transform) return handles;
        double timeStep = transform.TimeViewport.VisibleSpanSeconds / 1000d;
        double relativeLimit = transform.TimeViewport.DomainSpanSeconds;
        double middleY = transform.AmplitudeOrigin + transform.AmplitudeLength / 2d;
        foreach (ProjectedGateGeometryItem item in projected)
        {
            GateDefinition? definition = draft.Gates.FirstOrDefault(x => x.Id == item.GateId);
            if (definition is null) continue;
            GateGeometryItemSnapshot? exact = _stableGeometry?.Gates.FirstOrDefault(x => x.GateId == item.GateId);
            GateId anchorGateId = exact?.Anchor?.GateId ?? default;
            GateRevision anchorRevision = exact?.Anchor?.GateRevision ?? default;
            bool selected = _selectedGate == item.GateId;
            if (item.StartCoordinate is { } start && IsVisibleTimeHandle(start, GateHandleKind.Start,
                definition.Window.StartOffsetSeconds, transform))
            {
                (double target, double offset) = InsetTimeHandle(start, transform);
                double startValue = definition.Window.StartOffsetSeconds;
                handles.Add(new(item.GateId, item.GateRevision,
                GateHandleKind.Start, target, middleY, startValue, "s",
                Math.Min(-relativeLimit, startValue), startValue + definition.Window.DurationSeconds - timeStep,
                timeStep, selected, selected && _focusedHandle == GateHandleKind.Start, true, anchorGateId,
                anchorRevision, offset));
            }
            if (item.EndCoordinate is { } end && IsVisibleTimeHandle(end, GateHandleKind.End,
                definition.Window.StartOffsetSeconds + definition.Window.DurationSeconds, transform))
            {
                (double target, double offset) = InsetTimeHandle(end, transform);
                double endValue = definition.Window.StartOffsetSeconds + definition.Window.DurationSeconds;
                handles.Add(new(item.GateId, item.GateRevision,
                GateHandleKind.End, target, middleY, endValue, "s",
                definition.Window.StartOffsetSeconds + timeStep, Math.Max(relativeLimit, endValue),
                timeStep, selected, selected && _focusedHandle == GateHandleKind.End, true, anchorGateId,
                anchorRevision, offset));
            }
            if (item.StartCoordinate is { } thresholdStart && item.EndCoordinate is { } thresholdEnd &&
                item.Thresholds.FirstOrDefault() is { } threshold && definition.Detector.Threshold is { } gateThreshold &&
                IsInsideTimePlot((thresholdStart + thresholdEnd) / 2d, transform))
                handles.Add(new(item.GateId, item.GateRevision, GateHandleKind.Threshold,
                    (thresholdStart + thresholdEnd) / 2d,
                    threshold.Coordinate, gateThreshold.NormalizedValue, "normalized", 0, 1, .01, selected,
                    selected && _focusedHandle == GateHandleKind.Threshold, true, anchorGateId, anchorRevision));
        }
        return handles;
    }

    private GateGeometrySourceIdentity? ResolveCaptureSourceUnderLock()
    {
        GatePlanDraft? draft = _editor.Snapshot.Draft;
        if (_stableArtifact is { } preview && ReferenceEquals(preview.Checkpoint, draft)) return preview.Source;
        return _stableCommittedArtifact?.Source;
    }

    private static (double Target, double Offset) InsetTimeHandle(double exact, GateVisualTransform transform)
    {
        double minimum = transform.TimeOrigin + BoundaryHandleInsetDip;
        double maximum = transform.TimeOrigin + transform.TimeLength - BoundaryHandleInsetDip;
        double target = maximum < minimum ? transform.TimeOrigin + transform.TimeLength / 2d :
            Math.Clamp(exact, minimum, maximum);
        return (target, exact - target);
    }

    private static bool IsInsideTimePlot(double coordinate, GateVisualTransform transform) =>
        coordinate >= transform.TimeOrigin && coordinate <= transform.TimeOrigin + transform.TimeLength;

    private bool IsVisibleTimeHandle(double coordinate, GateHandleKind kind, double value,
        GateVisualTransform transform)
    {
        if (IsInsideTimePlot(coordinate, transform)) return true;
        if (kind != GateHandleKind.End || _signal is not { } signal ||
            transform.TimeViewport.VisibleMaximumSeconds != transform.TimeViewport.DomainMaximumSeconds)
            return false;
        double exclusiveTraceEnd = signal.MinimumTimeSeconds +
            signal.OriginalSampleCount / signal.Metadata.Configuration.SampleRateHz;
        double tolerance = Math.Max(Math.Abs(exclusiveTraceEnd), 1d) * 1e-12;
        return Math.Abs(value - exclusiveTraceEnd) <= tolerance;
    }

    private static GatePlanDraft ChangeDraft(GatePlanDraft draft, GateId gateId, GateHandleKind handle, double value)
    {
        if (!double.IsFinite(value)) throw new ArgumentOutOfRangeException(nameof(value));
        GateDefinition[] gates = draft.Gates.Select(gate => gate.Id != gateId ? gate : ChangeGate(gate, handle, value)).ToArray();
        return new(draft.Authority, draft.BaseRevision, draft.SchemaVersion, draft.AlgorithmVersion, gates);
    }

    private static GateDefinition ChangeGate(GateDefinition gate, GateHandleKind handle, double value)
    {
        double start = gate.Window.StartOffsetSeconds, duration = gate.Window.DurationSeconds;
        GateDetectorDefinition detector = gate.Detector;
        switch (handle)
        {
            case GateHandleKind.Start: duration += start - value; start = value; break;
            case GateHandleKind.End: duration = value - start; break;
            case GateHandleKind.Threshold:
                detector = new(detector.Kind, new(Math.Clamp(value, 0, 1)), detector.Hysteresis, detector.LocalExtrema); break;
            default: throw new ArgumentOutOfRangeException(nameof(handle));
        }
        return new(gate.Id, gate.Revision, new(gate.Window.StartReference, start, duration),
            gate.SignalDomain, detector, gate.RequestedMeasures);
    }

    private void SetSnapshotUnderLock(GateOverlayVisualSnapshot snapshot) => _overlay = snapshot;
    private void Publish(GateOverlayVisualSnapshot snapshot)
    {
        lock (_gate)
        {
            if (!ReferenceEquals(_overlay, snapshot)) return;
            _publications.Enqueue(snapshot);
            if (_publishing) return;
            _publishing = true;
        }
        while (true)
        {
            GateOverlayVisualSnapshot? next; Action<GateOverlayVisualSnapshot>? handlers;
            lock (_gate)
            {
                if (!_publications.TryDequeue(out next)) { _publishing = false; return; }
                handlers = SnapshotChanged;
            }
            if (handlers is null) continue;
            foreach (Action<GateOverlayVisualSnapshot> handler in handlers.GetInvocationList())
                try { handler(next); } catch { }
        }
    }

    private static GateOverlayRequestOutcome Map(GateGeometryEvaluationStatus? status, bool cancelled) =>
        cancelled ? GateOverlayRequestOutcome.Cancelled : status switch
        {
            GateGeometryEvaluationStatus.CapacityExceeded => GateOverlayRequestOutcome.CapacityExceeded,
            GateGeometryEvaluationStatus.Cancelled => GateOverlayRequestOutcome.Cancelled,
            GateGeometryEvaluationStatus.Closed => GateOverlayRequestOutcome.Closed,
            GateGeometryEvaluationStatus.Faulted => GateOverlayRequestOutcome.Faulted,
            _ => GateOverlayRequestOutcome.NotAvailable
        };

    private static GateOverlayVisualSnapshot Empty(GateOverlayGeometryStatus status, ulong generation) =>
        new(status, null, null, InspectionRevision.Initial, [], [], null, GateHandleKind.Unknown,
            generation, 0, null, status == GateOverlayGeometryStatus.Faulted ? "overlay-fault" : null);
    private void ThrowIfClosed() => ObjectDisposedException.ThrowIf(_closed, this);

    private sealed class EvaluationRequest(ulong sequence, ulong contextGeneration,
        GatePreviewArtifact? previewArtifact, GateCommittedArtifact? committedArtifact,
        GateGeometryEvaluationRequest request, CompiledGatePlan compiledPlan,
        CancellationToken callerToken)
    {
        internal ulong Sequence { get; } = sequence;
        internal ulong ContextGeneration { get; } = contextGeneration;
        internal GatePreviewArtifact? PreviewArtifact { get; } = previewArtifact;
        internal GateCommittedArtifact? CommittedArtifact { get; } = committedArtifact;
        internal GateGeometryEvaluationRequest Request { get; } = request;
        internal CompiledGatePlan CompiledPlan { get; } = compiledPlan;
        internal CancellationToken CallerToken { get; } = callerToken;
        internal TaskCompletionSource<GateOverlayRequestOutcome> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

}
