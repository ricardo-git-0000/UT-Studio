using UTStudio.Domain.Acquisition;

namespace UTStudio.Contracts.Acquisition;

/// <summary>A neutral conventional RF source with serialized lifecycle operations.</summary>
/// <remarks>
/// Invalid arguments use argument exceptions; invalid lifecycle operations use InvalidOperationException.
/// Requested cancellation uses OperationCanceledException, not a faulted source state.
/// Operational failures are observable through returned tasks and State; preserve primary and cleanup errors.
/// Never publish Idle, configure, or start again while previous execution resources remain outstanding.
/// Fault recovery requires cleanup, disconnection, and a new connection/configuration.
/// DisposeAsync performs final cleanup without forcibly reclaiming transferred memory or replacing reader drainage.
/// </remarks>
public interface IUtFrameSource : IAsyncDisposable
{
    UtSourceId SourceId { get; }
    UtSourceCapabilities Capabilities { get; }

    /// <summary>An atomic immutable snapshot; versions increase on state changes.</summary>
    UtSourceState State { get; }

    /// <summary>Connects logically. Long-running connection must observe cancellation.</summary>
    Task ConnectAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Validates the whole configuration while connected and idle, without outstanding run resources.
    /// Independent capability maxima do not guarantee joint feasibility. Rejection preserves previous settings.
    /// </summary>
    Task ConfigureAsync(
        ConventionalAcquisitionConfiguration configuration,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Starts with a valid, new run ID; repeated Start is rejected.
    /// Returns without waiting for the first frame or for frame consumption.
    /// Before successful return the source owns rollback: cancel producer, complete writer, drain and release.
    /// After return Application owns the reader even if the request is immediately cancelled.
    /// The request token must not remain linked to an established run's private lifetime token.
    /// </summary>
    Task<UtAcquisitionRun> StartAsync(
        AcquisitionRunId runId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Idempotently requests producer cancellation, including clock, buffer and blocked channel-write waits.
    /// Waits only for producer exit and writer completion, not reader drainage or AllFramesReleased.
    /// Application must keep the sole reader draining concurrently. Cancelling this wait does not abort cleanup.
    /// </summary>
    Task StopAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Idempotently disconnects after Application has drained and observed AllFramesReleased.
    /// Does not reclaim transferred memory or replace reader drainage.
    /// </summary>
    Task DisconnectAsync(CancellationToken cancellationToken = default);
}
