namespace UTStudio.Contracts.Processing;

public enum GatePreparedReservationState { Unknown, Prepared, Committed, Aborted }
public enum GatePreparedReservationOperation { Unknown, Commit, Abort }
public enum GatePreparedReservationResultStatus
{
    Unknown, InvalidReservation, Committed, AlreadyCommitted, Aborted, AlreadyAborted, OwnerOrGenerationMismatch
}

public readonly record struct GatePreparedReservationId
{
    public GatePreparedReservationId(Guid value)
    {
        if (value == Guid.Empty) throw new ArgumentException("A non-empty reservation identifier is required.", nameof(value));
        Value = value;
    }
    public Guid Value { get; }
    public bool IsValid => Value != Guid.Empty;
}

public readonly record struct GateAuthorityGeneration
{
    public GateAuthorityGeneration(ulong value)
    {
        if (value == 0) throw new ArgumentOutOfRangeException(nameof(value));
        Value = value;
    }
    public ulong Value { get; }
    public bool IsValid => Value > 0;
}

public readonly record struct GatePreparedOperationGeneration
{
    public GatePreparedOperationGeneration(ulong value)
    {
        if (value == 0) throw new ArgumentOutOfRangeException(nameof(value));
        Value = value;
    }
    public ulong Value { get; }
    public bool IsValid => Value > 0;
}

public readonly record struct GateReplacementGeneration
{
    public GateReplacementGeneration(ulong value)
    {
        if (value == 0) throw new ArgumentOutOfRangeException(nameof(value));
        Value = value;
    }
    public ulong Value { get; }
    public bool IsValid => Value > 0;
}

public readonly record struct GateRunGeneration
{
    public GateRunGeneration(ulong value)
    {
        if (value == 0) throw new ArgumentOutOfRangeException(nameof(value));
        Value = value;
    }
    public ulong Value { get; }
    public bool IsValid => Value > 0;
}

public readonly record struct GatePreparedReservationOwner
{
    public GatePreparedReservationOwner(Guid value)
    {
        if (value == Guid.Empty) throw new ArgumentException("A non-empty reservation owner is required.", nameof(value));
        Value = value;
    }
    public Guid Value { get; }
    public bool IsValid => Value != Guid.Empty;
}

public readonly record struct GatePreparedReservation
{
    public GatePreparedReservation(GatePreparedReservationId id, GatePreparedReservationOwner owner,
        GatePreparedOperationGeneration generation, GatePreparedReservationState state)
    {
        if (!id.IsValid || !owner.IsValid || !generation.IsValid) throw new ArgumentException("The reservation identity is invalid.");
        if (!Enum.IsDefined(state) || state == GatePreparedReservationState.Unknown) throw new ArgumentOutOfRangeException(nameof(state));
        Id = id; Owner = owner; Generation = generation; State = state;
    }
    public GatePreparedReservationId Id { get; }
    public GatePreparedReservationOwner Owner { get; }
    public GatePreparedOperationGeneration Generation { get; }
    public GatePreparedReservationState State { get; }
    public bool IsValid => Id.IsValid && Owner.IsValid && Generation.IsValid && State != GatePreparedReservationState.Unknown;
}

public readonly record struct GatePreparedReservationResult
{
    private GatePreparedReservationResult(GatePreparedReservationResultStatus status, GatePreparedReservation reservation)
    {
        if (!Enum.IsDefined(status) || status == GatePreparedReservationResultStatus.Unknown) throw new ArgumentOutOfRangeException(nameof(status));
        bool coherent = status switch
        {
            GatePreparedReservationResultStatus.InvalidReservation => !reservation.IsValid,
            GatePreparedReservationResultStatus.Committed or GatePreparedReservationResultStatus.AlreadyCommitted =>
                reservation.IsValid && reservation.State == GatePreparedReservationState.Committed,
            GatePreparedReservationResultStatus.Aborted or GatePreparedReservationResultStatus.AlreadyAborted =>
                reservation.IsValid && reservation.State == GatePreparedReservationState.Aborted,
            GatePreparedReservationResultStatus.OwnerOrGenerationMismatch =>
                reservation.IsValid,
            _ => false
        };
        if (!coherent) throw new ArgumentException("The result and reservation state are inconsistent.", nameof(reservation));
        Status = status; Reservation = reservation;
    }
    public GatePreparedReservationResultStatus Status { get; }
    public GatePreparedReservation Reservation { get; }

    public static GatePreparedReservationResult Classify(GatePreparedReservation reservation,
        GatePreparedReservationOwner owner, GatePreparedOperationGeneration generation,
        GatePreparedReservationOperation operation)
    {
        if (!reservation.IsValid)
            return new(GatePreparedReservationResultStatus.InvalidReservation, default);
        if (owner != reservation.Owner || generation != reservation.Generation)
            return new(GatePreparedReservationResultStatus.OwnerOrGenerationMismatch, reservation);
        if (!Enum.IsDefined(operation) || operation == GatePreparedReservationOperation.Unknown)
            throw new ArgumentOutOfRangeException(nameof(operation));
        GatePreparedReservationResultStatus status = (operation, reservation.State) switch
        {
            (GatePreparedReservationOperation.Commit, GatePreparedReservationState.Prepared) =>
                GatePreparedReservationResultStatus.Committed,
            (GatePreparedReservationOperation.Commit, GatePreparedReservationState.Committed) =>
                GatePreparedReservationResultStatus.AlreadyCommitted,
            (GatePreparedReservationOperation.Commit, GatePreparedReservationState.Aborted) =>
                GatePreparedReservationResultStatus.AlreadyAborted,
            (GatePreparedReservationOperation.Abort, GatePreparedReservationState.Prepared) =>
                GatePreparedReservationResultStatus.Aborted,
            (GatePreparedReservationOperation.Abort, GatePreparedReservationState.Committed) =>
                GatePreparedReservationResultStatus.AlreadyCommitted,
            (GatePreparedReservationOperation.Abort, GatePreparedReservationState.Aborted) =>
                GatePreparedReservationResultStatus.AlreadyAborted,
            _ => throw new ArgumentOutOfRangeException(nameof(reservation))
        };
        GatePreparedReservationState resultingState = status switch
        {
            GatePreparedReservationResultStatus.Committed => GatePreparedReservationState.Committed,
            GatePreparedReservationResultStatus.Aborted => GatePreparedReservationState.Aborted,
            _ => reservation.State
        };
        GatePreparedReservation resultReservation = resultingState == reservation.State ? reservation :
            new(reservation.Id, reservation.Owner, reservation.Generation, resultingState);
        return new(status, resultReservation);
    }
}
