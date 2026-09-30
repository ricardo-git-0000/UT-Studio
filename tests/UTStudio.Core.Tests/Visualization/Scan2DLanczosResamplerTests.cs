using System.Buffers;
using UTStudio.Visualization.Core.Scan2D;

namespace UTStudio.Core.Tests.Visualization;

[TestClass]
public sealed class Scan2DLanczosResamplerTests
{
    [TestMethod]
    public void SincAndLanczosKernelHaveExactSpecialValuesSymmetryAndSupport()
    {
        Assert.AreEqual(1d, Scan2DResampler.NormalizedSinc(0));
        Assert.AreEqual(1d, Scan2DResampler.NormalizedSinc(1e-13));
        foreach (double integer in new[] { -2d, -1d, 1d, 2d })
            Assert.AreEqual(0d, Scan2DResampler.NormalizedSinc(integer));
        foreach (double x in new[] { .125, .5, 1.25, 2.75 })
            Assert.AreEqual(Scan2DResampler.LanczosWeight(x), Scan2DResampler.LanczosWeight(-x), 1e-15);
        Assert.AreEqual(0d, Scan2DResampler.LanczosWeight(-3));
        Assert.AreEqual(0d, Scan2DResampler.LanczosWeight(3));
        Assert.AreEqual(0d, Scan2DResampler.LanczosWeight(4));

        (double Fraction, double Expected)[] referenceSums =
        [
            (0, 1),
            (.125, .99904197907331282),
            (.25, .9969715379878975),
            (.5, .99429854881014146),
            (.875, .99904197907331282)
        ];
        foreach ((double fraction, double expected) in referenceSums)
        {
            double sum = 0;
            for (int offset = -2; offset <= 3; offset++)
                sum += Scan2DResampler.LanczosWeight(fraction - offset);
            Assert.AreEqual(expected, sum, 1e-15);
        }
    }

    [TestMethod]
    public void IndependentReferenceFixturesCoverRampImpulseLobesOvershootClampAndRounding()
    {
        // Literal reference values were calculated independently at high precision from the
        // published Lanczos-3 definition, not by a test copy of the production implementation.
        AssertRender1D([0, 20, 40, 60, 80, 100], [.25, .5, .75, 1.25, 1.5, 1.75, 2, 2.25, 2.5, 2.75, 3.5, 4.5],
            [3, 8, 14, 25, 30, 36, 40, 45, 50, 55, 70, 92]);
        AssertRender1D([0, 0, 255, 0, 0, 0], [.25, .5, .75, 1.25, 1.5, 1.75, 2, 2.25, 2.5, 2.75, 3.5, 4.5],
            [0, 0, 0, 69, 156, 228, 255, 228, 156, 69, 0, 6]);
        AssertRender1D([255, 255, 0, 0, 0, 0], [.25, .5, .75, 1.25, 1.5, 1.75, 2, 2.25, 2.5, 2.75],
            [255, 255, 255, 201, 128, 54, 0, 0, 0, 0]);
        AssertRender1D([0, 0, 255, 255, 255, 255], [.25, .5, .75, 1.25, 1.5, 1.75, 2, 2.25, 2.5, 2.75],
            [0, 0, 0, 54, 128, 201, 255, 255, 255, 255]);
    }

    [TestMethod]
    public void ConstantsIdentitySmallDimensionsRowsColumnsAndValidZeroAreExact()
    {
        foreach ((int width, int height) in new[] { (1, 1), (1, 5), (5, 1), (2, 3), (5, 5) })
        {
            byte[] constant = Enumerable.Repeat((byte)73, checked(width * height)).ToArray();
            using Scan2DRenderResult result = Render(constant, width, height,
                new(0, 0, 1, 1), width, height);
            CollectionAssert.AreEqual(constant, result.Intensities.ToArray());
            Assert.IsTrue(result.ValidityMask.Span.ToArray().All(x => x == 1));
        }

        using Scan2DRenderResult zero = Render([0], 1, 1, new(0, 0, 1, 1), 1, 1);
        Assert.AreEqual((byte)0, zero.Intensities.Span[0]);
        Assert.AreEqual((byte)1, zero.ValidityMask.Span[0]);
        Assert.AreEqual((byte)Scan2DSampleProvenance.AcquiredSupport, zero.Provenance.Span[0]);
    }

