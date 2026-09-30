using System.Buffers;
using UTStudio.Visualization.Core.Scan2D;

namespace UTStudio.Core.Tests.Visualization;

[TestClass]
public sealed class Scan2DAdvancedResamplerTests
{
    [TestMethod]
    [DataRow(Scan2DInterpolationMode.Bilinear)]
    [DataRow(Scan2DInterpolationMode.Bicubic)]
    public void ConstantsIdentityAndSingletonsAreExact(Scan2DInterpolationMode mode)
    {
        AssertRender([0], 1, 1, new(0, 0, 1, 1), 1, 1, mode, [0], [1]);
        AssertRender([255], 1, 1, new(0, 0, 1, 1), 1, 1, mode, [255], [1]);
        AssertRender(Enumerable.Repeat((byte)73, 16).ToArray(), 4, 4,
            new(0, 0, 1, 1), 4, 4, mode, Enumerable.Repeat((byte)73, 16).ToArray(),
            Enumerable.Repeat((byte)1, 16).ToArray());
    }

    [TestMethod]
    public void BilinearRampsAndDeterministicRoundingAreExact()
    {
        AssertRender([0, 100], 2, 1, new(.25, 0, .25, 1), 3, 1,
            Scan2DInterpolationMode.Bilinear, [25, 50, 75], [1, 1, 1]);
        AssertRender([0, 20], 1, 2, new(0, .25, 1, .25), 1, 3,
            Scan2DInterpolationMode.Bilinear, [5, 10, 15], [1, 1, 1]);
        AssertRender([0, 10, 20, 30], 2, 2, new(.5, .5, 1, 1), 1, 1,
            Scan2DInterpolationMode.Bilinear, [15], [1]);
        AssertRender([0, 1], 2, 1, new(.5, 0, 1, 1), 1, 1,
            Scan2DInterpolationMode.Bilinear, [1], [1]);
        AssertRender([0, 255], 2, 1, new(.5, 0, 1, 1), 1, 1,
            Scan2DInterpolationMode.Bilinear, [128], [1]);
    }

    [TestMethod]
    public void BicubicPreservesHorizontalVerticalAndDiagonalLinearRamps()
    {
        AssertRender([0, 10, 20, 30], 4, 1, new(1.5, 0, 1, 1), 1, 1,
            Scan2DInterpolationMode.Bicubic, [15], [1]);
        AssertRender([0, 20, 40, 60], 1, 4, new(0, 1.5, 1, 1), 1, 1,
            Scan2DInterpolationMode.Bicubic, [30], [1]);
        byte[] diagonal = new byte[16];
        for (int y = 0; y < 4; y++)
            for (int x = 0; x < 4; x++) { diagonal[y * 4 + x] = checked((byte)(10 * x + 20 * y)); }
        AssertRender(diagonal, 4, 4, new(1.5, 1.5, 1, 1), 1, 1,
            Scan2DInterpolationMode.Bicubic, [45], [1]);
    }

    [TestMethod]
    public void BilinearSupportsUpscaleDownscaleAndDimensionsBelowKernel()
    {
        AssertRender([10, 20], 2, 1, SameExtent(2, 1, 4, 1), 4, 1,
            Scan2DInterpolationMode.Bilinear, [10, 13, 18, 20], [1, 1, 1, 1]);
        AssertRender([0, 10, 20, 30], 4, 1, SameExtent(4, 1, 2, 1), 2, 1,
            Scan2DInterpolationMode.Bilinear, [5, 25], [1, 1]);
        AssertRender([42], 1, 1, new(-1, -1, 1, 1), 3, 3,
            Scan2DInterpolationMode.Bilinear, Enumerable.Repeat((byte)42, 9).ToArray(),
            Enumerable.Repeat((byte)1, 9).ToArray());
    }

    [TestMethod]
    public void BicubicImpulseOvershootClampAndZeroAreExact()
    {
        AssertRender([0, 255, 0, 0], 4, 1, new(1.5, 0, 1, 1), 1, 1,
            Scan2DInterpolationMode.Bicubic, [143], [1]);
        AssertRender([0, 255, 255, 255], 4, 1, new(1.5, 0, 1, 1), 1, 1,
            Scan2DInterpolationMode.Bicubic, [255], [1]);
        AssertRender([255, 0, 0, 0], 4, 1, new(1.5, 0, 1, 1), 1, 1,
            Scan2DInterpolationMode.Bicubic, [0], [1]);
    }

