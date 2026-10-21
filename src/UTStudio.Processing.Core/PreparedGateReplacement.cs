using UTStudio.Contracts.Processing;
using UTStudio.Domain.Acquisition;
using UTStudio.Domain.Inspection;
using UTStudio.Domain.Processing;

namespace UTStudio.Processing.Core;

internal readonly record struct DesiredReplacementKey(InspectionId InspectionId, GatePlanId GatePlanId)
{
    internal bool IsValid => InspectionId.IsValid && GatePlanId.IsValid;
}

internal readonly record struct DesiredReplacementControlSlot(bool IsProvisional);

internal sealed record DesiredReplacementControlLimits
{
    internal const int DefaultMaximumInstalledDesiredReplacements = 64;
    internal const int DefaultTerminalReservationTombstoneCapacity = 64;
    internal const int MaximumControlEntries = 4096;

    internal DesiredReplacementControlLimits(
        int maximumInstalledDesiredReplacements = DefaultMaximumInstalledDesiredReplacements,
        int terminalReservationTombstoneCapacity = DefaultTerminalReservationTombstoneCapacity)
    {
        if (maximumInstalledDesiredReplacements is <= 0 or > MaximumControlEntries)
            throw new ArgumentOutOfRangeException(nameof(maximumInstalledDesiredReplacements));
        if (terminalReservationTombstoneCapacity is <= 0 or > MaximumControlEntries)
            throw new ArgumentOutOfRangeException(nameof(terminalReservationTombstoneCapacity));
        MaximumInstalledDesiredReplacements = maximumInstalledDesiredReplacements;
        TerminalReservationTombstoneCapacity = terminalReservationTombstoneCapacity;
    }

    internal int MaximumInstalledDesiredReplacements { get; }
    internal int TerminalReservationTombstoneCapacity { get; }
}

internal readonly record struct PrepareDesiredReplacementRequest(
    InspectionId InspectionId,
    AcquisitionRunId RunId,
    GateRunGeneration RunGeneration,
    GatePlanId GatePlanId,
    GatePlanRevision GatePlanRevision,
    GatePreparedReservationOwner Owner);

internal enum PrepareDesiredReplacementStatus
{
    Unknown,
    Prepared,
    InvalidIdentity,
    Closed,
    AlreadyPrepared,
    CapacityExceeded,
    CounterOverflow
}

internal readonly record struct PrepareDesiredReplacementResult
{
    private PrepareDesiredReplacementResult(PrepareDesiredReplacementStatus status,
        PreparedGateReplacement prepared)
    {
        Status = status;
        Prepared = prepared;
    }

    internal PrepareDesiredReplacementStatus Status { get; }
    internal PreparedGateReplacement Prepared { get; }
    internal bool IsPrepared => Status == PrepareDesiredReplacementStatus.Prepared;

    internal static PrepareDesiredReplacementResult Success(PreparedGateReplacement prepared) =>
        new(PrepareDesiredReplacementStatus.Prepared, prepared);

    internal static PrepareDesiredReplacementResult Rejected(PrepareDesiredReplacementStatus status) =>
        status is PrepareDesiredReplacementStatus.Unknown or PrepareDesiredReplacementStatus.Prepared
            ? throw new ArgumentOutOfRangeException(nameof(status))
            : new(status, default);
}

internal readonly record struct PreparedGateReplacement(
    GatePreparedReservation Reservation,
    DesiredReplacementKey Key,
    GateReplacementToken TentativeToken,
    GateReplacementGeneration TentativeGeneration)
{
    internal bool IsValid => Reservation.IsValid && Key.IsValid && TentativeToken.IsValid && TentativeGeneration.IsValid;
}

/// <summary>
/// Minimal bounded replay state. It deliberately retains no run, plan, view, predecessor, or store reference.
/// </summary>
internal readonly record struct DesiredReplacementReservationTombstone(
    GatePreparedReservationId ReservationId,
    GatePreparedReservationOwner Owner,
    GatePreparedOperationGeneration Generation,
    GatePreparedReservationState State)
{
    internal GatePreparedReservation Snapshot() => new(ReservationId, Owner, Generation, State);
}

internal readonly record struct DesiredReplacementControlDiagnostics(
    bool HasPreparedReservation,
    int InstalledDesiredReplacementCount,
    int ProvisionalControlSlots,
    int TerminalReservationTombstones,
    int MaximumInstalledDesiredReplacements,
    int TerminalReservationTombstoneCapacity,
    ulong CommittedToken,
    ulong PreparedOperationGeneration);

internal sealed class PreparedDesiredReplacementOwner
{
    private readonly InMemoryGateDerivedStore _store;

    internal PreparedDesiredReplacementOwner(InMemoryGateDerivedStore store) => _store = store;

    internal PrepareDesiredReplacementResult Prepare(PrepareDesiredReplacementRequest request) =>
        _store.PrepareDesiredReplacement(request);

    internal GatePreparedReservationResult Commit(GatePreparedReservation reservation,
        GatePreparedReservationOwner owner, GatePreparedOperationGeneration generation) =>
        _store.CommitDesiredReplacement(reservation, owner, generation);

    internal GatePreparedReservationResult Abort(GatePreparedReservation reservation,
        GatePreparedReservationOwner owner, GatePreparedOperationGeneration generation) =>
        _store.AbortDesiredReplacement(reservation, owner, generation);

    internal InMemoryGateDerivedStore.InstalledDesiredReplacement? FindInstalled(DesiredReplacementKey key) =>
        _store.FindInstalledDesiredReplacement(key);

    internal DesiredReplacementControlDiagnostics Diagnostics => _store.DesiredReplacementDiagnostics;
}

internal sealed class PreparedDesiredReplacementState
{
    internal PreparedDesiredReplacementState(GatePreparedReservation reservation,
        InMemoryGateDerivedStore.InstalledDesiredReplacement installation,
        DesiredReplacementControlSlot? provisionalSlot,
        ActiveDerivedViewDescriptor?[] predecessorCell)
    {
        Reservation = reservation;
        Installation = installation;
        ProvisionalSlot = provisionalSlot;
        PredecessorCell = predecessorCell;
    }

    internal GatePreparedReservation Reservation { get; }
    internal InMemoryGateDerivedStore.InstalledDesiredReplacement Installation { get; }
    internal DesiredReplacementControlSlot? ProvisionalSlot { get; }
    internal ActiveDerivedViewDescriptor?[] PredecessorCell { get; }
}
