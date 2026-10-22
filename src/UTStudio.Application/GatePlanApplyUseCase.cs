using UTStudio.Contracts.Processing;
using UTStudio.Contracts.Inspection;
using UTStudio.Domain.Acquisition;
using UTStudio.Domain.Inspection;
using UTStudio.Domain.Processing;
using UTStudio.Processing.Core;

namespace UTStudio.Application;

internal readonly record struct GatePlanApplyRunContext(
    InspectionId InspectionId,
    AcquisitionRunId RunId,
    GateRunGeneration RunGeneration)
{
    internal bool IsValid => InspectionId.IsValid && RunId.IsValid && RunGeneration.IsValid;
}

internal interface IGatePlanApplyLifecycle
{
    bool IsRunning(GatePlanApplyRunContext context);
    bool TryCrossPointOfNoReturn(GatePlanApplyRunContext context);
}

internal sealed class GatePlanApplyServices
{
    private readonly object _gate = new();
    private GateStoreRunAuthoritySnapshot? _currentRun;

    internal GatePlanApplyServices(IGatePlanAuthorityReader authorityReader,
        InMemoryGatePlanPreparedCommitOwner authority, IPreparedGatePlanEpochPort epochs,
        IPreparedGateStoreControl store, IGatePlanPreviewCandidateValidator candidateValidator)
    {
        AuthorityReader = authorityReader ?? throw new ArgumentNullException(nameof(authorityReader));
        Authority = authority ?? throw new ArgumentNullException(nameof(authority));
        Epochs = epochs ?? throw new ArgumentNullException(nameof(epochs));
        Store = store ?? throw new ArgumentNullException(nameof(store));
        CandidateValidator = candidateValidator ?? throw new ArgumentNullException(nameof(candidateValidator));
    }

    internal IGatePlanAuthorityReader AuthorityReader { get; }
    internal InMemoryGatePlanPreparedCommitOwner Authority { get; }
    internal IPreparedGatePlanEpochPort Epochs { get; }
    internal IPreparedGateStoreControl Store { get; }
    internal IGatePlanPreviewCandidateValidator CandidateValidator { get; }

    internal GatePlanApplyRunContext OpenRun(InspectionId inspectionId, AcquisitionRunId runId)
    {
        GateStoreRunAuthoritySnapshot? outgoing;
        lock (_gate) outgoing = _currentRun;
        var owner = new GatePreparedReservationOwner(Guid.NewGuid());
        PrepareGateRunTransitionResult prepared = Store.PrepareRunTransition(
            new(outgoing, inspectionId, runId, owner));
        if (!prepared.IsPrepared)
            throw new ApplicationCommitInvariantException($"Gate store run preparation returned {prepared.Status}.");
        GatePreparedReservation reservation = prepared.Prepared.Reservation;
        GatePreparedReservationResult committed = Store.CommitRunTransition(reservation,
            reservation.Owner, reservation.Generation);
        if (committed.Status != GatePreparedReservationResultStatus.Committed)
            throw new ApplicationCommitInvariantException($"Gate store run commit returned {committed.Status}.");
        lock (_gate) _currentRun = prepared.Prepared.Incoming;
        return new(inspectionId, runId, prepared.Prepared.Incoming.Generation);
    }

    internal void StopRun(GatePlanApplyRunContext context)
    {
        GateStoreRunAuthoritySnapshot current;
        lock (_gate)
        {
            if (_currentRun is not { } found || found.InspectionId != context.InspectionId ||
                found.RunId != context.RunId || found.Generation != context.RunGeneration)
                throw new ApplicationCommitInvariantException("The gate store run context changed before Stop.");
            current = found;
        }
        GateStoreRunStopResult stopped = Store.StopCurrentRun(current);
        if (stopped.Status is not (GateStoreRunStopStatus.Stopped or GateStoreRunStopStatus.AlreadyStopped))
            throw new ApplicationCommitInvariantException($"Gate store Stop returned {stopped.Status}.");
        lock (_gate) _currentRun = stopped.Current;
    }