    [TestMethod]
    public void BicubicConstantIsExactDuringSimultaneousUpscaleAndDownscale()
    {
        AssertRender(Enumerable.Repeat((byte)91, 16).ToArray(), 4, 4, SameExtent(4, 4, 7, 2), 7, 2,
            Scan2DInterpolationMode.Bicubic, Enumerable.Repeat((byte)91, 14).ToArray(),
            Enumerable.Repeat((byte)1, 14).ToArray());
    }

    [TestMethod]
    public void BicubicNonConstantUpscaleDownscaleAndSmallBordersAreExact()
    {
        AssertRender([0, 10, 20, 30], 4, 1, SameExtent(4, 1, 8, 1), 8, 1,
            Scan2DInterpolationMode.Bicubic, [0, 2, 7, 13, 18, 23, 28, 31],
            Enumerable.Repeat((byte)1, 8).ToArray());
        AssertRender([0, 10, 20, 30], 4, 1, SameExtent(4, 1, 2, 1), 2, 1,
            Scan2DInterpolationMode.Bicubic, [4, 26], [1, 1]);
        AssertRender([10, 200], 2, 1, new(0, 0, 1, 1), 2, 1,
            Scan2DInterpolationMode.Bicubic, [10, 200], [1, 1]);
        AssertRender([10, 100, 200], 3, 1, new(0, 0, 1, 1), 3, 1,
            Scan2DInterpolationMode.Bicubic, [10, 100, 200], [1, 1, 1]);
    }

    [TestMethod]
    [DataRow(Scan2DInterpolationMode.Bilinear)]
    [DataRow(Scan2DInterpolationMode.Bicubic)]
    public void AnyInvalidOrDifferentSegmentTapProducesNoData(Scan2DInterpolationMode mode)
    {
        int width = mode == Scan2DInterpolationMode.Bilinear ? 2 : 4;
        byte[] values = Enumerable.Repeat((byte)100, width).ToArray();
        byte[] mask = Enumerable.Repeat((byte)1, width).ToArray();
        byte[] segments = Enumerable.Repeat((byte)1, width).ToArray();
        mask[0] = 0; segments[0] = 0;
        AssertRender(values, width, 1, new(mode == Scan2DInterpolationMode.Bilinear ? 0d : 1d, 0, 1, 1),
            1, 1, mode, [0], [0], mask, segments);

        mask[0] = 1; segments[0] = 2;
        AssertRender(values, width, 1, new(mode == Scan2DInterpolationMode.Bilinear ? 0d : 1d, 0, 1, 1),
            1, 1, mode, [0], [0], mask, segments);
    }

    [TestMethod]
    [DataRow(Scan2DInterpolationMode.Bilinear, 3)]
    [DataRow(Scan2DInterpolationMode.Bicubic, 5)]
    public void OneAndMultipleCellGapsRemainNoData(Scan2DInterpolationMode mode, int width)
    {
        byte[] values = Enumerable.Repeat((byte)80, width).ToArray();
        byte[] mask = Enumerable.Repeat((byte)1, width).ToArray();
        byte[] segments = Enumerable.Repeat((byte)1, width).ToArray();
        mask[width / 2] = 0; segments[width / 2] = 0;
        if (width == 5) { mask[3] = 0; segments[3] = 0; }
        AssertRender(values, width, 1, new(width / 2d, 0, 1, 1), 1, 1, mode, [0], [0], mask, segments);
    }

    [TestMethod]
    public void ProvenanceIsPreservedOnlyForExactInBoundsSourceCenter()
    {
        byte[] provenance = [(byte)Scan2DSampleProvenance.VisualDerived, (byte)Scan2DSampleProvenance.AcquiredSupport,
            (byte)Scan2DSampleProvenance.AcquiredSupport, (byte)Scan2DSampleProvenance.AcquiredSupport];
        using Scan2DRenderResult exact = Render([10, 20, 30, 40], 4, 1, new(0, 0, 1, 1), 4, 1,
            Scan2DInterpolationMode.Bicubic, provenance: provenance);
        Assert.AreEqual((byte)Scan2DSampleProvenance.VisualDerived, exact.Provenance.Span[0]);
        using Scan2DRenderResult clamped = Render([10], 1, 1, new(-1, 0, 1, 1), 1, 1,
            Scan2DInterpolationMode.Bilinear);
        Assert.AreEqual((byte)Scan2DSampleProvenance.VisualDerived, clamped.Provenance.Span[0]);
    }

