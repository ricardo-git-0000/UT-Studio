using System.Buffers;
using UTStudio.Visualization.Core.Scan2D;

namespace UTStudio.Core.Tests.Visualization;

[TestClass]
public sealed class Scan2DResamplerTests
{
    [TestMethod]
    public void NoneCopiesIntensityMaskAndProvenanceExactlyIncludingValidZero()
    {
        byte[] intensity = [0, 9, 17, 0];
        byte[] mask = [1, 0, 1, 0];
        byte[] segments = [1, 0, 1, 0];
        byte[] provenance =
            [(byte)Scan2DSampleProvenance.AcquiredSupport, (byte)Scan2DSampleProvenance.NoData,
             (byte)Scan2DSampleProvenance.AcquiredSupport, (byte)Scan2DSampleProvenance.NoData];
        Scan2DGridGeometry geometry = new(.5, .5, 1, 1);
        var source = new Scan2DSourceGrid(2, 2, 2, geometry, intensity, mask, segments, provenance);
        var request = new Scan2DRenderRequest(geometry, 2, 2, geometry, 2, 2,
            new(0, 0, 2, 2), Scan2DResamplingOptions.Default);
        using var owner = new TrackingOwner(12);

        using Scan2DRenderResult result = new Scan2DResampler().Render(source, request, owner);

        CollectionAssert.AreEqual(intensity, result.Intensities.ToArray());
        CollectionAssert.AreEqual(mask, result.ValidityMask.ToArray());
        CollectionAssert.AreEqual(provenance, result.Provenance.ToArray());
        Assert.AreEqual((byte)0, result.Intensities.Span[0]);
        Assert.AreEqual((byte)1, result.ValidityMask.Span[0]);
    }

    [TestMethod]
    [DataRow(1, 5, new byte[] { 10, 10, 10, 10, 10 })]
    [DataRow(2, 3, new byte[] { 10, 10, 20 })]
    [DataRow(3, 2, new byte[] { 10, 30 })]
    [DataRow(4, 2, new byte[] { 10, 30 })]
    public void NearestUsesPixelCentersAndLowerTie(int sourceWidth, int destinationWidth, byte[] expected)
    {
        byte[] intensity = Enumerable.Range(1, sourceWidth).Select(x => checked((byte)(x * 10))).ToArray();
        byte[] mask = Enumerable.Repeat((byte)1, sourceWidth).ToArray();
        byte[] segments = Enumerable.Repeat((byte)7, sourceWidth).ToArray();
        byte[] provenance = Enumerable.Repeat((byte)Scan2DSampleProvenance.AcquiredSupport, sourceWidth).ToArray();
        Scan2DGridGeometry sourceGeometry = new(0, 0, 1, 1);
        Scan2DGridGeometry destinationGeometry = SameExtent(sourceWidth, 1, destinationWidth, 1);
        var source = new Scan2DSourceGrid(sourceWidth, 1, sourceWidth, sourceGeometry,
            intensity, mask, segments, provenance);
        var request = new Scan2DRenderRequest(sourceGeometry, sourceWidth, 1, destinationGeometry,
            destinationWidth, 1, new(0, 0, destinationWidth, 1),
            new(Scan2DInterpolationMode.NearestNeighbor));
        using var owner = new TrackingOwner(checked(destinationWidth * 3));

        using Scan2DRenderResult result = new Scan2DResampler().Render(source, request, owner);

        CollectionAssert.AreEqual(expected, result.Intensities.ToArray());
        CollectionAssert.AreEqual(Enumerable.Repeat((byte)1, destinationWidth).ToArray(), result.ValidityMask.ToArray());
    }

    [TestMethod]
    public void NearestIdentityWorksForOneColumnAndOneRow()
    {
        byte[] values = [1, 2, 3, 4, 5];
        Scan2DGridGeometry geometry = new(0, 0, 1, 1);
        var source = new Scan2DSourceGrid(1, 5, 1, geometry, values,
            Enumerable.Repeat((byte)1, 5).ToArray(), Enumerable.Repeat((byte)1, 5).ToArray(),
            Enumerable.Repeat((byte)Scan2DSampleProvenance.AcquiredSupport, 5).ToArray());
        var request = new Scan2DRenderRequest(geometry, 1, 5, geometry, 1, 5,
            new(0, 0, 1, 5), new(Scan2DInterpolationMode.NearestNeighbor));
        using var owner = new TrackingOwner(15);
        using Scan2DRenderResult result = new Scan2DResampler().Render(source, request, owner);
        CollectionAssert.AreEqual(values, result.Intensities.ToArray());
        Assert.IsTrue(result.Provenance.Span.ToArray().All(x => x == (byte)Scan2DSampleProvenance.AcquiredSupport));
    }

