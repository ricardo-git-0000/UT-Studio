using UTStudio.Application;
using UTStudio.Contracts.Inspection;
using UTStudio.Contracts.Processing;
using UTStudio.Core.Tests.Processing;
using UTStudio.Domain.Processing;
using UTStudio.Processing.Core;

namespace UTStudio.Core.Tests.Application;

[TestClass]
public sealed class ExactGateGeometryEvaluatorTests
{
    [TestMethod]
    public async Task ExactCrossingEventKeepsLeftSampleIndexAndInterpolatesDisplayedRfOrdinate()
    {
        const double normalized = .5;
        CompiledGatePlan plan = GateEvaluationTestData.CompilePreview(GateEvaluationTestData.Gate("A", 3,
            detector: GateDetectorKind.FirstRisingCrossing, domain: GateSignalDomain.RawSigned,
            threshold: normalized));
        Fixture fixture = Fixture.Create([-100, 100, 0], plan, previewGeneration: 3);

        GateGeometryEvaluationResult result = await fixture.Evaluator.EvaluateAsync(fixture.Request, plan);

        GateExactEventSnapshot detected = result.Snapshot!.Gates.Single().DetectedEvent!;
        double threshold = GateThresholdScale.ToSignalCounts(GateSignalDomain.RawSigned, normalized);
        double fraction = (threshold + 100d) / 200d;
        Assert.AreEqual(GateResultStatus.Detected, detected.Status);
        Assert.AreEqual(GateDetectionEventKind.RisingCrossing, detected.EventKind);
        Assert.AreEqual(0, detected.SampleIndex);
        Assert.AreEqual(fraction, detected.FractionalSamplePosition!.Value, 1e-15);
        Assert.AreEqual(threshold, detected.DetectorAmplitudeCounts!.Value, 1e-15);
        Assert.AreEqual(threshold, detected.DisplayedRfAmplitudeCounts!.Value, 1e-12);
    }

    [TestMethod]
    public async Task ExactEventAndCausalAnchorDoNotDependOnRequestedMeasureProjection()
    {
        GateMeasureKind[] statusOnly = [GateMeasureKind.Status, GateMeasureKind.Quality];
        GateDefinition a = GateEvaluationTestData.Gate("A", 3, measures: statusOnly);
        GateDefinition b = GateEvaluationTestData.Gate("B", 2, dependency: "A",
            offsetSeconds: 1e-6, measures: statusOnly);
        CompiledGatePlan plan = GateEvaluationTestData.CompilePreview(a, b);
        Fixture fixture = Fixture.Create([0, 100, 0, 200, 0], plan, previewGeneration: 31);

        GateGeometryEvaluationResult result = await fixture.Evaluator.EvaluateAsync(fixture.Request, plan);

        GateGeometryItemSnapshot[] gates = result.Snapshot!.Gates.ToArray();
        Assert.AreEqual(GateResultStatus.Detected, gates[0].DetectedEvent!.Status);
        Assert.AreEqual(1e-6, gates[0].DetectedEvent!.DetectionTimeSeconds!.Value, 1e-15);
        Assert.AreEqual(1e-6, gates[1].Anchor!.DetectionTimeSeconds!.Value, 1e-15);
        Assert.AreEqual(2e-6, gates[1].StartSeconds!.Value, 1e-15);
    }

