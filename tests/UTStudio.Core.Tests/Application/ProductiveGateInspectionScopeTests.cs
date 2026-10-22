using UTStudio.Application;
using UTStudio.Contracts.Application;
using UTStudio.Contracts.Inspection;
using UTStudio.Contracts.Processing;
using UTStudio.Core.Tests.Processing;
using UTStudio.Domain.Acquisition;
using UTStudio.Domain.Inspection;
using UTStudio.Domain.Processing;
using UTStudio.Inspection.Core;
using UTStudio.Processing.Core;

namespace UTStudio.Core.Tests.Application;

[TestClass]
public sealed class ProductiveGateInspectionScopeTests
{
    [TestMethod]
    public async Task ScopeBuildsOneSharedGraphAndDisposeIsIdempotent()
    {
        var inspection = new InspectionId(Guid.NewGuid());
        await using var registry = new InspectionDataStoreRegistry(inspection,
            new InMemoryInspectionStoreOptions(1024 * 1024, 32));
        ProductiveGateInspectionScope scope = await ProductiveGateInspectionScope.CreateAsync(
            inspection, registry.Store, Configuration());

        Assert.AreSame(scope.InitialPlan, scope.Authority.Read(
            new(inspection, scope.InitialPlan.Source.Id)).Committed.CompiledPlan);
        Assert.IsNotNull(scope.PreviewCompiler);
        Assert.IsNotNull(scope.GeometryEvaluator);
        Assert.IsNotNull(scope.Ingress);
        Assert.IsNotNull(scope.DerivedStore);

        Task first = scope.DisposeAsync().AsTask();
        Task second = scope.DisposeAsync().AsTask();
        Assert.AreSame(first, second);
        await first.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [TestMethod]
    public async Task PartialConstructionFailureRollsBackEveryCreatedOwner()
    {
        var inspection = new InspectionId(Guid.NewGuid());
        await using var registry = new InspectionDataStoreRegistry(inspection,
            new InMemoryInspectionStoreOptions(1024 * 1024, 32));

        var rolledBack = new List<string>();
        InvalidOperationException error = await Assert.ThrowsExactlyAsync<InvalidOperationException>(async () =>
            await ProductiveGateInspectionScope.CreateCoreAsync(inspection, registry.Store, Configuration(),
                new(), CancellationToken.None, stage => stage == "historical"
                    ? new InvalidOperationException("synthetic-construction") : null,
                rolledBack.Add));

        Assert.AreEqual("synthetic-construction", error.Message);
        CollectionAssert.AreEqual(new[] { "historical" }, rolledBack);

        rolledBack.Clear();
        error = await Assert.ThrowsExactlyAsync<InvalidOperationException>(async () =>
            await ProductiveGateInspectionScope.CreateCoreAsync(inspection, registry.Store, Configuration(),
                new(), CancellationToken.None, stage => stage == "apply"
                    ? new InvalidOperationException("synthetic-apply") : null,
                rolledBack.Add));
        Assert.AreEqual("synthetic-apply", error.Message);
        CollectionAssert.AreEqual(new[] { "live", "store", "authority" }, rolledBack);
    }

    [TestMethod]
    [DoNotParallelize]
    public async Task ScopeDisposeDrainsAnAdmittedApplyAndOwnsServicesExactlyOnce()
    {
        var inspection = new InspectionId(Guid.NewGuid());
        var run = new AcquisitionRunId(Guid.NewGuid());
        await using var registry = new InspectionDataStoreRegistry(inspection,
            new InMemoryInspectionStoreOptions(1024 * 1024, 32));
        await using var source = new ManualSessionSource();
        BlockingEpochPort? blocker = null;
        TaskCompletionSource cleanupStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        ProductiveGateInspectionScope scope = await ProductiveGateInspectionScope.CreateCoreAsync(
            inspection, registry.Store, Configuration(), new(), CancellationToken.None,
            epochDecorator: inner => blocker = new(inner),
            sessionCleanupStarted: () => cleanupStarted.TrySetResult());
        ApplicationSession session = scope.CreateSession(source, registry.OpenRun);
        await session.StartAsync(Configuration(), run);
        ManualSessionSource.CountingOwner frame = source.QueueFrame(0);
        await frame.Returned.Task.WaitAsync(TimeSpan.FromSeconds(5));
        GatePlanAuthoritySnapshot authority = scope.Authority.Read(
            new(inspection, scope.InitialPlan.Source.Id));
        var draft = new GatePlanDraft(authority.Authority, authority.Committed.Revision,
            authority.Committed.Plan.SchemaVersion, authority.Committed.Plan.AlgorithmVersion,
            [GateTestPlan.Gate("default-global-maximum", threshold: 0.5)]);
        var intent = new GatePlanEditIntent(GatePlanEditIntentKind.Apply, authority.Authority,
            authority.Committed.Revision, draft);
        GatePlanPreviewCandidate candidate = scope.PreviewCompiler.Compile(draft,
            new(Guid.NewGuid(), 1)).Candidate!;

        Task<GatePlanApplySubmission> invocation = Task.Run(() =>
            session.TryApplyGatePlan(new(intent, candidate)));
        await blocker!.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Task first = Task.Run(async () => await scope.DisposeAsync());
        await cleanupStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.IsFalse(first.IsCompleted);
        blocker.Release.Set();
        GatePlanApplySubmission submission = await invocation.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.AreEqual(GatePlanApplySubmissionStatus.Admitted, submission.Status);
        GatePlanEditResult terminal = await submission.Completion!.WaitAsync(TimeSpan.FromSeconds(5));
        await first.WaitAsync(TimeSpan.FromSeconds(5));
        await scope.DisposeAsync();

        Assert.AreEqual(GatePlanEditOutcome.Closed, terminal.Outcome);
        Assert.AreEqual(SessionPhase.Disposed, session.Snapshot.Phase);
        blocker.Release.Dispose();
    }

    private static ConventionalAcquisitionConfiguration Configuration() =>
        new(new PhysicalChannelId(0), 8, 50_000_000, firstSampleOffsetSeconds: 0.000_001);

    private sealed class BlockingEpochPort(IPreparedGatePlanEpochPort inner) : IPreparedGatePlanEpochPort
    {
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal ManualResetEventSlim Release { get; } = new(false);
        public GatePlanEpochPrepareResult PrepareEpoch(InspectionId inspectionId, AcquisitionRunId runId,
            CompiledGatePlan plan, InspectionRevision effectiveFromInclusive,
            GatePreparedReservationOwner owner, GatePreparedOperationGeneration generation)
        {
            GatePlanEpochPrepareResult result = inner.PrepareEpoch(inspectionId, runId, plan,
                effectiveFromInclusive, owner, generation);
            Entered.TrySetResult();
            if (!Release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("Apply was not released.");
            return result;
        }
        public GatePreparedReservationResult CommitEpoch(GatePreparedReservation reservation,
            GatePreparedReservationOwner owner, GatePreparedOperationGeneration generation) =>
            inner.CommitEpoch(reservation, owner, generation);
        public GatePreparedReservationResult AbortEpoch(GatePreparedReservation reservation,
            GatePreparedReservationOwner owner, GatePreparedOperationGeneration generation) =>
            inner.AbortEpoch(reservation, owner, generation);
    }
}
