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
public sealed class GatePlanApplyUseCaseTests
{
    [TestMethod]
    public async Task AcceptedUsesOneCompiledPlanForEpochDesiredAndAuthorityAndPublishesLast()
    {
        await using var fixture = new Fixture();
        GatePlanEditIntent intent = fixture.Intent();
        var observed = new List<uint>();
        var revisionTwo = Signal();
        using IDisposable subscription = fixture.Reader.Observe(intent.Authority).Subscribe(
            new Observer(value =>
            {
                lock (observed) observed.Add(value.Committed.Revision.Value);
                if (value.Committed.Revision.Value == 2) revisionTwo.TrySetResult();
            }));

        GatePlanApplySubmission submission = fixture.UseCase.TryApply(intent, fixture.Context);
        GatePlanEditResult result = await submission.WaitAsync();

        Assert.AreEqual(GatePlanApplySubmissionStatus.Admitted, submission.Status);
        Assert.AreEqual(GatePlanEditOutcome.Accepted, result.Outcome);
        Assert.AreEqual(new GatePlanRevision(2), result.ResultingRevision);
        GatePlanAuthorityInstalledState installed = fixture.Authority.PreparedCommits.ReadInstalled(intent.Authority)!;
        Assert.AreEqual(installed.Authority.Committed.Signature,
            fixture.Orchestrator.Diagnostics.ActiveRuns.Single().Plans.Last().PlanSignature);
        InMemoryGateDerivedStore.InstalledDesiredReplacement desired = fixture.Store.DesiredReplacementControl
            .FindInstalled(new(fixture.Inspection, intent.Authority.PlanId))!;
        Assert.AreEqual(installed.Authority.Committed.Revision, desired.PlanRevision);
        await revisionTwo.Task.WaitAsync(TimeSpan.FromSeconds(5));
        lock (observed) CollectionAssert.AreEqual(new uint[] { 1, 2 }, observed);
    }

    [TestMethod]
    public async Task StopBeforePointOfNoReturnAbortsEveryPreparedOwnerAndReturnsClosed()
    {
        await using var fixture = new Fixture();
        fixture.Lifecycle.AllowPointOfNoReturn = false;

        GatePlanEditResult result = await fixture.UseCase.TryApply(fixture.Intent(), fixture.Context).WaitAsync();

        Assert.AreEqual(GatePlanEditOutcome.Closed, result.Outcome);
        Assert.AreEqual(new GatePlanRevision(1), fixture.Reader.Read(fixture.AuthorityKey).Committed.Revision);
        Assert.IsFalse(fixture.Orchestrator.Diagnostics.ActiveRuns.Single().HasPreparedEpoch);
        Assert.IsFalse(fixture.Store.DesiredReplacementControl.Diagnostics.HasPreparedReservation);
        Assert.AreEqual(0, fixture.Authority.PreparedCommits.Diagnostics.PreparedReservations);
    }

    [TestMethod]
    public async Task CallerCancellationAfterAdmissionCancelsOnlyWait()
    {
        await using var fixture = new Fixture();
        var entered = Signal(); var release = Signal();
        ApplicationCommitAdmission blocker = fixture.Coordinator.AdmitCommit(fixture.Run,
            ApplicationCommitOperationClass.RfCommit, async () =>
            {
                entered.TrySetResult(); await release.Task;
                return new InspectionAppendResult(InspectionAppendStatus.Committed, new(1), wasAccepted: true);
            });
        await entered.Task;
        GatePlanApplySubmission submission = fixture.UseCase.TryApply(fixture.Intent(), fixture.Context);
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();

        await Assert.ThrowsExactlyAsync<TaskCanceledException>(() => submission.WaitAsync(cancellation.Token));
        release.TrySetResult(); await blocker.Completion;
        Assert.AreEqual(GatePlanEditOutcome.Accepted, (await submission.Completion!).Outcome);
    }