    internal GateStoreRunAuthoritySnapshot BeginFinalReconciliation(GatePlanApplyRunContext context)
    {
        GateStoreRunAuthoritySnapshot current = RequireCurrent(context);
        GateStoreRunStopResult transition = Store.BeginFinalReconciliation(current);
        if (transition.Status is not (GateStoreRunStopStatus.Stopped or GateStoreRunStopStatus.AlreadyStopped))
            throw new ApplicationCommitInvariantException(
                $"Gate store final reconciliation returned {transition.Status}.");
        lock (_gate)
        {
            if (_currentRun == current) _currentRun = transition.Current;
        }
        return transition.Current;
    }

    internal bool CompleteFinalReconciliation(GateStoreRunAuthoritySnapshot expected)
    {
        lock (_gate)
        {
            if (_currentRun != expected) return false;
        }
        GateStoreRunStopResult transition = Store.CompleteFinalReconciliation(expected);
        if (transition.Status == GateStoreRunStopStatus.IdentityMismatch) return false;
        if (transition.Status is not (GateStoreRunStopStatus.Stopped or GateStoreRunStopStatus.AlreadyStopped))
            throw new ApplicationCommitInvariantException(
                $"Gate store final completion returned {transition.Status}.");
        lock (_gate)
        {
            if (_currentRun == expected) _currentRun = transition.Current;
        }
        return true;
    }

    private GateStoreRunAuthoritySnapshot RequireCurrent(GatePlanApplyRunContext context)
    {
        lock (_gate)
        {
            if (_currentRun is not { } current || current.InspectionId != context.InspectionId ||
                current.RunId != context.RunId || current.Generation != context.RunGeneration)
                throw new ApplicationCommitInvariantException("The gate store run context changed.");
            return current;
        }
    }
}

internal sealed class GatePlanApplyUseCase
{
    private readonly ApplicationCommitCoordinator _coordinator;
    private readonly IGatePlanAuthorityReader _authorityReader;
    private readonly InMemoryGatePlanPreparedCommitOwner _authority;
    private readonly IPreparedGatePlanEpochPort _epochs;
    private readonly IPreparedGateStoreControl _store;
    private readonly IGatePlanPreviewCandidateValidator _candidateValidator;
    private readonly IGatePlanApplyLifecycle _lifecycle;
    private long _operationGeneration;

    internal GatePlanApplyUseCase(ApplicationCommitCoordinator coordinator,
        IGatePlanAuthorityReader authorityReader, InMemoryGatePlanPreparedCommitOwner authority,
        IPreparedGatePlanEpochPort epochs, IPreparedGateStoreControl store,
        IGatePlanApplyLifecycle lifecycle, IGatePlanPreviewCandidateValidator candidateValidator)
    {
        _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
        _authorityReader = authorityReader ?? throw new ArgumentNullException(nameof(authorityReader));
        _authority = authority ?? throw new ArgumentNullException(nameof(authority));
        _epochs = epochs ?? throw new ArgumentNullException(nameof(epochs));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _lifecycle = lifecycle ?? throw new ArgumentNullException(nameof(lifecycle));
        _candidateValidator = candidateValidator ?? throw new ArgumentNullException(nameof(candidateValidator));
    }

