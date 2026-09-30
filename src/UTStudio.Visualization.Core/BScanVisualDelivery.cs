using System.Buffers;
using UTStudio.Domain.Acquisition;
using UTStudio.Visualization.Core.Scan2D;

namespace UTStudio.Visualization.Core;

internal interface IByteOwnerPool { IMemoryOwner<byte> Rent(int minimumLength); }
internal sealed class SharedByteOwnerPool : IByteOwnerPool
{
    internal static SharedByteOwnerPool Instance { get; } = new();
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
    private readonly IScan2DResampler _resampler = new Scan2DResampler();
    private readonly SemaphoreSlim _wake = new(0, 1);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly List<Subscription> _subscriptions = [];
    private readonly Task _worker;
    private readonly Dictionary<string, BScanRenderRequest> _pending = [];
    private readonly Dictionary<string, BScanRenderRequest> _lastRequests = [];
    private readonly Dictionary<string, long> _pendingGenerations = [];
    private readonly Dictionary<string, int> _consumerOwners = [];
    private readonly Dictionary<string, long> _consumerOwnerBytes = [];
    private readonly Dictionary<string, BScanVisualSnapshot> _currentByConsumer = [];
    private readonly Dictionary<string, BScanDisplayedVisualIdentity> _displayedByConsumer = [];
    private readonly HashSet<string> _consumers = [];
    private readonly HashSet<string> _bootstrapConsumers = [];
    private readonly HashSet<string> _retiringConsumers = [];
    private long _requestGeneration;
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
    internal Action? BeforeRasterCandidateValidation { get; set; }
    internal Action<int, int>? AfterRasterColumnReleased { get; set; }
    internal Action? BeforeMetadataCandidateValidation { get; set; }
    internal int ActiveConsumerCount { get { lock (_gate) { return _consumers.Count; } } }
    internal int PendingBootstrapCount { get { lock (_gate) { return _bootstrapConsumers.Count; } } }
    internal int RetainedSnapshotCount { get { lock (_gate) { return _currentByConsumer.Count; } } }
    internal long RenderRequestCount => Interlocked.Read(ref _renderRequests);
    internal long FullIdentityRequestCount => Interlocked.Read(ref _fullIdentityRequests);
    internal BScanDisplayedVisualIdentity? GetAcknowledgedDisplay(string consumerId)
    { lock (_gate) { return _displayedByConsumer.GetValueOrDefault(consumerId); } }

    public BScanVisualDelivery(BScanIngress ingress, TimeProvider? timeProvider = null)
        : this(ingress, timeProvider, SharedByteOwnerPool.Instance) { }