    [TestMethod]
    public async Task CoordinatorSaturationIsInternalAndConsumesNoOrdinal()
    {
        await using var fixture = new Fixture(coordinatorCapacity: 1);
        var entered = Signal(); var release = Signal();
        ApplicationCommitAdmission blocker = fixture.Coordinator.AdmitCommit(fixture.Run,
            ApplicationCommitOperationClass.RfCommit, async () =>
            {
                entered.TrySetResult(); await release.Task;
                return new InspectionAppendResult(InspectionAppendStatus.Committed, new(1), wasAccepted: true);
            });
        await entered.Task;
        ulong ordinal = fixture.Coordinator.Snapshot.LastAdmissionOrdinal;

        GatePlanApplySubmission rejected = fixture.UseCase.TryApply(fixture.Intent(), fixture.Context);

        Assert.AreEqual(GatePlanApplySubmissionStatus.CoordinatorCapacityExceeded, rejected.Status);
        Assert.IsNull(rejected.Completion);
        Assert.AreEqual(ordinal, fixture.Coordinator.Snapshot.LastAdmissionOrdinal);
        Assert.AreEqual(new GatePlanRevision(1), fixture.Reader.Read(fixture.AuthorityKey).Committed.Revision);
        release.TrySetResult(); await blocker.Completion;
    }

    [TestMethod]
    public async Task CancellationBeforeAdmissionIsInternalAndConsumesNoOrdinal()
    {
        await using var fixture = new Fixture();
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();

        GatePlanApplySubmission rejected = fixture.UseCase.TryApply(
            fixture.Intent(), fixture.Context, cancellation.Token);

        Assert.AreEqual(GatePlanApplySubmissionStatus.CancelledBeforeAdmission, rejected.Status);
        Assert.IsNull(rejected.Completion);
        Assert.AreEqual((ulong)0, fixture.Coordinator.Snapshot.LastAdmissionOrdinal);
    }

    [TestMethod]
    public async Task RfApplyRfUsesOneGlobalOrdinalSequenceWithoutSkipping()
    {
        await using var fixture = new Fixture();
        ApplicationCommitAdmission rf1 = fixture.Coordinator.AdmitCommit(fixture.Run,
            ApplicationCommitOperationClass.RfCommit,
            () => ValueTask.FromResult(new InspectionAppendResult(InspectionAppendStatus.Committed, new(1),
                wasAccepted: true)));
        await rf1.Completion;
        GatePlanEditIntent sameIntent = fixture.Intent();
        Assert.AreEqual(GatePlanEditOutcome.Accepted,
            (await fixture.UseCase.TryApply(sameIntent, fixture.Context).WaitAsync()).Outcome);
        ApplicationCommitAdmission rf3 = fixture.Coordinator.AdmitCommit(fixture.Run,
            ApplicationCommitOperationClass.RfCommit,
            () => ValueTask.FromResult(new InspectionAppendResult(InspectionAppendStatus.Committed, new(2),
                wasAccepted: true)));

        Assert.AreEqual(ApplicationCommitTerminalStatus.Confirmed, (await rf3.Completion).Status);
        Assert.AreEqual((ulong)3, fixture.Coordinator.Snapshot.LastTerminalOrdinal);
    }

    [TestMethod]
    public async Task GapApplyGapUsesOneGlobalOrdinalSequenceWithoutSkipping()
    {
        await using var fixture = new Fixture();
        ApplicationCommitAdmission gap1 = fixture.Coordinator.AdmitCommit(fixture.Run,
            ApplicationCommitOperationClass.GapCommit,
            () => ValueTask.FromResult(new InspectionAppendResult(InspectionAppendStatus.Committed, new(1),
                wasAccepted: true)));
        await gap1.Completion;
        Assert.AreEqual(GatePlanEditOutcome.Accepted,
            (await fixture.UseCase.TryApply(fixture.Intent(), fixture.Context).WaitAsync()).Outcome);
        ApplicationCommitAdmission gap3 = fixture.Coordinator.AdmitCommit(fixture.Run,
            ApplicationCommitOperationClass.GapCommit,
            () => ValueTask.FromResult(new InspectionAppendResult(InspectionAppendStatus.Committed, new(2),
                wasAccepted: true)));

        Assert.AreEqual(ApplicationCommitTerminalStatus.Confirmed, (await gap3.Completion).Status);
        Assert.AreEqual((ulong)3, fixture.Coordinator.Snapshot.LastTerminalOrdinal);
    }

