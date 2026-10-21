using UTStudio.Contracts.Inspection;
using UTStudio.Contracts.Processing;
using UTStudio.Domain.Acquisition;
using UTStudio.Domain.Inspection;
using UTStudio.Domain.Processing;
using UTStudio.Processing.Core;

namespace UTStudio.Core.Tests.Processing;

[TestClass]
public sealed class InMemoryGateDerivedStorePreparedReplacementTests
{
    [TestMethod]
    public async Task PrepareIsInvisibleAndSecondPrepareConsumesNothing()
    {
        await using var store = Store();
        DerivedStoreRevision revision = store.Revision;
        GateDerivedStoreDiagnostics historical = store.Diagnostics;
        PrepareDesiredReplacementResult first = store.DesiredReplacementControl.Prepare(Request("A"));
        DesiredReplacementControlDiagnostics prepared = store.DesiredReplacementControl.Diagnostics;

        PrepareDesiredReplacementResult same = store.DesiredReplacementControl.Prepare(Request("A"));
        PrepareDesiredReplacementResult other = store.DesiredReplacementControl.Prepare(Request("B"));

        Assert.AreEqual(PrepareDesiredReplacementStatus.Prepared, first.Status);
        Assert.AreEqual(PrepareDesiredReplacementStatus.AlreadyPrepared, same.Status);
        Assert.AreEqual(PrepareDesiredReplacementStatus.AlreadyPrepared, other.Status);
        Assert.IsNull(store.ActiveView);
        Assert.AreEqual(revision, store.Revision);
        Assert.AreEqual(historical.LogicalPayloadBytes, store.Diagnostics.LogicalPayloadBytes);
        Assert.AreEqual(historical.BatchCount, store.Diagnostics.BatchCount);
        Assert.IsTrue(prepared.HasPreparedReservation);
        Assert.AreEqual(prepared, store.DesiredReplacementControl.Diagnostics);
        Assert.IsNull(store.DesiredReplacementControl.FindInstalled(first.Prepared.Key));
    }

    [TestMethod]
    public async Task AbortReusesTentativeReplacementValuesButNotOperationGeneration()
    {
        await using var store = Store();
        PrepareDesiredReplacementResult first = store.DesiredReplacementControl.Prepare(Request("A"));
        Assert.AreEqual(GatePreparedReservationResultStatus.Aborted,
            Abort(store, first.Prepared).Status);

        PrepareDesiredReplacementResult second = store.DesiredReplacementControl.Prepare(Request("A"));

        Assert.AreEqual(first.Prepared.TentativeToken, second.Prepared.TentativeToken);
        Assert.AreEqual(first.Prepared.TentativeGeneration, second.Prepared.TentativeGeneration);
        Assert.AreNotEqual(first.Prepared.Reservation.Generation, second.Prepared.Reservation.Generation);
        Assert.AreEqual(first.Prepared.Reservation.Generation.Value + 1,
            second.Prepared.Reservation.Generation.Value);
    }

    [TestMethod]
    public async Task CommitConsumesGlobalTokenAndPerKeyGenerationAcrossAtoBtoA()
    {
        await using var store = Store(maximumInstalled: 2);
        PreparedGateReplacement a1 = Prepare(store, "A"); Commit(store, a1);
        PreparedGateReplacement b1 = Prepare(store, "B"); Commit(store, b1);
        PreparedGateReplacement a2 = Prepare(store, "A"); Commit(store, a2);

        InMemoryGateDerivedStore.InstalledDesiredReplacement installedA =
            store.DesiredReplacementControl.FindInstalled(a2.Key)!;
        InMemoryGateDerivedStore.InstalledDesiredReplacement installedB =
            store.DesiredReplacementControl.FindInstalled(b1.Key)!;
        Assert.AreEqual(1UL, a1.TentativeToken.Value);
        Assert.AreEqual(2UL, b1.TentativeToken.Value);
        Assert.AreEqual(3UL, a2.TentativeToken.Value);
        Assert.AreEqual(1UL, a1.TentativeGeneration.Value);
        Assert.AreEqual(1UL, b1.TentativeGeneration.Value);
        Assert.AreEqual(2UL, a2.TentativeGeneration.Value);
        Assert.AreEqual(a2.TentativeToken, installedA.Token);
        Assert.AreEqual(b1.TentativeToken, installedB.Token);
        Assert.AreEqual(2, store.DesiredReplacementControl.Diagnostics.InstalledDesiredReplacementCount);
    }