    [TestMethod]
    public async Task PreviewChainUsesOneExactEvaluationAndPreservesCausalTerminals()
    {
        short[] samples = [0, 100, 0, 200, 0, 300, 0, 0];
        GateDefinition a = GateEvaluationTestData.Gate("A", 2, detector: GateDetectorKind.GlobalMaximum);
        GateDefinition b = GateEvaluationTestData.Gate("B", 2, dependency: "A",
            detector: GateDetectorKind.GlobalMaximum, offsetSeconds: 2e-6);
        GateDefinition c = GateEvaluationTestData.Gate("C", 2, dependency: "B",
            detector: GateDetectorKind.GlobalMaximum, offsetSeconds: 2e-6);
        CompiledGatePlan plan = GateEvaluationTestData.CompilePreview(a, b, c);
        Fixture fixture = Fixture.Create(samples, plan, previewGeneration: 4);

        GateGeometryEvaluationResult result = await fixture.Evaluator.EvaluateAsync(fixture.Request, plan);

        Assert.AreEqual(GateGeometryEvaluationStatus.Completed, result.Status);
        Assert.AreEqual(1, fixture.Provider.LeasesDisposed);
        Assert.AreEqual(0, fixture.Provider.ActiveLeases);
        GateGeometryItemSnapshot[] gates = result.Snapshot!.Gates.ToArray();
        Assert.AreEqual(GateGeometrySourceKind.Preview, result.Snapshot.Source.Kind);
        Assert.AreEqual(plan.Source.Revision, result.Snapshot.Source.Revision);
        Assert.IsNull(gates[0].Anchor);
        Assert.AreEqual(new GateId("A"), gates[1].Anchor!.GateId);
        Assert.AreEqual(new GateId("B"), gates[2].Anchor!.GateId);
        Assert.AreEqual(1e-6, gates[1].Anchor!.DetectionTimeSeconds);
        Assert.AreEqual(3e-6, gates[2].Anchor!.DetectionTimeSeconds);
        Assert.AreEqual(5e-6, gates[2].StartSeconds!.Value, 1e-15);

        GateDefinition noEvent = GateEvaluationTestData.Gate("A", 2,
            detector: GateDetectorKind.FirstRisingCrossing, threshold: 1);
        GateDefinition dependent = GateEvaluationTestData.Gate("B", 2, dependency: "A");
        CompiledGatePlan terminalPlan = GateEvaluationTestData.CompilePreview(noEvent, dependent);
        Fixture terminal = Fixture.Create([0, 0, 0], terminalPlan, previewGeneration: 5);
        GateGeometryEvaluationResult absent = await terminal.Evaluator.EvaluateAsync(terminal.Request, terminalPlan);
        Assert.AreEqual(GateResultStatus.DependencyNotDetected, absent.Snapshot!.Gates[1].EvaluationStatus);
        Assert.AreEqual(GateResultStatus.NoEvent, absent.Snapshot.Gates[1].Anchor!.Status);
        Assert.IsNull(absent.Snapshot.Gates[1].StartSeconds);
    }

    [TestMethod]
    public async Task CapacityIsExactRejectsOneOverAndRecoversWithoutAcquiringLease()
    {
        CompiledGatePlan plan = GateEvaluationTestData.CompilePreview(GateEvaluationTestData.Gate("A", 4));
        var provider = new BlockingProvider(GateEvaluationTestData.Trace(4).Descriptor, [0, 1, 2, 3]);
        var guard = new Guard();
        var evaluator = new ExactGateGeometryEvaluator(provider, guard, new(2));
        GateGeometryEvaluationRequest request = Request(plan, provider.Descriptor.Identity, 1);

        Task<GateGeometryEvaluationResult> first = evaluator.EvaluateAsync(request, plan).AsTask();
        Task<GateGeometryEvaluationResult> second = evaluator.EvaluateAsync(request, plan).AsTask();
        await provider.TwoReads.Task.WaitAsync(TimeSpan.FromSeconds(5));
        GateGeometryEvaluationResult overflow = await evaluator.EvaluateAsync(request, plan);
        Assert.AreEqual(GateGeometryEvaluationStatus.CapacityExceeded, overflow.Status);
        Assert.AreEqual(2, provider.Reads);
        provider.Release.TrySetResult();
        Assert.AreEqual(GateGeometryEvaluationStatus.Completed, (await first).Status);
        Assert.AreEqual(GateGeometryEvaluationStatus.Completed, (await second).Status);
        Assert.AreEqual(GateGeometryEvaluationStatus.Completed,
            (await evaluator.EvaluateAsync(request, plan)).Status);
        Assert.AreEqual(3, provider.LeasesDisposed);
        Assert.AreEqual(0, provider.ActiveLeases);
    }

