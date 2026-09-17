using System.Buffers;
using UTStudio.Acquisition.Simulator;
using System.Diagnostics;
using System.Threading.Channels;
using UTStudio.Contracts.Acquisition;
using UTStudio.Domain.Acquisition;

namespace UTStudio.LoadTests;

internal enum ExperimentalWaitReason { None, Buffer, Channel, Pacing }

/// <summary>Load-test-only source. It is not registered by or referenced from production.</summary>
internal sealed class ExperimentalFrameSource : IUtFrameSource
{
    private readonly object _gate = new();
    private readonly LoadOptions _options;
    private readonly LoadTelemetry _telemetry;
    private readonly IPacingClock _clock;
    private ConventionalAcquisitionConfiguration? _configuration;
    private RunContext? _run;
    private UtSourceState _state = new(0, UtConnectionState.Disconnected, UtAcquisitionState.Idle);
    private Task? _dispose;
    private AcquisitionRunId _lastRunId;

    internal ExperimentalFrameSource(LoadOptions options, LoadTelemetry telemetry, IPacingClock? clock = null)
    {
        _options = options;
        _telemetry = telemetry;
        _clock = clock ?? StopwatchPacingClock.Instance;
        SourceId = new UtSourceId("load-test-experimental");
        Capabilities = new UtSourceCapabilities([new PhysicalChannelId(0)],
            ConventionalAcquisitionConfiguration.MaximumSampleCount,
            ConventionalAcquisitionConfiguration.MaximumSampleRateHz, true);
    }

    public UtSourceId SourceId { get; }
    public UtSourceCapabilities Capabilities { get; }
    public UtSourceState State { get { lock (_gate) { return _state; } } }
    internal int OutstandingBuffers { get { lock (_gate) { return _run?.Pool.Outstanding ?? 0; } } }
    internal int MaximumOutstandingBuffers { get { lock (_gate) { return _run?.Pool.MaximumOutstanding ?? 0; } } }
    internal long Produced => _telemetry.Produced;
    internal ExperimentalWaitReason WaitReason { get { lock (_gate) { return _run?.WaitReason ?? ExperimentalWaitReason.None; } } }

