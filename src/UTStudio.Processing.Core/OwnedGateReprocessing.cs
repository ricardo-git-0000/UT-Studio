using UTStudio.Contracts.Processing;

namespace UTStudio.Processing.Core;

/// <summary>
/// Internal admission bridge for Application-owned historical work. The owner is allocated before admission;
/// Bind must be synchronous, non-allocating and non-fallable. Materialization runs without scheduler locks.
/// </summary>
internal interface IGateReprocessingAdmissionOwner
{
    void Bind(IGateReprocessingJob job);
    GateStoreOnlyCandidateDescriptor MaterializeCandidate(GateDerivedCoverageManifest manifest);
}

internal interface IOwnedGateReprocessingScheduler
{
    ValueTask<GateReprocessingAdmissionResult> AdmitOwnedAsync(GateReprocessingJobDescriptor descriptor,
        IGateReprocessingAdmissionOwner owner, CancellationToken cancellationToken = default);
}

/// <summary>Internal lifecycle used by the inspection-scoped historical owner.</summary>
internal interface IOwnedGateReprocessingLifecycle : IAsyncDisposable
{
    void CloseAdmission();
    Task StopAsync();
}

/// <summary>Segregated inspection lifecycle for the owner of live epochs, workers, callbacks and leases.</summary>
internal interface IGateLiveProcessingLifecycle : IAsyncDisposable
{
    GateProcessingDiagnostics Diagnostics { get; }
}
