using Microsoft.Extensions.Logging;
using UTStudio.Presentation;

namespace UTStudio.App.Wpf.Services;

internal sealed record ShutdownStep(string Name, Func<Task> Run, Func<bool>? ConfirmedAfterError = null,
    bool RequiresConfirmedCleanup = false);
public sealed record ShutdownFailure(string Phase, string Message, Exception Error);
public sealed record ShutdownStatus(string Phase, bool DiagnosticTimeout = false, bool Completed = false,
    ShutdownFailure? PrimaryFailure = null, IReadOnlyList<ShutdownFailure>? SecondaryFailures = null)
{
    public string? Error => PrimaryFailure?.Message;
    public IReadOnlyList<ShutdownFailure> CleanupFailures => SecondaryFailures ?? [];
}

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
        lock (_gate) { return _shutdown ??= RunAsync(); }
    }

    private async Task RunAsync()
    {
        using var diagnosticCancellation = new CancellationTokenSource();
        Task diagnostic = DiagnoseAsync(diagnosticCancellation.Token);
        try
        {
            List<ShutdownFailure> failures = [];
            bool cleanupConfirmed = true;
            foreach (var step in _steps)
            {
                if (step.RequiresConfirmedCleanup && !cleanupConfirmed) { continue; }
                lock (_gate) { _status = _status with { Phase = step.Name }; }
                try { await step.Run().ConfigureAwait(false); }
                catch (Exception error)
                {
                    _logger.LogError(error, "Shutdown failed in {Phase}", step.Name);
                    failures.AddRange(Flatten(step.Name, error));
                    bool confirmed = false;
                    try { confirmed = step.ConfirmedAfterError?.Invoke() == true; }
                    catch (Exception confirmationError)
                    {
                        var secondary = new ShutdownFailure(step.Name + " (confirmación)", Message(confirmationError), confirmationError);
                        failures.Add(secondary);
                        _logger.LogError(confirmationError, "Shutdown confirmation failed in {Phase}", step.Name);
                    }
                    if (!confirmed) { cleanupConfirmed = false; }
                    lock (_gate)
                    {
                        _status = _status with
                        {
                            PrimaryFailure = failures[0],
                            SecondaryFailures = failures.Skip(1).ToArray()
                        };
                    }
                }
            }
            if (!cleanupConfirmed)
            {
                lock (_gate) { _status = _status with { Phase = "Limpieza obligatoria no confirmada", Completed = false }; }
                throw failures[0].Error;
            }
            lock (_gate)
            {
                _status = _status with
                {
                    Phase = "Cierre completado",
                    Completed = true,
                    PrimaryFailure = failures.Count == 0 ? null : failures[0],
                    SecondaryFailures = failures.Skip(1).ToArray()
                };
            }
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

    private static string Message(Exception error) => string.IsNullOrWhiteSpace(error.Message)
        ? error.GetType().Name : error.Message;

    private static IEnumerable<ShutdownFailure> Flatten(string phase, Exception error)
    {
        if (error is CleanupFailureException cleanup)
        {
            foreach (ShutdownFailure item in FlattenStage(phase, cleanup.PrimaryFailure)) { yield return item; }
            foreach (CleanupStageFailure secondary in cleanup.SecondaryFailures)
            { foreach (ShutdownFailure item in FlattenStage(phase, secondary)) { yield return item; } }
            yield break;
        }
        if (error is AggregateException aggregate)
        {
            foreach (Exception inner in aggregate.InnerExceptions)
            { foreach (ShutdownFailure item in Flatten(phase, inner)) { yield return item; } }
            yield break;
        }
        yield return new(phase, Message(error), error);
    }

    private static IEnumerable<ShutdownFailure> FlattenStage(string phase, CleanupStageFailure failure) =>
        Flatten($"{phase}/{failure.Stage}", failure.Error);

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