    internal BScanVisualDelivery(BScanIngress ingress, TimeProvider? timeProvider, IByteOwnerPool pool)
    {
        _ingress = ingress ?? throw new ArgumentNullException(nameof(ingress));
        _clock = timeProvider ?? TimeProvider.System;
        _pool = pool ?? throw new ArgumentNullException(nameof(pool));
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
        }
        QueueBootstrap(consumerId);
    }

    private void QueueBootstrap(string consumerId)
    {
        BScanTraceStore.BScanTraceLease?[] leases =
            ArrayPool<BScanTraceStore.BScanTraceLease?>.Shared.Rent(_ingress.VisualCapacity);
        int count = _ingress.CaptureVisualLeases(leases.AsSpan(0, _ingress.VisualCapacity),
            out ConventionalUtFrameMetadata? metadata, out BScanStoreVisualState state);
        try
        {
            if (metadata is null || count == 0 || state.ResidentRange is not { } resident) { return; }
            lock (_gate)
            {
                if (!_consumers.Contains(consumerId)) { return; }
                if (_lastRequests.TryGetValue(consumerId, out BScanRenderRequest? current) &&
                    current.RunId == metadata.RunId) { _bootstrapConsumers.Remove(consumerId); return; }
                _bootstrapConsumers.Add(consumerId);
            }
            double minimum = metadata.Configuration.FirstSampleOffsetSeconds;
            double maximum = minimum + metadata.Configuration.SampleCount / metadata.Configuration.SampleRateHz;
            RequestRender(new(metadata.RunId, BScanSpatialViewportOperations.Reset(resident),
                ScanTimeViewportOperations.Create(minimum, maximum), 1, 1, 0, ConsumerId: consumerId));
        }
        catch (Exception error) when (error is ObjectDisposedException or InvalidOperationException) { }
        finally
        {
            for (int index = 0; index < count; index++) { leases[index]?.Dispose(); }
            ArrayPool<BScanTraceStore.BScanTraceLease?>.Shared.Return(leases, clearArray: true);
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
            _currentByConsumer.Remove(consumerId);
            _displayedByConsumer.Remove(consumerId);
            _bootstrapConsumers.Remove(consumerId);
            if (_consumerOwners.GetValueOrDefault(consumerId) == 0)
            {
                _consumerOwners.Remove(consumerId); _consumerOwnerBytes.Remove(consumerId);
                _consumers.Remove(consumerId); _retiringConsumers.Remove(consumerId);
            }
        }
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
            if (request.ConsumerId == "default" && !_consumers.Contains("default"))
            { _consumers.Add("default"); _consumerOwners["default"] = 0; _consumerOwnerBytes["default"] = 0; }
            if (!_consumers.Contains(request.ConsumerId))
            { throw new InvalidOperationException("The raster consumer is not registered."); }
            Interlocked.Increment(ref _renderRequests);
            if (request.DisplayedSnapshotVersion is null) { Interlocked.Increment(ref _fullIdentityRequests); }
            if (_pending.ContainsKey(request.ConsumerId)) { _coalesced = true; _updatesCoalesced++; }
            _lastRequests[request.ConsumerId] = request;
            _bootstrapConsumers.Remove(request.ConsumerId);
            _pending[request.ConsumerId] = request;
            _pendingGenerations[request.ConsumerId] = ++_requestGeneration;
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
            { subscription.Offer(new(snapshot)); }
            return subscription;
        }
    }

    public async ValueTask<BScanRasterResult> GetRegionAsync(BScanRenderRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        request.Validate();
        lock (_gate)
        {
            if (_disposed) { return BScanRasterResult.NotAvailable(); }
            _regionOperations++;
        }
        long started = _clock.GetTimestamp();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdown.Token);
        try
        {
            linked.Token.ThrowIfCancellationRequested();
            lock (_gate)
            {
                if (request.ConsumerId == "default" && !_consumers.Contains("default"))
                { _consumers.Add("default"); _consumerOwners["default"] = 0; _consumerOwnerBytes["default"] = 0; }
                if (!_consumers.Contains(request.ConsumerId)) { return BScanRasterResult.NotAvailable(); }
                if (_consumerOwners.GetValueOrDefault(request.ConsumerId) >=
                    _ingress.Options.MaximumRasterOwnersPerConsumer)
                { return BScanRasterResult.ResourceLimit("The consumer raster owner quota is exhausted."); }
            }
            BScanRasterResult result = BuildRegion(request, linked.Token);
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
            lock (_gate) { _lastError = Message(error); _recoveryConsumers.Add(request.ConsumerId); }
            return BScanRasterResult.Faulted(error);
        }
        finally
        {
            RecordBuildDuration(_clock.GetElapsedTime(started, _clock.GetTimestamp()));
            EndRegionOperation();
        }
    }

    private BScanRasterResult BuildRegion(BScanRenderRequest request, CancellationToken token)
    {
        BScanTraceStore.BScanTraceLease?[] leases = ArrayPool<BScanTraceStore.BScanTraceLease?>.Shared.Rent(_ingress.VisualCapacity);
        int count = _ingress.CaptureVisualLeases(leases.AsSpan(0, _ingress.VisualCapacity),
            out ConventionalUtFrameMetadata? metadata, out BScanStoreVisualState state);
        try
        {
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
            int cells = checked(width * request.PixelHeight);
            int length = checked(cells * 3);
            IMemoryOwner<byte> owner = _pool.Rent(length);
            if (owner.Memory.Length < length) { owner.Dispose(); throw new InvalidOperationException("Raster pool returned an undersized owner."); }
            long actualBytes = owner.Memory.Length;
            long budget = checked(_ingress.Options.VisualMemoryBudgetBytes - _ingress.VisualBytesReserved);
            lock (_gate)
            {
                long consumerCeiling = _ingress.Options.VisualMemoryBudgetBytes /
                    _ingress.Options.MaximumRasterConsumers;
                long consumerBytes = _consumerOwnerBytes.GetValueOrDefault(request.ConsumerId);
                if (!_consumers.Contains(request.ConsumerId) ||
                    _consumerOwners.GetValueOrDefault(request.ConsumerId) >=
                        _ingress.Options.MaximumRasterOwnersPerConsumer ||
                    checked(_activeOwnerBytes + actualBytes) > budget ||
                    checked(consumerBytes + actualBytes) > consumerCeiling)
                {
                    owner.Dispose();
                    return BScanRasterResult.ResourceLimit("The B-Scan visual memory quota is exhausted.");
                }
                _activeOwnerBytes += actualBytes;
                _activeOwners++;
                _consumerOwners[request.ConsumerId] = _consumerOwners.GetValueOrDefault(request.ConsumerId) + 1;
                _consumerOwnerBytes[request.ConsumerId] = consumerBytes + actualBytes;
                _maximumOwners = Math.Max(_maximumOwners, _activeOwners);
                _maximumOwnerBytes = Math.Max(_maximumOwnerBytes, _activeOwnerBytes);
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
            }
            catch
            {
                owner.Dispose();
                DiscardOwner(request.ConsumerId, actualBytes);
                throw;
            }
            bool bootstrap = request.DisplayedSnapshotVersion is null && request.DisplayedStoreRevision is null &&
                request.DisplayedWrapGeneration is null && request.Displayed is null;
            if (!_ingress.IsVisualStateCurrent(metadata, state.StoreRevision, state.WrapGeneration) &&
                !((bootstrap || request.FollowLiveWindow) && _ingress.IsBootstrapStateCompatible(
                    metadata, state.StoreRevision, state.WrapGeneration)))
            {
                owner.Dispose(); DiscardOwner(request.ConsumerId, actualBytes);
                lock (_gate) { _staleCandidates++; _recoveryConsumers.Add(request.ConsumerId); }
                return BScanRasterResult.Stale();
            }
            bool superseded;
            lock (_gate)
            {
                superseded = _lastRequests.TryGetValue(request.ConsumerId, out BScanRenderRequest? latest) &&
                    !HasSameRenderSignature(request, latest);
                if (superseded) { _staleCandidates++; _recoveryConsumers.Add(request.ConsumerId); }
            }
            if (superseded)
            {
                owner.Dispose(); DiscardOwner(request.ConsumerId, actualBytes);
                return BScanRasterResult.Stale();
            }

            ulong version;
            lock (_gate) { version = ++_snapshotVersion; }
            var dirty = new BScanDirtyRange(dirtyFirst, dirtyLast,
                request.DisplayedStoreRevision.GetValueOrDefault(), state.StoreRevision);
            var snapshot = CreateSnapshot(metadata, request, state, version, dirty,
                new(x, 0, width, request.PixelHeight), full, reason, leases, count) with
            { HorizontalPixelShift = horizontalPixelShift };
            bool active;
            lock (_gate)
            {
                active = !_disposed && _consumers.Contains(request.ConsumerId) &&
                    !_retiringConsumers.Contains(request.ConsumerId);
                if (active) { _currentByConsumer[request.ConsumerId] = snapshot; _recoveryConsumers.Remove(request.ConsumerId); }
            }
            if (!active)
            { owner.Dispose(); DiscardOwner(request.ConsumerId, actualBytes); return BScanRasterResult.NotAvailable(); }
            return BScanRasterResult.Available(new(snapshot, x, 0, width, request.PixelHeight,
                owner, () => ReleaseOwner(request.ConsumerId, actualBytes)));
        }
        finally
        {
            for (int index = 0; index < count; index++) { leases[index]?.Dispose(); }
            ArrayPool<BScanTraceStore.BScanTraceLease?>.Shared.Return(leases, clearArray: true);
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
        const int BlockRows = 64;
        int sourceWidth = GetResamplingSourceWidth(request);
        int verticalHalo = request.EffectiveResampling.Mode switch
        {
            Scan2DInterpolationMode.Bilinear => 1,
            Scan2DInterpolationMode.Bicubic => 2,
            _ => 0
        };
        int firstOffset = checked((int)decimal.Floor(request.SpatialViewport.MinimumOffset));
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
            sampleFirstByRow[y] = Math.Clamp((int)Math.Floor((t0 - offset) * rate), 0, metadata.Configuration.SampleCount);
            sampleLastByRow[y] = Math.Clamp((int)Math.Ceiling((t1 - offset) * rate), 0, metadata.Configuration.SampleCount);
        }

        for (int blockY = 0; blockY < request.PixelHeight; blockY += BlockRows)
        {
            int blockHeight = Math.Min(BlockRows, request.PixelHeight - blockY);
            int sourceStartY = Math.Max(0, blockY - verticalHalo);
            int sourceEndY = Math.Min(request.PixelHeight, checked(blockY + blockHeight + verticalHalo));
            int blockSourceHeight = sourceEndY - sourceStartY;
            int blockSourceCells = checked(sourceWidth * blockSourceHeight);
            int blockDestinationCells = checked(request.PixelWidth * blockHeight);
            int scratchLength = checked(checked(blockDestinationCells * 3) + checked(blockSourceCells * 4));
            IMemoryOwner<byte> scratch = _pool.Rent(scratchLength);
            long scratchBytes = scratch.Memory.Length;
            AdmitScratchOwner(scratch, scratchBytes);
            try
            {
                Memory<byte> block = scratch.Memory[..scratchLength];
                block.Span.Clear();
                Memory<byte> sourceStorage = block.Slice(checked(blockDestinationCells * 3), checked(blockSourceCells * 4));
                Memory<byte> sourceIntensity = sourceStorage[..blockSourceCells];
                Memory<byte> sourceMask = sourceStorage.Slice(blockSourceCells, blockSourceCells);
                Memory<byte> sourceSegments = sourceStorage.Slice(checked(blockSourceCells * 2), blockSourceCells);
                Memory<byte> sourceProvenance = sourceStorage.Slice(checked(blockSourceCells * 3), blockSourceCells);
                byte segment = 1;
                bool gap = false;
                for (int sourceX = 0; sourceX < sourceWidth; sourceX++)
                {
                    ulong? acquisitionIndex = TryAddOffset(request.SpatialViewport.OriginAcquisitionIndex,
                        checked(firstOffset + sourceX));
                    int leaseIndex = acquisitionIndex is { } index ? FindAcquisitionIndex(leases, count, index) : -1;
                    if (leaseIndex < 0) { gap = true; continue; }
                    if (gap) { segment = segment == byte.MaxValue ? (byte)1 : checked((byte)(segment + 1)); gap = false; }
                    for (int localY = 0; localY < blockSourceHeight; localY++)
                    {
                        int y = sourceStartY + localY;
                        bool found = false; short selected = 0; int magnitude = -1;
                        SelectRange(leases[leaseIndex]!, bucketSamples, sampleFirstByRow[y], sampleLastByRow[y],
                            ref selected, ref magnitude, ref found);
                        if (!found) { continue; }
                        int sourceIndex = checked(localY * sourceWidth + sourceX);
                        sourceIntensity.Span[sourceIndex] = BScanBipolarIntensity8.Map(selected);
                        sourceMask.Span[sourceIndex] = 1; sourceSegments.Span[sourceIndex] = segment;
                        sourceProvenance.Span[sourceIndex] = (byte)Scan2DSampleProvenance.AcquiredSupport;
                    }
                }

                var sourceGeometry = new Scan2DGridGeometry(firstOffset + .5, sourceStartY + .5, 1, 1);
                var destinationGeometry = new Scan2DGridGeometry(
                    (double)(request.SpatialViewport.MinimumOffset + request.SpatialViewport.Span /
                        (2m * request.PixelWidth)), blockY + .5,
                    (double)(request.SpatialViewport.Span / request.PixelWidth), 1);
                var source = new Scan2DSourceGrid(sourceWidth, blockSourceHeight, sourceWidth, sourceGeometry,
                    sourceIntensity, sourceMask, sourceSegments, sourceProvenance);
                var renderRequest = new Scan2DRenderRequest(sourceGeometry, sourceWidth, blockSourceHeight,
                    destinationGeometry, request.PixelWidth, blockHeight,
                    new(0, 0, request.PixelWidth, blockHeight), request.EffectiveResampling);
                using Scan2DRenderResult result = _resampler.Render(source, renderRequest,
                    new BorrowedByteOwner(block[..checked(blockDestinationCells * 3)]), token);
                CopyBlock(result.Intensities.Span, destinationStorage.Span, blockY, request.PixelWidth, 0);
                CopyBlock(result.ValidityMask.Span, destinationStorage.Span, blockY, request.PixelWidth,
                    checked(request.PixelWidth * request.PixelHeight));
                CopyBlock(result.Provenance.Span, destinationStorage.Span, blockY, request.PixelWidth,
                    checked(request.PixelWidth * request.PixelHeight * 2));
            }
            finally { scratch.Dispose(); ReleaseScratchBytes(scratchBytes); }
        }
    }

    private void AdmitScratchOwner(IMemoryOwner<byte> owner, long bytes)
    {
        lock (_gate)
        {
            long budget = checked(_ingress.Options.VisualMemoryBudgetBytes - _ingress.VisualBytesReserved);
            if (owner.Memory.Length == 0 || checked(_activeOwnerBytes + bytes) > budget)
            { owner.Dispose(); throw new InvalidOperationException("The B-Scan visual scratch quota is exhausted."); }
            _activeOwnerBytes += bytes;
            _maximumOwnerBytes = Math.Max(_maximumOwnerBytes, _activeOwnerBytes);
        }
    }

    private void ReleaseScratchBytes(long bytes)
    {
        lock (_gate)
        {
            _activeOwnerBytes -= bytes;
        }
    }

    private static void CopyBlock(ReadOnlySpan<byte> source, Span<byte> destination, int blockY,
        int width, int planeOffset)
    {
        int destinationOffset = checked(planeOffset + blockY * width);
        source.CopyTo(destination.Slice(destinationOffset, source.Length));
    }

    private static int GetResamplingSourceWidth(BScanRenderRequest request)
    {
        decimal first = decimal.Floor(request.SpatialViewport.MinimumOffset);
        decimal lastExclusive = decimal.Ceiling(request.SpatialViewport.MaximumOffset);
        int width = checked((int)(lastExclusive - first));
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 1);
        return width;
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

    private static int FindAcquisitionIndex(BScanTraceStore.BScanTraceLease?[] leases, int count, ulong target)
    {
        int low = 0, high = count - 1;
        while (low <= high)
        {
            int middle = low + ((high - low) >> 1);
            ulong value = leases[middle]!.Descriptor.AcquisitionIndex;
            if (value == target) { return middle; }
            if (value < target) { low = middle + 1; } else { high = middle - 1; }
        }
        return -1;
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
        BScanTraceStore.BScanTraceLease?[] leases = ArrayPool<BScanTraceStore.BScanTraceLease?>.Shared.Rent(_ingress.VisualCapacity);
        int count = _ingress.CaptureVisualLeases(leases.AsSpan(0, _ingress.VisualCapacity),
            out ConventionalUtFrameMetadata? metadata, out BScanStoreVisualState state);
        try
        {
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
            BeforeMetadataCandidateValidation?.Invoke();
            if (!_ingress.IsVisualStateCurrent(metadata, state.StoreRevision, state.WrapGeneration))
            { lock (_gate) { _staleCandidates++; _recoveryConsumers.Add(request.ConsumerId); } return; }
            lock (_gate)
            {
                if (_disposed) { return; }
                if (_pendingGenerations.GetValueOrDefault(request.ConsumerId) != requestGeneration)
                {
                    _staleCandidates++;
                    _recoveryConsumers.Add(request.ConsumerId);
                    return;
                }
                _lastPublication = _clock.GetTimestamp();
                _currentByConsumer[request.ConsumerId] = snapshot;
                _updatesPublished++;
                if (full) { _fullRefreshes++; } else { _incrementalRefreshes++; }
                _coalesced = false;
                _recoveryConsumers.Remove(request.ConsumerId);
                foreach (Subscription subscription in _subscriptions) { subscription.Offer(new(snapshot)); }
            }
        }
        finally
        {
            for (int index = 0; index < count; index++) { leases[index]?.Dispose(); }
            ArrayPool<BScanTraceStore.BScanTraceLease?>.Shared.Return(leases, clearArray: true);
        }
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
        AcquisitionRunId? currentRun = _ingress.CurrentMetadata?.RunId;
        string[] bootstrap;
        lock (_gate)
        {
            if (_disposed) { return; }
            _columnsIndexed++;
            foreach ((string consumerId, BScanRenderRequest request) in _lastRequests)
            {
                if (_pending.ContainsKey(consumerId)) { _coalesced = true; _updatesCoalesced++; }
                _pending[consumerId] = request;
                _pendingGenerations[consumerId] = ++_requestGeneration;
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

    private sealed class BScanLeaseComparer : IComparer<BScanTraceStore.BScanTraceLease?>
    {
        internal static BScanLeaseComparer Instance { get; } = new();
        public int Compare(BScanTraceStore.BScanTraceLease? x, BScanTraceStore.BScanTraceLease? y) =>
            x!.Descriptor.AcquisitionIndex.CompareTo(y!.Descriptor.AcquisitionIndex);
    }

    private sealed class Subscription(BScanVisualDelivery owner, IObserver<BScanVisualUpdate> observer) : IDisposable
    {
        private readonly object _gate = new();
        private BScanVisualUpdate? _pending;
        private Task _pump = Task.CompletedTask;
        private bool _detached;
        internal void Offer(BScanVisualUpdate update)
        {
            lock (_gate)
            {
                if (_detached) { return; }
                _pending = update;
                if (_pump.IsCompleted) { _pump = Task.Run(Pump); }
            }
        }
        private void Pump()
        {
            while (true)
            {
                BScanVisualUpdate? update;
                lock (_gate) { if (_detached || _pending is null) { return; } update = _pending; _pending = null; }
                try
                {
                    lock (owner._gate) { owner._observerCallbacksStarted++; }
                    observer.OnNext(update);
                    lock (owner._gate) { owner._observerCallbacksCompleted++; }
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
}
// End of the neutral B-Scan delivery implementation.
