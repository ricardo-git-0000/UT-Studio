using System.Buffers;
using UTStudio.Visualization.Core.Scan2D;

namespace UTStudio.Core.Tests.Visualization;

[TestClass]
public sealed class Scan2DProvenanceTests
{
    public static IEnumerable<object[]> ModeIntensityAndSourceProvenance
    {
        get
        {
            foreach (Scan2DInterpolationMode mode in Enum.GetValues<Scan2DInterpolationMode>())
                foreach (byte intensity in new byte[] { 0, 127, 255 })
                    foreach (Scan2DSampleProvenance provenance in Enum.GetValues<Scan2DSampleProvenance>())
                    { yield return [mode, intensity, provenance]; }
        }
    }

    [TestMethod]
    [DynamicData(nameof(ModeIntensityAndSourceProvenance))]
    public void SmallIndependentMatrixPreservesIntensityAndAppliesProvenanceDominance(
        Scan2DInterpolationMode mode, byte intensity, Scan2DSampleProvenance sourceProvenance)
    {
        int width = WidthFor(mode);
        double coordinate = mode == Scan2DInterpolationMode.None ? 0 : InterpolatedCoordinateFor(mode);
        byte[] values = Enumerable.Repeat(intensity, width).ToArray();
        byte[] provenance = Enumerable.Repeat((byte)sourceProvenance, width).ToArray();
        bool isNoData = sourceProvenance == Scan2DSampleProvenance.NoData;
        byte[] validity = Enumerable.Repeat(isNoData ? (byte)0 : (byte)1, width).ToArray();
        byte[] segments = Enumerable.Repeat(isNoData ? (byte)0 : (byte)1, width).ToArray();

        using Scan2DRenderResult result = Render(mode, values, coordinate, provenance, validity, segments);

        byte expectedIntensity = isNoData && mode != Scan2DInterpolationMode.None ? (byte)0 : intensity;
        Assert.AreEqual(expectedIntensity, result.Intensities.Span[0]);
        Assert.AreEqual(isNoData ? (byte)0 : (byte)1, result.ValidityMask.Span[0]);
        Scan2DSampleProvenance expected = isNoData
            ? Scan2DSampleProvenance.NoData
            : sourceProvenance == Scan2DSampleProvenance.SpatialEstimated
            ? Scan2DSampleProvenance.SpatialEstimated
            : mode == Scan2DInterpolationMode.None || sourceProvenance == Scan2DSampleProvenance.VisualDerived
                ? sourceProvenance
                : Scan2DSampleProvenance.VisualDerived;
        Assert.AreEqual((byte)expected, result.Provenance.Span[0]);
    }

    [TestMethod]
    public void NoneCopiesSpatialEstimatedExactly()
    {
        byte[] provenance = [(byte)Scan2DSampleProvenance.SpatialEstimated];
        using Scan2DRenderResult result = Render(Scan2DInterpolationMode.None, [83], 0, provenance);
        Assert.AreEqual((byte)83, result.Intensities.Span[0]);
        Assert.AreEqual((byte)1, result.ValidityMask.Span[0]);
        Assert.AreEqual(provenance[0], result.Provenance.Span[0]);
    }

    [TestMethod]
    [DataRow(Scan2DInterpolationMode.NearestNeighbor)]
    [DataRow(Scan2DInterpolationMode.Bilinear)]
    [DataRow(Scan2DInterpolationMode.Bicubic)]
    [DataRow(Scan2DInterpolationMode.Lanczos)]
    public void ExactMatchPreservesSpatialEstimated(Scan2DInterpolationMode mode)
    {
        int width = WidthFor(mode);
        int exact = ExactIndexFor(mode);
        byte[] provenance = Acquired(width);
        provenance[exact] = (byte)Scan2DSampleProvenance.SpatialEstimated;

        using Scan2DRenderResult result = Render(mode, Values(width), exact, provenance);

        Assert.AreEqual((byte)Scan2DSampleProvenance.SpatialEstimated, result.Provenance.Span[0]);
    }

