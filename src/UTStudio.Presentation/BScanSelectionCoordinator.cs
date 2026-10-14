using System.ComponentModel;
using UTStudio.Domain.Acquisition;
using UTStudio.Domain.Inspection;
using UTStudio.Visualization.Core;
using UTStudio.Contracts.Inspection;

namespace UTStudio.Presentation;

public enum AScanDisplayMode { NoData, Live, Selected }
public enum TraceSelectionResidency { Resident, Evicted }

public sealed record TraceSelectionState(
    AcquisitionRunId? RunId,
    AScanSnapshot? Snapshot,
    TraceIdentity? Identity,
    ulong? AcquisitionIndex,
    TraceSelectionResidency? Residency,
    BScanTraceResultKind? LastResult,
    string? SelectionError,
    bool IsPending,
    string? RequestOwnerId)
{
    public static TraceSelectionState Empty { get; } = new(null, null, null, null, null, null, null, false, null);
}

/// <summary>Global neutral latest-selection-wins resolver for one related A/B scan group.</summary>
public sealed class TraceSelectionScope : SafeObservableObject, IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly IUiDispatcher _dispatcher;
    private readonly IAuthoritativeAScanSelector? _selector;
    private CancellationTokenSource? _activeCancellation;
    private TraceSelectionState _state = TraceSelectionState.Empty;
    private AcquisitionRunId? _activeRun;
    private readonly HashSet<AcquisitionRunId> _retiredRuns = [];
    private readonly Queue<AcquisitionRunId> _retiredRunOrder = [];
    private readonly Dictionary<string, (AcquisitionRunId RunId, ulong Version)> _residencyVersions = [];
    private const int RetiredRunCapacity = 64;
    private long _generation;
    private int _closing;
    private Task _activeResolution = Task.CompletedTask;
    private readonly HashSet<Task> _resolutions = [];
    private string? _activeOwnerId;
    private string? _selectedOwnerId;
    private Task? _disposal;
    private long _observerErrors;

    public TraceSelectionScope(IUiDispatcher dispatcher, IAuthoritativeAScanSelector? selector = null)
    {
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _selector = selector;
    }

    internal TraceSelectionScope(IUiDispatcher dispatcher, IAuthoritativeAScanSelector selector,
        IAScanProjector unusedProjector) : this(dispatcher, selector)
        => ArgumentNullException.ThrowIfNull(unusedProjector);

    public TraceSelectionState State => _state;
    public bool IsClosed => Volatile.Read(ref _closing) != 0;
    public long ObserverErrorCount => Interlocked.Read(ref _observerErrors);

    public Task SetRunAsync(AcquisitionRunId? runId)
    {
        CancellationTokenSource? cancellation = null;
        long generation;
        lock (_gate)
        {
            if (IsClosed || _activeRun == runId || (runId is { } candidate && _retiredRuns.Contains(candidate)))
            { return Task.CompletedTask; }
            if (_activeRun is { } previous)
            {
                _retiredRuns.Add(previous);
                _retiredRunOrder.Enqueue(previous);
                if (_retiredRunOrder.Count > RetiredRunCapacity)
                { _retiredRuns.Remove(_retiredRunOrder.Dequeue()); }
            }
            _activeRun = runId;
            _residencyVersions.Clear();
            generation = ++_generation;
            cancellation = _activeCancellation;
            _activeCancellation = null;
        }
        cancellation?.Cancel();
        return _dispatcher.InvokeAsync(() =>
        {
            if (Volatile.Read(ref _generation) != generation || _activeRun != runId || IsClosed) { return; }
            _selectedOwnerId = null;
            ReplaceState(TraceSelectionState.Empty with { RunId = runId });
        });
    }

    public Task SelectAsync(string ownerId, TraceIdentity identity)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerId);
        if (!identity.IsValid) { throw new ArgumentException("A valid trace identity is required.", nameof(identity)); }
        CancellationTokenSource cancellation;
        CancellationTokenSource? previousCancellation;
        long generation;
        TaskCompletionSource completion;
        Task previous;
        lock (_gate)
        {
            if (IsClosed || _activeRun != identity.Stream.AcquisitionRunId) { return Task.CompletedTask; }
            previousCancellation = _activeCancellation;
            cancellation = new();
            _activeCancellation = cancellation;
            _activeOwnerId = ownerId;
            generation = ++_generation;
            completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            previous = _activeResolution;
            _activeResolution = completion.Task;
            _resolutions.Add(completion.Task);
        }
        try { previousCancellation?.Cancel(); }
        catch { Interlocked.Increment(ref _observerErrors); }
        _ = CompleteResolutionAsync(previous, ownerId, identity, generation, cancellation, completion);
        return completion.Task;
    }

    public void Cancel(string ownerId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerId);
        CancellationTokenSource? cancellation = null;
        lock (_gate)
        {
            if (_activeOwnerId != ownerId || IsClosed) { return; }
            _generation++;
            cancellation = _activeCancellation;
            _activeCancellation = null;
            _activeOwnerId = null;
        }
        cancellation?.Cancel();
        PublishCancelled(_generation);
    }

    public void CancelPending()
    {
        CancellationTokenSource? cancellation;
        lock (_gate)
        {
            if (IsClosed || _activeCancellation is null) { return; }
            _generation++;
            cancellation = _activeCancellation;
            _activeCancellation = null;
            _activeOwnerId = null;
        }
        cancellation.Cancel();
        PublishCancelled(_generation);
    }

    public void ReturnToLive()
    {
        CancellationTokenSource? cancellation;
        long generation;
        lock (_gate)
        {
            if (IsClosed) { return; }
            generation = ++_generation;
            cancellation = _activeCancellation;
            _activeCancellation = null;
            _activeOwnerId = null;
            _selectedOwnerId = null;
        }
        cancellation?.Cancel();
        _ = _dispatcher.InvokeAsync(() =>
        {
            if (IsClosed || Volatile.Read(ref _generation) != generation) { return; }
            ReplaceState(TraceSelectionState.Empty with { RunId = _activeRun });
        });
    }

    private void PublishCancelled(long generation) => _ = _dispatcher.InvokeAsync(() =>
    {
        if (IsClosed || Volatile.Read(ref _generation) != generation) { return; }
        ReplaceState(_state with
        {
            IsPending = false,
            LastResult = BScanTraceResultKind.Cancelled,
            SelectionError = null,
            RequestOwnerId = null
        });
    });

    private async Task CompleteResolutionAsync(Task previous, string ownerId, TraceIdentity identity, long generation,
        CancellationTokenSource cancellation, TaskCompletionSource completion)
    {
        Exception? failure = null;
        try
        {
            try { await previous.ConfigureAwait(false); } catch { }
            if (IsCurrent(generation, identity.Stream.AcquisitionRunId))
            { await ResolveAsync(ownerId, identity, generation, cancellation).ConfigureAwait(false); }
        }
        catch (Exception error) { failure = error; }
        finally
        {
            cancellation.Dispose();
            lock (_gate) { _resolutions.Remove(completion.Task); }
            if (failure is null) { completion.TrySetResult(); }
            else { completion.TrySetException(failure); }
        }
    }

    public void UpdateResidency(string ownerId, UtStreamIdentity stream, ulong snapshotVersion,
        BScanAcquisitionRange residentRange)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerId);
        _ = _dispatcher.InvokeAsync(() =>
        {
            if (IsClosed || _state.RunId != stream.AcquisitionRunId || _state.Identity?.Stream != stream ||
                _state.AcquisitionIndex is not { } index) { return; }
            if (_residencyVersions.TryGetValue(ownerId, out var previous) && previous.RunId == stream.AcquisitionRunId &&
                snapshotVersion <= previous.Version) { return; }
            _residencyVersions[ownerId] = (stream.AcquisitionRunId, snapshotVersion);
            TraceSelectionResidency next = index >= residentRange.First && index <= residentRange.Last
                ? TraceSelectionResidency.Resident : TraceSelectionResidency.Evicted;
            if (next == _state.Residency) { return; }
            ReplaceState(_state with { Residency = next });
        });
    }

    private async Task ResolveAsync(string ownerId, TraceIdentity identity, long generation,
        CancellationTokenSource cancellation)
    {
        await _dispatcher.InvokeAsync(() =>
        {
            if (IsCurrent(generation, identity.Stream.AcquisitionRunId))
            { ReplaceState(_state with { IsPending = true, LastResult = null, SelectionError = null, RequestOwnerId = ownerId }); }
        }).ConfigureAwait(false);

        if (_selector is null)
        {
            await _dispatcher.InvokeAsync(() =>
            {
                if (!IsCurrent(generation, identity.Stream.AcquisitionRunId)) { return; }
                lock (_gate)
                {
                    _selectedOwnerId = ownerId;
                    _activeCancellation = null;
                    _activeOwnerId = null;
                }
                ReplaceState(new(identity.Stream.AcquisitionRunId, null, identity, identity.AcquisitionIndex,
                    TraceSelectionResidency.Resident, BScanTraceResultKind.Available, null, false, null));
            }).ConfigureAwait(false);
            return;
        }

        AScanSelectionResult result;
        try
        {
            result = await _selector.ResolveAsync(identity, checked((ulong)generation), cancellation.Token)
            .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        { result = new(BScanTraceResultKind.Cancelled); }
        catch (Exception error)
        { result = new(BScanTraceResultKind.Faulted, Error: error.Message); }

        if (!IsCurrent(generation, identity.Stream.AcquisitionRunId)) { return; }
        if (result.Kind == BScanTraceResultKind.Available && result.Snapshot is not null)
        {
            bool published = false;
            await _dispatcher.InvokeAsync(() =>
            {
                if (!IsCurrent(generation, identity.Stream.AcquisitionRunId)) { return; }
                lock (_gate)
                {
                    if (!IsCurrent(generation, identity.Stream.AcquisitionRunId)) { return; }
                    _selectedOwnerId = ownerId;
                    _activeCancellation = null;
                    _activeOwnerId = null;
                    published = true;
                }
                ReplaceState(new(identity.Stream.AcquisitionRunId, result.Snapshot, identity, identity.AcquisitionIndex,
                    TraceSelectionResidency.Resident, BScanTraceResultKind.Available, null, false, null));
            }).ConfigureAwait(false);
            if (!published) { return; }
            await _dispatcher.InvokeAsync(() => { }).ConfigureAwait(false);
            return;
        }

        BScanTraceResultKind kind = result.Kind;
        await _dispatcher.InvokeAsync(() =>
        {
            if (!IsCurrent(generation, identity.Stream.AcquisitionRunId)) { return; }
            ReplaceState(_state with
            {
                LastResult = kind,
                SelectionError = kind == BScanTraceResultKind.Faulted
                ? result.Error ?? "Trace selection failed." : null,
                IsPending = false,
                RequestOwnerId = null
            });
        }).ConfigureAwait(false);
        lock (_gate) { if (_generation == generation) { _activeCancellation = null; _activeOwnerId = null; } }
    }

    private bool IsCurrent(long generation, AcquisitionRunId runId) =>
        !IsClosed && Volatile.Read(ref _generation) == generation && _activeRun == runId;

    private void ReplaceState(TraceSelectionState state)
    {
        if (_state == state || IsClosed) { return; }
        _state = state;
        OnPropertyChanged(nameof(State));
    }

    public ValueTask DisposeAsync()
    {
        CancellationTokenSource? cancellation;
        TaskCompletionSource? completion = null;
        Task[] active = [];
        lock (_gate)
        {
            if (_disposal is not null) { return new(_disposal); }
            Volatile.Write(ref _closing, 1);
            _generation++;
            cancellation = _activeCancellation;
            active = [.. _resolutions];
            completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _disposal = completion.Task;
        }
        Exception? cancellationError = null;
        try { cancellation?.Cancel(); } catch (Exception error) { cancellationError = error; }
        _ = CompleteDisposeAsync(active, completion!, cancellationError);
        return new(_disposal);
    }

    private async Task CompleteDisposeAsync(Task[] active, TaskCompletionSource completion,
        Exception? cancellationError)
    {
        try
        {
            await DisposeCoreAsync(active).ConfigureAwait(false);
            if (cancellationError is null) { completion.TrySetResult(); }
            else { completion.TrySetException(cancellationError); }
        }
        catch (Exception error) { completion.TrySetException(error); }
    }

    private async Task DisposeCoreAsync(Task[] active)
    {
        Exception? failure = null;
        try { await Task.WhenAll(active).ConfigureAwait(false); } catch (Exception error) { failure = error; }
        try { await _dispatcher.InvokeAsync(() => { }).ConfigureAwait(false); }
        catch (Exception error) { failure ??= error; }
        lock (_gate)
        {
            _activeCancellation?.Dispose();
            _activeCancellation = null;
            _selectedOwnerId = null;
            _retiredRuns.Clear();
            _retiredRunOrder.Clear();
            _residencyVersions.Clear();
        }
        if (failure is not null) { throw failure; }
    }

    protected override void OnObserverFailure(Exception error) => Interlocked.Increment(ref _observerErrors);
}
