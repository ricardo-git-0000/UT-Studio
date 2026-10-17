using UTStudio.Contracts.Inspection;
using UTStudio.Contracts.Processing;
using UTStudio.Domain.Acquisition;
using UTStudio.Domain.Processing;
using UTStudio.Processing.Core;

namespace UTStudio.Application;

public sealed class ProductiveGateProcessing : IAsyncDisposable
{
    internal ProductiveGateProcessing(CommittedGateProcessingOrchestrator ingress, CompiledGatePlan plan)
    { Ingress = ingress; Plan = plan; }
    public ICommittedGateProcessingIngress Ingress { get; }
    public CompiledGatePlan Plan { get; }
    public ValueTask DisposeAsync() => ((IAsyncDisposable)Ingress).DisposeAsync();
}

public static class ProductiveGateProcessingFactory
{
    public static ProductiveGateProcessing Create(IInspectionDataStore store,
        ConventionalAcquisitionConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(store); ArgumentNullException.ThrowIfNull(configuration);
        double duration = configuration.SampleCount / configuration.SampleRateHz;
        var gate = new GateDefinition(new GateId("default-global-maximum"), new GateRevision(1),
            new GateWindowDefinition(GateTimeReference.TraceOrigin(), configuration.FirstSampleOffsetSeconds, duration),
            GateSignalDomain.Absolute, new GateDetectorDefinition(GateDetectorKind.GlobalMaximum),
            [GateMeasureKind.Amplitude, GateMeasureKind.DetectionTime, GateMeasureKind.Status, GateMeasureKind.Quality]);
        var plan = new GatePlan(new GatePlanId("productive-default"), new GatePlanRevision(1),
            new GateSchemaVersion(GatePlanCompilerOptions.CurrentSchemaVersion),
            new GateAlgorithmVersion(GatePlanCompilerOptions.CurrentAlgorithmVersion), [gate]);
        GatePlanCompilationResult result = new GatePlanCompiler().Compile(plan);
        CompiledGatePlan compiled = result.CompiledPlan ?? throw new InvalidOperationException(
            $"The productive gate plan did not compile: {string.Join(", ", result.Errors.Select(x => x.Message))}");
        return new(new(store, store, new TransientGateEvaluationBatchSink(),
            new GateEvaluator(new GateScratchPool())), compiled);
    }
}
