using System.Buffers;

namespace UTStudio.Visualization.Core.Scan2D;

public interface IScan2DResampler
{
    Scan2DRenderResult Render(Scan2DSourceGrid source, Scan2DRenderRequest request,
        IMemoryOwner<byte> destinationOwner, CancellationToken cancellationToken = default);
}

internal interface ILanczosTablePool
{
    int[] RentIndices(int minimumLength);
    double[] RentWeights(int minimumLength);
    void Return(int[] array);
    void Return(double[] array);
}

internal sealed class SharedLanczosTablePool : ILanczosTablePool
{
    internal static SharedLanczosTablePool Instance { get; } = new();
    public int[] RentIndices(int minimumLength) => ArrayPool<int>.Shared.Rent(minimumLength);
    public double[] RentWeights(int minimumLength) => ArrayPool<double>.Shared.Rent(minimumLength);
    public void Return(int[] array) => ArrayPool<int>.Shared.Return(array);
    public void Return(double[] array) => ArrayPool<double>.Shared.Return(array);
}

internal readonly record struct LanczosTableMemory(long LogicalBytes, long RentedBytes);

public sealed class Scan2DResampler : IScan2DResampler
{
    private const int LanczosTapCount = 6;
    private const int LanczosFirstTapOffset = -2;
    private const int LanczosWeightStride = LanczosTapCount + 1;
    private readonly ILanczosTablePool _lanczosTablePool;
    private readonly Action<long>? _tableBytesChanged;
    internal Action<int>? AfterKernelRowProcessed { get; set; }
    internal Action<int, bool>? AfterLanczosCoordinatePrepared { get; set; }
    internal LanczosTableMemory LastLanczosTableMemory { get; private set; }

    public Scan2DResampler() : this(SharedLanczosTablePool.Instance, null) { }

    internal Scan2DResampler(ILanczosTablePool lanczosTablePool, Action<long>? tableBytesChanged = null)
    {
        _lanczosTablePool = lanczosTablePool ?? throw new ArgumentNullException(nameof(lanczosTablePool));
        _tableBytesChanged = tableBytesChanged;
    }

    public Scan2DRenderResult Render(Scan2DSourceGrid source, Scan2DRenderRequest request,
        IMemoryOwner<byte> destinationOwner, CancellationToken cancellationToken = default)
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
                    using (LanczosTables tables = PrepareLanczosTables(source, request, cancellationToken))
                    { RenderLanczosPrepared(source, request, result, tables, cancellationToken); }
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

    private static void RenderNearest(Scan2DSourceGrid source, Scan2DRenderRequest request,
        Scan2DRenderResult result, CancellationToken token)
    {
        Scan2DRectangle region = request.Viewport;
        for (int y = region.Y; y < checked(region.Y + region.Height); y++)
        {
            token.ThrowIfCancellationRequested();
            int sourceY = NearestIndex(request.DestinationGeometry.FirstCenterY + y * request.DestinationGeometry.CenterStepY,
                source.Geometry.FirstCenterY, source.Geometry.CenterStepY, source.Height);
            double coordinateY = SourceCoordinate(request.DestinationGeometry.FirstCenterY +
                y * request.DestinationGeometry.CenterStepY, source.Geometry.FirstCenterY,
                source.Geometry.CenterStepY);
            for (int x = region.X; x < checked(region.X + region.Width); x++)
            {
                int sourceX = NearestIndex(request.DestinationGeometry.FirstCenterX + x * request.DestinationGeometry.CenterStepX,
                    source.Geometry.FirstCenterX, source.Geometry.CenterStepX, source.Width);
                double coordinateX = SourceCoordinate(request.DestinationGeometry.FirstCenterX +
                    x * request.DestinationGeometry.CenterStepX, source.Geometry.FirstCenterX,
                    source.Geometry.CenterStepX);
                int sourceIndex = checked(sourceY * source.Stride + sourceX);
                if (source.ValidityMask.Span[sourceIndex] == 0) { continue; }
                int destinationIndex = checked(y * result.Stride + x);
                result.Intensities.Span[destinationIndex] = source.Intensities.Span[sourceIndex];
                result.ValidityMask.Span[destinationIndex] = 1;
                result.Provenance.Span[destinationIndex] = IsExactSourceCenter(coordinateX, source.Width) &&
                    IsExactSourceCenter(coordinateY, source.Height) ? source.Provenance.Span[sourceIndex] :
                    (byte)Scan2DSampleProvenance.VisualDerived;
            }
        }
    }