    [TestMethod]
    public async Task CapacityCountsInstalledKeysAndReplacementNeedsNoAdditionalSlot()
    {
        await using var store = Store(maximumInstalled: 1);
        PreparedGateReplacement firstA = Prepare(store, "A"); Commit(store, firstA);

        PrepareDesiredReplacementResult rejectedB = store.DesiredReplacementControl.Prepare(Request("B"));
        Assert.AreEqual(PrepareDesiredReplacementStatus.CapacityExceeded, rejectedB.Status);
        Assert.AreEqual(1UL, store.DesiredReplacementControl.Diagnostics.CommittedToken);

        PreparedGateReplacement secondA = Prepare(store, "A");
        Assert.AreEqual(GatePreparedReservationResultStatus.Committed, Commit(store, secondA).Status);
        Assert.AreEqual(1, store.DesiredReplacementControl.Diagnostics.InstalledDesiredReplacementCount);
        Assert.AreEqual(0, store.DesiredReplacementControl.Diagnostics.ProvisionalControlSlots);
    }

    [TestMethod]
    public async Task ExactCapacityAndAbortReturnAProvisionalNewKeySlot()
    {
        await using var store = Store(maximumInstalled: 2);
        PreparedGateReplacement a = Prepare(store, "A"); Commit(store, a);
        PreparedGateReplacement b = Prepare(store, "B");
        Assert.AreEqual(1, store.DesiredReplacementControl.Diagnostics.ProvisionalControlSlots);
        Abort(store, b);
        Assert.AreEqual(0, store.DesiredReplacementControl.Diagnostics.ProvisionalControlSlots);

        PreparedGateReplacement c = Prepare(store, "C"); Commit(store, c);
        DesiredReplacementControlDiagnostics exact = store.DesiredReplacementControl.Diagnostics;
        Assert.AreEqual(2, exact.InstalledDesiredReplacementCount);
        PrepareDesiredReplacementResult oneOver = store.DesiredReplacementControl.Prepare(Request("D"));
        Assert.AreEqual(PrepareDesiredReplacementStatus.CapacityExceeded, oneOver.Status);
        Assert.AreEqual(exact, store.DesiredReplacementControl.Diagnostics);
    }

    [TestMethod]
    public async Task AbortPreservesPriorDesireAndCommitSupersedesIt()
    {
        await using var store = Store();
        await Activate(store, "predecessor-a");
        PreparedGateReplacement first = Prepare(store, "A"); Commit(store, first);
        InMemoryGateDerivedStore.InstalledDesiredReplacement original =
            store.DesiredReplacementControl.FindInstalled(first.Key)!;
        ActiveDerivedViewDescriptor originalPredecessor = original.Predecessor!;

        await Activate(store, "predecessor-b");
        PreparedGateReplacement aborted = Prepare(store, "A"); Abort(store, aborted);
        Assert.AreSame(original, store.DesiredReplacementControl.FindInstalled(first.Key));
        Assert.AreEqual(originalPredecessor,
            store.DesiredReplacementControl.FindInstalled(first.Key)!.Predecessor);

        PreparedGateReplacement replacement = Prepare(store, "A"); Commit(store, replacement);
        InMemoryGateDerivedStore.InstalledDesiredReplacement current =
            store.DesiredReplacementControl.FindInstalled(first.Key)!;
        Assert.AreNotSame(original, current);
        Assert.AreEqual(replacement.TentativeToken, current.Token);
        Assert.AreEqual(replacement.TentativeGeneration, current.Generation);
        Assert.AreEqual(store.ActiveView, current.Predecessor);
    }

