using UTStudio.Contracts.Inspection;
using UTStudio.Domain.Acquisition;
using UTStudio.Domain.Inspection;
using UTStudio.Domain.Processing;

namespace UTStudio.Contracts.Processing;

public enum GateProcessingAdmissionStatus { Unknown, Accepted, Coalesced, Duplicate, Late, Rejected, Closed }
public enum GateBatchDeliveryStatus { Unknown, Accepted, Rejected, Failed, Cancelled }
public enum GateProcessingLifecycleState { Unknown, Created, Running, AdmissionClosed, FinalReconciling, Draining, Stopped, Disposing, Closed, Faulted }

public readonly struct GateProcessingSignal
{
    public GateProcessingSignal(TraceIdentity traceIdentity, InspectionRevision revision)
    { TraceIdentity = traceIdentity; Revision = revision; }
    public TraceIdentity TraceIdentity { get; }
    public InspectionRevision Revision { get; }
    public bool IsValid => TraceIdentity.IsValid && Revision != InspectionRevision.Initial;
}

public sealed record GateProcessingRunRegistration
{
    public GateProcessingRunRegistration(InspectionId inspectionId, AcquisitionRunId runId,
        IReadOnlyList<UtStreamIdentity> streams, CompiledGatePlan plan)
    {
        if (!inspectionId.IsValid || !runId.IsValid) { throw new ArgumentException("Inspection and run must be valid."); }
        ArgumentNullException.ThrowIfNull(streams); ArgumentNullException.ThrowIfNull(plan);
        UtStreamIdentity[] copy = streams.Distinct().ToArray();
        if (copy.Length == 0 || copy.Any(x => !x.IsValid || x.InspectionId != inspectionId || x.AcquisitionRunId != runId))
        { throw new ArgumentException("At least one matching stream is required.", nameof(streams)); }
        InspectionId = inspectionId; RunId = runId; Streams = Array.AsReadOnly(copy); Plan = plan;
    }
    public InspectionId InspectionId { get; }
    public AcquisitionRunId RunId { get; }
    public IReadOnlyList<UtStreamIdentity> Streams { get; }
    public CompiledGatePlan Plan { get; }
}

public readonly struct GateBatchDeliveryResult
{
    public GateBatchDeliveryResult(GateBatchDeliveryStatus status, string? errorCode = null)
    { Status = status; ErrorCode = errorCode; }
    public GateBatchDeliveryStatus Status { get; }
    public string? ErrorCode { get; }
    public static GateBatchDeliveryResult Accepted() => new(GateBatchDeliveryStatus.Accepted);
}

public interface IGateEvaluationBatchSink
{
    /// <summary>
    /// Awaited serially in InspectionRevision order. The immutable batch is borrowed only
    /// for this call and must not be retained. Implementations may re-enter the ingress;
    /// rejection, failure, and exceptional cancellation are explicit delivery outcomes.
    /// </summary>
    ValueTask<GateBatchDeliveryResult> DeliverAsync(GateEvaluationBatch batch,
        CancellationToken exceptionalCancellation = default);
}

public interface ICommittedGateProcessingIngress
{
    GateProcessingAdmissionStatus OpenRun(GateProcessingRunRegistration registration);
    GateProcessingAdmissionStatus NotifyCommitted(GateProcessingSignal signal);
    void NotifyProviderProgress(AcquisitionRunId runId);
    void RequestFinalReconciliation(AcquisitionRunId runId, InspectionRevision finalRevision);
    void StopAdmission(AcquisitionRunId runId);
    ValueTask WaitForDrainAsync(AcquisitionRunId runId);
}

public sealed class GateProcessingError
{
    public GateProcessingError(string code, string message)
    { ArgumentException.ThrowIfNullOrWhiteSpace(code); ArgumentException.ThrowIfNullOrWhiteSpace(message); Code = code; Message = message; }
    public string Code { get; }
    public string Message { get; }
}

public sealed class GateProcessingStreamWatermark
{
    public GateProcessingStreamWatermark(UtStreamIdentity stream, InspectionRevision observedRevision)
    { Stream = stream; ObservedRevision = observedRevision; }
    public UtStreamIdentity Stream { get; }
    public InspectionRevision ObservedRevision { get; }
}

