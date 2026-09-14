using Microsoft.Extensions.Hosting;

namespace UTStudio.App.Wpf.Composition;

// WPF Closing owns shutdown. No console signals or background windowless lifetime.
internal sealed class DesktopHostLifetime : IHostLifetime
{
    public Task WaitForStartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