    public Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            EnsureAvailable(); cancellationToken.ThrowIfCancellationRequested();
            if (_state.Connection == UtConnectionState.Connected) { return Task.CompletedTask; }
            if (_state.Connection != UtConnectionState.Disconnected) { throw new InvalidOperationException("Invalid connection state."); }
            _configuration = new(new PhysicalChannelId(0), _options.SampleCount, 50_000_000);
            SetState(UtConnectionState.Connected, UtAcquisitionState.Idle);
            return Task.CompletedTask;
        }
    }

    public Task ConfigureAsync(ConventionalAcquisitionConfiguration configuration, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        lock (_gate)
        {
            EnsureAvailable(); cancellationToken.ThrowIfCancellationRequested();
            if (_state.Connection != UtConnectionState.Connected || _state.Acquisition != UtAcquisitionState.Idle || _run is not null)
            { throw new InvalidOperationException("Source must be connected and idle."); }
            if (configuration.PhysicalChannelId.Value != 0 || configuration.SignalMode != UtSignalMode.Rf)
            { throw new ArgumentException("The experimental source supports RF channel zero.", nameof(configuration)); }
            _configuration = configuration;
            return Task.CompletedTask;
        }
    }

    public Task<UtAcquisitionRun> StartAsync(AcquisitionRunId runId, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            EnsureAvailable(); cancellationToken.ThrowIfCancellationRequested();
            if (!runId.IsValid || runId == _lastRunId) { throw new ArgumentException("A new valid run is required.", nameof(runId)); }
            if (_state.Connection != UtConnectionState.Connected || _state.Acquisition != UtAcquisitionState.Idle || _run is not null)
            { throw new InvalidOperationException("Source must be connected and idle."); }
            var configuration = _configuration!;
            var pool = new SamplePool(8, configuration.SampleCount);
            var metadata = new ConventionalUtFrameMetadata(SourceId, runId, configuration, DateTimeOffset.UtcNow);
            var context = new RunContext(metadata, pool);
            try { cancellationToken.ThrowIfCancellationRequested(); }
            catch { pool.Seal(); context.Cancellation.Dispose(); throw; }
            _run = context;
            _lastRunId = runId;
            SetState(UtConnectionState.Connected, UtAcquisitionState.Running);
            context.Producer = Task.Run(() => ProduceAsync(context));
            return Task.FromResult(new UtAcquisitionRun(metadata, _telemetry.Counters.Reader(context.Channel.Reader),
                context.ProducerCompletion.Task, pool.AllFramesReleased));
        }
    }

    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        Task completion;
        lock (_gate)
        {
            if (_run is null) { return Task.CompletedTask; }
            if (_state.Acquisition != UtAcquisitionState.Faulted) { SetState(_state.Connection, UtAcquisitionState.Stopping); }
            _run.Stop ??= StopCoreAsync(_run);
            completion = _run.Stop;
        }
        return completion.WaitAsync(cancellationToken);
    }

    private static async Task StopCoreAsync(RunContext context)
    {
        await context.Cancellation.CancelAsync().ConfigureAwait(false);
        await context.ProducerCompletion.Task.ConfigureAwait(false);
    }

    public Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            EnsureAvailable(); cancellationToken.ThrowIfCancellationRequested();
            if (_run is not null && (!(_run.ProducerCompletion.Task.IsCompleted && _run.Pool.AllFramesReleased.IsCompletedSuccessfully)))
            { throw new InvalidOperationException("Frames remain outstanding."); }
            RetireRun();
            _configuration = null;
            SetState(UtConnectionState.Disconnected, UtAcquisitionState.Idle);
            return Task.CompletedTask;
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (_gate) { return new(_dispose ??= DisposeCoreAsync()); }
    }

    private async Task DisposeCoreAsync()
    {
        RunContext? run;
        lock (_gate) { run = _run; }
        Exception? failure = null;
        if (run is not null)
        {
            try { await StopAsync().ConfigureAwait(false); }
            catch (Exception error) { failure = error; }
            try { await run.Pool.AllFramesReleased.ConfigureAwait(false); }
            catch (Exception error) { failure ??= error; }
        }
        lock (_gate)
        {
            RetireRun(); _configuration = null;
            SetState(UtConnectionState.Disconnected, UtAcquisitionState.Idle);
        }
        if (failure is not null) { System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw(); }
    }

    private async Task ProduceAsync(RunContext context)
    {
        Exception? failure = null;
        IMemoryOwner<short>? owner = null;
        ConventionalUtFrame? frame = null;
        AcquisitionMetrics.TrackedOwner? tracked = null;
        ulong sequence = 0;
        long origin = _clock.Timestamp;

        var token = context.Cancellation.Token;
        try
        {
            while (true)
            {
                TimeSpan delay;
                while ((delay = _telemetry.Demand.UntilNext(_clock.Timestamp)) > TimeSpan.Zero)
                {
                    context.WaitReason = ExperimentalWaitReason.Pacing;
                    await _clock.DelayAsync(delay, token).ConfigureAwait(false);
                }
                token.ThrowIfCancellationRequested();
                int batch = _telemetry.Demand.Claim(_clock.Timestamp);
                for (int index = 0; index < batch; index++)
                {
                    token.ThrowIfCancellationRequested();
                    _telemetry.Demand.PrepareOffer(index);
                    _telemetry.Counters.Offer();
                    var rent = context.Pool.RentAsync(token);
                    context.WaitReason = rent.IsCompleted ? ExperimentalWaitReason.None : ExperimentalWaitReason.Buffer;
                    owner = await rent.ConfigureAwait(false);
                    owner = tracked = _telemetry.Counters.Track(owner);
                    context.WaitReason = ExperimentalWaitReason.None;
                    Fill(owner.Memory.Span, sequence, token);
                    frame = new ConventionalUtFrame(context.Metadata, sequence,
                        Stopwatch.GetElapsedTime(origin), owner);
                    tracked.Generated();
                    owner = null;
                    var write = _telemetry.Counters.WriteAsync(context.Channel.Writer, frame, token);
                    context.WaitReason = write.IsCompleted ? ExperimentalWaitReason.None : ExperimentalWaitReason.Channel;
                    await write.ConfigureAwait(false);
                    frame = null;
                    tracked = null;
                    context.WaitReason = ExperimentalWaitReason.None;
                    sequence = checked(sequence + 1);
                }

            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception error) { failure = error; }
        finally
        {
            tracked?.NotTransferred();
            try { frame?.Dispose(); } catch (Exception error) { failure ??= error; }
            try { owner?.Dispose(); } catch (Exception error) { failure ??= error; }
            try { context.Pool.Seal(); } catch (Exception error) { failure ??= error; }
            context.WaitReason = ExperimentalWaitReason.None;
            context.Channel.Writer.TryComplete(failure);
            if (failure is null) { context.ProducerCompletion.TrySetResult(); }
            else
            {
                lock (_gate) { SetState(_state.Connection, UtAcquisitionState.Faulted, Describe("load.producer", failure)); }
                context.ProducerCompletion.TrySetException(failure);
            }
        }
    }

    private static void Fill(Span<short> samples, ulong sequence, CancellationToken token)
    {
        uint state = unchecked((uint)sequence + 0x9E3779B9u);
        for (int i = 0; i < samples.Length; i++)
        {
            if ((i & 1023) == 0) { token.ThrowIfCancellationRequested(); }
            state = unchecked(state * 1664525u + 1013904223u);
            samples[i] = unchecked((short)(state >> 16));
        }
    }

    private void RetireRun() { _run?.Cancellation.Dispose(); _run = null; }
    private void EnsureAvailable() => ObjectDisposedException.ThrowIf(_dispose is not null, this);
    private void SetState(UtConnectionState connection, UtAcquisitionState acquisition, UtSourceError? error = null) =>
        _state = new(checked(_state.Version + 1), connection, acquisition, error);
    private static UtSourceError Describe(string code, Exception error) => new(code, error.Message);

    private sealed class RunContext
    {
        internal RunContext(ConventionalUtFrameMetadata metadata, SamplePool pool)
        {
            Metadata = metadata; Pool = pool;
            Channel = System.Threading.Channels.Channel.CreateBounded<ConventionalUtFrame>(new BoundedChannelOptions(4)
            { SingleReader = true, SingleWriter = true, FullMode = BoundedChannelFullMode.Wait, AllowSynchronousContinuations = false });
        }
        internal ConventionalUtFrameMetadata Metadata { get; }
        internal SamplePool Pool { get; }
        internal Channel<ConventionalUtFrame> Channel { get; }
        internal CancellationTokenSource Cancellation { get; } = new();
        internal TaskCompletionSource ProducerCompletion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal Task Producer { get; set; } = Task.CompletedTask;
        internal Task? Stop;
        internal volatile ExperimentalWaitReason WaitReason;
    }

    private sealed class SamplePool
    {
        private readonly object _gate = new();
        private readonly Channel<short[]> _free;
        private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private bool _sealed;
        private int _outstanding;
        private int _maximumOutstanding;
        internal SamplePool(int count, int samples)
        {
            _free = Channel.CreateBounded<short[]>(new BoundedChannelOptions(count) { FullMode = BoundedChannelFullMode.Wait });
            for (int i = 0; i < count; i++) { _free.Writer.TryWrite(new short[samples]); }
        }
        internal Task AllFramesReleased => _released.Task;
        internal int Outstanding { get { lock (_gate) { return _outstanding; } } }
        internal int MaximumOutstanding { get { lock (_gate) { return _maximumOutstanding; } } }
        internal async ValueTask<IMemoryOwner<short>> RentAsync(CancellationToken token)
        {
            while (await _free.Reader.WaitToReadAsync(token).ConfigureAwait(false))
            {
                lock (_gate)
                {
                    token.ThrowIfCancellationRequested();
                    if (_sealed) { throw new InvalidOperationException("Pool sealed."); }
                    if (_free.Reader.TryRead(out var buffer))
                    {
                        _outstanding++;
                        _maximumOutstanding = Math.Max(_maximumOutstanding, _outstanding);
                        return new Lease(this, buffer);
                    }
                }
            }
            throw new InvalidOperationException("Pool sealed.");
        }
        internal void Seal()
        {
            lock (_gate)
            {
                _sealed = true; _free.Writer.TryComplete();
                while (_free.Reader.TryRead(out _)) { }
                Complete();
            }
        }
        private void Return(short[] buffer)
        {
            lock (_gate)
            {
                if (!_sealed && !_free.Writer.TryWrite(buffer)) { throw new InvalidOperationException("Pool accounting failed."); }
                _outstanding--; Complete();
            }
        }
        private void Complete() { if (_sealed && _outstanding == 0) { _released.TrySetResult(); } }
        private sealed class Lease(SamplePool pool, short[] buffer) : IMemoryOwner<short>
        {
            private SamplePool? _pool = pool;
            public Memory<short> Memory => Volatile.Read(ref _pool) is null ? throw new ObjectDisposedException(nameof(Lease)) : buffer;
            public void Dispose() { Interlocked.Exchange(ref _pool, null)?.Return(buffer); }
        }
    }
}
