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

    [TestMethod]
    public async Task HistoricalOwnerSurvivesImmediateCompletionAndActivatesExactlyOnce()
    {
        await using var fixture = new Fixture();
        GatePlanApplySubmission apply = fixture.UseCase.TryApply(fixture.Intent(), fixture.Context);
        Assert.AreEqual(GatePlanEditOutcome.Accepted, (await apply.Completion!).Outcome);
        CompiledGatePlan plan = fixture.Reader.Read(fixture.AuthorityKey).Committed.CompiledPlan;
        var stream = new UtStreamIdentity(fixture.Inspection, fixture.Run, new("history"), new(0));
        var scheduler = new ImmediateOwnedScheduler(fixture.Store);
        var coordinator = new GateHistoricalReplacementCoordinator(scheduler,
            fixture.Store.ReplacementControl, fixture.Authority.PreparedCommits);

        GateHistoricalReplacementSubmission admitted = await coordinator.TryAdmitAsync(
            fixture.AuthorityKey, fixture.Context, new(1),
            [new GateReprocessingStreamSelection(stream, [new(0, 0)])], 1);
        GateHistoricalReplacementTerminal terminal = await admitted.Completion!;

        Assert.AreEqual(GateHistoricalReplacementSubmissionStatus.Admitted, admitted.Status);
        Assert.IsNull(terminal.ObserverError, terminal.ObserverError?.ToString());
        Assert.IsTrue(terminal.Activation!.IsSuccess);
        Assert.AreEqual(plan.Source.Revision, fixture.Store.ActiveView!.Manifest.PlanRevision);
        GateHistoricalReplacementSubmission duplicate = await coordinator.TryAdmitAsync(
            fixture.AuthorityKey, fixture.Context, new(1),
            [new GateReprocessingStreamSelection(stream, [new(0, 0)])], 1);
        Assert.AreEqual(GateHistoricalReplacementSubmissionStatus.AlreadyRequested, duplicate.Status);
    }

    [TestMethod]
    [DataRow(GateReprocessingLifecycle.Faulted)]
    [DataRow(GateReprocessingLifecycle.Cancelled)]
    public async Task HistoricalTerminalFailureReconcilesOwnerAndWaiterCancellationDoesNotCancelJob(
        GateReprocessingLifecycle lifecycle)
    {
        await using var fixture = new Fixture();
        Assert.AreEqual(GatePlanEditOutcome.Accepted,
            (await fixture.UseCase.TryApply(fixture.Intent(), fixture.Context).Completion!).Outcome);
        var scheduler = new ControlledOwnedScheduler();
        var coordinator = new GateHistoricalReplacementCoordinator(scheduler,
            fixture.Store.ReplacementControl, fixture.Authority.PreparedCommits);
        var stream = new UtStreamIdentity(fixture.Inspection, fixture.Run, new("history"), new(0));
        GateHistoricalReplacementSubmission admitted = await coordinator.TryAdmitAsync(
            fixture.AuthorityKey, fixture.Context, new(1),
            [new GateReprocessingStreamSelection(stream, [new(0, 0)])], 1);
        using var waiterCancellation = new CancellationTokenSource();
        waiterCancellation.Cancel();
        await Assert.ThrowsExactlyAsync<TaskCanceledException>(() =>
            admitted.WaitAsync(waiterCancellation.Token));

        scheduler.Complete(lifecycle);
        GateHistoricalReplacementTerminal terminal = await admitted.Completion!;
        GateApplyLedgerSnapshot ledger = fixture.Authority.PreparedCommits
            .ReadRetainedLedgers(fixture.Inspection).Single(x => x.PlanRevision == new GatePlanRevision(2));

        Assert.AreEqual(lifecycle, terminal.Progress.Lifecycle);
        Assert.IsNull(terminal.ObserverError);
        Assert.AreEqual(GateCandidateRetirementState.Reclaimed, ledger.CandidateRetirement);
        Assert.AreEqual(GateApplyLedgerPendingResource.None, ledger.PendingResources);
        Assert.IsNull(fixture.Store.ActiveView);
    }

    [TestMethod]
    public async Task DisposeDuringAbandonTransfersPhysicalResponsibilityAndTerminatesObserverLedger()
    {
        await using var fixture = new Fixture();
        Assert.AreEqual(GatePlanEditOutcome.Accepted,
            (await fixture.UseCase.TryApply(fixture.Intent(), fixture.Context).Completion!).Outcome);
        var scheduler = new DelayedCompletedScheduler(fixture.Store);
        var blockedStore = new BlockingAbandonStore(fixture.Store.ReplacementControl);
        var coordinator = new GateHistoricalReplacementCoordinator(scheduler,
            blockedStore, fixture.Authority.PreparedCommits);
        var stream = new UtStreamIdentity(fixture.Inspection, fixture.Run, new("history"), new(0));
        GateHistoricalReplacementSubmission admitted = await coordinator.TryAdmitAsync(
            fixture.AuthorityKey, fixture.Context, new(1),
            [new GateReprocessingStreamSelection(stream, [new(0, 0)])], 1);
        var supersedingOwner = new GatePreparedReservationOwner(Guid.NewGuid());
        PrepareDesiredReplacementResult superseding = fixture.Store.DesiredReplacementControl.Prepare(
            new(fixture.Inspection, fixture.Run, fixture.Context.RunGeneration, fixture.AuthorityKey.PlanId,
                new(2), supersedingOwner));
        Assert.IsTrue(superseding.IsPrepared);
        Assert.AreEqual(GatePreparedReservationResultStatus.Committed,
            fixture.Store.DesiredReplacementControl.Commit(superseding.Prepared.Reservation,
                superseding.Prepared.Reservation.Owner, superseding.Prepared.Reservation.Generation).Status);

        scheduler.Complete();
        await blockedStore.AbandonEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await fixture.Store.DisposeAsync();
        blockedStore.ReleaseAbandon.Set();
        GateHistoricalReplacementTerminal terminal = await admitted.Completion!;
        GateApplyLedgerSnapshot old = fixture.Authority.PreparedCommits.ReadRetainedLedgers(fixture.Inspection)
            .Single(x => x.PlanRevision == new GatePlanRevision(2));

        Assert.IsNull(terminal.ObserverError, terminal.ObserverError?.ToString());
        Assert.AreEqual(GateCandidateAbandonStatus.Disposed, terminal.Abandonment!.Status);
        Assert.AreEqual(GateActivationState.Superseded, old.Activation);
        Assert.AreEqual(GateLedgerCauseKind.TokenSuperseded, old.PrimaryCause!.Value.Kind);
        Assert.AreEqual(GateCandidateRetirementState.Reclaimed, old.CandidateRetirement);
        Assert.AreEqual(GateApplyLedgerPendingResource.None, old.PendingResources);
        Assert.IsTrue(old.SecondaryCauses.Any(x => x.Stage == GateLedgerStage.CandidateRetirement &&
            x.Kind == GateLedgerCauseKind.Disposed));
    }

    [TestMethod]
    public async Task DisposeBetweenBindAndAdmissionReturnKeepsObserverOwnerUntilTerminalCompletion()
    {
        await using var fixture = new Fixture();
        Assert.AreEqual(GatePlanEditOutcome.Accepted,
            (await fixture.UseCase.TryApply(fixture.Intent(), fixture.Context).Completion!).Outcome);
        var scheduler = new BindBarrierScheduler();
        var coordinator = new GateHistoricalReplacementCoordinator(scheduler,
            fixture.Store.ReplacementControl, fixture.Authority.PreparedCommits);
        var stream = new UtStreamIdentity(fixture.Inspection, fixture.Run, new("history"), new(0));

        Task<GateHistoricalReplacementSubmission> admission = coordinator.TryAdmitAsync(
            fixture.AuthorityKey, fixture.Context, new(1),
            [new GateReprocessingStreamSelection(stream, [new(0, 0)])], 1).AsTask();
        await scheduler.Bound.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await fixture.Store.DisposeAsync();
        scheduler.ReleaseAdmission.TrySetResult();
        GateHistoricalReplacementSubmission admitted = await admission;
        Assert.AreEqual(GateHistoricalReplacementSubmissionStatus.Admitted, admitted.Status);

        scheduler.Complete(GateReprocessingLifecycle.Cancelled);
        GateHistoricalReplacementTerminal terminal = await admitted.Completion!;
        GateApplyLedgerSnapshot ledger = fixture.Authority.PreparedCommits.ReadRetainedLedgers(fixture.Inspection)
            .Single(x => x.PlanRevision == new GatePlanRevision(2));
        Assert.IsNull(terminal.ObserverError, terminal.ObserverError?.ToString());
        Assert.AreEqual(GateHistoricalExecutionState.Cancelled, ledger.HistoricalExecution);
        Assert.AreEqual(GateCandidateRetirementState.Reclaimed, ledger.CandidateRetirement);
        Assert.AreEqual(GateApplyLedgerPendingResource.None, ledger.PendingResources);
    }

    [TestMethod]
    public async Task DisposeBeforeReservationRejectsWithoutCreatingObserverOwner()
    {
        await using var fixture = new Fixture();
        Assert.AreEqual(GatePlanEditOutcome.Accepted,
            (await fixture.UseCase.TryApply(fixture.Intent(), fixture.Context).Completion!).Outcome);
        await fixture.Store.DisposeAsync();
        var coordinator = new GateHistoricalReplacementCoordinator(new ControlledOwnedScheduler(),
            fixture.Store.ReplacementControl, fixture.Authority.PreparedCommits);
        var stream = new UtStreamIdentity(fixture.Inspection, fixture.Run, new("history"), new(0));

        GateHistoricalReplacementSubmission submission = await coordinator.TryAdmitAsync(
            fixture.AuthorityKey, fixture.Context, new(1),
            [new GateReprocessingStreamSelection(stream, [new(0, 0)])], 1);
        GateApplyLedgerSnapshot ledger = fixture.Authority.PreparedCommits.ReadRetainedLedgers(fixture.Inspection)
            .Single(x => x.PlanRevision == new GatePlanRevision(2));

        Assert.AreEqual(GateHistoricalReplacementSubmissionStatus.Closed, submission.Status);
        Assert.IsNull(submission.Completion);
        Assert.AreEqual(GateHistoricalAdmissionState.Rejected, ledger.HistoricalAdmission);
        Assert.AreEqual(GateApplyLedgerPendingResource.None, ledger.PendingResources);
    }

    [TestMethod]
    public async Task DisposeDuringDeferredPreviousRetirementCompletesStoreTombstoneAndObserverLedger()
    {
        await using var fixture = new Fixture();
        var stream = new UtStreamIdentity(fixture.Inspection, fixture.Run, new("history"), new(0));
        CompiledGatePlan oldPlan = Fixture.CompilePlan(1);
        var identity = new TraceIdentity(stream, 0);
        var descriptor = new TraceDescriptor(identity, 0, TimeSpan.Zero, null, null,
            4, 10_000_000, 0, UtSignalMode.Rf, traceQuality: UtTraceQuality.Good);
        var trace = new ConfirmedGateTraceDescriptor(descriptor, identity, new(1), UtTraceQuality.Good);
        GateEvaluationBatch batch = new GateEvaluator(new GateScratchPool())
            .Evaluate(oldPlan, trace, new short[] { 1, 4, 2, 0 }).Batch!;
        Assert.AreEqual(GateDerivedMutationStatus.Committed, (await fixture.Store.CommitAsync(batch)).Status);
        var coverage = new GateDerivedStreamCoverage(stream, 0, 0, 1,
            GateDerivedStreamCoverage.ComputeIdentityDigest([identity]), [new(0, 0)]);
        var manifest = new GateDerivedCoverageManifest(fixture.Inspection, fixture.Run, [coverage], new(1),
            oldPlan.Source.Id, oldPlan.Source.Revision, oldPlan.Signature.Hex, oldPlan.Source.SchemaVersion,
            oldPlan.Source.AlgorithmVersion, 1, fixture.Store.Revision);
        Assert.AreEqual(GateDerivedMutationStatus.Committed,
            (await fixture.Store.ConfirmTerminalCoverageAsync([new(fixture.Inspection, fixture.Run, new(1),
                oldPlan.Source.Id, oldPlan.Source.Revision, oldPlan.Signature.Hex, oldPlan.Source.SchemaVersion,
                oldPlan.Source.AlgorithmVersion, [coverage])])).Status);
        manifest = new(fixture.Inspection, fixture.Run, [coverage], new(1), oldPlan.Source.Id,
            oldPlan.Source.Revision, oldPlan.Signature.Hex, oldPlan.Source.SchemaVersion,
            oldPlan.Source.AlgorithmVersion, 1, fixture.Store.Revision);
        Assert.AreEqual(GateDerivedMutationStatus.Activated,
            fixture.Store.Activate(manifest, new("old-view")).Status);

        Assert.AreEqual(GatePlanEditOutcome.Accepted,
            (await fixture.UseCase.TryApply(fixture.Intent(), fixture.Context).Completion!).Outcome);
        IGateDerivedReadLease lease = fixture.Store.OpenActiveView();
        var scheduler = new DelayedCompletedScheduler(fixture.Store);
        var signalingStore = new SignalingRetirementStore(fixture.Store.ReplacementControl);
        var coordinator = new GateHistoricalReplacementCoordinator(scheduler,
            signalingStore, fixture.Authority.PreparedCommits);
        GateHistoricalReplacementSubmission admitted = await coordinator.TryAdmitAsync(
            fixture.AuthorityKey, fixture.Context, new(1),
            [new GateReprocessingStreamSelection(stream, [new(0, 0)])], 1);

        scheduler.Complete();
        await signalingStore.RetirementEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Task dispose = fixture.Store.DisposeAsync().AsTask();
        Assert.IsFalse(dispose.IsCompleted);
        lease.Dispose();
        GateHistoricalReplacementTerminal terminal = await admitted.Completion!;
        await dispose;
        GateApplyLedgerSnapshot ledger = fixture.Authority.PreparedCommits.ReadRetainedLedgers(fixture.Inspection)
            .Single(x => x.PlanRevision == new GatePlanRevision(2));

        Assert.IsNull(terminal.ObserverError, terminal.ObserverError?.ToString());
        Assert.AreEqual(GateDerivedMutationStatus.Reclaimed, terminal.PreviousViewReclaim!.Value.Status);
        Assert.AreEqual(GatePreviousViewRetirementState.Retired, ledger.PreviousViewRetirement);
        Assert.AreEqual(GateApplyLedgerPendingResource.None, ledger.PendingResources);
        Assert.AreEqual(0, fixture.Store.Diagnostics.InvalidatedViewCount);
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

        internal GatePlanEditIntent IntentCurrent(double threshold)
        {
            CommittedGatePlanSnapshot current = Reader.Read(AuthorityKey).Committed;
            var draft = new GatePlanDraft(AuthorityKey, current.Revision, current.Plan.SchemaVersion,
                current.Plan.AlgorithmVersion, [GateTestPlan.Gate("A", threshold: threshold)]);
            return new(GatePlanEditIntentKind.Apply, AuthorityKey, current.Revision, draft);
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

    private sealed class ImmediateOwnedScheduler(InMemoryGateDerivedStore store) : IOwnedGateReprocessingScheduler
    {
        public async ValueTask<GateReprocessingAdmissionResult> AdmitOwnedAsync(GateReprocessingJobDescriptor descriptor,
            IGateReprocessingAdmissionOwner owner, CancellationToken cancellationToken = default)
        {
            if (cancellationToken.IsCancellationRequested)
                return new(GateReprocessingAdmissionStatus.Cancelled);
            var job = new ImmediateJob(descriptor);
            owner.Bind(job);
            byte[] emptyHeader = new byte[6];
            System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(emptyHeader,
                GateDerivedStreamCoverage.DigestCanonicalVersion);
            var coverages = descriptor.Selection.Select(item => new GateDerivedStreamCoverage(
                item.Stream, null, null, 0, System.Security.Cryptography.SHA256.HashData(emptyHeader),
                item.Ranges)).ToArray();
            GateDerivedMutationResult confirmed = await store.ConfirmTerminalCoverageAsync(
                [new(descriptor.InspectionId, descriptor.RunId, descriptor.SourceFinalRevision,
                    descriptor.Plan.Source.Id, descriptor.Plan.Source.Revision, descriptor.Plan.Signature.Hex,
                    descriptor.Plan.Source.SchemaVersion, descriptor.Plan.Source.AlgorithmVersion, coverages)]);
            Assert.AreEqual(GateDerivedMutationStatus.Committed, confirmed.Status);
            Assert.IsTrue(store.Revision.IsValid);
            var manifest = new GateDerivedCoverageManifest(descriptor.InspectionId, descriptor.RunId, coverages,
                descriptor.SourceFinalRevision, descriptor.Plan.Source.Id, descriptor.Plan.Source.Revision,
                descriptor.Plan.Signature.Hex, descriptor.Plan.Source.SchemaVersion,
                descriptor.Plan.Source.AlgorithmVersion, 0, store.Revision);
            GateStoreOnlyCandidateDescriptor candidate = owner.MaterializeCandidate(manifest);
            job.Complete(candidate);
            return new(GateReprocessingAdmissionStatus.Admitted, job);
        }

        internal sealed class ImmediateJob(GateReprocessingJobDescriptor descriptor) : IGateReprocessingJob
        {
            private readonly TaskCompletionSource<GateReprocessingProgressSnapshot> _completion =
                new(TaskCreationOptions.RunContinuationsAsynchronously);
            private GateReprocessingProgressSnapshot? _progress;
            public GateReprocessingProgressSnapshot Progress => _progress ?? throw new InvalidOperationException();
            public Task<GateReprocessingProgressSnapshot> Completion => _completion.Task;
            public void Cancel() { }
            internal void Complete(GateStoreOnlyCandidateDescriptor candidate)
            {
                DateTimeOffset now = DateTimeOffset.UtcNow;
                _progress = new(descriptor.JobId, descriptor.AttemptId, GateReprocessingLifecycle.Completed,
                    descriptor.SourceFinalRevision, descriptor.Plan.Source.Revision,
                    GateReprocessingPublicationPolicy.StoreOnly, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
                    null, null, now, now, requestedCoverage: descriptor.Selection,
                    planEpoch: new(descriptor.Plan.Source.Id, descriptor.Plan.Source.Revision,
                        descriptor.Plan.Signature, descriptor.Plan.Source.SchemaVersion,
                        descriptor.Plan.Source.AlgorithmVersion), storeOnlyCandidate: candidate);
                _completion.SetResult(_progress);
            }
        }
    }

    private sealed class ControlledOwnedScheduler : IOwnedGateReprocessingScheduler
    {
        private ControlledJob? _job;
        public ValueTask<GateReprocessingAdmissionResult> AdmitOwnedAsync(GateReprocessingJobDescriptor descriptor,
            IGateReprocessingAdmissionOwner owner, CancellationToken cancellationToken = default)
        {
            _job = new(descriptor); owner.Bind(_job);
            return ValueTask.FromResult(new GateReprocessingAdmissionResult(
                GateReprocessingAdmissionStatus.Admitted, _job));
        }
        internal void Complete(GateReprocessingLifecycle lifecycle) => _job!.Complete(lifecycle);

        private sealed class ControlledJob(GateReprocessingJobDescriptor descriptor) : IGateReprocessingJob
        {
            private readonly TaskCompletionSource<GateReprocessingProgressSnapshot> _completion =
                new(TaskCreationOptions.RunContinuationsAsynchronously);
            private GateReprocessingProgressSnapshot? _progress;
            public GateReprocessingProgressSnapshot Progress => _progress ?? throw new InvalidOperationException();
            public Task<GateReprocessingProgressSnapshot> Completion => _completion.Task;
            public void Cancel() { }
            internal void Complete(GateReprocessingLifecycle lifecycle)
            {
                DateTimeOffset now = DateTimeOffset.UtcNow;
                _progress = new(descriptor.JobId, descriptor.AttemptId, lifecycle,
                    descriptor.SourceFinalRevision, descriptor.Plan.Source.Revision,
                    GateReprocessingPublicationPolicy.StoreOnly, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
                    lifecycle == GateReprocessingLifecycle.Faulted ? "controlled-fault" : null,
                    null, now, now, requestedCoverage: descriptor.Selection,
                    planEpoch: new(descriptor.Plan.Source.Id, descriptor.Plan.Source.Revision,
                        descriptor.Plan.Signature, descriptor.Plan.Source.SchemaVersion,
                        descriptor.Plan.Source.AlgorithmVersion));
                _completion.SetResult(_progress);
            }
        }
    }

    private sealed class BindBarrierScheduler : IOwnedGateReprocessingScheduler
    {
        private ControlledJob? _job;
        internal TaskCompletionSource Bound { get; } = Signal();
        internal TaskCompletionSource ReleaseAdmission { get; } = Signal();

        public async ValueTask<GateReprocessingAdmissionResult> AdmitOwnedAsync(GateReprocessingJobDescriptor descriptor,
            IGateReprocessingAdmissionOwner owner, CancellationToken cancellationToken = default)
        {
            _job = new(descriptor); owner.Bind(_job); Bound.TrySetResult();
            await ReleaseAdmission.Task.WaitAsync(TimeSpan.FromSeconds(5));
            return new(GateReprocessingAdmissionStatus.Admitted, _job);
        }

        internal void Complete(GateReprocessingLifecycle lifecycle) => _job!.Complete(lifecycle);

        private sealed class ControlledJob(GateReprocessingJobDescriptor descriptor) : IGateReprocessingJob
        {
            private readonly TaskCompletionSource<GateReprocessingProgressSnapshot> _completion =
                new(TaskCreationOptions.RunContinuationsAsynchronously);
            private GateReprocessingProgressSnapshot? _progress;
            public GateReprocessingProgressSnapshot Progress => _progress ?? throw new InvalidOperationException();
            public Task<GateReprocessingProgressSnapshot> Completion => _completion.Task;
            public void Cancel() { }
            internal void Complete(GateReprocessingLifecycle lifecycle)
            {
                DateTimeOffset now = DateTimeOffset.UtcNow;
                _progress = new(descriptor.JobId, descriptor.AttemptId, lifecycle,
                    descriptor.SourceFinalRevision, descriptor.Plan.Source.Revision,
                    GateReprocessingPublicationPolicy.StoreOnly, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
                    null, null, now, now, requestedCoverage: descriptor.Selection,
                    planEpoch: new(descriptor.Plan.Source.Id, descriptor.Plan.Source.Revision,
                        descriptor.Plan.Signature, descriptor.Plan.Source.SchemaVersion,
                        descriptor.Plan.Source.AlgorithmVersion));
                _completion.SetResult(_progress);
            }
        }
    }

    private sealed class DelayedCompletedScheduler(InMemoryGateDerivedStore store) : IOwnedGateReprocessingScheduler
    {
        private GateReprocessingJobDescriptor? _descriptor;
        private IGateReprocessingAdmissionOwner? _owner;
        private ImmediateOwnedScheduler.ImmediateJob? _job;
        public ValueTask<GateReprocessingAdmissionResult> AdmitOwnedAsync(GateReprocessingJobDescriptor descriptor,
            IGateReprocessingAdmissionOwner owner, CancellationToken cancellationToken = default)
        {
            _descriptor = descriptor; _owner = owner; _job = new(descriptor); owner.Bind(_job);
            return ValueTask.FromResult(new GateReprocessingAdmissionResult(GateReprocessingAdmissionStatus.Admitted, _job));
        }
        internal void Complete()
        {
            GateReprocessingJobDescriptor descriptor = _descriptor!;
            byte[] header = new byte[6];
            System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(header,
                GateDerivedStreamCoverage.DigestCanonicalVersion);
            GateDerivedStreamCoverage[] coverage = descriptor.Selection.Select(item => new GateDerivedStreamCoverage(
                item.Stream, null, null, 0, System.Security.Cryptography.SHA256.HashData(header), item.Ranges)).ToArray();
            Assert.AreEqual(GateDerivedMutationStatus.Committed, store.ConfirmTerminalCoverageAsync(
                [new(descriptor.InspectionId, descriptor.RunId, descriptor.SourceFinalRevision,
                    descriptor.Plan.Source.Id, descriptor.Plan.Source.Revision, descriptor.Plan.Signature.Hex,
                    descriptor.Plan.Source.SchemaVersion, descriptor.Plan.Source.AlgorithmVersion, coverage)])
                .AsTask().GetAwaiter().GetResult().Status);
            var manifest = new GateDerivedCoverageManifest(descriptor.InspectionId, descriptor.RunId, coverage,
                descriptor.SourceFinalRevision, descriptor.Plan.Source.Id, descriptor.Plan.Source.Revision,
                descriptor.Plan.Signature.Hex, descriptor.Plan.Source.SchemaVersion,
                descriptor.Plan.Source.AlgorithmVersion, 0, store.Revision);
            _job!.Complete(_owner!.MaterializeCandidate(manifest));
        }
    }

    private sealed class BlockingAbandonStore(IGateReplacementStore inner) : IGateReplacementStore
    {
        internal TaskCompletionSource AbandonEntered { get; } = Signal();
        internal ManualResetEventSlim ReleaseAbandon { get; } = new(false);
        public GateCandidateReservationResult ReserveCandidate(InspectionId inspectionId, AcquisitionRunId runId,
            GateRunGeneration runGeneration, GatePlanId planId, GatePlanRevision planRevision,
            GateReprocessingAttemptId attemptId) => inner.ReserveCandidate(inspectionId, runId, runGeneration,
                planId, planRevision, attemptId);
        public GateStoreOnlyCandidateDescriptor MaterializeCandidate(GateCandidateReservation reservation,
            GateDerivedCoverageManifest manifest) => inner.MaterializeCandidate(reservation, manifest);
        public void ReleaseReservation(GateCandidateReservation reservation) => inner.ReleaseReservation(reservation);
        public GateCandidateActivationResult ActivateCandidate(GateCandidateActivationRequest request) =>
            inner.ActivateCandidate(request);
        public GateCandidateAbandonResult AbandonCandidate(GateStoreOnlyCandidateDescriptor candidate)
        {
            AbandonEntered.TrySetResult();
            if (!ReleaseAbandon.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException();
            return inner.AbandonCandidate(candidate);
        }
        public GateCandidateReclaimResult ReclaimCandidate(GateStoreOnlyCandidateDescriptor candidate) =>
            inner.ReclaimCandidate(candidate);
        public GateDerivedMutationResult ReclaimPreviousView() => inner.ReclaimPreviousView();
        public ValueTask<GateDerivedMutationResult> CompletePreviousViewRetirementAsync() =>
            inner.CompletePreviousViewRetirementAsync();
    }

    private sealed class SignalingRetirementStore(IGateReplacementStore inner) : IGateReplacementStore
    {
        internal TaskCompletionSource RetirementEntered { get; } = Signal();
        public GateCandidateReservationResult ReserveCandidate(InspectionId inspectionId, AcquisitionRunId runId,
            GateRunGeneration runGeneration, GatePlanId planId, GatePlanRevision planRevision,
            GateReprocessingAttemptId attemptId) => inner.ReserveCandidate(inspectionId, runId, runGeneration,
                planId, planRevision, attemptId);
        public GateStoreOnlyCandidateDescriptor MaterializeCandidate(GateCandidateReservation reservation,
            GateDerivedCoverageManifest manifest) => inner.MaterializeCandidate(reservation, manifest);
        public void ReleaseReservation(GateCandidateReservation reservation) => inner.ReleaseReservation(reservation);
        public GateCandidateActivationResult ActivateCandidate(GateCandidateActivationRequest request) =>
            inner.ActivateCandidate(request);
        public GateCandidateAbandonResult AbandonCandidate(GateStoreOnlyCandidateDescriptor candidate) =>
            inner.AbandonCandidate(candidate);
        public GateCandidateReclaimResult ReclaimCandidate(GateStoreOnlyCandidateDescriptor candidate) =>
            inner.ReclaimCandidate(candidate);
        public GateDerivedMutationResult ReclaimPreviousView() => inner.ReclaimPreviousView();
        public ValueTask<GateDerivedMutationResult> CompletePreviousViewRetirementAsync()
        {
            RetirementEntered.TrySetResult();
            return inner.CompletePreviousViewRetirementAsync();
        }
    }

    private static TaskCompletionSource Signal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

}