    internal GatePlanApplySubmission TryApply(GatePlanApplyRequest request, GatePlanApplyRunContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        GatePlanEditIntent intent = request.Intent;
        GatePlanPreviewCandidate candidate = request.Candidate;
        ArgumentNullException.ThrowIfNull(intent);
        if (intent.Kind != GatePlanEditIntentKind.Apply || intent.Draft is null)
            throw new ArgumentException("An Apply intent with a coherent draft is required.", nameof(intent));
        if (!context.IsValid || context.InspectionId != intent.Authority.InspectionId)
            throw new ArgumentException("The Apply run context is invalid.", nameof(context));

        GatePlanAuthoritySnapshot observed = _authorityReader.Read(intent.Authority);
        GatePlanEditorSnapshot editing = new(observed.Committed, intent.Draft,
            state: intent.ExpectedRevision == observed.Committed.Revision ?
                GatePlanEditorState.Editing : GatePlanEditorState.Stale);
        if (!CandidateMatchesIntent(candidate, intent) || !_candidateValidator.IsAuthentic(candidate))
            return Completed(GatePlanEditResult.InvalidPreviewArtifact(editing));

        GatePlanEditResult? semanticResult = null;

        ApplicationCommitAdmissionResult admission = _coordinator.TryAdmitGatePlanApply(context.RunId,
            new($"GatePlanApply:{intent.Authority.PlanId.Value}:{intent.ExpectedRevision.Value}"),
            () =>
            {
                long rawGeneration = Interlocked.Increment(ref _operationGeneration);
                if (rawGeneration <= 0)
                {
                    semanticResult = GatePlanEditResult.FaultedBeforeCommit(editing);
                    return ValueTask.FromResult<Action?>(null);
                }
                var operationGeneration = new GatePreparedOperationGeneration((ulong)rawGeneration);
                var owner = new GatePreparedReservationOwner(Guid.NewGuid());
                return ExecuteAsync(intent, intent.Draft, context, editing, candidate, owner,
                    operationGeneration, result => semanticResult = result);
            }, cancellationToken);
        if (!admission.IsAdmitted)
            return Rejected(admission.Status);

        return new(GatePlanApplySubmissionStatus.Admitted,
            CompleteAsync(admission.Admission!, () => semanticResult));
    }