    [TestMethod]
    public async Task LiveEpochCapacityIsSemanticAndStillReconcilesItsAdmittedOrdinal()
    {
        await using var fixture = new Fixture(processingOptions: new(maximumPlanEpochsPerRun: 1));

        GatePlanApplySubmission submission = fixture.UseCase.TryApply(fixture.Intent(), fixture.Context);
        GatePlanEditResult result = await submission.WaitAsync();

        Assert.AreEqual(GatePlanApplySubmissionStatus.Admitted, submission.Status);
        Assert.AreEqual(GatePlanEditOutcome.CapacityExceeded, result.Outcome);
        Assert.AreEqual(GatePlanCapacityResource.LiveEpochs, result.CapacityResource);
        Assert.AreEqual((ulong)1, fixture.Coordinator.Snapshot.LastAdmissionOrdinal);
        Assert.AreEqual((ulong)1, fixture.Coordinator.Snapshot.LastTerminalOrdinal);
        Assert.AreEqual(new GatePlanRevision(1), fixture.Reader.Read(fixture.AuthorityKey).Committed.Revision);
    }

    [TestMethod]
    public async Task CommitInvariantAfterPointOfNoReturnFaultsCoordinatorAndReleasesPreparedOwners()
    {
        await using var fixture = new Fixture(faultEpochCommit: true);
        GatePlanApplySubmission apply = fixture.UseCase.TryApply(fixture.Intent(), fixture.Context);

        ApplicationCommitInvariantException error = await Assert.ThrowsExactlyAsync<ApplicationCommitInvariantException>(
            () => apply.WaitAsync());

        StringAssert.Contains(error.Message, "epoch");
        Assert.AreEqual(ApplicationCommitCoordinatorLifecycle.Faulted, fixture.Coordinator.Snapshot.Lifecycle);
        Assert.IsFalse(fixture.Orchestrator.Diagnostics.ActiveRuns.Single().HasPreparedEpoch);
        Assert.IsFalse(fixture.Store.DesiredReplacementControl.Diagnostics.HasPreparedReservation);
        Assert.AreEqual(0, fixture.Authority.PreparedCommits.Diagnostics.PreparedReservations);
        Assert.AreEqual(new GatePlanRevision(1), fixture.Reader.Read(fixture.AuthorityKey).Committed.Revision);
    }

    [TestMethod]
    public async Task ExceptionAfterPointOfNoReturnPreservesPrimaryAndSkipsQueuedRf()
    {
        await using var fixture = new Fixture(throwEpochCommit: true);
        var hold = Signal(); var release = Signal();
        ApplicationCommitAdmission first = fixture.Coordinator.AdmitCommit(fixture.Run,
            ApplicationCommitOperationClass.RfCommit, async () =>
            {
                hold.TrySetResult(); await release.Task;
                return new InspectionAppendResult(InspectionAppendStatus.Committed, new(1), wasAccepted: true);
            });
        await hold.Task;
        GatePlanApplySubmission apply = fixture.UseCase.TryApply(fixture.Intent(), fixture.Context);
        bool queuedRfExecuted = false;
        ApplicationCommitAdmission queued = fixture.Coordinator.AdmitCommit(fixture.Run,
            ApplicationCommitOperationClass.RfCommit, () =>
            {
                queuedRfExecuted = true;
                return ValueTask.FromResult(new InspectionAppendResult(InspectionAppendStatus.Committed,
                    new(2), wasAccepted: true));
            });
        release.TrySetResult(); await first.Completion;

        ApplicationCommitInvariantException error = await Assert.ThrowsExactlyAsync<ApplicationCommitInvariantException>(
            () => apply.WaitAsync());
        ApplicationCommitTerminalResult skipped = await queued.Completion;

        Assert.IsInstanceOfType<InvalidOperationException>(error.InnerException);
        Assert.AreEqual("synthetic epoch commit failure", error.InnerException.Message);
        Assert.AreEqual(ApplicationCommitTerminalStatus.SkippedAfterInvariantFault, skipped.Status);
        Assert.AreSame(error, skipped.Error);
        Assert.IsFalse(queuedRfExecuted);
    }

