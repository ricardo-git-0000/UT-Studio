using BenchmarkDotNet.Attributes;
using UTStudio.Contracts.Inspection;
using UTStudio.Contracts.Processing;
using UTStudio.Domain.Acquisition;
using UTStudio.Domain.Inspection;
using UTStudio.Domain.Processing;
using UTStudio.Inspection.Core;
using UTStudio.Processing.Core;

namespace UTStudio.Benchmarks;

/// <summary>Diagnostic Phase 5 dry smoke; includes scheduling and store publication, with no baseline claim.</summary>
[MemoryDiagnoser]
public class GateReprocessingBenchmarks
{
    private readonly InspectionId _inspection = new(Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"));
    private readonly AcquisitionRunId _run = new(Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"));
    private InMemoryInspectionDataStore _rf = null!;
    private UtStreamIdentity[] _streams = [];
    private InspectionRevision _finalRevision;
    private CompiledGatePlan _plan = null!;

    [Params(2_048, 65_535)] public int SampleCount { get; set; }
    [Params(1, 64, 256)] public int BlockSize { get; set; }
    [Params(1, 4)] public int StreamCount { get; set; }
    [Params(GateReprocessingPublicationPolicy.StoreOnly,
        GateReprocessingPublicationPolicy.ActivateOnComplete)]
    public GateReprocessingPublicationPolicy PublicationPolicy { get; set; }

    [GlobalSetup]
    public async Task Setup()
    {
        _streams = Enumerable.Range(0, StreamCount).Select(index => new UtStreamIdentity(_inspection, _run,
            new UtSourceId($"reprocessing-benchmark-{index}"), new PhysicalChannelId(index))).ToArray();
        const int tracesPerStream = 16;
        _rf = new(_inspection, new((long)SampleCount * sizeof(short) * tracesPerStream * StreamCount + 65_536,
            tracesPerStream * StreamCount + 8));
        IInspectionRunWriter writer = await _rf.OpenRunAsync(_run);
        short[] samples = new short[SampleCount];
        samples[SampleCount / 2] = short.MaxValue;
        foreach (UtStreamIdentity stream in _streams)
            for (ulong index = 0; index < tracesPerStream; index++)
            {
                var identity = new TraceIdentity(stream, index);
                var descriptor = new TraceDescriptor(identity, index, TimeSpan.Zero, null, null, SampleCount,
                    100_000_000, 0, UtSignalMode.Rf, provenance: UtTraceProvenance.Simulator,
                    traceQuality: UtTraceQuality.Good);
                InspectionAppendResult result = await writer.CommitAsync(new(new(descriptor, samples)));
                if (result.Status != InspectionAppendStatus.Committed) throw new InvalidOperationException(result.Status.ToString());
            }
        _finalRevision = (await writer.SealRunAsync()).FinalRevision;
        var gate = new GateDefinition(new GateId("A"), new GateRevision(1),
            new GateWindowDefinition(GateTimeReference.TraceOrigin(), 0, SampleCount / 100_000_000d),
            GateSignalDomain.Absolute, new GateDetectorDefinition(GateDetectorKind.GlobalMaximum),
            [GateMeasureKind.Amplitude, GateMeasureKind.Status, GateMeasureKind.Quality]);
        _plan = new GatePlanCompiler().Compile(new GatePlan(new GatePlanId("reprocessing-benchmark"),
            new GatePlanRevision(1), new GateSchemaVersion(GatePlanCompilerOptions.CurrentSchemaVersion),
            new GateAlgorithmVersion(GatePlanCompilerOptions.CurrentAlgorithmVersion), [gate])).CompiledPlan!;
    }

    [Benchmark]
    public async Task<long> ReprocessRepresentativeMatrix()
    {
        await using var derived = new InMemoryGateDerivedStore(new(128, 32_000_000));
        await using var scheduler = new GateReprocessingScheduler(_rf, derived,
            new(maximumWorkers: 1, maximumAdmittedJobs: 1), new GateScratchPool(1, 300_000));
        var descriptor = new GateReprocessingJobDescriptor(new(Guid.NewGuid()), new(Guid.NewGuid()),
            _inspection, _run, _finalRevision,
            _streams.Select(stream => new GateReprocessingStreamSelection(stream, [new(0, 15)])),
            _plan, BlockSize, PublicationPolicy,
            PublicationPolicy == GateReprocessingPublicationPolicy.ActivateOnComplete
                ? new DerivedViewId("benchmark-view") : null,
            snapshotPageSize: 2);
        IGateReprocessingJob job = (await scheduler.AdmitAsync(descriptor)).Job!;
        GateReprocessingProgressSnapshot result = await job.Completion;
        if (result.Lifecycle != GateReprocessingLifecycle.Completed) throw new InvalidOperationException(result.PrimaryError);
        if (!result.CleanupConfirmed || result.ActiveWorkers != 0 || result.ActiveCallbacks != 0 ||
            result.LeasesAcquired != result.LeasesReleased || result.BufferRents != result.BufferReturns ||
            result.ScratchRents != result.ScratchReturns || derived.Diagnostics.AdmittedWriters != 0 ||
            derived.Diagnostics.ReservedBatchCount != 0 || derived.Diagnostics.ReservedCoverageCount != 0 ||
            derived.Diagnostics.ReservedActivationCount != 0)
            throw new InvalidOperationException("Benchmark left historical resources unbalanced.");
        return result.NewlyCommittedBatchCount + result.ReusedBatchCount;
    }

    [GlobalCleanup]
    public async Task Cleanup() => await _rf.DisposeAsync();
}

/// <summary>Autonomous public-boundary Dry scenarios. Barriers are deterministic, not latency models.</summary>
[MemoryDiagnoser]
public class GateReprocessingOperationalBenchmarks
{
    private InMemoryInspectionDataStore _rf = null!;
    private InspectionId _inspection;
    private AcquisitionRunId _run;
    private InspectionRevision _revision;
    private UtStreamIdentity[] _streams = [];
    private CompiledGatePlan _plan = null!;

    public static IEnumerable<DryScenario> Scenarios =>
    [
        new("Normal_2048_b1_1s", 2_048, 1, 1, DryDependency.Normal, DryTermination.Complete, false, false,
            GateReprocessingPublicationPolicy.StoreOnly),
        new("Normal_65535_b256_4s", 65_535, 256, 4, DryDependency.Normal, DryTermination.Complete, false, false,
            GateReprocessingPublicationPolicy.ActivateOnComplete),
        new("ProviderSlow_Live", 2_048, 64, 1, DryDependency.ProviderBlocked, DryTermination.Complete, true, false,
            GateReprocessingPublicationPolicy.StoreOnly),
        new("StoreSlow_Live", 65_535, 64, 1, DryDependency.StoreBlocked, DryTermination.Complete, true, false,
            GateReprocessingPublicationPolicy.StoreOnly),
        new("Duplicate_MultipleBlocks", 2_048, 1, 4, DryDependency.Normal, DryTermination.Complete, false, true,
            GateReprocessingPublicationPolicy.StoreOnly),
        new("Cancel_ProviderBoundary", 65_535, 64, 1, DryDependency.ProviderBlocked, DryTermination.Cancel, false, false,
            GateReprocessingPublicationPolicy.StoreOnly),
        new("Cancel_StoreBoundary", 2_048, 256, 1, DryDependency.StoreBlocked, DryTermination.Cancel, false, false,
            GateReprocessingPublicationPolicy.StoreOnly),
        new("Stop_PublicReadBoundary", 65_535, 1, 1, DryDependency.ProviderBlocked, DryTermination.Stop, false, false,
            GateReprocessingPublicationPolicy.StoreOnly),
        new("Stop_PublicStoreBoundary", 2_048, 64, 4, DryDependency.StoreBlocked, DryTermination.Stop, false, false,
            GateReprocessingPublicationPolicy.StoreOnly),
        new("Activate_MultipleStreams", 2_048, 256, 4, DryDependency.Normal, DryTermination.Complete, false, false,
            GateReprocessingPublicationPolicy.ActivateOnComplete)
    ];

    [ParamsSource(nameof(Scenarios))]
    public DryScenario Scenario { get; set; } = Scenarios.First();

    public DryOutcome LastOutcome { get; private set; }

    [GlobalSetup]
    public async Task Setup()
    {
        _inspection = new(Guid.NewGuid()); _run = new(Guid.NewGuid());
        _streams = Enumerable.Range(0, Scenario.StreamCount).Select(index => new UtStreamIdentity(
            _inspection, _run, new UtSourceId($"dry-{index}"), new PhysicalChannelId(index))).ToArray();
        const int traces = 2;
        _rf = new(_inspection, new((long)Scenario.SampleCount * sizeof(short) * traces * Scenario.StreamCount + 1_000_000,
            traces * Scenario.StreamCount + 16));
        IInspectionRunWriter writer = await _rf.OpenRunAsync(_run);
        short[] samples = new short[Scenario.SampleCount]; samples[Scenario.SampleCount / 2] = short.MaxValue;
        foreach (UtStreamIdentity stream in _streams)
            for (ulong index = 0; index < traces; index++)
            {
                var identity = new TraceIdentity(stream, index);
                var descriptor = new TraceDescriptor(identity, index, TimeSpan.Zero, null, null,
                    Scenario.SampleCount, 100_000_000, 0, UtSignalMode.Rf,
                    provenance: UtTraceProvenance.Simulator, traceQuality: UtTraceQuality.Good);
                if ((await writer.CommitAsync(new(new(descriptor, samples)))).Status != InspectionAppendStatus.Committed)
                    throw new InvalidOperationException("Dry RF setup failed.");
            }
        _revision = (await writer.SealRunAsync()).FinalRevision;
        var gate = new GateDefinition(new GateId("A"), new GateRevision(1),
            new GateWindowDefinition(GateTimeReference.TraceOrigin(), 0, Scenario.SampleCount / 100_000_000d),
            GateSignalDomain.Absolute, new GateDetectorDefinition(GateDetectorKind.GlobalMaximum),
            [GateMeasureKind.Amplitude, GateMeasureKind.Status, GateMeasureKind.Quality]);
        _plan = new GatePlanCompiler().Compile(new GatePlan(new GatePlanId("dry-operational"),
            new GatePlanRevision(1), new GateSchemaVersion(GatePlanCompilerOptions.CurrentSchemaVersion),
            new GateAlgorithmVersion(GatePlanCompilerOptions.CurrentAlgorithmVersion), [gate])).CompiledPlan!;
    }

    [Benchmark]
    public async Task<long> RunOperationalScenario()
    {
        var barrier = new PublicBarrier();
        IInspectionSealedRunSnapshotProvider provider = Scenario.Dependency == DryDependency.ProviderBlocked
            ? new BlockingProvider(_rf, barrier) : _rf;
        await using var inner = new InMemoryGateDerivedStore(new(128, 32_000_000));
        IGateDerivedStore store = Scenario.Dependency == DryDependency.StoreBlocked
            ? new BlockingStore(inner, barrier) : inner;
        await using var scheduler = new GateReprocessingScheduler(provider, store,
            new(maximumWorkers: 2, maximumAdmittedJobs: 2, sampleBufferBudgetBytes: 262_144),
            new GateScratchPool(2, 600_000));
        GateReprocessingProgressSnapshot terminal = await RunOnceAsync(false);
        if (Scenario.Duplicate) terminal = await RunOnceAsync(true);
        GateDerivedStoreDiagnostics diagnostics = inner.Diagnostics;
        long expectedIdentities = 2L * Scenario.StreamCount;
        GateReprocessingLifecycle expectedLifecycle = Scenario.Termination == DryTermination.Complete
            ? GateReprocessingLifecycle.Completed : GateReprocessingLifecycle.Cancelled;
        bool expectedView = Scenario.Policy == GateReprocessingPublicationPolicy.ActivateOnComplete &&
            expectedLifecycle == GateReprocessingLifecycle.Completed;
        Validate(Scenario, terminal, diagnostics, expectedIdentities, expectedLifecycle,
            expectedView, inner.ActiveView is not null);
        LastOutcome = new(Scenario.Name, Scenario.SampleCount, Scenario.BlockSize, Scenario.StreamCount,
            terminal.Lifecycle, expectedIdentities, terminal.IdentitiesVisited,
            terminal.NewlyCommittedBatchCount, terminal.ReusedBatchCount, diagnostics.Revision.Value,
            expectedView, inner.ActiveView is not null, terminal.CleanupConfirmed,
            terminal.LeasesAcquired + terminal.BufferRents + terminal.ScratchRents);
        return LastOutcome.DiagnosticChecksum;

        async Task<GateReprocessingProgressSnapshot> RunOnceAsync(bool replay)
        {
            var descriptor = new GateReprocessingJobDescriptor(new(Guid.NewGuid()), new(Guid.NewGuid()),
                _inspection, _run, _revision,
                _streams.Select(stream => new GateReprocessingStreamSelection(stream, [new(0, 1)])),
                _plan, Scenario.BlockSize, Scenario.Policy,
                Scenario.Policy == GateReprocessingPublicationPolicy.ActivateOnComplete
                    ? new DerivedViewId($"dry-{Scenario.Name}-{replay}") : null, 1);
            IGateReprocessingJob job = (await scheduler.AdmitAsync(descriptor)).Job!;
            Task? stop = null;
            if (Scenario.Dependency != DryDependency.Normal)
            {
                await barrier.Entered.Task;
                if (Scenario.Live) await RunLiveProbeAsync();
                if (Scenario.Termination == DryTermination.Cancel) job.Cancel();
                if (Scenario.Termination == DryTermination.Stop) stop = scheduler.StopAsync();
                barrier.Release();
            }
            GateReprocessingProgressSnapshot result = await job.Completion;
            if (stop is not null) await stop;
            if (Scenario.Duplicate && replay && result.ReusedBatchCount != result.SelectedTraceCount)
                throw new InvalidOperationException("Dry Duplicate did not reuse every batch.");
            return result;
        }
    }

    private async Task RunLiveProbeAsync()
    {
        var run = new AcquisitionRunId(Guid.NewGuid());
        var stream = new UtStreamIdentity(_inspection, run, new UtSourceId("dry-live"), new PhysicalChannelId(99));
        IInspectionRunWriter writer = await _rf.OpenRunAsync(run);
        var sink = new DrySink(); var scratch = new GateScratchPool(1, 300_000);
        await using var live = new CommittedGateProcessingOrchestrator(_rf, _rf, sink, new GateEvaluator(scratch));
        if (live.OpenRun(new(_inspection, run, [stream], _plan)) != GateProcessingAdmissionStatus.Accepted)
            throw new InvalidOperationException("Dry live admission failed.");
        var identity = new TraceIdentity(stream, 0);
        var descriptor = new TraceDescriptor(identity, 1, TimeSpan.Zero, null, null, 4,
            100_000_000, 0, UtSignalMode.Rf, traceQuality: UtTraceQuality.Good);
        InspectionAppendResult committed = await writer.CommitAsync(new(new(descriptor, new short[] { 0, 7, 0, 0 })));
        live.NotifyCommitted(new(identity, committed.Revision));
        await sink.Delivered.Task;
        InspectionRevision revision = (await writer.SealRunAsync()).FinalRevision;
        live.StopAdmission(run); live.RequestFinalReconciliation(run, revision);
        await live.WaitForDrainAsync(run);
        if (live.Diagnostics.ActiveLeases != 0 || live.Diagnostics.ActiveCallbacks != 0 ||
            scratch.Snapshot.ActiveRentals != 0) throw new InvalidOperationException("Dry live resources unbalanced.");
    }

    private static void Validate(DryScenario scenario, GateReprocessingProgressSnapshot terminal,
        GateDerivedStoreDiagnostics diagnostics, long expectedIdentities,
        GateReprocessingLifecycle expectedLifecycle, bool expectedView, bool activeView)
    {
        bool completedCounts = expectedLifecycle != GateReprocessingLifecycle.Completed ||
            (terminal.SelectedTraceCount == expectedIdentities && terminal.IdentitiesVisited == expectedIdentities &&
             terminal.ReadTraceCount == expectedIdentities && terminal.EvaluatedTraceCount == expectedIdentities);
        bool batches = scenario.Duplicate
            ? terminal.NewlyCommittedBatchCount == 0 && terminal.ReusedBatchCount == expectedIdentities
            : expectedLifecycle != GateReprocessingLifecycle.Completed ||
              terminal.NewlyCommittedBatchCount == expectedIdentities;
        bool terminalPublication = expectedLifecycle == GateReprocessingLifecycle.Completed
            ? diagnostics.TerminalEvidenceCount == 1
            : diagnostics.TerminalEvidenceCount == 0 && !activeView;
        bool revision = diagnostics.Revision.Value ==
            checked((ulong)diagnostics.BatchCount + (ulong)diagnostics.Activations);
        bool operationBalances = terminal.CommitProgress is { Active: 0 } commit &&
            commit.Admitted == commit.Completed && terminal.CoverageProgress is { Active: 0 } coverage &&
            coverage.Admitted == coverage.Completed && terminal.ActivationProgress is { Active: 0 } activation &&
            activation.Admitted == activation.Completed;
        if (terminal.Lifecycle != expectedLifecycle || terminal.PrimaryError is not null ||
            !completedCounts || !batches || !terminalPublication || !revision || !operationBalances ||
            !terminal.CleanupConfirmed || terminal.ActiveWorkers != 0 || terminal.ActiveCallbacks != 0 ||
            terminal.ActiveLeases != 0 || terminal.LeasesAcquired != terminal.LeasesReleased ||
            terminal.BufferRents != terminal.BufferReturns || terminal.ScratchRents != terminal.ScratchReturns ||
            diagnostics.AdmittedWriters != 0 || diagnostics.ReservedBatchCount != 0 ||
            diagnostics.ReservedCoverageCount != 0 || diagnostics.ReservedActivationCount != 0 ||
            expectedView != activeView)
            throw new InvalidOperationException("Dry operational terminal diagnostics are inconsistent.");
    }

    [GlobalCleanup]
    public async Task Cleanup() => await _rf.DisposeAsync();

    public sealed record DryScenario(string Name, int SampleCount, int BlockSize, int StreamCount,
        DryDependency Dependency, DryTermination Termination, bool Live, bool Duplicate,
        GateReprocessingPublicationPolicy Policy)
    { public override string ToString() => Name; }
    public readonly record struct DryOutcome(string Scenario, int SampleCount, int BlockSize, int StreamCount,
        GateReprocessingLifecycle Lifecycle, long ExpectedIdentities, long ProcessedIdentities,
        long NewBatches, long DuplicateBatches, ulong FinalRevision, bool ActiveViewExpected,
        bool ActiveViewPresent, bool CleanupConfirmed, long DiagnosticChecksum);
    public enum DryDependency { Normal, ProviderBlocked, StoreBlocked }
    public enum DryTermination { Complete, Cancel, Stop }

    private sealed class PublicBarrier
    {
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _once;
        internal TaskCompletionSource Entered => _entered;
        internal async ValueTask WaitAsync(CancellationToken token)
        { if (Interlocked.Exchange(ref _once, 1) == 0) { _entered.TrySetResult(); await _release.Task.WaitAsync(token); } }
        internal void Release() => _release.TrySetResult();
    }

    private sealed class BlockingProvider(IInspectionSealedRunSnapshotProvider inner, PublicBarrier barrier) :
        IInspectionSealedRunSnapshotProvider
    {
        public async ValueTask<InspectionSealedRunSnapshotOpenResult> OpenSealedRunSnapshotAsync(
            InspectionSealedRunSnapshotRequest request, CancellationToken cancellationToken = default)
        {
            InspectionSealedRunSnapshotOpenResult opened = await inner.OpenSealedRunSnapshotAsync(request, cancellationToken);
            return opened.Status == InspectionSealedRunSnapshotOpenStatus.Available
                ? InspectionSealedRunSnapshotOpenResult.Available(new Snapshot(opened.Snapshot!, barrier)) : opened;
        }
        private sealed class Snapshot(IInspectionSealedRunSnapshot inner, PublicBarrier barrier) : IInspectionSealedRunSnapshot
        {
            public ushort ContractVersion => inner.ContractVersion; public InspectionId InspectionId => inner.InspectionId;
            public AcquisitionRunId RunId => inner.RunId; public InspectionRevision SourceFinalRevision => inner.SourceFinalRevision;
            public InspectionRunState RunState => inner.RunState; public int MaximumPageSize => inner.MaximumPageSize;
            public IReadOnlyList<InspectionSealedStreamSnapshot> Streams => inner.Streams;
            public InspectionSealedRunEnumerationDiagnostics EnumerationDiagnostics => inner.EnumerationDiagnostics;
            public long CountIdentities(InspectionSealedRunRangeQuery query) => inner.CountIdentities(query);
            public InspectionSealedRunIdentityPage ReadIdentities(UtStreamIdentity stream, InspectionSealedRunCursor? cursor = null) => inner.ReadIdentities(stream, cursor);
            public InspectionSealedRunIdentityPage ReadIdentities(InspectionSealedRunRangeQuery query, InspectionSealedRunCursor? cursor = null) => inner.ReadIdentities(query, cursor);
            public async ValueTask<InspectionTraceReadResult> ReadAsync(TraceIdentity identity, CancellationToken token = default)
            { await barrier.WaitAsync(token); return await inner.ReadAsync(identity, token); }
            public void Dispose() => inner.Dispose();
        }
    }

    private sealed class BlockingStore(InMemoryGateDerivedStore inner, PublicBarrier barrier) : IGateDerivedStore, IGateProcessingTerminalCoverageSink
    {
        public DerivedStoreRevision Revision => inner.Revision; public ActiveDerivedViewDescriptor? ActiveView => inner.ActiveView;
        public GateDerivedStoreDiagnostics Diagnostics => inner.Diagnostics;
        public ValueTask<GateBatchDeliveryResult> DeliverAsync(GateEvaluationBatch batch, CancellationToken token = default) => inner.DeliverAsync(batch, token);
        public async ValueTask<GateDerivedMutationResult> CommitAsync(GateEvaluationBatch batch, CancellationToken token = default)
        { await barrier.WaitAsync(token); return await inner.CommitAsync(batch, token); }
        public GateDerivedMutationResult Activate(GateDerivedCoverageManifest manifest, DerivedViewId viewId) => inner.Activate(manifest, viewId);
        public ValueTask<GateDerivedMutationResult> ActivateAsync(GateDerivedCoverageManifest manifest, DerivedViewId viewId, CancellationToken token = default) => inner.ActivateAsync(manifest, viewId, token);
        public GateDerivedMutationResult Invalidate(DerivedViewId viewId) => inner.Invalidate(viewId);
        public ValueTask<GateDerivedMutationResult> ReclaimInvalidatedAsync(CancellationToken token = default) => inner.ReclaimInvalidatedAsync(token);
        public IGateDerivedReadLease OpenActiveView() => inner.OpenActiveView();
        public GateProcessingTerminalCoverageStatus ConfirmTerminalCoverage(IReadOnlyList<GateProcessingTerminalCoverage> coverage) => inner.ConfirmTerminalCoverage(coverage);
        public ValueTask<GateDerivedMutationResult> ConfirmTerminalCoverageAsync(IReadOnlyList<GateProcessingTerminalCoverage> coverage, CancellationToken token = default) => inner.ConfirmTerminalCoverageAsync(coverage, token);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class DrySink : IGateEvaluationBatchSink
    {
        internal TaskCompletionSource Delivered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ValueTask<GateBatchDeliveryResult> DeliverAsync(GateEvaluationBatch batch, CancellationToken token = default)
        { Delivered.TrySetResult(); return ValueTask.FromResult(GateBatchDeliveryResult.Accepted()); }
    }
}