    [TestMethod]
    public async Task CommitCapturesActiveViewAtInstallationAndNeverChangesIt()
    {
        await using var store = Store();
        await Activate(store, "view-a");
        ActiveDerivedViewDescriptor viewA = store.ActiveView!;
        PreparedGateReplacement stable = Prepare(store, "A"); Commit(store, stable);
        Assert.AreEqual(viewA, store.DesiredReplacementControl.FindInstalled(stable.Key)!.Predecessor);

        PreparedGateReplacement changing = Prepare(store, "A");
        await Activate(store, "view-b");
        ActiveDerivedViewDescriptor viewB = store.ActiveView!;
        Commit(store, changing);

        Assert.AreEqual(viewB, store.DesiredReplacementControl.FindInstalled(changing.Key)!.Predecessor);
        Assert.AreEqual(viewB, store.ActiveView);
    }

    [TestMethod]
    public async Task CommitWithoutActiveViewCapturesNullAndPreservesRevisionAndHistoricalBudget()
    {
        await using var store = Store();
        DerivedStoreRevision revision = store.Revision;
        GateDerivedStoreDiagnostics before = store.Diagnostics;
        PreparedGateReplacement prepared = Prepare(store, "A");
        Commit(store, prepared);

        Assert.IsNull(store.DesiredReplacementControl.FindInstalled(prepared.Key)!.Predecessor);
        Assert.IsNull(store.ActiveView);
        Assert.AreEqual(revision, store.Revision);
        Assert.AreEqual(before.LogicalPayloadBytes, store.Diagnostics.LogicalPayloadBytes);
        Assert.AreEqual(before.BatchCount, store.Diagnostics.BatchCount);
        Assert.AreEqual(before.ReservedLogicalPayloadBytes, store.Diagnostics.ReservedLogicalPayloadBytes);
    }

    [TestMethod]
    public async Task ReplayCredentialPrecedenceDefaultAndEvictionAreDeterministic()
    {
        await using var store = Store(tombstones: 2);
        Assert.AreEqual(GatePreparedReservationResultStatus.InvalidReservation,
            store.DesiredReplacementControl.Commit(default, default, default).Status);

        PreparedGateReplacement first = Prepare(store, "A");
        var wrongOwner = new GatePreparedReservationOwner(Guid.NewGuid());
        Assert.AreEqual(GatePreparedReservationResultStatus.OwnerOrGenerationMismatch,
            store.DesiredReplacementControl.Commit(first.Reservation, wrongOwner,
                first.Reservation.Generation).Status);
        Assert.AreEqual(GatePreparedReservationResultStatus.Aborted, Abort(store, first).Status);
        Assert.AreEqual(GatePreparedReservationResultStatus.AlreadyAborted, Commit(store, first).Status);

        PreparedGateReplacement second = Prepare(store, "B"); Commit(store, second);
        PreparedGateReplacement third = Prepare(store, "C"); Abort(store, third);
        Assert.AreEqual(2, store.DesiredReplacementControl.Diagnostics.TerminalReservationTombstones);
        Assert.AreEqual(GatePreparedReservationResultStatus.InvalidReservation, Commit(store, first).Status);
        Assert.AreEqual(GatePreparedReservationResultStatus.AlreadyCommitted, Commit(store, second).Status);
        Assert.AreEqual(GatePreparedReservationResultStatus.AlreadyAborted, Abort(store, third).Status);
    }

    [TestMethod]
    public async Task CommitAndAbortHaveExactSingleConsumptionReplayMatrix()
    {
        await using var store = Store();
        PreparedGateReplacement committed = Prepare(store, "A");
        Assert.AreEqual(GatePreparedReservationResultStatus.Committed, Commit(store, committed).Status);
        Assert.AreEqual(GatePreparedReservationResultStatus.AlreadyCommitted, Commit(store, committed).Status);
        Assert.AreEqual(GatePreparedReservationResultStatus.AlreadyCommitted, Abort(store, committed).Status);

        PreparedGateReplacement aborted = Prepare(store, "B");
        Assert.AreEqual(GatePreparedReservationResultStatus.Aborted, Abort(store, aborted).Status);
        Assert.AreEqual(GatePreparedReservationResultStatus.AlreadyAborted, Abort(store, aborted).Status);
        Assert.AreEqual(GatePreparedReservationResultStatus.AlreadyAborted, Commit(store, aborted).Status);
        var wrongGeneration = new GatePreparedOperationGeneration(aborted.Reservation.Generation.Value + 1);
        Assert.AreEqual(GatePreparedReservationResultStatus.OwnerOrGenerationMismatch,
            store.DesiredReplacementControl.Commit(aborted.Reservation, aborted.Reservation.Owner,
                wrongGeneration).Status);
    }