    private ValueTask<Action?> ExecuteAsync(GatePlanEditIntent intent, GatePlanDraft draft,
        GatePlanApplyRunContext context,
        GatePlanEditorSnapshot editing, GatePlanPreviewCandidate candidate, GatePreparedReservationOwner owner,
        GatePreparedOperationGeneration operationGeneration, Action<GatePlanEditResult> setResult)
    {
        if (!_lifecycle.IsRunning(context))
        { setResult(Closed(editing)); return ValueTask.FromResult<Action?>(null); }

        GatePlanAuthoritySnapshot current = _authorityReader.Read(intent.Authority);
        if (current.Committed.Revision != intent.ExpectedRevision)
        {
            var stale = new GatePlanEditorSnapshot(current.Committed, intent.Draft,
                state: GatePlanEditorState.Stale);
            setResult(GatePlanEditResult.Conflict(intent.ExpectedRevision, stale));
            return ValueTask.FromResult<Action?>(null);
        }
        if (!CandidateMatchesIntent(candidate, intent) || !_candidateValidator.IsAuthentic(candidate))
        {
            setResult(GatePlanEditResult.InvalidPreviewArtifact(editing));
            return ValueTask.FromResult<Action?>(null);
        }
        CompiledGatePlan compiled = candidate.CompiledPlan;

        ApplicationCommitCoordinatorSnapshot frontier = _coordinator.Snapshot;
        if (frontier.RevisionRun.IsValid && frontier.RevisionRun != context.RunId)
            throw new ApplicationCommitInvariantException("The RF frontier belongs to another run.");
        if (frontier.LastConfirmedRevision.Value == ulong.MaxValue)
        { setResult(GatePlanEditResult.FaultedBeforeCommit(editing)); return ValueTask.FromResult<Action?>(null); }
        var effectiveFrom = new InspectionRevision(frontier.LastConfirmedRevision.Value + 1);

        GatePlanEpochPrepareResult epoch = _epochs.PrepareEpoch(context.InspectionId, context.RunId,
            compiled, effectiveFrom, owner, operationGeneration);
        if (!epoch.IsPrepared)
        {
            setResult(MapEpoch(epoch.Status, editing));
            return ValueTask.FromResult<Action?>(null);
        }

        PrepareDesiredReplacementResult desired = _store.PrepareDesiredReplacement(
            new(context.InspectionId, context.RunId, context.RunGeneration, intent.Authority.PlanId,
                compiled.Source.Revision, owner));
        if (!desired.IsPrepared)
        {
            RequireAbort(_epochs.AbortEpoch(epoch.Reservation, owner, operationGeneration), "epoch");
            setResult(MapDesired(desired.Status, editing));
            return ValueTask.FromResult<Action?>(null);
        }

        GatePlanAuthorityPrepareResult authority = _authority.Prepare(new(intent.Authority,
            intent.ExpectedRevision, draft, context.RunId, context.RunGeneration, owner,
            operationGeneration, compiled));
        if (!authority.IsPrepared)
        {
            RequireAbort(_store.AbortDesiredReplacement(desired.Prepared.Reservation,
                desired.Prepared.Reservation.Owner, desired.Prepared.Reservation.Generation), "desired replacement");
            RequireAbort(_epochs.AbortEpoch(epoch.Reservation, owner, operationGeneration), "epoch");
            setResult(MapAuthority(authority, editing, intent, current));
            return ValueTask.FromResult<Action?>(null);
        }

        if (!_lifecycle.TryCrossPointOfNoReturn(context))
        {
            RequireAbort(_authority.Abort(authority.Reservation, owner, operationGeneration), "authority");
            RequireAbort(_store.AbortDesiredReplacement(desired.Prepared.Reservation,
                desired.Prepared.Reservation.Owner, desired.Prepared.Reservation.Generation), "desired replacement");
            RequireAbort(_epochs.AbortEpoch(epoch.Reservation, owner, operationGeneration), "epoch");
            setResult(Closed(editing));
            return ValueTask.FromResult<Action?>(null);
        }

        try
        {
            GatePreparedReservationResult epochCommit = _epochs.CommitEpoch(epoch.Reservation, owner, operationGeneration);
            if (epochCommit.Status != GatePreparedReservationResultStatus.Committed)
            {
                AbortAfterInvariant(() => _authority.Abort(authority.Reservation, owner, operationGeneration));
                AbortAfterInvariant(() => _store.AbortDesiredReplacement(desired.Prepared.Reservation,
                    desired.Prepared.Reservation.Owner, desired.Prepared.Reservation.Generation));
                AbortAfterInvariant(() => _epochs.AbortEpoch(epoch.Reservation, owner, operationGeneration));
                throw InvariantCommit("epoch", epochCommit);
            }
            GatePreparedReservationResult desiredCommit = _store.CommitDesiredReplacement(desired.Prepared.Reservation,
                desired.Prepared.Reservation.Owner, desired.Prepared.Reservation.Generation);
            if (desiredCommit.Status != GatePreparedReservationResultStatus.Committed)
            {
                AbortAfterInvariant(() => _authority.Abort(authority.Reservation, owner, operationGeneration));
                AbortAfterInvariant(() => _store.AbortDesiredReplacement(desired.Prepared.Reservation,
                    desired.Prepared.Reservation.Owner, desired.Prepared.Reservation.Generation));
                throw InvariantCommit("desired replacement", desiredCommit);
            }
            GatePlanAuthorityCommitResult committed = _authority.Commit(authority.Reservation, owner, operationGeneration);
            if (committed.Operation.Status != GatePreparedReservationResultStatus.Committed)
            {
                AbortAfterInvariant(() => _authority.Abort(authority.Reservation, owner, operationGeneration));
                throw InvariantCommit("authority", committed.Operation);
            }
            GatePlanAuthorityInstalledState installed = _authority.ReadInstalled(intent.Authority) ??
                throw new ApplicationCommitInvariantException("The committed authority aggregate is unavailable.");
            var resulting = new GatePlanEditorSnapshot(installed.Authority.Committed,
                state: GatePlanEditorState.Committed);
            setResult(GatePlanEditResult.Accepted(editing, resulting));
            GatePlanAuthorityDeferredPublication publication = committed.Publication ??
                throw new ApplicationCommitInvariantException("The authority commit did not return its publication ticket.");
            return ValueTask.FromResult<Action?>(() =>
            {
                if (!publication.Publish())
                    throw new ApplicationCommitInvariantException("The confirmed authority publication was not admitted.");
            });
        }
        catch (Exception error)
        {
            AbortAfterInvariant(() => _authority.Abort(authority.Reservation, owner, operationGeneration));
            AbortAfterInvariant(() => _store.AbortDesiredReplacement(desired.Prepared.Reservation,
                desired.Prepared.Reservation.Owner, desired.Prepared.Reservation.Generation));
            AbortAfterInvariant(() => _epochs.AbortEpoch(epoch.Reservation, owner, operationGeneration));
            if (error is ApplicationCommitInvariantException) throw;
            throw new ApplicationCommitInvariantException("The aggregate commit failed after its point of no return.", error);
        }
    }

