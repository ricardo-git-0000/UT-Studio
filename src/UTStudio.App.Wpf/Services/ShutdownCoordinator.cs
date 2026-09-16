using Microsoft.Extensions.Logging;

namespace UTStudio.App.Wpf.Services;

internal sealed record ShutdownStep(string Name, Func<Task> Run, Func<bool>? ConfirmedAfterError = null);
public sealed record ShutdownStatus(string Phase, bool DiagnosticTimeout = false, bool Completed = false, string? Error = null);

/// <summary>One observed cleanup sequence; the five-second clock only changes diagnostics.</summary>
public sealed class ShutdownCoordinator
{
    private readonly object _gate = new();
    private readonly IReadOnlyList<ShutdownStep> _steps;
    private readonly ILogger _logger;
    private readonly TimeProvider _clock;
    private ShutdownStatus _status = new("Preparado");
    private Task? _shutdown;

    internal ShutdownCoordinator(IReadOnlyList<ShutdownStep> steps, ILogger logger, TimeProvider clock)
    {
        _steps = steps;
        _logger = logger;
        _clock = clock;
    }

    public ShutdownStatus Status { get { lock (_gate) { return _status; } } }
    public Task ShutdownAsync()
    {
        lock (_gate) { return _shutdown ??= Task.Run(RunAsync); }
    }

    private async Task RunAsync()
    {
        using var diagnosticCancellation = new CancellationTokenSource();
        Task diagnostic = DiagnoseAsync(diagnosticCancellation.Token);
        try
        {
            foreach (var step in _steps)
            {
                lock (_gate) { _status = _status with { Phase = step.Name }; }
                try { await step.Run().ConfigureAwait(false); }
                catch (Exception error)
                {
                    _logger.LogError(error, "Shutdown failed in {Phase}", step.Name);
                    lock (_gate) { _status = _status with { Error = error.Message }; }
                    if (step.ConfirmedAfterError?.Invoke() != true) { throw; }
                }
            }
            lock (_gate) { _status = _status with { Phase = "Cierre completado", Completed = true }; }
        }
        finally
        {
            // Diagnostic teardown must never replace the resource-cleanup result.
            try { await diagnosticCancellation.CancelAsync().ConfigureAwait(false); }
            catch (Exception error) { _logger.LogWarning(error, "Shutdown diagnostic timer cancellation failed"); }
            try { await diagnostic.ConfigureAwait(false); }
            catch (Exception error) { _logger.LogWarning(error, "Shutdown diagnostic observation failed"); }
        }
    }

    private async Task DiagnoseAsync(CancellationToken token)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(5), _clock, token).ConfigureAwait(false);
            lock (_gate) { _status = _status with { DiagnosticTimeout = true }; }
            _logger.LogWarning("Shutdown exceeded five seconds in {Phase}; cleanup continues without forced release", Status.Phase);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception error)
        {
            _logger.LogError(error, "Shutdown diagnostic clock failed; resource cleanup continues");
        }
    }
}