    [TestMethod]
    public async Task CommitAbortRaceHasOneFirstConsumptionAndBalancedCapacity()
    {
        await using var store = Store();
        PreparedGateReplacement prepared = Prepare(store, "A");
        using var barrier = new Barrier(3);
        Task<GatePreparedReservationResult> commit = Task.Run(() =>
        { barrier.SignalAndWait(); return Commit(store, prepared); });
        Task<GatePreparedReservationResult> abort = Task.Run(() =>
        { barrier.SignalAndWait(); return Abort(store, prepared); });
        barrier.SignalAndWait();
        GatePreparedReservationResult[] results = await Task.WhenAll(commit, abort);

        Assert.AreEqual(1, results.Count(x => x.Status is GatePreparedReservationResultStatus.Committed or
            GatePreparedReservationResultStatus.Aborted));
        Assert.AreEqual(1, results.Count(x => x.Status is GatePreparedReservationResultStatus.AlreadyCommitted or
            GatePreparedReservationResultStatus.AlreadyAborted));
        bool committedWon = results.Any(x => x.Status == GatePreparedReservationResultStatus.Committed);
        Assert.AreEqual(committedWon, store.DesiredReplacementControl.FindInstalled(prepared.Key) is not null);
        Assert.IsFalse(store.DesiredReplacementControl.Diagnostics.HasPreparedReservation);
        Assert.AreEqual(0, store.DesiredReplacementControl.Diagnostics.ProvisionalControlSlots);
    }

    [TestMethod]
    public async Task DisposeAbortsPreparedClearsInstalledDesiresAndClosesAdmission()
    {
        var store = Store();
        PreparedGateReplacement installed = Prepare(store, "A"); Commit(store, installed);
        PreparedGateReplacement pending = Prepare(store, "B");
        await store.DisposeAsync();
        await store.DisposeAsync();

        DesiredReplacementControlDiagnostics diagnostics = store.DesiredReplacementControl.Diagnostics;
        Assert.IsFalse(diagnostics.HasPreparedReservation);
        Assert.AreEqual(0, diagnostics.InstalledDesiredReplacementCount);
        Assert.AreEqual(0, diagnostics.ProvisionalControlSlots);
        Assert.IsNull(store.DesiredReplacementControl.FindInstalled(installed.Key));
        Assert.AreEqual(GatePreparedReservationResultStatus.AlreadyAborted, Abort(store, pending).Status);
        Assert.AreEqual(PrepareDesiredReplacementStatus.Closed,
            store.DesiredReplacementControl.Prepare(Request("C")).Status);
    }

    [TestMethod]
    public async Task CommitAndDisposeRaceIsLinearizableAndAlwaysClearsControlPlane()
    {
        var store = Store();
        PreparedGateReplacement prepared = Prepare(store, "A");
        using var barrier = new Barrier(3);
        Task<GatePreparedReservationResult> commit = Task.Run(() =>
        { barrier.SignalAndWait(); return Commit(store, prepared); });
        Task dispose = Task.Run(async () =>
        { barrier.SignalAndWait(); await store.DisposeAsync(); });
        barrier.SignalAndWait();
        GatePreparedReservationResult result = await commit;
        await dispose;

        Assert.IsTrue(result.Status is GatePreparedReservationResultStatus.Committed or
            GatePreparedReservationResultStatus.AlreadyAborted);
        Assert.AreEqual(0, store.DesiredReplacementControl.Diagnostics.InstalledDesiredReplacementCount);
        Assert.AreEqual(0, store.DesiredReplacementControl.Diagnostics.ProvisionalControlSlots);
        Assert.IsFalse(store.DesiredReplacementControl.Diagnostics.HasPreparedReservation);
    }