    [TestMethod]
    public async Task InvariantExceptionAfterPointOfNoReturnKeepsIdentityAndAbortsAllReservations()
    {
        await using var fixture = new Fixture(throwInvariantEpochCommit: true);
        GatePlanApplySubmission apply = fixture.UseCase.TryApply(fixture.Intent(), fixture.Context);

        ApplicationCommitInvariantException error = await Assert.ThrowsExactlyAsync<ApplicationCommitInvariantException>(
            () => apply.WaitAsync());

        Assert.AreEqual("synthetic invariant epoch failure", error.Message);
        Assert.IsFalse(fixture.Orchestrator.Diagnostics.ActiveRuns.Single().HasPreparedEpoch);
        Assert.IsFalse(fixture.Store.DesiredReplacementControl.Diagnostics.HasPreparedReservation);
        Assert.AreEqual(0, fixture.Authority.PreparedCommits.Diagnostics.PreparedReservations);
        Assert.AreEqual(ApplicationCommitCoordinatorLifecycle.Faulted, fixture.Coordinator.Snapshot.Lifecycle);
    }

    [TestMethod]
    public void ApplicationBridgeUsesPreparedPortAndNotConcreteStore()
    {
        Type type = typeof(GatePlanApplyUseCase);
        Type[] fields = type.GetFields(System.Reflection.BindingFlags.Instance |
            System.Reflection.BindingFlags.NonPublic).Select(field => field.FieldType).ToArray();
        CollectionAssert.Contains(fields, typeof(IPreparedGateStoreControl));
        CollectionAssert.DoesNotContain(fields, typeof(InMemoryGateDerivedStore));
        Assert.IsFalse(type.IsPublic);
        Assert.IsFalse(typeof(GatePlanApplyServices).IsPublic);
    }

    [TestMethod]
    public async Task ApplicationSessionInstallsRunGenerationAppliesAndStopsStoreAfterCoordinatorDrain()
    {
        await using var source = new ManualSessionSource();
        var inspection = new InspectionId(Guid.NewGuid()); var run = new AcquisitionRunId(Guid.NewGuid());
        await using var registry = new InspectionDataStoreRegistry(inspection,
            new InMemoryInspectionStoreOptions(1024 * 1024, 32));
        await using var derived = new InMemoryGateDerivedStore();
        CompiledGatePlan initial = Fixture.CompilePlan(1);
        using var authority = new InMemoryGatePlanAuthorityOwner([
            new CommittedGatePlanSnapshot(inspection, initial)]);
        IGatePlanAuthorityReader reader = authority.Seal();
        await using var orchestrator = new CommittedGateProcessingOrchestrator(registry.Store,
            registry.Store, new Sink(), new GateEvaluator(new GateScratchPool()));
        var services = new GatePlanApplyServices(reader, authority.PreparedCommits,
            orchestrator, derived.DesiredReplacementControl);
        await using var session = new ApplicationSession(source, inspection, registry.OpenRun,
            null, null, orchestrator, initial, services);

        await session.StartAsync(new(new PhysicalChannelId(0), 8, 50_000_000,
            firstSampleOffsetSeconds: 0.000_001), run);
        CommittedGatePlanSnapshot committed = reader.Read(new(inspection, initial.Source.Id)).Committed;
        var draft = new GatePlanDraft(committed.AuthorityKey, committed.Revision,
            committed.Plan.SchemaVersion, committed.Plan.AlgorithmVersion,
            [GateTestPlan.Gate("A", threshold: 0.25)]);
        GatePlanApplySubmission apply = session.TryApplyGatePlan(new(GatePlanEditIntentKind.Apply,
            committed.AuthorityKey, committed.Revision, draft));

        Assert.AreEqual(GatePlanEditOutcome.Accepted, (await apply.WaitAsync()).Outcome);
        await session.StopAsync();
        Assert.AreEqual(GateStoreRunLifecycle.Stopped,
            derived.DesiredReplacementControl.RunDiagnostics.Current.Lifecycle);
        Assert.AreEqual(SessionPhase.Idle, session.Snapshot.Phase);
    }

