using UTStudio.Contracts.Inspection;
using UTStudio.Domain.Acquisition;

namespace UTStudio.LoadTests;

/// <summary>Observes sample-free committed notifications without retaining inspection data.</summary>
internal sealed class InstrumentedCommittedSink(LoadTelemetry telemetry,
    ICommittedTraceLifecycleSink inner) : ICommittedTraceLifecycleSink
{
    public void OpenRun(AcquisitionRunId runId) => inner.OpenRun(runId);
    public void NotifyCommitted(TraceIdentity identity, InspectionRevision revision)
    { telemetry.PrepareFrame(identity.AcquisitionIndex); inner.NotifyCommitted(identity, revision); }
    public void CloseRun(AcquisitionRunId runId) => inner.CloseRun(runId);
    public ValueTask WaitForCloseAsync(AcquisitionRunId runId) => inner.WaitForCloseAsync(runId);
    public void RequestFinalReconciliation(AcquisitionRunId runId, InspectionRevision finalRevision) =>
        inner.RequestFinalReconciliation(runId, finalRevision);
    public void StopAdmission(AcquisitionRunId runId) => inner.StopAdmission(runId);
    public ValueTask WaitForDrainAsync(AcquisitionRunId runId) => inner.WaitForDrainAsync(runId);
    public void FreezeRun(AcquisitionRunId runId) => inner.FreezeRun(runId);
}
