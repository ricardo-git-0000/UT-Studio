namespace UTStudio.Acquisition.Simulator;

// Internal diagnostics for deterministic checks of backpressure, not a source contract.
internal enum SimulatorWaitReason
{
    None,
    Channel,
    Buffer
}