    private void RenderKernel(Scan2DSourceGrid source, Scan2DRenderRequest request,
        Scan2DRenderResult result, CancellationToken token)
    {
        Scan2DRectangle region = request.Viewport;
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
        for (int y = region.Y; y < checked(region.Y + region.Height); y++)
        {
            token.ThrowIfCancellationRequested();
            double sourceY = SourceCoordinate(request.DestinationGeometry.FirstCenterY +
                y * request.DestinationGeometry.CenterStepY, source.Geometry.FirstCenterY,
                source.Geometry.CenterStepY);
            int baseY = checked((int)Math.Floor(sourceY));
            double fractionY = sourceY - baseY;
            for (int x = region.X; x < checked(region.X + region.Width); x++)
            {
                double sourceX = SourceCoordinate(request.DestinationGeometry.FirstCenterX +
                    x * request.DestinationGeometry.CenterStepX, source.Geometry.FirstCenterX,
                    source.Geometry.CenterStepX);
                int baseX = checked((int)Math.Floor(sourceX));
                double fractionX = sourceX - baseX;
                double accumulated = 0;
                double combinedWeight = 0;
                byte segment = 0;
                bool valid = true;
                for (int tapY = 0; tapY < tapCount && valid; tapY++)
                {
                    int unclampedY = checked(baseY + firstTapOffset + tapY);
                    int sampleY = Math.Clamp(unclampedY, 0, source.Height - 1);
                    double weightY = KernelWeight(mode, fractionY - (firstTapOffset + tapY), request.Options);
                    for (int tapX = 0; tapX < tapCount; tapX++)
                    {
                        int unclampedX = checked(baseX + firstTapOffset + tapX);
                        int sampleX = Math.Clamp(unclampedX, 0, source.Width - 1);
                        int sourceIndex = checked(sampleY * source.Stride + sampleX);
                        if (source.ValidityMask.Span[sourceIndex] == 0)
                        { valid = false; break; }
                        byte tapSegment = source.SegmentIds.Span[sourceIndex];
                        if (segment == 0) { segment = tapSegment; }
                        else if (tapSegment != segment) { valid = false; break; }
                        double weightX = KernelWeight(mode, fractionX - (firstTapOffset + tapX), request.Options);
                        double weight = weightY * weightX;
                        accumulated += source.Intensities.Span[sourceIndex] * weight;
                        combinedWeight += weight;
                    }
                }
                if (!valid) { continue; }
                if (mode == Scan2DInterpolationMode.Lanczos)
                {
                    if (!double.IsFinite(combinedWeight) ||
                        Math.Abs(combinedWeight) < LanczosCoincidenceTolerance)
                    { throw new InvalidOperationException("Lanczos normalization weight is zero."); }
                    accumulated /= combinedWeight;
                }
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
            int[] array = _pool.RentIndices(length);
            try { Admit(checked((long)array.Length * sizeof(int))); return array; }
            catch { _pool.Return(array); throw; }
        }

        private double[] RentWeights(int length)
        {
            double[] array = _pool.RentWeights(length);
            try { Admit(checked((long)array.Length * sizeof(double))); return array; }
            catch { _pool.Return(array); throw; }
        }

        private void Admit(long bytes)
        {
            _bytesChanged?.Invoke(bytes);
            RentedBytes = checked(RentedBytes + bytes);
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
