using UTStudio.Contracts.Acquisition;
using UTStudio.Contracts.Inspection;
using UTStudio.Contracts.Processing;
using UTStudio.Domain.Acquisition;
using UTStudio.Domain.Inspection;
using UTStudio.Processing.Core;

namespace UTStudio.Application;

/// <summary>
/// Sole inspection-scoped owner of editable gate authority, live/historical processing, derived state and exact
/// geometry. Sessions and views borrow its ports and never dispose individual owners.
/// </summary>
public sealed class ProductiveGateInspectionScope : IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly InMemoryGateDerivedStore _store;
    private readonly CommittedGateProcessingOrchestrator _live;
    private readonly InMemoryGatePlanAuthorityOwner _authorityOwner;
    private readonly GateHistoricalRunServices _historical;
    private readonly Action? _sessionCleanupStarted;
    private ApplicationSession? _session;
    private Task? _disposeTask;

    private ProductiveGateInspectionScope(InspectionId inspectionId, CompiledGatePlan initialPlan,
        InMemoryGateDerivedStore store, CommittedGateProcessingOrchestrator live,
        InMemoryGatePlanAuthorityOwner authorityOwner, GatePlanPreviewCompiler previewCompiler,
        GatePlanApplyServices applyServices, GateHistoricalRunServices historical,
        ExactGateGeometryEvaluator geometry, Action? sessionCleanupStarted)
    {
        InspectionId = inspectionId; InitialPlan = initialPlan; _store = store; _live = live;
        _authorityOwner = authorityOwner; PreviewCompiler = previewCompiler; ApplyServices = applyServices;
        _historical = historical; GeometryEvaluator = geometry;
        _sessionCleanupStarted = sessionCleanupStarted;
    }

    public InspectionId InspectionId { get; }
    public GatePlanAuthorityKey AuthorityKey => new(InspectionId, InitialPlan.Source.Id);
    internal CompiledGatePlan InitialPlan { get; }
    public IGatePlanAuthorityReader Authority => ApplyServices.AuthorityReader;
    public IGatePlanPreviewCompiler PreviewCompiler { get; }
    public IGateGeometryEvaluator GeometryEvaluator { get; }
    internal ICommittedGateProcessingIngress Ingress => _live;
    internal IGateDerivedStore DerivedStore => _store;
    internal GatePlanApplyServices ApplyServices { get; }

    public static ValueTask<ProductiveGateInspectionScope> CreateAsync(InspectionId inspectionId,
        IInspectionDataStore inspectionStore, ConventionalAcquisitionConfiguration configuration,
        CancellationToken cancellationToken = default) =>
        CreateCoreAsync(inspectionId, inspectionStore, configuration, new(), cancellationToken);

    internal static async ValueTask<ProductiveGateInspectionScope> CreateCoreAsync(InspectionId inspectionId,
        IInspectionDataStore inspectionStore, ConventionalAcquisitionConfiguration configuration,
        GateHeadlessInspectionOptions options, CancellationToken cancellationToken,
        Func<string, Exception?>? constructionFailure = null, Action<string>? rollbackObserved = null,
        Func<IPreparedGatePlanEpochPort, IPreparedGatePlanEpochPort>? epochDecorator = null,
        Action? sessionCleanupStarted = null)
    {
        if (!inspectionId.IsValid) throw new ArgumentException("A valid inspection is required.", nameof(inspectionId));
        ArgumentNullException.ThrowIfNull(inspectionStore); ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(options); cancellationToken.ThrowIfCancellationRequested();
        InMemoryGateDerivedStore? store = null; CommittedGateProcessingOrchestrator? live = null;
        InMemoryGatePlanAuthorityOwner? authority = null; GateHistoricalRunServices? historical = null;
        try
        {
            CompiledGatePlan initial = ProductiveGateProcessingFactory.CompileDefault(configuration);
            store = new(null, new(), options.MaximumReplacementCandidates);
            live = new(inspectionStore, inspectionStore, store, new(new()),
                new(maximumPlanEpochsPerRun: options.MaximumLiveEpochsPerRun));
            authority = new([new CommittedGatePlanSnapshot(inspectionId, initial)], default,
                options.AuthorityOptions(), null);
            IGatePlanAuthorityReader reader = authority.Seal();
            var compiler = new GatePlanPreviewCompiler();
            IPreparedGatePlanEpochPort epochs = epochDecorator?.Invoke(live) ?? live;
            var apply = new GatePlanApplyServices(reader, authority.PreparedCommits, epochs,
                store.DesiredReplacementControl, compiler);
            ThrowConstructionFailure(constructionFailure, "apply");
            var scheduler = new GateReprocessingScheduler(inspectionStore, store, options.SchedulerOptions());
            var replacement = new GateHistoricalReplacementCoordinator(scheduler, store.ReplacementControl,
                authority.PreparedCommits, options.MaximumHistoricalObservers);
            historical = new(replacement, scheduler, store.ReplacementControl, authority, live);
            ThrowConstructionFailure(constructionFailure, "historical");
            var geometry = new ExactGateGeometryEvaluator(inspectionStore, new CommittedGeometryGuard(reader));
            return new(inspectionId, initial, store, live, authority, compiler, apply, historical, geometry,
                sessionCleanupStarted);
        }
        catch (Exception primary)
        {
            List<Exception> failures = [primary];
            if (historical is not null)
                await ObserveRollbackAsync(historical.DisposeAsync(), "historical", rollbackObserved, failures).ConfigureAwait(false);
            else
            {
                if (live is not null)
                    await ObserveRollbackAsync(live.DisposeAsync(), "live", rollbackObserved, failures).ConfigureAwait(false);
                if (store is not null)
                    await ObserveRollbackAsync(store.DisposeAsync(), "store", rollbackObserved, failures).ConfigureAwait(false);
                if (authority is not null)
                {
                    try { authority.Dispose(); await authority.WaitForCloseAsync().ConfigureAwait(false); rollbackObserved?.Invoke("authority"); }
                    catch (Exception error) { failures.Add(error); }
                }
            }
            if (failures.Count == 1) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(primary).Throw();
            throw new AggregateException("Inspection gate scope construction failed and cleanup reported secondary errors.", failures);
        }
    }

    public ApplicationSession CreateSession(IUtFrameSource source,
        Func<AcquisitionRunId, IInspectionRunWriter> inspectionWriterFactory,
        ICommittedTraceSink? bScanSink = null, ICommittedTraceSink? aScanSink = null)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposeTask is not null, this);
            if (_session is not null) throw new InvalidOperationException("The inspection scope already owns a session.");
            return _session = new(source, InspectionId, inspectionWriterFactory, bScanSink, aScanSink,
                _live, InitialPlan, ApplyServices, _historical, _live, ownsGateServices: false,
                cleanupRequested: _sessionCleanupStarted);
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (_gate) return new(_disposeTask ??= DisposeCoreAsync());
    }

    private async Task DisposeCoreAsync()
    {
        List<Exception> failures = [];
        ApplicationSession? session; lock (_gate) session = _session;
        if (session is not null) try { await session.DisposeAsync().ConfigureAwait(false); } catch (Exception e) { failures.Add(e); }
        try { await _historical.DisposeAsync().ConfigureAwait(false); } catch (Exception e) { failures.Add(e); }
        if (failures.Count == 1) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failures[0]).Throw();
        if (failures.Count > 1) throw new AggregateException(failures);
    }

    private static void ThrowConstructionFailure(Func<string, Exception?>? hook, string stage)
    { if (hook?.Invoke(stage) is { } error) throw error; }
    private static async ValueTask ObserveRollbackAsync(ValueTask operation, string owner,
        Action<string>? observed, List<Exception> failures)
    {
        try { await operation.ConfigureAwait(false); observed?.Invoke(owner); }
        catch (Exception error) { failures.Add(error); }
    }

    private sealed class CommittedGeometryGuard(IGatePlanAuthorityReader authority) : IGateGeometrySourceGuard
    {
        public GateGeometrySourceValidationStatus Validate(GateGeometrySourceIdentity source)
        {
            if (source.Kind != GateGeometrySourceKind.Committed) return GateGeometrySourceValidationStatus.Superseded;
            try
            {
                GatePlanAuthoritySnapshot current = authority.Read(source.Authority);
                return current.Committed.Revision == source.BaseRevision &&
                    current.Committed.Revision == source.CandidateRevision &&
                    current.Committed.Signature.Equals(source.Signature) &&
                    current.Generation == source.AuthorityGeneration &&
                    current.PublicationSequence == source.PublicationSequence
                    ? GateGeometrySourceValidationStatus.Current : GateGeometrySourceValidationStatus.Superseded;
            }
            catch (ObjectDisposedException) { return GateGeometrySourceValidationStatus.Closed; }
            catch (KeyNotFoundException) { return GateGeometrySourceValidationStatus.Superseded; }
        }
    }
}
