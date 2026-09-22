using System.Buffers;
using UTStudio.Domain.Acquisition;

namespace UTStudio.Visualization.Core;

internal interface IByteOwnerPool { IMemoryOwner<byte> Rent(int minimumLength); }
internal sealed class SharedByteOwnerPool : IByteOwnerPool
{
    internal static SharedByteOwnerPool Instance { get; } = new();
    public IMemoryOwner<byte> Rent(int minimumLength) => MemoryPool<byte>.Shared.Rent(minimumLength);
}

/// <summary>Bounded, neutral and latest-only visual projection over a B-Scan ingress.</summary>
public sealed class BScanVisualDelivery : IObservable<BScanVisualUpdate>, IBScanRasterRegionProvider, IAsyncDisposable
{
    private const int DurationWindow = 1024;
    public static readonly TimeSpan MinimumPublicationInterval = TimeSpan.FromTicks(333_334);
    private readonly object _gate = new();
    private readonly BScanIngress _ingress;
    private readonly TimeProvider _clock;
    private readonly IByteOwnerPool _pool;
    private readonly SemaphoreSlim _wake = new(0, 1);
    private readonly SemaphoreSlim _ownerQuota = new(2, 2);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly List<Subscription> _subscriptions = [];
    private readonly Task _worker;
    private BScanRenderRequest? _pending;
    private BScanRenderRequest? _lastRequest;
    private long _requestGeneration;
    private long _pendingRequestGeneration;
    private bool _coalesced;
    private bool _recoveryRequired;
    private bool _disposed;
    private Task? _disposal;
    private long? _lastPublication;
    private ulong _snapshotVersion;
    private BScanVisualSnapshot? _current;
    private int _regionOperations;
    private TaskCompletionSource? _regionDrain;
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
    private string? _lastError;
    internal Action? BeforeRasterCandidateValidation { get; set; }
    internal Action? BeforeMetadataCandidateValidation { get; set; }

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

    public BScanVisualSnapshot? Current { get { lock (_gate) { return _current; } } }

