using UTStudio.Contracts.Inspection;
using UTStudio.Contracts.Processing;
using UTStudio.Core.Tests.Processing;
using UTStudio.Domain.Processing;
using UTStudio.Visualization.Core;

namespace UTStudio.Core.Tests.Visualization;

[TestClass]
public sealed class GateGeometryProjectorTests
{
    [TestMethod]
    public void ProjectsOriginalTimeWithoutClippingAndPreservesMissingValues()
    {
        GateGeometrySnapshot snapshot = Snapshot(GateSignalDomain.RawSigned, .5, -1, 3);
        var context = new GateGeometryProjectionContext(ScanTimeViewportOperations.Create(0, 2),
            10, 100, GateAmplitudePresentationKind.RawRf, 20, 200);
        ProjectedGateGeometryItem item = GateGeometryProjector.Project(snapshot, context).Single();
        Assert.AreEqual(-40d, item.StartCoordinate);
        Assert.AreEqual(160d, item.EndCoordinate);
        Assert.AreEqual(-.5d / 32768d * 100d, item.Thresholds.Single().AmplitudePercent);

        GateGeometrySnapshot missing = Snapshot(GateSignalDomain.RawSigned, null, null, null);
        ProjectedGateGeometryItem absent = GateGeometryProjector.Project(missing, context).Single();
        Assert.IsNull(absent.StartCoordinate); Assert.IsNull(absent.EndCoordinate);
        Assert.IsEmpty(absent.Thresholds);
    }

    [TestMethod]
    public void ProjectsRfAndRectifiedThresholdConventionsWithoutSamples()
    {
        var rf = new GateGeometryProjectionContext(ScanTimeViewportOperations.Create(0, 1),
            0, 100, GateAmplitudePresentationKind.RawRf, 0, 200);
        var rectified = new GateGeometryProjectionContext(ScanTimeViewportOperations.Create(0, 1),
            0, 100, GateAmplitudePresentationKind.RectifiedMagnitude, 0, 100);

        ProjectedGateGeometryItem positiveRf = GateGeometryProjector.Project(
            Snapshot(GateSignalDomain.Positive, 1, 0, 1), rf).Single();
        Assert.AreEqual(100d * 32767d / 32768d, positiveRf.Thresholds.Single().AmplitudePercent);
        ProjectedGateGeometryItem negativeRf = GateGeometryProjector.Project(
            Snapshot(GateSignalDomain.NegativeMagnitude, 1, 0, 1), rf).Single();
        Assert.AreEqual(-100d, negativeRf.Thresholds.Single().AmplitudePercent);
        ProjectedGateGeometryItem absoluteRf = GateGeometryProjector.Project(
            Snapshot(GateSignalDomain.Absolute, .5, 0, 1), rf).Single();
        CollectionAssert.AreEqual(new[] { -50d, 50d }, absoluteRf.Thresholds.Select(x => x.AmplitudePercent).ToArray());
        ProjectedGateGeometryItem positiveRectified = GateGeometryProjector.Project(
            Snapshot(GateSignalDomain.Positive, 1, 0, 1), rectified).Single();
        Assert.AreEqual(100d, positiveRectified.Thresholds.Single().AmplitudePercent);
        ProjectedGateGeometryItem invalid = GateGeometryProjector.Project(
            Snapshot(GateSignalDomain.RawSigned, .5, 0, 1), rectified).Single();
        Assert.AreEqual(GateGeometryProjectionStatus.IncompatibleAmplitudeDomain, invalid.Status);
        Assert.IsEmpty(invalid.Thresholds);
    }

    private static GateGeometrySnapshot Snapshot(GateSignalDomain domain, double? threshold,
        double? start, double? end)
    {
        CompiledGatePlan plan = GateEvaluationTestData.Compile(
            GateEvaluationTestData.Gate("A", 4, domain: domain, threshold: threshold));
        ConfirmedGateTraceDescriptor trace = GateEvaluationTestData.Trace(4);
        var authority = new GatePlanAuthorityKey(trace.ConfirmedIdentity.Stream.InspectionId, plan.Source.Id);
        GateGeometrySourceIdentity source = GateGeometrySourceIdentity.PreviewSource(authority,
            plan.Source.Revision, plan.Signature, new(Guid.NewGuid(), 1));
        return new(source, trace.ConfirmedIdentity, trace.SourceInspectionRevision,
            [new(new GateId("A"), new GateRevision(1), domain, GateResultStatus.Detected,
                null, start, end, threshold)]);
    }
}
