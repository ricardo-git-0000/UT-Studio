using UTStudio.Contracts.Acquisition;
using UTStudio.Domain.Acquisition;

namespace UTStudio.LoadTests;

// Observes the existing descriptor, never reads Frames or owns/disposes its buffers.
internal sealed class RunCaptureSource(IUtFrameSource inner) : IUtFrameSource
{
    internal UtAcquisitionRun? Run { get; private set; }
    public UtSourceId SourceId => inner.SourceId;
    public UtSourceCapabilities Capabilities => inner.Capabilities;
    public UtSourceState State => inner.State;
    public Task ConnectAsync(CancellationToken cancellationToken = default) => inner.ConnectAsync(cancellationToken);
    public Task ConfigureAsync(ConventionalAcquisitionConfiguration configuration, CancellationToken cancellationToken = default) => inner.ConfigureAsync(configuration, cancellationToken);
    public async Task<UtAcquisitionRun> StartAsync(AcquisitionRunId runId, CancellationToken cancellationToken = default)
    {
        Run = await inner.StartAsync(runId, cancellationToken).ConfigureAwait(false);
        return Run;
    }
    public Task StopAsync(CancellationToken cancellationToken = default) => inner.StopAsync(cancellationToken);
    public Task DisconnectAsync(CancellationToken cancellationToken = default) => inner.DisconnectAsync(cancellationToken);
    public ValueTask DisposeAsync() => inner.DisposeAsync();
}
