using UTStudio.Domain.Acquisition;

namespace UTStudio.Core.Tests.Domain;

[TestClass]
public sealed class ConventionalUtModelTests
{
    [TestMethod]
    public void SourceIdsRejectEmptyValuesAndUseOrdinalIdentity()
    {
        Assert.ThrowsExactly<ArgumentNullException>(() => new UtSourceId(null!));
        foreach (var value in new[] { "", " ", "\t" })
        {
            Assert.ThrowsExactly<ArgumentException>(() => new UtSourceId(value));
        }

        Assert.AreEqual(new UtSourceId("source"), new UtSourceId("source"));
        Assert.AreNotEqual(new UtSourceId("source"), new UtSourceId("SOURCE"));
        Assert.AreEqual(" source ", new UtSourceId(" source ").Value);
        Assert.IsFalse(default(UtSourceId).IsValid);
        Assert.AreEqual(string.Empty, default(UtSourceId).Value);
    }

    [TestMethod]
    public void RunIdsRequireNonemptyCallerSuppliedGuid()
    {
        Assert.ThrowsExactly<ArgumentException>(() => new AcquisitionRunId(Guid.Empty));
        var guid = Guid.Parse("61a0a293-b425-40af-8645-8ec38c732bf2");
        Assert.AreEqual(new AcquisitionRunId(guid), new AcquisitionRunId(guid));
        Assert.IsTrue(new AcquisitionRunId(guid).IsValid);
        Assert.IsFalse(default(AcquisitionRunId).IsValid);
    }

    [TestMethod]
    public void PhysicalChannelIdentityIsNotLimitedByChannelCount()
    {
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new PhysicalChannelId(-1));
        Assert.AreEqual(new PhysicalChannelId(0), default(PhysicalChannelId));
        Assert.AreEqual(1000, new PhysicalChannelId(1000).Value);
    }

    [TestMethod]
    [DataRow(1)]
    [DataRow(65_535)]
    public void SampleCountIncludesProductBoundaries(int sampleCount)
    {
        var configuration = new ConventionalAcquisitionConfiguration(default, sampleCount, 100_000_000);
        Assert.AreEqual(sampleCount, configuration.SampleCount);
        Assert.AreEqual(100_000_000d, configuration.SampleRateHz);
        Assert.AreEqual(UtSignalMode.Rf, configuration.SignalMode);
    }

    [TestMethod]
    [DataRow(-1)]
    [DataRow(0)]
    [DataRow(65_536)]
    public void InvalidSampleCountIsRejected(int sampleCount)
    {
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => new ConventionalAcquisitionConfiguration(default, sampleCount, 50_000_000));
    }

    [TestMethod]
    public void SamplingFrequencyMustBeFinitePositiveAndWithinProductLimit()
    {
        foreach (var rate in new[] { 0, -1, 100_000_001, double.NaN, double.PositiveInfinity, double.NegativeInfinity })
        {
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(
                () => new ConventionalAcquisitionConfiguration(default, 1, rate));
        }

        Assert.AreEqual(double.Epsilon,
            new ConventionalAcquisitionConfiguration(default, 1, double.Epsilon).SampleRateHz);
    }

    [TestMethod]
    public void FiniteNegativeOffsetIsAllowedButUnknownSignalModeIsNot()
    {
        var configuration = new ConventionalAcquisitionConfiguration(new PhysicalChannelId(7), 2, 50_000_000, -1e-6);
        Assert.AreEqual(-1e-6, configuration.FirstSampleOffsetSeconds);
        Assert.AreEqual(new PhysicalChannelId(7), configuration.PhysicalChannelId);
        foreach (var offset in new[] { double.NaN, double.PositiveInfinity, double.NegativeInfinity })
        {
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(
                () => new ConventionalAcquisitionConfiguration(default, 2, 50_000_000, offset));
        }

        Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => new ConventionalAcquisitionConfiguration(default, 2, 50_000_000, signalMode: (UtSignalMode)1));
    }

    [TestMethod]
    public void MetadataRejectsDefaultIdentifiersAndNullConfiguration()
    {
        var configuration = new ConventionalAcquisitionConfiguration(default, 2, 50_000_000);
        var source = new UtSourceId("synthetic");
        var run = new AcquisitionRunId(Guid.NewGuid());
        Assert.ThrowsExactly<ArgumentException>(
            () => new ConventionalUtFrameMetadata(default, run, configuration, default));
        Assert.ThrowsExactly<ArgumentException>(
            () => new ConventionalUtFrameMetadata(source, default, configuration, default));
        Assert.ThrowsExactly<ArgumentNullException>(
            () => new ConventionalUtFrameMetadata(source, run, null!, default));
    }

    [TestMethod]
    public void MetadataSharesValidatedRfConfigurationAndNormalizesUtc()
    {
        var configuration = new ConventionalAcquisitionConfiguration(default, 2048, 50_000_000);
        var origin = new DateTimeOffset(2026, 9, 12, 12, 0, 0, TimeSpan.FromHours(2));
        var source = new UtSourceId("synthetic");
        var run = new AcquisitionRunId(Guid.NewGuid());
        var metadata = new ConventionalUtFrameMetadata(source, run, configuration, origin);

        Assert.AreSame(configuration, metadata.Configuration);
        Assert.AreEqual(source, metadata.SourceId);
        Assert.AreEqual(run, metadata.RunId);
        Assert.AreEqual(origin, metadata.RunStartedAtUtc);
        Assert.AreEqual(TimeSpan.Zero, metadata.RunStartedAtUtc.Offset);
        Assert.AreEqual(UtSignalMode.Rf, metadata.Configuration.SignalMode);
    }
}
