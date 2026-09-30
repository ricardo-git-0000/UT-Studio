using System.Buffers;

namespace UTStudio.Visualization.Core.Scan2D;

public interface IScan2DResampler
{
    Scan2DRenderResult Render(Scan2DSourceGrid source, Scan2DRenderRequest request,
        IMemoryOwner<byte> destinationOwner, CancellationToken cancellationToken = default);
}

public sealed class Scan2DResampler : IScan2DResampler
{
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
            for (int x = region.X; x < checked(region.X + region.Width); x++)
            {
                int sourceX = NearestIndex(request.DestinationGeometry.FirstCenterX + x * request.DestinationGeometry.CenterStepX,
                    source.Geometry.FirstCenterX, source.Geometry.CenterStepX, source.Width);
                int sourceIndex = checked(sourceY * source.Stride + sourceX);
                if (source.ValidityMask.Span[sourceIndex] == 0) { continue; }
                int destinationIndex = checked(y * result.Stride + x);
                result.Intensities.Span[destinationIndex] = source.Intensities.Span[sourceIndex];
                result.ValidityMask.Span[destinationIndex] = 1;
                result.Provenance.Span[destinationIndex] = (byte)(request.DestinationWidth == source.Width &&
                    request.DestinationHeight == source.Height && request.DestinationGeometry == source.Geometry
                    ? Scan2DSampleProvenance.AcquiredSupport : Scan2DSampleProvenance.VisualDerived);
            }
        }
    }

    // Centers are mapped in physical coordinates. Midpoint ties select the lower source index.
    private static int NearestIndex(double destinationCenter, double firstSourceCenter, double sourceStep, int length)
    {
        double coordinate = (destinationCenter - firstSourceCenter) / sourceStep;
        int index = checked((int)Math.Ceiling(coordinate - 0.5));
        return Math.Clamp(index, 0, length - 1);
    }
}