    [TestMethod]
    public void HorizontalVerticalAndDiagonalRampsUseTheExistingCenterConvention()
    {
        using Scan2DRenderResult horizontal = Render([0, 20, 40, 60, 80, 100], 6, 1,
            new(2.5, 0, 1, 1), 1, 1);
        Assert.AreEqual((byte)50, horizontal.Intensities.Span[0]);
        using Scan2DRenderResult vertical = Render([0, 20, 40, 60, 80, 100], 1, 6,
            new(0, 2.5, 1, 1), 1, 1);
        Assert.AreEqual((byte)50, vertical.Intensities.Span[0]);

        byte[] diagonal = new byte[36];
        for (int y = 0; y < 6; y++)
            for (int x = 0; x < 6; x++) diagonal[y * 6 + x] = checked((byte)(10 * x + 20 * y));
        using Scan2DRenderResult result = Render(diagonal, 6, 6, new(2.5, 2.5, 1, 1), 1, 1);
        Assert.AreEqual((byte)75, result.Intensities.Span[0]);
    }

    [TestMethod]
    public void IndependentTwoDimensionalFixtureAndNonConstantScaleChangesAreExact()
    {
        byte[] values = Enumerable.Range(0, 36).Select(x => checked((byte)((x * 7) % 256))).ToArray();
        using Scan2DRenderResult fixture = Render(values, 6, 6, new(2.25, 1.75, 1, 1), 1, 1);
        Assert.AreEqual((byte)90, fixture.Intensities.Span[0]);

        AssertRender1D([0, 20, 40, 60, 80, 100],
            Enumerable.Range(0, 12).Select(x => (x + .5) * 6 / 12 - .5).ToArray(),
            [0, 3, 14, 25, 36, 45, 55, 64, 75, 86, 97, 101]);
        AssertRender1D([0, 20, 40, 60, 80, 100],
            Enumerable.Range(0, 3).Select(x => (x + .5) * 6 / 3 - .5).ToArray(),
            [8, 50, 92]);
    }

    [TestMethod]
    public void EveryRequiredTapNoDataOrDifferentSegmentInvalidatesThePixel()
    {
        byte[] values = Enumerable.Repeat((byte)100, 36).ToArray();
        for (int tap = 0; tap < 36; tap++)
        {
            byte[] mask = Enumerable.Repeat((byte)1, 36).ToArray();
            byte[] segments = Enumerable.Repeat((byte)1, 36).ToArray();
            mask[tap] = 0; segments[tap] = 0;
            using Scan2DRenderResult noData = Render(values, 6, 6, new(2.5, 2.5, 1, 1), 1, 1,
                mask, segments);
            Assert.AreEqual((byte)0, noData.ValidityMask.Span[0], $"tap {tap}");

            mask[tap] = 1; segments[tap] = 2;
            using Scan2DRenderResult otherSegment = Render(values, 6, 6, new(2.5, 2.5, 1, 1), 1, 1,
                mask, segments);
            Assert.AreEqual((byte)0, otherSegment.ValidityMask.Span[0], $"segment tap {tap}");
        }
    }

    [TestMethod]
    public void OneAndMultipleCellGapsRemainNoDataAndMayWidenByTheConservativeSupport()
    {
        byte[] values = Enumerable.Repeat((byte)80, 8).ToArray();
        byte[] mask = Enumerable.Repeat((byte)1, 8).ToArray();
        byte[] segments = Enumerable.Repeat((byte)1, 8).ToArray();
        mask[3] = 0; segments[3] = 0;
        using Scan2DRenderResult one = Render(values, 8, 1, new(4.5, 0, 1, 1), 1, 1, mask, segments);
        Assert.AreEqual((byte)0, one.ValidityMask.Span[0]);
        mask[4] = 0; segments[4] = 0;
        using Scan2DRenderResult multiple = Render(values, 8, 1, new(5, 0, 1, 1), 1, 1, mask, segments);
        Assert.AreEqual((byte)0, multiple.ValidityMask.Span[0]);
    }

