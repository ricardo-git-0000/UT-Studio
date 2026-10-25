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
        Assert.AreEqual(0d, item.Thresholds.Single().AmplitudePercent);

        GateGeometrySnapshot missing = Snapshot(GateSignalDomain.RawSigned, null, null, null);
        ProjectedGateGeometryItem absent = GateGeometryProjector.Project(missing, context).Single();
        Assert.IsNull(absent.StartCoordinate); Assert.IsNull(absent.EndCoordinate);
        Assert.IsEmpty(absent.Thresholds);
    }

    [TestMethod]
    public void ProjectsOneThresholdFromThePresentedSignalRangeRegardlessOfLegacyDomainMetadata()
    {
        var rf = new GateGeometryProjectionContext(ScanTimeViewportOperations.Create(0, 1),
            0, 100, GateAmplitudePresentationKind.RawRf, 0, 200);
        var rectified = new GateGeometryProjectionContext(ScanTimeViewportOperations.Create(0, 1),
            0, 100, GateAmplitudePresentationKind.RectifiedMagnitude, 0, 100);

        ProjectedGateGeometryItem positiveRf = GateGeometryProjector.Project(
            Snapshot(GateSignalDomain.Positive, 1, 0, 1), rf).Single();
        Assert.AreEqual(100d, positiveRf.Thresholds.Single().AmplitudePercent);
        ProjectedGateGeometryItem negativeRf = GateGeometryProjector.Project(
            Snapshot(GateSignalDomain.NegativeMagnitude, 1, 0, 1), rf).Single();
        Assert.AreEqual(100d, negativeRf.Thresholds.Single().AmplitudePercent);
        ProjectedGateGeometryItem absoluteRf = GateGeometryProjector.Project(
            Snapshot(GateSignalDomain.Absolute, .5, 0, 1), rf).Single();
        CollectionAssert.AreEqual(new[] { 0d }, absoluteRf.Thresholds.Select(x => x.AmplitudePercent).ToArray());
        ProjectedGateGeometryItem positiveRectified = GateGeometryProjector.Project(
            Snapshot(GateSignalDomain.Positive, 1, 0, 1), rectified).Single();
        Assert.AreEqual(100d, positiveRectified.Thresholds.Single().AmplitudePercent);
        ProjectedGateGeometryItem rectifiedInput = GateGeometryProjector.Project(
            Snapshot(GateSignalDomain.RawSigned, .5, 0, 1), rectified).Single();
        Assert.AreEqual(GateGeometryProjectionStatus.Projected, rectifiedInput.Status);
        Assert.AreEqual(50d, rectifiedInput.Thresholds.Single().AmplitudePercent);
    }

    [TestMethod]
    public void ProjectsExactEventOnSignedRfOrdinateInsteadOfDetectorMagnitude()
    {
        GateGeometrySnapshot snapshot = Snapshot(GateSignalDomain.Absolute, .5, 0, 1,
            new(GateResultStatus.Detected, GateDetectionEventKind.LocalMaximum, 1, 1, .5,
                12000, -12000, 1000, 2000));
        var context = new GateGeometryProjectionContext(ScanTimeViewportOperations.Create(0, 1),
            0, 100, GateAmplitudePresentationKind.RawRf, 0, 200);

        GateEventProjection detected = GateGeometryProjector.Project(snapshot, context).Single().DetectedEvent!;

        Assert.AreEqual(50d, detected.TimeCoordinate, 1e-12);
        Assert.IsLessThan(100d, detected.AmplitudeCoordinate,
            "A negative RF event must remain on the negative semiaxis even when the detector uses magnitude.");
        Assert.AreEqual(-12000d, detected.DisplayedRfAmplitudeCounts);
        Assert.AreEqual(12000d, detected.DetectorAmplitudeCounts);
    }

    private static GateGeometrySnapshot Snapshot(GateSignalDomain domain, double? threshold,
        double? start, double? end, GateExactEventSnapshot? detectedEvent = null)
    {
        CompiledGatePlan plan = GateEvaluationTestData.CompilePreview(
            GateEvaluationTestData.Gate("A", 4, domain: domain, threshold: threshold));
        ConfirmedGateTraceDescriptor trace = GateEvaluationTestData.Trace(4);
        var authority = new GatePlanAuthorityKey(trace.ConfirmedIdentity.Stream.InspectionId, plan.Source.Id);
        GateGeometrySourceIdentity source = GateGeometrySourceIdentity.PreviewSource(authority,
            new(1), new(2), plan.Signature, new(Guid.NewGuid(), 1));
        return new(source, trace.ConfirmedIdentity, trace.SourceInspectionRevision,
            [new(new GateId("A"), new GateRevision(1), domain, GateResultStatus.Detected,
                null, start, end, threshold, detectedEvent)]);
    }
}
