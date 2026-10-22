using UTStudio.Contracts.Inspection;
using UTStudio.Contracts.Processing;
using UTStudio.Domain.Inspection;
using UTStudio.Processing.Core;

namespace UTStudio.Application;

/// <summary>
/// Inspection-scoped owner for sealed-run handoffs. It survives run switches and drains every admitted observer
/// before disposing the scheduler, replacement store and authority, in that order.
/// </summary>
internal sealed class GateHistoricalRunServices : IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly GateHistoricalReplacementCoordinator _coordinator;
    private readonly IOwnedGateReprocessingLifecycle _scheduler;
    private readonly IGateReplacementStoreLifecycle _store;
    private readonly InMemoryGatePlanAuthorityOwner _authorityOwner;
    private Task? _disposeTask;

    internal GateHistoricalRunServices(GateHistoricalReplacementCoordinator coordinator,
        IOwnedGateReprocessingLifecycle scheduler, IGateReplacementStoreLifecycle store,
        InMemoryGatePlanAuthorityOwner authorityOwner)
    {
        _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
        _scheduler = scheduler ?? throw new ArgumentNullException(nameof(scheduler));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _authorityOwner = authorityOwner ?? throw new ArgumentNullException(nameof(authorityOwner));
    }

    internal async ValueTask<GateHistoricalReplacementSubmission> HandoffAsync(GatePlanApplyServices plans,
        GatePlanApplyRunContext run, InspectionRunSealResult seal, UtStreamIdentity stream,
        int blockSize = 64)
    {
        ArgumentNullException.ThrowIfNull(plans);
        if (!run.IsValid || seal.RunId != run.RunId || seal.FinalRevision == InspectionRevision.Initial ||
            !stream.IsValid || blockSize <= 0)
            throw new ArgumentException("The sealed-run handoff is invalid.");
        GateStoreRunAuthoritySnapshot finalizing = plans.BeginFinalReconciliation(run);
        try
        {
            GateHistoricalSealResolution target = plans.Authority.ResolveHistoricalAtSeal(
                run.InspectionId, run.RunId, run.RunGeneration);
            if (!target.HasTarget)
            {
                plans.CompleteFinalReconciliation(finalizing);
                return new(target.Status == GateHistoricalSealResolutionStatus.Closed ?
                    GateHistoricalReplacementSubmissionStatus.Closed :
                    GateHistoricalReplacementSubmissionStatus.Superseded);
            }

            if (seal.TraceCount == 0)
            {
                if (seal.MinimumAcquisitionIndex is not null || seal.MaximumAcquisitionIndex is not null)
                    throw new ApplicationCommitInvariantException("An empty sealed run published acquisition bounds.");
                plans.Authority.CompleteHistoricalWithoutData(target.Authority, target.PlanRevision);
                plans.CompleteFinalReconciliation(finalizing);
                return new(GateHistoricalReplacementSubmissionStatus.Superseded);
            }
            if (seal.MinimumAcquisitionIndex is not { } minimum || seal.MaximumAcquisitionIndex is not { } maximum ||
                minimum > maximum)
                throw new ApplicationCommitInvariantException("The sealed run did not publish authoritative bounds.");

            GateHistoricalReplacementSubmission submission = await _coordinator.TryAdmitAsync(target.Authority, run,
                seal.FinalRevision, [new GateReprocessingStreamSelection(stream, [new(minimum, maximum)])], blockSize,
                CancellationToken.None, _ =>
                {
                    plans.CompleteFinalReconciliation(finalizing);
                    return ValueTask.CompletedTask;
                }).ConfigureAwait(false);
            if (!submission.IsAdmitted) plans.CompleteFinalReconciliation(finalizing);
            return submission;
        }
        catch
        {
            plans.CompleteFinalReconciliation(finalizing);
            throw;
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (_gate) return new(_disposeTask ??= DisposeCoreAsync());
    }

    private async Task DisposeCoreAsync()
    {
        _coordinator.CloseAdmission();
        _scheduler.CloseAdmission();
        List<Exception> failures = [];
        try { await _scheduler.StopAsync().ConfigureAwait(false); }
        catch (Exception error) { failures.Add(error); }
        try { await _coordinator.DrainAsync().ConfigureAwait(false); }
        catch (Exception error) { failures.Add(error); }
        try { await _scheduler.DisposeAsync().ConfigureAwait(false); }
        catch (Exception error) { failures.Add(error); }
        try { await _store.DisposeAsync().ConfigureAwait(false); }
        catch (Exception error) { failures.Add(error); }
        try
        {
            _authorityOwner.Dispose();
            await _authorityOwner.WaitForCloseAsync().ConfigureAwait(false);
        }
        catch (Exception error) { failures.Add(error); }
        if (failures.Count == 1)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failures[0]).Throw();
        if (failures.Count > 1) throw new AggregateException(failures);
    }
}