    [TestMethod]
    public void ProvenanceIsPreservedOnlyAtAnExactlyCoincidentInBoundsCenter()
    {
        using Scan2DRenderResult exact = Render([10, 20, 30, 40, 50, 60], 6, 1,
            new(2, 0, 1, 1), 1, 1);
        Assert.AreEqual((byte)Scan2DSampleProvenance.AcquiredSupport, exact.Provenance.Span[0]);
        using Scan2DRenderResult near = Render([10, 20, 30, 40, 50, 60], 6, 1,
            new(2 + 5e-13, 0, 1, 1), 1, 1);
        Assert.AreEqual((byte)Scan2DSampleProvenance.VisualDerived, near.Provenance.Span[0]);
        using Scan2DRenderResult replicated = Render([10], 1, 1, new(-1, 0, 1, 1), 1, 1);
        Assert.AreEqual((byte)Scan2DSampleProvenance.VisualDerived, replicated.Provenance.Span[0]);
    }

    [TestMethod]
    public void OptionsFixLanczosRadiusAndIncludeItInTheStructuralSignature()
    {
        var options = new Scan2DResamplingOptions(Scan2DInterpolationMode.Lanczos);
        Assert.AreEqual(3, options.LanczosA);
        Assert.AreEqual(3u, options.AlgorithmVersion);
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            new Scan2DResamplingOptions(Scan2DInterpolationMode.Lanczos, lanczosRadius: 2));
        Assert.AreNotEqual(options, new Scan2DResamplingOptions(Scan2DInterpolationMode.Lanczos,
            algorithmVersion: 4));
    }

    private static void AssertRender1D(byte[] source, double[] centers, byte[] expected)
    {
        var destination = new Scan2DGridGeometry(centers[0], 0,
            centers.Length == 1 ? 1 : centers[1] - centers[0], 1);
        // Non-uniform fixture coordinates are evaluated one at a time.
        byte[] actual = new byte[centers.Length];
        for (int index = 0; index < centers.Length; index++)
        {
            using Scan2DRenderResult result = Render(source, source.Length, 1,
                destination with { }, 1, 1, firstCenterX: centers[index]);
            actual[index] = result.Intensities.Span[0];
        }
        CollectionAssert.AreEqual(expected, actual);
    }

    private static Scan2DRenderResult Render(byte[] values, int width, int height,
        Scan2DGridGeometry destination, int destinationWidth, int destinationHeight,
        byte[]? mask = null, byte[]? segments = null, double? firstCenterX = null)
    {
        int cells = checked(width * height);
        mask ??= Enumerable.Repeat((byte)1, cells).ToArray();
        segments ??= Enumerable.Repeat((byte)1, cells).ToArray();
        byte[] provenance = mask.Select(x => x == 0 ? (byte)Scan2DSampleProvenance.NoData :
            (byte)Scan2DSampleProvenance.AcquiredSupport).ToArray();
        var sourceGeometry = new Scan2DGridGeometry(0, 0, 1, 1);
        if (firstCenterX is { } center)
            destination = new Scan2DGridGeometry(center, destination.FirstCenterY,
                destination.CenterStepX, destination.CenterStepY);
        var source = new Scan2DSourceGrid(width, height, width, sourceGeometry,
            values, mask, segments, provenance);
        var request = new Scan2DRenderRequest(sourceGeometry, width, height, destination,
            destinationWidth, destinationHeight, new(0, 0, destinationWidth, destinationHeight),
            new(Scan2DInterpolationMode.Lanczos));
        return new Scan2DResampler().Render(source, request,
            new Owner(checked(destinationWidth * destinationHeight * 3)));
    }

    private sealed class Owner(int length) : IMemoryOwner<byte>
    {
        private byte[]? _bytes = new byte[length];
        public Memory<byte> Memory => _bytes ?? throw new ObjectDisposedException(nameof(Owner));
        public void Dispose() => _bytes = null;
    }
}