    private static async Task<GatePlanEditResult> CompleteAsync(ApplicationCommitAdmission admission,
        Func<GatePlanEditResult?> result)
    {
        ApplicationCommitTerminalResult terminal = await admission.Completion.ConfigureAwait(false);
        if (terminal.Status is ApplicationCommitTerminalStatus.InvariantFaulted or
            ApplicationCommitTerminalStatus.SkippedAfterInvariantFault)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(terminal.Error ??
                new ApplicationCommitInvariantException("The aggregate commit coordinator faulted.")).Throw();
        if (terminal.Status != ApplicationCommitTerminalStatus.Completed)
            throw terminal.Error ?? new InvalidOperationException($"Apply ended as {terminal.Status}.");
        return result() ?? throw new ApplicationCommitInvariantException("Apply completed without a semantic result.");
    }

    private static GatePlanApplySubmission Completed(GatePlanEditResult result) =>
        new(GatePlanApplySubmissionStatus.CompletedBeforeAdmission, Task.FromResult(result));

    private static GatePlanApplySubmission Rejected(ApplicationCommitAdmissionStatus status) => status switch
    {
        ApplicationCommitAdmissionStatus.CancelledBeforeAdmission =>
            new(GatePlanApplySubmissionStatus.CancelledBeforeAdmission),
        ApplicationCommitAdmissionStatus.AdmissionClosed =>
            new(GatePlanApplySubmissionStatus.AdmissionClosed),
        ApplicationCommitAdmissionStatus.CapacityExceeded =>
            new(GatePlanApplySubmissionStatus.CoordinatorCapacityExceeded),
        ApplicationCommitAdmissionStatus.OrdinalExhausted =>
            new(GatePlanApplySubmissionStatus.CoordinatorOrdinalExhausted),
        _ => throw new ArgumentOutOfRangeException(nameof(status))
    };

    private static GatePlanEditResult MapEpoch(GatePlanEpochPrepareStatus status,
        GatePlanEditorSnapshot editing) => status switch
        {
            GatePlanEpochPrepareStatus.LiveEpochLimitReached or GatePlanEpochPrepareStatus.ProvisionalCapacityExceeded =>
                GatePlanEditResult.CapacityExceeded(editing, GatePlanCapacityResource.LiveEpochs),
            GatePlanEpochPrepareStatus.RunClosed or GatePlanEpochPrepareStatus.RunNotFound or
                GatePlanEpochPrepareStatus.Disposed => Closed(editing),
            _ => throw new ApplicationCommitInvariantException($"Unexpected epoch preparation result: {status}.")
        };