    [TestMethod]
    public async Task ApplicationSessionStopWinsWhilePrepareIsBeforePointOfNoReturn()
    {
        await using var fixture = new SessionFixture(BlockEpochAt.Prepare);
        await fixture.StartAsync();

        Task<GatePlanApplySubmission> invocation = Task.Run(() => fixture.Session.TryApplyGatePlan(fixture.Intent()));
        await fixture.Epochs.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Task stop = fixture.Session.StopAsync();
        fixture.Epochs.Release.Set();
        GatePlanEditResult result = await (await invocation).WaitAsync();
        await stop;

        Assert.AreEqual(GatePlanEditOutcome.Closed, result.Outcome);
        Assert.AreEqual(new GatePlanRevision(1), fixture.Reader.Read(fixture.AuthorityKey).Committed.Revision);
        Assert.AreEqual(GateStoreRunLifecycle.Stopped,
            fixture.Store.DesiredReplacementControl.RunDiagnostics.Current.Lifecycle);
    }

    [TestMethod]
    public async Task ApplicationSessionApplyWinsPointOfNoReturnAndStopWaitsForCompleteCommit()
    {
        await using var fixture = new SessionFixture(BlockEpochAt.Commit);
        await fixture.StartAsync();

        Task<GatePlanApplySubmission> invocation = Task.Run(() => fixture.Session.TryApplyGatePlan(fixture.Intent()));
        await fixture.Epochs.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Task stop = fixture.Session.StopAsync();
        Assert.IsFalse(stop.IsCompleted);
        fixture.Epochs.Release.Set();
        GatePlanEditResult result = await (await invocation).WaitAsync();
        await stop;

        Assert.AreEqual(GatePlanEditOutcome.Accepted, result.Outcome);
        Assert.AreEqual(new GatePlanRevision(2), fixture.Reader.Read(fixture.AuthorityKey).Committed.Revision);
        Assert.AreEqual(GateStoreRunLifecycle.Stopped,
            fixture.Store.DesiredReplacementControl.RunDiagnostics.Current.Lifecycle);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        internal readonly InspectionId Inspection = new(Guid.NewGuid());
        internal readonly AcquisitionRunId Run = new(Guid.NewGuid());
        internal readonly InMemoryGateDerivedStore Store = new();
        internal readonly InspectionDataStoreRegistry Registry;
        internal readonly InMemoryGatePlanAuthorityOwner Authority;
        internal readonly IGatePlanAuthorityReader Reader;
        internal readonly CommittedGateProcessingOrchestrator Orchestrator;
        internal readonly ApplicationCommitCoordinator Coordinator;
        internal readonly TestLifecycle Lifecycle = new();
        internal readonly GatePlanApplyUseCase UseCase;
        internal readonly GatePlanAuthorityKey AuthorityKey;
        internal readonly GatePlanApplyRunContext Context;

        internal Fixture(int coordinatorCapacity = 8, GateProcessingOptions? processingOptions = null,
            bool faultEpochCommit = false, bool throwEpochCommit = false, bool throwInvariantEpochCommit = false)
        {
            CompiledGatePlan initial = Compile(1);
            var committed = new CommittedGatePlanSnapshot(Inspection, initial);
            Authority = new([committed]); Reader = Authority.Seal(); AuthorityKey = committed.AuthorityKey;
            Registry = new(Inspection, new InMemoryInspectionStoreOptions(1024 * 1024, 32));
            Orchestrator = new(Registry.Store, Registry.Store, new Sink(),
                new GateEvaluator(new GateScratchPool()), processingOptions);
            Assert.AreEqual(GateProcessingAdmissionStatus.Accepted, Orchestrator.OpenRun(new(
                Inspection, Run, [new(Inspection, Run, new("apply-test"), new(0))], initial)));
            var services = new GatePlanApplyServices(Reader, Authority.PreparedCommits,
                Orchestrator, Store.DesiredReplacementControl);
            Context = services.OpenRun(Inspection, Run); Lifecycle.Context = Context;
            Coordinator = new(Inspection, coordinatorCapacity);
            IPreparedGatePlanEpochPort epochs = faultEpochCommit || throwEpochCommit || throwInvariantEpochCommit ?
                new FaultingEpochPort(Orchestrator, throwEpochCommit, throwInvariantEpochCommit) : Orchestrator;
            UseCase = new(Coordinator, Reader, Authority.PreparedCommits, epochs,
                Store.DesiredReplacementControl, Lifecycle);
        }

        internal GatePlanEditIntent Intent()
        {
            CommittedGatePlanSnapshot current = Reader.Read(AuthorityKey).Committed;
            var draft = new GatePlanDraft(AuthorityKey, new(1), current.Plan.SchemaVersion,
                current.Plan.AlgorithmVersion, [GateTestPlan.Gate("A", threshold: 0.2)]);
            return new(GatePlanEditIntentKind.Apply, AuthorityKey, new(1), draft);
        }

        public async ValueTask DisposeAsync()
        {
            Coordinator.CloseAdmission(); await Coordinator.DisposeAsync();
            await Orchestrator.DisposeAsync(); await Store.DisposeAsync(); await Registry.DisposeAsync(); Authority.Dispose();
        }

        internal static CompiledGatePlan CompilePlan(uint revision)
        {
            GatePlanCompilationResult result = new GatePlanCompiler().Compile(
                GateTestPlan.Plan([GateTestPlan.Gate("A")], revision: revision));
            Assert.IsTrue(result.IsCompiled); return result.CompiledPlan!;
        }

        private static CompiledGatePlan Compile(uint revision) => CompilePlan(revision);
    }

