using System.Diagnostics;
using UTStudio.Domain.Acquisition;
using UTStudio.Visualization.Core;

namespace UTStudio.LoadTests;

internal sealed class InstrumentedProjector(IAScanProjector inner, LoadTelemetry telemetry) : IAScanProjector
{
    public AScanSnapshot Project(ConventionalUtFrameMetadata metadata, ulong sequence,
        TimeSpan elapsedSinceRunStart, ReadOnlySpan<short> samples, ulong version)
    {
        long started = Stopwatch.GetTimestamp();
        try { return inner.Project(metadata, sequence, elapsedSinceRunStart, samples, version); }
        finally { telemetry.Projection(started, Stopwatch.GetTimestamp()); }
    }
}
