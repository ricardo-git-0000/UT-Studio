using UTStudio.Contracts.Inspection;
using UTStudio.Contracts.Processing;
using UTStudio.Domain.Acquisition;
using UTStudio.Domain.Inspection;
using UTStudio.Processing.Core;

namespace UTStudio.Core.Tests.Processing;

internal static class GateEvaluationTestData
{
    internal static ConfirmedGateTraceDescriptor Trace(int sampleCount, double rate = 1_000_000,
        double offset = 0, UtTraceQuality quality = UtTraceQuality.Good,
        InspectionRevision? revision = null, UtTraceQuality? descriptorQuality = null,
        UtSampleEncoding sampleEncoding = UtSampleEncoding.SignedInt16, ulong sequence = 9,
        TimeSpan? elapsed = null, DateTimeOffset? timestamp = null,
        ScanSpatialCoordinate? spatial = null, UtClockQuality clockQuality = UtClockQuality.Synchronized,
        UtTraceProvenance provenance = UtTraceProvenance.Simulator)
    {
        var stream = new UtStreamIdentity(new InspectionId(Guid.Parse("10000000-0000-0000-0000-000000000001")),
            new AcquisitionRunId(Guid.Parse("20000000-0000-0000-0000-000000000002")),
            new UtSourceId("processing-test"), new PhysicalChannelId(0));
        var identity = new TraceIdentity(stream, 7);
        var descriptor = new TraceDescriptor(identity, sequence, elapsed ?? TimeSpan.Zero,
            timestamp ?? DateTimeOffset.UnixEpoch, spatial, sampleCount, rate, offset, UtSignalMode.Rf,
            clockQuality, sampleEncoding, provenance, descriptorQuality ?? quality);
        return new(descriptor, identity, revision ?? new InspectionRevision(5), quality);
    }

    internal static CompiledGatePlan Compile(params GateDefinition[] gates)
    {
        GatePlanCompilationResult result = new GatePlanCompiler(new(16)).Compile(GateTestPlan.Plan(gates));
        Assert.IsTrue(result.IsCompiled, string.Join(";", result.Errors.Select(x => x.Message)));
        return result.CompiledPlan!;
    }

    internal static CompiledGatePlan CompilePreview(params GateDefinition[] gates)
    {
        GatePlan plan = GateTestPlan.Plan(gates, revision: 2);
        GatePlanCompilationResult result = new GatePlanCompiler(new(16)).Compile(plan);
        Assert.IsTrue(result.IsCompiled, string.Join(";", result.Errors.Select(x => x.Message)));
        return result.CompiledPlan!;
    }

    internal static GateDefinition Gate(string id, int samples, double rate = 1_000_000,
        string? dependency = null, GateDetectorKind detector = GateDetectorKind.GlobalMaximum,
        GateSignalDomain domain = GateSignalDomain.RawSigned, double? threshold = null,
        GateHysteresis? hysteresis = null, GateLocalExtremaParameters? local = null,
        double offsetSeconds = 0, IEnumerable<GateMeasureKind>? measures = null) => GateTestPlan.Gate(id,
            dependency, detector, domain, threshold, hysteresis, local, offsetSeconds, samples / rate,
            measures: measures ?? [GateMeasureKind.Amplitude, GateMeasureKind.DetectionTime,
                GateMeasureKind.TimeOfFlight, GateMeasureKind.Status, GateMeasureKind.Quality]);
}

internal sealed class TestGateScratchPool : IGateScratchPool
{
    private readonly bool _reject;
    private readonly Exception? _returnError;
    private readonly GateScratchPool _owner = new();
    private long _rentCount;
    private long _returnCount;
    private long _rejectionCount;
    private int _active;
    internal TestGateScratchPool(bool reject = false, bool throwOnReturn = false, Exception? returnError = null)
    { _reject = reject; _returnError = returnError ?? (throwOnReturn ? new InvalidOperationException("test return failure") : null); }
    public bool TryRent(int sampleCount, out GateScratchLease? lease)
    {
        if (_reject) { _rejectionCount++; lease = null; return false; }
        _rentCount++; _active++;
        int bucket = 16; while (bucket < sampleCount) { bucket <<= 1; }
        lease = new GateScratchLease(_owner, _rentCount, new GateScratchBuffer(bucket)); return true;
    }
    public void Return(GateScratchLease lease)
    {
        _returnCount++; _active--;
        if (_returnError is not null) { throw _returnError; }
    }
    public GateScratchDiagnostics Snapshot => new(_rentCount, _returnCount, _rejectionCount, _active,
        _active, 0, 0, 0);
}
