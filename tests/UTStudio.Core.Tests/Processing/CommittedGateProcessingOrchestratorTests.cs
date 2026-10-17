using UTStudio.Contracts.Inspection;
using UTStudio.Contracts.Processing;
using UTStudio.Domain.Acquisition;
using UTStudio.Domain.Inspection;
using UTStudio.Domain.Processing;
using UTStudio.Inspection.Core;
using UTStudio.Processing.Core;

namespace UTStudio.Core.Tests.Processing;

[TestClass]
public sealed class CommittedGateProcessingOrchestratorTests
{
    [TestMethod]
    public async Task CommittedSignalReadsEvaluatesDeliversAndBalancesLease()
    {
        await using var fixture = new Fixture();
        InspectionAppendResult commit = await fixture.CommitAsync(0, [1, 9, -3]);
        Assert.AreEqual<ulong>(1, commit.Revision.Value);
        Assert.AreEqual(GateProcessingAdmissionStatus.Accepted,
            fixture.Orchestrator.NotifyCommitted(new(fixture.Identity(0), commit.Revision)));

        GateEvaluationBatch batch = await fixture.Sink.Next.Task;
        Assert.AreEqual(fixture.Identity(0), batch.Trace.ConfirmedIdentity);
        Assert.AreEqual(commit.Revision, batch.Trace.SourceInspectionRevision);
        await fixture.SealAndDrainAsync();

        GateProcessingDiagnostics diagnostics = fixture.Orchestrator.Diagnostics;
        Assert.AreEqual(1L, diagnostics.ProviderAvailable);
        Assert.AreEqual(1L, diagnostics.LeasesAcquired);
        Assert.AreEqual(1L, diagnostics.LeasesReleased);
        Assert.AreEqual(0, diagnostics.ActiveLeases);
        Assert.AreEqual(1L, diagnostics.BatchesAccepted);
        Assert.AreEqual(0, diagnostics.ActiveCallbacks);
    }

    [TestMethod]
    public async Task FinalRevisionRecoversLostAndDuplicateSignalsWithoutDoubleEvaluation()
    {
        await using var fixture = new Fixture();
        InspectionAppendResult first = await fixture.CommitAsync(0, [4, 5]);
        InspectionAppendResult second = await fixture.CommitAsync(1, [7, 2]);
        Assert.AreEqual(GateProcessingAdmissionStatus.Accepted,
            fixture.Orchestrator.NotifyCommitted(new(fixture.Identity(1), second.Revision)));
        GateProcessingAdmissionStatus duplicate = fixture.Orchestrator.NotifyCommitted(
            new(fixture.Identity(1), second.Revision));
        Assert.IsTrue(duplicate is GateProcessingAdmissionStatus.Coalesced or GateProcessingAdmissionStatus.Duplicate);

        await fixture.SealAndDrainAsync();
        Assert.HasCount(2, fixture.Sink.Batches);
        CollectionAssert.AreEqual(new[] { first.Revision.Value, second.Revision.Value },
            fixture.Sink.Batches.Select(x => x.Trace.SourceInspectionRevision.Value).ToArray());
        Assert.AreEqual(2L, fixture.Orchestrator.Diagnostics.EvaluationsCompleted);
    }

    [TestMethod]
    public void SignalContractIsSampleFreeAndSchedulerCapacityIsBounded()
    {
        string[] properties = typeof(GateProcessingSignal).GetProperties().Select(x => x.Name).ToArray();
        CollectionAssert.Contains(properties, nameof(GateProcessingSignal.Revision));
        CollectionAssert.Contains(properties, nameof(GateProcessingSignal.TraceIdentity));
        Assert.IsFalse(properties.Any(x => x.Contains("Sample", StringComparison.OrdinalIgnoreCase)));
        var options = new GateProcessingOptions();
        Assert.AreEqual(2, options.MaximumRuns);
        Assert.AreEqual(8, options.MaximumStreamsPerRun);
        Assert.AreEqual(8, options.MaximumPlanEpochsPerRun);
        Assert.AreEqual(256, options.CatalogPageSize);
        Assert.AreEqual(65_536, options.MaximumEligibleTracesPerPlanEpoch);
    }

    [TestMethod]
    public async Task TerminalCoverageLedgerAdmitsExactLimitAndFaultsBeforeNextIdentity()
    {
        await using var fixture = new Fixture(new(maximumEligibleTracesPerPlanEpoch: 2));
        _ = await fixture.CommitAsync(0, [1, 2]);
        _ = await fixture.CommitAsync(1, [2, 3]);
        InspectionAppendResult third = await fixture.CommitAsync(2, [3, 4]);
        fixture.Orchestrator.NotifyCommitted(new(fixture.Identity(2), third.Revision));
        InspectionRunSealResult seal = await fixture.Writer.SealRunAsync();
        fixture.MarkSealed();
        fixture.Orchestrator.StopAdmission(fixture.Run);
        fixture.Orchestrator.RequestFinalReconciliation(fixture.Run, seal.FinalRevision);

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            fixture.Orchestrator.WaitForDrainAsync(fixture.Run).AsTask());

