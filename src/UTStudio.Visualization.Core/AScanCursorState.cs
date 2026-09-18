using UTStudio.Domain.Acquisition;

namespace UTStudio.Visualization.Core;

public enum AScanCursorId
{
    A,
    B
}

/// <summary>
/// Neutral cursor measurement. Amplitude comes from the nearest point retained by the visual snapshot;
/// it is not guaranteed to be the original acquisition sample at <see cref="TimeSeconds"/>.
/// </summary>
public sealed record AScanCursorMeasurement(double TimeSeconds, double AmplitudePercent,
    double RepresentedPointTimeSeconds, int RepresentedPointIndex)
{
    public double TimeMicroseconds => TimeSeconds * 1e6;
    public double RepresentedPointTimeMicroseconds => RepresentedPointTimeSeconds * 1e6;
}

/// <summary>Immutable manual A-Scan cursor state owned by Presentation.</summary>
public sealed record AScanCursorState(AcquisitionRunId RunId, ulong SnapshotVersion, bool IsVisible, AScanCursorId ActiveCursor,
    AScanCursorMeasurement A, AScanCursorMeasurement B)
{
    public double DeltaTimeSeconds => B.TimeSeconds - A.TimeSeconds;
    public double DeltaTimeMicroseconds => DeltaTimeSeconds * 1e6;
    public double DeltaAmplitudePercent => B.AmplitudePercent - A.AmplitudePercent;
}

/// <summary>Pure cursor calculations over an immutable, already reduced visual snapshot.</summary>
public static class AScanCursorMeasurements
{
    public static AScanCursorState Create(AScanSnapshot snapshot, bool isVisible = true)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        double range = snapshot.MaximumTimeSeconds - snapshot.MinimumTimeSeconds;
        return new(snapshot.Metadata.RunId, snapshot.Version, isVisible, AScanCursorId.A,
            Measure(snapshot, snapshot.MinimumTimeSeconds + range * .25),
            Measure(snapshot, snapshot.MinimumTimeSeconds + range * .75));
    }

    public static AScanCursorState Move(AScanCursorState state, AScanSnapshot snapshot,
        AScanCursorId cursor, double timeSeconds)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(snapshot);
        var measurement = Measure(snapshot, timeSeconds);
        return cursor == AScanCursorId.A
            ? state with { RunId = snapshot.Metadata.RunId, SnapshotVersion = snapshot.Version, ActiveCursor = cursor, A = measurement }
            : state with { RunId = snapshot.Metadata.RunId, SnapshotVersion = snapshot.Version, ActiveCursor = cursor, B = measurement };
    }

    public static AScanCursorState Reconcile(AScanCursorState state, AScanSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(snapshot);
        return state with
        {
            RunId = snapshot.Metadata.RunId,
            SnapshotVersion = snapshot.Version,
            A = Measure(snapshot, state.A.TimeSeconds),
            B = Measure(snapshot, state.B.TimeSeconds)
        };
    }

    public static AScanCursorState Reset(AScanCursorState state, AScanSnapshot snapshot) =>
        Create(snapshot, state?.IsVisible ?? throw new ArgumentNullException(nameof(state))) with
        { ActiveCursor = state.ActiveCursor };

    private static AScanCursorMeasurement Measure(AScanSnapshot snapshot, double requestedTimeSeconds)
    {
        if (!double.IsFinite(requestedTimeSeconds))
        { throw new ArgumentOutOfRangeException(nameof(requestedTimeSeconds)); }
        double time = Math.Clamp(requestedTimeSeconds, snapshot.MinimumTimeSeconds, snapshot.MaximumTimeSeconds);
        if (snapshot.Points.Count == 0) { throw new ArgumentException("Snapshot has no visual points.", nameof(snapshot)); }
        int nearest = 0;
        double distance = Math.Abs(snapshot.Points[0].TimeSeconds - time);
        for (int index = 1; index < snapshot.Points.Count; index++)
        {
            double candidate = Math.Abs(snapshot.Points[index].TimeSeconds - time);
            if (candidate < distance) { nearest = index; distance = candidate; }
        }
        var point = snapshot.Points[nearest];
        return new(time, point.AmplitudePercent, point.TimeSeconds, nearest);
    }
}