public sealed class GateProcessingPlanBalance
{
    public GateProcessingPlanBalance(GatePlanRevision planRevision, GatePlanSignature planSignature,
        long confirmedEligible, long evaluatedAndDelivered, long explicitlyRejectedBeforeAdmission,
        long terminallyFailed, long explicitlyUnprocessedByPlanTransition)
    {
        PlanRevision = planRevision; PlanSignature = planSignature ?? throw new ArgumentNullException(nameof(planSignature));
        ConfirmedEligible = confirmedEligible; EvaluatedAndDelivered = evaluatedAndDelivered;
        ExplicitlyRejectedBeforeAdmission = explicitlyRejectedBeforeAdmission; TerminallyFailed = terminallyFailed;
        ExplicitlyUnprocessedByPlanTransition = explicitlyUnprocessedByPlanTransition;
    }
    public GatePlanRevision PlanRevision { get; }
    public GatePlanSignature PlanSignature { get; }
    public long ConfirmedEligible { get; }
    public long EvaluatedAndDelivered { get; }
    public long ExplicitlyRejectedBeforeAdmission { get; }
    public long TerminallyFailed { get; }
    public long ExplicitlyUnprocessedByPlanTransition { get; }
    public long Terminal => EvaluatedAndDelivered + ExplicitlyRejectedBeforeAdmission + TerminallyFailed + ExplicitlyUnprocessedByPlanTransition;
    public long Pending => ConfirmedEligible - Terminal;
    public bool IsLosslessBalanced => Pending == 0;
}

public sealed class GateProcessingRunDiagnostics
{
    public GateProcessingRunDiagnostics(AcquisitionRunId runId, GateProcessingLifecycleState lifecycle,
        GatePlanRevision planRevision, GatePlanSignature planSignature, InspectionRevision targetRevision,
        InspectionRevision observedRevision, InspectionRevision reconciledRevision, InspectionRevision? finalRevision,
        bool finalityConfirmed, IReadOnlyList<GateProcessingStreamWatermark> watermarks,
        IReadOnlyList<GateProcessingPlanBalance> plans, long workAdmitted, long workTerminal,
        int activeWorkers, int activeCallbacks, int activeLeases, GateProcessingError? lastError)
    {
        RunId = runId; Lifecycle = lifecycle; PlanRevision = planRevision;
        PlanSignature = planSignature ?? throw new ArgumentNullException(nameof(planSignature));
        TargetRevision = targetRevision; ObservedRevision = observedRevision; ReconciledRevision = reconciledRevision;
        FinalRevision = finalRevision; FinalityConfirmed = finalityConfirmed;
        Watermarks = Array.AsReadOnly((watermarks ?? throw new ArgumentNullException(nameof(watermarks))).ToArray());
        Plans = Array.AsReadOnly((plans ?? throw new ArgumentNullException(nameof(plans))).ToArray());
        WorkAdmitted = workAdmitted; WorkTerminal = workTerminal; ActiveWorkers = activeWorkers;
        ActiveCallbacks = activeCallbacks; ActiveLeases = activeLeases; LastError = lastError;
    }
    public AcquisitionRunId RunId { get; }
    public GateProcessingLifecycleState Lifecycle { get; }
    public GatePlanRevision PlanRevision { get; }
    public GatePlanSignature PlanSignature { get; }
    public InspectionRevision TargetRevision { get; }
    public InspectionRevision ObservedRevision { get; }
    public InspectionRevision ReconciledRevision { get; }
    public InspectionRevision? FinalRevision { get; }
    public bool FinalityConfirmed { get; }
    public IReadOnlyList<GateProcessingStreamWatermark> Watermarks { get; }
    public IReadOnlyList<GateProcessingPlanBalance> Plans { get; }
    public long WorkAdmitted { get; }
    public long WorkTerminal { get; }
    public long WorkPending => WorkAdmitted - WorkTerminal;
    public int ActiveWorkers { get; }
    public int ActiveCallbacks { get; }
    public int ActiveLeases { get; }
    public GateProcessingError? LastError { get; }
}

