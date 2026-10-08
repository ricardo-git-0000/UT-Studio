using System.Buffers;
using System.Numerics;
using UTStudio.Domain.Acquisition;
using UTStudio.Visualization.Core.Scan2D;

namespace UTStudio.Visualization.Core;

internal interface IByteOwnerPool
{
    int GetMaximumRentalLength(int minimumLength) => minimumLength;
    IMemoryOwner<byte> Rent(int minimumLength);
    IMemoryOwner<byte> Rent(int minimumLength, BScanVisualBufferKind kind) => Rent(minimumLength);
}
internal sealed class SharedByteOwnerPool : IByteOwnerPool
{
    internal static SharedByteOwnerPool Instance { get; } = new();
    public int GetMaximumRentalLength(int minimumLength) => Math.Max(4096,
        checked((int)BitOperations.RoundUpToPowerOf2((uint)Math.Max(1, minimumLength))));
    public IMemoryOwner<byte> Rent(int minimumLength) => MemoryPool<byte>.Shared.Rent(minimumLength);
}

/// <summary>Bounded, neutral and latest-only visual projection over a B-Scan ingress.</summary>
public sealed class BScanVisualDelivery : IBScanVisualSource, IAsyncDisposable
{
    private const int DurationWindow = 1024;
    public static readonly TimeSpan MinimumPublicationInterval = TimeSpan.FromTicks(333_334);
    public static readonly TimeSpan WideLivePublicationInterval = TimeSpan.FromMilliseconds(100);
    private readonly object _gate = new();
    private readonly BScanIngress _ingress;
    private readonly TimeProvider _clock;
    private readonly IByteOwnerPool _pool;
    private readonly Scan2DResampler _resampler;
    private readonly SemaphoreSlim _wake = new(0, 1);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly List<Subscription> _subscriptions = [];
    private readonly Task _worker;
    private readonly Dictionary<string, BScanRenderRequest> _pending = [];
    private readonly Dictionary<string, BScanRenderRequest> _lastRequests = [];
    private readonly Dictionary<string, long> _pendingGenerations = [];
    private readonly Dictionary<string, long> _committedGenerations = [];
    private readonly Dictionary<string, int> _consumerOwners = [];
    private readonly Dictionary<string, long> _consumerOwnerBytes = [];
    private readonly Dictionary<string, BScanVisualSnapshot> _currentByConsumer = [];
    private readonly Dictionary<string, BScanDisplayedVisualIdentity> _displayedByConsumer = [];
    private readonly Dictionary<string, long> _consumerLifetimes = [];
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, long> _presentationEpochs = [];
    private readonly HashSet<string> _consumers = [];
    private readonly HashSet<string> _bootstrapConsumers = [];
    private readonly Dictionary<string, BScanConsumerBootstrapState> _bootstrapStates = [];
    private readonly HashSet<string> _retiringConsumers = [];
    private long _requestGeneration;
    private long _consumerLifetime;
    private long _presentationEpoch;
    private AcquisitionRunId? _latestVisualRunId;
    private long _renderRequests;
    private long _fullIdentityRequests;
    private bool _coalesced;
    private readonly HashSet<string> _recoveryConsumers = [];
    private bool _disposed;
    private Task? _disposal;
    private long? _lastPublication;
    private ulong _snapshotVersion;
    private int _regionOperations;
    private TaskCompletionSource? _regionDrain;
    private TaskCompletionSource? _ownerDrain;
    private long _activeOwnerBytes;
    private int _activeOwners;
    private int _maximumOwners;
    private long _maximumOwnerBytes;
    private readonly long[] _buildDurations = new long[DurationWindow];
    private long _buildDurationCount;
    private long _buildDurationMaximum;
    private ulong _columnsIndexed, _updatesPublished, _updatesCoalesced, _fullRefreshes;
    private ulong _incrementalRefreshes, _staleCandidates, _cancelledCandidates, _observerErrors;
    private ulong _observerCallbacksStarted, _observerCallbacksCompleted;
    private bool _closed;
    private Task? _drain;
    private string? _lastError;
    private BScanVisualAudit? _audit;
    private BScanMetadataCandidateAudit? _lastMetadataCandidateAudit;
    private BScanRequestGenerationReason _lastGenerationReason;
    internal Action? BeforeRasterCandidateValidation { get; set; }
    internal Action? BeforeRasterCandidateCommit { get; set; }
    /// <summary>Diagnostic seam invoked after a real Lanczos row and before the next cancellation checkpoint.</summary>
    internal Action<int>? AfterLanczosKernelRowProcessed
    {
        get => _resampler.AfterKernelRowProcessed;
        set => _resampler.AfterKernelRowProcessed = value;
    }
    internal Action<int, int>? AfterRasterColumnReleased { get; set; }
    internal Action? BeforeMetadataCandidateValidation { get; set; }
    internal Action<BScanVisualSnapshot>? BeforeMetadataCandidateCommit { get; set; }
    internal Action<string, AcquisitionRunId>? AfterBootstrapRequestQueued { get; set; }
    internal int ActiveConsumerCount { get { lock (_gate) { return _consumers.Count; } } }
    internal int PendingBootstrapCount { get { lock (_gate) { return _bootstrapConsumers.Count; } } }
    internal int RetainedSnapshotCount { get { lock (_gate) { return _currentByConsumer.Count; } } }
    internal long RenderRequestCount => Interlocked.Read(ref _renderRequests);
    internal long FullIdentityRequestCount => Interlocked.Read(ref _fullIdentityRequests);
    internal long ResamplingTablePreparationCount => _resampler.TablePreparationCount;
    internal BScanMetadataCandidateAudit? LastMetadataCandidateAudit
    { get { lock (_gate) { return _lastMetadataCandidateAudit; } } }
    internal void EnableAudit(int capacity) => Volatile.Write(ref _audit, new(capacity));
    internal BScanVisualAuditSnapshot AuditSnapshot =>
        Volatile.Read(ref _audit)?.Snapshot() ?? new([], 0);
    internal BScanVisualMemorySnapshot VisualMemorySnapshot => _ingress.VisualBuffers.Snapshot;
    internal BScanDisplayedVisualIdentity? GetAcknowledgedDisplay(string consumerId)
    { lock (_gate) { return _displayedByConsumer.GetValueOrDefault(consumerId); } }
    internal BScanRenderRequest? GetLastRequest(string consumerId)
    { lock (_gate) { return _lastRequests.GetValueOrDefault(consumerId); } }

    public BScanVisualDelivery(BScanIngress ingress, TimeProvider? timeProvider = null)
        : this(ingress, timeProvider,
            new BoundedByteOwnerPool((ingress ?? throw new ArgumentNullException(nameof(ingress))).VisualBuffers))
    { }

    internal BScanVisualDelivery(BScanIngress ingress, TimeProvider? timeProvider, IByteOwnerPool pool)
    {
        _ingress = ingress ?? throw new ArgumentNullException(nameof(ingress));
        _clock = timeProvider ?? TimeProvider.System;
        _pool = pool ?? throw new ArgumentNullException(nameof(pool));
        _resampler = new Scan2DResampler(new BoundedLanczosTablePool(_ingress.VisualBuffers), ChangeTableBytes,
            reuseTables: true);
        _ingress.VisualStateChanged += OnVisualStateChanged;
        _worker = Task.Run(PublishLoopAsync);
    }

    public bool RegisterConsumer(string consumerId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(consumerId);
        lock (_gate)
        {
            if (_disposed) { return false; }
            if (_consumers.Contains(consumerId)) { return true; }
            if (_consumers.Count >= _ingress.Options.MaximumRasterConsumers) { return false; }
            _consumers.Add(consumerId);
            _consumerLifetimes[consumerId] = ++_consumerLifetime;
            _presentationEpochs[consumerId] = 0;
            _consumerOwners[consumerId] = 0;
            _consumerOwnerBytes[consumerId] = 0;
        }
        return true;
    }