    [TestMethod]
    [DataRow(Scan2DInterpolationMode.Bilinear)]
    [DataRow(Scan2DInterpolationMode.Bicubic)]
    [DataRow(Scan2DInterpolationMode.Lanczos)]
    public void ExactAcquiredMatchIgnoresEstimatedZeroWeightNeighbors(Scan2DInterpolationMode mode)
    {
        int width = WidthFor(mode);
        int exact = ExactIndexFor(mode);
        byte[] provenance = Enumerable.Repeat((byte)Scan2DSampleProvenance.SpatialEstimated, width).ToArray();
        provenance[exact] = (byte)Scan2DSampleProvenance.AcquiredSupport;

        using Scan2DRenderResult result = Render(mode, Values(width), exact, provenance);

        Assert.AreEqual((byte)Scan2DSampleProvenance.AcquiredSupport, result.Provenance.Span[0]);
    }

    [TestMethod]
    public void NearestNonExactPreservesSelectedSpatialEstimated()
    {
        byte[] provenance = [(byte)Scan2DSampleProvenance.SpatialEstimated,
            (byte)Scan2DSampleProvenance.AcquiredSupport];
        using Scan2DRenderResult result = Render(Scan2DInterpolationMode.NearestNeighbor,
            [25, 200], .25, provenance);
        Assert.AreEqual((byte)25, result.Intensities.Span[0]);
        Assert.AreEqual((byte)Scan2DSampleProvenance.SpatialEstimated, result.Provenance.Span[0]);
    }

    [TestMethod]
    [DataRow(Scan2DInterpolationMode.Bilinear)]
    [DataRow(Scan2DInterpolationMode.Bicubic)]
    [DataRow(Scan2DInterpolationMode.Lanczos)]
    public void ContributingSpatialEstimatedTapDominatesMixedInterpolation(Scan2DInterpolationMode mode)
    {
        int width = WidthFor(mode);
        byte[] provenance = Acquired(width);
        provenance[ExactIndexFor(mode)] = (byte)Scan2DSampleProvenance.SpatialEstimated;

        using Scan2DRenderResult result = Render(mode, Values(width), InterpolatedCoordinateFor(mode), provenance);

        Assert.AreEqual((byte)Scan2DSampleProvenance.SpatialEstimated, result.Provenance.Span[0]);
    }

    [TestMethod]
    [DataRow(Scan2DInterpolationMode.Bilinear)]
    [DataRow(Scan2DInterpolationMode.Bicubic)]
    [DataRow(Scan2DInterpolationMode.Lanczos)]
    public void SpatialEstimatedTapWithExactlyZeroCombinedWeightDoesNotChangeProvenance(
        Scan2DInterpolationMode mode)
    {
        int width = WidthFor(mode);
        int exact = ExactIndexFor(mode);
        byte[] provenance = Acquired(width);
        provenance[ZeroWeightNeighborFor(mode)] = (byte)Scan2DSampleProvenance.SpatialEstimated;

        using Scan2DRenderResult result = Render(mode, Values(width), exact, provenance);

        Assert.AreEqual((byte)Scan2DSampleProvenance.AcquiredSupport, result.Provenance.Span[0]);
    }

