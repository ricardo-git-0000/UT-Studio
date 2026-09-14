using UTStudio.Domain.Acquisition;

namespace UTStudio.Contracts.Application;

/// <summary>Borrowed session control and immutable latest state, without acquisition buffers.</summary>
/// <remarks>Disposal of the session belongs to composition, not to its observers.</remarks>
public interface IApplicationSession : IObservable<SessionSnapshot>
{
    SessionSnapshot Snapshot { get; }
    Task StartAsync(ConventionalAcquisitionConfiguration configuration, AcquisitionRunId runId,
        CancellationToken cancellationToken = default);
    Task StopAsync(CancellationToken cancellationToken = default);
}