    [TestMethod]
    public async Task CancellationDuringReadAndEvaluationReleasesLeaseBufferScratchAndSlot()
    {
        CompiledGatePlan plan = GateEvaluationTestData.CompilePreview(GateEvaluationTestData.Gate("A", 32,
            detector: GateDetectorKind.FirstLocalMaximum, local: new(1, 0, 1)));
        var blocked = new BlockingProvider(GateEvaluationTestData.Trace(32).Descriptor, new short[32]);
        var evaluator = new ExactGateGeometryEvaluator(blocked, new Guard(), new(1));
        GateGeometryEvaluationRequest request = Request(plan, blocked.Descriptor.Identity, 2);
        using var readCancellation = new CancellationTokenSource();
        Task<GateGeometryEvaluationResult> waiting = evaluator.EvaluateAsync(request, plan, readCancellation.Token).AsTask();
        await blocked.FirstRead.Task.WaitAsync(TimeSpan.FromSeconds(5));
        readCancellation.Cancel(); blocked.Release.TrySetCanceled(readCancellation.Token);
        Assert.AreEqual(GateGeometryEvaluationStatus.Cancelled, (await waiting).Status);
        Assert.AreEqual(0, blocked.ActiveLeases);

        var provider = new BlockingProvider(GateEvaluationTestData.Trace(32).Descriptor, Alternating(32), block: false);
        using var entered = new ManualResetEventSlim();
        using var continueEvaluation = new ManualResetEventSlim();
        var core = new GateEvaluator(new GateScratchPool(1, 524_288), checkpoint =>
        {
            if (checkpoint != GateEvaluationCheckpoint.AfterScratchRent) return;
            entered.Set(); continueEvaluation.Wait();
        });
        var during = new ExactGateGeometryEvaluator(provider, new Guard(), new(1), core);
        using var evaluationCancellation = new CancellationTokenSource();
        Task<GateGeometryEvaluationResult> running = Task.Run(async () =>
            await during.EvaluateAsync(request, plan, evaluationCancellation.Token));
        Assert.IsTrue(entered.Wait(TimeSpan.FromSeconds(5)));
        Assert.AreEqual(1, provider.LeasesDisposed, "The RF lease must be gone before evaluation starts.");
        evaluationCancellation.Cancel(); continueEvaluation.Set();
        Assert.AreEqual(GateGeometryEvaluationStatus.Cancelled, (await running).Status);
        Assert.AreEqual(0, core.ScratchDiagnostics.ActiveRentals);
        Assert.AreEqual(GateGeometryEvaluationStatus.Completed,
            (await during.EvaluateAsync(request, plan)).Status);
    }

    [TestMethod]
    public async Task RevalidationAndSuccessivePreviewIdentitiesMakeOutOfOrderResultsDiscardable()
    {
        CompiledGatePlan plan = GateEvaluationTestData.CompilePreview(GateEvaluationTestData.Gate("A", 4));
        ConfirmedGateTraceDescriptor trace = GateEvaluationTestData.Trace(4);
        var firstProvider = new BlockingProvider(trace.Descriptor, [0, 1, 2, 3]);
        var secondProvider = new BlockingProvider(trace.Descriptor, [3, 2, 1, 0]);
        GateGeometryEvaluationRequest firstRequest = Request(plan, trace.ConfirmedIdentity, 10);
        GateGeometryEvaluationRequest secondRequest = Request(plan, trace.ConfirmedIdentity, 11);
        var firstEvaluator = new ExactGateGeometryEvaluator(firstProvider, new Guard(), new(1));
        var secondEvaluator = new ExactGateGeometryEvaluator(secondProvider, new Guard(), new(1));
        Task<GateGeometryEvaluationResult> olderPending = firstEvaluator.EvaluateAsync(firstRequest, plan).AsTask();
        Task<GateGeometryEvaluationResult> newerPending = secondEvaluator.EvaluateAsync(secondRequest, plan).AsTask();
        await Task.WhenAll(firstProvider.FirstRead.Task, secondProvider.FirstRead.Task).WaitAsync(TimeSpan.FromSeconds(5));
        secondProvider.Release.TrySetResult();
        GateGeometryEvaluationResult newer = await newerPending;
        Assert.IsFalse(olderPending.IsCompleted);
        firstProvider.Release.TrySetResult();
        GateGeometryEvaluationResult older = await olderPending;
        Assert.AreEqual(11UL, newer.Request.Source.Preview.Generation);
        Assert.AreEqual(10UL, older.Request.Source.Preview.Generation);
        Assert.AreNotEqual(newer.Request.Source.Preview.Value, older.Request.Source.Preview.Value);

        var guard = new Guard { StatusAfterFirst = GateGeometrySourceValidationStatus.Superseded };
        var provider = new BlockingProvider(GateEvaluationTestData.Trace(4).Descriptor, [0, 1, 2, 3], block: false);
        var stale = new ExactGateGeometryEvaluator(provider, guard, new(1));
        GateGeometryEvaluationResult result = await stale.EvaluateAsync(Request(plan, provider.Descriptor.Identity, 12, guard), plan);
        Assert.AreEqual(GateGeometryEvaluationStatus.Stale, result.Status);
        Assert.AreEqual(1, provider.LeasesDisposed);
        Assert.AreEqual(0, provider.ActiveLeases);
    }

