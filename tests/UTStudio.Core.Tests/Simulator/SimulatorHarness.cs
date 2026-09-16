using UTStudio.Acquisition.Simulator;
using UTStudio.Contracts.Acquisition;
using UTStudio.Domain.Acquisition;
using UTStudio.Core.Tests.TestDoubles;
using System.Runtime.CompilerServices;

namespace UTStudio.Core.Tests.Simulator;

internal sealed class SimulatorHarness : IAsyncDisposable
{
    private readonly List<ConventionalUtFrame> _borrowed = [];
    internal SimulatorHarness(SimulatorOptions? options = null)
    {
        Source = new SimulatorUtFrameSource(new UtSourceId("synthetic-test"), options, Clock);
    }

    internal ManualSimulatorTimeProvider Clock { get; } = new();
    internal SimulatorUtFrameSource Source { get; }
    internal UtAcquisitionRun Run { get; private set; } = null!;

    internal async Task StartAsync(CancellationToken token = default)
    {
        await DiagnosticWait.For(Source.ConnectAsync());
        Run = await DiagnosticWait.For(Source.StartAsync(new AcquisitionRunId(Guid.NewGuid()), token));
    }

    internal async Task<ConventionalUtFrame> ReadAsync()
    {
        using var watchdog = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            var frame = await Run.Frames.ReadAsync(watchdog.Token);
            _borrowed.Add(frame);
            return frame;
        }
        catch (OperationCanceledException error) when (watchdog.IsCancellationRequested)
        { throw new TimeoutException("Not reached within 10 seconds: simulator frame available", error); }
    }

    internal static Task UntilAsync(Func<bool> condition,
        [CallerArgumentExpression(nameof(condition))] string description = "simulator condition") => DiagnosticWait.Until(condition, description);

    public async ValueTask DisposeAsync()
    {
        try { await DiagnosticWait.For(Source.StopAsync()); }
        catch (InvalidOperationException) { } // Fault tests assert producer failures themselves.
        catch (AggregateException) { } // Fault tests also assert cancellation callback failures.
        finally
        {
            foreach (var frame in _borrowed) { frame.Dispose(); }
            if (Run is not null)
            {
                while (Run.Frames.TryRead(out var frame)) { frame.Dispose(); }
            }
        }

        try { await DiagnosticWait.For(Source.DisposeAsync().AsTask()); }
        catch (InvalidOperationException) { }
        catch (AggregateException) { }
    }
}
