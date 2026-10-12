using BenchmarkDotNet.Attributes;
using UTStudio.Contracts.Inspection;
using UTStudio.Domain.Inspection;
using UTStudio.Visualization.Core;

namespace UTStudio.Benchmarks;

[MemoryDiagnoser]
public class VisualNotificationWithoutInterestBenchmarks
{
    [Params(2048, 65535)] public int SampleCount { get; set; }
    private VisualDeliveryFixture _fixture = null!;
    [IterationSetup] public void Setup() => _fixture = VisualDeliveryFixture.CreateWithoutInterest(SampleCount);
    [IterationCleanup] public void Cleanup() => _fixture.Dispose();
    [Benchmark(OperationsPerInvoke = 16, Description = "Confirmed trace notification without visual interest")]
    public ulong NotifyWithoutInterest() => _fixture.NotifyBatch(16);
}

[MemoryDiagnoser]
public class VisualProjectionBenchmarks
{
    [Params(2048, 65535)] public int SampleCount { get; set; }
    private VisualDeliveryFixture _fixture = null!;
    [IterationSetup] public void Setup() => _fixture = VisualDeliveryFixture.CreateTimerParked(SampleCount, keepPending: false);
    [IterationCleanup] public void Cleanup() => _fixture.Dispose();
    [Benchmark(Description = "Confirmed trace notification with projection into empty mailbox")]
    public ulong NotifyWithProjection() => _fixture.NotifyOne();
}

[MemoryDiagnoser]
public class VisualLatestOnlyReplacementBenchmarks
{
    [Params(2048, 65535)] public int SampleCount { get; set; }
    private VisualDeliveryFixture _fixture = null!;
    [IterationSetup] public void Setup() => _fixture = VisualDeliveryFixture.CreateTimerParked(SampleCount, keepPending: true);
    [IterationCleanup] public void Cleanup() => _fixture.Dispose();
    [Benchmark(OperationsPerInvoke = 16, Description = "Confirmed trace notification with latest-only replacement")]
    public ulong ReplaceLatestBatch() => _fixture.NotifyBatch(16);
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
    private static readonly InspectionId InspectionId =
        new(new Guid("22222222-2222-2222-2222-222222222222"));
    private readonly AScanVisualDelivery _delivery;
    private readonly BenchmarkTraceProvider _traceProvider;
    private readonly BenchmarkProjector _projector;
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
        _traceProvider = new();
        _projector = new();
        _delivery = new(_traceProvider, _projector, _timeProvider);
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
            fixture.NotifyOne();
            fixture._projector.WaitForCalls(1, DiagnosticTimeout);
            fixture.Wait(VisualDiagnosticWorkerState.WaitingForSignal, hasPending: false);
            fixture.NotifyOne();
            fixture._projector.WaitForCalls(2, DiagnosticTimeout);
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

    internal ulong NotifyOne()
    {
        ulong sequence = _sequence++;
        var stream = new UtStreamIdentity(InspectionId, _metadata.RunId,
            _metadata.SourceId, _metadata.Configuration.PhysicalChannelId);
        var identity = new TraceIdentity(stream, sequence);
        var elapsed = TimeSpan.FromTicks((long)sequence);
        var descriptor = new TraceDescriptor(identity, sequence, elapsed,
            _metadata.RunStartedAtUtc + elapsed, null, _samples.Length,
            _metadata.Configuration.SampleRateHz, _metadata.Configuration.FirstSampleOffsetSeconds,
            _metadata.Configuration.SignalMode);
        InspectionRevision revision = _traceProvider.Confirm(descriptor, _samples);
        _delivery.NotifyCommitted(identity, revision);
        return _sequence;
    }

    internal ulong NotifyBatch(int count)
    {
        for (int index = 0; index < count; index++) { NotifyOne(); }
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
        if (_traceProvider.AcquiredLeases != _traceProvider.ReleasedLeases)
        {
            throw new InvalidOperationException(
                $"Benchmark lease imbalance: acquired {_traceProvider.AcquiredLeases}, released {_traceProvider.ReleasedLeases}.");
        }
        _timeProvider.Dispose();
    }

    private sealed class Observer : IObserver<AScanSnapshot>
    {
        public void OnNext(AScanSnapshot value) { }
        public void OnError(Exception error) { }
        public void OnCompleted() { }
    }

    private sealed class BenchmarkProjector : IAScanProjector
    {
        private readonly object _gate = new();
        private readonly List<(int Count, TaskCompletionSource Completion)> _waiters = [];
        private int _calls;

        internal void WaitForCalls(int count, TimeSpan timeout)
        {
            Task wait;
            lock (_gate)
            {
                if (_calls >= count) return;
                var completion = new TaskCompletionSource(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                _waiters.Add((count, completion));
                wait = completion.Task;
            }
            wait.WaitAsync(timeout).GetAwaiter().GetResult();
        }

        public AScanSnapshot Project(
            UTStudio.Domain.Acquisition.ConventionalUtFrameMetadata metadata,
            ulong sequence, TimeSpan elapsedSinceRunStart, ReadOnlySpan<short> samples,
            ulong version)
        {
            AScanSnapshot snapshot = new AScanProjector().Project(
                metadata, sequence, elapsedSinceRunStart, samples, version);
            lock (_gate)
            {
                _calls++;
                foreach (var waiter in _waiters.Where(waiter => _calls >= waiter.Count))
                {
                    waiter.Completion.TrySetResult();
                }
                _waiters.RemoveAll(waiter => _calls >= waiter.Count);
            }
            return snapshot;
        }
    }

    private sealed class BenchmarkTraceProvider : IUtTraceProvider
    {
        private readonly object _gate = new();
        private readonly Dictionary<TraceIdentity,
            (TraceDescriptor Descriptor, short[] Samples, InspectionRevision Revision)> _traces = [];
        private ulong _revision;
        internal int AcquiredLeases;
        internal int ReleasedLeases;

        internal InspectionRevision Confirm(TraceDescriptor descriptor, ReadOnlySpan<short> samples)
        {
            lock (_gate)
            {
                var revision = new InspectionRevision(++_revision);
                _traces[descriptor.Identity] = (descriptor, samples.ToArray(), revision);
                return revision;
            }
        }

        public ValueTask<InspectionTraceReadResult> ReadAsync(TraceIdentity identity,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_gate)
            {
                return _traces.TryGetValue(identity, out var trace)
                    ? ValueTask.FromResult(InspectionTraceReadResult.Available(
                        trace.Revision, new BenchmarkLease(trace.Descriptor, trace.Samples, this)))
                    : ValueTask.FromResult(InspectionTraceReadResult.Unavailable(
                        InspectionReadStatus.NotAvailable, new InspectionRevision(_revision)));
            }
        }
    }

    private sealed class BenchmarkLease : IInspectionTraceLease
    {
        private readonly BenchmarkTraceProvider _provider;
        private int _disposed;
        internal BenchmarkLease(
            TraceDescriptor descriptor, short[] samples, BenchmarkTraceProvider provider)
        {
            Descriptor = descriptor;
            Samples = new BenchmarkSamples(samples);
            _provider = provider;
            Interlocked.Increment(ref provider.AcquiredLeases);
        }
        public TraceDescriptor Descriptor { get; }
        public IReadOnlyUtSamples Samples { get; }
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                Interlocked.Increment(ref _provider.ReleasedLeases);
            }
        }

        private sealed class BenchmarkSamples(short[] values) : IReadOnlyUtSamples
        {
            public int Count => values.Length;
            public short this[int index] => values[index];
            public void CopyTo(Span<short> destination) => values.CopyTo(destination);
        }
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