    [TestMethod]
    public async Task ReadTerminalsRemainTypedAndPlanIdentityCannotBeMixed()
    {
        CompiledGatePlan plan = GateEvaluationTestData.CompilePreview(GateEvaluationTestData.Gate("A", 4));
        ConfirmedGateTraceDescriptor trace = GateEvaluationTestData.Trace(4);
        GateGeometryEvaluationRequest request = Request(plan, trace.ConfirmedIdentity, 20);
        foreach ((InspectionReadStatus read, GateGeometryEvaluationStatus expected) in new[]
        {
            (InspectionReadStatus.NotAvailable, GateGeometryEvaluationStatus.NotAvailable),
            (InspectionReadStatus.Gap, GateGeometryEvaluationStatus.NotAvailable),
            (InspectionReadStatus.OutOfRange, GateGeometryEvaluationStatus.NotAvailable),
            (InspectionReadStatus.Cancelled, GateGeometryEvaluationStatus.Cancelled),
            (InspectionReadStatus.Closed, GateGeometryEvaluationStatus.Closed),
            (InspectionReadStatus.Faulted, GateGeometryEvaluationStatus.Faulted)
        })
        {
            var evaluator = new ExactGateGeometryEvaluator(new TerminalProvider(read), new Guard(), new(1));
            Assert.AreEqual(expected, (await evaluator.EvaluateAsync(request, plan)).Status);
        }

        CompiledGatePlan other = GateEvaluationTestData.CompilePreview(
            GateEvaluationTestData.Gate("A", 4, threshold: .75));
        var available = new BlockingProvider(trace.Descriptor, [0, 1, 2, 3], block: false);
        var exact = new ExactGateGeometryEvaluator(available, new Guard(), new(1));
        await Assert.ThrowsExactlyAsync<ArgumentException>(async () =>
            await exact.EvaluateAsync(request, other));
        Assert.AreEqual(0, available.Reads);
    }