    [TestMethod]
    [DataRow(Scan2DInterpolationMode.Bilinear)]
    [DataRow(Scan2DInterpolationMode.Bicubic)]
    [DataRow(Scan2DInterpolationMode.Lanczos)]
    public void NoDataHasPriorityEvenWhenAnotherTapIsSpatialEstimated(Scan2DInterpolationMode mode)
    {
        int width = WidthFor(mode);
        byte[] provenance = Acquired(width);
        provenance[ExactIndexFor(mode)] = (byte)Scan2DSampleProvenance.SpatialEstimated;
        byte[] validity = Enumerable.Repeat((byte)1, width).ToArray();
        byte[] segments = Enumerable.Repeat((byte)1, width).ToArray();
        int invalid = ZeroWeightNeighborFor(mode);
        validity[invalid] = 0;
        segments[invalid] = 0;
        provenance[invalid] = (byte)Scan2DSampleProvenance.NoData;

        using Scan2DRenderResult result = Render(mode, Values(width), ExactIndexFor(mode), provenance,
            validity, segments);

        Assert.AreEqual((byte)0, result.ValidityMask.Span[0]);
        Assert.AreEqual((byte)Scan2DSampleProvenance.NoData, result.Provenance.Span[0]);
    }

    [TestMethod]
    [DataRow(Scan2DInterpolationMode.None)]
    [DataRow(Scan2DInterpolationMode.NearestNeighbor)]
    [DataRow(Scan2DInterpolationMode.Bilinear)]
    [DataRow(Scan2DInterpolationMode.Bicubic)]
    [DataRow(Scan2DInterpolationMode.Lanczos)]
    public void FullAndRowBlocksProduceIdenticalProvenance(Scan2DInterpolationMode mode)
    {
        const int width = 6, height = 4;
        byte[] values = Enumerable.Range(0, width * height).Select(value => (byte)(value * 7)).ToArray();
        byte[] provenance = Acquired(values.Length);
        provenance[2 * width + 2] = (byte)Scan2DSampleProvenance.SpatialEstimated;
        var geometry = new Scan2DGridGeometry(0, 0, 1, 1);
        var source = new Scan2DSourceGrid(width, height, width, geometry, values,
            Enumerable.Repeat((byte)1, values.Length).ToArray(),
            Enumerable.Repeat((byte)1, values.Length).ToArray(), provenance);
        var resampler = new Scan2DResampler();
        Scan2DGridGeometry destinationGeometry = mode == Scan2DInterpolationMode.None
            ? geometry : new(.25, .25, 1, 1);
        using Scan2DRenderResult full = resampler.Render(source,
            new Scan2DRenderRequest(geometry, width, height, destinationGeometry, width, height,
                new(0, 0, width, height), new(mode)), new Owner(values.Length * 3));

        for (int row = 0; row < height; row++)
        {
            using Scan2DRenderResult block = resampler.Render(source,
                new Scan2DRenderRequest(geometry, width, height, destinationGeometry, width, height,
                    new(0, row, width, 1), new(mode)), new Owner(values.Length * 3));
            CollectionAssert.AreEqual(full.Provenance.Span.Slice(row * width, width).ToArray(),
                block.Provenance.Span.Slice(row * width, width).ToArray(), $"row {row}");
        }
    }

    [TestMethod]
    public void CancellationDoesNotReturnPartiallyWrittenProvenance()
    {
        const int size = 64;
        byte[] values = Enumerable.Repeat((byte)42, size * size).ToArray();
        byte[] valid = Enumerable.Repeat((byte)1, values.Length).ToArray();
        var geometry = new Scan2DGridGeometry(0, 0, 1, 1);
        var source = new Scan2DSourceGrid(size, size, size, geometry, values, valid, valid,
            Enumerable.Repeat((byte)Scan2DSampleProvenance.SpatialEstimated, values.Length).ToArray());
        var owner = new CountingOwner(values.Length * 3);
        var resampler = new Scan2DResampler();
        using var cancellation = new CancellationTokenSource();
        resampler.AfterKernelRowProcessed = _ => cancellation.Cancel();

        Assert.ThrowsExactly<OperationCanceledException>(() => resampler.Render(source,
            Request(Scan2DInterpolationMode.Bilinear, geometry, size, size, new(0, 0, size, size)),
            owner, cancellation.Token));
        Assert.AreEqual(1, owner.DisposeCount);
    }

