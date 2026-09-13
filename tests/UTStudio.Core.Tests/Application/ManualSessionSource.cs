using System.Buffers;
using System.Collections.Concurrent;
using System.Threading.Channels;
using UTStudio.Contracts.Acquisition;
using UTStudio.Domain.Acquisition;

namespace UTStudio.Core.Tests.Application;

/// <summary>Explicit handshakes control progress; real time is used only by test watchdogs.</summary>
internal sealed class ManualSessionSource : IUtFrameSource
{
    private readonly object _gate = new();
    private ConventionalAcquisitionConfiguration? _configuration;
    private int _outstanding;
    private bool _sealed;
    internal static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal ConcurrentQueue<string> Calls { get; } = new();
    internal ManualStep Connect { get; } = new();
    internal ManualStep Configure { get; } = new();
    internal ManualStep Start { get; } = new();
    internal ManualStep Disconnect { get; } = new();
    internal ManualStep Rollback { get; } = new();
    internal TaskCompletionSource StopEntered { get; } = Signal();
    internal TaskCompletionSource Producer { get; } = Signal();
    internal TaskCompletionSource Released { get; } = Signal();
    internal Channel<ConventionalUtFrame> Channel { get; } = System.Threading.Channels.Channel.CreateBounded<ConventionalUtFrame>(
        new BoundedChannelOptions(4) { FullMode = BoundedChannelFullMode.Wait, SingleReader = true, AllowSynchronousContinuations = false });
    internal bool HoldProducer { get; set; }
    internal bool HoldReleaseBarrier { get; set; }
    internal Exception? StopError { get; set; }
    internal Action? AfterRunCreated { get; set; }
    internal UtAcquisitionRun? Run { get; private set; }
    internal int DisposeCount { get; private set; }
    internal CancellationTokenSource ProductionCancellation { get; } = new();

    public UtSourceId SourceId { get; } = new("manual-session");
    public UtSourceCapabilities Capabilities { get; } = new([new PhysicalChannelId(0)], 2048, 50_000_000, true);
    public UtSourceState State { get; private set; } = new(0, UtConnectionState.Disconnected, UtAcquisitionState.Idle);

    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        Calls.Enqueue("Connect");
        await Connect.EnterAsync(cancellationToken);
        State = new(State.Version + 1, UtConnectionState.Connected, UtAcquisitionState.Idle);
    }

    public async Task ConfigureAsync(ConventionalAcquisitionConfiguration configuration, CancellationToken cancellationToken = default)
    {
        Calls.Enqueue("Configure");
        await Configure.EnterAsync(cancellationToken);
        _configuration = configuration;
    }

    public async Task<UtAcquisitionRun> StartAsync(AcquisitionRunId runId, CancellationToken cancellationToken = default)
    {
        Calls.Enqueue("Start");
        try { await Start.EnterAsync(cancellationToken); }
        catch
        {
            Calls.Enqueue("Rollback");
            await Rollback.EnterAsync(CancellationToken.None);
            Calls.Enqueue("RollbackFinished");
            throw;
        }

        var metadata = new ConventionalUtFrameMetadata(SourceId, runId, _configuration!, DateTimeOffset.UnixEpoch);
        Run = new UtAcquisitionRun(metadata, Channel.Reader, Producer.Task, Released.Task);
        State = new(State.Version + 1, UtConnectionState.Connected, UtAcquisitionState.Running);
        AfterRunCreated?.Invoke();
        return Run;
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        Calls.Enqueue("Stop");
        ProductionCancellation.Cancel();
        StopEntered.TrySetResult();
        if (Run is not null)
        {
            if (!HoldProducer) { FinishProducer(); }
            await Producer.Task.WaitAsync(cancellationToken);
        }

        if (StopError is not null) { throw StopError; }
    }

    internal void FinishProducer(Exception? error = null)
    {
        lock (_gate)
        {
            _sealed = true;
            Channel.Writer.TryComplete(error);
            if (error is null) { Producer.TrySetResult(); }
            else { Producer.TrySetException(error); }
            TryRelease();
        }
    }

    internal CountingOwner QueueFrame(ulong sequence, ConventionalUtFrameMetadata? metadata = null,
        Action? beforeReturn = null, Exception? releaseError = null)
    {
        var (frame, owner) = CreateFrame(sequence, metadata, beforeReturn, releaseError);
        if (!Channel.Writer.TryWrite(frame)) { frame.Dispose(); throw new InvalidOperationException("Test channel is full."); }
        return owner;
    }

    internal (ConventionalUtFrame Frame, CountingOwner Owner) CreateFrame(ulong sequence,
        ConventionalUtFrameMetadata? metadata = null, Action? beforeReturn = null, Exception? releaseError = null)
    {
        lock (_gate)
        {
            if (_sealed) { throw new InvalidOperationException("Producer is sealed."); }
            _outstanding++;
        }

        metadata ??= Run!.Metadata;
        var owner = new CountingOwner(metadata.Configuration.SampleCount, () =>
        {
            beforeReturn?.Invoke();
            lock (_gate) { _outstanding--; TryRelease(); }
            if (releaseError is not null) { throw releaseError; }
        });
        return (new ConventionalUtFrame(metadata, sequence, TimeSpan.FromTicks((long)sequence), owner), owner);
    }

    private void TryRelease()
    {
        if (_sealed && _outstanding == 0 && !HoldReleaseBarrier) { Released.TrySetResult(); }
    }

    public async Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        Calls.Enqueue("Disconnect");
        await Disconnect.EnterAsync(cancellationToken);
        State = new(State.Version + 1, UtConnectionState.Disconnected, UtAcquisitionState.Idle);
    }

    public ValueTask DisposeAsync()
    {
        DisposeCount++;
        ProductionCancellation.Dispose();
        return ValueTask.CompletedTask;
    }

    internal sealed class ManualStep
    {
        internal bool Held { get; set; }
        internal Exception? Error { get; set; }
        internal TaskCompletionSource Entered { get; } = Signal();
        internal TaskCompletionSource Continue { get; } = Signal();
        internal TaskCompletionSource Cancelled { get; } = Signal();

        internal async Task EnterAsync(CancellationToken token)
        {
            Entered.TrySetResult();
            try
            {
                token.ThrowIfCancellationRequested();
                if (Held) { await Continue.Task.WaitAsync(token); }
            }
            catch (OperationCanceledException) { Cancelled.TrySetResult(); throw; }
            if (Error is not null) { throw Error; }
        }
    }

    internal sealed class CountingOwner(int count, Action onReturn) : IMemoryOwner<short>
    {
        private readonly short[] _samples = new short[count];
        private int _disposeCount;
        internal int DisposeCount => Volatile.Read(ref _disposeCount);
        internal TaskCompletionSource Returned { get; } = Signal();
        public Memory<short> Memory => _samples;
        public void Dispose()
        {
            Interlocked.Increment(ref _disposeCount);
            try { onReturn(); }
            finally { Returned.TrySetResult(); }
        }
    }
}
