using UTStudio.Contracts.Presentation;
using UTStudio.Domain.Acquisition;

namespace UTStudio.LoadTests;

/// <summary>Marks entry into the visual branch; the borrowed span is forwarded synchronously and never retained.</summary>
internal sealed class InstrumentedFrameSink(IConventionalFrameSink inner, LoadTelemetry telemetry) : IConventionalFrameSink
{
    public void OpenRun(AcquisitionRunId runId) => inner.OpenRun(runId);
    public void Accept(ConventionalUtFrameMetadata metadata, ulong sequence, TimeSpan elapsedSinceRunStart,
        ReadOnlySpan<short> samples)
    {
        telemetry.PrepareFrame(sequence);
        inner.Accept(metadata, sequence, elapsedSinceRunStart, samples);
    }
    public void CloseRun(AcquisitionRunId runId) => inner.CloseRun(runId);
}
