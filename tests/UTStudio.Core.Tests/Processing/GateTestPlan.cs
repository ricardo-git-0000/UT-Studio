using UTStudio.Contracts.Processing;
using UTStudio.Domain.Processing;

namespace UTStudio.Core.Tests.Processing;

internal static class GateTestPlan
{
    internal static GateDefinition Gate(string id, string? dependency = null,
        GateDetectorKind detector = GateDetectorKind.GlobalMaximum,
        GateSignalDomain domain = GateSignalDomain.RawSigned,
        double? threshold = null, GateHysteresis? hysteresis = null,
        GateLocalExtremaParameters? local = null, double offsetSeconds = 0,
        double durationSeconds = 0.000001, uint revision = 1,
        IEnumerable<GateMeasureKind>? measures = null,
        GateAcceptedTraceQuality quality = GateAcceptedTraceQuality.GoodAndDegraded)
    {
        GateTimeReference reference = dependency is null
            ? GateTimeReference.TraceOrigin() : GateTimeReference.GateEvent(new GateId(dependency));
        return new(new GateId(id), new GateRevision(revision),
            new GateWindowDefinition(reference, offsetSeconds, durationSeconds),
            domain, new GateDetectorDefinition(detector,
                threshold.HasValue ? new GateThreshold(threshold.Value) : null, hysteresis, local),
            measures ?? [GateMeasureKind.Amplitude, GateMeasureKind.Status], quality);
    }

    internal static GatePlan Plan(IEnumerable<GateDefinition> gates, string id = "plan", uint revision = 1,
        ushort schema = 1, ushort algorithm = 1) => new(new GatePlanId(id), new GatePlanRevision(revision),
            new GateSchemaVersion(schema), new GateAlgorithmVersion(algorithm), gates);
}
