using System.Buffers;
using System.Numerics;
using UTStudio.Contracts.Inspection;
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
                _ingress.CurrentVisualState is { IsProjectable: true } current ? current.Metadata.RunId : null,
                BScanBootstrapPhase.Requested, 0, null);
        }
        Audit(BScanVisualAuditEventKind.BootstrapRequested, consumerId,
            _ingress.CurrentVisualState, 0, null);
        QueueBootstrap(consumerId);
    }

    private void QueueBootstrap(string consumerId)
    {
        BScanRenderRequest? previousRequest = null;
        try
        {
            BScanIngressVisualState? ingressState = _ingress.CurrentVisualState;
            if (ingressState is null || ingressState.StoreState.CommittedTraceCount == 0 ||
                ingressState.StoreState.ResidentRange is not { } resident) { return; }
            ConventionalUtFrameMetadata metadata = ingressState.Metadata;
            BScanStoreVisualState state = ingressState.StoreState;
            lock (_gate)
            {
                if (!_consumers.Contains(consumerId) ||
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
                FollowLiveWindow: previousRequest?.FollowLiveWindow ?? true,
                Resampling: previousRequest?.EffectiveResampling ?? Scan2DResamplingOptions.Default));
            AfterBootstrapRequestQueued?.Invoke(consumerId, metadata.RunId);
        }
        catch (Exception error) when (error is ObjectDisposedException or InvalidOperationException) { }
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
            if (_ingress.CurrentVisualState is { IsProjectable: true } visualState &&
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
                    ingressState, _committedGenerations.GetValueOrDefault(snapshot.ConsumerId), request), null);
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
        BScanProjectionOperation? projection = null;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, _shutdown.Token, _ingress.RunCancellationToken);
        try
        {
            linked.Token.ThrowIfCancellationRequested();
            if (!_ingress.TryBeginProjection(out BScanIngressVisualState? admittedState, out projection) ||
                admittedState is null) { return BScanRasterResult.NotAvailable(); }
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
            BScanRasterResult result = BuildRegionPaged(request, requestGeneration,
                consumerLifetime, admittedState, linked.Token);
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
            projection?.Dispose();
            RecordBuildDuration(_clock.GetElapsedTime(started, _clock.GetTimestamp()));
            EndRegionOperation();
        }
    }

    private BScanRasterResult BuildRegionPaged(BScanRenderRequest request, long requestGeneration,
        long consumerLifetime, BScanIngressVisualState admittedState, CancellationToken token)
    {
        BScanRenderRequest admittedRequest = request;
        BScanStoreVisualState state = admittedState.StoreState;
        ConventionalUtFrameMetadata metadata = admittedState.Metadata;
        if (metadata.RunId != request.RunId || state.ResidentRange is not { } resident)
        { return BScanRasterResult.NotAvailable(); }
        request = ReconcileLiveWindow(request, resident);
        BScanAcquisitionRange represented = RepresentedRange(request.SpatialViewport, resident);
        if (represented.Last - represented.First >= (ulong)_ingress.Options.MaximumColumns)
        {
            return BScanRasterResult.ResourceLimit(
                "The requested B-Scan viewport exceeds the configured visible-column capacity.");
        }
        var ticket = BScanProjectionTicket.Create(admittedState, request,
            consumerLifetime, requestGeneration);
        int horizontalPixelShift = GetHorizontalPixelShift(request);
        BScanFullRefreshReason reason = GetFullRefreshReason(request, metadata, state,
            horizontalPixelShift);
        bool full = reason != BScanFullRefreshReason.None;
        BScanAcquisitionRange dirtyRange;
        if (full) { dirtyRange = represented; }
        else if (!_ingress.TryGetDirtyRange(ticket.Stream, represented,
            request.DisplayedStoreRevision.GetValueOrDefault(), out dirtyRange))
        { return BScanRasterResult.NotAvailable(); }
        BScanAcquisitionRange traversalRange = represented;
        if (!full && request.EffectiveResampling.Mode == Scan2DInterpolationMode.Bilinear)
        {
            dirtyRange = ExpandRange(dirtyRange, represented, 1);
            traversalRange = ExpandRange(dirtyRange, represented, 1);
        }
        (int x, int width) = RasterColumns(request, dirtyRange.First, dirtyRange.Last, full);
        bool compatibleHorizontalShift = request.EffectiveResampling.Mode ==
            Scan2DInterpolationMode.Bilinear
            ? x + width == request.PixelWidth && width >= horizontalPixelShift
            : x == request.PixelWidth - horizontalPixelShift && width == horizontalPixelShift;
        if (horizontalPixelShift > 0 && (full || !compatibleHorizontalShift))
        {
            horizontalPixelShift = 0;
            reason |= BScanFullRefreshReason.Recovery;
            full = true;
            x = 0;
            width = request.PixelWidth;
            dirtyRange = represented;
        }

        int cells = checked(width * request.PixelHeight);
        int length = checked(cells * 3);
        int reservationLength = _pool.GetMaximumRentalLength(length);
        if (reservationLength < length)
        { throw new InvalidOperationException("Raster pool returned an invalid reservation quote."); }
        if (!TryReserveRasterOwner(request.ConsumerId, reservationLength))
        { return BScanRasterResult.ResourceLimit("The B-Scan visual memory quota is exhausted."); }
        IMemoryOwner<byte> owner;
        try { owner = _pool.Rent(length); }
        catch { DiscardOwner(request.ConsumerId, reservationLength); throw; }
        long actualBytes = owner.Memory.Length;
        if (actualBytes < length || actualBytes > reservationLength ||
            !TryAdjustRasterReservation(request.ConsumerId, reservationLength, actualBytes))
        {
            owner.Dispose();
            DiscardOwner(request.ConsumerId, reservationLength);
            return BScanRasterResult.ResourceLimit("The B-Scan visual memory quota is exhausted.");
        }

        IMemoryOwner<byte>? extremaOwner = null;
        long extremaBytes = 0;
        bool extremaAdmitted = false;
        var available = new Dictionary<ulong, TraceIdentity>();
        var selectable = new TraceIdentity?[request.PixelWidth];
        if (!full)
        {
            SeedIncrementalIdentityMap(request.ConsumerId, represented, dirtyRange, x, width,
                horizontalPixelShift, available, selectable);
        }
        ulong? previous = null;
        bool discontinuities = state.CommittedGapCount != 0;
        try
        {
            Memory<byte> storage = owner.Memory[..length];
            storage.Span.Clear();
            Memory<byte> pixelsMemory = storage[..cells];
            Memory<byte> validityMemory = storage.Slice(cells, cells);
            Memory<byte> provenanceMemory = storage.Slice(checked(cells * 2), cells);
            BScanPageTraversalResult traversal;
            if (request.EffectiveResampling.Mode == Scan2DInterpolationMode.None)
            {
                extremaOwner = RentScratchOwner(request.ConsumerId, checked(cells * sizeof(short)),
                    out extremaBytes);
                extremaAdmitted = true;
                Memory<byte> extremaMemory = extremaOwner.Memory[..checked(cells * sizeof(short))];
                extremaMemory.Span.Clear();
                double rate = metadata.Configuration.SampleRateHz;
                double offset = metadata.Configuration.FirstSampleOffsetSeconds;
                int[] sampleFirst = new int[request.PixelHeight];
                int[] sampleLast = new int[request.PixelHeight];
                for (int y = 0; y < request.PixelHeight; y++)
                {
                    double t0 = request.TimeViewport.VisibleMinimumSeconds +
                        request.TimeViewport.VisibleSpanSeconds * y / request.PixelHeight;
                    double t1 = request.TimeViewport.VisibleMinimumSeconds +
                        request.TimeViewport.VisibleSpanSeconds * (y + 1) / request.PixelHeight;
                    sampleFirst[y] = Math.Clamp((int)Math.Floor((t0 - offset) * rate),
                        0, metadata.Configuration.SampleCount);
                    sampleLast[y] = Math.Clamp((int)Math.Ceiling((t1 - offset) * rate),
                        0, metadata.Configuration.SampleCount);
                }

                traversal = _ingress.VisitVisualPages(ticket, represented, page =>
                {
                Span<byte> pixels = pixelsMemory.Span;
                Span<byte> validity = validityMemory.Span;
                Span<byte> provenance = provenanceMemory.Span;
                Span<short> extrema = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, short>(
                    extremaMemory.Span);
                foreach (InspectionRangeItem item in page.Items)
                {
                    if (item.Status != InspectionReadStatus.Available || item.Lease is null) { continue; }
                    if (item.Identity.Stream != ticket.Stream)
                    { throw new InvalidOperationException("The range provider returned a trace from another stream."); }
                    ulong index = item.Identity.AcquisitionIndex;
                    available[index] = item.Identity;
                    if (previous is { } prior && (prior == ulong.MaxValue || index != prior + 1))
                    { discontinuities = true; }
                    previous = index;
                    decimal relative = BScanSpatialViewportOperations.RelativeOffset(index,
                        request.SpatialViewport.OriginAcquisitionIndex);
                    int firstPixel = Math.Clamp((int)decimal.Floor((relative -
                        request.SpatialViewport.MinimumOffset) / request.SpatialViewport.Span *
                        request.PixelWidth), 0, request.PixelWidth - 1);
                    int lastPixel = Math.Clamp((int)decimal.Ceiling((relative + 1 -
                        request.SpatialViewport.MinimumOffset) / request.SpatialViewport.Span *
                        request.PixelWidth), firstPixel + 1, request.PixelWidth);
                    if (selectable[firstPixel] is null) { selectable[firstPixel] = item.Identity; }
                    int localFirst = Math.Max(firstPixel, x);
                    int localLast = Math.Min(lastPixel, x + width);
                    IReadOnlyUtSamples samples = item.Lease.Samples;
                    for (int pixelX = localFirst; pixelX < localLast; pixelX++)
                    {
                        int localX = pixelX - x;
                        for (int y = 0; y < request.PixelHeight; y++)
                        {
                            int destination = checked(y * width + localX);
                            short selected = extrema[destination];
                            int magnitude = validity[destination] == 0 ? -1 : Math.Abs((int)selected);
                            for (int sample = sampleFirst[y]; sample < sampleLast[y]; sample++)
                            {
                                short candidate = samples[sample];
                                int candidateMagnitude = Math.Abs((int)candidate);
                                if (candidateMagnitude <= magnitude) { continue; }
                                selected = candidate;
                                magnitude = candidateMagnitude;
                            }
                            if (magnitude < 0) { continue; }
                            extrema[destination] = selected;
                            pixels[destination] = BScanBipolarIntensity8.Map(selected);
                            validity[destination] = 1;
                            provenance[destination] = (byte)Scan2DSampleProvenance.AcquiredSupport;
                        }
                    }
                }
                }, token);
            }
            else
            {
                traversal = RasterizeResampledPages(ticket, traversalRange, represented,
                    metadata, request, x, width, storage, available, selectable,
                    ref previous, ref discontinuities, token);
            }
            if (traversal.Stale)
            { owner.Dispose(); DiscardOwner(request.ConsumerId, actualBytes); return BScanRasterResult.Stale(); }
            if (traversal.TerminalStatus is { } terminal)
            {
                owner.Dispose(); DiscardOwner(request.ConsumerId, actualBytes);
                return terminal switch
                {
                    InspectionReadStatus.Cancelled => BScanRasterResult.Cancelled(),
                    InspectionReadStatus.Closed => BScanRasterResult.Closed(),
                    InspectionReadStatus.OutOfRange => BScanRasterResult.OutOfRange(),
                    _ => BScanRasterResult.Faulted(
                        new InvalidOperationException("The authoritative range query faulted."))
                };
            }
            discontinuities |= traversal.AvailableCount == 0 ||
                !available.ContainsKey(represented.First) || !available.ContainsKey(represented.Last) ||
                traversal.Statuses.Any(static status => status is InspectionReadStatus.NotAvailable or
                    InspectionReadStatus.Gap);
            token.ThrowIfCancellationRequested();
            BeforeRasterCandidateCommit?.Invoke();
            var dirty = new BScanDirtyRange(dirtyRange.First, dirtyRange.Last,
                request.DisplayedStoreRevision.GetValueOrDefault(), state.StoreRevision);
            BScanVisualSnapshot candidate = CreateSnapshotProjected(admittedState, metadata, request,
                state, dirty, new(x, 0, width, request.PixelHeight), full, reason, represented,
                discontinuities, available, selectable, traversal.Statuses) with
            { HorizontalPixelShift = horizontalPixelShift };
            BeforeRasterCandidateValidation?.Invoke();
            BScanVisualSnapshot? snapshot = null;
            bool stale;
            bool active;
            lock (_gate)
            {
                active = !_disposed && _consumers.Contains(request.ConsumerId) &&
                    !_retiringConsumers.Contains(request.ConsumerId) &&
                    _consumerLifetimes.GetValueOrDefault(request.ConsumerId) == consumerLifetime;
                bool generationCurrent =
                    _pendingGenerations.GetValueOrDefault(request.ConsumerId) == requestGeneration;
                bool signatureCurrent = !_lastRequests.TryGetValue(request.ConsumerId,
                    out BScanRenderRequest? latest) || HasSameRenderSignature(admittedRequest, latest);
                bool authorityCurrent = _ingress.IsTicketCurrent(ticket);
                bool monotonic = ticket.FollowLiveWindow
                    ? candidate.StoreRevision >= request.DisplayedStoreRevision.GetValueOrDefault()
                    : !_currentByConsumer.TryGetValue(request.ConsumerId,
                        out BScanVisualSnapshot? current) || current.RunId != candidate.RunId ||
                        current.StoreRevision <= candidate.StoreRevision;
                stale = active && (!generationCurrent || !signatureCurrent || !authorityCurrent || !monotonic);
                if (stale)
                {
                    _staleCandidates++;
                    if (!generationCurrent || !signatureCurrent || !monotonic)
                    { RequestRecoveryUnderLock(request.ConsumerId, requestGeneration, request); }
                }
                else if (active)
                {
                    snapshot = candidate with { SnapshotVersion = ++_snapshotVersion };
                    _currentByConsumer[request.ConsumerId] = snapshot;
                    _recoveryConsumers.Remove(request.ConsumerId);
                }
            }
            if (stale || !active)
            {
                owner.Dispose(); DiscardOwner(request.ConsumerId, actualBytes);
                return stale ? BScanRasterResult.Stale() : BScanRasterResult.NotAvailable();
            }
            return BScanRasterResult.Available(new(snapshot!, x, 0, width, request.PixelHeight,
                owner, () => ReleaseOwner(request.ConsumerId, actualBytes)));
        }
        catch
        {
            owner.Dispose();
            DiscardOwner(request.ConsumerId, actualBytes);
            throw;
        }
        finally
        {
            extremaOwner?.Dispose();
            if (extremaAdmitted) { ReleaseScratchBytes(request.ConsumerId, extremaBytes); }
        }
    }

    private BScanPageTraversalResult RasterizeResampledPages(BScanProjectionTicket ticket,
        BScanAcquisitionRange traversalRange, BScanAcquisitionRange represented,
        ConventionalUtFrameMetadata metadata, BScanRenderRequest request, int destinationX,
        int destinationWidth, Memory<byte> destinationStorage,
        Dictionary<ulong, TraceIdentity> available, TraceIdentity?[] selectable,
        ref ulong? previous, ref bool discontinuities, CancellationToken token)
    {
        decimal firstValue = RelativeOffset(request.SpatialViewport.OriginAcquisitionIndex,
            traversalRange.First);
        decimal lastExclusive = checked(RelativeOffset(
            request.SpatialViewport.OriginAcquisitionIndex, traversalRange.Last) + 1);
        int firstOffset = checked((int)firstValue);
        int sourceWidth = checked((int)(lastExclusive - firstOffset));
        ArgumentOutOfRangeException.ThrowIfLessThan(sourceWidth, 1);
        double rate = metadata.Configuration.SampleRateHz;
        double offset = metadata.Configuration.FirstSampleOffsetSeconds;
        double visibleSampleMinimum = Math.Clamp(
            (request.TimeViewport.VisibleMinimumSeconds - offset) * rate,
            0, metadata.Configuration.SampleCount);
        double visibleSampleMaximum = Math.Clamp(
            (request.TimeViewport.VisibleMaximumSeconds - offset) * rate,
            0, metadata.Configuration.SampleCount);
        int visibleSampleFirst = Math.Clamp((int)Math.Floor(visibleSampleMinimum),
            0, metadata.Configuration.SampleCount);
        int visibleSampleLastExclusive = Math.Clamp((int)Math.Ceiling(visibleSampleMaximum),
            0, metadata.Configuration.SampleCount);
        int visibleSamples = Math.Max(1, visibleSampleLastExclusive - visibleSampleFirst);
        int sourceHeight = Math.Min(request.PixelHeight, visibleSamples);
        bool upsamplingTime = visibleSamples <= request.PixelHeight;

        int sourceCells = checked(sourceWidth * sourceHeight);
        IMemoryOwner<byte>? intensityOwner = null, maskOwner = null, segmentOwner = null;
        long intensityBytes = 0, maskBytes = 0, segmentBytes = 0;
        bool intensityAdmitted = false, maskAdmitted = false, segmentAdmitted = false;
        ulong? pagePrevious = previous;
        bool pageDiscontinuities = discontinuities;
        try
        {
            intensityOwner = RentScratchOwner(request.ConsumerId, sourceCells, out intensityBytes);
            intensityAdmitted = true;
            maskOwner = RentScratchOwner(request.ConsumerId, sourceCells, out maskBytes);
            maskAdmitted = true;
            segmentOwner = RentScratchOwner(request.ConsumerId, sourceCells, out segmentBytes);
            segmentAdmitted = true;
            Memory<byte> intensity = intensityOwner.Memory[..sourceCells];
            Memory<byte> mask = maskOwner.Memory[..sourceCells];
            Memory<byte> segments = segmentOwner.Memory[..sourceCells];
            intensity.Span.Clear(); mask.Span.Clear(); segments.Span.Clear();
            BScanPageTraversalResult traversal = _ingress.VisitVisualPages(ticket, traversalRange, page =>
            {
                Span<byte> intensitySpan = intensity.Span;
                Span<byte> maskSpan = mask.Span;
                foreach (InspectionRangeItem item in page.Items)
                {
                    if (item.Status != InspectionReadStatus.Available || item.Lease is null) { continue; }
                    if (item.Identity.Stream != ticket.Stream)
                    { throw new InvalidOperationException("The range provider returned a trace from another stream."); }
                    ulong index = item.Identity.AcquisitionIndex;
                    available[index] = item.Identity;
                    if (pagePrevious is { } prior && (prior == ulong.MaxValue || index != prior + 1))
                    { pageDiscontinuities = true; }
                    pagePrevious = index;
                    decimal relative = RelativeOffset(request.SpatialViewport.OriginAcquisitionIndex, index);
                    int sourceX = checked((int)(relative - firstOffset));
                    int selectionPixel = (int)decimal.Floor((relative -
                        request.SpatialViewport.MinimumOffset) / request.SpatialViewport.Span *
                        request.PixelWidth);
                    if ((uint)selectionPixel < (uint)selectable.Length && selectable[selectionPixel] is null)
                    { selectable[selectionPixel] = item.Identity; }
                    if ((uint)sourceX >= (uint)sourceWidth) { continue; }
                    IReadOnlyUtSamples samples = item.Lease.Samples;
                    for (int y = 0; y < sourceHeight; y++)
                    {
                        int sampleFirst;
                        int sampleLast;
                        if (upsamplingTime)
                        {
                            sampleFirst = Math.Min(visibleSampleFirst + y,
                                metadata.Configuration.SampleCount - 1);
                            sampleLast = sampleFirst + 1;
                        }
                        else
                        {
                            double u0 = visibleSampleMinimum +
                                (visibleSampleMaximum - visibleSampleMinimum) * y / sourceHeight;
                            double u1 = visibleSampleMinimum +
                                (visibleSampleMaximum - visibleSampleMinimum) * (y + 1) / sourceHeight;
                            sampleFirst = Math.Clamp((int)Math.Floor(u0), 0,
                                metadata.Configuration.SampleCount);
                            sampleLast = Math.Clamp((int)Math.Ceiling(u1), 0,
                                metadata.Configuration.SampleCount);
                        }
                        bool found = false;
                        short selected = 0;
                        int magnitude = -1;
                        for (int sample = sampleFirst; sample < sampleLast; sample++)
                        {
                            short candidate = samples[sample];
                            int candidateMagnitude = Math.Abs((int)candidate);
                            if (candidateMagnitude <= magnitude) { continue; }
                            selected = candidate; magnitude = candidateMagnitude; found = true;
                        }
                        if (!found) { continue; }
                        int sourceIndex = checked(y * sourceWidth + sourceX);
                        intensitySpan[sourceIndex] = BScanBipolarIntensity8.Map(selected);
                        maskSpan[sourceIndex] = 1;
                    }
                }
            }, token);
            previous = pagePrevious;
            discontinuities = pageDiscontinuities;
            if (traversal.Stale || traversal.TerminalStatus is not null) { return traversal; }

            Span<byte> maskValues = mask.Span;
            Span<byte> segmentValues = segments.Span;
            byte segment = 1;
            bool inGap = false;
            for (int sourceX = 0; sourceX < sourceWidth; sourceX++)
            {
                bool columnAvailable = false;
                for (int y = 0; y < sourceHeight && !columnAvailable; y++)
                { columnAvailable = maskValues[y * sourceWidth + sourceX] != 0; }
                if (!columnAvailable) { inGap = true; continue; }
                if (inGap)
                { segment = segment == byte.MaxValue ? (byte)1 : checked((byte)(segment + 1)); inGap = false; }
                for (int y = 0; y < sourceHeight; y++)
                {
                    int sourceIndex = y * sourceWidth + sourceX;
                    if (maskValues[sourceIndex] != 0) { segmentValues[sourceIndex] = segment; }
                }
            }
            double destinationStepY =
                (visibleSampleMaximum - visibleSampleMinimum) / request.PixelHeight;
            double sourceFirstY = upsamplingTime ? visibleSampleFirst + .5 :
                visibleSampleMinimum + destinationStepY / 2;
            double sourceStepY = upsamplingTime ? 1 : destinationStepY;
            var sourceGeometry = new Scan2DGridGeometry(firstOffset + .5, sourceFirstY, 1, sourceStepY);
            var destinationGeometry = new Scan2DGridGeometry(
                (double)(request.SpatialViewport.MinimumOffset + request.SpatialViewport.Span *
                    (2m * destinationX + 1) / (2m * request.PixelWidth)),
                visibleSampleMinimum + destinationStepY / 2,
                (double)(request.SpatialViewport.Span / request.PixelWidth), destinationStepY);
            var source = new Scan2DSourceGrid(sourceWidth, sourceHeight, sourceWidth, sourceGeometry,
                intensity, mask, segments, mask);
            var renderRequest = new Scan2DRenderRequest(sourceGeometry, sourceWidth, sourceHeight,
                destinationGeometry, destinationWidth, request.PixelHeight,
                new(0, 0, destinationWidth, request.PixelHeight), request.EffectiveResampling);
            using Scan2DRenderResult ignored = _resampler.Render(source, renderRequest,
                new BorrowedByteOwner(destinationStorage), token);
            ClearOutsideRepresentedRange(destinationStorage.Span, request, represented,
                destinationX, destinationWidth);
            return traversal;
        }
        finally
        {
            segmentOwner?.Dispose();
            if (segmentAdmitted) { ReleaseScratchBytes(request.ConsumerId, segmentBytes); }
            maskOwner?.Dispose();
            if (maskAdmitted) { ReleaseScratchBytes(request.ConsumerId, maskBytes); }
            intensityOwner?.Dispose();
            if (intensityAdmitted) { ReleaseScratchBytes(request.ConsumerId, intensityBytes); }
        }
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

    private static void ClearOutsideRepresentedRange(Span<byte> storage,
        BScanRenderRequest request, BScanAcquisitionRange represented,
        int destinationX, int destinationWidth)
    {
        int cells = checked(destinationWidth * request.PixelHeight);
        decimal first = RelativeOffset(request.SpatialViewport.OriginAcquisitionIndex,
            represented.First);
        decimal lastExclusive = checked(RelativeOffset(
            request.SpatialViewport.OriginAcquisitionIndex, represented.Last) + 1);
        for (int localX = 0; localX < destinationWidth; localX++)
        {
            int x = checked(destinationX + localX);
            decimal center = request.SpatialViewport.MinimumOffset +
                request.SpatialViewport.Span * (2m * x + 1) / (2m * request.PixelWidth);
            if (center >= first && center < lastExclusive) { continue; }
            for (int y = 0; y < request.PixelHeight; y++)
            {
                int index = checked(y * destinationWidth + localX);
                storage[index] = 0;
                storage[cells + index] = 0;
                storage[checked(cells * 2) + index] = (byte)Scan2DSampleProvenance.NoData;
            }
        }
    }

    private static BScanAcquisitionRange ExpandRange(BScanAcquisitionRange range,
        BScanAcquisitionRange bounds, ulong radius)
    {
        ulong first = range.First - Math.Min(radius, range.First - bounds.First);
        ulong last = range.Last + Math.Min(radius, bounds.Last - range.Last);
        return new(first, last);
    }

    private void SeedIncrementalIdentityMap(string consumerId,
        BScanAcquisitionRange representedRange, BScanAcquisitionRange dirtyRange,
        int dirtyX, int dirtyWidth, int horizontalPixelShift,
        Dictionary<ulong, TraceIdentity> available, TraceIdentity?[] selectable)
    {
        lock (_gate)
        {
            if (!_currentByConsumer.TryGetValue(consumerId, out BScanVisualSnapshot? prior)) { return; }
            foreach ((ulong index, TraceIdentity identity) in prior.AvailableTraceIdentities)
            {
                if (index >= representedRange.First && index <= representedRange.Last &&
                    (index < dirtyRange.First || index > dirtyRange.Last))
                { available[index] = identity; }
            }
            int copy = Math.Min(selectable.Length, Math.Max(0,
                prior.SelectableTraceIdentities.Count - horizontalPixelShift));
            for (int index = 0; index < copy; index++)
            { selectable[index] = prior.SelectableTraceIdentities[index + horizontalPixelShift]; }
        }
        Array.Clear(selectable, dirtyX, dirtyWidth);
    }

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
        ConventionalUtFrameMetadata metadata, BScanStoreVisualState state, int horizontalPixelShift = 0)
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
        if (request.EffectiveResampling.Mode is not (
            Scan2DInterpolationMode.None or Scan2DInterpolationMode.Bilinear))
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
        if (recovery) { reason |= BScanFullRefreshReason.Recovery; }
        return reason;
    }

    private static int GetHorizontalPixelShift(BScanRenderRequest request)
    {
        if (request.EffectiveResampling.Mode is not (
                Scan2DInterpolationMode.None or Scan2DInterpolationMode.Bilinear) ||
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
        BScanRenderRequest admittedRequest = request;
        BScanProjectionOperation? projection = null;
        Exception? primary = null;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            _shutdown.Token, _ingress.RunCancellationToken);
        try
        {
            if (!_ingress.TryBeginProjection(out BScanIngressVisualState? admittedState, out projection) ||
                admittedState is null) { return; }
            BScanStoreVisualState state = admittedState.StoreState;
            ConventionalUtFrameMetadata metadata = admittedState.Metadata;
            if (metadata.RunId != request.RunId || state.ResidentRange is not { } resident) { return; }
            request = ReconcileLiveWindow(request, resident);
            BScanAcquisitionRange represented = RepresentedRange(request.SpatialViewport, resident);
            if (represented.Last - represented.First >= (ulong)_ingress.Options.MaximumColumns) { return; }
            request = WithAcknowledgedDisplay(request);
            request = ReconcileLiveWindow(request, resident);
            long consumerLifetime;
            lock (_gate) { consumerLifetime = _consumerLifetimes.GetValueOrDefault(request.ConsumerId); }
            var ticket = BScanProjectionTicket.Create(admittedState, request,
                consumerLifetime, requestGeneration);
            int horizontalPixelShift = GetHorizontalPixelShift(request);
            BScanFullRefreshReason reason = GetFullRefreshReason(request, metadata, state,
                horizontalPixelShift);
            bool full = reason != BScanFullRefreshReason.None;
            BScanAcquisitionRange dirtyRange;
            if (full) { dirtyRange = represented; }
            else if (!_ingress.TryGetDirtyRange(ticket.Stream, represented,
                request.DisplayedStoreRevision.GetValueOrDefault(), out dirtyRange))
            { return; }
            var available = new Dictionary<ulong, TraceIdentity>();
            var selectable = new TraceIdentity?[request.PixelWidth];
            if (!full)
            {
                (int dirtyX, int dirtyWidth) = RasterColumns(request,
                    dirtyRange.First, dirtyRange.Last, false);
                SeedIncrementalIdentityMap(request.ConsumerId, represented, dirtyRange,
                    dirtyX, dirtyWidth, horizontalPixelShift, available, selectable);
            }
            ulong? previous = null;
            bool discontinuities = state.CommittedGapCount != 0;
            BScanPageTraversalResult traversal = _ingress.VisitVisualPages(ticket, dirtyRange, page =>
            {
                foreach (InspectionRangeItem item in page.Items)
                {
                    if (item.Status != InspectionReadStatus.Available || item.Lease is null) { continue; }
                    if (item.Identity.Stream != ticket.Stream)
                    { throw new InvalidOperationException("The range provider returned a trace from another stream."); }
                    ulong index = item.Identity.AcquisitionIndex;
                    available[index] = item.Identity;
                    if (previous is { } prior && (prior == ulong.MaxValue || index != prior + 1))
                    { discontinuities = true; }
                    previous = index;
                    decimal offset = BScanSpatialViewportOperations.RelativeOffset(index,
                        request.SpatialViewport.OriginAcquisitionIndex);
                    int pixel = (int)decimal.Floor((offset - request.SpatialViewport.MinimumOffset) /
                        request.SpatialViewport.Span * request.PixelWidth);
                    if ((uint)pixel < (uint)selectable.Length && selectable[pixel] is null)
                    { selectable[pixel] = item.Identity; }
                }
            }, linked.Token);
            if (traversal.Stale || traversal.TerminalStatus is not null) { return; }
            discontinuities |= traversal.AvailableCount == 0 ||
                !available.ContainsKey(represented.First) || !available.ContainsKey(represented.Last) ||
                traversal.Statuses.Any(static status => status is InspectionReadStatus.NotAvailable or InspectionReadStatus.Gap);
            var dirty = new BScanDirtyRange(dirtyRange.First, dirtyRange.Last,
                request.DisplayedStoreRevision.GetValueOrDefault(), state.StoreRevision);
            var candidate = CreateSnapshotProjected(admittedState, metadata, request, state, dirty,
                new(0, 0, request.PixelWidth, request.PixelHeight), full, reason, represented,
                discontinuities, available, selectable, traversal.Statuses) with
            { HorizontalPixelShift = horizontalPixelShift };
            BeforeMetadataCandidateCommit?.Invoke(candidate);
            BeforeMetadataCandidateValidation?.Invoke();
            Audit(BScanVisualAuditEventKind.CommitAttempted, request.ConsumerId, admittedState,
                requestGeneration, request);
            Subscription[] observers;
            BScanVisualSnapshot snapshot;
            BScanVisualCallbackAuditContext? callbackAudit = null;
            lock (_gate)
            {
                if (_disposed)
                {
                    Audit(BScanVisualAuditEventKind.CandidateRejectedClosed, request.ConsumerId,
                        admittedState, requestGeneration, request, BScanVisualAuditResult.Closed,
                        BScanVisualAuditRejection.Closed);
                    return;
                }
                bool ingressCurrent = _ingress.IsTicketCurrent(ticket);
                bool storeCurrent = ingressCurrent;
                long currentGeneration = _pendingGenerations.GetValueOrDefault(request.ConsumerId);
                bool active = _consumers.Contains(request.ConsumerId) &&
                    !_retiringConsumers.Contains(request.ConsumerId) &&
                    _consumerLifetimes.GetValueOrDefault(request.ConsumerId) == consumerLifetime;
                bool signatureCurrent = !_lastRequests.TryGetValue(request.ConsumerId,
                    out BScanRenderRequest? latestRequest) || HasSameRenderSignature(admittedRequest, latestRequest);
                if (!active || currentGeneration != requestGeneration || !signatureCurrent || !ingressCurrent)
                {
                    _lastMetadataCandidateAudit = new(request.RunId, requestGeneration,
                        currentGeneration, ingressCurrent, storeCurrent, false,
                        _lastGenerationReason, request);
                    Audit(BScanVisualAuditEventKind.CandidateRejectedStale, request.ConsumerId,
                        admittedState, requestGeneration, request, BScanVisualAuditResult.Stale,
                        !active ? BScanVisualAuditRejection.Consumer :
                        currentGeneration != requestGeneration ? BScanVisualAuditRejection.Generation :
                        !signatureCurrent ? BScanVisualAuditRejection.Signature :
                        BScanVisualAuditRejection.StoreState);
                    _staleCandidates++;
                    if (currentGeneration != requestGeneration || !signatureCurrent)
                    { RequestRecoveryUnderLock(request.ConsumerId, requestGeneration, request); }
                    long latestGeneration = _pendingGenerations.GetValueOrDefault(request.ConsumerId);
                    if (_committedGenerations.GetValueOrDefault(request.ConsumerId) != latestGeneration &&
                        !_pending.ContainsKey(request.ConsumerId) &&
                        _lastRequests.TryGetValue(request.ConsumerId, out BScanRenderRequest? latest) &&
                        _ingress.CurrentVisualState is { IsProjectable: true } current &&
                        latest.RunId == current.Metadata.RunId)
                    { _pending[request.ConsumerId] = latest; }
                    if (_pending.ContainsKey(request.ConsumerId) && _wake.CurrentCount == 0)
                    { _wake.Release(); }
                    return;
                }
                snapshot = candidate with { SnapshotVersion = ++_snapshotVersion };
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
                callbackAudit = CaptureCallbackAuditContext(request.ConsumerId, admittedState,
                    requestGeneration, request);
                Audit(BScanVisualAuditEventKind.CandidateCommitted, request.ConsumerId,
                    admittedState, requestGeneration, request, BScanVisualAuditResult.Committed);
            }
            var update = new BScanVisualUpdate(snapshot);
            BScanProjectionCallbackGroup? callbackGroup = observers.Length == 0
                ? null : new(projection!, observers.Length);
            if (callbackGroup is not null) { projection = null; }
            foreach (Subscription subscription in observers)
            { subscription.Offer(update, callbackAudit, callbackGroup); }
        }
        catch (OperationCanceledException error) when (linked.IsCancellationRequested)
        { primary = error; }
        catch (Exception error) { primary = error; throw; }
        finally { projection?.Dispose(); }
    }

    private static BScanVisualSnapshot CreateSnapshotProjected(BScanIngressVisualState ingressState,
        ConventionalUtFrameMetadata metadata, BScanRenderRequest request,
        BScanStoreVisualState state, BScanDirtyRange dirty, BScanRasterRectangle region,
        bool full, BScanFullRefreshReason reason, BScanAcquisitionRange represented,
        bool discontinuities, IReadOnlyDictionary<ulong, TraceIdentity> available,
        TraceIdentity?[] selectable, IReadOnlySet<InspectionReadStatus> statuses)
    {
        double domainMinimum = metadata.Configuration.FirstSampleOffsetSeconds;
        double domainMaximum = domainMinimum +
            metadata.Configuration.SampleCount / metadata.Configuration.SampleRateHz;
        return new(metadata.RunId, 0, dirty.BaseStoreRevision, state.StoreRevision,
            state.WrapGeneration, represented, state.ResidentRange!.Value,
            ScanTimeViewportOperations.Create(domainMinimum, domainMaximum), request.SpatialViewport,
            request.TimeViewport, request.PixelWidth, request.PixelHeight,
            request.RenderConfigurationVersion, dirty, region, discontinuities, full, reason)
        {
            ConsumerId = request.ConsumerId,
            Stream = new(metadata.SourceId, metadata.RunId,
                metadata.Configuration.PhysicalChannelId),
            TraceStream = ingressState.StreamIdentity,
            AvailableTraceIdentities = new Dictionary<ulong, TraceIdentity>(available),
            SelectableTraceIdentities = Array.AsReadOnly(selectable),
            NoDataStatuses = new HashSet<InspectionReadStatus>(statuses.Where(
                static status => status != InspectionReadStatus.Available)),
            RunState = state.RunState,
            FinalRevision = state.FinalRevision,
            AreWatermarksFinal = state.AreWatermarksFinal,
            CommittedTraceCount = state.CommittedTraceCount,
            CommittedGapCount = state.CommittedGapCount,
            Resampling = request.EffectiveResampling
        };
    }

    private static BScanAcquisitionRange RepresentedRange(BScanSpatialViewport viewport,
        BScanAcquisitionRange resident)
    {
        decimal firstValue = decimal.Floor(viewport.OriginAcquisitionIndex + viewport.MinimumOffset);
        decimal lastValue = decimal.Ceiling(viewport.OriginAcquisitionIndex + viewport.MaximumOffset) - 1;
        ulong first = firstValue <= 0 ? 0 : firstValue >= ulong.MaxValue ? ulong.MaxValue : (ulong)firstValue;
        ulong last = lastValue <= 0 ? 0 : lastValue >= ulong.MaxValue ? ulong.MaxValue : (ulong)lastValue;
        first = Math.Clamp(first, resident.First, resident.Last);
        last = Math.Clamp(last, first, resident.Last);
        return new(first, last);
    }

    private void OnVisualStateChanged()
    {
        AcquisitionRunId? currentRun = _ingress.CurrentVisualState is { IsProjectable: true } state
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

    private sealed class Subscription(BScanVisualDelivery owner, IObserver<BScanVisualUpdate> observer) : IDisposable
    {
        private readonly object _gate = new();
        private BScanPendingCallback? _pending;
        private Task _pump = Task.CompletedTask;
        private bool _detached;
        internal void Offer(BScanVisualUpdate update, BScanVisualCallbackAuditContext? audit,
            BScanProjectionCallbackGroup? projection)
        {
            lock (_gate)
            {
                if (_detached) { projection?.Release(); return; }
                _pending?.Projection?.Release();
                _pending = new(update, audit, projection);
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
                finally { pending.Projection?.Release(); }
            }
        }
        internal Task Detach()
        {
            lock (_gate)
            {
                _detached = true;
                _pending?.Projection?.Release();
                _pending = null;
                return _pump;
            }
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
        BScanVisualCallbackAuditContext? Audit,
        BScanProjectionCallbackGroup? Projection);

    private sealed class BScanProjectionCallbackGroup(BScanProjectionOperation projection, int count)
    {
        private BScanProjectionOperation? _projection = projection;
        private int _remaining = count;
        internal void Release()
        {
            if (Interlocked.Decrement(ref _remaining) == 0)
            { Interlocked.Exchange(ref _projection, null)?.Dispose(); }
        }
    }
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
