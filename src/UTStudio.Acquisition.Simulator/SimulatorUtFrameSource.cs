using System.Buffers;
using System.Threading.Channels;
using UTStudio.Contracts.Acquisition;
using UTStudio.Domain.Acquisition;

namespace UTStudio.Acquisition.Simulator;

/// <summary>A deterministic RF source with a fixed buffer pool and lossless, bounded backpressure.</summary>
/// <remarks>
/// Connect accepts DefaultConfiguration. Configure may replace it while idle.
/// Stop does not drain the transferred reader. Dispose waits for its consumer to drain and return all frames;
/// the consumer must keep doing so concurrently. No lifecycle wait holds the state lock.
/// </remarks>
public sealed class SimulatorUtFrameSource : IUtFrameSource
{
    private readonly object _gate = new();
    private readonly SimulatorOptions _options;
    private readonly TimeProvider _time;
    private ConventionalAcquisitionConfiguration? _configuration;
    private RunContext? _run;
    private AcquisitionRunId _lastRunId;
    private UtSourceState _state = new(0, UtConnectionState.Disconnected, UtAcquisitionState.Idle);
    private Task? _disposeTask;
    private long _producedFrames;

    public SimulatorUtFrameSource(UtSourceId sourceId, SimulatorOptions? options = null, TimeProvider? timeProvider = null)
    {
        if (!sourceId.IsValid) { throw new ArgumentException("A valid source identifier is required.", nameof(sourceId)); }
        SourceId = sourceId;
        _options = options ?? new SimulatorOptions();
        _time = timeProvider ?? TimeProvider.System;
        Capabilities = new UtSourceCapabilities([new PhysicalChannelId(0)],
            ConventionalAcquisitionConfiguration.MaximumSampleCount,
            ConventionalAcquisitionConfiguration.MaximumSampleRateHz, true);
    }

    public static ConventionalAcquisitionConfiguration DefaultConfiguration { get; } = new(default, 2048, 50_000_000);
    public UtSourceId SourceId { get; }
    public UtSourceCapabilities Capabilities { get; }
    internal SimulatorWaitReason WaitReason { get { lock (_gate) { return _run?.WaitReason ?? SimulatorWaitReason.None; } } }
    internal int OutstandingBuffers { get { lock (_gate) { return _run?.Pool.Outstanding ?? 0; } } }
    internal int MaximumOutstandingBuffers { get { lock (_gate) { return _run?.Pool.MaximumOutstanding ?? 0; } } }
    internal AcquisitionMetrics? Metrics { get; init; }
    internal long ProducedFrames => Interlocked.Read(ref _producedFrames);
    public UtSourceState State
    {
        get { lock (_gate) { Refresh(); return _state; } }
    }