    [TestMethod]
    public async Task CommittedIdentityAndLeaseCleanupFailureRemainExplicit()
    {
        CompiledGatePlan plan = GateEvaluationTestData.CompilePreview(GateEvaluationTestData.Gate("A", 4));
        ConfirmedGateTraceDescriptor trace = GateEvaluationTestData.Trace(4);
        var committed = new CommittedGatePlanSnapshot(trace.ConfirmedIdentity.Stream.InspectionId, plan);
        var authority = new GatePlanAuthoritySnapshot(committed, new GateAuthorityGeneration(7),
            new GateAuthorityPublicationSequence(9));
        GateGeometrySourceIdentity source = GateGeometrySourceIdentity.Committed(authority);
        var request = new GateGeometryEvaluationRequest(source, trace.ConfirmedIdentity,
            trace.SourceInspectionRevision);
        var provider = new BlockingProvider(trace.Descriptor, [0, 1, 2, 3], block: false);
        GateGeometryEvaluationResult completed = await new ExactGateGeometryEvaluator(provider,
            new Guard(), new(1)).EvaluateAsync(request, plan);
        Assert.AreEqual(GateGeometrySourceKind.Committed, completed.Snapshot!.Source.Kind);
        Assert.AreEqual(7UL, completed.Snapshot.Source.AuthorityGeneration.Value);
        Assert.AreEqual(9UL, completed.Snapshot.Source.PublicationSequence.Value);

        var cleanupFailure = new BlockingProvider(trace.Descriptor, [0, 1, 2, 3],
            block: false, throwOnDispose: true);
        GateGeometryEvaluationResult faulted = await new ExactGateGeometryEvaluator(cleanupFailure,
            new Guard(), new(1)).EvaluateAsync(request, plan);
        Assert.AreEqual(GateGeometryEvaluationStatus.Faulted, faulted.Status);
        StringAssert.Contains(faulted.ErrorCode!, "trace-copy");
        Assert.AreEqual(1, cleanupFailure.LeasesDisposed);
        Assert.AreEqual(0, cleanupFailure.ActiveLeases);

        var copyFailure = new BlockingProvider(trace.Descriptor, [0, 1, 2, 3],
            block: false, throwOnCopy: true);
        GateGeometryEvaluationResult copyFaulted = await new ExactGateGeometryEvaluator(copyFailure,
            new Guard(), new(1)).EvaluateAsync(request, plan);
        Assert.AreEqual(GateGeometryEvaluationStatus.Faulted, copyFaulted.Status);
        Assert.AreEqual(1, copyFailure.LeasesDisposed);
        Assert.AreEqual(0, copyFailure.ActiveLeases);
    }

    [TestMethod]
    public async Task ScratchCapacityFailureIsTypedAndRecovers()
    {
        CompiledGatePlan plan = GateEvaluationTestData.CompilePreview(GateEvaluationTestData.Gate("A", 32,
            detector: GateDetectorKind.FirstLocalMaximum, local: new(1, 0, 1)));
        ConfirmedGateTraceDescriptor trace = GateEvaluationTestData.Trace(32);
        var provider = new BlockingProvider(trace.Descriptor, Alternating(32), block: false);
        var scratch = new RejectOnceScratchPool();
        var core = new GateEvaluator(scratch);
        var evaluator = new ExactGateGeometryEvaluator(provider, new Guard(), new(1), core);
        GateGeometryEvaluationRequest request = Request(plan, trace.ConfirmedIdentity, 30);
        Assert.AreEqual(GateGeometryEvaluationStatus.CapacityExceeded,
            (await evaluator.EvaluateAsync(request, plan)).Status);
        Assert.AreEqual(GateGeometryEvaluationStatus.Completed,
            (await evaluator.EvaluateAsync(request, plan)).Status);
        Assert.AreEqual(2, provider.LeasesDisposed);
        Assert.AreEqual(0, scratch.Snapshot.ActiveRentals);
    }

    private static GateGeometryEvaluationRequest Request(CompiledGatePlan plan, TraceIdentity identity,
        ulong generation, Guard? previewGuard = null)
    {
        var authority = new GatePlanAuthorityKey(identity.Stream.InspectionId, plan.Source.Id);
        var source = GateGeometrySourceIdentity.PreviewSource(authority, new(1), plan.Source.Revision,
            plan.Signature, new(Guid.NewGuid(), generation));
        return new(source, identity, new InspectionRevision(5), previewGuard ?? new Guard());
    }

    private static short[] Alternating(int count) => Enumerable.Range(0, count)
        .Select(index => (short)(index % 2 == 0 ? 0 : 100)).ToArray();

    private sealed record Fixture(ExactGateGeometryEvaluator Evaluator,
        BlockingProvider Provider, GateGeometryEvaluationRequest Request)
    {
        internal static Fixture Create(short[] samples, CompiledGatePlan plan, ulong previewGeneration)
        {
            ConfirmedGateTraceDescriptor trace = GateEvaluationTestData.Trace(samples.Length);
            var provider = new BlockingProvider(trace.Descriptor, samples, block: false);
            return new(new(provider, new Guard(), new(1)), provider,
                ExactGateGeometryEvaluatorTests.Request(plan, trace.ConfirmedIdentity, previewGeneration));
        }
    }