    [TestMethod]
    public async Task AbortAndDisposeRaceIsLinearizableAndAlwaysClearsControlPlane()
    {
        var store = Store();
        PreparedGateReplacement prepared = Prepare(store, "A");
        using var barrier = new Barrier(3);
        Task<GatePreparedReservationResult> abort = Task.Run(() =>
        { barrier.SignalAndWait(); return Abort(store, prepared); });
        Task dispose = Task.Run(async () =>
        { barrier.SignalAndWait(); await store.DisposeAsync(); });
        barrier.SignalAndWait();
        GatePreparedReservationResult result = await abort;
        await dispose;

        Assert.IsTrue(result.Status is GatePreparedReservationResultStatus.Aborted or
            GatePreparedReservationResultStatus.AlreadyAborted);
        Assert.AreEqual(0, store.DesiredReplacementControl.Diagnostics.InstalledDesiredReplacementCount);
        Assert.AreEqual(0, store.DesiredReplacementControl.Diagnostics.ProvisionalControlSlots);
        Assert.IsFalse(store.DesiredReplacementControl.Diagnostics.HasPreparedReservation);
    }

    [TestMethod]
    public async Task RunPayloadMustMatchOpenAuthority()
    {
        await using var store = Store();
        PrepareDesiredReplacementRequest authorized = Request("A");
        PrepareDesiredReplacementResult accepted = store.DesiredReplacementControl.Prepare(authorized);
        Assert.AreEqual(PrepareDesiredReplacementStatus.Prepared, accepted.Status);
        Abort(store, accepted.Prepared);

        await using var invalidStore = Store();
        PrepareDesiredReplacementRequest wrongRun = authorized with { RunId = new(Guid.NewGuid()) };
        Assert.AreEqual(PrepareDesiredReplacementStatus.RunNotOpen,
            invalidStore.DesiredReplacementControl.Prepare(wrongRun).Status);

        PrepareDesiredReplacementRequest invalid = authorized with { RunId = default };
        Assert.AreEqual(PrepareDesiredReplacementStatus.InvalidIdentity,
            invalidStore.DesiredReplacementControl.Prepare(invalid).Status);
    }

    [TestMethod]
    public async Task EveryInvalidIdentityIsRejectedWithoutConsumingOperationGeneration()
    {
        PrepareDesiredReplacementRequest valid = Request("A");
        PrepareDesiredReplacementRequest[] invalid =
        [
            valid with { InspectionId = default },
            valid with { RunId = default },
            valid with { RunGeneration = default },
            valid with { GatePlanId = default },
            valid with { GatePlanRevision = default },
            valid with { Owner = default }
        ];
        foreach (PrepareDesiredReplacementRequest request in invalid)
        {
            await using var store = Store();
            ulong operationGeneration = store.DesiredReplacementControl.Diagnostics.PreparedOperationGeneration;
            Assert.AreEqual(PrepareDesiredReplacementStatus.InvalidIdentity,
                store.DesiredReplacementControl.Prepare(request).Status);
            Assert.AreEqual(operationGeneration,
                store.DesiredReplacementControl.Diagnostics.PreparedOperationGeneration);
            Assert.IsFalse(store.DesiredReplacementControl.Diagnostics.HasPreparedReservation);
        }
    }

    [TestMethod]
    public void AllDesiredReplacementCountersRejectOverflowBeforeConstruction()
    {
        Assert.IsFalse(InMemoryGateDerivedStore.TryGetNextDesiredReplacementCounter(ulong.MaxValue, out _),
            "GateReplacementToken overflow must be rejected.");
        Assert.IsFalse(InMemoryGateDerivedStore.TryGetNextDesiredReplacementCounter(ulong.MaxValue, out _),
            "GateReplacementGeneration overflow must be rejected.");
        Assert.IsFalse(InMemoryGateDerivedStore.TryGetNextDesiredReplacementCounter(ulong.MaxValue, out _),
            "GatePreparedOperationGeneration overflow must be rejected.");
        Assert.IsTrue(InMemoryGateDerivedStore.TryGetNextDesiredReplacementCounter(ulong.MaxValue - 1,
            out ulong maximum));
        Assert.AreEqual(ulong.MaxValue, maximum);
    }

