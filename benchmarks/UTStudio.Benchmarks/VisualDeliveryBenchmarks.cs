using BenchmarkDotNet.Attributes;
using UTStudio.Visualization.Core;

namespace UTStudio.Benchmarks;

[MemoryDiagnoser]
public class VisualAcceptWithoutInterestBenchmarks
{
    [Params(2048, 65535)] public int SampleCount { get; set; }
    private VisualDeliveryFixture _fixture = null!;
    [IterationSetup] public void Setup() => _fixture = VisualDeliveryFixture.CreateWithoutInterest(SampleCount);
    [IterationCleanup] public void Cleanup() => _fixture.Dispose();
    [Benchmark(OperationsPerInvoke = 16, Description = "Accept without visual interest")]
    public ulong AcceptWithoutInterest() => _fixture.AcceptBatch(16);
}

[MemoryDiagnoser]
public class VisualProjectionBenchmarks
{
    [Params(2048, 65535)] public int SampleCount { get; set; }
    private VisualDeliveryFixture _fixture = null!;
    [IterationSetup] public void Setup() => _fixture = VisualDeliveryFixture.CreateTimerParked(SampleCount, keepPending: false);
    [IterationCleanup] public void Cleanup() => _fixture.Dispose();
    [Benchmark(Description = "Accept with projection into empty mailbox")]
    public ulong AcceptWithProjection() => _fixture.AcceptOne();
}

[MemoryDiagnoser]
public class VisualLatestOnlyReplacementBenchmarks
{
    [Params(2048, 65535)] public int SampleCount { get; set; }
    private VisualDeliveryFixture _fixture = null!;
    [IterationSetup] public void Setup() => _fixture = VisualDeliveryFixture.CreateTimerParked(SampleCount, keepPending: true);
    [IterationCleanup] public void Cleanup() => _fixture.Dispose();
    [Benchmark(OperationsPerInvoke = 16, Description = "Accept with projection and latest-only replacement")]
    public ulong ReplaceLatestBatch() => _fixture.AcceptBatch(16);
}

[MemoryDiagnoser]
public class VisualStatisticsBenchmarks
{
    [Params(2048, 65535)] public int SampleCount { get; set; }
    private VisualDeliveryFixture _fixture = null!;
    [IterationSetup] public void Setup() => _fixture = VisualDeliveryFixture.CreateWithoutInterest(SampleCount);
    [IterationCleanup] public void Cleanup() => _fixture.Dispose();
    [Benchmark(Description = "Read delivery statistics (lock and snapshot allocation)")]
    public long ReadStatistics() => _fixture.ReadStatistics();
}

internal sealed class VisualDeliveryFixture : IDisposable
{
    private static readonly TimeSpan DiagnosticTimeout = TimeSpan.FromSeconds(10);
    private readonly AScanVisualDelivery _delivery;
    private readonly FrozenTimeProvider _timeProvider;
    private readonly short[] _samples;
    private readonly UTStudio.Domain.Acquisition.ConventionalUtFrameMetadata _metadata;
    private IDisposable? _subscription;
    private ulong _sequence;

    private VisualDeliveryFixture(int sampleCount)
    {
        _metadata = BenchmarkData.Metadata(sampleCount);
        _samples = BenchmarkData.Samples(sampleCount);
        _timeProvider = new();
        _delivery = new(timeProvider: _timeProvider);
        _delivery.OpenRun(_metadata.RunId);
    }

    internal static VisualDeliveryFixture CreateWithoutInterest(int sampleCount)
    {
        var fixture = new VisualDeliveryFixture(sampleCount);
        try
        {
            fixture.Wait(VisualDiagnosticWorkerState.WaitingForSignal, hasPending: false);
            return fixture;
        }
        catch { try { fixture.Dispose(); } catch { } throw; }
    }

    internal static VisualDeliveryFixture CreateTimerParked(int sampleCount, bool keepPending)
    {
        var fixture = new VisualDeliveryFixture(sampleCount);
        try
        {
            fixture._subscription = fixture._delivery.Subscribe(new Observer());
            fixture.AcceptOne();
            fixture.Wait(VisualDiagnosticWorkerState.WaitingForSignal, hasPending: false);
            fixture.AcceptOne();
            fixture.Wait(VisualDiagnosticWorkerState.WaitingForTimer, hasPending: true);
            if (!keepPending)
            {
                fixture._delivery.CloseRun(fixture._metadata.RunId);
                fixture._delivery.OpenRun(fixture._metadata.RunId);
                fixture._sequence = 0;
                fixture.Wait(VisualDiagnosticWorkerState.WaitingForTimer, hasPending: false);
            }
            return fixture;
        }
        catch { try { fixture.Dispose(); } catch { } throw; }
    }

    internal ulong AcceptOne()
    {
        ulong sequence = _sequence++;
        _delivery.Accept(_metadata, sequence, TimeSpan.FromTicks((long)sequence), _samples);
        return _sequence;
    }

    internal ulong AcceptBatch(int count)
    {
        for (int index = 0; index < count; index++) { AcceptOne(); }
        return _sequence;
    }

    internal long ReadStatistics() => _delivery.Statistics.Received;
    private void Wait(VisualDiagnosticWorkerState state, bool hasPending) =>
        _delivery.WaitForDiagnosticStateAsync(new(state, hasPending), DiagnosticTimeout).GetAwaiter().GetResult();

    public void Dispose()
    {
        _delivery.DisposeForDiagnosticsAsync(DiagnosticTimeout).GetAwaiter().GetResult();
        _subscription?.Dispose();
        _subscription = null;
        _timeProvider.Dispose();
    }

    private sealed class Observer : IObserver<AScanSnapshot>
    {
        public void OnNext(AScanSnapshot value) { }
        public void OnError(Exception error) { }
        public void OnCompleted() { }
    }

    private sealed class FrozenTimeProvider : TimeProvider, IDisposable
    {
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => 0;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) => new FrozenTimer();
        public void Dispose() { }
        private sealed class FrozenTimer : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period) => true;
            public void Dispose() { }
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