    private sealed class FaultingEpochPort(IPreparedGatePlanEpochPort inner, bool throwOnCommit = false,
        bool throwInvariantOnCommit = false) : IPreparedGatePlanEpochPort
    {
        public GatePlanEpochPrepareResult PrepareEpoch(InspectionId inspectionId, AcquisitionRunId runId,
            CompiledGatePlan plan, InspectionRevision effectiveFromInclusive,
            GatePreparedReservationOwner owner, GatePreparedOperationGeneration generation) =>
            inner.PrepareEpoch(inspectionId, runId, plan, effectiveFromInclusive, owner, generation);

        public GatePreparedReservationResult CommitEpoch(GatePreparedReservation reservation,
            GatePreparedReservationOwner owner, GatePreparedOperationGeneration generation)
        {
            if (throwInvariantOnCommit)
                throw new ApplicationCommitInvariantException("synthetic invariant epoch failure");
            _ = inner.AbortEpoch(reservation, owner, generation);
            if (throwOnCommit) throw new InvalidOperationException("synthetic epoch commit failure");
            return inner.CommitEpoch(reservation, owner, generation);
        }

        public GatePreparedReservationResult AbortEpoch(GatePreparedReservation reservation,
            GatePreparedReservationOwner owner, GatePreparedOperationGeneration generation) =>
            inner.AbortEpoch(reservation, owner, generation);
    }

    private enum BlockEpochAt { Prepare, Commit }

    private sealed class BlockingEpochPort(IPreparedGatePlanEpochPort inner, BlockEpochAt blockAt) :
        IPreparedGatePlanEpochPort
    {
        internal TaskCompletionSource Entered { get; } = Signal();
        internal ManualResetEventSlim Release { get; } = new(false);

        public GatePlanEpochPrepareResult PrepareEpoch(InspectionId inspectionId, AcquisitionRunId runId,
            CompiledGatePlan plan, InspectionRevision effectiveFromInclusive,
            GatePreparedReservationOwner owner, GatePreparedOperationGeneration generation)
        {
            GatePlanEpochPrepareResult result = inner.PrepareEpoch(inspectionId, runId, plan,
                effectiveFromInclusive, owner, generation);
            if (blockAt == BlockEpochAt.Prepare) Block();
            return result;
        }

        public GatePreparedReservationResult CommitEpoch(GatePreparedReservation reservation,
            GatePreparedReservationOwner owner, GatePreparedOperationGeneration generation)
        {
            if (blockAt == BlockEpochAt.Commit) Block();
            return inner.CommitEpoch(reservation, owner, generation);
        }

        public GatePreparedReservationResult AbortEpoch(GatePreparedReservation reservation,
            GatePreparedReservationOwner owner, GatePreparedOperationGeneration generation) =>
            inner.AbortEpoch(reservation, owner, generation);

        private void Block()
        {
            Entered.TrySetResult();
            if (!Release.Wait(TimeSpan.FromSeconds(10)))
                throw new TimeoutException("The lifecycle arbitration test did not release the epoch port.");
        }
    }