        GateProcessingDiagnostics diagnostics = fixture.Orchestrator.Diagnostics;
        GateProcessingRunDiagnostics failed = diagnostics.ActiveRuns.Concat(diagnostics.TerminalRuns).Single();
        Assert.AreEqual(GateProcessingLifecycleState.Faulted, failed.Lifecycle);
        Assert.AreEqual(2L, failed.WorkAdmitted);
        Assert.AreEqual(2L, failed.WorkTerminal);
        Assert.AreEqual(2L, failed.Plans.Single().ConfirmedEligible);
        Assert.AreEqual(2L, failed.Plans.Single().EvaluatedAndDelivered);
        Assert.AreEqual(1L, diagnostics.Saturations);
        Assert.HasCount(2, fixture.Sink.Batches);
    }

    [TestMethod]
    public async Task Scenario06_07_15_OutOfOrderLateAndCoalescedSignalsPreserveRevisionOrder()
    {
        await using var fixture = new Fixture();
        InspectionAppendResult one = await fixture.CommitAsync(0, [1, 2]);
        _ = await fixture.CommitAsync(1, [2, 3]);
        InspectionAppendResult three = await fixture.CommitAsync(2, [3, 4]);
        Assert.AreEqual(GateProcessingAdmissionStatus.Accepted,
            fixture.Orchestrator.NotifyCommitted(new(fixture.Identity(2), three.Revision)));
        Assert.AreEqual(GateProcessingAdmissionStatus.Coalesced,
            fixture.Orchestrator.NotifyCommitted(new(fixture.Identity(0), one.Revision)));
        Assert.AreEqual(three.Revision, fixture.Orchestrator.Diagnostics.ActiveRuns.Single()
            .Watermarks.Single().ObservedRevision);
        InspectionRunSealResult seal = await fixture.Writer.SealRunAsync(); fixture.MarkSealed();
        fixture.Orchestrator.StopAdmission(fixture.Run);
        Assert.AreEqual(GateProcessingAdmissionStatus.Late,
            fixture.Orchestrator.NotifyCommitted(new(fixture.Identity(0), one.Revision)));
        fixture.Orchestrator.RequestFinalReconciliation(fixture.Run, seal.FinalRevision);
        await fixture.Orchestrator.WaitForDrainAsync(fixture.Run);
        CollectionAssert.AreEqual(new ulong[] { 1, 2, 3 },
            fixture.Sink.Batches.Select(x => x.Trace.SourceInspectionRevision.Value).ToArray());
    }

    [TestMethod]
    public async Task Scenario18_22_NotAvailableIsNonterminalAndProviderProgressReactivatesItOnce()
    {
        await using var registry = new InspectionDataStoreRegistry(new(Guid.NewGuid()),
            new InMemoryInspectionStoreOptions(1024, 10));
        AcquisitionRunId run = new(Guid.NewGuid());
        await using IInspectionRunWriter writer = registry.OpenRun(run);
        UtStreamIdentity stream = new(registry.InspectionId, run, new("provisional"), new(0));
        TraceIdentity identity = new(stream, 0);
        var descriptor = Descriptor(identity, 0, 2);
        InspectionAppendResult commit = await writer.CommitAsync(new(new(descriptor, new short[] { 1, 7 })));
        var provider = new ProvisionalProvider(registry.Store);
        var sink = new CapturingSink();
        await using var orchestrator = new CommittedGateProcessingOrchestrator(registry.Store, provider, sink,
            new GateEvaluator(new GateScratchPool()));
        Assert.AreEqual(GateProcessingAdmissionStatus.Accepted,
            orchestrator.OpenRun(new(registry.InspectionId, run, [stream], CompilePlan())));
        orchestrator.NotifyCommitted(new(identity, commit.Revision));
        await provider.FirstRead.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.HasCount(1, orchestrator.Diagnostics.ActiveRuns);
        GateProcessingRunDiagnostics suspended = orchestrator.Diagnostics.ActiveRuns[0];
        Assert.AreEqual(1L, suspended.WorkAdmitted);
        Assert.AreEqual(0L, suspended.WorkTerminal);
        provider.MakeAvailable();
        orchestrator.NotifyProviderProgress(run);
        await sink.Next.Task.WaitAsync(TimeSpan.FromSeconds(10));
        InspectionRunSealResult seal = await writer.SealRunAsync();
        orchestrator.StopAdmission(run); orchestrator.RequestFinalReconciliation(run, seal.FinalRevision);
        await orchestrator.WaitForDrainAsync(run).AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.HasCount(1, orchestrator.Diagnostics.TerminalRuns);
        GateProcessingRunDiagnostics terminal = orchestrator.Diagnostics.TerminalRuns[0];
        Assert.AreEqual(1L, terminal.WorkAdmitted);
        Assert.AreEqual(1L, terminal.WorkTerminal);
        Assert.IsTrue(terminal.Plans.Single().IsLosslessBalanced);
        Assert.IsGreaterThanOrEqualTo(2, provider.ReadCount);
        Assert.HasCount(1, sink.Batches);
    }

    [TestMethod]
    public async Task Scenario31_32_33_OpenEmptyRunOnlyDrainsAfterFinalRevision()
    {
        await using var fixture = new Fixture();
        Task drain = fixture.Orchestrator.WaitForDrainAsync(fixture.Run).AsTask();
        Assert.IsFalse(drain.IsCompleted);
        await fixture.SealAndDrainAsync();
        Assert.HasCount(1, fixture.Orchestrator.Diagnostics.TerminalRuns);
        GateProcessingRunDiagnostics terminal = fixture.Orchestrator.Diagnostics.TerminalRuns[0];
        Assert.IsTrue(terminal.FinalityConfirmed);
        Assert.AreEqual(InspectionRevision.Initial, terminal.FinalRevision);
        Assert.AreEqual(0L, terminal.WorkAdmitted);
    }

    [TestMethod]
    public async Task Scenario34_35_GapOnlyAndSparseIndicesNeverCreateSyntheticTrace()
    {
        await using var fixture = new Fixture();
        var gap = new GapRecord(fixture.Stream, 1, 4, 0, 5,
            InspectionGapCause.SourceSequenceDiscontinuity);
        InspectionAppendResult committed = await fixture.Writer.CommitGapAsync(gap);
        Assert.AreEqual(InspectionAppendStatus.Committed, committed.Status);
        await fixture.SealAndDrainAsync();
        Assert.IsEmpty(fixture.Sink.Batches);
        Assert.AreEqual(0L, fixture.Orchestrator.Diagnostics.ProviderAvailable);
        Assert.AreEqual(1L, fixture.Orchestrator.Diagnostics.RevisionsReconciled);
    }

    [TestMethod]
    public async Task Scenario36_37_38_42_PlanEpochsArePinnedAndDiagnosedPerRun()
    {
        await using var fixture = new Fixture();
        InspectionAppendResult first = await fixture.CommitAsync(0, [1, 5]);
        fixture.Orchestrator.NotifyCommitted(new(fixture.Identity(0), first.Revision));
        await fixture.Sink.Next.Task.WaitAsync(TimeSpan.FromSeconds(10));
        CompiledGatePlan secondPlan = CompilePlan(2, "plan-2");
        Assert.AreEqual(GateProcessingAdmissionStatus.Accepted,
            fixture.Orchestrator.ActivatePlan(fixture.Run, secondPlan, new(2)));
        InspectionAppendResult second = await fixture.CommitAsync(1, [7, 2]);
        fixture.Orchestrator.NotifyCommitted(new(fixture.Identity(1), second.Revision));
        await fixture.SealAndDrainAsync();
        CollectionAssert.AreEqual(new uint[] { 1, 2 }, fixture.Sink.Batches
            .Select(x => x.Entries.Single().PlanRevision.Value).ToArray());
        GateProcessingRunDiagnostics terminal = fixture.Orchestrator.Diagnostics.TerminalRuns.Single();
        Assert.HasCount(2, terminal.Plans);
        Assert.IsTrue(terminal.Plans.All(x => x.IsLosslessBalanced));
        Assert.AreEqual(secondPlan.Source.Revision, terminal.PlanRevision);
        Assert.AreEqual(secondPlan.Signature, terminal.PlanSignature);
    }

    [TestMethod]
    [DataRow(GateBatchDeliveryStatus.Accepted)]
    [DataRow(GateBatchDeliveryStatus.Rejected)]
    [DataRow(GateBatchDeliveryStatus.Failed)]
    [DataRow(GateBatchDeliveryStatus.Cancelled)]
    public async Task Scenarios48_49_SinkOutcomesAreExplicitAndLosslessBalanced(GateBatchDeliveryStatus status)
    {
        await using var fixture = new Fixture(); fixture.Sink.Status = status;
        InspectionAppendResult commit = await fixture.CommitAsync(0, [1, 9]);
        fixture.Orchestrator.NotifyCommitted(new(fixture.Identity(0), commit.Revision));
        await fixture.Sink.Next.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await fixture.SealAndDrainAsync();
        GateProcessingPlanBalance balance = fixture.Orchestrator.Diagnostics.TerminalRuns.Single().Plans.Single();
        Assert.IsTrue(balance.IsLosslessBalanced);
        if (status == GateBatchDeliveryStatus.Accepted) { Assert.AreEqual(1L, balance.EvaluatedAndDelivered); }
        else { Assert.AreEqual(1L, balance.TerminallyFailed); }
    }

    [TestMethod]
    public async Task Scenarios50_51_ThrowingAndReentrantSinkAreOwnedAndDiagnosed()
    {
        await using var fixture = new Fixture();
        fixture.Sink.OnDeliver = batch =>
        {
            Assert.IsNotNull(batch);
            GateProcessingDiagnostics snapshot = fixture.Orchestrator.Diagnostics;
            Assert.IsNotNull(snapshot);
            fixture.Orchestrator.NotifyProviderProgress(fixture.Run);
            throw new InvalidOperationException("sink");
        };
        InspectionAppendResult commit = await fixture.CommitAsync(0, [1, 9]);
        fixture.Orchestrator.NotifyCommitted(new(fixture.Identity(0), commit.Revision));
        await fixture.Sink.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await fixture.SealAndDrainAsync();
        GateProcessingDiagnostics diagnostics = fixture.Orchestrator.Diagnostics;
        Assert.AreEqual(1L, diagnostics.BatchesFailed);
        Assert.AreEqual(diagnostics.CallbacksAdmitted, diagnostics.CallbacksCompleted);
        Assert.AreEqual(0, diagnostics.ActiveCallbacks);
        Assert.IsNotNull(diagnostics.PrimaryError);
    }

    [TestMethod]
    public async Task Scenarios52_61_StopWaitsForSuspendedCallbackAndReleasesLeaseAfterward()
    {
        await using var fixture = new Fixture(); fixture.Sink.Suspend = true;
        InspectionAppendResult commit = await fixture.CommitAsync(0, [1, 9]);
        fixture.Orchestrator.NotifyCommitted(new(fixture.Identity(0), commit.Revision));
        await fixture.Sink.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        InspectionRunSealResult seal = await fixture.Writer.SealRunAsync();
        fixture.Orchestrator.StopAdmission(fixture.Run);
        fixture.Orchestrator.RequestFinalReconciliation(fixture.Run, seal.FinalRevision);
        Task drain = fixture.Orchestrator.WaitForDrainAsync(fixture.Run).AsTask();
        Assert.IsFalse(drain.IsCompleted); Assert.AreEqual(1, fixture.Orchestrator.Diagnostics.ActiveCallbacks);
        fixture.Sink.Release.TrySetResult();
        await drain.WaitAsync(TimeSpan.FromSeconds(10)); fixture.MarkSealed();
        Assert.AreEqual(0, fixture.Orchestrator.Diagnostics.ActiveCallbacks);
        Assert.AreEqual(fixture.Orchestrator.Diagnostics.LeasesAcquired,
            fixture.Orchestrator.Diagnostics.LeasesReleased);
    }

    [TestMethod]
    public async Task Scenarios62_64_65_73_ConcurrentDisposeIsSharedAndClosedOnlyAfterCleanup()
    {
        var inspection = new InspectionId(Guid.NewGuid());
        await using var registry = new InspectionDataStoreRegistry(inspection, new(1024, 10));
        var orchestrator = new CommittedGateProcessingOrchestrator(registry.Store, registry.Store,
            new CapturingSink(), new GateEvaluator(new GateScratchPool()));
        Task[] disposals = Enumerable.Range(0, 8).Select(_ => Task.Run(() => orchestrator.DisposeAsync().AsTask())).ToArray();
        await Task.WhenAll(disposals).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.IsTrue(disposals.All(x => x.IsCompletedSuccessfully));
        Assert.AreEqual(GateProcessingLifecycleState.Closed, orchestrator.Diagnostics.State);
        Assert.AreEqual(0, orchestrator.Diagnostics.ActiveWorkers);
        Assert.AreEqual(0, orchestrator.Diagnostics.ActiveCallbacks);
        Assert.AreEqual(0, orchestrator.Diagnostics.ActiveLeases);
        await orchestrator.DisposeAsync();
    }

    [TestMethod]
    [DataRow(InspectionReadStatus.Gap)]
    [DataRow(InspectionReadStatus.OutOfRange)]
    [DataRow(InspectionReadStatus.Cancelled)]
    [DataRow(InspectionReadStatus.Closed)]
    [DataRow(InspectionReadStatus.Faulted)]
    public async Task Scenarios23_24_25_26_27_ProviderTerminalStatesFailExplicitlyAndBalance(
        InspectionReadStatus status)
    {
        await using var fixture = new Fixture();
        InspectionAppendResult commit = await fixture.CommitAsync(0, [1, 2]);
        var provider = new TerminalProvider(status);
        await using var orchestrator = new CommittedGateProcessingOrchestrator(fixture.Registry.Store, provider,
            new CapturingSink(), new GateEvaluator(new GateScratchPool()));
        orchestrator.OpenRun(new(fixture.Inspection, fixture.Run, [fixture.Stream], CompilePlan()));
        orchestrator.NotifyCommitted(new(fixture.Identity(0), commit.Revision));
        InspectionRunSealResult seal = await fixture.Writer.SealRunAsync(); fixture.MarkSealed();
        orchestrator.StopAdmission(fixture.Run); orchestrator.RequestFinalReconciliation(fixture.Run, seal.FinalRevision);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            orchestrator.WaitForDrainAsync(fixture.Run).AsTask().WaitAsync(TimeSpan.FromSeconds(10)));
        GateProcessingRunDiagnostics terminal = orchestrator.Diagnostics.TerminalRuns.Single();
        Assert.AreEqual(1L, terminal.Plans.Single().TerminallyFailed);
        Assert.IsTrue(terminal.Plans.Single().IsLosslessBalanced);
        Assert.AreEqual(GateProcessingLifecycleState.Faulted, terminal.Lifecycle);
    }

    [TestMethod]
    public async Task Scenarios28_29_CatalogAndProviderExceptionsFailDrainWithoutLeakingCapacity()
    {
        await using var fixture = new Fixture();
        InspectionAppendResult commit = await fixture.CommitAsync(0, [1, 2]);
        foreach (bool catalogFailure in new[] { true, false })
        {
            IInspectionChangeProvider catalog = catalogFailure ? new ThrowingCatalog() : fixture.Registry.Store;
            IUtTraceProvider provider = catalogFailure ? fixture.Registry.Store : new ThrowingProvider();
            await using var orchestrator = new CommittedGateProcessingOrchestrator(catalog, provider,
                new CapturingSink(), new GateEvaluator(new GateScratchPool()));
            orchestrator.OpenRun(new(fixture.Inspection, fixture.Run, [fixture.Stream], CompilePlan()));
            orchestrator.NotifyCommitted(new(fixture.Identity(0), commit.Revision));
            orchestrator.StopAdmission(fixture.Run); orchestrator.RequestFinalReconciliation(fixture.Run, commit.Revision);
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                orchestrator.WaitForDrainAsync(fixture.Run).AsTask().WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.AreEqual(1L, orchestrator.Diagnostics.FailedRuns);
            Assert.IsEmpty(orchestrator.Diagnostics.ActiveRuns);
        }
        _ = await fixture.Writer.SealRunAsync(); fixture.MarkSealed();
    }

    [TestMethod]
    public async Task Scenarios11_12_16_66_AdmissionAndSaturationAreTypedBoundedAndPreserveAcceptedRun()
    {
        var inspection = new InspectionId(Guid.NewGuid());
        await using var registry = new InspectionDataStoreRegistry(inspection, new(4096, 20));
        var run1 = new AcquisitionRunId(Guid.NewGuid()); var run2 = new AcquisitionRunId(Guid.NewGuid());
        UtStreamIdentity stream1 = new(inspection, run1, new("capacity"), new(0));
        UtStreamIdentity stream2 = new(inspection, run2, new("capacity"), new(0));
        await using IInspectionRunWriter writer = registry.OpenRun(run1);
        var sink = new CapturingSink();
        await using var orchestrator = new CommittedGateProcessingOrchestrator(registry.Store, registry.Store,
            sink, new GateEvaluator(new GateScratchPool()), new(maximumRuns: 1));
        Assert.AreEqual(GateProcessingAdmissionStatus.Accepted,
            orchestrator.OpenRun(new(inspection, run1, [stream1], CompilePlan())));
        Assert.AreEqual(GateProcessingAdmissionStatus.Rejected,
            orchestrator.OpenRun(new(inspection, run2, [stream2], CompilePlan())));
        var identity = new TraceIdentity(stream1, 0); var descriptor = Descriptor(identity, 0, 2);
        InspectionAppendResult commit = await writer.CommitAsync(new(new(descriptor, new short[] { 1, 2 })));
        orchestrator.NotifyCommitted(new(identity, commit.Revision));
        InspectionRunSealResult seal = await writer.SealRunAsync(); orchestrator.StopAdmission(run1);
        Assert.AreEqual(GateProcessingAdmissionStatus.Late, orchestrator.NotifyCommitted(new(identity, commit.Revision)));
        orchestrator.RequestFinalReconciliation(run1, seal.FinalRevision);
        await orchestrator.WaitForDrainAsync(run1).AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.AreEqual(1L, orchestrator.Diagnostics.BatchesAccepted);
        Assert.IsGreaterThanOrEqualTo(1L, orchestrator.Diagnostics.Saturations);
        Assert.IsLessThanOrEqualTo(orchestrator.Diagnostics.PeakSchedulerDepth,
            orchestrator.Diagnostics.SchedulerCapacity);
    }

    [TestMethod]
    public async Task Scenario19_LostProviderProgressIsRecoveredByFinalReconciliationPulse()
    {
        await using var fixture = new Fixture();
        InspectionAppendResult commit = await fixture.CommitAsync(0, [1, 2]);
        var provider = new ProvisionalProvider(fixture.Registry.Store); var sink = new CapturingSink();
        await using var orchestrator = new CommittedGateProcessingOrchestrator(fixture.Registry.Store, provider,
            sink, new GateEvaluator(new GateScratchPool()));
        orchestrator.OpenRun(new(fixture.Inspection, fixture.Run, [fixture.Stream], CompilePlan()));
        orchestrator.NotifyCommitted(new(fixture.Identity(0), commit.Revision));
        await provider.FirstRead.Task.WaitAsync(TimeSpan.FromSeconds(10));
        provider.MakeAvailable(); // availability notification is deliberately lost
        InspectionRunSealResult seal = await fixture.Writer.SealRunAsync(); fixture.MarkSealed();
        orchestrator.StopAdmission(fixture.Run); orchestrator.RequestFinalReconciliation(fixture.Run, seal.FinalRevision);
        await orchestrator.WaitForDrainAsync(fixture.Run).AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.HasCount(1, sink.Batches);
        Assert.IsTrue(orchestrator.Diagnostics.TerminalRuns.Single().Plans.Single().IsLosslessBalanced);
    }

    [TestMethod]
    public async Task Scenarios40_41_PlanCannotChangeAdmittedWorkAndNoTransitionGapIsInvented()
    {
        await using var fixture = new Fixture(); fixture.Sink.Suspend = true;
        InspectionAppendResult commit = await fixture.CommitAsync(0, [1, 2]);
        fixture.Orchestrator.NotifyCommitted(new(fixture.Identity(0), commit.Revision));
        await fixture.Sink.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.AreEqual(GateProcessingAdmissionStatus.Rejected,
            fixture.Orchestrator.ActivatePlan(fixture.Run, CompilePlan(2, "later"), commit.Revision));
        fixture.Sink.Release.TrySetResult(); await fixture.SealAndDrainAsync();
        GateProcessingRunDiagnostics terminal = fixture.Orchestrator.Diagnostics.TerminalRuns.Single();
        Assert.AreEqual(new GatePlanRevision(1), fixture.Sink.Batches.Single().Entries.Single().PlanRevision);
        Assert.AreEqual(0L, terminal.Plans.Single().ExplicitlyUnprocessedByPlanTransition);
        Assert.IsTrue(terminal.Plans.Single().IsLosslessBalanced);
    }

    [TestMethod]
    public async Task Scenarios42_67_68_69_71_MultiRunDiagnosticsAreSeparatedAndFailureIsIsolated()
    {
        var inspection = new InspectionId(Guid.NewGuid());
        await using var badRegistry = new InspectionDataStoreRegistry(inspection, new(4096, 10));
        await using var goodRegistry = new InspectionDataStoreRegistry(inspection, new(4096, 10));
        AcquisitionRunId badRun = new(Guid.NewGuid()), goodRun = new(Guid.NewGuid());
        await using IInspectionRunWriter badWriter = badRegistry.OpenRun(badRun);
        await using IInspectionRunWriter goodWriter = goodRegistry.OpenRun(goodRun);
        UtStreamIdentity badStream = new(inspection, badRun, new("multi"), new(0));
        UtStreamIdentity goodStreamA = new(inspection, goodRun, new("multi"), new(0));
        UtStreamIdentity goodStreamB = new(inspection, goodRun, new("multi"), new(1));
        var authority = new CompositeAuthority(badRun, badRegistry.Store, goodRun, goodRegistry.Store);
        var provider = new SelectiveFailureProvider(authority, badRun); var sink = new CapturingSink();
        await using var orchestrator = new CommittedGateProcessingOrchestrator(authority, provider, sink,
            new GateEvaluator(new GateScratchPool()), new(maximumRuns: 2));
        CompiledGatePlan badPlan = CompilePlan(1, "bad"); CompiledGatePlan goodPlan = CompilePlan(2, "good");
        orchestrator.OpenRun(new(inspection, badRun, [badStream], badPlan));
        orchestrator.OpenRun(new(inspection, goodRun, [goodStreamA, goodStreamB], goodPlan));
        InspectionAppendResult bad = await badWriter.CommitAsync(new(new(Descriptor(new(badStream, 0), 0, 2), new short[] { 1, 2 })));
        InspectionAppendResult goodA = await goodWriter.CommitAsync(new(new(Descriptor(new(goodStreamA, 0), 0, 2), new short[] { 2, 3 })));
        InspectionAppendResult goodB = await goodWriter.CommitAsync(new(new(Descriptor(new(goodStreamB, 1), 1, 2), new short[] { 3, 4 })));
        orchestrator.NotifyCommitted(new(new(badStream, 0), bad.Revision));
        orchestrator.NotifyCommitted(new(new(goodStreamA, 0), goodA.Revision));
        orchestrator.NotifyCommitted(new(new(goodStreamB, 1), goodB.Revision));
        InspectionRunSealResult badSeal = await badWriter.SealRunAsync();
        InspectionRunSealResult goodSeal = await goodWriter.SealRunAsync();
        orchestrator.StopAdmission(badRun); orchestrator.RequestFinalReconciliation(badRun, badSeal.FinalRevision);
        orchestrator.StopAdmission(goodRun); orchestrator.RequestFinalReconciliation(goodRun, goodSeal.FinalRevision);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            orchestrator.WaitForDrainAsync(badRun).AsTask().WaitAsync(TimeSpan.FromSeconds(10)));
        await orchestrator.WaitForDrainAsync(goodRun).AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        GateProcessingRunDiagnostics[] terminal = orchestrator.Diagnostics.TerminalRuns.OrderBy(x => x.RunId.ToString()).ToArray();
        Assert.HasCount(2, terminal);
        GateProcessingRunDiagnostics failed = terminal.Single(x => x.RunId == badRun);
        GateProcessingRunDiagnostics completed = terminal.Single(x => x.RunId == goodRun);
        Assert.AreEqual(GateProcessingLifecycleState.Faulted, failed.Lifecycle);
        Assert.AreEqual(GateProcessingLifecycleState.Stopped, completed.Lifecycle);
        Assert.AreEqual(badPlan.Signature, failed.PlanSignature);
        Assert.AreEqual(goodPlan.Signature, completed.PlanSignature);
        Assert.HasCount(2, completed.Watermarks);
        Assert.AreEqual(2L, completed.WorkTerminal);
        Assert.HasCount(2, sink.Batches);
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task Scenarios57_58_59_StopDuringCatalogOrProviderReadWaitsAndDrains(bool blockCatalog)
    {
        await using var fixture = new Fixture();
        InspectionAppendResult commit = await fixture.CommitAsync(0, [1, 2]);
        var catalogGate = new BlockingCatalog(fixture.Registry.Store) { Block = blockCatalog };
        var providerGate = new BlockingProvider(fixture.Registry.Store) { Block = !blockCatalog };
        var sink = new CapturingSink();
        await using var orchestrator = new CommittedGateProcessingOrchestrator(catalogGate, providerGate, sink,
            new GateEvaluator(new GateScratchPool()));
        orchestrator.OpenRun(new(fixture.Inspection, fixture.Run, [fixture.Stream], CompilePlan()));
        orchestrator.NotifyCommitted(new(fixture.Identity(0), commit.Revision));
        TaskCompletionSource entered = blockCatalog ? catalogGate.Entered : providerGate.Entered;
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        InspectionRunSealResult seal = await fixture.Writer.SealRunAsync(); fixture.MarkSealed();
        orchestrator.StopAdmission(fixture.Run); orchestrator.RequestFinalReconciliation(fixture.Run, seal.FinalRevision);
        Task drain = orchestrator.WaitForDrainAsync(fixture.Run).AsTask(); Assert.IsFalse(drain.IsCompleted);
        if (blockCatalog) { catalogGate.Release.TrySetResult(); } else { providerGate.Release.TrySetResult(); }
        await drain.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.HasCount(1, sink.Batches);
        Assert.AreEqual(0, orchestrator.Diagnostics.ActiveWorkers);
        Assert.AreEqual(orchestrator.Diagnostics.LeasesAcquired, orchestrator.Diagnostics.LeasesReleased);
    }

    [TestMethod]
    public async Task Scenario30_MismatchedProviderRevisionIsTerminalFailureAndLeaseIsReleasedOnce()
    {
        await using var fixture = new Fixture();
        InspectionAppendResult commit = await fixture.CommitAsync(0, [1, 2]);
        var provider = new MismatchedRevisionProvider(fixture.Registry.Store); var sink = new CapturingSink();
        await using var orchestrator = new CommittedGateProcessingOrchestrator(fixture.Registry.Store, provider,
            sink, new GateEvaluator(new GateScratchPool()));
        orchestrator.OpenRun(new(fixture.Inspection, fixture.Run, [fixture.Stream], CompilePlan()));
        orchestrator.NotifyCommitted(new(fixture.Identity(0), commit.Revision));
        InspectionRunSealResult seal = await fixture.Writer.SealRunAsync(); fixture.MarkSealed();
        orchestrator.StopAdmission(fixture.Run); orchestrator.RequestFinalReconciliation(fixture.Run, seal.FinalRevision);
        await orchestrator.WaitForDrainAsync(fixture.Run).AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.AreEqual(1, provider.LeaseDisposals);
        Assert.AreEqual(1L, orchestrator.Diagnostics.LeasesAcquired);
        Assert.AreEqual(1L, orchestrator.Diagnostics.LeasesReleased);
        Assert.IsEmpty(sink.Batches);
        GateProcessingRunDiagnostics terminal = orchestrator.Diagnostics.TerminalRuns.Single();
        Assert.AreEqual(1L, terminal.Plans.Single().TerminallyFailed);
        Assert.IsTrue(terminal.Plans.Single().IsLosslessBalanced);
    }

    [TestMethod]
    public async Task Scenario53_PrimarySinkErrorAndSecondaryLeaseCleanupErrorRemainBalanced()
    {
        await using var fixture = new Fixture();
        InspectionAppendResult commit = await fixture.CommitAsync(0, [1, 2]);
        var provider = new ThrowingCleanupProvider(fixture.Registry.Store);
        var sink = new CapturingSink { OnDeliver = _ => throw new FormatException("primary") };
        await using var orchestrator = new CommittedGateProcessingOrchestrator(fixture.Registry.Store, provider,
            sink, new GateEvaluator(new GateScratchPool()));
        orchestrator.OpenRun(new(fixture.Inspection, fixture.Run, [fixture.Stream], CompilePlan()));
        orchestrator.NotifyCommitted(new(fixture.Identity(0), commit.Revision));
        InspectionRunSealResult seal = await fixture.Writer.SealRunAsync(); fixture.MarkSealed();
        orchestrator.StopAdmission(fixture.Run); orchestrator.RequestFinalReconciliation(fixture.Run, seal.FinalRevision);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            orchestrator.WaitForDrainAsync(fixture.Run).AsTask().WaitAsync(TimeSpan.FromSeconds(10)));
        GateProcessingDiagnostics diagnostics = orchestrator.Diagnostics;
        Assert.AreEqual("FormatException", diagnostics.PrimaryError!.Message);
        Assert.IsTrue(diagnostics.SecondaryErrors.Any(x => x.Message == "InvalidOperationException"));
        Assert.AreEqual(diagnostics.LeasesAcquired, diagnostics.LeasesReleased);
        Assert.AreEqual(0, diagnostics.ActiveLeases);
    }

    [TestMethod]
    public async Task Scenario39_PlanChangeWhileProviderIsSuspendedPinsOldAndUsesNewPlanNext()
    {
        await using var fixture = new Fixture();
        InspectionAppendResult first = await fixture.CommitAsync(0, [1, 2]);
        var provider = new ProvisionalProvider(fixture.Registry.Store); var sink = new CapturingSink();
        await using var orchestrator = new CommittedGateProcessingOrchestrator(fixture.Registry.Store, provider,
            sink, new GateEvaluator(new GateScratchPool()));
        orchestrator.OpenRun(new(fixture.Inspection, fixture.Run, [fixture.Stream], CompilePlan(1, "old")));
        orchestrator.NotifyCommitted(new(fixture.Identity(0), first.Revision));
        await provider.FirstRead.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.AreEqual(GateProcessingAdmissionStatus.Accepted,
            orchestrator.ActivatePlan(fixture.Run, CompilePlan(2, "new"), new(first.Revision.Value + 1)));
        Assert.AreEqual(new GatePlanRevision(1), orchestrator.Diagnostics.ActiveRuns.Single().PlanRevision);
        provider.MakeAvailable(); orchestrator.NotifyProviderProgress(fixture.Run);
        InspectionAppendResult second = await fixture.CommitAsync(1, [2, 3]);
        orchestrator.NotifyCommitted(new(fixture.Identity(1), second.Revision));
        InspectionRunSealResult seal = await fixture.Writer.SealRunAsync(); fixture.MarkSealed();
        orchestrator.StopAdmission(fixture.Run); orchestrator.RequestFinalReconciliation(fixture.Run, seal.FinalRevision);
        await orchestrator.WaitForDrainAsync(fixture.Run).AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.HasCount(2, sink.Batches);
        Assert.AreEqual(1L, Convert.ToInt64(sink.Batches[0].Entries.Single().PlanRevision.Value));
        Assert.AreEqual(2L, Convert.ToInt64(sink.Batches[1].Entries.Single().PlanRevision.Value));
    }

    [TestMethod]
    public async Task Scenarios54_63_BorrowedProductSinkRetainsNothingAndDisposeDuringRunningBalances()
    {
        Assert.IsFalse(typeof(TransientGateEvaluationBatchSink).GetFields(
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public |
            System.Reflection.BindingFlags.NonPublic).Any(x => typeof(GateEvaluationBatch).IsAssignableFrom(x.FieldType)));
        await using var fixture = new Fixture();
        InspectionAppendResult commit = await fixture.CommitAsync(0, [1, 2]);
        var provider = new BlockingProvider(fixture.Registry.Store) { Block = true };
        var orchestrator = new CommittedGateProcessingOrchestrator(fixture.Registry.Store, provider,
            new TransientGateEvaluationBatchSink(), new GateEvaluator(new GateScratchPool()));
        orchestrator.OpenRun(new(fixture.Inspection, fixture.Run, [fixture.Stream], CompilePlan()));
        orchestrator.NotifyCommitted(new(fixture.Identity(0), commit.Revision));
        await provider.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await orchestrator.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.AreEqual(GateProcessingLifecycleState.Closed, orchestrator.Diagnostics.State);
        Assert.AreEqual(0, orchestrator.Diagnostics.ActiveWorkers);
        Assert.AreEqual(0, orchestrator.Diagnostics.ActiveCallbacks);
        Assert.AreEqual(0, orchestrator.Diagnostics.ActiveLeases);
        _ = await fixture.Writer.SealRunAsync(); fixture.MarkSealed();
    }

    [TestMethod]
    public async Task DiagnosticsSnapshotsAreConcurrentNonhybridAndTerminalHistoryIsBounded()
    {
        var inspection = new InspectionId(Guid.NewGuid());
        await using var registry = new InspectionDataStoreRegistry(inspection, new(4096, 12));
        await using var orchestrator = new CommittedGateProcessingOrchestrator(registry.Store, registry.Store,
            new CapturingSink(), new GateEvaluator(new GateScratchPool()),
            new(maximumRuns: 1, maximumTerminalRunSummaries: 2));
        for (int number = 1; number <= 3; number++)
        {
            var run = new AcquisitionRunId(Guid.NewGuid()); var source = new UtSourceId("diagnostics");
            var stream = new UtStreamIdentity(inspection, run, source, new(0));
            await using IInspectionRunWriter writer = registry.OpenRun(run);
            orchestrator.OpenRun(new(inspection, run, [stream], CompilePlan((uint)number, $"plan-{number}")));
            GateProcessingRunDiagnostics[] snapshots = await Task.WhenAll(Enumerable.Range(0, 32)
                .Select(_ => Task.Run(() => orchestrator.Diagnostics.ActiveRuns.Single())));
            Assert.IsTrue(snapshots.All(x => x.RunId == run && x.PlanRevision.Value == (uint)number));
            InspectionRunSealResult seal = await writer.SealRunAsync();
            orchestrator.StopAdmission(run); orchestrator.RequestFinalReconciliation(run, seal.FinalRevision);
            await orchestrator.WaitForDrainAsync(run);
        }
        Assert.HasCount(2, orchestrator.Diagnostics.TerminalRuns);
        Assert.IsEmpty(orchestrator.Diagnostics.ActiveRuns);
    }

    [TestMethod]
    public async Task InvalidTraceProducesTerminalBatchWithoutReadingSamples()
    {
        await using var fixture = new Fixture();
        InspectionAppendResult commit = await fixture.CommitAsync(0, [1, 2], UtTraceQuality.Invalid);
        var sink = new CapturingSink();
        await using var orchestrator = new CommittedGateProcessingOrchestrator(fixture.Registry.Store,
            new SamplesForbiddenProvider(fixture.Registry.Store), sink, new GateEvaluator(new GateScratchPool()));
        orchestrator.OpenRun(new(fixture.Inspection, fixture.Run, [fixture.Stream], CompilePlan()));
        orchestrator.NotifyCommitted(new(fixture.Identity(0), commit.Revision));
        InspectionRunSealResult seal = await fixture.Writer.SealRunAsync(); fixture.MarkSealed();
        orchestrator.StopAdmission(fixture.Run); orchestrator.RequestFinalReconciliation(fixture.Run, seal.FinalRevision);
        await orchestrator.WaitForDrainAsync(fixture.Run);
        Assert.AreEqual(GateResultStatus.InvalidTrace, sink.Batches.Single().Entries.Single().Status);
    }

    [TestMethod]
    public async Task FinalRevisionFreezesAdmissionAndFailedRunRetiresOnlyAfterCleanup()
    {
        await using var fixture = new Fixture();
        fixture.Orchestrator.RequestFinalReconciliation(fixture.Run, InspectionRevision.Initial);
        fixture.Orchestrator.RequestFinalReconciliation(fixture.Run, InspectionRevision.Initial);
        Assert.ThrowsExactly<InvalidOperationException>(() =>
            fixture.Orchestrator.RequestFinalReconciliation(fixture.Run, new(1)));
        Assert.AreEqual(GateProcessingAdmissionStatus.Late,
            fixture.Orchestrator.NotifyCommitted(new(fixture.Identity(0), new(1))));
        fixture.Orchestrator.StopAdmission(fixture.Run);
        await fixture.Orchestrator.WaitForDrainAsync(fixture.Run);
        _ = await fixture.Writer.SealRunAsync(); fixture.MarkSealed();

        GateProcessingRunDiagnostics terminal = fixture.Orchestrator.Diagnostics.TerminalRuns.Single();
        Assert.AreEqual(InspectionRevision.Initial, terminal.FinalRevision);
        Assert.AreEqual(0, terminal.ActiveWorkers);
        Assert.AreEqual(0, terminal.ActiveCallbacks);
        Assert.AreEqual(0, terminal.ActiveLeases);
    }

    [TestMethod]
    public async Task DisposeAfterProvisionalNotAvailableTerminalizesPendingWork()
    {
        await using var fixture = new Fixture();
        InspectionAppendResult commit = await fixture.CommitAsync(0, [1, 2]);
        var provider = new ProvisionalProvider(fixture.Registry.Store);
        var orchestrator = new CommittedGateProcessingOrchestrator(fixture.Registry.Store, provider,
            new CapturingSink(), new GateEvaluator(new GateScratchPool()));
        orchestrator.OpenRun(new(fixture.Inspection, fixture.Run, [fixture.Stream], CompilePlan()));
        orchestrator.NotifyCommitted(new(fixture.Identity(0), commit.Revision));
        await provider.FirstRead.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await orchestrator.DisposeAsync();
        GateProcessingRunDiagnostics failed = orchestrator.Diagnostics.ActiveRuns.Single();
        Assert.AreEqual(0L, failed.WorkPending);
        Assert.AreEqual(1L, failed.Plans.Single().TerminallyFailed);
        Assert.IsTrue(failed.Plans.Single().IsLosslessBalanced);
        _ = await fixture.Writer.SealRunAsync(); fixture.MarkSealed();
    }

    [TestMethod]
    public async Task Scenario44_DisposeBeforeProviderReadAcquiresNoLease()
    {
        await using var fixture = new Fixture();
        InspectionAppendResult commit = await fixture.CommitAsync(0, [1, 2]);
        var catalog = new BlockingCatalog(fixture.Registry.Store) { Block = true };
        var orchestrator = new CommittedGateProcessingOrchestrator(catalog, fixture.Registry.Store,
            new CapturingSink(), new GateEvaluator(new GateScratchPool()));
        orchestrator.OpenRun(new(fixture.Inspection, fixture.Run, [fixture.Stream], CompilePlan()));
        orchestrator.NotifyCommitted(new(fixture.Identity(0), commit.Revision));
        await catalog.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await orchestrator.DisposeAsync();
        Assert.AreEqual(0L, orchestrator.Diagnostics.LeasesAcquired);
        Assert.AreEqual(0, orchestrator.Diagnostics.ActiveLeases);
        _ = await fixture.Writer.SealRunAsync(); fixture.MarkSealed();
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task Scenarios45_60_DisposeOrStopDuringEvaluationWaitsForLeaseAndScratch(bool exceptionalDispose)
    {
        await using var fixture = new Fixture();
        InspectionAppendResult commit = await fixture.CommitAsync(0, [1, 2]);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pool = new GateScratchPool();
        var evaluator = new GateEvaluator(pool, checkpoint =>
        {
            if (checkpoint == GateEvaluationCheckpoint.AfterScratchRent)
            { entered.TrySetResult(); release.Task.GetAwaiter().GetResult(); }
        });
        var orchestrator = new CommittedGateProcessingOrchestrator(fixture.Registry.Store, fixture.Registry.Store,
            new CapturingSink(), evaluator);
        orchestrator.OpenRun(new(fixture.Inspection, fixture.Run, [fixture.Stream], CompilePlan()));
        orchestrator.NotifyCommitted(new(fixture.Identity(0), commit.Revision));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Task completion;
        if (exceptionalDispose) { completion = orchestrator.DisposeAsync().AsTask(); }
        else
        {
            InspectionRunSealResult seal = await fixture.Writer.SealRunAsync(); fixture.MarkSealed();
            orchestrator.StopAdmission(fixture.Run); orchestrator.RequestFinalReconciliation(fixture.Run, seal.FinalRevision);
            completion = orchestrator.WaitForDrainAsync(fixture.Run).AsTask();
        }
        Assert.IsFalse(completion.IsCompleted); release.TrySetResult();
        if (exceptionalDispose) { await completion.WaitAsync(TimeSpan.FromSeconds(10)); }
        else { await completion.WaitAsync(TimeSpan.FromSeconds(10)); await orchestrator.DisposeAsync(); }
        Assert.AreEqual(orchestrator.Diagnostics.LeasesAcquired, orchestrator.Diagnostics.LeasesReleased);
        Assert.AreEqual(0, orchestrator.Diagnostics.ActiveLeases);
        Assert.AreEqual(0, pool.Snapshot.ActiveRentals);
        GateProcessingRunDiagnostics balance = orchestrator.Diagnostics.ActiveRuns.SingleOrDefault()
            ?? orchestrator.Diagnostics.TerminalRuns.Single();
        Assert.AreEqual(balance.WorkAdmitted, balance.WorkTerminal);
        Assert.IsTrue(balance.Plans.All(x => x.IsLosslessBalanced));
        if (!fixture.IsSealed) { _ = await fixture.Writer.SealRunAsync(); fixture.MarkSealed(); }
    }

    [TestMethod]
    public async Task Scenario70_TwoOrchestratorsShareOneBoundedScratchPoolAndBalanceIt()
    {
        var pool = new GateScratchPool(2);
        CompiledGatePlan plan = GateEvaluationTestData.Compile(GateEvaluationTestData.Gate("A", 3,
            detector: GateDetectorKind.FirstLocalMaximum, local: new(1, 0, 1)));
        async Task ExecuteAsync(int channel)
        {
            var inspection = new InspectionId(Guid.NewGuid()); var run = new AcquisitionRunId(Guid.NewGuid());
            await using var registry = new InspectionDataStoreRegistry(inspection, new(1024, 4));
            await using IInspectionRunWriter writer = registry.OpenRun(run);
            var stream = new UtStreamIdentity(inspection, run, new("shared-scratch"), new(channel));
            var identity = new TraceIdentity(stream, 0);
            var descriptor = new TraceDescriptor(identity, 0, TimeSpan.Zero, null, null, 3, 1_000_000, 0,
                UtSignalMode.Rf, sampleEncoding: UtSampleEncoding.SignedInt16,
                provenance: UtTraceProvenance.Simulator, traceQuality: UtTraceQuality.Good);
            InspectionAppendResult commit = await writer.CommitAsync(new(new(descriptor, new short[] { 0, 2, 0 })));
            await using var orchestrator = new CommittedGateProcessingOrchestrator(registry.Store, registry.Store,
                new CapturingSink(), new GateEvaluator(pool));
            orchestrator.OpenRun(new(inspection, run, [stream], plan));
            orchestrator.NotifyCommitted(new(identity, commit.Revision));
            InspectionRunSealResult seal = await writer.SealRunAsync();
            orchestrator.StopAdmission(run); orchestrator.RequestFinalReconciliation(run, seal.FinalRevision);
            await orchestrator.WaitForDrainAsync(run);
        }
        await Task.WhenAll(ExecuteAsync(0), ExecuteAsync(1));
        Assert.AreEqual(2L, pool.Snapshot.RentCount);
        Assert.AreEqual(pool.Snapshot.RentCount, pool.Snapshot.ReturnCount);
        Assert.AreEqual(0, pool.Snapshot.ActiveRentals);
    }

    [TestMethod]
    public async Task Scenario47_EvaluationExceptionIsTerminalAndReleasesLeaseAndScratch()
    {
        await using var fixture = new Fixture();
        InspectionAppendResult commit = await fixture.CommitAsync(0, [0, 2, 0]);
        var pool = new GateScratchPool();
        var evaluator = new GateEvaluator(pool, checkpoint =>
        { if (checkpoint == GateEvaluationCheckpoint.AfterScratchRent) { throw new FormatException("evaluation"); } });
        await using var orchestrator = new CommittedGateProcessingOrchestrator(fixture.Registry.Store,
            fixture.Registry.Store, new CapturingSink(), evaluator);
        orchestrator.OpenRun(new(fixture.Inspection, fixture.Run, [fixture.Stream], CompilePlan()));
        orchestrator.NotifyCommitted(new(fixture.Identity(0), commit.Revision));
        InspectionRunSealResult seal = await fixture.Writer.SealRunAsync(); fixture.MarkSealed();
        orchestrator.StopAdmission(fixture.Run); orchestrator.RequestFinalReconciliation(fixture.Run, seal.FinalRevision);
        await orchestrator.WaitForDrainAsync(fixture.Run);
        GateProcessingRunDiagnostics terminal = orchestrator.Diagnostics.TerminalRuns.Single();
        Assert.AreEqual(1L, terminal.Plans.Single().TerminallyFailed);
        Assert.IsTrue(terminal.Plans.Single().IsLosslessBalanced);
        Assert.AreEqual(orchestrator.Diagnostics.LeasesAcquired, orchestrator.Diagnostics.LeasesReleased);
        Assert.AreEqual(0, pool.Snapshot.ActiveRentals);
    }

    [TestMethod]
    public async Task DisposeDuringCallbackThatIgnoresCancellationHasOneTerminalOutcome()
    {
        await using var fixture = new Fixture();
        InspectionAppendResult commit = await fixture.CommitAsync(0, [1, 2]);
        var sink = new IgnoringCancellationSink();
        var orchestrator = new CommittedGateProcessingOrchestrator(fixture.Registry.Store,
            fixture.Registry.Store, sink, new GateEvaluator(new GateScratchPool()));
        orchestrator.OpenRun(new(fixture.Inspection, fixture.Run, [fixture.Stream], CompilePlan()));
        orchestrator.NotifyCommitted(new(fixture.Identity(0), commit.Revision));
        await sink.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Task dispose = orchestrator.DisposeAsync().AsTask(); Assert.IsFalse(dispose.IsCompleted);
        sink.Release.TrySetResult(); await dispose.WaitAsync(TimeSpan.FromSeconds(10));
        GateProcessingRunDiagnostics failed = orchestrator.Diagnostics.ActiveRuns.Single();
        Assert.AreEqual(1L, failed.WorkAdmitted); Assert.AreEqual(1L, failed.WorkTerminal);
        Assert.AreEqual(1L, failed.Plans.Single().TerminallyFailed);
        Assert.AreEqual(0L, failed.Plans.Single().EvaluatedAndDelivered);
        Assert.IsTrue(failed.Plans.Single().IsLosslessBalanced);
        _ = await fixture.Writer.SealRunAsync(); fixture.MarkSealed();
    }

    private sealed class Fixture : IAsyncDisposable
    {
        internal readonly InspectionId Inspection = new(Guid.NewGuid());
        internal readonly AcquisitionRunId Run = new(Guid.NewGuid());
        internal readonly UtSourceId Source = new("processing-test");
        internal readonly CapturingSink Sink = new();
        internal readonly InspectionDataStoreRegistry Registry;
        internal readonly IInspectionRunWriter Writer;
        internal readonly CommittedGateProcessingOrchestrator Orchestrator;
        private bool _sealed;

        internal Fixture(GateProcessingOptions? options = null)
        {
            Registry = new(Inspection, new InMemoryInspectionStoreOptions(1024 * 1024, 100));
            Writer = Registry.OpenRun(Run);
            Orchestrator = new(Registry.Store, Registry.Store, Sink, new GateEvaluator(new GateScratchPool()), options);
            Assert.AreEqual(GateProcessingAdmissionStatus.Accepted,
                Orchestrator.OpenRun(new(Inspection, Run, [Stream], CompilePlan())));
        }

        internal UtStreamIdentity Stream => new(Inspection, Run, Source, new PhysicalChannelId(0));
        internal TraceIdentity Identity(ulong index) => new(Stream, index);

        internal ValueTask<InspectionAppendResult> CommitAsync(ulong index, short[] samples,
            UtTraceQuality quality = UtTraceQuality.Good)
        {
            var descriptor = new TraceDescriptor(Identity(index), index, TimeSpan.FromMilliseconds(index), null,
                null, samples.Length, 1_000_000, 0, UtSignalMode.Rf, sampleEncoding: UtSampleEncoding.SignedInt16,
                provenance: UtTraceProvenance.Simulator, traceQuality: quality);
            return Writer.CommitAsync(new(new(descriptor, samples)));
        }

        internal async Task SealAndDrainAsync()
        {
            if (_sealed) { return; }
            InspectionRunSealResult seal = await Writer.SealRunAsync();
            Orchestrator.StopAdmission(Run);
            Orchestrator.RequestFinalReconciliation(Run, seal.FinalRevision);
            await Orchestrator.WaitForDrainAsync(Run).AsTask().WaitAsync(TimeSpan.FromSeconds(10));
            _sealed = true;
        }

        internal void MarkSealed() => _sealed = true;
        internal bool IsSealed => _sealed;

        public async ValueTask DisposeAsync()
        {
            if (!_sealed) { await SealAndDrainAsync(); }
            await Orchestrator.DisposeAsync();
            await Writer.DisposeAsync();
            await Registry.DisposeAsync();
        }

    }

    private static TraceDescriptor Descriptor(TraceIdentity identity, ulong sequence, int samples) =>
        new(identity, sequence, TimeSpan.FromTicks((long)sequence), null, null, samples, 1_000_000, 0,
            UtSignalMode.Rf, sampleEncoding: UtSampleEncoding.SignedInt16,
            provenance: UtTraceProvenance.Simulator, traceQuality: UtTraceQuality.Good);

    private static CompiledGatePlan CompilePlan(uint revision = 1, string id = "plan")
    {
        GatePlan plan = GateTestPlan.Plan([GateTestPlan.Gate("A", durationSeconds: 0.000001)], id, revision);
        return new GatePlanCompiler().Compile(plan).CompiledPlan!;
    }

    private sealed class ProvisionalProvider(IUtTraceProvider inner) : IUtTraceProvider
    {
        private volatile bool _available;
        private int _reads;
        internal readonly TaskCompletionSource FirstRead = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int ReadCount => Volatile.Read(ref _reads);
        internal void MakeAvailable() => _available = true;
        public ValueTask<InspectionTraceReadResult> ReadAsync(TraceIdentity identity,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _reads); FirstRead.TrySetResult();
            return _available ? inner.ReadAsync(identity, cancellationToken) :
                ValueTask.FromResult(InspectionTraceReadResult.Unavailable(InspectionReadStatus.NotAvailable,
                    InspectionRevision.Initial));
        }
    }

    private sealed class TerminalProvider(InspectionReadStatus status) : IUtTraceProvider
    {
        public ValueTask<InspectionTraceReadResult> ReadAsync(TraceIdentity identity,
            CancellationToken cancellationToken = default) => ValueTask.FromResult(
                InspectionTraceReadResult.Unavailable(status, new(1),
                    status == InspectionReadStatus.Faulted ? "synthetic" : null));
    }

    private sealed class ThrowingProvider : IUtTraceProvider
    {
        public ValueTask<InspectionTraceReadResult> ReadAsync(TraceIdentity identity,
            CancellationToken cancellationToken = default) => throw new InvalidOperationException("provider");
    }

    private sealed class ThrowingCatalog : IInspectionChangeProvider
    {
        public ValueTask<InspectionChangePage> ReadChangesAsync(InspectionChangeQuery query,
            CancellationToken cancellationToken = default) => throw new InvalidOperationException("catalog");
    }

    private sealed class SelectiveFailureProvider(IUtTraceProvider inner, AcquisitionRunId failedRun) : IUtTraceProvider
    {
        public ValueTask<InspectionTraceReadResult> ReadAsync(TraceIdentity identity,
            CancellationToken cancellationToken = default) => identity.Stream.AcquisitionRunId == failedRun
                ? ValueTask.FromResult(InspectionTraceReadResult.Unavailable(InspectionReadStatus.Faulted,
                    new InspectionRevision(1), "selected-run"))
                : inner.ReadAsync(identity, cancellationToken);
    }

    private sealed class CompositeAuthority(AcquisitionRunId firstRun, IInspectionDataStore first,
        AcquisitionRunId secondRun, IInspectionDataStore second) : IInspectionChangeProvider, IUtTraceProvider
    {
        public ValueTask<InspectionChangePage> ReadChangesAsync(InspectionChangeQuery query,
            CancellationToken cancellationToken = default) => query.RunId == firstRun
                ? first.ReadChangesAsync(query, cancellationToken)
                : query.RunId == secondRun ? second.ReadChangesAsync(query, cancellationToken)
                : throw new InvalidOperationException("unknown run");
        public ValueTask<InspectionTraceReadResult> ReadAsync(TraceIdentity identity,
            CancellationToken cancellationToken = default) => identity.Stream.AcquisitionRunId == firstRun
                ? first.ReadAsync(identity, cancellationToken)
                : identity.Stream.AcquisitionRunId == secondRun ? second.ReadAsync(identity, cancellationToken)
                : throw new InvalidOperationException("unknown run");
    }

    private sealed class BlockingCatalog(IInspectionChangeProvider inner) : IInspectionChangeProvider
    {
        internal bool Block;
        internal readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async ValueTask<InspectionChangePage> ReadChangesAsync(InspectionChangeQuery query,
            CancellationToken cancellationToken = default)
        {
            Entered.TrySetResult(); if (Block) { await Release.Task.WaitAsync(cancellationToken); }
            return await inner.ReadChangesAsync(query, cancellationToken);
        }
    }

    private sealed class BlockingProvider(IUtTraceProvider inner) : IUtTraceProvider
    {
        internal bool Block;
        internal readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async ValueTask<InspectionTraceReadResult> ReadAsync(TraceIdentity identity,
            CancellationToken cancellationToken = default)
        {
            Entered.TrySetResult(); if (Block) { await Release.Task.WaitAsync(cancellationToken); }
            return await inner.ReadAsync(identity, cancellationToken);
        }
    }

    private sealed class MismatchedRevisionProvider(IUtTraceProvider inner) : IUtTraceProvider
    {
        internal int LeaseDisposals;
        public async ValueTask<InspectionTraceReadResult> ReadAsync(TraceIdentity identity,
            CancellationToken cancellationToken = default)
        {
            InspectionTraceReadResult actual = await inner.ReadAsync(identity, cancellationToken);
            return InspectionTraceReadResult.Available(new(actual.Revision.Value + 1),
                new TrackingLease(actual.Lease!, () => Interlocked.Increment(ref LeaseDisposals)));
        }
    }

    private sealed class ThrowingCleanupProvider(IUtTraceProvider inner) : IUtTraceProvider
    {
        public async ValueTask<InspectionTraceReadResult> ReadAsync(TraceIdentity identity,
            CancellationToken cancellationToken = default)
        {
            InspectionTraceReadResult actual = await inner.ReadAsync(identity, cancellationToken);
            return InspectionTraceReadResult.Available(actual.Revision,
                new TrackingLease(actual.Lease!, () => throw new InvalidOperationException("cleanup")));
        }
    }

    private sealed class SamplesForbiddenProvider(IUtTraceProvider inner) : IUtTraceProvider
    {
        public async ValueTask<InspectionTraceReadResult> ReadAsync(TraceIdentity identity,
            CancellationToken cancellationToken = default)
        {
            InspectionTraceReadResult actual = await inner.ReadAsync(identity, cancellationToken);
            return InspectionTraceReadResult.Available(actual.Revision, new SamplesForbiddenLease(actual.Lease!));
        }
    }

    private sealed class SamplesForbiddenLease(IInspectionTraceLease inner) : IInspectionTraceLease
    {
        public TraceDescriptor Descriptor => inner.Descriptor;
        public IReadOnlyUtSamples Samples => throw new InvalidOperationException("Invalid samples must not be read.");
        public void Dispose() => inner.Dispose();
    }

    private sealed class TrackingLease(IInspectionTraceLease inner, Action disposed) : IInspectionTraceLease
    {
        private int _disposed;
        public TraceDescriptor Descriptor => inner.Descriptor;
        public IReadOnlyUtSamples Samples => inner.Samples;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) { throw new InvalidOperationException("double dispose"); }
            try { inner.Dispose(); } finally { disposed(); }
        }
    }

    private sealed class CapturingSink : IGateEvaluationBatchSink
    {
        internal readonly List<GateEvaluationBatch> Batches = [];
        internal readonly TaskCompletionSource<GateEvaluationBatch> Next =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal GateBatchDeliveryStatus Status = GateBatchDeliveryStatus.Accepted;
        internal bool Suspend;
        internal Action<GateEvaluationBatch>? OnDeliver;

        public async ValueTask<GateBatchDeliveryResult> DeliverAsync(GateEvaluationBatch batch,
            CancellationToken exceptionalCancellation = default)
        {
            lock (Batches) { Batches.Add(batch); }
            Next.TrySetResult(batch); Entered.TrySetResult(); OnDeliver?.Invoke(batch);
            if (Suspend) { await Release.Task.WaitAsync(exceptionalCancellation); }
            return new(Status);
        }
    }

    private sealed class IgnoringCancellationSink : IGateEvaluationBatchSink
    {
        internal readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async ValueTask<GateBatchDeliveryResult> DeliverAsync(GateEvaluationBatch batch,
            CancellationToken exceptionalCancellation = default)
        { Entered.TrySetResult(); await Release.Task; return GateBatchDeliveryResult.Accepted(); }
    }
}
