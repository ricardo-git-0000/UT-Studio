using UTStudio.Contracts.Inspection;
using UTStudio.Contracts.Processing;
using UTStudio.Domain.Acquisition;
using UTStudio.Domain.Inspection;
using UTStudio.Domain.Processing;

namespace UTStudio.Processing.Core;

public enum GatePlanEpochPrepareStatus
{
    Unknown,
    Prepared,
    InvalidIdentity,
    RunNotFound,
    RunClosed,
    Disposed,
    InvalidPlan,
    InvalidBoundary,
    DuplicatePlan,
    ProvisionalCapacityExceeded,
    LiveEpochLimitReached
}

public readonly record struct GatePlanEpochPrepareResult
{
    private GatePlanEpochPrepareResult(GatePlanEpochPrepareStatus status,
        GatePreparedReservation reservation)
    {
        if (!Enum.IsDefined(status) || status == GatePlanEpochPrepareStatus.Unknown)
            throw new ArgumentOutOfRangeException(nameof(status));
        if ((status == GatePlanEpochPrepareStatus.Prepared) != reservation.IsValid)
            throw new ArgumentException("Only a successful preparation carries a reservation.", nameof(reservation));
        Status = status;
        Reservation = reservation;
    }

    public GatePlanEpochPrepareStatus Status { get; }
    public GatePreparedReservation Reservation { get; }
    public bool IsPrepared => Status == GatePlanEpochPrepareStatus.Prepared;

    public static GatePlanEpochPrepareResult Prepared(GatePreparedReservation reservation) =>
        new(GatePlanEpochPrepareStatus.Prepared, reservation);

    public static GatePlanEpochPrepareResult Rejected(GatePlanEpochPrepareStatus status) =>
        status == GatePlanEpochPrepareStatus.Prepared
            ? throw new ArgumentOutOfRangeException(nameof(status))
            : new(status, default);
}

/// <summary>
/// Minimal neutral entry point for the next Application increment. A successful prepare owns one provisional
/// live-epoch slot until exactly one Commit or Abort consumes it. Commit and Abort are synchronous and invoke no
/// callbacks. Each run retains only its most recent terminal reservation for idempotent replay; preparing the
/// next reservation makes older terminal identifiers invalid.
/// </summary>
public interface IPreparedGatePlanEpochPort
{
    GatePlanEpochPrepareResult PrepareEpoch(InspectionId inspectionId, AcquisitionRunId runId,
        CompiledGatePlan plan, InspectionRevision effectiveFromInclusive,
        GatePreparedReservationOwner owner, GatePreparedOperationGeneration generation);

    GatePreparedReservationResult CommitEpoch(GatePreparedReservation reservation,
        GatePreparedReservationOwner owner, GatePreparedOperationGeneration generation);

    GatePreparedReservationResult AbortEpoch(GatePreparedReservation reservation,
        GatePreparedReservationOwner owner, GatePreparedOperationGeneration generation);
}
