using UTStudio.Contracts.Inspection;
using UTStudio.Contracts.Processing;
using UTStudio.Domain.Acquisition;
using UTStudio.Domain.Processing;
using UTStudio.Processing.Core;

namespace UTStudio.Application;

public sealed class ProductiveGateProcessing : IAsyncDisposable
{
    private readonly InMemoryGateDerivedStore _store;
    internal ProductiveGateProcessing(CommittedGateProcessingOrchestrator ingress, CompiledGatePlan plan,
        InMemoryGateDerivedStore store)
    { Ingress = ingress; Plan = plan; _store = store; }
    public ICommittedGateProcessingIngress Ingress { get; }
    public CompiledGatePlan Plan { get; }
    public IGateDerivedStore DerivedStore => _store;
    public async ValueTask DisposeAsync()
    {
        Exception? primary = null;
        try { await ((IAsyncDisposable)Ingress).DisposeAsync().ConfigureAwait(false); }
        catch (Exception error) { primary = error; }
        try { await _store.DisposeAsync().ConfigureAwait(false); }
        catch (Exception error) when (primary is not null)
        { throw new AggregateException("Productive gate processing cleanup failed.", primary, error); }
        if (primary is not null)
        { System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(primary).Throw(); }
    }
}

public static class ProductiveGateProcessingFactory
{
    public static ProductiveGateProcessing Create(IInspectionDataStore store,
        ConventionalAcquisitionConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(store); ArgumentNullException.ThrowIfNull(configuration);
        CompiledGatePlan compiled = CompileDefault(configuration);
        var derived = new InMemoryGateDerivedStore();
        return new(new(store, store, derived, new GateEvaluator(new GateScratchPool())), compiled, derived);
    }

    internal static CompiledGatePlan CompileDefault(ConventionalAcquisitionConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
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
        return compiled;
    }
}