public sealed class GateProcessingDiagnostics
{
    public GateProcessingDiagnostics(GateProcessingLifecycleState state, long signalsReceived, long signalsCoalesced,
        long duplicateSignals, long lateSignals, long unitsAdmitted, long unitsRejected, long saturations,
        long revisionsObserved, long revisionsReconciled, long providerAvailable, long providerNotAvailable,
        long providerGap, long providerOutOfRange, long providerCancelled, long providerClosed, long providerFaulted,
        long leasesAcquired, long leasesReleased, int activeLeases, long evaluationsStarted, long evaluationsCompleted,
        long evaluationsCancelled, long evaluationsFailed, long batchesAccepted, long batchesRejected,
        long batchesFailed, long callbacksAdmitted, long callbacksCompleted,
        int activeWorkers, int activeCallbacks, int schedulerDepth, int peakSchedulerDepth, int schedulerCapacity,
        int openRuns, long drainedRuns, long failedRuns, IReadOnlyList<GateProcessingRunDiagnostics> activeRuns,
        IReadOnlyList<GateProcessingRunDiagnostics> terminalRuns, GateProcessingError? primaryError,
        IReadOnlyList<GateProcessingError> secondaryErrors)
    {
        State = state; SignalsReceived = signalsReceived; SignalsCoalesced = signalsCoalesced;
        DuplicateSignals = duplicateSignals; LateSignals = lateSignals; UnitsAdmitted = unitsAdmitted;
        UnitsRejected = unitsRejected; Saturations = saturations; RevisionsObserved = revisionsObserved;
        RevisionsReconciled = revisionsReconciled; ProviderAvailable = providerAvailable;
        ProviderNotAvailable = providerNotAvailable; ProviderGap = providerGap; ProviderOutOfRange = providerOutOfRange;
        ProviderCancelled = providerCancelled; ProviderClosed = providerClosed; ProviderFaulted = providerFaulted;
        LeasesAcquired = leasesAcquired; LeasesReleased = leasesReleased; ActiveLeases = activeLeases;
        EvaluationsStarted = evaluationsStarted; EvaluationsCompleted = evaluationsCompleted;
        EvaluationsCancelled = evaluationsCancelled; EvaluationsFailed = evaluationsFailed;
        BatchesAccepted = batchesAccepted; BatchesRejected = batchesRejected; BatchesFailed = batchesFailed;
        CallbacksAdmitted = callbacksAdmitted; CallbacksCompleted = callbacksCompleted;
        ActiveWorkers = activeWorkers; ActiveCallbacks = activeCallbacks; SchedulerDepth = schedulerDepth;
        PeakSchedulerDepth = peakSchedulerDepth; SchedulerCapacity = schedulerCapacity; OpenRuns = openRuns;
        DrainedRuns = drainedRuns; FailedRuns = failedRuns;
        ActiveRuns = Array.AsReadOnly((activeRuns ?? throw new ArgumentNullException(nameof(activeRuns))).ToArray());
        TerminalRuns = Array.AsReadOnly((terminalRuns ?? throw new ArgumentNullException(nameof(terminalRuns))).ToArray());
        PrimaryError = primaryError;
        SecondaryErrors = secondaryErrors ?? throw new ArgumentNullException(nameof(secondaryErrors));
    }
    public GateProcessingLifecycleState State { get; }
    public long SignalsReceived { get; }
    public long SignalsCoalesced { get; }
    public long DuplicateSignals { get; }
    public long LateSignals { get; }
    public long UnitsAdmitted { get; }
    public long UnitsRejected { get; }
    public long Saturations { get; }
    public long RevisionsObserved { get; }
    public long RevisionsReconciled { get; }
    public long ProviderAvailable { get; }
    public long ProviderNotAvailable { get; }
    public long ProviderGap { get; }
    public long ProviderOutOfRange { get; }
    public long ProviderCancelled { get; }
    public long ProviderClosed { get; }
    public long ProviderFaulted { get; }
    public long LeasesAcquired { get; }
    public long LeasesReleased { get; }
    public int ActiveLeases { get; }
    public long EvaluationsStarted { get; }
    public long EvaluationsCompleted { get; }
    public long EvaluationsCancelled { get; }
    public long EvaluationsFailed { get; }
    public long BatchesAccepted { get; }
    public long BatchesRejected { get; }
    public long BatchesFailed { get; }
    public long CallbacksAdmitted { get; }
    public long CallbacksCompleted { get; }
    public int ActiveWorkers { get; }
    public int ActiveCallbacks { get; }
    public int SchedulerDepth { get; }
    public int PeakSchedulerDepth { get; }
    public int SchedulerCapacity { get; }
    public int OpenRuns { get; }
    public long DrainedRuns { get; }
    public long FailedRuns { get; }
    public IReadOnlyList<GateProcessingRunDiagnostics> ActiveRuns { get; }
    public IReadOnlyList<GateProcessingRunDiagnostics> TerminalRuns { get; }
    public GateProcessingError? PrimaryError { get; }
    public IReadOnlyList<GateProcessingError> SecondaryErrors { get; }
}
