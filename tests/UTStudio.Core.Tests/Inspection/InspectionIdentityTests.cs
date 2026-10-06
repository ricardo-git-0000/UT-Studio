using UTStudio.Contracts.Inspection;
using UTStudio.Domain.Acquisition;
using UTStudio.Domain.Inspection;

namespace UTStudio.Core.Tests.Inspection;

[TestClass]
public sealed class InspectionIdentityTests
{
    [TestMethod]
    public void DefaultsAreInvalidAndZeroValuesAreAllowedWhereSpecified()
    {
        Assert.IsFalse(default(InspectionId).IsValid);
        Assert.IsFalse(default(UtStreamIdentity).IsValid);
        Assert.IsFalse(default(TraceIdentity).IsValid);
        Assert.ThrowsExactly<ArgumentException>(() => new InspectionId(Guid.Empty));
        Assert.IsFalse(default(BeamId).IsValid);
        Assert.IsFalse(default(FocalLawId).IsValid);
        Assert.AreEqual(0, new BeamId(0).Value);
        Assert.AreEqual(0, new FocalLawId(0).Value);
        Assert.IsTrue(new BeamId(int.MaxValue).IsValid);
        Assert.IsTrue(new FocalLawId(int.MaxValue).IsValid);
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new BeamId(-1));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new FocalLawId(-1));
        Assert.ThrowsExactly<ArgumentException>(() => new UtStreamIdentity(Inspection, Run,
            new("source"), new(0), default(BeamId)));
        Assert.ThrowsExactly<ArgumentException>(() => new UtStreamIdentity(Inspection, Run,
            new("source"), new(0), null, default(FocalLawId)));
        _ = new TraceIdentity(Stream(), 0);
        _ = new TraceIdentity(Stream(), ulong.MaxValue);
    }

    [TestMethod]
    public void EqualityAndHashIncludeEveryIdentityDimensionButNotSequence()
    {
        UtStreamIdentity stream = Stream();
        var first = new TraceIdentity(stream, 7);
        var same = new TraceIdentity(stream, 7);
        var next = new TraceIdentity(stream, 8);
        var beamed = new TraceIdentity(new(stream.InspectionId, stream.AcquisitionRunId,
            stream.SourceId, stream.PhysicalChannelId, new BeamId(0)), 7);
        Assert.AreEqual(first, same);
        Assert.AreEqual(first.GetHashCode(), same.GetHashCode());
        Assert.AreNotEqual(first, next);
        Assert.AreNotEqual(first, beamed);
        Assert.AreNotEqual(first, new TraceIdentity(new(new InspectionId(Guid.NewGuid()), Run,
            stream.SourceId, stream.PhysicalChannelId), 7));
        Assert.AreNotEqual(first, new TraceIdentity(new(Inspection, new AcquisitionRunId(Guid.NewGuid()),
            stream.SourceId, stream.PhysicalChannelId), 7));
        Assert.AreNotEqual(first, new TraceIdentity(new(Inspection, Run, new("other-source"),
            stream.PhysicalChannelId), 7));
        Assert.AreNotEqual(first, new TraceIdentity(new(Inspection, Run, stream.SourceId, new(1)), 7));
        Assert.AreNotEqual(first, new TraceIdentity(new(Inspection, Run, stream.SourceId,
            stream.PhysicalChannelId, null, new FocalLawId(0)), 7));
        Assert.AreNotEqual(Descriptor(first, 100), Descriptor(first, 101));
    }

    [TestMethod]
    public void DescriptorMetadataRejectsUndefinedValuesAndHasSafeDiagnosticText()
    {
        TraceIdentity identity = new(Stream(), 1);
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => Descriptor(identity,
            clockQuality: (UtClockQuality)99));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => Descriptor(identity,
            sampleEncoding: (UtSampleEncoding)99));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => Descriptor(identity,
            provenance: (UtTraceProvenance)99));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => Descriptor(identity,
            traceQuality: (UtTraceQuality)99));
        string diagnostic = Descriptor(identity).ToString();
        Assert.IsFalse(diagnostic.Contains("samples=", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(diagnostic.Contains("payload", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public void DiagnosticFormatContainsOnlyIdentifiers()
    {
        string value = new TraceIdentity(Stream(), 42).ToString();
        StringAssert.Contains(value, "inspection=");
        StringAssert.Contains(value, "index=42");
        Assert.IsFalse(value.Contains("samples", StringComparison.OrdinalIgnoreCase));
    }

    internal static InspectionId Inspection => new(Guid.Parse("10000000-0000-0000-0000-000000000001"));
    internal static AcquisitionRunId Run => new(Guid.Parse("20000000-0000-0000-0000-000000000002"));
    internal static UtStreamIdentity Stream() => new(Inspection, Run, new("inspection-test"), new(0));
    internal static TraceDescriptor Descriptor(TraceIdentity identity, ulong sequence = 0, int samples = 4,
        double rate = 10_000_000, UtClockQuality clockQuality = UtClockQuality.Unknown,
        UtSampleEncoding sampleEncoding = UtSampleEncoding.SignedInt16,
        UtTraceProvenance provenance = UtTraceProvenance.Unknown,
        UtTraceQuality traceQuality = UtTraceQuality.Unknown) =>
        new(identity, sequence, TimeSpan.FromTicks((long)sequence), DateTimeOffset.UnixEpoch, null,
            samples, rate, 0, UtSignalMode.Rf, clockQuality, sampleEncoding, provenance, traceQuality);
}