    public Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            EnsureAvailable();
            cancellationToken.ThrowIfCancellationRequested();
            Refresh();
            if (_state.Connection == UtConnectionState.Connected) { return Task.CompletedTask; }
            if (_state.Connection != UtConnectionState.Disconnected) { throw new InvalidOperationException("Disconnect before reconnecting."); }
            SetState(UtConnectionState.Connecting, UtAcquisitionState.Idle);
            _configuration = DefaultConfiguration;
            SetState(UtConnectionState.Connected, UtAcquisitionState.Idle);
            return Task.CompletedTask;
        }
    }

    public Task ConfigureAsync(ConventionalAcquisitionConfiguration configuration, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            EnsureAvailable();
            cancellationToken.ThrowIfCancellationRequested();
            RequireIdle();
            SyntheticRfGenerator.Validate(configuration);
            _configuration = configuration;
            return Task.CompletedTask;
        }
    }

    public Task<UtAcquisitionRun> StartAsync(AcquisitionRunId runId, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            EnsureAvailable();
            cancellationToken.ThrowIfCancellationRequested();
            RequireIdle();
            if (!runId.IsValid || runId == _lastRunId) { throw new ArgumentException("Supply a new valid execution identifier.", nameof(runId)); }
            var configuration = _configuration!;
            SetState(UtConnectionState.Connected, UtAcquisitionState.Starting);
            BoundedSampleBufferPool? pool = null;
            RunContext? context = null;
            try
            {
                pool = new BoundedSampleBufferPool(_options.BufferCount, configuration.SampleCount);
                var metadata = new ConventionalUtFrameMetadata(SourceId, runId, configuration, _time.GetUtcNow());
                context = new RunContext(metadata, pool, _options.ChannelCapacity, _time.GetTimestamp(), Metrics);
                var result = new UtAcquisitionRun(metadata, context.Metrics?.Reader(context.Channel.Reader) ?? context.Channel.Reader, context.Producer.Task, pool.AllFramesReleased);
                cancellationToken.ThrowIfCancellationRequested();
                // Commit: all fallible preparation precedes enabling the producer. No request token is linked.
                _run = context;
                Interlocked.Exchange(ref _producedFrames, 0);
                _lastRunId = runId;
                SetState(UtConnectionState.Connected, UtAcquisitionState.Running);
                _ = Task.Run(() => ProduceAsync(context));
                return Task.FromResult(result);
            }
            catch (Exception error)
            {
                // No producer was enabled before commit, so there are no transferred or queued frames.
                pool?.Seal();
                context?.Cancellation.Dispose();
                _run = null;
                if (error is OperationCanceledException && cancellationToken.IsCancellationRequested)
                {
                    SetState(UtConnectionState.Connected, UtAcquisitionState.Idle);
                }
                else
                {
                    SetState(UtConnectionState.Connected, UtAcquisitionState.Faulted,
                        DescribeError("simulator.start", error));
                }

                throw;
            }
        }
    }

    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        Task completion;
        lock (_gate)
        {
            if (_disposeTask is not null) { completion = _run?.StopCompletion ?? Task.CompletedTask; }
            else
            {
                Refresh();
                RequestStop();
                completion = _run?.StopCompletion ?? Task.CompletedTask;
            }
        }

        return completion.WaitAsync(cancellationToken);
    }

    public Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            EnsureAvailable();
            cancellationToken.ThrowIfCancellationRequested();
            Refresh();
            if (_run is not null && !IsReleased(_run)) { throw new InvalidOperationException("Stop, drain and return all frames before disconnecting."); }
            RetireRun();
            if (_state.Connection == UtConnectionState.Disconnected) { return Task.CompletedTask; }
            SetState(UtConnectionState.Disconnecting, UtAcquisitionState.Idle);
            _configuration = null;
            SetState(UtConnectionState.Disconnected, UtAcquisitionState.Idle);
            return Task.CompletedTask;
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (_disposeTask is null)
            {
                RequestStop();
                _disposeTask = DisposeCoreAsync(_run);
            }

            return new ValueTask(_disposeTask);
        }
    }

    private async Task DisposeCoreAsync(RunContext? context)
    {
        Exception? failure = null;
        if (context is not null)
        {
            try { await context.StopCompletion!.ConfigureAwait(false); }
            catch (Exception error) { failure = error; }
            await context.Pool.AllFramesReleased.ConfigureAwait(false);
        }

        lock (_gate)
        {
            RetireRun();
            _configuration = null;
            SetState(UtConnectionState.Disconnected,
                failure is null ? UtAcquisitionState.Idle : UtAcquisitionState.Faulted,
                _state.PrimaryError ?? (failure is null ? null : DescribeError("simulator.cleanup", failure)),
                _state.CleanupErrors);
        }

        if (failure is not null) { System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw(); }
    }

    private async Task ProduceAsync(RunContext context)
    {
        Exception? failure = null;
        IMemoryOwner<short>? owner = null;
        ConventionalUtFrame? frame = null;
        AcquisitionMetrics.TrackedOwner? tracked = null;
        var token = context.Cancellation.Token;
        try
        {
            ulong sequence = 0;
            while (true)
            {
                context.Metrics?.Offer();
                var reservation = context.Pool.RentAsync(token);
                context.WaitReason = reservation.IsCompleted ? SimulatorWaitReason.None : SimulatorWaitReason.Buffer;
                owner = await reservation.ConfigureAwait(false);
                if (context.Metrics is { } metrics) { owner = tracked = metrics.Track(owner); }
                context.WaitReason = SimulatorWaitReason.None;
                token.ThrowIfCancellationRequested();
                var elapsed = _time.GetElapsedTime(context.OriginTimestamp);
                SyntheticRfGenerator.Fill(owner.Memory.Span, context.Metadata.Configuration, _options, sequence, token);
                frame = new ConventionalUtFrame(context.Metadata, sequence, elapsed, owner);
                tracked?.Generated();
                owner = null;
                var write = context.Metrics is { } instrumentation ? instrumentation.WriteAsync(context.Channel.Writer, frame, token) : context.Channel.Writer.WriteAsync(frame, token);
                context.WaitReason = write.IsCompleted ? SimulatorWaitReason.None : SimulatorWaitReason.Channel;
                await write.ConfigureAwait(false);
                tracked = null;
                frame = null; // Successful write, even if cancellation raced: reader now owns it.
                Interlocked.Increment(ref _producedFrames);
                context.WaitReason = SimulatorWaitReason.None;
                sequence = checked(sequence + 1);
                long deliveredAt = _time.GetTimestamp();
                TimeSpan remaining = _options.Period;
                do
                {
                    // Rearm against monotonic time if a provider wakes early. Never catch up missed periods.
                    var delay = TimeSpan.FromMilliseconds(Math.Ceiling(remaining.TotalMilliseconds));
                    await Task.Delay(delay, _time, token).ConfigureAwait(false);
                    remaining = _options.Period - _time.GetElapsedTime(deliveredAt);
                } while (remaining > TimeSpan.Zero);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception error) { failure = error; }
        finally
        {
            List<UtSourceError>? cleanupErrors = null;
            void Cleanup(Action action)
            {
                try { action(); }
                catch (Exception error)
                {
                    if (failure is null) { failure = error; }
                    else { (cleanupErrors ??= []).Add(DescribeError("simulator.cleanup", error)); }
                }
            }

            tracked?.NotTransferred();
            Cleanup(() => frame?.Dispose());
            Cleanup(() => owner?.Dispose());
            Cleanup(context.Pool.Seal);
            context.WaitReason = SimulatorWaitReason.None;
            try
            {
                lock (_gate)
                {
                    if (failure is not null)
                    {
                        SetState(_state.Connection, UtAcquisitionState.Faulted,
                            DescribeError("simulator.producer", failure), cleanupErrors);
                    }
                    else if (_state.Acquisition != UtAcquisitionState.Faulted)
                    {
                        SetState(_state.Connection, UtAcquisitionState.Stopping);
                    }
                }
            }
            catch (Exception error) { failure ??= error; }
            finally
            {
                context.Channel.Writer.TryComplete(failure);
                if (failure is null) { context.Producer.TrySetResult(); }
                else
                {
                    context.Producer.TrySetException(failure);
                    _ = context.Producer.Task.Exception; // Observe even when the caller only watches State.
                }
            }
        }
    }

    private void RequestStop()
    {
        if (_run is null) { return; }
        if (_state.Acquisition != UtAcquisitionState.Faulted)
        {
            SetState(_state.Connection, UtAcquisitionState.Stopping);
        }

        // CancelAsync marks cancellation immediately but executes callbacks asynchronously, outside _gate.
        // Keep the CTS alive until those callbacks as well as the producer have finished.
        _run.StopCompletion ??= CompleteStopAsync(_run, _run.Cancellation.CancelAsync());
    }

    private async Task CompleteStopAsync(RunContext context, Task cancellation)
    {
        try { await Task.WhenAll(context.Producer.Task, cancellation).ConfigureAwait(false); }
        catch
        {
            if (cancellation.Exception is { } error)
            {
                lock (_gate)
                {
                    var diagnostic = DescribeError("simulator.cancellation", error);
                    SetState(_state.Connection, UtAcquisitionState.Faulted, _state.PrimaryError ?? diagnostic,
                        _state.PrimaryError is null ? _state.CleanupErrors : [.. _state.CleanupErrors, diagnostic]);
                }
            }

            throw;
        }
    }

    private void RequireIdle()
    {
        Refresh();
        if (_state.Connection != UtConnectionState.Connected || _state.Acquisition != UtAcquisitionState.Idle)
        {
            throw new InvalidOperationException("A connected, fully cleaned idle source is required.");
        }

        RetireRun();
    }

    private static bool IsReleased(RunContext context) =>
        context.Producer.Task.IsCompleted && context.StopCompletion?.IsCompleted != false &&
        context.Pool.AllFramesReleased.IsCompletedSuccessfully;

    private void Refresh()
    {
        if (_run is not null && IsReleased(_run) && _state.Acquisition == UtAcquisitionState.Stopping)
        {
            SetState(_state.Connection, UtAcquisitionState.Idle);
        }
    }

    private void RetireRun()
    {
        _run?.Cancellation.Dispose();
        _run = null;
    }

    private void EnsureAvailable() => ObjectDisposedException.ThrowIf(_disposeTask is not null, this);

    private static UtSourceError DescribeError(string code, Exception error) =>
        new(code, string.IsNullOrWhiteSpace(error.Message) ? error.GetType().Name : error.Message);

    private void SetState(UtConnectionState connection, UtAcquisitionState acquisition, UtSourceError? error = null,
        IEnumerable<UtSourceError>? cleanupErrors = null) =>
        _state = new UtSourceState(checked(_state.Version + 1), connection, acquisition, error, cleanupErrors);

    private sealed class RunContext
    {
        internal RunContext(ConventionalUtFrameMetadata metadata, BoundedSampleBufferPool pool, int capacity, long originTimestamp, AcquisitionMetrics? metrics)
        {
            Metadata = metadata;
            Pool = pool;
            OriginTimestamp = originTimestamp;
            Metrics = metrics;
            Channel = System.Threading.Channels.Channel.CreateBounded<ConventionalUtFrame>(new BoundedChannelOptions(capacity)
            {
                SingleReader = true,
                SingleWriter = true,
                FullMode = BoundedChannelFullMode.Wait,
                AllowSynchronousContinuations = false
            });
        }

        internal ConventionalUtFrameMetadata Metadata { get; }
        internal BoundedSampleBufferPool Pool { get; }
        internal long OriginTimestamp { get; }
        internal AcquisitionMetrics? Metrics { get; }
        internal Channel<ConventionalUtFrame> Channel { get; }
        internal CancellationTokenSource Cancellation { get; } = new();
        internal TaskCompletionSource Producer { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal volatile SimulatorWaitReason WaitReason;
        internal Task? StopCompletion;
    }
}
