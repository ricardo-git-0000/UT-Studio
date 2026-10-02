using System.Buffers;
using System.Numerics;

namespace UTStudio.Visualization.Core.Scan2D;

public interface IScan2DResampler
{
    Scan2DRenderResult Render(Scan2DSourceGrid source, Scan2DRenderRequest request,
        IMemoryOwner<byte> destinationOwner, CancellationToken cancellationToken = default);
}

internal interface ILanczosTablePool
{
    int GetMaximumIndicesLength(int minimumLength) => minimumLength;
    int GetMaximumWeightsLength(int minimumLength) => minimumLength;
    int[] RentIndices(int minimumLength);
    double[] RentWeights(int minimumLength);
    void Return(int[] array);
    void Return(double[] array);
}

internal sealed class SharedLanczosTablePool : ILanczosTablePool
{
    internal static SharedLanczosTablePool Instance { get; } = new();
    public int GetMaximumIndicesLength(int minimumLength) => GetMaximumLength(minimumLength);
    public int GetMaximumWeightsLength(int minimumLength) => GetMaximumLength(minimumLength);
    public int[] RentIndices(int minimumLength) => ArrayPool<int>.Shared.Rent(minimumLength);
    public double[] RentWeights(int minimumLength) => ArrayPool<double>.Shared.Rent(minimumLength);
    public void Return(int[] array) => ArrayPool<int>.Shared.Return(array);
    public void Return(double[] array) => ArrayPool<double>.Shared.Return(array);
    private static int GetMaximumLength(int minimumLength) => Math.Max(16,
        checked((int)BitOperations.RoundUpToPowerOf2((uint)Math.Max(1, minimumLength))));
}

internal readonly record struct LanczosTableMemory(long LogicalBytes, long RentedBytes);

public sealed class Scan2DResampler : IScan2DResampler, IDisposable
{
    private const int LanczosTapCount = 6;
    private const int LanczosFirstTapOffset = -2;
    private const int LanczosWeightStride = LanczosTapCount + 1;
    private readonly ILanczosTablePool _lanczosTablePool;
    private readonly Action<long>? _tableBytesChanged;
    private readonly bool _reuseTables;
    private readonly object _tableGate = new();
    private CachedTableEntry? _cachedTables;
    private bool _disposed;
    private long _tablePreparationCount;
    internal Action<int>? AfterKernelRowProcessed { get; set; }
    internal Action<int, bool>? AfterLanczosCoordinatePrepared { get; set; }
    internal LanczosTableMemory LastLanczosTableMemory { get; private set; }
    internal long TablePreparationCount => Interlocked.Read(ref _tablePreparationCount);

    public Scan2DResampler() : this(SharedLanczosTablePool.Instance, null, false) { }

    internal Scan2DResampler(ILanczosTablePool lanczosTablePool, Action<long>? tableBytesChanged = null,
        bool reuseTables = false)
    {
        _lanczosTablePool = lanczosTablePool ?? throw new ArgumentNullException(nameof(lanczosTablePool));
        _tableBytesChanged = tableBytesChanged;
        _reuseTables = reuseTables;
    }

    public Scan2DRenderResult Render(Scan2DSourceGrid source, Scan2DRenderRequest request,
        IMemoryOwner<byte> destinationOwner, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return RenderCore(source, request, destinationOwner, cancellationToken);
    }

