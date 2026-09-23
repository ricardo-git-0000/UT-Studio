using System.ComponentModel;
using UTStudio.Domain.Acquisition;
using UTStudio.Visualization.Core;

namespace UTStudio.Presentation;

public enum AScanDisplayMode { NoData, Live, Selected }
public enum BScanSelectionResidency { Resident, Evicted }

public sealed record BScanSelectionState(
    AcquisitionRunId? RunId,
    AScanSnapshot? Snapshot,
    BScanTraceIdentity? Identity,
    ulong? AcquisitionIndex,
    BScanSelectionResidency? Residency,
    BScanTraceResultKind? LastResult,
    string? SelectionError,
    bool IsPending,
    string? RequestOwnerId)
{
    public static BScanSelectionState Empty { get; } = new(null, null, null, null, null, null, null, false, null);
}

/// <summary>Global neutral latest-selection-wins resolver for one related A/B scan group.</summary>
public sealed class BScanSelectionCoordinator : SafeObservableObject, IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly IUiDispatcher _dispatcher;
    private readonly IBScanTraceProvider _provider;
    private readonly IAScanProjector _projector;
    private CancellationTokenSource? _activeCancellation;
    private BScanTraceSnapshot? _selectedTrace;
    private BScanSelectionState _state = BScanSelectionState.Empty;
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

    public BScanSelectionCoordinator(IUiDispatcher dispatcher, IBScanTraceProvider provider, IAScanProjector projector)
    {
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
        _projector = projector ?? throw new ArgumentNullException(nameof(projector));
    }

    public BScanSelectionState State => _state;
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
            BScanTraceSnapshot? previous;
            lock (_gate) { previous = _selectedTrace; _selectedTrace = null; }
            _selectedOwnerId = null;
            ReplaceState(BScanSelectionState.Empty with { RunId = runId });
            previous?.Dispose();
        });
    }

    public Task SelectAsync(string ownerId, BScanTraceIdentity identity)
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
            if (IsClosed || _activeRun != identity.RunId) { return Task.CompletedTask; }
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

    private async Task CompleteResolutionAsync(Task previous, string ownerId, BScanTraceIdentity identity, long generation,
        CancellationTokenSource cancellation, TaskCompletionSource completion)
    {
        Exception? failure = null;
        try
        {
            try { await previous.ConfigureAwait(false); } catch { }
            if (IsCurrent(generation, identity.RunId))
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

    public void UpdateResidency(string ownerId, AcquisitionRunId runId, ulong snapshotVersion,
        BScanAcquisitionRange residentRange)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerId);
        _ = _dispatcher.InvokeAsync(() =>
        {
            if (IsClosed || _selectedOwnerId != ownerId || _state.RunId != runId ||
                _state.AcquisitionIndex is not { } index) { return; }
            if (_residencyVersions.TryGetValue(ownerId, out var previous) && previous.RunId == runId &&
                snapshotVersion <= previous.Version) { return; }
            _residencyVersions[ownerId] = (runId, snapshotVersion);
            BScanSelectionResidency next = index >= residentRange.First && index <= residentRange.Last
                ? BScanSelectionResidency.Resident : BScanSelectionResidency.Evicted;
            if (next == _state.Residency) { return; }
            ReplaceState(_state with { Residency = next });
        });
    }

    private async Task ResolveAsync(string ownerId, BScanTraceIdentity identity, long generation,
        CancellationTokenSource cancellation)
    {
        await _dispatcher.InvokeAsync(() =>
        {
            if (IsCurrent(generation, identity.RunId))
            { ReplaceState(_state with { IsPending = true, LastResult = null, SelectionError = null, RequestOwnerId = ownerId }); }
        }).ConfigureAwait(false);

        BScanTraceResult result;
        try { result = await _provider.GetTraceAsync(identity, cancellation.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { result = BScanTraceResult.Cancelled(); }
        catch (Exception error) { result = BScanTraceResult.Faulted(error); }

        BScanTraceSnapshot? candidate = result.Trace;
        AScanSnapshot? projected = null;
        string? validationError = null;
        if (result.Kind == BScanTraceResultKind.Available && candidate is not null)
        {
            try
            {
                ValidateCandidate(identity, candidate);
                projected = _projector.Project(candidate.Descriptor.Metadata, identity.Sequence,
                    candidate.Descriptor.ElapsedSinceRunStart, candidate.Samples.Span, checked((ulong)generation));
            }
            catch (Exception error) { validationError = error.Message; }
        }

        if (!IsCurrent(generation, identity.RunId)) { candidate?.Dispose(); return; }
        if (projected is not null && candidate is not null)
        {
            BScanTraceSnapshot? previous = null;
            bool published = false;
            await _dispatcher.InvokeAsync(() =>
            {
                if (!IsCurrent(generation, identity.RunId)) { return; }
                lock (_gate)
                {
                    if (!IsCurrent(generation, identity.RunId)) { return; }
                    previous = _selectedTrace;
                    _selectedTrace = candidate;
                    _selectedOwnerId = ownerId;
                    _activeCancellation = null;
                    _activeOwnerId = null;
                    published = true;
                }
                ReplaceState(new(identity.RunId, projected, identity, candidate.Descriptor.AcquisitionIndex,
                    BScanSelectionResidency.Resident, BScanTraceResultKind.Available, null, false, null));
            }).ConfigureAwait(false);
            if (!published) { candidate.Dispose(); return; }
            await _dispatcher.InvokeAsync(() => { }).ConfigureAwait(false);
            previous?.Dispose();
            return;
        }

        candidate?.Dispose();
        BScanTraceResultKind kind = validationError is null ? result.Kind : BScanTraceResultKind.Faulted;
        await _dispatcher.InvokeAsync(() =>
        {
            if (!IsCurrent(generation, identity.RunId)) { return; }
            ReplaceState(_state with
            {
                LastResult = kind,
                SelectionError = kind == BScanTraceResultKind.Faulted
                ? validationError ?? result.Error ?? "Trace selection failed." : null,
                IsPending = false,
                RequestOwnerId = null
            });
        }).ConfigureAwait(false);
        lock (_gate) { if (_generation == generation) { _activeCancellation = null; _activeOwnerId = null; } }
    }

    private bool IsCurrent(long generation, AcquisitionRunId runId) =>
        !IsClosed && Volatile.Read(ref _generation) == generation && _activeRun == runId;

    private static void ValidateCandidate(BScanTraceIdentity requested, BScanTraceSnapshot candidate)
    {
        var descriptor = candidate.Descriptor;
        if (descriptor.Identity != requested || descriptor.Metadata.SourceId != requested.SourceId ||
            descriptor.Metadata.RunId != requested.RunId ||
            descriptor.Metadata.Configuration.PhysicalChannelId != requested.PhysicalChannelId ||
            descriptor.SampleCount != candidate.Samples.Length ||
            descriptor.Metadata.Configuration.SampleCount != descriptor.SampleCount ||
            descriptor.Metadata.Configuration.SampleRateHz != descriptor.SampleRateHz ||
            descriptor.Metadata.Configuration.FirstSampleOffsetSeconds != descriptor.FirstSampleOffsetSeconds)
        { throw new InvalidOperationException("The trace provider returned incompatible identity or metadata."); }
    }

    private void ReplaceState(BScanSelectionState state)
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
        BScanTraceSnapshot? selected = null;
        lock (_gate)
        {
            _activeCancellation?.Dispose();
            _activeCancellation = null;
            selected = _selectedTrace;
            _selectedTrace = null;
            _selectedOwnerId = null;
            _retiredRuns.Clear();
            _retiredRunOrder.Clear();
            _residencyVersions.Clear();
        }
        try { selected?.Dispose(); } catch (Exception error) { failure ??= error; }
        if (failure is not null) { throw failure; }
    }

    protected override void OnObserverFailure(Exception error) => Interlocked.Increment(ref _observerErrors);
}