    [TestMethod]
    public void OptionsAndSignatureIncludeModeParameterAndAlgorithmVersion()
    {
        var bilinear = new Scan2DResamplingOptions(Scan2DInterpolationMode.Bilinear);
        var bicubic = new Scan2DResamplingOptions(Scan2DInterpolationMode.Bicubic);
        Assert.AreNotEqual(bilinear, bicubic);
        Assert.AreEqual(-0.5, bicubic.CubicA);
        Assert.Throws<ArgumentOutOfRangeException>(() => new Scan2DResamplingOptions(
            Scan2DInterpolationMode.Bicubic, algorithmVersion: 2, cubicA: -0.75));
        Assert.Throws<ArgumentOutOfRangeException>(() => new Scan2DResamplingOptions(
            Scan2DInterpolationMode.Bicubic, algorithmVersion: 2, cubicA: double.NaN));
        Assert.AreNotEqual(bicubic, new Scan2DResamplingOptions(Scan2DInterpolationMode.Bicubic,
            algorithmVersion: 3));
    }

    [TestMethod]
    [DataRow(Scan2DInterpolationMode.Bilinear)]
    [DataRow(Scan2DInterpolationMode.Bicubic)]
    public void CancellationDisposesOwnerWithoutPublishingPartialResult(Scan2DInterpolationMode mode)
    {
        const int size = 128;
        byte[] values = Enumerable.Repeat((byte)50, size * size).ToArray();
        byte[] valid = Enumerable.Repeat((byte)1, size * size).ToArray();
        var source = new Scan2DSourceGrid(size, size, size, new(0, 0, 1, 1), values, valid, valid,
            Enumerable.Repeat((byte)Scan2DSampleProvenance.AcquiredSupport, size * size).ToArray());
        var request = new Scan2DRenderRequest(source.Geometry, size, size, source.Geometry, size, size,
            new(0, 0, size, size), new(mode));
        var owner = new CountingOwner(size * size * 3);
        using var cancellation = new CancellationTokenSource();
        var resampler = new Scan2DResampler();
        resampler.AfterKernelRowProcessed = row => { if (row == 0) { cancellation.Cancel(); } };
        Assert.ThrowsExactly<OperationCanceledException>(() =>
            resampler.Render(source, request, owner, cancellation.Token));
        Assert.AreEqual(1, owner.DisposeCount);
    }

    private static void AssertRender(byte[] values, int width, int height, Scan2DGridGeometry destination,
        int destinationWidth, int destinationHeight, Scan2DInterpolationMode mode, byte[] expected,
        byte[] expectedMask, byte[]? mask = null, byte[]? segments = null)
    {
        using Scan2DRenderResult result = Render(values, width, height, destination, destinationWidth,
            destinationHeight, mode, mask, segments);
        CollectionAssert.AreEqual(expected, result.Intensities.ToArray());
        CollectionAssert.AreEqual(expectedMask, result.ValidityMask.ToArray());
    }

    private static Scan2DRenderResult Render(byte[] values, int width, int height,
        Scan2DGridGeometry destination, int destinationWidth, int destinationHeight,
        Scan2DInterpolationMode mode, byte[]? mask = null, byte[]? segments = null, byte[]? provenance = null)
    {
        int cells = checked(width * height);
        mask ??= Enumerable.Repeat((byte)1, cells).ToArray();
        segments ??= Enumerable.Repeat((byte)1, cells).ToArray();
        provenance ??= mask.Select(value => value == 0 ? (byte)Scan2DSampleProvenance.NoData :
            (byte)Scan2DSampleProvenance.AcquiredSupport).ToArray();
        var sourceGeometry = new Scan2DGridGeometry(0, 0, 1, 1);
        var source = new Scan2DSourceGrid(width, height, width, sourceGeometry,
            values, mask, segments, provenance);
        var request = new Scan2DRenderRequest(sourceGeometry, width, height, destination,
            destinationWidth, destinationHeight, new(0, 0, destinationWidth, destinationHeight), new(mode));
        return new Scan2DResampler().Render(source, request,
            new Owner(checked(destinationWidth * destinationHeight * 3)));
    }

    private static Scan2DGridGeometry SameExtent(int sourceWidth, int sourceHeight,
        int destinationWidth, int destinationHeight) => new(
        (double)sourceWidth / (2 * destinationWidth) - .5,
        (double)sourceHeight / (2 * destinationHeight) - .5,
        (double)sourceWidth / destinationWidth, (double)sourceHeight / destinationHeight);

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