    private sealed class Guard : IGateGeometrySourceGuard, IGatePreviewSourceGuard
    {
        private int _calls;
        internal GateGeometrySourceValidationStatus StatusAfterFirst { get; init; } = GateGeometrySourceValidationStatus.Current;
        public GateGeometrySourceValidationStatus Validate(GateGeometrySourceIdentity source) =>
            Interlocked.Increment(ref _calls) == 1 ? GateGeometrySourceValidationStatus.Current : StatusAfterFirst;
        public GateGeometrySourceValidationStatus ValidatePreview(GateGeometrySourceIdentity source) => Validate(source);
    }

    private sealed class BlockingProvider : IUtTraceProvider
    {
        private readonly short[] _samples;
        private readonly bool _block;
        private readonly bool _throwOnDispose;
        private readonly bool _throwOnCopy;
        private int _reads, _active, _disposed;
        internal BlockingProvider(TraceDescriptor descriptor, short[] samples, bool block = true,
            bool throwOnDispose = false, bool throwOnCopy = false)
        {
            Descriptor = descriptor; _samples = samples; _block = block;
            _throwOnDispose = throwOnDispose; _throwOnCopy = throwOnCopy;
        }
        internal TraceDescriptor Descriptor { get; }
        internal int Reads => Volatile.Read(ref _reads);
        internal int ActiveLeases => Volatile.Read(ref _active);
        internal int LeasesDisposed => Volatile.Read(ref _disposed);
        internal TaskCompletionSource FirstRead { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource TwoReads { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async ValueTask<InspectionTraceReadResult> ReadAsync(TraceIdentity identity,
            CancellationToken cancellationToken = default)
        {
            int reads = Interlocked.Increment(ref _reads); FirstRead.TrySetResult();
            if (reads >= 2) TwoReads.TrySetResult();
            if (_block) await Release.Task.WaitAsync(cancellationToken);
            Interlocked.Increment(ref _active);
            return InspectionTraceReadResult.Available(new InspectionRevision(5), new Lease(this));
        }

        private sealed class Lease(BlockingProvider owner) : IInspectionTraceLease
        {
            private int _disposed;
            public TraceDescriptor Descriptor => owner.Descriptor;
            public IReadOnlyUtSamples Samples { get; } = new Samples(owner._samples, owner._throwOnCopy);
            public void Dispose()
            {
                if (Interlocked.Exchange(ref _disposed, 1) != 0) throw new InvalidOperationException("double dispose");
                Interlocked.Decrement(ref owner._active); Interlocked.Increment(ref owner._disposed);
                if (owner._throwOnDispose) throw new InvalidOperationException("lease cleanup failed");
            }
        }
        private sealed class Samples(short[] values, bool throwOnCopy) : IReadOnlyUtSamples
        {
            public int Count => values.Length;
            public short this[int index] => values[index];
            public void CopyTo(Span<short> destination)
            {
                if (throwOnCopy) throw new InvalidOperationException("copy failed");
                values.CopyTo(destination);
            }
        }
    }

    private sealed class RejectOnceScratchPool : IGateScratchPool
    {
        private readonly GateScratchPool _inner = new(1, 524_288);
        private int _attempts;
        public bool TryRent(int sampleCount, out GateScratchLease? lease)
        {
            if (Interlocked.Increment(ref _attempts) == 1) { lease = null; return false; }
            return ((IGateScratchPool)_inner).TryRent(sampleCount, out lease);
        }
        public void Return(GateScratchLease lease) => ((IGateScratchPool)_inner).Return(lease);
        public GateScratchDiagnostics Snapshot => _inner.Snapshot;
    }

    private sealed class TerminalProvider(InspectionReadStatus status) : IUtTraceProvider
    {
        public ValueTask<InspectionTraceReadResult> ReadAsync(TraceIdentity identity,
            CancellationToken cancellationToken = default) => ValueTask.FromResult(
                InspectionTraceReadResult.Unavailable(status, new InspectionRevision(5),
                    status == InspectionReadStatus.Faulted ? "read-fault" : null));
    }
}