    private static Scan2DRenderResult Render(Scan2DInterpolationMode mode, byte[] values,
        double destinationCenterX, byte[] provenance, byte[]? validity = null, byte[]? segments = null)
    {
        int width = values.Length;
        validity ??= Enumerable.Repeat((byte)1, width).ToArray();
        segments ??= Enumerable.Repeat((byte)1, width).ToArray();
        var sourceGeometry = new Scan2DGridGeometry(0, 0, 1, 1);
        var destinationGeometry = mode == Scan2DInterpolationMode.None
            ? sourceGeometry : new Scan2DGridGeometry(destinationCenterX, 0, 1, 1);
        int destinationWidth = mode == Scan2DInterpolationMode.None ? width : 1;
        var source = new Scan2DSourceGrid(width, 1, width, sourceGeometry, values, validity, segments, provenance);
        var request = new Scan2DRenderRequest(sourceGeometry, width, 1, destinationGeometry,
            destinationWidth, 1, new(0, 0, destinationWidth, 1), new(mode));
        return new Scan2DResampler().Render(source, request, new Owner(destinationWidth * 3));
    }

    private static Scan2DRenderRequest Request(Scan2DInterpolationMode mode, Scan2DGridGeometry geometry,
        int width, int height, Scan2DRectangle viewport) =>
        new(geometry, width, height, geometry, width, height, viewport, new(mode));

    private static int WidthFor(Scan2DInterpolationMode mode) => mode switch
    {
        Scan2DInterpolationMode.None => 1,
        Scan2DInterpolationMode.NearestNeighbor or Scan2DInterpolationMode.Bilinear => 2,
        Scan2DInterpolationMode.Bicubic => 4,
        Scan2DInterpolationMode.Lanczos => 6,
        _ => throw new ArgumentOutOfRangeException(nameof(mode))
    };

    private static int ExactIndexFor(Scan2DInterpolationMode mode) => mode switch
    {
        Scan2DInterpolationMode.NearestNeighbor or Scan2DInterpolationMode.Bilinear => 0,
        Scan2DInterpolationMode.Bicubic => 1,
        Scan2DInterpolationMode.Lanczos => 2,
        _ => 0
    };

    private static int ZeroWeightNeighborFor(Scan2DInterpolationMode mode) => mode switch
    {
        Scan2DInterpolationMode.Bilinear => 1,
        Scan2DInterpolationMode.Bicubic => 2,
        Scan2DInterpolationMode.Lanczos => 3,
        _ => throw new ArgumentOutOfRangeException(nameof(mode))
    };

    private static double InterpolatedCoordinateFor(Scan2DInterpolationMode mode) => mode switch
    {
        Scan2DInterpolationMode.NearestNeighbor => .25,
        Scan2DInterpolationMode.Bilinear => .5,
        Scan2DInterpolationMode.Bicubic => 1.5,
        Scan2DInterpolationMode.Lanczos => 2.5,
        _ => 0
    };

    private static byte[] Values(int width) => Enumerable.Range(1, width).Select(value => (byte)(value * 20)).ToArray();
    private static byte[] Acquired(int length) =>
        Enumerable.Repeat((byte)Scan2DSampleProvenance.AcquiredSupport, length).ToArray();

    private sealed class Owner(int length) : IMemoryOwner<byte>
    {
        private byte[]? _bytes = new byte[length];
        public Memory<byte> Memory => _bytes ?? throw new ObjectDisposedException(nameof(Owner));
        public void Dispose() => _bytes = null;
    }

    private sealed class CountingOwner(int length) : IMemoryOwner<byte>
    {
        private byte[]? _bytes = new byte[length];
        public int DisposeCount { get; private set; }
        public Memory<byte> Memory => _bytes ?? throw new ObjectDisposedException(nameof(CountingOwner));
        public void Dispose() { if (_bytes is not null) { _bytes = null; DisposeCount++; } }
    }
}