    private sealed class SessionFixture : IAsyncDisposable
    {
        internal readonly InspectionId Inspection = new(Guid.NewGuid());
        internal readonly AcquisitionRunId Run = new(Guid.NewGuid());
        internal readonly ManualSessionSource Source = new();
        internal readonly InspectionDataStoreRegistry Registry;
        internal readonly InMemoryGateDerivedStore Store = new();
        internal readonly InMemoryGatePlanAuthorityOwner Authority;
        internal readonly IGatePlanAuthorityReader Reader;
        internal readonly CommittedGateProcessingOrchestrator Orchestrator;
        internal readonly BlockingEpochPort Epochs;
        internal readonly ApplicationSession Session;
        internal readonly CompiledGatePlan Initial;
        internal readonly GatePlanAuthorityKey AuthorityKey;

        internal SessionFixture(BlockEpochAt blockAt)
        {
            Registry = new(Inspection, new InMemoryInspectionStoreOptions(1024 * 1024, 32));
            Initial = Fixture.CompilePlan(1);
            Authority = new([new(Inspection, Initial)]);
            Reader = Authority.Seal(); AuthorityKey = new(Inspection, Initial.Source.Id);
            Orchestrator = new(Registry.Store, Registry.Store, new Sink(),
                new GateEvaluator(new GateScratchPool()));
            Epochs = new(Orchestrator, blockAt);
            var services = new GatePlanApplyServices(Reader, Authority.PreparedCommits,
                Epochs, Store.DesiredReplacementControl);
            Session = new(Source, Inspection, Registry.OpenRun, null, null, Orchestrator, Initial, services);
        }

        internal Task StartAsync() => Session.StartAsync(new(new PhysicalChannelId(0), 8, 50_000_000,
            firstSampleOffsetSeconds: 0.000_001), Run);

        internal GatePlanEditIntent Intent()
        {
            CommittedGatePlanSnapshot current = Reader.Read(AuthorityKey).Committed;
            var draft = new GatePlanDraft(AuthorityKey, current.Revision, current.Plan.SchemaVersion,
                current.Plan.AlgorithmVersion, [GateTestPlan.Gate("A", threshold: 0.4)]);
            return new(GatePlanEditIntentKind.Apply, AuthorityKey, current.Revision, draft);
        }

        public async ValueTask DisposeAsync()
        {
            Epochs.Release.Set();
            await Session.DisposeAsync(); await Orchestrator.DisposeAsync(); await Store.DisposeAsync();
            await Registry.DisposeAsync(); Authority.Dispose(); await Source.DisposeAsync(); Epochs.Release.Dispose();
        }
    }

    private sealed class TestLifecycle : IGatePlanApplyLifecycle
    {
        internal GatePlanApplyRunContext Context;
        internal bool AllowPointOfNoReturn = true;
        internal TaskCompletionSource PointOfNoReturnEntered { get; } = Signal();
        public bool IsRunning(GatePlanApplyRunContext context) => context == Context;
        public bool TryCrossPointOfNoReturn(GatePlanApplyRunContext context)
        {
            PointOfNoReturnEntered.TrySetResult();
            return AllowPointOfNoReturn && context == Context;
        }
    }

    private sealed class Sink : IGateEvaluationBatchSink
    {
        public ValueTask<GateBatchDeliveryResult> DeliverAsync(GateEvaluationBatch batch,
            CancellationToken exceptionalCancellation = default) => ValueTask.FromResult(GateBatchDeliveryResult.Accepted());
    }

    private sealed class Observer(Action<GatePlanAuthoritySnapshot> next) : IObserver<GatePlanAuthoritySnapshot>
    {
        public void OnNext(GatePlanAuthoritySnapshot value) => next(value);
        public void OnError(Exception error) => Assert.Fail(error.ToString());
        public void OnCompleted() { }
    }

    private static TaskCompletionSource Signal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

}
