using UTStudio.Contracts.Processing;
using UTStudio.Domain.Acquisition;
using UTStudio.Domain.Inspection;

namespace UTStudio.Processing.Core;

internal enum GateStoreRunLifecycle { None, Open, Stopped }
internal enum GateStorePreparedOperationKind { None, DesiredReplacement, RunTransition }

internal readonly record struct GateStoreRunAuthoritySnapshot(
    InspectionId InspectionId,
    AcquisitionRunId RunId,
    GateRunGeneration Generation,
    GateStoreRunLifecycle Lifecycle)
{
    internal bool IsValid => InspectionId.IsValid && RunId.IsValid && Generation.IsValid &&
        Lifecycle is GateStoreRunLifecycle.Open or GateStoreRunLifecycle.Stopped;
}

internal enum GateStoreRunStopStatus
{
    Unknown,
    Stopped,
    AlreadyStopped,
    NotConfigured,
    IdentityMismatch,
    Closed
}

internal readonly record struct GateStoreRunStopResult(
    GateStoreRunStopStatus Status,
    GateStoreRunAuthoritySnapshot Current);

internal readonly record struct PrepareGateRunTransitionRequest(
    GateStoreRunAuthoritySnapshot? ExpectedOutgoing,
    InspectionId IncomingInspectionId,
    AcquisitionRunId IncomingRunId,
    GatePreparedReservationOwner Owner);

internal enum PrepareGateRunTransitionStatus
{
    Unknown,
    Prepared,
    InvalidIdentity,
    Closed,
    AlreadyPrepared,
    OutgoingNotConfigured,
    OutgoingNotStopped,
    OutgoingMismatch,
    IncomingMatchesOutgoing,
    CounterOverflow
}

internal readonly record struct PreparedGateRunTransition(
    GatePreparedReservation Reservation,
    GateStoreRunAuthoritySnapshot? Outgoing,
    GateStoreRunAuthoritySnapshot Incoming)
{
    internal bool IsValid => Reservation.IsValid && Incoming.IsValid && Incoming.Lifecycle == GateStoreRunLifecycle.Open;
}

internal readonly record struct PrepareGateRunTransitionResult
{
    private PrepareGateRunTransitionResult(PrepareGateRunTransitionStatus status,
        PreparedGateRunTransition prepared)
    { Status = status; Prepared = prepared; }

    internal PrepareGateRunTransitionStatus Status { get; }
    internal PreparedGateRunTransition Prepared { get; }
    internal bool IsPrepared => Status == PrepareGateRunTransitionStatus.Prepared;

    internal static PrepareGateRunTransitionResult Success(PreparedGateRunTransition prepared) =>
        new(PrepareGateRunTransitionStatus.Prepared, prepared);

    internal static PrepareGateRunTransitionResult Rejected(PrepareGateRunTransitionStatus status) =>
        status is PrepareGateRunTransitionStatus.Unknown or PrepareGateRunTransitionStatus.Prepared
            ? throw new ArgumentOutOfRangeException(nameof(status))
            : new(status, default);
}

internal readonly record struct GateRunTransitionReservationTombstone(
    GatePreparedReservationId ReservationId,
    GatePreparedReservationOwner Owner,
    GatePreparedOperationGeneration Generation,
    GatePreparedReservationState State)
{
    internal GatePreparedReservation Snapshot() => new(ReservationId, Owner, Generation, State);
}

internal readonly record struct GateStoreRunControlDiagnostics(
    GateStoreRunAuthoritySnapshot Current,
    bool HasPreparedTransition,
    int TerminalTransitionTombstones,
    int TerminalTransitionTombstoneCapacity);

internal interface IPreparedGateStoreControl
{
    PrepareDesiredReplacementResult PrepareDesiredReplacement(PrepareDesiredReplacementRequest request);
    GatePreparedReservationResult CommitDesiredReplacement(GatePreparedReservation reservation,
        GatePreparedReservationOwner owner, GatePreparedOperationGeneration generation);
    GatePreparedReservationResult AbortDesiredReplacement(GatePreparedReservation reservation,
        GatePreparedReservationOwner owner, GatePreparedOperationGeneration generation);
    PrepareGateRunTransitionResult PrepareRunTransition(PrepareGateRunTransitionRequest request);
    GatePreparedReservationResult CommitRunTransition(GatePreparedReservation reservation,
        GatePreparedReservationOwner owner, GatePreparedOperationGeneration generation);
    GatePreparedReservationResult AbortRunTransition(GatePreparedReservation reservation,
        GatePreparedReservationOwner owner, GatePreparedOperationGeneration generation);
    GateStoreRunStopResult StopCurrentRun(GateStoreRunAuthoritySnapshot expected);
}

internal sealed class PreparedGateRunTransitionState
{
    internal PreparedGateRunTransitionState(GatePreparedReservation reservation,
        GateStoreRunAuthoritySnapshot? outgoing, GateStoreRunAuthoritySnapshot incoming,
        DesiredReplacementKey[] outgoingDesiredKeys)
    {
        Reservation = reservation;
        Outgoing = outgoing;
        Incoming = incoming;
        OutgoingDesiredKeys = outgoingDesiredKeys;
    }

    internal GatePreparedReservation Reservation { get; }
    internal GateStoreRunAuthoritySnapshot? Outgoing { get; }
    internal GateStoreRunAuthoritySnapshot Incoming { get; }
    internal DesiredReplacementKey[] OutgoingDesiredKeys { get; }
}
