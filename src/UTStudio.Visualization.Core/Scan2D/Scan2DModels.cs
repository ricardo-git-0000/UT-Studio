using System.Buffers;

namespace UTStudio.Visualization.Core.Scan2D;

public enum Scan2DInterpolationMode { None = 0, NearestNeighbor = 1, Bilinear = 2, Bicubic = 3, Lanczos = 4 }

public enum Scan2DMaskPolicy { AllRequiredSupportValidAndSameSegment = 0 }

/// <summary>
/// Neutral sample origin. SpatialEstimated is reserved for a future explicit reconstruction module;
/// visual resamplers never produce it.
/// </summary>
public enum Scan2DSampleProvenance : byte
{
    NoData = 0,
    AcquiredSupport = 1,
    VisualDerived = 2,
    SpatialEstimated = 3
}

public readonly record struct Scan2DGridGeometry
{
    public Scan2DGridGeometry(double firstCenterX, double firstCenterY, double centerStepX, double centerStepY)
    {
        if (!double.IsFinite(firstCenterX)) { throw new ArgumentOutOfRangeException(nameof(firstCenterX)); }
        if (!double.IsFinite(firstCenterY)) { throw new ArgumentOutOfRangeException(nameof(firstCenterY)); }
        if (!double.IsFinite(centerStepX) || centerStepX <= 0) { throw new ArgumentOutOfRangeException(nameof(centerStepX)); }
        if (!double.IsFinite(centerStepY) || centerStepY <= 0) { throw new ArgumentOutOfRangeException(nameof(centerStepY)); }
        FirstCenterX = firstCenterX; FirstCenterY = firstCenterY;
        CenterStepX = centerStepX; CenterStepY = centerStepY;
    }

    public double FirstCenterX { get; }
    public double FirstCenterY { get; }
    public double CenterStepX { get; }
    public double CenterStepY { get; }
}

public readonly record struct Scan2DRectangle(int X, int Y, int Width, int Height)
{
    public Scan2DRectangle ValidateWithin(int width, int height)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(X);
        ArgumentOutOfRangeException.ThrowIfNegative(Y);
        ArgumentOutOfRangeException.ThrowIfLessThan(Width, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(Height, 1);
        if (checked(X + Width) > width) { throw new ArgumentOutOfRangeException(nameof(Width)); }
        if (checked(Y + Height) > height) { throw new ArgumentOutOfRangeException(nameof(Height)); }
        return this;
    }
}

/// <summary>
/// Non-owning neutral grid. Cell centers are FirstCenter + index * CenterStep. Validity is authoritative;
/// intensity zero is a regular numeric value. Segment zero is reserved for invalid cells.
/// </summary>
public readonly struct Scan2DSourceGrid
{
    public Scan2DSourceGrid(int width, int height, int stride, Scan2DGridGeometry geometry,
        ReadOnlyMemory<byte> intensities, ReadOnlyMemory<byte> validityMask,
        ReadOnlyMemory<byte> segmentIds, ReadOnlyMemory<byte> provenance)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(height, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(stride, width);
        int required = checked(checked((height - 1) * stride) + width);
        if (intensities.Length < required) { throw new ArgumentException("Intensity storage is smaller than the grid.", nameof(intensities)); }
        if (validityMask.Length < required) { throw new ArgumentException("Validity storage is smaller than the grid.", nameof(validityMask)); }
        if (segmentIds.Length < required) { throw new ArgumentException("Segment storage is smaller than the grid.", nameof(segmentIds)); }
        if (provenance.Length < required) { throw new ArgumentException("Provenance storage is smaller than the grid.", nameof(provenance)); }
        ReadOnlySpan<byte> masks = validityMask.Span;
        ReadOnlySpan<byte> segments = segmentIds.Span;
        ReadOnlySpan<byte> origins = provenance.Span;
        for (int y = 0; y < height; y++)
        {
            int row = checked(y * stride);
            for (int x = 0; x < width; x++)
            {
                int index = row + x;
                if (masks[index] > 1) { throw new ArgumentException("Validity values must be zero or one.", nameof(validityMask)); }
                bool valid = masks[index] != 0;
                if (valid != (segments[index] != 0))
                { throw new ArgumentException("Valid cells require a non-zero segment and invalid cells require segment zero.", nameof(segmentIds)); }
                if (!Enum.IsDefined((Scan2DSampleProvenance)origins[index]) ||
                    valid == (origins[index] == (byte)Scan2DSampleProvenance.NoData))
                { throw new ArgumentException("Provenance must agree with validity.", nameof(provenance)); }
            }
        }
        Width = width; Height = height; Stride = stride; Geometry = geometry;
        Intensities = intensities; ValidityMask = validityMask; SegmentIds = segmentIds; Provenance = provenance;
    }

    public int Width { get; }
    public int Height { get; }
    public int Stride { get; }
    public Scan2DGridGeometry Geometry { get; }
    public ReadOnlyMemory<byte> Intensities { get; }
    public ReadOnlyMemory<byte> ValidityMask { get; }
    public ReadOnlyMemory<byte> SegmentIds { get; }
    public ReadOnlyMemory<byte> Provenance { get; }
}