    private static GatePlanEditResult MapDesired(PrepareDesiredReplacementStatus status,
        GatePlanEditorSnapshot editing) => status switch
        {
            PrepareDesiredReplacementStatus.CapacityExceeded =>
                GatePlanEditResult.CapacityExceeded(editing, GatePlanCapacityResource.DerivedStore),
            PrepareDesiredReplacementStatus.Closed or PrepareDesiredReplacementStatus.RunNotOpen => Closed(editing),
            PrepareDesiredReplacementStatus.CounterOverflow => GatePlanEditResult.FaultedBeforeCommit(editing),
            _ => throw new ApplicationCommitInvariantException($"Unexpected desired-replacement result: {status}.")
        };

    private static GatePlanEditResult MapAuthority(GatePlanAuthorityPrepareResult result,
        GatePlanEditorSnapshot editing, GatePlanEditIntent intent, GatePlanAuthoritySnapshot current) => result.Status switch
        {
            GatePlanAuthorityPrepareStatus.Conflict => GatePlanEditResult.Conflict(intent.ExpectedRevision,
                new(current.Committed, intent.Draft, state: GatePlanEditorState.Stale)),
            GatePlanAuthorityPrepareStatus.RevisionExhausted => GatePlanEditResult.RevisionExhausted(editing),
            GatePlanAuthorityPrepareStatus.LedgerSlotCapacityExceeded =>
                GatePlanEditResult.CapacityExceeded(editing, GatePlanCapacityResource.LedgerRevisions),
            GatePlanAuthorityPrepareStatus.LedgerByteCapacityExceeded or GatePlanAuthorityPrepareStatus.LedgerEstimateOverflow =>
                GatePlanEditResult.CapacityExceeded(editing, GatePlanCapacityResource.LedgerBytes),
            GatePlanAuthorityPrepareStatus.Closed => Closed(editing),
            GatePlanAuthorityPrepareStatus.GenerationExhausted => GatePlanEditResult.FaultedBeforeCommit(editing),
            _ => throw new ApplicationCommitInvariantException($"Unexpected authority preparation result: {result.Status}.")
        };

    private static GatePlanEditResult Closed(GatePlanEditorSnapshot editing) =>
        GatePlanEditResult.Closed(new(editing.Committed, state: GatePlanEditorState.Closed), editing.CurrentRevision);

    private static bool CandidateMatchesIntent(GatePlanPreviewCandidate candidate, GatePlanEditIntent intent) =>
        ReferenceEquals(candidate.Draft, intent.Draft) && candidate.Draft.Authority == intent.Authority &&
        candidate.BaseRevision == intent.ExpectedRevision && intent.ExpectedRevision.Value != uint.MaxValue &&
        candidate.CandidateRevision.Value == intent.ExpectedRevision.Value + 1 &&
        candidate.CompiledPlan.Source.Id == intent.Authority.PlanId &&
        candidate.CompiledPlan.Source.Revision == candidate.CandidateRevision &&
        candidate.CompiledPlan.Signature.Equals(candidate.Signature);

    private static void RequireAbort(GatePreparedReservationResult result, string owner)
    {
        if (result.Status != GatePreparedReservationResultStatus.Aborted)
            throw new ApplicationCommitInvariantException($"Prepared {owner} abort returned {result.Status}.");
    }

    private static ApplicationCommitInvariantException InvariantCommit(string owner,
        GatePreparedReservationResult result) =>
        new($"Prepared {owner} commit returned {result.Status}.");

    private static void AbortAfterInvariant(Func<GatePreparedReservationResult> abort)
    {
        try
        {
            GatePreparedReservationResult result = abort();
            if (result.Status is not (GatePreparedReservationResultStatus.Aborted or
                GatePreparedReservationResultStatus.AlreadyAborted or
                GatePreparedReservationResultStatus.AlreadyCommitted))
                throw new ApplicationCommitInvariantException(
                    $"Invariant cleanup returned {result.Status}.");
        }
        catch
        {
            // The original commit-contract violation remains primary; owner Dispose is the final safety net.
        }
    }
}