    private Scan2DRenderResult RenderCore(Scan2DSourceGrid source, Scan2DRenderRequest request,
        IMemoryOwner<byte> destinationOwner, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(destinationOwner);
        if (source.Width != request.SourceWidth || source.Height != request.SourceHeight ||
            source.Geometry != request.SourceGeometry)
        { destinationOwner.Dispose(); throw new ArgumentException("The request source geometry does not match the source grid.", nameof(request)); }

        int cells = checked(request.DestinationWidth * request.DestinationHeight);
        if (destinationOwner.Memory.Length < checked(cells * 3))
        { destinationOwner.Dispose(); throw new ArgumentException("The destination owner is too small.", nameof(destinationOwner)); }
        var result = new Scan2DRenderResult(destinationOwner, request.DestinationWidth,
            request.DestinationHeight, request.DestinationWidth, request.Viewport,
            request.Viewport == new Scan2DRectangle(0, 0, request.DestinationWidth, request.DestinationHeight),
            Scan2DRenderDiagnostic.Completed);
        result.Intensities.Span.Clear(); result.ValidityMask.Span.Clear(); result.Provenance.Span.Clear();
        try
        {
            switch (request.Options.Mode)
            {
                case Scan2DInterpolationMode.None:
                    CopyIdentity(source, request, result, cancellationToken);
                    break;
                case Scan2DInterpolationMode.NearestNeighbor:
                    RenderNearest(source, request, result, cancellationToken);
                    break;
                case Scan2DInterpolationMode.Bilinear:
                case Scan2DInterpolationMode.Bicubic:
                    RenderKernel(source, request, result, cancellationToken);
                    break;
                case Scan2DInterpolationMode.Lanczos:
                    RenderLanczos(source, request, result, cancellationToken);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(request), "Unsupported interpolation mode.");
            }
            return result;
        }
        catch { result.Dispose(); throw; }
    }

    private static void CopyIdentity(Scan2DSourceGrid source, Scan2DRenderRequest request,
        Scan2DRenderResult result, CancellationToken token)
    {
        if (source.Width != request.DestinationWidth || source.Height != request.DestinationHeight ||
            source.Geometry != request.DestinationGeometry)
        { throw new ArgumentException("None requires identical source and destination grids.", nameof(request)); }
        for (int y = request.Viewport.Y; y < checked(request.Viewport.Y + request.Viewport.Height); y++)
        {
            token.ThrowIfCancellationRequested();
            int sourceOffset = checked(y * source.Stride + request.Viewport.X);
            int destinationOffset = checked(y * result.Stride + request.Viewport.X);
            int count = request.Viewport.Width;
            source.Intensities.Span.Slice(sourceOffset, count).CopyTo(result.Intensities.Span[destinationOffset..]);
            source.ValidityMask.Span.Slice(sourceOffset, count).CopyTo(result.ValidityMask.Span[destinationOffset..]);
            for (int x = 0; x < count; x++)
                result.Provenance.Span[destinationOffset + x] = source.Provenance.Span[sourceOffset + x];
        }
    }

    private void RenderNearest(Scan2DSourceGrid source, Scan2DRenderRequest request,
        Scan2DRenderResult result, CancellationToken token)
    {
        Scan2DRectangle region = request.Viewport;
        if (_reuseTables)
        {
            using TableLease lease = AcquireTables(source, request, 1, token);
            RenderNearestPrepared(source, request, result, lease.KernelTables, token);
            return;
        }
        KernelTables? ownedTables = null;
        KernelTables tables = GetKernelTables(source, request, 1, ref ownedTables, out bool prepare);
        try
        {
            if (prepare)
            {
                PrepareNearestAxis(region.X, region.Width, request.DestinationGeometry.FirstCenterX,
                    request.DestinationGeometry.CenterStepX, source.Geometry.FirstCenterX,
                    source.Geometry.CenterStepX, source.Width, tables.XIndices, tables.XWeights, token);
                PrepareNearestAxis(region.Y, region.Height, request.DestinationGeometry.FirstCenterY,
                    request.DestinationGeometry.CenterStepY, source.Geometry.FirstCenterY,
                    source.Geometry.CenterStepY, source.Height, tables.YIndices, tables.YWeights, token);
            }
            RenderNearestPrepared(source, request, result, tables, token);
        }
        catch { throw; }
        finally { ownedTables?.Dispose(); }
    }

    private static void RenderNearestPrepared(Scan2DSourceGrid source, Scan2DRenderRequest request,
        Scan2DRenderResult result, KernelTables tables, CancellationToken token)
    {
        Scan2DRectangle region = request.Viewport;
        ReadOnlySpan<byte> sourceIntensities = source.Intensities.Span;
        ReadOnlySpan<byte> sourceValidity = source.ValidityMask.Span;
        ReadOnlySpan<byte> sourceProvenance = source.Provenance.Span;
        Span<byte> destinationIntensities = result.Intensities.Span;
        Span<byte> destinationValidity = result.ValidityMask.Span;
        Span<byte> destinationProvenance = result.Provenance.Span;
        for (int localY = 0; localY < region.Height; localY++)
        {
            token.ThrowIfCancellationRequested();
            int y = checked(region.Y + localY);
            int sourceY = tables.YIndices[localY];
            double coordinateY = tables.YWeights[checked(localY * 2 + 1)];
            for (int localX = 0; localX < region.Width; localX++)
            {
                int x = checked(region.X + localX);
                int sourceX = tables.XIndices[localX];
                double coordinateX = tables.XWeights[checked(localX * 2 + 1)];
                int sourceIndex = checked(sourceY * source.Stride + sourceX);
                if (sourceValidity[sourceIndex] == 0) { continue; }
                int destinationIndex = checked(y * result.Stride + x);
                destinationIntensities[destinationIndex] = sourceIntensities[sourceIndex];
                destinationValidity[destinationIndex] = 1;
                destinationProvenance[destinationIndex] = IsExactSourceCenter(coordinateX, source.Width) &&
                    IsExactSourceCenter(coordinateY, source.Height) ? sourceProvenance[sourceIndex] :
                    (byte)Scan2DSampleProvenance.VisualDerived;
            }
        }
    }

    private static void PrepareNearestAxis(int firstDestination, int destinationCount,
        double firstDestinationCenter, double destinationStep, double firstSourceCenter,
        double sourceStep, int sourceLength, int[] indices, double[] coordinates,
        CancellationToken token)
    {
        for (int local = 0; local < destinationCount; local++)
        {
            token.ThrowIfCancellationRequested();
            int destination = checked(firstDestination + local);
            double destinationCenter = firstDestinationCenter + destination * destinationStep;
            indices[local] = NearestIndex(destinationCenter, firstSourceCenter, sourceStep, sourceLength);
            coordinates[checked(local * 2 + 1)] = SourceCoordinate(destinationCenter, firstSourceCenter, sourceStep);
        }
    }

    private void RenderKernel(Scan2DSourceGrid source, Scan2DRenderRequest request,
        Scan2DRenderResult result, CancellationToken token)
    {
        Scan2DInterpolationMode mode = request.Options.Mode;
        int tapCount = mode switch
        {
            Scan2DInterpolationMode.Bilinear => 2,
            Scan2DInterpolationMode.Bicubic => 4,
            Scan2DInterpolationMode.Lanczos => 6,
            _ => throw new ArgumentOutOfRangeException(nameof(request), "Unsupported kernel mode.")
        };
        int firstTapOffset = mode switch
        {
            Scan2DInterpolationMode.Bilinear => 0,
            Scan2DInterpolationMode.Bicubic => -1,
            Scan2DInterpolationMode.Lanczos => -2,
            _ => throw new ArgumentOutOfRangeException(nameof(request), "Unsupported kernel mode.")
        };
        Scan2DRectangle region = request.Viewport;
        if (_reuseTables)
        {
            using TableLease lease = AcquireTables(source, request, tapCount, token);
            KernelTables cached = lease.KernelTables;
            RenderPreparedKernel(source, request, result, tapCount, cached.XIndices, cached.XWeights,
                cached.YIndices, cached.YWeights, token);
            return;
        }
        KernelTables? ownedTables = null;
        KernelTables tables = GetKernelTables(source, request, tapCount, ref ownedTables, out bool prepare);
        try
        {
            if (prepare)
            {
                PrepareKernelAxis(region.X, region.Width, request.DestinationGeometry.FirstCenterX,
                    request.DestinationGeometry.CenterStepX, source.Geometry.FirstCenterX,
                    source.Geometry.CenterStepX, source.Width, tapCount, firstTapOffset, mode,
                    request.Options, tables.XIndices, tables.XWeights, token);
                PrepareKernelAxis(region.Y, region.Height, request.DestinationGeometry.FirstCenterY,
                    request.DestinationGeometry.CenterStepY, source.Geometry.FirstCenterY,
                    source.Geometry.CenterStepY, source.Height, tapCount, firstTapOffset, mode,
                    request.Options, tables.YIndices, tables.YWeights, token);
            }
            RenderPreparedKernel(source, request, result, tapCount, tables.XIndices, tables.XWeights,
                tables.YIndices, tables.YWeights, token);
        }
        catch { throw; }
        finally { ownedTables?.Dispose(); }
    }

    private static void PrepareKernelAxis(int firstDestination, int destinationCount,
        double firstDestinationCenter, double destinationStep, double firstSourceCenter,
        double sourceStep, int sourceLength, int tapCount, int firstTapOffset,
        Scan2DInterpolationMode mode, Scan2DResamplingOptions options, int[] indices,
        double[] weights, CancellationToken token)
    {
        int weightStride = checked(tapCount + 1);
        for (int local = 0; local < destinationCount; local++)
        {
            token.ThrowIfCancellationRequested();
            int destination = checked(firstDestination + local);
            double coordinate = SourceCoordinate(firstDestinationCenter + destination * destinationStep,
                firstSourceCenter, sourceStep);
            int baseIndex = checked((int)Math.Floor(coordinate));
            double fraction = coordinate - baseIndex;
            int indexOffset = checked(local * tapCount);
            int weightOffset = checked(local * weightStride);
            for (int tap = 0; tap < tapCount; tap++)
            {
                int offset = checked(firstTapOffset + tap);
                indices[indexOffset + tap] = Math.Clamp(checked(baseIndex + offset), 0, sourceLength - 1);
                weights[weightOffset + tap] = KernelWeight(mode, fraction - offset, options);
            }
            weights[weightOffset + tapCount] = coordinate;
        }
    }

    private void RenderPreparedKernel(Scan2DSourceGrid source, Scan2DRenderRequest request,
        Scan2DRenderResult result, int tapCount, int[] xIndices, double[] xWeights,
        int[] yIndices, double[] yWeights, CancellationToken token)
    {
        Scan2DRectangle region = request.Viewport;
        int weightStride = checked(tapCount + 1);
        ReadOnlySpan<byte> sourceIntensities = source.Intensities.Span;
        ReadOnlySpan<byte> sourceValidity = source.ValidityMask.Span;
        ReadOnlySpan<byte> sourceSegments = source.SegmentIds.Span;
        ReadOnlySpan<byte> sourceProvenance = source.Provenance.Span;
        Span<byte> destinationIntensities = result.Intensities.Span;
        Span<byte> destinationValidity = result.ValidityMask.Span;
        Span<byte> destinationProvenance = result.Provenance.Span;
        for (int localY = 0; localY < region.Height; localY++)
        {
            token.ThrowIfCancellationRequested();
            int y = checked(region.Y + localY);
            int yIndexOffset = checked(localY * tapCount);
            int yWeightOffset = checked(localY * weightStride);
            double sourceY = yWeights[yWeightOffset + tapCount];
            for (int localX = 0; localX < region.Width; localX++)
            {
                int x = checked(region.X + localX);
                int xIndexOffset = checked(localX * tapCount);
                int xWeightOffset = checked(localX * weightStride);
                double sourceX = xWeights[xWeightOffset + tapCount];
                double accumulated = 0;
                byte segment = 0;
                bool valid = true;
                for (int tapY = 0; tapY < tapCount && valid; tapY++)
                {
                    int sampleY = yIndices[yIndexOffset + tapY];
                    double weightY = yWeights[yWeightOffset + tapY];
                    for (int tapX = 0; tapX < tapCount; tapX++)
                    {
                        int sampleX = xIndices[xIndexOffset + tapX];
                        int sourceIndex = checked(sampleY * source.Stride + sampleX);
                        if (sourceValidity[sourceIndex] == 0)
                        { valid = false; break; }
                        byte tapSegment = sourceSegments[sourceIndex];
                        if (segment == 0) { segment = tapSegment; }
                        else if (tapSegment != segment) { valid = false; break; }
                        double weight = weightY * xWeights[xWeightOffset + tapX];
                        accumulated += sourceIntensities[sourceIndex] * weight;
                    }
                }
                if (!valid) { continue; }
                int destinationIndex = checked(y * result.Stride + x);
                destinationIntensities[destinationIndex] = RoundIntensity(accumulated);
                destinationValidity[destinationIndex] = 1;
                if (IsExactSourceCenter(sourceX, source.Width) && IsExactSourceCenter(sourceY, source.Height))
                {
                    int exactX = Math.Clamp(checked((int)sourceX), 0, source.Width - 1);
                    int exactY = Math.Clamp(checked((int)sourceY), 0, source.Height - 1);
                    destinationProvenance[destinationIndex] = sourceProvenance[checked(exactY * source.Stride + exactX)];
                }
                else { destinationProvenance[destinationIndex] = (byte)Scan2DSampleProvenance.VisualDerived; }
            }
            AfterKernelRowProcessed?.Invoke(y);
        }
    }

    private KernelTables GetKernelTables(Scan2DSourceGrid source, Scan2DRenderRequest request,
        int tapCount, ref KernelTables? ownedTables, out bool prepare)
    {
        prepare = true;
        return ownedTables = new KernelTables(_lanczosTablePool, _tableBytesChanged,
            request.Viewport.Width, request.Viewport.Height, tapCount);
    }

    private void RenderLanczos(Scan2DSourceGrid source, Scan2DRenderRequest request,
        Scan2DRenderResult result, CancellationToken token)
    {
        if (_reuseTables)
        {
            using TableLease lease = AcquireTables(source, request, LanczosTapCount, token);
            RenderLanczosPrepared(source, request, result, lease.LanczosTables, token);
            return;
        }
        using LanczosTables tables = PrepareLanczosTables(source, request, token);
        RenderLanczosPrepared(source, request, result, tables, token);
    }

    private TableLease AcquireTables(Scan2DSourceGrid source, Scan2DRenderRequest request,
        int tapCount, CancellationToken token)
    {
        TableKey key = TableKey.Create(source, request, tapCount);
        lock (_tableGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_cachedTables is { } existing && existing.Key == key)
            {
                existing.Users++;
                return new TableLease(this, existing);
            }

            CachedTableEntry created = CreateTableEntry(source, request, tapCount, key, token);
            if (_cachedTables is { } previous)
            {
                previous.Retired = true;
                if (previous.Users == 0) { previous.Dispose(); }
            }
            _cachedTables = created;
            created.Users = 1;
            return new TableLease(this, created);
        }
    }

    private CachedTableEntry CreateTableEntry(Scan2DSourceGrid source, Scan2DRenderRequest request,
        int tapCount, TableKey key, CancellationToken token)
    {
        if (request.Options.Mode == Scan2DInterpolationMode.Lanczos)
        {
            LanczosTables lanczos = PrepareLanczosTables(source, request, token);
            Interlocked.Increment(ref _tablePreparationCount);
            return new(key, lanczos);
        }

        Scan2DRectangle region = request.Viewport;
        var tables = new KernelTables(_lanczosTablePool, _tableBytesChanged,
            region.Width, region.Height, tapCount);
        try
        {
            if (request.Options.Mode == Scan2DInterpolationMode.NearestNeighbor)
            {
                PrepareNearestAxis(region.X, region.Width, request.DestinationGeometry.FirstCenterX,
                    request.DestinationGeometry.CenterStepX, source.Geometry.FirstCenterX,
                    source.Geometry.CenterStepX, source.Width, tables.XIndices, tables.XWeights, token);
                PrepareNearestAxis(region.Y, region.Height, request.DestinationGeometry.FirstCenterY,
                    request.DestinationGeometry.CenterStepY, source.Geometry.FirstCenterY,
                    source.Geometry.CenterStepY, source.Height, tables.YIndices, tables.YWeights, token);
            }
            else
            {
                int firstTapOffset = request.Options.Mode == Scan2DInterpolationMode.Bilinear ? 0 : -1;
                PrepareKernelAxis(region.X, region.Width, request.DestinationGeometry.FirstCenterX,
                    request.DestinationGeometry.CenterStepX, source.Geometry.FirstCenterX,
                    source.Geometry.CenterStepX, source.Width, tapCount, firstTapOffset,
                    request.Options.Mode, request.Options, tables.XIndices, tables.XWeights, token);
                PrepareKernelAxis(region.Y, region.Height, request.DestinationGeometry.FirstCenterY,
                    request.DestinationGeometry.CenterStepY, source.Geometry.FirstCenterY,
                    source.Geometry.CenterStepY, source.Height, tapCount, firstTapOffset,
                    request.Options.Mode, request.Options, tables.YIndices, tables.YWeights, token);
            }
            Interlocked.Increment(ref _tablePreparationCount);
            return new(key, tables);
        }
        catch { tables.Dispose(); throw; }
    }

    public void Dispose()
    {
        lock (_tableGate)
        {
            if (_disposed) { return; }
            _disposed = true;
            if (_cachedTables is { } cached)
            {
                cached.Retired = true;
                if (cached.Users == 0) { cached.Dispose(); }
                _cachedTables = null;
            }
        }
    }

    private void Release(CachedTableEntry entry)
    {
        lock (_tableGate)
        {
            entry.Users--;
            if (entry.Users == 0 && entry.Retired) { entry.Dispose(); }
        }
    }

    private readonly record struct TableKey(
        Scan2DInterpolationMode Mode, Scan2DResamplingOptions Options, int SourceWidth, int SourceHeight,
        int DestinationWidth, int DestinationHeight, int ViewportWidth, int ViewportHeight, int TapCount,
        double FirstSourceX, double FirstSourceY, double SourceStepX, double SourceStepY)
    {
        internal static TableKey Create(Scan2DSourceGrid source, Scan2DRenderRequest request, int tapCount)
        {
            Scan2DRectangle region = request.Viewport;
            double firstX = SourceCoordinate(request.DestinationGeometry.FirstCenterX +
                region.X * request.DestinationGeometry.CenterStepX,
                source.Geometry.FirstCenterX, source.Geometry.CenterStepX);
            double firstY = SourceCoordinate(request.DestinationGeometry.FirstCenterY +
                region.Y * request.DestinationGeometry.CenterStepY,
                source.Geometry.FirstCenterY, source.Geometry.CenterStepY);
            return new(request.Options.Mode, request.Options, source.Width, source.Height,
                request.DestinationWidth, request.DestinationHeight, region.Width, region.Height, tapCount,
                firstX, firstY,
                request.DestinationGeometry.CenterStepX / source.Geometry.CenterStepX,
                request.DestinationGeometry.CenterStepY / source.Geometry.CenterStepY);
        }
    }

    private sealed class CachedTableEntry : IDisposable
    {
        internal CachedTableEntry(TableKey key, KernelTables tables)
        { Key = key; KernelTables = tables; }

        internal CachedTableEntry(TableKey key, LanczosTables tables)
        { Key = key; LanczosTables = tables; }

        internal TableKey Key { get; }
        internal KernelTables? KernelTables { get; }
        internal LanczosTables? LanczosTables { get; }
        internal int Users { get; set; }
        internal bool Retired { get; set; }

        public void Dispose()
        {
            KernelTables?.Dispose();
            LanczosTables?.Dispose();
        }
    }

    private sealed class TableLease : IDisposable
    {
        private Scan2DResampler? _owner;
        private CachedTableEntry? _entry;

        internal TableLease(Scan2DResampler owner, CachedTableEntry entry)
        { _owner = owner; _entry = entry; }

        internal KernelTables KernelTables => _entry?.KernelTables ??
            throw new InvalidOperationException("The cached entry does not contain kernel tables.");
        internal LanczosTables LanczosTables => _entry?.LanczosTables ??
            throw new InvalidOperationException("The cached entry does not contain Lanczos tables.");

        public void Dispose()
        {
            CachedTableEntry? entry = Interlocked.Exchange(ref _entry, null);
            Scan2DResampler? owner = Interlocked.Exchange(ref _owner, null);
            if (entry is not null && owner is not null) { owner.Release(entry); }
        }
    }

    internal LanczosTables PrepareLanczosTables(Scan2DSourceGrid source, Scan2DRenderRequest request,
        CancellationToken token = default)
    {
        Scan2DRectangle region = request.Viewport;
        var tables = new LanczosTables(_lanczosTablePool, _tableBytesChanged, region.Width, region.Height);
        LastLanczosTableMemory = tables.Memory;
        try
        {
            PrepareLanczosAxis(region.X, region.Width, request.DestinationGeometry.FirstCenterX,
                request.DestinationGeometry.CenterStepX, source.Geometry.FirstCenterX,
                source.Geometry.CenterStepX, source.Width, tables.XIndices, tables.XWeights, false, token);
            PrepareLanczosAxis(region.Y, region.Height, request.DestinationGeometry.FirstCenterY,
                request.DestinationGeometry.CenterStepY, source.Geometry.FirstCenterY,
                source.Geometry.CenterStepY, source.Height, tables.YIndices, tables.YWeights, true, token);
            return tables;
        }
        catch { tables.Dispose(); throw; }
    }

    private void PrepareLanczosAxis(int firstDestination, int destinationCount, double firstDestinationCenter,
        double destinationStep, double firstSourceCenter, double sourceStep, int sourceLength,
        int[] indices, double[] weights, bool yAxis, CancellationToken token)
    {
        for (int local = 0; local < destinationCount; local++)
        {
            token.ThrowIfCancellationRequested();
            int destination = checked(firstDestination + local);
            double coordinate = SourceCoordinate(firstDestinationCenter + destination * destinationStep,
                firstSourceCenter, sourceStep);
            int baseIndex = checked((int)Math.Floor(coordinate));
            double fraction = coordinate - baseIndex;
            int indexOffset = checked(local * LanczosTapCount);
            int weightOffset = checked(local * LanczosWeightStride);
            for (int tap = 0; tap < LanczosTapCount; tap++)
            {
                int offset = LanczosFirstTapOffset + tap;
                indices[indexOffset + tap] = Math.Clamp(checked(baseIndex + offset), 0, sourceLength - 1);
                weights[weightOffset + tap] = LanczosWeight(fraction - offset);
            }
            weights[weightOffset + LanczosTapCount] = coordinate;
            AfterLanczosCoordinatePrepared?.Invoke(destination, yAxis);
        }
    }

    internal void RenderLanczosPrepared(Scan2DSourceGrid source, Scan2DRenderRequest request,
        Scan2DRenderResult result, LanczosTables tables, CancellationToken token = default)
    {
        Scan2DRectangle region = request.Viewport;
        for (int localY = 0; localY < region.Height; localY++)
        {
            token.ThrowIfCancellationRequested();
            int y = checked(region.Y + localY);
            int yIndexOffset = checked(localY * LanczosTapCount);
            int yWeightOffset = checked(localY * LanczosWeightStride);
            double sourceY = tables.YWeights[yWeightOffset + LanczosTapCount];
            for (int localX = 0; localX < region.Width; localX++)
            {
                int x = checked(region.X + localX);
                int xIndexOffset = checked(localX * LanczosTapCount);
                int xWeightOffset = checked(localX * LanczosWeightStride);
                double sourceX = tables.XWeights[xWeightOffset + LanczosTapCount];
                double accumulated = 0;
                double combinedWeight = 0;
                byte segment = 0;
                bool valid = true;
                for (int tapY = 0; tapY < LanczosTapCount && valid; tapY++)
                {
                    int sampleY = tables.YIndices[yIndexOffset + tapY];
                    double weightY = tables.YWeights[yWeightOffset + tapY];
                    for (int tapX = 0; tapX < LanczosTapCount; tapX++)
                    {
                        int sampleX = tables.XIndices[xIndexOffset + tapX];
                        int sourceIndex = checked(sampleY * source.Stride + sampleX);
                        if (source.ValidityMask.Span[sourceIndex] == 0)
                        { valid = false; break; }
                        byte tapSegment = source.SegmentIds.Span[sourceIndex];
                        if (segment == 0) { segment = tapSegment; }
                        else if (tapSegment != segment) { valid = false; break; }
                        double weightX = tables.XWeights[xWeightOffset + tapX];
                        double weight = weightY * weightX;
                        accumulated += source.Intensities.Span[sourceIndex] * weight;
                        combinedWeight += weight;
                    }
                }
                if (!valid) { continue; }
                if (!double.IsFinite(combinedWeight) || Math.Abs(combinedWeight) < LanczosCoincidenceTolerance)
                { throw new InvalidOperationException("Lanczos normalization weight is zero."); }
                accumulated /= combinedWeight;
                int destinationIndex = checked(y * result.Stride + x);
                result.Intensities.Span[destinationIndex] = RoundIntensity(accumulated);
                result.ValidityMask.Span[destinationIndex] = 1;
                if (IsExactSourceCenter(sourceX, source.Width) && IsExactSourceCenter(sourceY, source.Height))
                {
                    int exactX = Math.Clamp(checked((int)sourceX), 0, source.Width - 1);
                    int exactY = Math.Clamp(checked((int)sourceY), 0, source.Height - 1);
                    result.Provenance.Span[destinationIndex] =
                        source.Provenance.Span[checked(exactY * source.Stride + exactX)];
                }
                else { result.Provenance.Span[destinationIndex] = (byte)Scan2DSampleProvenance.VisualDerived; }
            }
            AfterKernelRowProcessed?.Invoke(y);
        }
    }

    internal sealed class LanczosTables : IDisposable
    {
        private readonly ILanczosTablePool _pool;
        private readonly Action<long>? _bytesChanged;
        private int[]? _xIndices;
        private int[]? _yIndices;
        private double[]? _xWeights;
        private double[]? _yWeights;

        internal LanczosTables(ILanczosTablePool pool, Action<long>? bytesChanged, int width, int height)
        {
            _pool = pool;
            _bytesChanged = bytesChanged;
            long logical = checked((long)(width + height) *
                (LanczosTapCount * sizeof(int) + LanczosWeightStride * sizeof(double)));
            try
            {
                _xIndices = RentIndices(checked(width * LanczosTapCount));
                _xWeights = RentWeights(checked(width * LanczosWeightStride));
                _yIndices = RentIndices(checked(height * LanczosTapCount));
                _yWeights = RentWeights(checked(height * LanczosWeightStride));
                Memory = new(logical, RentedBytes);
            }
            catch { Dispose(); throw; }
        }

        internal int[] XIndices => _xIndices ?? throw new ObjectDisposedException(nameof(LanczosTables));
        internal int[] YIndices => _yIndices ?? throw new ObjectDisposedException(nameof(LanczosTables));
        internal double[] XWeights => _xWeights ?? throw new ObjectDisposedException(nameof(LanczosTables));
        internal double[] YWeights => _yWeights ?? throw new ObjectDisposedException(nameof(LanczosTables));
        internal LanczosTableMemory Memory { get; private set; }
        private long RentedBytes { get; set; }

        private int[] RentIndices(int length)
        {
            int reservedLength = _pool.GetMaximumIndicesLength(length);
            if (reservedLength < length) { throw new InvalidOperationException("Lanczos pool returned an invalid indices quote."); }
            long reserved = checked((long)reservedLength * sizeof(int));
            _bytesChanged?.Invoke(reserved);
            int[]? array = null;
            try
            {
                array = _pool.RentIndices(length);
                if (array.Length < length || array.Length > reservedLength)
                { throw new InvalidOperationException("Lanczos pool violated its indices reservation quote."); }
                long actual = checked((long)array.Length * sizeof(int));
                _bytesChanged?.Invoke(checked(actual - reserved));
                RentedBytes = checked(RentedBytes + actual);
                return array;
            }
            catch
            {
                if (array is not null) { _pool.Return(array); }
                _bytesChanged?.Invoke(-reserved);
                throw;
            }
        }

        private double[] RentWeights(int length)
        {
            int reservedLength = _pool.GetMaximumWeightsLength(length);
            if (reservedLength < length) { throw new InvalidOperationException("Lanczos pool returned an invalid weights quote."); }
            long reserved = checked((long)reservedLength * sizeof(double));
            _bytesChanged?.Invoke(reserved);
            double[]? array = null;
            try
            {
                array = _pool.RentWeights(length);
                if (array.Length < length || array.Length > reservedLength)
                { throw new InvalidOperationException("Lanczos pool violated its weights reservation quote."); }
                long actual = checked((long)array.Length * sizeof(double));
                _bytesChanged?.Invoke(checked(actual - reserved));
                RentedBytes = checked(RentedBytes + actual);
                return array;
            }
            catch
            {
                if (array is not null) { _pool.Return(array); }
                _bytesChanged?.Invoke(-reserved);
                throw;
            }
        }

        public void Dispose()
        {
            if (_yWeights is { } yWeights) { _yWeights = null; _pool.Return(yWeights); }
            if (_yIndices is { } yIndices) { _yIndices = null; _pool.Return(yIndices); }
            if (_xWeights is { } xWeights) { _xWeights = null; _pool.Return(xWeights); }
            if (_xIndices is { } xIndices) { _xIndices = null; _pool.Return(xIndices); }
            if (RentedBytes != 0) { _bytesChanged?.Invoke(-RentedBytes); RentedBytes = 0; }
        }
    }

    private sealed class KernelTables : IDisposable
    {
        private readonly ILanczosTablePool _pool;
        private readonly Action<long>? _bytesChanged;
        private int[]? _xIndices;
        private int[]? _yIndices;
        private double[]? _xWeights;
        private double[]? _yWeights;
        private long _rentedBytes;

        internal KernelTables(ILanczosTablePool pool, Action<long>? bytesChanged,
            int width, int height, int tapCount)
        {
            _pool = pool;
            _bytesChanged = bytesChanged;
            int weightStride = checked(tapCount + 1);
            try
            {
                _xIndices = RentIndices(checked(width * tapCount));
                _xWeights = RentWeights(checked(width * weightStride));
                _yIndices = RentIndices(checked(height * tapCount));
                _yWeights = RentWeights(checked(height * weightStride));
            }
            catch { Dispose(); throw; }
        }

        internal int[] XIndices => _xIndices ?? throw new ObjectDisposedException(nameof(KernelTables));
        internal int[] YIndices => _yIndices ?? throw new ObjectDisposedException(nameof(KernelTables));
        internal double[] XWeights => _xWeights ?? throw new ObjectDisposedException(nameof(KernelTables));
        internal double[] YWeights => _yWeights ?? throw new ObjectDisposedException(nameof(KernelTables));

        private int[] RentIndices(int length)
        {
            int reservedLength = _pool.GetMaximumIndicesLength(length);
            if (reservedLength < length) { throw new InvalidOperationException("Lanczos pool returned an invalid indices quote."); }
            long reserved = checked((long)reservedLength * sizeof(int));
            _bytesChanged?.Invoke(reserved);
            int[]? array = null;
            try
            {
                array = _pool.RentIndices(length);
                if (array.Length < length || array.Length > reservedLength)
                { throw new InvalidOperationException("Lanczos pool violated its indices reservation quote."); }
                long actual = checked((long)array.Length * sizeof(int));
                _bytesChanged?.Invoke(checked(actual - reserved));
                _rentedBytes = checked(_rentedBytes + actual);
                return array;
            }
            catch
            {
                if (array is not null) { _pool.Return(array); }
                _bytesChanged?.Invoke(-reserved);
                throw;
            }
        }

        private double[] RentWeights(int length)
        {
            int reservedLength = _pool.GetMaximumWeightsLength(length);
            if (reservedLength < length) { throw new InvalidOperationException("Lanczos pool returned an invalid weights quote."); }
            long reserved = checked((long)reservedLength * sizeof(double));
            _bytesChanged?.Invoke(reserved);
            double[]? array = null;
            try
            {
                array = _pool.RentWeights(length);
                if (array.Length < length || array.Length > reservedLength)
                { throw new InvalidOperationException("Lanczos pool violated its weights reservation quote."); }
                long actual = checked((long)array.Length * sizeof(double));
                _bytesChanged?.Invoke(checked(actual - reserved));
                _rentedBytes = checked(_rentedBytes + actual);
                return array;
            }
            catch
            {
                if (array is not null) { _pool.Return(array); }
                _bytesChanged?.Invoke(-reserved);
                throw;
            }
        }

        public void Dispose()
        {
            if (_yWeights is { } yWeights) { _yWeights = null; _pool.Return(yWeights); }
            if (_yIndices is { } yIndices) { _yIndices = null; _pool.Return(yIndices); }
            if (_xWeights is { } xWeights) { _xWeights = null; _pool.Return(xWeights); }
            if (_xIndices is { } xIndices) { _xIndices = null; _pool.Return(xIndices); }
            if (_rentedBytes != 0) { _bytesChanged?.Invoke(-_rentedBytes); _rentedBytes = 0; }
        }
    }

    private static double SourceCoordinate(double destinationCenter, double firstSourceCenter, double sourceStep) =>
        (destinationCenter - firstSourceCenter) / sourceStep;

    private static double LinearWeight(double distance)
    {
        double absolute = Math.Abs(distance);
        return absolute < 1 ? 1 - absolute : 0;
    }

    private static double KeysWeight(double distance, double a)
    {
        double absolute = Math.Abs(distance);
        if (absolute < 1)
        { return ((a + 2) * absolute - (a + 3)) * absolute * absolute + 1; }
        if (absolute < 2)
        { return ((a * absolute - 5 * a) * absolute + 8 * a) * absolute - 4 * a; }
        return 0;
    }

    private static double KernelWeight(Scan2DInterpolationMode mode, double distance,
        Scan2DResamplingOptions options) => mode switch
        {
            Scan2DInterpolationMode.Bilinear => LinearWeight(distance),
            Scan2DInterpolationMode.Bicubic => KeysWeight(distance, options.CubicA),
            Scan2DInterpolationMode.Lanczos => LanczosWeight(distance),
            _ => throw new ArgumentOutOfRangeException(nameof(mode))
        };

    // The tolerance makes mathematically coincident centers and integer sinc zeros stable against
    // binary coordinate error; it is far below an intensity rounding boundary.
    internal const double LanczosCoincidenceTolerance = 1e-12;

    internal static double NormalizedSinc(double value)
    {
        if (Math.Abs(value) <= LanczosCoincidenceTolerance) { return 1; }
        double nearestInteger = Math.Round(value);
        if (nearestInteger != 0 && Math.Abs(value - nearestInteger) <= LanczosCoincidenceTolerance)
        { return 0; }
        double piValue = Math.PI * value;
        return Math.Sin(piValue) / piValue;
    }

    internal static double LanczosWeight(double distance)
    {
        double absolute = Math.Abs(distance);
        return absolute < Scan2DResamplingOptions.LanczosRadius
            ? NormalizedSinc(distance) * NormalizedSinc(distance / Scan2DResamplingOptions.LanczosRadius)
            : 0;
    }

    private static byte RoundIntensity(double value)
    {
        double clamped = Math.Clamp(value, byte.MinValue, byte.MaxValue);
        return checked((byte)Math.Round(clamped, MidpointRounding.AwayFromZero));
    }

    // Provenance uses zero tolerance: a merely near-integer coordinate remains derived.
    private static bool IsExactSourceCenter(double coordinate, int length) =>
        coordinate == Math.Truncate(coordinate) && coordinate >= 0 && coordinate < length;

    // Centers are mapped in physical coordinates. Midpoint ties select the lower source index.
    private static int NearestIndex(double destinationCenter, double firstSourceCenter, double sourceStep, int length)
    {
        double coordinate = (destinationCenter - firstSourceCenter) / sourceStep;
        int index = checked((int)Math.Ceiling(coordinate - 0.5));
        return Math.Clamp(index, 0, length - 1);
    }
}