public readonly record struct Scan2DResamplingOptions
{
    public const uint CurrentAlgorithmVersion = 3;
    public const double CatmullRomA = -0.5;
    public const int LanczosRadius = 3;

    public Scan2DResamplingOptions(Scan2DInterpolationMode mode,
        Scan2DMaskPolicy maskPolicy = Scan2DMaskPolicy.AllRequiredSupportValidAndSameSegment,
        uint algorithmVersion = CurrentAlgorithmVersion, double cubicA = CatmullRomA,
        int lanczosRadius = LanczosRadius)
    {
        Mode = Enum.IsDefined(mode) ? mode : Scan2DInterpolationMode.None;
        if (!Enum.IsDefined(maskPolicy)) { throw new ArgumentOutOfRangeException(nameof(maskPolicy)); }
        if (algorithmVersion == 0) { throw new ArgumentOutOfRangeException(nameof(algorithmVersion)); }
        if (!double.IsFinite(cubicA) || cubicA != CatmullRomA)
        { throw new ArgumentOutOfRangeException(nameof(cubicA), "Phase 2 supports only Keys/Catmull-Rom a=-0.5."); }
        if (lanczosRadius != LanczosRadius)
        { throw new ArgumentOutOfRangeException(nameof(lanczosRadius), "Lanczos supports only the fixed radius a=3."); }
        MaskPolicy = maskPolicy; AlgorithmVersion = algorithmVersion; CubicA = cubicA;
        LanczosA = lanczosRadius;
    }

    public Scan2DInterpolationMode Mode { get; }
    public Scan2DMaskPolicy MaskPolicy { get; }
    public uint AlgorithmVersion { get; }
    public double CubicA { get; }
    public int LanczosA { get; }
    public static Scan2DResamplingOptions Default => new(Scan2DInterpolationMode.None);
}

public readonly record struct Scan2DRenderRequest
{
    public Scan2DRenderRequest(Scan2DGridGeometry sourceGeometry, int sourceWidth, int sourceHeight,
        Scan2DGridGeometry destinationGeometry, int destinationWidth, int destinationHeight,
        Scan2DRectangle viewport, Scan2DResamplingOptions options)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(sourceWidth, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(sourceHeight, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(destinationWidth, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(destinationHeight, 1);
        _ = checked(sourceWidth * sourceHeight);
        _ = checked(destinationWidth * destinationHeight);
        ValidateLastCenter(sourceGeometry, sourceWidth, sourceHeight, nameof(sourceGeometry));
        ValidateLastCenter(destinationGeometry, destinationWidth, destinationHeight, nameof(destinationGeometry));
        SourceGeometry = sourceGeometry; SourceWidth = sourceWidth; SourceHeight = sourceHeight;
        DestinationGeometry = destinationGeometry; DestinationWidth = destinationWidth;
        DestinationHeight = destinationHeight; Viewport = viewport.ValidateWithin(destinationWidth, destinationHeight);
        Options = options;
    }

    private static void ValidateLastCenter(Scan2DGridGeometry geometry, int width, int height, string parameter)
    {
        if (!double.IsFinite(geometry.FirstCenterX + (width - 1) * geometry.CenterStepX) ||
            !double.IsFinite(geometry.FirstCenterY + (height - 1) * geometry.CenterStepY))
        { throw new ArgumentOutOfRangeException(parameter, "The final grid center must be finite."); }
    }

    public Scan2DGridGeometry SourceGeometry { get; }
    public int SourceWidth { get; }
    public int SourceHeight { get; }
    public Scan2DGridGeometry DestinationGeometry { get; }
    public int DestinationWidth { get; }
    public int DestinationHeight { get; }
    public Scan2DRectangle Viewport { get; }
    public Scan2DResamplingOptions Options { get; }
}

public readonly record struct Scan2DRenderSignature(
    Guid RunId, ulong StoreRevision, ulong WrapGeneration,
    Scan2DGridGeometry SourceGeometry, int SourceWidth, int SourceHeight,
    Scan2DGridGeometry DestinationGeometry, int DestinationWidth, int DestinationHeight,
    Scan2DRectangle Viewport, Scan2DResamplingOptions Options, ulong RenderConfigurationVersion);

public enum Scan2DRenderDiagnostic { Completed }

public sealed class Scan2DRenderResult : IDisposable
{
    private IMemoryOwner<byte>? _owner;
    private readonly Memory<byte> _intensities;
    private readonly Memory<byte> _validityMask;
    private readonly Memory<byte> _provenance;

    internal Scan2DRenderResult(IMemoryOwner<byte> owner, int width, int height, int stride,
        Scan2DRectangle affectedRegion, bool isFull, Scan2DRenderDiagnostic diagnostic)
    {
        int cells = checked(stride * height);
        if (owner.Memory.Length < checked(cells * 3)) { owner.Dispose(); throw new ArgumentException("Owner is too small.", nameof(owner)); }
        _owner = owner; Width = width; Height = height; Stride = stride; AffectedRegion = affectedRegion;
        IsFull = isFull; Diagnostic = diagnostic;
        _intensities = owner.Memory[..cells];
        _validityMask = owner.Memory.Slice(cells, cells);
        _provenance = owner.Memory.Slice(checked(cells * 2), cells);
    }

    public int Width { get; }
    public int Height { get; }
    public int Stride { get; }
    public Scan2DRectangle AffectedRegion { get; }
    public bool IsFull { get; }
    public Scan2DRenderDiagnostic Diagnostic { get; }
    public Memory<byte> Intensities => Volatile.Read(ref _owner) is not null
        ? _intensities : throw new ObjectDisposedException(nameof(Scan2DRenderResult));
    public Memory<byte> ValidityMask => Volatile.Read(ref _owner) is not null
        ? _validityMask : throw new ObjectDisposedException(nameof(Scan2DRenderResult));
    public Memory<byte> Provenance => Volatile.Read(ref _owner) is not null
        ? _provenance : throw new ObjectDisposedException(nameof(Scan2DRenderResult));
    public void Dispose() => Interlocked.Exchange(ref _owner, null)?.Dispose();
}