    [TestMethod]
    public void InvalidTapRemainsNoDataAndIntensityIsNotConsulted()
    {
        Scan2DGridGeometry sourceGeometry = new(0, 0, 1, 1);
        Scan2DGridGeometry destinationGeometry = SameExtent(2, 1, 4, 1);
        var source = new Scan2DSourceGrid(2, 1, 2, sourceGeometry, new byte[] { 0, 99 },
            new byte[] { 1, 0 }, new byte[] { 1, 0 },
            new byte[] { (byte)Scan2DSampleProvenance.AcquiredSupport, (byte)Scan2DSampleProvenance.NoData });
        var request = new Scan2DRenderRequest(sourceGeometry, 2, 1, destinationGeometry, 4, 1,
            new(0, 0, 4, 1), new(Scan2DInterpolationMode.NearestNeighbor));
        using var owner = new TrackingOwner(12);
        using Scan2DRenderResult result = new Scan2DResampler().Render(source, request, owner);
        CollectionAssert.AreEqual(new byte[] { 0, 0, 0, 0 }, result.Intensities.ToArray());
        CollectionAssert.AreEqual(new byte[] { 1, 1, 0, 0 }, result.ValidityMask.ToArray());
    }

    [TestMethod]
    public void NearestTransformsBothAxesExactly()
    {
        byte[] values = [1, 2, 3, 4];
        byte[] valid = [1, 1, 1, 1];
        byte[] segments = [1, 1, 1, 1];
        byte[] provenance = Enumerable.Repeat((byte)Scan2DSampleProvenance.AcquiredSupport, 4).ToArray();
        Scan2DGridGeometry sourceGeometry = new(0, 0, 1, 1);
        Scan2DGridGeometry destinationGeometry = SameExtent(2, 2, 3, 3);
        var source = new Scan2DSourceGrid(2, 2, 2, sourceGeometry, values, valid, segments, provenance);
        var request = new Scan2DRenderRequest(sourceGeometry, 2, 2, destinationGeometry, 3, 3,
            new(0, 0, 3, 3), new(Scan2DInterpolationMode.NearestNeighbor));
        using var owner = new TrackingOwner(27);
        using Scan2DRenderResult result = new Scan2DResampler().Render(source, request, owner);
        CollectionAssert.AreEqual(new byte[] { 1, 1, 2, 1, 1, 2, 3, 3, 4 }, result.Intensities.ToArray());
    }

    [TestMethod]
    public void ResultRejectsUseAfterDisposeAndMarksPartialViewport()
    {
        Scan2DGridGeometry geometry = new(0, 0, 1, 1);
        var source = new Scan2DSourceGrid(2, 1, 2, geometry, new byte[] { 1, 2 }, new byte[] { 1, 1 },
            new byte[] { 1, 1 }, Enumerable.Repeat((byte)Scan2DSampleProvenance.AcquiredSupport, 2).ToArray());
        var request = new Scan2DRenderRequest(geometry, 2, 1, geometry, 2, 1,
            new(1, 0, 1, 1), Scan2DResamplingOptions.Default);
        var owner = new TrackingOwner(6);
        Scan2DRenderResult result = new Scan2DResampler().Render(source, request, owner);
        Assert.IsFalse(result.IsFull);
        result.Dispose();
        Assert.ThrowsExactly<ObjectDisposedException>(() => _ = result.Intensities);
        Assert.AreEqual(1, owner.DisposeCount);
    }

