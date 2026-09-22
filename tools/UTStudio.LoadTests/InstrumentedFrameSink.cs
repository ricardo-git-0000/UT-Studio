using UTStudio.Contracts.Presentation;
using UTStudio.Domain.Acquisition;

namespace UTStudio.LoadTests;

/// <summary>Marks entry into the visual branch; the borrowed span is forwarded synchronously and never retained.</summary>
internal sealed class InstrumentedFrameSink(IConventionalFrameSink inner, LoadTelemetry telemetry) : IConventionalFrameSink
{
    public void OpenRun(ConventionalUtFrameMetadata metadata) => inner.OpenRun(metadata);
    public void Accept(ConventionalUtFrameMetadata metadata, ulong sequence, ulong acquisitionIndex,
        TimeSpan elapsedSinceRunStart, ScanSpatialCoordinate? spatialCoordinate, ReadOnlySpan<short> samples)
    {
        telemetry.PrepareFrame(sequence);
        inner.Accept(metadata, sequence, acquisitionIndex, elapsedSinceRunStart, spatialCoordinate, samples);
    }
    public void CloseRun(AcquisitionRunId runId) => inner.CloseRun(runId);
}