    public void RequestBootstrap(string consumerId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(consumerId);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_consumers.Contains(consumerId))
            { throw new InvalidOperationException("The raster consumer is not registered."); }
            if (!_bootstrapConsumers.Add(consumerId)) { return; }
            _bootstrapStates[consumerId] = new(
                _ingress.CurrentVisualState is { IsOpen: true } current ? current.Metadata.RunId : null,
                BScanBootstrapPhase.Requested, 0, null);
        }
        Audit(BScanVisualAuditEventKind.BootstrapRequested, consumerId,
            _ingress.CurrentVisualState, 0, null);
        QueueBootstrap(consumerId);
    }

    private void QueueBootstrap(string consumerId)
    {
        BScanRenderRequest? previousRequest = null;
        BScanVisualLeaseBatch? batch = null;
        Exception? primary = null;
        Exception? swallowed = null;
        try
        {
            batch = _ingress.CaptureVisualLeases(_shutdown.Token,
                out BScanIngressVisualState? ingressState, out BScanStoreVisualState state);
            int count = batch?.Count ?? 0;
            ConventionalUtFrameMetadata? metadata = ingressState?.Metadata;
            if (metadata is null || count == 0 || state.ResidentRange is not { } resident) { return; }
            lock (_gate)
            {
                if (!_consumers.Contains(consumerId) || ingressState is null ||
                    !ReferenceEquals(ingressState, _ingress.CurrentVisualState)) { return; }
                if (_lastRequests.TryGetValue(consumerId, out BScanRenderRequest? current))
                {
                    if (current.RunId == metadata.RunId)
                    {
                        if (_bootstrapStates.TryGetValue(consumerId, out BScanConsumerBootstrapState? bootstrap) &&
                            bootstrap.RunId == metadata.RunId &&
                            bootstrap.Phase is BScanBootstrapPhase.Pending or BScanBootstrapPhase.Admitted)
                        { return; }
                        previousRequest = current;
                    }
                    else { previousRequest = current; }
                }
                _bootstrapConsumers.Add(consumerId);
                _bootstrapStates[consumerId] = new(metadata.RunId, BScanBootstrapPhase.Pending,
                    state.StoreRevision, null);
            }
            Audit(BScanVisualAuditEventKind.BootstrapPending, consumerId, ingressState, 0, null);
            double minimum = metadata.Configuration.FirstSampleOffsetSeconds;
            double maximum = minimum + metadata.Configuration.SampleCount / metadata.Configuration.SampleRateHz;
            RequestRender(new(metadata.RunId, BScanSpatialViewportOperations.Reset(resident),
                ScanTimeViewportOperations.Create(minimum, maximum),
                previousRequest?.PixelWidth ?? 1, previousRequest?.PixelHeight ?? 1,
                previousRequest?.RenderConfigurationVersion ?? 0, ConsumerId: consumerId,
                FollowLiveWindow: previousRequest?.FollowLiveWindow ?? false,
                Resampling: previousRequest?.EffectiveResampling ?? Scan2DResamplingOptions.Default));
            AfterBootstrapRequestQueued?.Invoke(consumerId, metadata.RunId);
        }
        catch (Exception error) when (error is ObjectDisposedException or InvalidOperationException)
        { swallowed = error; }
        catch (Exception error) { primary = error; throw; }
        finally
        {
            if (swallowed is not null) { batch?.DisposeAfterSwallowed(swallowed); }
            else { batch?.Dispose(primary); }
        }
    }

    public void UnregisterConsumer(string consumerId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(consumerId);
        lock (_gate)
        {
            _retiringConsumers.Add(consumerId);
            _pending.Remove(consumerId);
            _lastRequests.Remove(consumerId);
            _pendingGenerations.Remove(consumerId);
            _committedGenerations.Remove(consumerId);
            _currentByConsumer.Remove(consumerId);
            _displayedByConsumer.Remove(consumerId);
            _consumerLifetimes.Remove(consumerId);
            _presentationEpochs.TryRemove(consumerId, out _);
            _bootstrapConsumers.Remove(consumerId);
            _bootstrapStates.Remove(consumerId);
            _recoveryConsumers.Remove(consumerId);
            if (_consumerOwners.GetValueOrDefault(consumerId) == 0)
            {
                _consumerOwners.Remove(consumerId); _consumerOwnerBytes.Remove(consumerId);
                _consumers.Remove(consumerId); _retiringConsumers.Remove(consumerId);
            }
        }
        Audit(BScanVisualAuditEventKind.ConsumerRemoved, consumerId,
            _ingress.CurrentVisualState, 0, null, BScanVisualAuditResult.Closed);
    }

    public BScanVisualDiagnostics Diagnostics
    {
        get
        {
            BScanDiagnostics ingress = _ingress.Diagnostics;
            lock (_gate)
            {
                return new(_columnsIndexed, _fullRefreshes + _incrementalRefreshes, _fullRefreshes,
                    _incrementalRefreshes, _staleCandidates + _cancelledCandidates, _updatesPublished,
                    _updatesCoalesced, _observerCallbacksStarted, _observerCallbacksCompleted,
                    _observerErrors, _activeOwners, _maximumOwners, ingress.VisualLogicalBytes,
                    _activeOwnerBytes, _maximumOwnerBytes, BuildDurationsUnderLock(),
                    _closed ? BScanVisualCloseState.Closed : _disposed ? BScanVisualCloseState.Closing : BScanVisualCloseState.Open,
                    _regionOperations, _lastError)
                {
                    EffectiveCapacity = ingress.EffectiveCapacity,
                    ColumnsAccepted = ingress.BScanColumnsAccepted,
                    ColumnsRejectedPinnedSlot = ingress.BScanColumnsRejectedPinnedSlot,
                    ColumnsEvicted = ingress.BScanColumnsEvicted
                };
            }
        }
    }

    public void RequestRender(BScanRenderRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        request.Validate();
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_ingress.CurrentVisualState is { IsOpen: true } visualState &&
                request.RunId != visualState.Metadata.RunId)
            { return; }
            if (request.ConsumerId == "default" && !_consumers.Contains("default"))
            {
                _consumers.Add("default"); _consumerLifetimes["default"] = ++_consumerLifetime;
                _presentationEpochs["default"] = 0;
                _consumerOwners["default"] = 0; _consumerOwnerBytes["default"] = 0;
            }
            if (!_consumers.Contains(request.ConsumerId))
            { throw new InvalidOperationException("The raster consumer is not registered."); }
            if (_pending.TryGetValue(request.ConsumerId, out BScanRenderRequest? pending) &&
                HasSameRenderSignature(pending, request))
            { return; }
            Interlocked.Increment(ref _renderRequests);
            if (request.DisplayedSnapshotVersion is null) { Interlocked.Increment(ref _fullIdentityRequests); }
            if (_pending.ContainsKey(request.ConsumerId)) { _coalesced = true; _updatesCoalesced++; }
            _lastRequests[request.ConsumerId] = request;
            if (_bootstrapStates.TryGetValue(request.ConsumerId, out BScanConsumerBootstrapState? bootstrap) &&
                bootstrap.RunId == request.RunId &&
                bootstrap.Phase is BScanBootstrapPhase.Requested or BScanBootstrapPhase.Pending)
            {
                _bootstrapStates[request.ConsumerId] = bootstrap with
                { Phase = BScanBootstrapPhase.Admitted, RequestGeneration = _requestGeneration + 1 };
            }
            else
            {
                _bootstrapConsumers.Remove(request.ConsumerId);
                _bootstrapStates.Remove(request.ConsumerId);
            }
            _pending[request.ConsumerId] = request;
            _pendingGenerations[request.ConsumerId] = ++_requestGeneration;
            _presentationEpochs[request.ConsumerId] = ++_presentationEpoch;
            _lastGenerationReason = BScanRequestGenerationReason.RequestRender;
            Audit(BScanVisualAuditEventKind.BuildAdmitted, request.ConsumerId,
                _ingress.CurrentVisualState, _requestGeneration, request,
                BScanVisualAuditResult.Accepted);
            if (_wake.CurrentCount == 0) { _wake.Release(); }
        }
    }

    public void AcknowledgeDisplayed(string consumerId, BScanDisplayedVisualIdentity displayed)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(consumerId);
        ArgumentNullException.ThrowIfNull(displayed);
        lock (_gate)
        {
            if (_disposed || !_consumers.Contains(consumerId)) { return; }
            if (!_lastRequests.TryGetValue(consumerId, out BScanRenderRequest? current) ||
                current.RunId != displayed.Stream.RunId) { return; }
            BScanRenderRequest acknowledged = current with
            {
                SpatialViewport = displayed.Render.SpatialViewport,
                TimeViewport = displayed.Render.TimeViewport,
                PixelWidth = displayed.Render.PixelWidth,
                PixelHeight = displayed.Render.PixelHeight,
                RenderConfigurationVersion = displayed.Render.RenderConfigurationVersion,
                DisplayedSnapshotVersion = displayed.Revision.SnapshotVersion,
                DisplayedStoreRevision = displayed.Revision.StoreRevision,
                DisplayedWrapGeneration = displayed.Revision.WrapGeneration,
                Displayed = displayed
            };
            _lastRequests[consumerId] = acknowledged;
            _displayedByConsumer[consumerId] = displayed;
            if (_pending.ContainsKey(consumerId))
            {
                _pending[consumerId] = acknowledged;
                _pendingGenerations[consumerId] = ++_requestGeneration;
                _lastGenerationReason = BScanRequestGenerationReason.AcknowledgeDisplayed;
            }
        }
    }

    public IDisposable Subscribe(IObserver<BScanVisualUpdate> observer)
    {
        ArgumentNullException.ThrowIfNull(observer);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var subscription = new Subscription(this, observer);
            _subscriptions.Add(subscription);
            foreach (BScanVisualSnapshot snapshot in _currentByConsumer.Values)
            {
                _lastRequests.TryGetValue(snapshot.ConsumerId, out BScanRenderRequest? request);
                BScanIngressVisualState? ingressState = _ingress.CurrentVisualState is { } current &&
                    current.Metadata.RunId == snapshot.RunId ? current : null;
                subscription.Offer(new(snapshot), CaptureCallbackAuditContext(snapshot.ConsumerId,
                    ingressState, _committedGenerations.GetValueOrDefault(snapshot.ConsumerId), request));
            }
            return subscription;
        }
    }

    public async ValueTask<BScanRasterResult> GetRegionAsync(BScanRenderRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        request.Validate();
        long requestGeneration;
        long consumerLifetime;
        lock (_gate)
        {
            if (_disposed) { return BScanRasterResult.NotAvailable(); }
            _regionOperations++;
            requestGeneration = _pendingGenerations.GetValueOrDefault(request.ConsumerId);
            consumerLifetime = _consumerLifetimes.GetValueOrDefault(request.ConsumerId);
        }
        long started = _clock.GetTimestamp();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdown.Token);
        try
        {
            linked.Token.ThrowIfCancellationRequested();
            lock (_gate)
            {
                if (request.ConsumerId == "default" && !_consumers.Contains("default"))
                {
                    _consumers.Add("default"); _consumerLifetimes["default"] = ++_consumerLifetime;
                    _presentationEpochs["default"] = 0;
                    _consumerOwners["default"] = 0; _consumerOwnerBytes["default"] = 0;
                    consumerLifetime = _consumerLifetimes["default"];
                }
                if (!_consumers.Contains(request.ConsumerId)) { return BScanRasterResult.NotAvailable(); }
                if (_consumerOwners.GetValueOrDefault(request.ConsumerId) >=
                    _ingress.Options.MaximumRasterOwnersPerConsumer)
                { return BScanRasterResult.ResourceLimit("The consumer raster owner quota is exhausted."); }
            }
            BScanRasterResult result = BuildRegion(request, requestGeneration,
                consumerLifetime, linked.Token);
            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            lock (_gate) { _cancelledCandidates++; }
            return BScanRasterResult.Cancelled();
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
            return BScanRasterResult.NotAvailable();
        }
        catch (Exception error)
        {
            lock (_gate)
            {
                bool current = _consumerLifetimes.GetValueOrDefault(request.ConsumerId) == consumerLifetime &&
                    _pendingGenerations.GetValueOrDefault(request.ConsumerId) == requestGeneration &&
                    (!_lastRequests.TryGetValue(request.ConsumerId, out BScanRenderRequest? latest) ||
                    HasSameRenderSignature(request, latest));
                if (current)
                {
                    _lastError = Message(error);
                    RequestRecoveryUnderLock(request.ConsumerId, requestGeneration, request);
                }
                else
                {
                    Audit(BScanVisualAuditEventKind.CandidateRejectedStale, request.ConsumerId,
                        _ingress.CurrentVisualState, requestGeneration, request,
                        BScanVisualAuditResult.Faulted, BScanVisualAuditRejection.Generation);
                }
            }
            return BScanRasterResult.Faulted(error);
        }
        finally
        {
            RecordBuildDuration(_clock.GetElapsedTime(started, _clock.GetTimestamp()));
            EndRegionOperation();
        }
    }

    private BScanRasterResult BuildRegion(BScanRenderRequest request, long requestGeneration,
        long consumerLifetime, CancellationToken token)
    {
        BScanRenderRequest admittedRequest = request;
        BScanVisualLeaseBatch? batch = null;
        Exception? primary = null;
        try
        {
            batch = _ingress.CaptureVisualLeases(token,
                out BScanIngressVisualState? ingressState, out BScanStoreVisualState state);
            BScanTraceStore.BScanTraceLease?[] leases = batch?.Items ?? [];
            int count = batch?.Count ?? 0;
            Audit(BScanVisualAuditEventKind.BuildStarted, request.ConsumerId, ingressState,
                requestGeneration, request);
            ConventionalUtFrameMetadata? metadata = ingressState?.Metadata;
            if (metadata is null || metadata.RunId != request.RunId || count == 0 || state.ResidentRange is null)
            { return BScanRasterResult.NotAvailable(); }
            Array.Sort(leases, 0, count, BScanLeaseComparer.Instance);
            request = ReconcileLiveWindow(request, state.ResidentRange.Value);
            token.ThrowIfCancellationRequested();

            int horizontalPixelShift = GetHorizontalPixelShift(request);
            BScanFullRefreshReason reason = GetFullRefreshReason(request, metadata, state, leases, count,
                horizontalPixelShift);
            bool full = reason != BScanFullRefreshReason.None;
            ulong dirtyFirst = ulong.MaxValue, dirtyLast = 0;
            for (int i = 0; i < count; i++)
            {
                BScanTraceStore.BScanTraceLease lease = leases[i]!;
                if (full || lease.VisualRevision > request.DisplayedStoreRevision.GetValueOrDefault())
                {
                    dirtyFirst = Math.Min(dirtyFirst, lease.Descriptor.AcquisitionIndex);
                    dirtyLast = Math.Max(dirtyLast, lease.Descriptor.AcquisitionIndex);
                }
            }
            if (dirtyFirst == ulong.MaxValue)
            {
                return BScanRasterResult.NotAvailable();
            }

            (int x, int width) = RasterColumns(request, dirtyFirst, dirtyLast, full);
            if (horizontalPixelShift > 0 && (full || x != request.PixelWidth - horizontalPixelShift ||
                width != horizontalPixelShift))
            {
                horizontalPixelShift = 0;
                reason |= BScanFullRefreshReason.Recovery;
                full = true;
                x = 0;
                width = request.PixelWidth;
            }
            int cells = checked(width * request.PixelHeight);
            int length = checked(cells * 3);
            int reservationLength = _pool.GetMaximumRentalLength(length);
            if (reservationLength < length)
            { throw new InvalidOperationException("Raster pool returned an invalid reservation quote."); }
            if (!TryReserveRasterOwner(request.ConsumerId, reservationLength))
            {
                return BScanRasterResult.ResourceLimit("The B-Scan visual memory quota is exhausted.");
            }
            IMemoryOwner<byte> owner;
            try { owner = _pool.Rent(length); }
            catch { DiscardOwner(request.ConsumerId, reservationLength); throw; }
            if (owner.Memory.Length < length || owner.Memory.Length > reservationLength)
            {
                owner.Dispose();
                DiscardOwner(request.ConsumerId, reservationLength);
                throw new InvalidOperationException("Raster pool violated its rental reservation quote.");
            }
            long actualBytes = owner.Memory.Length;
            if (!TryAdjustRasterReservation(request.ConsumerId, reservationLength, actualBytes))
            {
                owner.Dispose();
                DiscardOwner(request.ConsumerId, reservationLength);
                return BScanRasterResult.ResourceLimit("The B-Scan visual memory quota is exhausted.");
            }
            Memory<byte> storage = owner.Memory[..length];
            storage.Span.Clear();
            Span<byte> pixels = storage.Span[..cells];
            Span<byte> validity = storage.Span.Slice(cells, cells);
            Span<byte> provenance = storage.Span.Slice(checked(cells * 2), cells);
            try
            {
                if (request.EffectiveResampling.Mode != Scan2DInterpolationMode.None)
                {
                    RasterizeResampled(leases, count, metadata, state.IndexBucketSamples, request,
                        storage, token);
                }
                else
                {
                    Rasterize(leases, count, metadata, state.IndexBucketSamples, request, x, width,
                        pixels, validity, provenance, token);
                }
                BeforeRasterCandidateValidation?.Invoke();
                token.ThrowIfCancellationRequested();
                BeforeRasterCandidateCommit?.Invoke();
            }
            catch
            {
                owner.Dispose();
                DiscardOwner(request.ConsumerId, actualBytes);
                throw;
            }
            var dirty = new BScanDirtyRange(dirtyFirst, dirtyLast,
                request.DisplayedStoreRevision.GetValueOrDefault(), state.StoreRevision);
            var candidate = CreateSnapshot(metadata, request, state, 0, dirty,
                new(x, 0, width, request.PixelHeight), full, reason, leases, count) with
            { HorizontalPixelShift = horizontalPixelShift };
            Audit(BScanVisualAuditEventKind.BuildCompleted, request.ConsumerId, ingressState,
                requestGeneration, request, BScanVisualAuditResult.Accepted);
            Audit(BScanVisualAuditEventKind.CommitAttempted, request.ConsumerId, ingressState,
                requestGeneration, request);
            BScanVisualSnapshot? snapshot = null;
            bool stale;
            bool active;
            BScanVisualAuditRejection staleRejection = BScanVisualAuditRejection.None;
            lock (_gate)
            {
                active = !_disposed && _consumers.Contains(request.ConsumerId) &&
                    !_retiringConsumers.Contains(request.ConsumerId) &&
                    _consumerLifetimes.GetValueOrDefault(request.ConsumerId) == consumerLifetime;
                bool generationCurrent =
                    _pendingGenerations.GetValueOrDefault(request.ConsumerId) == requestGeneration;
                bool signatureCurrent = !_lastRequests.TryGetValue(request.ConsumerId,
                    out BScanRenderRequest? latest) || HasSameRenderSignature(admittedRequest, latest);
                stale = active && (!generationCurrent || !signatureCurrent);
                if (stale)
                {
                    staleRejection = !generationCurrent ? BScanVisualAuditRejection.Generation :
                        BScanVisualAuditRejection.Signature;
                    _staleCandidates++;
                    RequestRecoveryUnderLock(request.ConsumerId, requestGeneration, request);
                }
                else if (active)
                {
                    snapshot = candidate with { SnapshotVersion = ++_snapshotVersion };
                    _currentByConsumer[request.ConsumerId] = snapshot;
                    _recoveryConsumers.Remove(request.ConsumerId);
                }
            }
            if (stale)
            {
                Audit(BScanVisualAuditEventKind.CandidateRejectedStale, request.ConsumerId,
                    ingressState, requestGeneration, request, BScanVisualAuditResult.Stale,
                    staleRejection);
                owner.Dispose(); DiscardOwner(request.ConsumerId, actualBytes); return BScanRasterResult.Stale();
            }
            if (!active)
            {
                Audit(BScanVisualAuditEventKind.CandidateRejectedClosed, request.ConsumerId,
                    ingressState, requestGeneration, request, BScanVisualAuditResult.Closed,
                    BScanVisualAuditRejection.Consumer);
                owner.Dispose(); DiscardOwner(request.ConsumerId, actualBytes); return BScanRasterResult.NotAvailable();
            }
            Audit(BScanVisualAuditEventKind.CandidateCommitted, request.ConsumerId, ingressState,
                requestGeneration, request, BScanVisualAuditResult.Committed);
            return BScanRasterResult.Available(new(snapshot!, x, 0, width, request.PixelHeight,
                owner, () => ReleaseOwner(request.ConsumerId, actualBytes)));
        }
        catch (Exception error) { primary = error; throw; }
        finally { batch?.Dispose(primary); }
    }

    private static BScanRenderRequest ReconcileLiveWindow(BScanRenderRequest request,
        BScanAcquisitionRange residentRange)
    {
        if (!request.FollowLiveWindow) { return request; }
        decimal width = request.SpatialViewport.Span;
        decimal residentCount = checked((decimal)(residentRange.Last - residentRange.First) + 1);
        if (residentCount < width) { return request; }
        ulong integralWidth = checked((ulong)decimal.Truncate(width));
        if (integralWidth == 0 || integralWidth != width) { return request; }
        ulong first = residentRange.Last - integralWidth + 1;
        return request with { SpatialViewport = new(first, 0, width) };
    }

    private static bool HasSameRenderSignature(BScanRenderRequest left, BScanRenderRequest right) =>
        left.RunId == right.RunId && left.SpatialViewport == right.SpatialViewport &&
        left.TimeViewport == right.TimeViewport && left.PixelWidth == right.PixelWidth &&
        left.PixelHeight == right.PixelHeight &&
        left.FollowLiveWindow == right.FollowLiveWindow &&
        left.RenderConfigurationVersion == right.RenderConfigurationVersion &&
        left.EffectiveResampling == right.EffectiveResampling &&
        left.DisplayedSnapshotVersion == right.DisplayedSnapshotVersion &&
        left.DisplayedStoreRevision == right.DisplayedStoreRevision &&
        left.DisplayedWrapGeneration == right.DisplayedWrapGeneration &&
        left.Displayed == right.Displayed;

    private BScanRenderRequest WithAcknowledgedDisplay(BScanRenderRequest request)
    {
        BScanDisplayedVisualIdentity? displayed;
        lock (_gate) { _displayedByConsumer.TryGetValue(request.ConsumerId, out displayed); }
        if (displayed is null || displayed.Stream.RunId != request.RunId ||
            request.Displayed is { } supplied &&
            supplied.Revision.StoreRevision > displayed.Revision.StoreRevision) { return request; }
        return request with
        {
            DisplayedSnapshotVersion = displayed.Revision.SnapshotVersion,
            DisplayedStoreRevision = displayed.Revision.StoreRevision,
            DisplayedWrapGeneration = displayed.Revision.WrapGeneration,
            Displayed = displayed
        };
    }

    private void Rasterize(BScanTraceStore.BScanTraceLease?[] leases, int count,
        ConventionalUtFrameMetadata metadata, int bucketSamples, BScanRenderRequest request, int rasterX, int width,
        Span<byte> pixels, Span<byte> validity, Span<byte> provenance, CancellationToken token)
    {
        double rate = metadata.Configuration.SampleRateHz;
        double offset = metadata.Configuration.FirstSampleOffsetSeconds;
        Span<int> sampleFirstByRow = stackalloc int[request.PixelHeight];
        Span<int> sampleLastByRow = stackalloc int[request.PixelHeight];
        for (int y = 0; y < request.PixelHeight; y++)
        {
            double t0 = request.TimeViewport.VisibleMinimumSeconds +
                request.TimeViewport.VisibleSpanSeconds * y / request.PixelHeight;
            double t1 = request.TimeViewport.VisibleMinimumSeconds +
                request.TimeViewport.VisibleSpanSeconds * (y + 1) / request.PixelHeight;
            sampleFirstByRow[y] = Math.Clamp((int)Math.Floor((t0 - offset) * rate),
                0, metadata.Configuration.SampleCount);
            sampleLastByRow[y] = Math.Clamp((int)Math.Ceiling((t1 - offset) * rate),
                0, metadata.Configuration.SampleCount);
        }
        int columnStart = 0;
        for (int localX = 0; localX < width; localX++)
        {
            token.ThrowIfCancellationRequested();
            int pixelX = rasterX + localX;
            decimal left = request.SpatialViewport.MinimumOffset +
                request.SpatialViewport.Span * pixelX / request.PixelWidth;
            decimal right = request.SpatialViewport.MinimumOffset +
                request.SpatialViewport.Span * (pixelX + 1) / request.PixelWidth;
            while (columnStart < count && BScanSpatialViewportOperations.RelativeOffset(
                leases[columnStart]!.Descriptor.AcquisitionIndex,
                request.SpatialViewport.OriginAcquisitionIndex) + 1 <= left)
            {
                leases[columnStart]!.Dispose();
                columnStart++;
            }
            int columnEnd = columnStart;
            while (columnEnd < count && BScanSpatialViewportOperations.RelativeOffset(
                leases[columnEnd]!.Descriptor.AcquisitionIndex,
                request.SpatialViewport.OriginAcquisitionIndex) < right)
            { columnEnd++; }
            for (int y = 0; y < request.PixelHeight; y++)
            {
                int sampleFirst = sampleFirstByRow[y];
                int sampleLastExclusive = sampleLastByRow[y];
                bool found = false;
                short selected = 0;
                int selectedMagnitude = -1;
                for (int column = columnStart; column < columnEnd; column++)
                {
                    BScanTraceStore.BScanTraceLease lease = leases[column]!;
                    SelectRange(lease, bucketSamples, sampleFirst, sampleLastExclusive,
                        ref selected, ref selectedMagnitude, ref found);
                }
                if (found)
                {
                    int destination = y * width + localX;
                    pixels[destination] = BScanBipolarIntensity8.Map(selected);
                    validity[destination] = 1;
                    provenance[destination] = (byte)Scan2DSampleProvenance.AcquiredSupport;
                }
            }
            AfterRasterColumnReleased?.Invoke(pixelX, count - columnStart);
        }
    }

    private void RasterizeResampled(BScanTraceStore.BScanTraceLease?[] leases, int count,
        ConventionalUtFrameMetadata metadata, int bucketSamples, BScanRenderRequest request,
        Memory<byte> destinationStorage, CancellationToken token)
    {
        bool lanczos = request.EffectiveResampling.Mode == Scan2DInterpolationMode.Lanczos;
        decimal requestedFirst = decimal.Floor(request.SpatialViewport.MinimumOffset);
        decimal requestedLastExclusive = decimal.Ceiling(request.SpatialViewport.MaximumOffset);
        decimal residentFirstOffset = lanczos ? RelativeOffset(request.SpatialViewport.OriginAcquisitionIndex,
            leases[0]!.Descriptor.AcquisitionIndex) : requestedFirst;
        decimal residentLastExclusive = lanczos ? checked(RelativeOffset(
            request.SpatialViewport.OriginAcquisitionIndex, leases[count - 1]!.Descriptor.AcquisitionIndex) + 1)
            : requestedLastExclusive;
        int firstOffset = checked((int)Math.Max(requestedFirst - (lanczos ? 3 : 0), residentFirstOffset));
        decimal lastExclusive = Math.Min(requestedLastExclusive + (lanczos ? 3 : 0), residentLastExclusive);
        int sourceWidth = checked((int)(lastExclusive - firstOffset));
        ArgumentOutOfRangeException.ThrowIfLessThan(sourceWidth, 1);
        double rate = metadata.Configuration.SampleRateHz;
        double offset = metadata.Configuration.FirstSampleOffsetSeconds;
        double visibleSampleMinimum = Math.Clamp(
            (request.TimeViewport.VisibleMinimumSeconds - offset) * rate, 0, metadata.Configuration.SampleCount);
        double visibleSampleMaximum = Math.Clamp(
            (request.TimeViewport.VisibleMaximumSeconds - offset) * rate, 0, metadata.Configuration.SampleCount);
        int visibleSampleFirst = Math.Clamp((int)Math.Floor(visibleSampleMinimum), 0, metadata.Configuration.SampleCount);
        int visibleSampleLastExclusive = Math.Clamp((int)Math.Ceiling(visibleSampleMaximum),
            0, metadata.Configuration.SampleCount);
        int visibleSamples = Math.Max(1, visibleSampleLastExclusive - visibleSampleFirst);
        int sourceHeight = Math.Min(request.PixelHeight, visibleSamples);
        bool upsamplingTime = visibleSamples <= request.PixelHeight;
        Span<int> sampleFirstByRow = stackalloc int[sourceHeight];
        Span<int> sampleLastByRow = stackalloc int[sourceHeight];
        for (int y = 0; y < sourceHeight; y++)
        {
            if (upsamplingTime)
            {
                sampleFirstByRow[y] = Math.Min(visibleSampleFirst + y, metadata.Configuration.SampleCount - 1);
                sampleLastByRow[y] = sampleFirstByRow[y] + 1;
            }
            else
            {
                double u0 = visibleSampleMinimum + (visibleSampleMaximum - visibleSampleMinimum) * y / sourceHeight;
                double u1 = visibleSampleMinimum + (visibleSampleMaximum - visibleSampleMinimum) * (y + 1) / sourceHeight;
                sampleFirstByRow[y] = Math.Clamp((int)Math.Floor(u0), 0, metadata.Configuration.SampleCount);
                sampleLastByRow[y] = Math.Clamp((int)Math.Ceiling(u1), 0, metadata.Configuration.SampleCount);
            }
        }

        int sourceCells = checked(sourceWidth * sourceHeight);
        IMemoryOwner<byte>? sourceIntensityOwner = null, sourceMaskOwner = null, sourceSegmentsOwner = null;
        long sourceIntensityBytes = 0, sourceMaskBytes = 0, sourceSegmentsBytes = 0;
        bool sourceIntensityAdmitted = false, sourceMaskAdmitted = false, sourceSegmentsAdmitted = false;
        try
        {
            sourceIntensityOwner = RentScratchOwner(request.ConsumerId, sourceCells, out sourceIntensityBytes);
            sourceIntensityAdmitted = true;
            sourceMaskOwner = RentScratchOwner(request.ConsumerId, sourceCells, out sourceMaskBytes);
            sourceMaskAdmitted = true;
            sourceSegmentsOwner = RentScratchOwner(request.ConsumerId, sourceCells, out sourceSegmentsBytes);
            sourceSegmentsAdmitted = true;
            Memory<byte> sourceIntensity = sourceIntensityOwner.Memory[..sourceCells];
            Memory<byte> sourceMask = sourceMaskOwner.Memory[..sourceCells];
            Memory<byte> sourceSegments = sourceSegmentsOwner.Memory[..sourceCells];
            // Valid source cells are always acquired support, so mask values 0/1 are also the exact
            // NoData/AcquiredSupport provenance values. Sharing avoids another full-raster owner.
            Memory<byte> sourceProvenance = sourceMask;
            sourceIntensity.Span.Clear();
            sourceMask.Span.Clear();
            sourceSegments.Span.Clear();
            byte segment = 1;
            bool gap = false;
            int leaseCursor = 0;
            for (int sourceX = 0; sourceX < sourceWidth; sourceX++)
            {
                token.ThrowIfCancellationRequested();
                ulong? acquisitionIndex = TryAddOffset(request.SpatialViewport.OriginAcquisitionIndex,
                    checked(firstOffset + sourceX));
                int leaseIndex = -1;
                if (acquisitionIndex is { } index)
                {
                    while (leaseCursor < count && leases[leaseCursor]!.Descriptor.AcquisitionIndex < index)
                    { leaseCursor++; }
                    if (leaseCursor < count && leases[leaseCursor]!.Descriptor.AcquisitionIndex == index)
                    { leaseIndex = leaseCursor; }
                }
                if (leaseIndex < 0) { gap = true; continue; }
                if (gap) { segment = segment == byte.MaxValue ? (byte)1 : checked((byte)(segment + 1)); gap = false; }
                for (int y = 0; y < sourceHeight; y++)
                {
                    bool found = false; short selected = 0; int magnitude = -1;
                    SelectRange(leases[leaseIndex]!, bucketSamples, sampleFirstByRow[y], sampleLastByRow[y],
                        ref selected, ref magnitude, ref found);
                    if (!found) { continue; }
                    int sourceIndex = checked(y * sourceWidth + sourceX);
                    sourceIntensity.Span[sourceIndex] = BScanBipolarIntensity8.Map(selected);
                    sourceMask.Span[sourceIndex] = 1; sourceSegments.Span[sourceIndex] = segment;
                    sourceProvenance.Span[sourceIndex] = (byte)Scan2DSampleProvenance.AcquiredSupport;
                }
            }
            for (int index = 0; index < count; index++) { leases[index]!.Dispose(); }

            double destinationStepY = (visibleSampleMaximum - visibleSampleMinimum) / request.PixelHeight;
            double sourceFirstY = upsamplingTime ? visibleSampleFirst + .5 : visibleSampleMinimum + destinationStepY / 2;
            double sourceStepY = upsamplingTime ? 1 : destinationStepY;
            var sourceGeometry = new Scan2DGridGeometry(firstOffset + .5, sourceFirstY, 1, sourceStepY);
            var destinationGeometry = new Scan2DGridGeometry(
                (double)(request.SpatialViewport.MinimumOffset + request.SpatialViewport.Span /
                    (2m * request.PixelWidth)), visibleSampleMinimum + destinationStepY / 2,
                (double)(request.SpatialViewport.Span / request.PixelWidth), destinationStepY);
            var source = new Scan2DSourceGrid(sourceWidth, sourceHeight, sourceWidth, sourceGeometry,
                sourceIntensity, sourceMask, sourceSegments, sourceProvenance);
            var renderRequest = new Scan2DRenderRequest(sourceGeometry, sourceWidth, sourceHeight,
                destinationGeometry, request.PixelWidth, request.PixelHeight,
                new(0, 0, request.PixelWidth, request.PixelHeight), request.EffectiveResampling);
            using Scan2DRenderResult result = _resampler.Render(source, renderRequest,
                new BorrowedByteOwner(destinationStorage), token);
        }
        finally
        {
            if (sourceSegmentsOwner is not null) sourceSegmentsOwner.Dispose();
            if (sourceSegmentsAdmitted) ReleaseScratchBytes(request.ConsumerId, sourceSegmentsBytes);
            if (sourceMaskOwner is not null) sourceMaskOwner.Dispose();
            if (sourceMaskAdmitted) ReleaseScratchBytes(request.ConsumerId, sourceMaskBytes);
            if (sourceIntensityOwner is not null) sourceIntensityOwner.Dispose();
            if (sourceIntensityAdmitted) ReleaseScratchBytes(request.ConsumerId, sourceIntensityBytes);
        }
    }

    private IMemoryOwner<byte> RentScratchOwner(string consumerId, int requestedBytes, out long actualBytes)
    {
        int reservationBytes = _pool.GetMaximumRentalLength(requestedBytes);
        if (reservationBytes < requestedBytes)
        { throw new InvalidOperationException("Raster pool returned an invalid reservation quote."); }
        if (!TryReserveScratchBytes(consumerId, reservationBytes))
        { throw new InvalidOperationException("The B-Scan visual scratch quota is exhausted."); }
        IMemoryOwner<byte> owner;
        try { owner = _pool.Rent(requestedBytes, BScanVisualBufferKind.Scratch); }
        catch { ReleaseScratchBytes(consumerId, reservationBytes); throw; }
        actualBytes = owner.Memory.Length;
        if (actualBytes < requestedBytes || actualBytes > reservationBytes)
        {
            owner.Dispose();
            ReleaseScratchBytes(consumerId, reservationBytes);
            throw new InvalidOperationException("Raster pool violated its scratch reservation quote.");
        }
        if (TryAdjustScratchReservation(consumerId, reservationBytes, actualBytes)) { return owner; }
        owner.Dispose();
        ReleaseScratchBytes(consumerId, reservationBytes);
        throw new InvalidOperationException("The B-Scan visual scratch quota is exhausted.");
    }

    private bool TryReserveScratchBytes(string consumerId, long requestedBytes)
    {
        lock (_gate)
        {
            long budget = checked(_ingress.Options.VisualMemoryBudgetBytes - _ingress.VisualBytesReserved);
            long consumerCeiling = _ingress.Options.VisualMemoryBudgetBytes /
                Math.Max(1, _consumers.Count);
            long consumerBytes = _consumerOwnerBytes.GetValueOrDefault(consumerId);
            if (requestedBytes == 0 || !_consumers.Contains(consumerId) ||
                checked(_activeOwnerBytes + requestedBytes) > budget ||
                checked(consumerBytes + requestedBytes) > consumerCeiling)
            { return false; }
            _activeOwnerBytes += requestedBytes;
            _consumerOwnerBytes[consumerId] = checked(consumerBytes + requestedBytes);
            _maximumOwnerBytes = Math.Max(_maximumOwnerBytes, _activeOwnerBytes);
            return true;
        }
    }

    private bool TryAdjustScratchReservation(string consumerId, long reservedBytes, long actualBytes)
    {
        long delta = checked(actualBytes - reservedBytes);
        if (delta == 0) { return true; }
        lock (_gate)
        {
            long budget = checked(_ingress.Options.VisualMemoryBudgetBytes - _ingress.VisualBytesReserved);
            long consumerCeiling = _ingress.Options.VisualMemoryBudgetBytes /
                Math.Max(1, _consumers.Count);
            long consumerBytes = _consumerOwnerBytes.GetValueOrDefault(consumerId);
            if (delta > 0 && (checked(_activeOwnerBytes + delta) > budget ||
                checked(consumerBytes + delta) > consumerCeiling)) { return false; }
            _activeOwnerBytes += delta;
            _consumerOwnerBytes[consumerId] = checked(consumerBytes + delta);
            _maximumOwnerBytes = Math.Max(_maximumOwnerBytes, _activeOwnerBytes);
            return true;
        }
    }

    private void ReleaseScratchBytes(string consumerId, long bytes)
    {
        lock (_gate)
        {
            _activeOwnerBytes -= bytes;
            long consumerBytes = checked(_consumerOwnerBytes.GetValueOrDefault(consumerId) - bytes);
            if (consumerBytes < 0)
            { throw new InvalidOperationException("The B-Scan consumer scratch ledger is inconsistent."); }
            _consumerOwnerBytes[consumerId] = consumerBytes;
        }
    }

    private void ChangeTableBytes(long delta)
    {
        lock (_gate)
        {
            if (delta > 0)
            {
                long budget = checked(_ingress.Options.VisualMemoryBudgetBytes - _ingress.VisualBytesReserved);
                if (checked(_activeOwnerBytes + delta) > budget)
                { throw new InvalidOperationException("The B-Scan visual table quota is exhausted."); }
                _activeOwnerBytes += delta;
                _maximumOwnerBytes = Math.Max(_maximumOwnerBytes, _activeOwnerBytes);
            }
            else { _activeOwnerBytes += delta; }
        }
    }

    private static void CopyBlock(ReadOnlySpan<byte> source, Span<byte> destination, int blockY,
        int width, int planeOffset)
    {
        int destinationOffset = checked(planeOffset + blockY * width);
        source.CopyTo(destination.Slice(destinationOffset, source.Length));
    }

    private static decimal RelativeOffset(ulong origin, ulong acquisitionIndex) =>
        acquisitionIndex >= origin ? acquisitionIndex - origin : -(decimal)(origin - acquisitionIndex);

    private static ulong? TryAddOffset(ulong origin, decimal offset)
    {
        decimal integral = decimal.Truncate(offset);
        if (integral != offset) { return null; }
        if (integral >= 0)
        {
            decimal remaining = ulong.MaxValue - origin;
            return integral <= remaining ? origin + (ulong)integral : null;
        }
        decimal magnitude = -integral;
        return magnitude <= origin ? origin - (ulong)magnitude : null;
    }

    private sealed class BorrowedByteOwner(Memory<byte> memory) : IMemoryOwner<byte>
    {
        public Memory<byte> Memory { get; } = memory;
        public void Dispose() { }
    }

    private static void SelectRange(BScanTraceStore.BScanTraceLease lease, int bucketSamples,
        int first, int lastExclusive, ref short selected, ref int selectedMagnitude, ref bool found)
    {
        if (first >= lastExclusive) { return; }
        int firstFullBucket = checked((first + bucketSamples - 1) / bucketSamples);
        int lastFullBucketExclusive = lastExclusive / bucketSamples;
        ReadOnlySpan<short> samples = lease.Samples.Span;
        int prefixEnd = Math.Min(lastExclusive, firstFullBucket * bucketSamples);
        for (int sample = first; sample < prefixEnd; sample++)
        { Consider(samples[sample], ref selected, ref selectedMagnitude, ref found); }
        ReadOnlySpan<BScanIndexedMinMax> index = lease.Index.Span;
        for (int bucket = firstFullBucket; bucket < lastFullBucketExclusive; bucket++)
        {
            BScanIndexedMinMax extrema = index[bucket];
            if (extrema.MinimumOffset <= extrema.MaximumOffset)
            {
                Consider(extrema.Minimum, ref selected, ref selectedMagnitude, ref found);
                Consider(extrema.Maximum, ref selected, ref selectedMagnitude, ref found);
            }
            else
            {
                Consider(extrema.Maximum, ref selected, ref selectedMagnitude, ref found);
                Consider(extrema.Minimum, ref selected, ref selectedMagnitude, ref found);
            }
        }
        int suffixStart = Math.Max(prefixEnd, lastFullBucketExclusive * bucketSamples);
        for (int sample = suffixStart; sample < lastExclusive; sample++)
        { Consider(samples[sample], ref selected, ref selectedMagnitude, ref found); }
    }

    private static void Consider(short value, ref short selected, ref int selectedMagnitude, ref bool found)
    {
        int magnitude = Math.Abs((int)value);
        if (magnitude <= selectedMagnitude) { return; }
        selectedMagnitude = magnitude;
        selected = value;
        found = true;
    }

    private static (int X, int Width) RasterColumns(BScanRenderRequest request, ulong first, ulong last, bool full)
    {
        if (full) { return (0, request.PixelWidth); }
        decimal a = BScanSpatialViewportOperations.RelativeOffset(first, request.SpatialViewport.OriginAcquisitionIndex);
        decimal b = BScanSpatialViewportOperations.RelativeOffset(last, request.SpatialViewport.OriginAcquisitionIndex) + 1;
        int x0 = Math.Clamp((int)Math.Floor((a - request.SpatialViewport.MinimumOffset) /
            request.SpatialViewport.Span * request.PixelWidth), 0, request.PixelWidth - 1);
        int x1 = Math.Clamp((int)Math.Ceiling((b - request.SpatialViewport.MinimumOffset) /
            request.SpatialViewport.Span * request.PixelWidth), x0 + 1, request.PixelWidth);
        return (x0, x1 - x0);
    }

    private BScanFullRefreshReason GetFullRefreshReason(BScanRenderRequest request,
        ConventionalUtFrameMetadata metadata, BScanStoreVisualState state,
        BScanTraceStore.BScanTraceLease?[] leases, int count, int horizontalPixelShift = 0)
    {
        bool coalesced;
        bool recovery;
        BScanVisualSnapshot? prior;
        lock (_gate)
        {
            coalesced = _coalesced;
            recovery = _recoveryConsumers.Contains(request.ConsumerId);
            _currentByConsumer.TryGetValue(request.ConsumerId, out prior);
        }
        if (request.DisplayedSnapshotVersion is null)
        {
            BScanFullRefreshReason initial = BScanFullRefreshReason.Initial;
            if (recovery) { initial |= BScanFullRefreshReason.Recovery; }
            return initial;
        }
        BScanFullRefreshReason reason = BScanFullRefreshReason.None;
        if (request.EffectiveResampling.Mode != Scan2DInterpolationMode.None)
        { reason |= BScanFullRefreshReason.RenderConfigurationChanged; }
        if (request.Displayed is null && prior is not null)
        {
            if (prior.RunId != request.RunId) { reason |= BScanFullRefreshReason.RunChanged; }
            if (request.DisplayedStoreRevision != prior.StoreRevision) { reason |= BScanFullRefreshReason.BaseRevisionMismatch; }
            if (prior.RasterWidth != request.PixelWidth || prior.RasterHeight != request.PixelHeight)
            { reason |= BScanFullRefreshReason.ResolutionChanged; }
            if ((prior.SpatialViewport != request.SpatialViewport && horizontalPixelShift == 0) ||
                prior.TimeViewport != request.TimeViewport)
            { reason |= BScanFullRefreshReason.ViewportChanged; }
            if (prior.RenderConfigurationVersion != request.RenderConfigurationVersion)
            { reason |= BScanFullRefreshReason.RenderConfigurationChanged; }
            if (prior.Resampling != request.EffectiveResampling)
            { reason |= BScanFullRefreshReason.RenderConfigurationChanged; }
            if (horizontalPixelShift == 0 && state.ResidentRange is { } resident &&
                (prior.ResidentRange.First < resident.First || prior.ResidentRange.Last > resident.Last))
            { reason |= BScanFullRefreshReason.DirtyRangeEvicted; }
        }
        if (request.DisplayedWrapGeneration != state.WrapGeneration && horizontalPixelShift == 0)
        { reason |= BScanFullRefreshReason.WrapChanged; }
        if (request.Displayed is { } displayed)
        {
            var targetStream = new BScanStreamIdentity(metadata.SourceId, metadata.RunId,
                metadata.Configuration.PhysicalChannelId);
            if (displayed.Stream != targetStream) { reason |= BScanFullRefreshReason.RunChanged; }
            if (displayed.Render.PixelWidth != request.PixelWidth || displayed.Render.PixelHeight != request.PixelHeight)
            { reason |= BScanFullRefreshReason.ResolutionChanged; }
            if ((displayed.Render.SpatialViewport != request.SpatialViewport && horizontalPixelShift == 0) ||
                displayed.Render.TimeViewport != request.TimeViewport)
            { reason |= BScanFullRefreshReason.ViewportChanged; }
            if (displayed.Render.RenderConfigurationVersion != request.RenderConfigurationVersion)
            { reason |= BScanFullRefreshReason.RenderConfigurationChanged; }
            if ((displayed.Render.Resampling == default ? Scan2DResamplingOptions.Default : displayed.Render.Resampling) != request.EffectiveResampling)
            { reason |= BScanFullRefreshReason.RenderConfigurationChanged; }
        }
        ulong baseRevision = request.DisplayedStoreRevision.GetValueOrDefault();
        if (baseRevision > state.StoreRevision) { reason |= BScanFullRefreshReason.BaseRevisionMismatch; }
        if (reason == BScanFullRefreshReason.None && baseRevision < state.StoreRevision)
        {
            ulong expected = baseRevision + 1;
            for (int index = 0; index < count && expected <= state.StoreRevision; index++)
            {
                ulong revision = leases[index]!.VisualRevision;
                if (revision < expected) { continue; }
                if (revision != expected) { reason |= BScanFullRefreshReason.DirtyRangeEvicted; break; }
                expected++;
            }
            if (expected <= state.StoreRevision) { reason |= BScanFullRefreshReason.DirtyRangeEvicted; }
        }
        if (recovery) { reason |= BScanFullRefreshReason.Recovery; }
        return reason;
    }

    private static int GetHorizontalPixelShift(BScanRenderRequest request)
    {
        if (request.EffectiveResampling.Mode != Scan2DInterpolationMode.None ||
            !request.FollowLiveWindow || request.Displayed is not { } displayed ||
            displayed.Render.PixelWidth != request.PixelWidth ||
            displayed.Render.PixelHeight != request.PixelHeight ||
            displayed.Render.TimeViewport != request.TimeViewport ||
            displayed.Render.RenderConfigurationVersion != request.RenderConfigurationVersion ||
            (displayed.Render.Resampling == default ? Scan2DResamplingOptions.Default : displayed.Render.Resampling) != request.EffectiveResampling ||
            displayed.Render.SpatialViewport.Span != request.SpatialViewport.Span)
        { return 0; }
        BScanSpatialViewport previous = displayed.Render.SpatialViewport;
        decimal previousMinimum = previous.OriginAcquisitionIndex + previous.MinimumOffset;
        decimal currentMinimum = request.SpatialViewport.OriginAcquisitionIndex +
            request.SpatialViewport.MinimumOffset;
        decimal traceShift = currentMinimum - previousMinimum;
        if (traceShift <= 0 || traceShift >= request.SpatialViewport.Span) { return 0; }
        decimal pixelShift = traceShift * request.PixelWidth / request.SpatialViewport.Span;
        if (pixelShift != decimal.Truncate(pixelShift)) { return 0; }
        return checked((int)pixelShift);
    }

    private async Task PublishLoopAsync()
    {
        try
        {
            while (true)
            {
                await _wake.WaitAsync(_shutdown.Token).ConfigureAwait(false);
                TimeSpan remaining;
                lock (_gate)
                {
                    TimeSpan interval = _pending.Count == 0
                        ? MinimumPublicationInterval
                        : _pending.Values.Max(PublicationInterval);
                    remaining = _lastPublication is { } last
                        ? interval - _clock.GetElapsedTime(last, _clock.GetTimestamp()) : TimeSpan.Zero;
                }
                if (remaining > TimeSpan.Zero) { await DelayAsync(remaining, _shutdown.Token).ConfigureAwait(false); }
                (string ConsumerId, BScanRenderRequest Request, long Generation)[] batch;
                lock (_gate)
                {
                    batch =
                    [
                        .. _pending.Select(item => (item.Key, item.Value,
                            _pendingGenerations.GetValueOrDefault(item.Key))),
                    ];
                    _pending.Clear();
                }
                foreach ((_, BScanRenderRequest request, long generation) in batch)
                { PublishMetadata(request, generation); }
            }
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { }
        catch (Exception error) { lock (_gate) { _lastError = Message(error); } }
    }

    internal static TimeSpan PublicationInterval(BScanRenderRequest request) =>
        request.FollowLiveWindow && request.SpatialViewport.Span > 512
            ? WideLivePublicationInterval
            : MinimumPublicationInterval;

    private void PublishMetadata(BScanRenderRequest request, long requestGeneration)
    {
        BScanVisualLeaseBatch? batch = null;
        Exception? primary = null;
        try
        {
            batch = _ingress.CaptureVisualLeases(_shutdown.Token,
                out BScanIngressVisualState? ingressState, out BScanStoreVisualState state);
            BScanTraceStore.BScanTraceLease?[] leases = batch?.Items ?? [];
            int count = batch?.Count ?? 0;
            ConventionalUtFrameMetadata? metadata = ingressState?.Metadata;
            if (metadata is null || metadata.RunId != request.RunId || count == 0 || state.ResidentRange is null) { return; }
            Array.Sort(leases, 0, count, BScanLeaseComparer.Instance);
            request = WithAcknowledgedDisplay(request);
            request = ReconcileLiveWindow(request, state.ResidentRange.Value);
            int horizontalPixelShift = GetHorizontalPixelShift(request);
            BScanFullRefreshReason reason = GetFullRefreshReason(request, metadata, state, leases, count,
                horizontalPixelShift);
            bool full = reason != BScanFullRefreshReason.None;
            ulong first = ulong.MaxValue, last = 0;
            for (int i = 0; i < count; i++)
            {
                var lease = leases[i]!;
                if (full || lease.VisualRevision > request.DisplayedStoreRevision.GetValueOrDefault())
                { first = Math.Min(first, lease.Descriptor.AcquisitionIndex); last = Math.Max(last, lease.Descriptor.AcquisitionIndex); }
            }
            if (first == ulong.MaxValue) { return; }
            ulong version;
            lock (_gate) { version = ++_snapshotVersion; }
            var dirty = new BScanDirtyRange(first, last, request.DisplayedStoreRevision.GetValueOrDefault(), state.StoreRevision);
            var snapshot = CreateSnapshot(metadata, request, state, version, dirty,
                new(0, 0, request.PixelWidth, request.PixelHeight), full, reason, leases, count) with
            { HorizontalPixelShift = horizontalPixelShift };
            BeforeMetadataCandidateCommit?.Invoke(snapshot);
            BeforeMetadataCandidateValidation?.Invoke();
            Audit(BScanVisualAuditEventKind.CommitAttempted, request.ConsumerId, ingressState,
                requestGeneration, request);
            Subscription[] observers;
            BScanVisualCallbackAuditContext? callbackAudit = null;
            lock (_gate)
            {
                if (_disposed)
                {
                    Audit(BScanVisualAuditEventKind.CandidateRejectedClosed, request.ConsumerId,
                        ingressState, requestGeneration, request, BScanVisualAuditResult.Closed,
                        BScanVisualAuditRejection.Closed);
                    return;
                }
                bool ingressCurrent = ingressState is not null &&
                    ReferenceEquals(ingressState, _ingress.CurrentVisualState);
                bool storeCurrent = ingressState is not null &&
                    ReferenceEquals(ingressState.StoreState, ingressState.Store.CurrentVisualState);
                long currentGeneration = _pendingGenerations.GetValueOrDefault(request.ConsumerId);
                if (currentGeneration != requestGeneration)
                {
                    _lastMetadataCandidateAudit = new(request.RunId, requestGeneration,
                        currentGeneration, ingressCurrent, storeCurrent, false,
                        _lastGenerationReason, request);
                    Audit(BScanVisualAuditEventKind.CandidateRejectedStale, request.ConsumerId,
                        ingressState, requestGeneration, request, BScanVisualAuditResult.Stale,
                        BScanVisualAuditRejection.Generation);
                    _staleCandidates++;
                    RequestRecoveryUnderLock(request.ConsumerId, requestGeneration, request);
                    long latestGeneration = _pendingGenerations.GetValueOrDefault(request.ConsumerId);
                    if (_committedGenerations.GetValueOrDefault(request.ConsumerId) != latestGeneration &&
                        !_pending.ContainsKey(request.ConsumerId) &&
                        _lastRequests.TryGetValue(request.ConsumerId, out BScanRenderRequest? latest) &&
                        _ingress.CurrentVisualState is { IsOpen: true } current &&
                        latest.RunId == current.Metadata.RunId)
                    { _pending[request.ConsumerId] = latest; }
                    if (_pending.ContainsKey(request.ConsumerId) && _wake.CurrentCount == 0)
                    { _wake.Release(); }
                    return;
                }
                _lastPublication = _clock.GetTimestamp();
                _lastMetadataCandidateAudit = new(request.RunId, requestGeneration,
                    requestGeneration, true, true, true, _lastGenerationReason, request);
                _currentByConsumer[request.ConsumerId] = snapshot;
                _committedGenerations[request.ConsumerId] = requestGeneration;
                if (_bootstrapStates.TryGetValue(request.ConsumerId, out BScanConsumerBootstrapState? bootstrap) &&
                    bootstrap.RunId == request.RunId)
                {
                    _bootstrapConsumers.Remove(request.ConsumerId);
                    _bootstrapStates.Remove(request.ConsumerId);
                }
                _updatesPublished++;
                if (full) { _fullRefreshes++; } else { _incrementalRefreshes++; }
                _coalesced = false;
                _recoveryConsumers.Remove(request.ConsumerId);
                observers = [.. _subscriptions];
                callbackAudit = CaptureCallbackAuditContext(request.ConsumerId, ingressState,
                    requestGeneration, request);
                Audit(BScanVisualAuditEventKind.CandidateCommitted, request.ConsumerId,
                    ingressState, requestGeneration, request, BScanVisualAuditResult.Committed);
            }
            var update = new BScanVisualUpdate(snapshot);
            foreach (Subscription subscription in observers)
            { subscription.Offer(update, callbackAudit); }
        }
        catch (Exception error) { primary = error; throw; }
        finally { batch?.Dispose(primary); }
    }

    private static BScanVisualSnapshot CreateSnapshot(ConventionalUtFrameMetadata metadata, BScanRenderRequest request,
        BScanStoreVisualState state, ulong version, BScanDirtyRange dirty, BScanRasterRectangle region,
        bool full, BScanFullRefreshReason reason, BScanTraceStore.BScanTraceLease?[] leases, int count)
    {
        double domainMinimum = metadata.Configuration.FirstSampleOffsetSeconds;
        double domainMaximum = domainMinimum + metadata.Configuration.SampleCount / metadata.Configuration.SampleRateHz;
        bool discontinuities = false;
        for (int i = 1; i < count; i++)
        { if (leases[i]!.Descriptor.AcquisitionIndex != leases[i - 1]!.Descriptor.AcquisitionIndex + 1) { discontinuities = true; break; } }
        return new(metadata.RunId, version, dirty.BaseStoreRevision, state.StoreRevision, state.WrapGeneration,
            new(dirty.FirstAcquisitionIndex, dirty.LastAcquisitionIndex), state.ResidentRange!.Value,
            ScanTimeViewportOperations.Create(domainMinimum, domainMaximum), request.SpatialViewport,
            request.TimeViewport, request.PixelWidth, request.PixelHeight, request.RenderConfigurationVersion,
            dirty, region, discontinuities, full, reason)
        {
            ConsumerId = request.ConsumerId,
            Stream = new(metadata.SourceId, metadata.RunId, metadata.Configuration.PhysicalChannelId),
            Resampling = request.EffectiveResampling
        };
    }

    private void OnVisualStateChanged()
    {
        AcquisitionRunId? currentRun = _ingress.CurrentVisualState is { IsOpen: true } state
            ? state.Metadata.RunId : null;
        string[] bootstrap;
        lock (_gate)
        {
            if (_disposed) { return; }
            _columnsIndexed++;
            _latestVisualRunId = currentRun;
            foreach ((string consumerId, BScanRenderRequest request) in _lastRequests)
            {
                if (currentRun is { } run && request.RunId != run)
                {
                    _pending.Remove(consumerId);
                    _pendingGenerations[consumerId] = ++_requestGeneration;
                    _lastGenerationReason = BScanRequestGenerationReason.RunChanged;
                    _currentByConsumer.Remove(consumerId);
                    _displayedByConsumer.Remove(consumerId);
                    RequestRecoveryUnderLock(consumerId,
                        _pendingGenerations.GetValueOrDefault(consumerId), request);
                    _bootstrapConsumers.Add(consumerId);
                    _bootstrapStates[consumerId] = new(run, BScanBootstrapPhase.Requested, 0, null);
                    continue;
                }
                if (_pending.ContainsKey(consumerId)) { _coalesced = true; _updatesCoalesced++; }
                _pending[consumerId] = request;
                _lastGenerationReason = BScanRequestGenerationReason.VisualStateChanged;
            }
            bootstrap =
            [
                .. _consumers.Where(consumer =>
                    _bootstrapConsumers.Contains(consumer) ||
                    currentRun is { } run &&
                    _lastRequests.TryGetValue(consumer, out BScanRenderRequest? request) &&
                    request.RunId != run),
            ];
            if (_pending.Count != 0 && _wake.CurrentCount == 0) { _wake.Release(); }
        }
        foreach (string consumer in bootstrap) { QueueBootstrap(consumer); }
    }

    private async Task DelayAsync(TimeSpan due, CancellationToken token)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using ITimer timer = _clock.CreateTimer(_ => completion.TrySetResult(), null, due, Timeout.InfiniteTimeSpan);
        await completion.Task.WaitAsync(token).ConfigureAwait(false);
    }

    private bool TryReserveRasterOwner(string consumerId, long requestedBytes)
    {
        lock (_gate)
        {
            long budget = checked(_ingress.Options.VisualMemoryBudgetBytes - _ingress.VisualBytesReserved);
            long consumerCeiling = _ingress.Options.VisualMemoryBudgetBytes /
                Math.Max(1, _consumers.Count);
            long consumerBytes = _consumerOwnerBytes.GetValueOrDefault(consumerId);
            if (!_consumers.Contains(consumerId) ||
                _consumerOwners.GetValueOrDefault(consumerId) >=
                    _ingress.Options.MaximumRasterOwnersPerConsumer ||
                checked(_activeOwnerBytes + requestedBytes) > budget ||
                checked(consumerBytes + requestedBytes) > consumerCeiling)
            { return false; }
            _activeOwnerBytes += requestedBytes;
            _activeOwners++;
            _consumerOwners[consumerId] = _consumerOwners.GetValueOrDefault(consumerId) + 1;
            _consumerOwnerBytes[consumerId] = consumerBytes + requestedBytes;
            _maximumOwners = Math.Max(_maximumOwners, _activeOwners);
            _maximumOwnerBytes = Math.Max(_maximumOwnerBytes, _activeOwnerBytes);
            return true;
        }
    }

    private bool TryAdjustRasterReservation(string consumerId, long reservedBytes, long actualBytes)
    {
        long delta = checked(actualBytes - reservedBytes);
        if (delta == 0) { return true; }
        lock (_gate)
        {
            long budget = checked(_ingress.Options.VisualMemoryBudgetBytes - _ingress.VisualBytesReserved);
            long consumerCeiling = _ingress.Options.VisualMemoryBudgetBytes /
                Math.Max(1, _consumers.Count);
            long consumerBytes = _consumerOwnerBytes.GetValueOrDefault(consumerId);
            if (delta > 0 && (checked(_activeOwnerBytes + delta) > budget ||
                checked(consumerBytes + delta) > consumerCeiling))
            { return false; }
            _activeOwnerBytes += delta;
            _consumerOwnerBytes[consumerId] = consumerBytes + delta;
            _maximumOwnerBytes = Math.Max(_maximumOwnerBytes, _activeOwnerBytes);
            return true;
        }
    }

    private void ReleaseOwner(string consumerId, long bytes)
    {
        DiscardOwner(consumerId, bytes);
    }

    private void DiscardOwner(string consumerId, long bytes)
    {
        TaskCompletionSource? drain = null;
        lock (_gate)
        {
            _activeOwnerBytes -= bytes;
            _activeOwners--;
            _consumerOwnerBytes[consumerId] = _consumerOwnerBytes.GetValueOrDefault(consumerId) - bytes;
            int remaining = _consumerOwners.GetValueOrDefault(consumerId) - 1;
            if (remaining <= 0)
            {
                _consumerOwners[consumerId] = 0;
                if (_retiringConsumers.Remove(consumerId))
                { _consumerOwners.Remove(consumerId); _consumerOwnerBytes.Remove(consumerId); _consumers.Remove(consumerId); }
            }
            else { _consumerOwners[consumerId] = remaining; }
            if (_activeOwners == 0) { drain = _ownerDrain; }
        }
        drain?.TrySetResult();
    }

    private void EndRegionOperation()
    {
        TaskCompletionSource? drain = null;
        lock (_gate) { if (--_regionOperations == 0) { drain = _regionDrain; } }
        drain?.TrySetResult();
    }

    public ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            _disposal ??= DisposeCoreAsync();
            return new(_disposal);
        }
    }

    internal void Stop()
    {
        lock (_gate)
        {
            if (_disposed) { return; }
            _disposed = true;
            _ingress.VisualStateChanged -= OnVisualStateChanged;
            _pending.Clear();
            _lastRequests.Clear();
            _currentByConsumer.Clear();
            _bootstrapConsumers.Clear();
            _bootstrapStates.Clear();
            _recoveryConsumers.Clear();
            _pendingGenerations.Clear();
            _committedGenerations.Clear();
            _displayedByConsumer.Clear();
            Subscription[] subscriptions = [.. _subscriptions];
            _subscriptions.Clear();
            Task regions = _regionOperations == 0 ? Task.CompletedTask :
                (_regionDrain ??= new(TaskCreationOptions.RunContinuationsAsynchronously)).Task;
            Task owners = _activeOwners == 0 ? Task.CompletedTask :
                (_ownerDrain ??= new(TaskCreationOptions.RunContinuationsAsynchronously)).Task;
            _drain = DrainCoreAsync(subscriptions, regions, owners);
        }
    }

    internal Task WaitForDrainAsync(CancellationToken cancellationToken = default)
    {
        Stop();
        Task drain;
        lock (_gate) { drain = _drain!; }
        return cancellationToken.CanBeCanceled ? drain.WaitAsync(cancellationToken) : drain;
    }

    private async Task DrainCoreAsync(Subscription[] subscriptions, Task regions, Task owners)
    {
        List<Exception> failures = [];
        try { await _shutdown.CancelAsync().ConfigureAwait(false); }
        catch (Exception error) { failures.Add(error); }
        foreach (Task callback in subscriptions.Select(subscription => subscription.Detach()))
        {
            try { await callback.ConfigureAwait(false); }
            catch (Exception error) { failures.Add(error); }
        }
        try { await _worker.ConfigureAwait(false); }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { }
        catch (Exception error) { failures.Add(error); }
        try { await regions.ConfigureAwait(false); }
        catch (Exception error) { failures.Add(error); }
        try { await owners.ConfigureAwait(false); }
        catch (Exception error) { failures.Add(error); }
        try { _resampler.Dispose(); }
        catch (Exception error) { failures.Add(error); }
        if (failures.Count != 0) { throw new AggregateException("B-Scan visual drain failed.", failures); }
    }

    private async Task DisposeCoreAsync()
    {
        Stop();
        await WaitForDrainAsync().ConfigureAwait(false);
        _shutdown.Dispose();
        _wake.Dispose();
        lock (_gate) { _closed = true; }
    }

    private void RecordBuildDuration(TimeSpan duration)
    {
        lock (_gate)
        {
            long ticks = Math.Max(0, duration.Ticks);
            _buildDurations[_buildDurationCount++ % DurationWindow] = ticks;
            _buildDurationMaximum = Math.Max(_buildDurationMaximum, ticks);
        }
    }

    private BScanVisualBuildDurationStatistics BuildDurationsUnderLock()
    {
        int count = (int)Math.Min(_buildDurationCount, DurationWindow);
        if (count == 0) { return new(0, TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero); }
        long[] values = _buildDurations[..count].ToArray();
        Array.Sort(values);
        static long Percentile(long[] source, int percentile) =>
            source[Math.Max(0, (int)Math.Ceiling(source.Length * percentile / 100d) - 1)];
        return new(_buildDurationCount, TimeSpan.FromTicks(Percentile(values, 50)),
            TimeSpan.FromTicks(Percentile(values, 95)), TimeSpan.FromTicks(Percentile(values, 99)),
            TimeSpan.FromTicks(_buildDurationMaximum));
    }

    private static string Message(Exception error) => string.IsNullOrWhiteSpace(error.Message) ? error.GetType().Name : error.Message;

    private void Audit(BScanVisualAuditEventKind kind, string consumerId,
        BScanIngressVisualState? ingressState, long requestGeneration, BScanRenderRequest? request,
        BScanVisualAuditResult result = BScanVisualAuditResult.None,
        BScanVisualAuditRejection rejection = BScanVisualAuditRejection.None) =>
        Volatile.Read(ref _audit)?.Record(kind, consumerId, ingressState, requestGeneration,
            _presentationEpochs.GetValueOrDefault(consumerId), request, result, rejection);

    private BScanVisualCallbackAuditContext? CaptureCallbackAuditContext(string consumerId,
        BScanIngressVisualState? ingressState, long requestGeneration, BScanRenderRequest? request)
    {
        if (Volatile.Read(ref _audit) is null) { return null; }
        return new(ingressState, requestGeneration,
            _presentationEpochs.GetValueOrDefault(consumerId), request);
    }

    private void Audit(BScanVisualAuditEventKind kind, string consumerId,
        BScanVisualCallbackAuditContext? context,
        BScanVisualAuditResult result = BScanVisualAuditResult.None)
    {
        BScanVisualAudit? audit = Volatile.Read(ref _audit);
        if (audit is null || context is not { } captured) { return; }
        audit.Record(kind, consumerId, captured.IngressState, captured.RequestGeneration,
            captured.PresentationEpoch, captured.Request, result);
    }

    private void RequestRecoveryUnderLock(string consumerId, long requestGeneration,
        BScanRenderRequest? request)
    {
        if (!_recoveryConsumers.Add(consumerId)) { return; }
        Audit(BScanVisualAuditEventKind.RecoveryRequested, consumerId,
            _ingress.CurrentVisualState, requestGeneration, request, BScanVisualAuditResult.Accepted);
    }

    private sealed class BScanLeaseComparer : IComparer<BScanTraceStore.BScanTraceLease?>
    {
        internal static BScanLeaseComparer Instance { get; } = new();
        public int Compare(BScanTraceStore.BScanTraceLease? x, BScanTraceStore.BScanTraceLease? y) =>
            x!.Descriptor.AcquisitionIndex.CompareTo(y!.Descriptor.AcquisitionIndex);
    }

    private sealed class Subscription(BScanVisualDelivery owner, IObserver<BScanVisualUpdate> observer) : IDisposable
    {
        private readonly object _gate = new();
        private BScanPendingCallback? _pending;
        private Task _pump = Task.CompletedTask;
        private bool _detached;
        internal void Offer(BScanVisualUpdate update, BScanVisualCallbackAuditContext? audit)
        {
            lock (_gate)
            {
                if (_detached) { return; }
                _pending = new(update, audit);
                owner.Audit(BScanVisualAuditEventKind.CallbackQueued,
                    update.Snapshot.ConsumerId, audit);
                if (_pump.IsCompleted) { _pump = Task.Run(Pump); }
            }
        }
        private void Pump()
        {
            while (true)
            {
                BScanPendingCallback pending;
                lock (_gate)
                {
                    if (_detached || _pending is not { } available) { return; }
                    pending = available;
                    _pending = null;
                }
                try
                {
                    lock (owner._gate) { owner._observerCallbacksStarted++; }
                    owner.Audit(BScanVisualAuditEventKind.CallbackStarted,
                        pending.Update.Snapshot.ConsumerId, pending.Audit);
                    observer.OnNext(pending.Update);
                    lock (owner._gate) { owner._observerCallbacksCompleted++; }
                    owner.Audit(BScanVisualAuditEventKind.CallbackCompleted,
                        pending.Update.Snapshot.ConsumerId, pending.Audit,
                        BScanVisualAuditResult.Committed);
                }
                catch (Exception error)
                {
                    Detach();
                    lock (owner._gate)
                    {
                        owner._observerCallbacksCompleted++;
                        owner._observerErrors++;
                        owner._lastError = Message(error);
                        owner._subscriptions.Remove(this);
                    }
                    return;
                }
            }
        }
        internal Task Detach()
        {
            lock (_gate) { _detached = true; _pending = null; return _pump; }
        }
        public void Dispose()
        {
            Task pump = Detach();
            lock (owner._gate) { owner._subscriptions.Remove(this); }
            if (Task.CurrentId != pump.Id) { pump.GetAwaiter().GetResult(); }
        }
    }

    private readonly record struct BScanPendingCallback(
        BScanVisualUpdate Update,
        BScanVisualCallbackAuditContext? Audit);
}

internal readonly record struct BScanVisualCallbackAuditContext(
    BScanIngressVisualState? IngressState,
    long RequestGeneration,
    long PresentationEpoch,
    BScanRenderRequest? Request);

internal sealed record BScanMetadataCandidateAudit(
    AcquisitionRunId RunId,
    long CandidateGeneration,
    long CurrentGeneration,
    bool IngressCurrent,
    bool StoreCurrent,
    bool Committed,
    BScanRequestGenerationReason LastGenerationReason,
    BScanRenderRequest CandidateRequest);

internal enum BScanRequestGenerationReason
{
    None,
    RequestRender,
    AcknowledgeDisplayed,
    RunChanged,
    VisualStateChanged
}

internal enum BScanBootstrapPhase { Requested, Pending, Admitted }

internal sealed record BScanConsumerBootstrapState(
    AcquisitionRunId? RunId,
    BScanBootstrapPhase Phase,
    ulong StoreRevision,
    long? RequestGeneration);
// End of the neutral B-Scan delivery implementation.