    [TestMethod]
    public void UnknownModeIsRejectedAndInvalidArgumentsOverflowAreRejected()
    {
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            new Scan2DResamplingOptions((Scan2DInterpolationMode)999));
        Assert.Throws<ArgumentOutOfRangeException>(() => new Scan2DGridGeometry(0, 0, 0, 1));
        Assert.Throws<OverflowException>(() => new Scan2DSourceGrid(int.MaxValue, 2, int.MaxValue,
            new(0, 0, 1, 1), Array.Empty<byte>(), Array.Empty<byte>(), Array.Empty<byte>(),
            Array.Empty<byte>()));
        Assert.Throws<OverflowException>(() => new Scan2DRenderRequest(new(0, 0, 1, 1), 1, 1,
            new(0, 0, 1, 1), int.MaxValue, 2, new(0, 0, 1, 1), Scan2DResamplingOptions.Default));
    }

    [TestMethod]
    public void ExactDefaultIsCanonicalizedBeforeStorageAndSignature()
    {
        Scan2DGridGeometry geometry = new(0, 0, 1, 1);
        var fromRawDefault = new Scan2DRenderRequest(geometry, 1, 1, geometry, 1, 1,
            new(0, 0, 1, 1), default);
        var fromCanonicalDefault = new Scan2DRenderRequest(geometry, 1, 1, geometry, 1, 1,
            new(0, 0, 1, 1), Scan2DResamplingOptions.Default);

        Assert.AreEqual(Scan2DResamplingOptions.Default, fromRawDefault.Options);
        Assert.AreEqual(fromCanonicalDefault.Options, fromRawDefault.Options);
        Assert.AreEqual(Scan2DResamplingOptions.CurrentAlgorithmVersion,
            fromRawDefault.Options.AlgorithmVersion);
        Assert.AreNotEqual(0u, fromRawDefault.Options.AlgorithmVersion);
    }

    [TestMethod]
    public void UnsupportedAlgorithmVersionsAreRejectedBeforeARequestCanBeCreated()
    {
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new Scan2DResamplingOptions(
            Scan2DInterpolationMode.None, algorithmVersion: 0));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new Scan2DResamplingOptions(
            Scan2DInterpolationMode.None,
            algorithmVersion: checked(Scan2DResamplingOptions.CurrentAlgorithmVersion + 1)));
    }

    [TestMethod]
    public void CancellationDisposesDestinationOwner()
    {
        const int size = 64;
        var source = new Scan2DSourceGrid(size, size, size, new(0, 0, 1, 1), new byte[size * size],
            Enumerable.Repeat((byte)1, size * size).ToArray(), Enumerable.Repeat((byte)1, size * size).ToArray(),
            Enumerable.Repeat((byte)Scan2DSampleProvenance.AcquiredSupport, size * size).ToArray());
        var request = new Scan2DRenderRequest(source.Geometry, size, size, source.Geometry, size, size,
            new(0, 0, size, size), new(Scan2DInterpolationMode.NearestNeighbor));
        var owner = new TrackingOwner(size * size * 3);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => new Scan2DResampler().Render(source, request, owner, cancellation.Token));
        Assert.AreEqual(1, owner.DisposeCount);
    }

    [TestMethod]
    public void ContractsAreNeutralAndDoNotReferenceBScanOrWpf()
    {
        Type[] scanTypes = typeof(Scan2DSourceGrid).Assembly.GetTypes()
            .Where(x => x.Namespace == typeof(Scan2DSourceGrid).Namespace).ToArray();
        Assert.IsFalse(scanTypes.SelectMany(x => x.GetMembers())
            .SelectMany(x => x switch
            {
                System.Reflection.PropertyInfo p => new[] { p.PropertyType },
                System.Reflection.MethodInfo m => m.GetParameters().Select(p => p.ParameterType).Append(m.ReturnType),
                _ => Array.Empty<Type>()
            }).Any(x => x.Name.Contains("BScan", StringComparison.Ordinal) ||
                (x.Namespace?.StartsWith("System.Windows", StringComparison.Ordinal) ?? false)));
    }

    private static Scan2DGridGeometry SameExtent(int sourceWidth, int sourceHeight, int destinationWidth, int destinationHeight) =>
        new((double)sourceWidth / (2 * destinationWidth) - .5,
            (double)sourceHeight / (2 * destinationHeight) - .5,
            (double)sourceWidth / destinationWidth, (double)sourceHeight / destinationHeight);

    private sealed class TrackingOwner(int length) : IMemoryOwner<byte>
    {
        private byte[]? _bytes = new byte[length];
        public int DisposeCount { get; private set; }
        public Memory<byte> Memory => _bytes ?? throw new ObjectDisposedException(nameof(TrackingOwner));
        public void Dispose() { if (_bytes is not null) { _bytes = null; DisposeCount++; } }
    }
}