    private static InMemoryGateDerivedStore Store(int maximumInstalled = 4, int tombstones = 4)
    {
        var store = new InMemoryGateDerivedStore(null, new(maximumInstalled, tombstones));
        PrepareGateRunTransitionResult prepared = store.DesiredReplacementControl.PrepareRunTransition(new(null,
            InspectionId(), RunId(), new(Guid.Parse("dddddddd-dddd-dddd-dddd-dddddddddddd"))));
        Assert.AreEqual(PrepareGateRunTransitionStatus.Prepared, prepared.Status);
        Assert.AreEqual(GatePreparedReservationResultStatus.Committed,
            store.DesiredReplacementControl.CommitRunTransition(prepared.Prepared.Reservation,
                prepared.Prepared.Reservation.Owner, prepared.Prepared.Reservation.Generation).Status);
        return store;
    }

    private static PrepareDesiredReplacementRequest Request(string plan,
        GatePreparedReservationOwner? owner = null) => new(
        InspectionId(), RunId(),
        new GateRunGeneration(1), new GatePlanId(plan), new GatePlanRevision(3),
        owner ?? new GatePreparedReservationOwner(Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc")));

    private static InspectionId InspectionId() =>
        new(Guid.Parse("10000000-0000-0000-0000-000000000001"));

    private static AcquisitionRunId RunId() =>
        new(Guid.Parse("20000000-0000-0000-0000-000000000002"));

    private static PreparedGateReplacement Prepare(InMemoryGateDerivedStore store, string plan)
    {
        PrepareDesiredReplacementResult result = store.DesiredReplacementControl.Prepare(Request(plan));
        Assert.AreEqual(PrepareDesiredReplacementStatus.Prepared, result.Status);
        return result.Prepared;
    }

    private static GatePreparedReservationResult Commit(InMemoryGateDerivedStore store,
        PreparedGateReplacement prepared) => store.DesiredReplacementControl.Commit(prepared.Reservation,
        prepared.Reservation.Owner, prepared.Reservation.Generation);

    private static GatePreparedReservationResult Abort(InMemoryGateDerivedStore store,
        PreparedGateReplacement prepared) => store.DesiredReplacementControl.Abort(prepared.Reservation,
        prepared.Reservation.Owner, prepared.Reservation.Generation);

    private static async Task Activate(InMemoryGateDerivedStore store, string viewId)
    {
        CompiledGatePlan plan = GateEvaluationTestData.Compile(GateEvaluationTestData.Gate("A", 3));
        GateEvaluationBatch batch = new GateEvaluator(new GateScratchPool()).Evaluate(
            plan, GateEvaluationTestData.Trace(3), [1, 4, 2]).Batch!;
        GateDerivedMutationResult batchResult = await store.CommitAsync(batch);
        Assert.IsTrue(batchResult.Status is GateDerivedMutationStatus.Committed or GateDerivedMutationStatus.Duplicate);
        GateEvaluationEntry entry = batch.Entries[0];
        TraceIdentity identity = batch.Trace.ConfirmedIdentity;
        var stream = new GateDerivedStreamCoverage(identity.Stream, identity.AcquisitionIndex,
            identity.AcquisitionIndex, 1, GateDerivedStreamCoverage.ComputeIdentityDigest([identity]));
        var manifest = new GateDerivedCoverageManifest(identity.Stream.InspectionId,
            identity.Stream.AcquisitionRunId, [stream], batch.Trace.SourceInspectionRevision,
            entry.PlanId, entry.PlanRevision, entry.PlanSignature.Hex, entry.SchemaVersion,
            entry.AlgorithmVersion, 1, store.Revision);
        GateProcessingTerminalCoverageStatus terminal = store.ConfirmTerminalCoverage([
            new(manifest.InspectionId, manifest.RunId, manifest.SourceFinalRevision, manifest.PlanId,
                manifest.PlanRevision, manifest.PlanSignature, manifest.SchemaVersion,
                manifest.AlgorithmVersion, manifest.Streams)]);
        Assert.IsTrue(terminal is GateProcessingTerminalCoverageStatus.Accepted or
            GateProcessingTerminalCoverageStatus.Duplicate);
        Assert.AreEqual(GateDerivedMutationStatus.Activated,
            store.Activate(manifest, new DerivedViewId(viewId)).Status);
    }
}