    public BScanVisualDiagnostics Diagnostics
    {
        get
        {
            lock (_gate)
            {
                return new(_columnsIndexed, _fullRefreshes + _incrementalRefreshes, _fullRefreshes,
                    _incrementalRefreshes, _staleCandidates + _cancelledCandidates, _updatesPublished,
                    _updatesCoalesced, _observerCallbacksStarted, _observerCallbacksCompleted,
                    _observerErrors, _activeOwners, _maximumOwners, _ingress.VisualBytesReserved,
                    _activeOwnerBytes, _maximumOwnerBytes, BuildDurationsUnderLock(),
                    _closed ? BScanVisualCloseState.Closed : _disposed ? BScanVisualCloseState.Closing : BScanVisualCloseState.Open,
                    _regionOperations, _lastError);
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
            if (_pending is not null) { _coalesced = true; _updatesCoalesced++; }
            _lastRequest = request;
            _pending = request;
            _pendingRequestGeneration = ++_requestGeneration;
            if (_wake.CurrentCount == 0) { _wake.Release(); }
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
            if (_current is not null) { subscription.Offer(new(_current)); }
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
        bool quota = false;
        long started = _clock.GetTimestamp();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdown.Token);
        try
        {
            await _ownerQuota.WaitAsync(linked.Token).ConfigureAwait(false);
            quota = true;
            BScanRasterResult result = BuildRegion(request, linked.Token);
            if (result.Region is null) { _ownerQuota.Release(); }
            quota = false;
            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (quota) { _ownerQuota.Release(); quota = false; }
            lock (_gate) { _cancelledCandidates++; }
            return BScanRasterResult.Cancelled();
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
            if (quota) { _ownerQuota.Release(); quota = false; }
            return BScanRasterResult.NotAvailable();
        }
        catch (Exception error)
        {
            if (quota) { _ownerQuota.Release(); quota = false; }
            lock (_gate) { _lastError = Message(error); _recoveryRequired = true; }
            return BScanRasterResult.Faulted(error);
        }
        finally
        {
            if (!quota) { /* no owner was transferred */ }
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
            token.ThrowIfCancellationRequested();

            BScanFullRefreshReason reason = GetFullRefreshReason(request, state);
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
            int length = checked(width * request.PixelHeight);
            IMemoryOwner<byte> owner = _pool.Rent(length);
            if (owner.Memory.Length < length) { owner.Dispose(); throw new InvalidOperationException("Raster pool returned an undersized owner."); }
            long actualBytes = owner.Memory.Length;
            long budget = _ingress.Options.VisualMemoryBudgetBytes - _ingress.VisualBytesReserved + 2L * 1024 * 1024;
            lock (_gate)
            {
                if (checked(_activeOwnerBytes + actualBytes) > budget)
                { owner.Dispose(); throw new InvalidOperationException("B-Scan visual budget exhausted."); }
                _activeOwnerBytes += actualBytes;
                _activeOwners++;
                _maximumOwners = Math.Max(_maximumOwners, _activeOwners);
                _maximumOwnerBytes = Math.Max(_maximumOwnerBytes, _activeOwnerBytes);
            }
            Span<byte> pixels = owner.Memory.Span[..length];
            pixels.Clear();
            try
            {
                Rasterize(leases, count, metadata, state.IndexBucketSamples, request, x, width, pixels, token);
                BeforeRasterCandidateValidation?.Invoke();
                token.ThrowIfCancellationRequested();
            }
            catch
            {
                owner.Dispose();
                DiscardOwner(actualBytes);
                throw;
            }
            if (!_ingress.IsVisualStateCurrent(metadata, state.StoreRevision, state.WrapGeneration))
            {
                owner.Dispose(); DiscardOwner(actualBytes);
                lock (_gate) { _staleCandidates++; _recoveryRequired = true; }
                return BScanRasterResult.Stale();
            }

            ulong version;
            lock (_gate) { version = ++_snapshotVersion; }
            var dirty = new BScanDirtyRange(dirtyFirst, dirtyLast,
                request.DisplayedStoreRevision.GetValueOrDefault(), state.StoreRevision);
            var snapshot = CreateSnapshot(metadata, request, state, version, dirty,
                new(x, 0, width, request.PixelHeight), full, reason, leases, count);
            lock (_gate) { if (!_disposed) { _current = snapshot; _recoveryRequired = false; } }
            return BScanRasterResult.Available(new(snapshot, x, 0, width, request.PixelHeight,
                owner, () => ReleaseOwner(actualBytes)));
        }
        finally
        {
            for (int index = 0; index < count; index++) { leases[index]?.Dispose(); }
            ArrayPool<BScanTraceStore.BScanTraceLease?>.Shared.Return(leases, clearArray: true);
        }
    }

    private static void Rasterize(BScanTraceStore.BScanTraceLease?[] leases, int count,
        ConventionalUtFrameMetadata metadata, int bucketSamples, BScanRenderRequest request, int rasterX, int width,
        Span<byte> pixels, CancellationToken token)
    {
        double rate = metadata.Configuration.SampleRateHz;
        double offset = metadata.Configuration.FirstSampleOffsetSeconds;
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
                request.SpatialViewport.OriginAcquisitionIndex) < left)
            { columnStart++; }
            int columnEnd = columnStart;
            while (columnEnd < count && BScanSpatialViewportOperations.RelativeOffset(
                leases[columnEnd]!.Descriptor.AcquisitionIndex,
                request.SpatialViewport.OriginAcquisitionIndex) < right)
            { columnEnd++; }
            for (int y = 0; y < request.PixelHeight; y++)
            {
                double t0 = request.TimeViewport.VisibleMinimumSeconds +
                    request.TimeViewport.VisibleSpanSeconds * y / request.PixelHeight;
                double t1 = request.TimeViewport.VisibleMinimumSeconds +
                    request.TimeViewport.VisibleSpanSeconds * (y + 1) / request.PixelHeight;
                int sampleFirst = Math.Clamp((int)Math.Floor((t0 - offset) * rate), 0, metadata.Configuration.SampleCount);
                int sampleLastExclusive = Math.Clamp((int)Math.Ceiling((t1 - offset) * rate), 0, metadata.Configuration.SampleCount);
                bool found = false;
                short selected = 0;
                int selectedMagnitude = -1;
                for (int column = columnStart; column < columnEnd; column++)
                {
                    BScanTraceStore.BScanTraceLease lease = leases[column]!;
                    SelectRange(lease, bucketSamples, sampleFirst, sampleLastExclusive,
                        ref selected, ref selectedMagnitude, ref found);
                }
                if (found) { pixels[y * width + localX] = BScanBipolarIntensity8.Map(selected); }
            }
        }
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

    private BScanFullRefreshReason GetFullRefreshReason(BScanRenderRequest request, BScanStoreVisualState state)
    {
        BScanVisualSnapshot? current;
        bool coalesced;
        bool recovery;
        lock (_gate) { current = _current; coalesced = _coalesced; recovery = _recoveryRequired; }
        if (request.DisplayedSnapshotVersion is null)
        {
            BScanFullRefreshReason initial = BScanFullRefreshReason.Initial;
            if (coalesced) { initial |= BScanFullRefreshReason.Coalesced; }
            if (recovery) { initial |= BScanFullRefreshReason.Recovery; }
            return initial;
        }
        BScanFullRefreshReason reason = BScanFullRefreshReason.None;
        if (current is null || current.RunId != request.RunId) { reason |= BScanFullRefreshReason.RunChanged; }
        if (request.DisplayedSnapshotVersion != current?.SnapshotVersion) { reason |= BScanFullRefreshReason.SnapshotVersionGap; }
        if (request.DisplayedStoreRevision != current?.StoreRevision) { reason |= BScanFullRefreshReason.BaseRevisionMismatch; }
        if (request.DisplayedWrapGeneration != state.WrapGeneration) { reason |= BScanFullRefreshReason.WrapChanged; }
        if (current is not null && state.ResidentRange is { } resident &&
            (current.ResidentRange.First < resident.First || current.ResidentRange.Last > resident.Last))
        { reason |= BScanFullRefreshReason.DirtyRangeEvicted; }
        if (current is not null && (current.RasterWidth != request.PixelWidth || current.RasterHeight != request.PixelHeight))
        { reason |= BScanFullRefreshReason.ResolutionChanged; }
        if (current is not null &&
            (current.SpatialViewport != request.SpatialViewport || current.TimeViewport != request.TimeViewport))
        { reason |= BScanFullRefreshReason.ViewportChanged; }
        if (current is not null && current.RenderConfigurationVersion != request.RenderConfigurationVersion)
        { reason |= BScanFullRefreshReason.RenderConfigurationChanged; }
        if (coalesced) { reason |= BScanFullRefreshReason.Coalesced; }
        if (recovery) { reason |= BScanFullRefreshReason.Recovery; }
        return reason;
    }

    private async Task PublishLoopAsync()
    {
        try
        {
            while (true)
            {
                await _wake.WaitAsync(_shutdown.Token).ConfigureAwait(false);
                BScanRenderRequest? request;
                long requestGeneration;
                TimeSpan remaining;
                lock (_gate)
                {
                    request = _pending;
                    requestGeneration = _pendingRequestGeneration;
                    remaining = _lastPublication is { } last
                        ? MinimumPublicationInterval - _clock.GetElapsedTime(last, _clock.GetTimestamp()) : TimeSpan.Zero;
                }
                if (remaining > TimeSpan.Zero) { await DelayAsync(remaining, _shutdown.Token).ConfigureAwait(false); }
                lock (_gate)
                {
                    request = _pending;
                    requestGeneration = _pendingRequestGeneration;
                    _pending = null;
                }
                if (request is null) { continue; }
                PublishMetadata(request, requestGeneration);
            }
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { }
        catch (Exception error) { lock (_gate) { _lastError = Message(error); } }
    }

    private void PublishMetadata(BScanRenderRequest request, long requestGeneration)
    {
        BScanTraceStore.BScanTraceLease?[] leases = ArrayPool<BScanTraceStore.BScanTraceLease?>.Shared.Rent(_ingress.VisualCapacity);
        int count = _ingress.CaptureVisualLeases(leases.AsSpan(0, _ingress.VisualCapacity),
            out ConventionalUtFrameMetadata? metadata, out BScanStoreVisualState state);
        try
        {
            if (metadata is null || metadata.RunId != request.RunId || count == 0 || state.ResidentRange is null) { return; }
            Array.Sort(leases, 0, count, BScanLeaseComparer.Instance);
            BScanFullRefreshReason reason = GetFullRefreshReason(request, state);
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
                new(0, 0, request.PixelWidth, request.PixelHeight), full, reason, leases, count);
            BeforeMetadataCandidateValidation?.Invoke();
            if (!_ingress.IsVisualStateCurrent(metadata, state.StoreRevision, state.WrapGeneration))
            { lock (_gate) { _staleCandidates++; _recoveryRequired = true; } return; }
            lock (_gate)
            {
                if (_disposed) { return; }
                if (_requestGeneration != requestGeneration)
                {
                    _staleCandidates++;
                    _recoveryRequired = true;
                    return;
                }
                _current = snapshot;
                _lastPublication = _clock.GetTimestamp();
                _updatesPublished++;
                if (full) { _fullRefreshes++; } else { _incrementalRefreshes++; }
                _coalesced = false;
                _recoveryRequired = false;
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
            dirty, region, discontinuities, full, reason);
    }

    private void OnVisualStateChanged()
    {
        lock (_gate)
        {
            if (_disposed) { return; }
            _columnsIndexed++;
            if (_pending is null) { _pending = _lastRequest; }
            else { _coalesced = true; _updatesCoalesced++; }
            _pendingRequestGeneration = ++_requestGeneration;
            if (_pending is not null && _wake.CurrentCount == 0) { _wake.Release(); }
        }
    }

    private async Task DelayAsync(TimeSpan due, CancellationToken token)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using ITimer timer = _clock.CreateTimer(_ => completion.TrySetResult(), null, due, Timeout.InfiniteTimeSpan);
        await completion.Task.WaitAsync(token).ConfigureAwait(false);
    }

    private void ReleaseOwner(long bytes)
    {
        DiscardOwner(bytes);
        _ownerQuota.Release();
    }

    private void DiscardOwner(long bytes)
    {
        lock (_gate) { _activeOwnerBytes -= bytes; _activeOwners--; }
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
            if (_disposal is null)
            {
                _disposed = true;
                _ingress.VisualStateChanged -= OnVisualStateChanged;
                _pending = null;
                _lastRequest = null;
                Subscription[] subscriptions = [.. _subscriptions];
                _subscriptions.Clear();
                Task regions = _regionOperations == 0 ? Task.CompletedTask :
                    (_regionDrain ??= new(TaskCreationOptions.RunContinuationsAsynchronously)).Task;
                _disposal = DisposeCoreAsync(subscriptions, regions);
            }
            return new(_disposal);
        }
    }

    private async Task DisposeCoreAsync(Subscription[] subscriptions, Task regions)
    {
        await _shutdown.CancelAsync().ConfigureAwait(false);
        await Task.WhenAll(subscriptions.Select(subscription => subscription.Detach())).ConfigureAwait(false);
        try { await _worker.ConfigureAwait(false); } catch (OperationCanceledException) { }
        await regions.ConfigureAwait(false);
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
