using UTStudio.Acquisition.Simulator;
using UTStudio.Contracts.Acquisition;
using UTStudio.Domain.Acquisition;

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
        await Source.ConnectAsync();
        Run = await Source.StartAsync(new AcquisitionRunId(Guid.NewGuid()), token);
    }

    internal async Task<ConventionalUtFrame> ReadAsync()
    {
        var frame = await Run.Frames.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        _borrowed.Add(frame);
        return frame;
    }

    internal static async Task UntilAsync(Func<bool> condition)
    {
        using var watchdog = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!condition())
        {
            watchdog.Token.ThrowIfCancellationRequested();
            await Task.Yield();
        }
    }

    public async ValueTask DisposeAsync()
    {
        try { await Source.StopAsync().WaitAsync(TimeSpan.FromSeconds(10)); }
        catch (InvalidOperationException) { } // Fault tests assert producer failures themselves.
        catch (AggregateException) { } // Fault tests also assert cancellation callback failures.
        foreach (var frame in _borrowed) { frame.Dispose(); }
        if (Run is not null)
        {
            while (Run.Frames.TryRead(out var frame)) { frame.Dispose(); }
        }

        try { await Source.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10)); }
        catch (InvalidOperationException) { }
        catch (AggregateException) { }
    }
}
