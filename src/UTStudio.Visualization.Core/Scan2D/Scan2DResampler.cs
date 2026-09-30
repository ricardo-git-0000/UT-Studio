using System.Buffers;

namespace UTStudio.Visualization.Core.Scan2D;

public interface IScan2DResampler
{
    Scan2DRenderResult Render(Scan2DSourceGrid source, Scan2DRenderRequest request,
        IMemoryOwner<byte> destinationOwner, CancellationToken cancellationToken = default);
}

public sealed class Scan2DResampler : IScan2DResampler
{
    internal Action<int>? AfterKernelRowProcessed { get; set; }

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
                    RenderKernel(source, request, result, bicubic: false, cancellationToken);
                    break;
                case Scan2DInterpolationMode.Bicubic:
                    RenderKernel(source, request, result, bicubic: true, cancellationToken);
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
        Scan2DRenderResult result, bool bicubic, CancellationToken token)
    {
        Scan2DRectangle region = request.Viewport;
        int tapCount = bicubic ? 4 : 2;
        int firstTapOffset = bicubic ? -1 : 0;
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
                byte segment = 0;
                bool valid = true;
                for (int tapY = 0; tapY < tapCount && valid; tapY++)
                {
                    int unclampedY = checked(baseY + firstTapOffset + tapY);
                    int sampleY = Math.Clamp(unclampedY, 0, source.Height - 1);
                    double weightY = bicubic ? KeysWeight(fractionY - (firstTapOffset + tapY),
                        request.Options.CubicA) : LinearWeight(fractionY - tapY);
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
                        double weightX = bicubic ? KeysWeight(fractionX - (firstTapOffset + tapX),
                            request.Options.CubicA) : LinearWeight(fractionX - tapX);
                        accumulated += source.Intensities.Span[sourceIndex] * weightY * weightX;
                    }
                }
                if (!valid) { continue; }
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

    private static byte RoundIntensity(double value)
    {
        double clamped = Math.Clamp(value, byte.MinValue, byte.MaxValue);
        return checked((byte)Math.Round(clamped, MidpointRounding.AwayFromZero));
    }

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
