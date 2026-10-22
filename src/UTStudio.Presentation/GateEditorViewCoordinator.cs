using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UTStudio.Contracts.Processing;
using UTStudio.Domain.Processing;
using UTStudio.Visualization.Core;

namespace UTStudio.Presentation;

public enum GateEditorViewPhase
{
    Unavailable, Committed, Editing, Previewing, Previewed, Applying, Busy,
    Conflict, ValidationFailed, Closed, Faulted
}

public sealed record GateEditorViewState(
    GateEditorViewPhase Phase,
    GatePlanEditorSnapshot Editor,
    GateOverlayVisualSnapshot Overlay,
    GatePlanApplySubmissionStatus? Admission = null,
    GatePlanEditResult? Result = null,
    bool WaiterCancelled = false,
    string? ErrorCode = null);

/// <summary>
/// Per-view owner of draft, Preview geometry, and UI observation. Productive ports are borrowed; admitted Apply
/// operations remain owned and reconciled by Application even after this coordinator closes.
/// </summary>
public sealed class GateEditorViewCoordinator : ObservableObject, IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly object _interactionSync = new();
    private readonly IGatePlanApplyPort _apply;
    private readonly GatePlanEditorSession _editor;
    private readonly GateOverlaySession _overlay;
    private readonly UiLifetime _lifetime;
    private readonly IDisposable _editorSubscription;
    private readonly UiAsyncCommand _beginEdit, _preview, _cancel, _applyCommand;
    private GateEditorViewState _state;
    private GateNumericPresentation? _publishedNumeric;
    private AScanCompositeVisualSnapshot? _composite;
    private GateInteractionRequestedEventArgsData? _pendingMove, _pendingInteraction;
    private readonly HashSet<GateHandleKind> _invalidNumericInputs = [];
    private Task? _interactionRunner;
    private ulong _applySequence;
    private bool _closed;
    private Task? _closeTask;

    public GateEditorViewCoordinator(GateEditingComponentContext context, IUiDispatcher dispatcher)
    {
        ArgumentNullException.ThrowIfNull(context); ArgumentNullException.ThrowIfNull(dispatcher);
        if (!context.IsValid) throw new ArgumentException("A valid gate editing context is required.", nameof(context));
        _apply = context.Apply;
        _lifetime = new(dispatcher);
        _editor = new(context.AuthorityKey, context.Authority, context.PreviewCompiler);
        _overlay = new(_editor, context.GeometryEvaluator);
        _state = new(GateEditorViewPhase.Committed, _editor.Snapshot, _overlay.Snapshot);
        _overlay.SnapshotChanged += ReceiveOverlay;
        _beginEdit = Command(BeginEditAsync, CanBeginEdit);
        _preview = Command(PreviewAsync, CanPreview);
        _cancel = Command(CancelAsync, CanCancel);
        _applyCommand = Command(ApplyAsync, CanApply);
        foreach (UiAsyncCommand command in Commands) command.Initialize();
        _editorSubscription = _editor.Subscribe(new EditorObserver(this));
    }

    public GateEditorViewState State => _state;
    public AScanCompositeVisualSnapshot? CompositeVisual => _composite;
    public IAsyncRelayCommand BeginEditCommand => _beginEdit;
    public IAsyncRelayCommand PreviewCommand => _preview;
    public IAsyncRelayCommand CancelCommand => _cancel;
    public IAsyncRelayCommand ApplyCommand => _applyCommand;
    public string? SelectedGateId => SelectedGate()?.Id.Value;
    public double? SelectedGateStartMicroseconds
    {
        get => SelectedGate()?.Window.StartOffsetSeconds * 1_000_000d;
        set => QueueNumericPreview(GateHandleKind.Start, value);
    }
    public double? SelectedGateEndMicroseconds
    {
        get => SelectedGate() is { } gate
            ? (gate.Window.StartOffsetSeconds + gate.Window.DurationSeconds) * 1_000_000d : null;
        set => QueueNumericPreview(GateHandleKind.End, value);
    }
    public double? SelectedGateThresholdPercent
    {
        get => SelectedGate()?.Detector.Threshold?.NormalizedValue * 100d;
        set => QueueNumericPreview(GateHandleKind.Threshold, value is null ? null : value / 100d);
    }
    public bool SelectedGateHasThreshold => SelectedGate()?.Detector.Threshold is not null;
    public string? NumericValidationMessage => _invalidNumericInputs.Count != 0
        ? "Introduzca un número válido." : _state.ErrorCode == "gate.numeric.range"
            ? "El valor debe permanecer dentro del rango válido de la puerta." : null;

    public void SetNumericInputValidity(GateHandleKind kind, bool isValid)
    {
        if (_closed || kind == GateHandleKind.Unknown) return;
        bool changed = isValid ? _invalidNumericInputs.Remove(kind) : _invalidNumericInputs.Add(kind);
        if (!changed) return;
        _publishedNumeric = null;
        OnPropertyChanged(nameof(NumericValidationMessage));
        RefreshCommands();
    }

    public void SetVisualContext(AScanSnapshot signal, AScanCursorState? cursors, GateVisualTransform transform)
    {
        if (_closed) return;
        bool changed = _overlay.SetVisualContext(signal, cursors, transform);
        if (!changed) return;
        DiscardPendingInteractions();
        _ = ObserveVisualAsync(_editor.Snapshot.Draft is null
            ? _overlay.RequestCommittedAsync() : _overlay.RequestEditingAsync());
    }

    public bool TryBeginCapture(AScanCompositeVisualSnapshot shown, GateHandleLogicalIdentity handle,
        out GateCaptureGeneration capture)
    {
        capture = default;
        if (_closed || !ReferenceEquals(shown, _composite) || !handle.IsValid ||
            !_overlay.Select(handle.GateId, handle.Kind)) return false;
        GateCaptureGeneration? accepted = _overlay.BeginGateCapture();
        if (accepted is not { IsValid: true } value) return false;
        capture = value; return true;
    }

    public void HandleInteraction(GateInteractionRequestedEventArgsData intent)
    {
        if (!IsCurrentInteraction(intent)) return;
        lock (_interactionSync)
        {
            if (_closed) return;
            if (intent.Kind == GateOverlayInteractionKind.MoveCapture)
            {
                if (_pendingInteraction?.Kind is GateOverlayInteractionKind.EndCapture or
                    GateOverlayInteractionKind.CancelPreview) return;
                _pendingMove = intent;
            }
            else
            {
                bool pendingTerminal = _pendingInteraction?.Kind is GateOverlayInteractionKind.EndCapture or
                    GateOverlayInteractionKind.CancelPreview;
                if (pendingTerminal && intent.Kind != GateOverlayInteractionKind.CancelPreview) return;
                if (intent.Kind == GateOverlayInteractionKind.CancelPreview) _pendingMove = null;
                _pendingInteraction = intent;
            }
            _interactionRunner ??= Task.Run(DrainInteractionsAsync);
        }
    }

    private async Task DrainInteractionsAsync()
    {
        while (true)
        {
            GateInteractionRequestedEventArgsData intent;
            lock (_interactionSync)
            {
                GateInteractionRequestedEventArgsData? next = _pendingMove ?? _pendingInteraction;
                if (next is null)
                {
                    _interactionRunner = null;
                    return;
                }
                intent = next.Value;
                if (_pendingMove is not null) _pendingMove = null;
                else _pendingInteraction = null;
            }
            if (!IsCurrentInteraction(intent)) continue;
            await ObserveInteractionAsync(intent).ConfigureAwait(false);
        }
    }

    private bool IsCurrentInteraction(GateInteractionRequestedEventArgsData intent) =>
        !_closed && intent.IsValid && (ReferenceEquals(intent.Snapshot, _composite) ||
        intent.Capture is { } capture && _overlay.Snapshot.Capture == capture);

    private void DiscardPendingInteractions()
    {
        lock (_interactionSync)
        {
            _pendingMove = null;
            _pendingInteraction = null;
        }
    }

    private async Task ObserveInteractionAsync(GateInteractionRequestedEventArgsData intent)
    {
        try
        {
            if (intent.Handle is { } handle && intent.Kind is GateOverlayInteractionKind.Select or
                GateOverlayInteractionKind.FocusNext or GateOverlayInteractionKind.FocusPrevious)
            { _overlay.Select(handle.GateId, handle.Kind); return; }
            switch (intent.Kind)
            {
                case GateOverlayInteractionKind.MoveCapture when intent.Value is { } value:
                    await _overlay.MoveCapturedHandleAsync(value).ConfigureAwait(false); break;
                case GateOverlayInteractionKind.EndCapture: _overlay.EndCapture(); break;
                case GateOverlayInteractionKind.CancelPreview: _overlay.RestoreStablePreview(); break;
                case GateOverlayInteractionKind.ConfirmPreview:
                    await _overlay.HandleKeyAsync(GateOverlayKeyIntent.ConfirmPreview).ConfigureAwait(false); break;
                case GateOverlayInteractionKind.Increment:
                    await _overlay.HandleKeyAsync(GateOverlayKeyIntent.Increment).ConfigureAwait(false); break;
                case GateOverlayInteractionKind.Decrement:
                    await _overlay.HandleKeyAsync(GateOverlayKeyIntent.Decrement).ConfigureAwait(false); break;
            }
        }
        catch (Exception error) { ReportError(error); }
    }

    private Task BeginEditAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        _invalidNumericInputs.Clear();
        GatePlanEditorSnapshot editor = _editor.BeginEdit();
        bool geometryReady = _overlay.BeginEditing(editor.Draft!);
        Publish(_state with
        {
            Phase = GateEditorViewPhase.Editing,
            Editor = editor,
            Result = null,
            Admission = null,
            WaiterCancelled = false,
            ErrorCode = null
        });
        if (!geometryReady) _ = ObserveVisualAsync(_overlay.RequestCommittedAsync(token));
        return Task.CompletedTask;
    }

    public async Task<GateOverlayRequestOutcome> PreviewHandleValueAsync(GateId gateId,
        GateHandleKind kind, double valueSeconds, CancellationToken token = default)
    {
        if (_closed) return GateOverlayRequestOutcome.Closed;
        Publish(_state with { Phase = GateEditorViewPhase.Previewing, Result = null, ErrorCode = null });
        GateOverlayRequestOutcome outcome = await _overlay.PreviewHandleValueAsync(gateId, kind,
            valueSeconds, token).ConfigureAwait(false);
        await _lifetime.InvokeAsync(() =>
        {
            GatePlanEditorSnapshot editor = _editor.Snapshot;
            GateEditorViewPhase phase = outcome == GateOverlayRequestOutcome.Completed
                ? GateEditorViewPhase.Previewed : outcome == GateOverlayRequestOutcome.NotAvailable
                    ? GateEditorViewPhase.ValidationFailed : Map(editor, outcome);
            Publish(_state with
            {
                Phase = phase,
                Editor = editor,
                ErrorCode = outcome == GateOverlayRequestOutcome.NotAvailable
                    ? "gate.numeric.range" : outcome == GateOverlayRequestOutcome.Faulted
                        ? "gate.preview.faulted" : null
            });
        }).ConfigureAwait(false);
        return outcome;
    }

    private async Task PreviewAsync(CancellationToken token)
    {
        GatePlanDraft draft = _editor.Snapshot.Draft ?? throw new InvalidOperationException("Begin edit first.");
        Publish(_state with { Phase = GateEditorViewPhase.Previewing, Result = null, ErrorCode = null });
        GateOverlayRequestOutcome outcome = await _overlay.PreviewAsync(draft, token).ConfigureAwait(false);
        await _lifetime.InvokeAsync(() =>
        {
            GatePlanEditorSnapshot editor = _editor.Snapshot;
            GateEditorViewPhase phase = outcome == GateOverlayRequestOutcome.Completed
                ? GateEditorViewPhase.Previewed : Map(editor, outcome);
            Publish(_state with
            {
                Phase = phase,
                Editor = editor,
                ErrorCode = outcome == GateOverlayRequestOutcome.Faulted ? "gate.preview.faulted" : null
            });
        }).ConfigureAwait(false);
    }

    private async Task CancelAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        _invalidNumericInputs.Clear();
        DiscardPendingInteractions();
        GatePlanEditorSnapshot editor = _editor.Cancel().Snapshot;
        GateEditorViewPhase phase = _state.Phase == GateEditorViewPhase.Applying
            ? GateEditorViewPhase.Applying : GateEditorViewPhase.Committed;
        Publish(_state with
        {
            Phase = phase,
            Editor = editor,
            Result = null,
            Admission = null,
            WaiterCancelled = false,
            ErrorCode = null
        });
        await _overlay.RequestCommittedAsync(token).ConfigureAwait(false);
    }

    private async Task ApplyAsync(CancellationToken waiterToken)
    {
        GatePreviewArtifact artifact = _editor.BorrowApplicablePreview() ??
            throw new InvalidOperationException("Apply requires a stable Preview.");
        GateEditorApplyTicket ticket = _editor.RegisterApply(artifact);
        ulong sequence = checked(++_applySequence);
        var intent = new GatePlanEditIntent(GatePlanEditIntentKind.Apply, artifact.Checkpoint.Authority,
            artifact.Checkpoint.BaseRevision, artifact.Checkpoint);
        GatePlanApplySubmission submission;
        try
        {
            submission = _apply.TryApplyGatePlan(new(intent, artifact.Candidate), waiterToken);
        }
        catch { _editor.AbandonApplyRegistration(ticket); throw; }
        if (!submission.IsAdmitted)
        {
            if (submission.Completion is { } immediate)
            {
                GatePlanEditResult terminal = await immediate.ConfigureAwait(false);
                _editor.ReconcileApply(ticket, terminal);
                await _lifetime.InvokeAsync(() => PublishTerminal(sequence, terminal,
                    submission.Status)).ConfigureAwait(false);
            }
            else
            {
                _editor.AbandonApplyRegistration(ticket);
                await _lifetime.InvokeAsync(() => PublishAdmission(sequence, submission.Status)).ConfigureAwait(false);
            }
            return;
        }

        Publish(_state with
        {
            Phase = GateEditorViewPhase.Applying,
            Admission = submission.Status,
            Result = null,
            WaiterCancelled = false,
            ErrorCode = null
        });
        var weak = new WeakReference<GateEditorViewCoordinator>(this);
        _ = Task.Run(() => ObserveTerminalAsync(submission.Completion!, weak, ticket, sequence, submission.Status));
        try
        {
            await submission.WaitAsync(waiterToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (waiterToken.IsCancellationRequested)
        {
            await _lifetime.InvokeAsync(() =>
            {
                if (sequence == _applySequence && _state.Phase == GateEditorViewPhase.Applying)
                    Publish(_state with { WaiterCancelled = true });
            }).ConfigureAwait(false);
        }
    }

    private static async Task ObserveTerminalAsync(Task<GatePlanEditResult> completion,
        WeakReference<GateEditorViewCoordinator> owner, GateEditorApplyTicket ticket, ulong sequence,
        GatePlanApplySubmissionStatus admission)
    {
        try
        {
            GatePlanEditResult result = await completion.ConfigureAwait(false);
            if (owner.TryGetTarget(out GateEditorViewCoordinator? target))
                target.ReceiveTerminal(ticket, sequence, admission, result, null);
        }
        catch (Exception error)
        {
            if (owner.TryGetTarget(out GateEditorViewCoordinator? target))
                target.ReceiveTerminal(ticket, sequence, admission, null, error);
        }
    }

    private void ReceiveTerminal(GateEditorApplyTicket ticket, ulong sequence,
        GatePlanApplySubmissionStatus admission, GatePlanEditResult? result, Exception? error) =>
        _lifetime.Post(() =>
        {
            if (_closed) return;
            if (result is not null)
            {
                _editor.ReconcileApply(ticket, result);
            }
            else _editor.AbandonApplyRegistration(ticket);
            if (sequence != _applySequence) return;
            if (error is not null)
                Publish(_state with
                {
                    Phase = GateEditorViewPhase.Faulted,
                    Admission = admission,
                    ErrorCode = "gate.apply.invariant",
                    Result = null
                });
            else
            {
                PublishTerminal(sequence, result!, admission);
            }
        });

    private void PublishTerminal(ulong sequence, GatePlanEditResult result,
        GatePlanApplySubmissionStatus admission)
    {
        if (sequence != _applySequence) return;
        GateEditorViewPhase phase = result.Outcome switch
        {
            GatePlanEditOutcome.Accepted => _editor.Snapshot.State == GatePlanEditorState.Committed
                ? GateEditorViewPhase.Committed : GateEditorViewPhase.Applying,
            GatePlanEditOutcome.Conflict or GatePlanEditOutcome.Superseded => GateEditorViewPhase.Conflict,
            GatePlanEditOutcome.ValidationFailed => GateEditorViewPhase.ValidationFailed,
            GatePlanEditOutcome.CapacityExceeded => GateEditorViewPhase.Busy,
            GatePlanEditOutcome.Closed => GateEditorViewPhase.Closed,
            GatePlanEditOutcome.CancelledBeforeCommit => GateEditorViewPhase.Editing,
            _ => GateEditorViewPhase.Faulted
        };
        Publish(_state with
        {
            Phase = phase,
            Editor = _editor.Snapshot,
            Admission = admission,
            Result = result,
            ErrorCode = phase == GateEditorViewPhase.Faulted ? $"gate.apply.{result.Outcome}" : null
        });
    }

    private void PublishAdmission(ulong sequence, GatePlanApplySubmissionStatus admission)
    {
        if (sequence != _applySequence) return;
        GateEditorViewPhase phase = admission switch
        {
            GatePlanApplySubmissionStatus.CoordinatorCapacityExceeded => GateEditorViewPhase.Busy,
            GatePlanApplySubmissionStatus.AdmissionClosed => GateEditorViewPhase.Closed,
            GatePlanApplySubmissionStatus.CancelledBeforeAdmission => GateEditorViewPhase.Editing,
            _ => GateEditorViewPhase.Faulted
        };
        Publish(_state with
        {
            Phase = phase,
            Admission = admission,
            Result = null,
            ErrorCode = phase == GateEditorViewPhase.Faulted ? $"gate.admission.{admission}" : null
        });
    }

    private void ReceiveOverlay(GateOverlayVisualSnapshot overlay) => _lifetime.Post(() =>
    {
        if (_closed) return;
        _composite = _overlay.CompositeSnapshot;
        GateEditorViewPhase phase = IsCurrentPreviewOverlay(overlay)
            ? GateEditorViewPhase.Previewed : _state.Phase;
        Publish(_state with { Overlay = overlay, Phase = phase });
    });

    private void ReceiveEditor(GatePlanEditorSnapshot editor) => _lifetime.Post(() =>
    {
        if (_closed) return;
        GateEditorViewPhase phase = _state.Phase == GateEditorViewPhase.Applying &&
            editor.State != GatePlanEditorState.Committed ? _state.Phase :
            Map(editor, IsCurrentPreviewOverlay(_overlay.Snapshot) ? GateOverlayRequestOutcome.Completed : null);
        Publish(_state with { Editor = editor, Phase = phase });
        if (editor.State == GatePlanEditorState.Committed && _composite is not null)
            _ = ObserveVisualAsync(_overlay.RequestCommittedAsync());
    });

    private async Task ObserveVisualAsync(ValueTask<GateOverlayRequestOutcome> operation)
    { try { await operation.ConfigureAwait(false); } catch (Exception error) { ReportError(error); } }

    private bool IsCurrentPreviewOverlay(GateOverlayVisualSnapshot overlay)
    {
        GatePreviewArtifact? artifact = _editor.BorrowApplicablePreview();
        return artifact is not null && overlay.Status == GateOverlayGeometryStatus.Current &&
            overlay.Source is { Kind: GateGeometrySourceKind.Preview } source &&
            source.Preview == artifact.Identity && source.Revision == artifact.Source.Revision &&
            source.BaseRevision == artifact.Source.BaseRevision;
    }

    private static GateEditorViewPhase Map(GatePlanEditorSnapshot editor, GateOverlayRequestOutcome? overlay) =>
        editor.State switch
        {
            GatePlanEditorState.Committed or GatePlanEditorState.Cancelled => GateEditorViewPhase.Committed,
            GatePlanEditorState.Editing => GateEditorViewPhase.Editing,
            GatePlanEditorState.Previewed => overlay == GateOverlayRequestOutcome.Completed
                ? GateEditorViewPhase.Previewed : GateEditorViewPhase.Previewing,
            GatePlanEditorState.ValidationFailed => GateEditorViewPhase.ValidationFailed,
            GatePlanEditorState.Stale => GateEditorViewPhase.Conflict,
            GatePlanEditorState.Closed => GateEditorViewPhase.Closed,
            _ => GateEditorViewPhase.Unavailable
        };

    private bool CanBeginEdit() => !_closed && _state.Phase is GateEditorViewPhase.Committed or
        GateEditorViewPhase.Conflict or GateEditorViewPhase.ValidationFailed or GateEditorViewPhase.Faulted;
    private bool CanPreview() => !_closed && _editor.Snapshot.Draft is not null &&
        _state.Phase is GateEditorViewPhase.Editing or GateEditorViewPhase.Previewed or
        GateEditorViewPhase.ValidationFailed;
    private bool CanCancel() => !_closed && _editor.Snapshot.Draft is not null;
    private bool CanApply() => !_closed && _invalidNumericInputs.Count == 0 &&
        _state.Phase == GateEditorViewPhase.Previewed &&
        _editor.BorrowApplicablePreview() is not null;
    private UiAsyncCommand Command(Func<CancellationToken, Task> execute, Func<bool> canExecute) =>
        new(_lifetime, execute, canExecute, ReportError, RefreshCommands);
    private UiAsyncCommand[] Commands => [_beginEdit, _preview, _cancel, _applyCommand];
    private void RefreshCommands() { foreach (UiAsyncCommand command in Commands) command.NotifyCanExecuteChanged(); }

    private void Publish(GateEditorViewState state)
    {
        if (_closed || state == _state) return;
        _state = state; OnPropertyChanged(nameof(State)); OnPropertyChanged(nameof(CompositeVisual));
        GateNumericPresentation numeric = ReadNumericPresentation();
        if (numeric != _publishedNumeric)
        {
            _publishedNumeric = numeric;
            OnPropertyChanged(nameof(SelectedGateId));
            OnPropertyChanged(nameof(SelectedGateStartMicroseconds));
            OnPropertyChanged(nameof(SelectedGateEndMicroseconds));
            OnPropertyChanged(nameof(SelectedGateThresholdPercent));
            OnPropertyChanged(nameof(SelectedGateHasThreshold));
            OnPropertyChanged(nameof(NumericValidationMessage));
        }
        RefreshCommands();
    }

    private GateNumericPresentation ReadNumericPresentation()
    {
        GateDefinition? gate = SelectedGate();
        return new(gate?.Id.Value,
            gate?.Window.StartOffsetSeconds * 1_000_000d,
            gate is null ? null : (gate.Window.StartOffsetSeconds + gate.Window.DurationSeconds) * 1_000_000d,
            gate?.Detector.Threshold?.NormalizedValue * 100d,
            gate?.Detector.Threshold is not null,
            NumericValidationMessage);
    }

    private GateDefinition? SelectedGate()
    {
        GatePlanDraft? draft = _editor.Snapshot.Draft;
        if (draft is null) return null;
        GateId? selected = _overlay.Snapshot.SelectedGate;
        return selected is { } gateId ? draft.Gates.FirstOrDefault(x => x.Id == gateId) : draft.Gates.FirstOrDefault();
    }

    private void QueueNumericPreview(GateHandleKind kind, double? displayedValue)
    {
        if (_closed || displayedValue is not { } finite || !double.IsFinite(finite) ||
            SelectedGate() is not { } gate) return;
        double value = kind == GateHandleKind.Threshold ? finite : finite / 1_000_000d;
        double? current = kind switch
        {
            GateHandleKind.Start => gate.Window.StartOffsetSeconds,
            GateHandleKind.End => gate.Window.StartOffsetSeconds + gate.Window.DurationSeconds,
            GateHandleKind.Threshold => gate.Detector.Threshold?.NormalizedValue,
            _ => null
        };
        if (current is null || Math.Abs(current.Value - value) <= 1e-15) return;
        _ = ObserveNumericPreviewAsync(gate.Id, kind, value);
    }

    private async Task ObserveNumericPreviewAsync(GateId gateId, GateHandleKind kind, double value)
    {
        try { await PreviewHandleValueAsync(gateId, kind, value).ConfigureAwait(false); }
        catch (Exception error) { ReportError(error); }
    }

    private sealed record GateNumericPresentation(string? GateId, double? StartMicroseconds,
        double? EndMicroseconds, double? ThresholdPercent, bool HasThreshold, string? ValidationMessage);

    private void ReportError(Exception error) => _lifetime.Post(() => Publish(_state with
    {
        Phase = GateEditorViewPhase.Faulted,
        ErrorCode = string.IsNullOrWhiteSpace(error.Message)
        ? error.GetType().Name : error.Message
    }));

    public ValueTask DisposeAsync()
    {
        lock (_gate) _closeTask ??= CloseCoreAsync();
        return new(_closeTask);
    }

    private async Task CloseCoreAsync()
    {
        if (_closed) return;
        _closed = true;
        Task? interactionRunner;
        lock (_interactionSync)
        {
            _pendingMove = null;
            _pendingInteraction = null;
            interactionRunner = _interactionRunner;
        }
        foreach (UiAsyncCommand command in Commands) command.DisableForClose();
        _overlay.SnapshotChanged -= ReceiveOverlay;
        _editorSubscription.Dispose();
        ValueTask overlayClose = _overlay.DisposeAsync();
        if (interactionRunner is not null) await interactionRunner.ConfigureAwait(false);
        await overlayClose.ConfigureAwait(false);
        _editor.Dispose();
        foreach (UiAsyncCommand command in Commands) await command.FinishForCloseAsync().ConfigureAwait(false);
        _lifetime.Close();
        await _lifetime.BarrierAsync().ConfigureAwait(false);
        _composite = null;
    }

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    { if (!_closed) try { base.OnPropertyChanged(e); } catch { } }

    private sealed class EditorObserver(GateEditorViewCoordinator owner) : IObserver<GatePlanEditorSnapshot>
    {
        public void OnNext(GatePlanEditorSnapshot value) => owner.ReceiveEditor(value);
        public void OnError(Exception error) => owner.ReportError(error);
        public void OnCompleted() { }
    }
}

/// <summary>Neutral copy of a WPF interaction event; Presentation never references WPF event args.</summary>
public readonly record struct GateInteractionRequestedEventArgsData(
    GateOverlayInteractionKind Kind,
    AScanCompositeVisualSnapshot Snapshot,
    GateHandleLogicalIdentity? Handle,
    GateCaptureGeneration? Capture,
    double? Value)
{
    public bool IsValid => Snapshot is not null && Enum.IsDefined(Kind);
}
