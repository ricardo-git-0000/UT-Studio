using UTStudio.Contracts.Inspection;
using UTStudio.Contracts.Processing;
using UTStudio.Domain.Acquisition;
using UTStudio.Domain.Inspection;
using UTStudio.Domain.Processing;
using UTStudio.Processing.Core;

namespace UTStudio.Core.Tests.Processing;

[TestClass]
public sealed class GateReplacementStoreTests
{
    private static readonly InspectionId Inspection = new(Guid.Parse("81000000-0000-0000-0000-000000000001"));
    private static readonly AcquisitionRunId Run = new(Guid.Parse("82000000-0000-0000-0000-000000000002"));
    private static readonly GatePreparedReservationOwner Owner = new(Guid.Parse("83000000-0000-0000-0000-000000000003"));

    [TestMethod]
    public async Task ReservationIsUniqueAndRunTransitionDefeatsLateCas()
    {
        await using var store = new InMemoryGateDerivedStore();
        GateStoreRunAuthoritySnapshot run = Open(store, Run);
        CommitDesired(store, run, new("plan"), new(1));
        var attempt = new GateReprocessingAttemptId(Guid.NewGuid());
        GateCandidateReservationResult first = store.ReplacementControl.ReserveCandidate(
            Inspection, Run, run.Generation, new("plan"), new(1), attempt);
        Assert.IsTrue(first.IsReserved);
        Assert.AreEqual(GateCandidateReservationStatus.AlreadyRequested,
            store.ReplacementControl.ReserveCandidate(Inspection, Run, run.Generation,
                new("plan"), new(1), new(Guid.NewGuid())).Status);
        GateStoreOnlyCandidateDescriptor candidate = store.ReplacementControl.MaterializeCandidate(
            first.Reservation!, Manifest(Run));

        var nextRun = new AcquisitionRunId(Guid.NewGuid());
        GateStoreRunAuthoritySnapshot stopped = store.DesiredReplacementControl.StopCurrentRun(run).Current;
        PreparedGateRunTransition transition = PrepareRun(store, stopped, nextRun);
        Assert.AreEqual(GatePreparedReservationResultStatus.Committed,
            store.DesiredReplacementControl.CommitRunTransition(transition.Reservation,
                transition.Reservation.Owner, transition.Reservation.Generation).Status);
        GateCandidateActivationResult late = store.ReplacementControl.ActivateCandidate(
            new(candidate, first.Reservation!.Predecessor, Run, run.Generation));

        Assert.AreEqual(GateCandidateActivationStatus.RunSuperseded, late.Status);
        GateCandidateAbandonResult abandoned = store.ReplacementControl.AbandonCandidate(candidate);
        Assert.AreEqual(GateCandidateAbandonStatus.LogicallyAbandoned, abandoned.Status);
        Assert.AreEqual(GateCandidateReclaimStatus.Reclaimed,
            store.ReplacementControl.ReclaimCandidate(abandoned.Candidate).Status);
    }

    [TestMethod]
    public async Task ReleasedPreadmissionReservationLeavesNoMaterialCandidateButKeepsRevisionClaim()
    {
        await using var store = new InMemoryGateDerivedStore();
        GateStoreRunAuthoritySnapshot run = Open(store, Run);
        CommitDesired(store, run, new("plan"), new(1));
        GateCandidateReservationResult reserved = store.ReplacementControl.ReserveCandidate(
            Inspection, Run, run.Generation, new("plan"), new(1), new(Guid.NewGuid()));

        store.ReplacementControl.ReleaseReservation(reserved.Reservation!);

        Assert.AreEqual(GateCandidateReservationStatus.AlreadyRequested,
            store.ReplacementControl.ReserveCandidate(Inspection, Run, run.Generation,
                new("plan"), new(1), new(Guid.NewGuid())).Status);
    }

    [TestMethod]
    public void StoreOnlyReceiptIsTerminalOnlyAndSupportsEmptyCoverage()
    {
        GateDerivedCoverageManifest manifest = Manifest(Run);
        var candidate = new GateStoreOnlyCandidateDescriptor(manifest, new("candidate"), new(1), new(1),
            GateCandidateOwnershipState.Available);
        CompiledGatePlan compiled = Compile();
        var epoch = new GateReprocessingPlanEpoch(manifest.PlanId, manifest.PlanRevision,
            compiled.Signature, manifest.SchemaVersion, manifest.AlgorithmVersion);
        DateTimeOffset now = DateTimeOffset.UtcNow;

        var terminal = new GateReprocessingProgressSnapshot(new(Guid.NewGuid()), new(Guid.NewGuid()),
            GateReprocessingLifecycle.Completed, manifest.SourceFinalRevision, manifest.PlanRevision,
            GateReprocessingPublicationPolicy.StoreOnly, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
            null, null, now, now, planEpoch: epoch, storeOnlyCandidate: candidate);

        Assert.AreSame(candidate, terminal.StoreOnlyCandidate);
        Assert.ThrowsExactly<ArgumentException>(() => new GateReprocessingProgressSnapshot(
            new(Guid.NewGuid()), new(Guid.NewGuid()), GateReprocessingLifecycle.Running,
            manifest.SourceFinalRevision, manifest.PlanRevision, GateReprocessingPublicationPolicy.StoreOnly,
            0, 0, 0, 0, 0, 0, 0, 0, 0, 0, null, null, now, now,
            planEpoch: epoch, storeOnlyCandidate: candidate));
    }

    [TestMethod]
    public async Task DisposedStoreConfirmsPhysicalCandidateOwnershipWithoutPostCloseMutation()
    {
        var store = new InMemoryGateDerivedStore();
        GateStoreRunAuthoritySnapshot run = Open(store, Run);
        CommitDesired(store, run, new("plan"), new(1));
        GateCandidateReservationResult reserved = store.ReplacementControl.ReserveCandidate(
            Inspection, Run, run.Generation, new("plan"), new(1), new(Guid.NewGuid()));
        GateStoreOnlyCandidateDescriptor candidate = store.ReplacementControl.MaterializeCandidate(
            reserved.Reservation!, Manifest(Run));

        await store.DisposeAsync();
        GateCandidateAbandonResult abandoned = store.ReplacementControl.AbandonCandidate(candidate);
        GateCandidateReclaimResult reclaimed = store.ReplacementControl.ReclaimCandidate(candidate);

        Assert.AreEqual(GateCandidateAbandonStatus.Disposed, abandoned.Status);
        Assert.AreEqual(GateCandidateOwnershipState.Reclaimed, abandoned.Candidate.OwnershipState);
        Assert.AreEqual(GateCandidateReclaimStatus.Disposed, reclaimed.Status);
        Assert.AreEqual(GateApplyLedgerLifecycleState.Disposed, reclaimed.Lifecycle);
    }

    [TestMethod]
    public async Task DisposeAfterLogicalAbandonCompletesStoreOwnedPhysicalCleanup()
    {
        var store = new InMemoryGateDerivedStore();
        GateStoreRunAuthoritySnapshot run = Open(store, Run);
        CommitDesired(store, run, new("plan"), new(1));
        GateCandidateReservationResult reserved = store.ReplacementControl.ReserveCandidate(
            Inspection, Run, run.Generation, new("plan"), new(1), new(Guid.NewGuid()));
        GateStoreOnlyCandidateDescriptor candidate = store.ReplacementControl.MaterializeCandidate(
            reserved.Reservation!, Manifest(Run));

        GateCandidateAbandonResult abandoned = store.ReplacementControl.AbandonCandidate(candidate);
        Assert.AreEqual(GateCandidateAbandonStatus.LogicallyAbandoned, abandoned.Status);
        Assert.AreEqual(GateCandidateOwnershipState.LogicallyAbandoned, abandoned.Candidate.OwnershipState);

        await store.DisposeAsync();
        GateCandidateReclaimResult reclaimed = store.ReplacementControl.ReclaimCandidate(abandoned.Candidate);

        Assert.AreEqual(GateCandidateReclaimStatus.Disposed, reclaimed.Status);
        Assert.AreEqual(GateCandidateOwnershipState.Reclaimed, reclaimed.Candidate.OwnershipState);
        Assert.AreEqual(GateApplyLedgerLifecycleState.Disposed, reclaimed.Lifecycle);
        Assert.AreEqual(0, store.Diagnostics.InvalidatedViewCount);
    }

    [TestMethod]
    public async Task PreviousViewBusyHasStoreOwnerUntilLeaseReleaseAndReclaimsExactlyOnce()
    {
        await using var store = new InMemoryGateDerivedStore();
        GateEvaluationBatch oldBatch = Batch(1, 0);
        Assert.AreEqual(GateDerivedMutationStatus.Committed, (await store.CommitAsync(oldBatch)).Status);
        GateDerivedCoverageManifest oldManifest = Manifest(oldBatch, store.Revision);
        Assert.AreEqual(GateDerivedMutationStatus.Committed,
            (await store.ConfirmTerminalCoverageAsync([Terminal(oldManifest)])).Status);
        oldManifest = Manifest(oldBatch, store.Revision);
        Assert.AreEqual(GateDerivedMutationStatus.Activated, store.Activate(oldManifest, new("old")).Status);
        GateStoreRunAuthoritySnapshot run = Open(store, Run);
        CommitDesired(store, run, new("plan"), new(2));
        GateEvaluationBatch newBatch = Batch(2, 1);
        Assert.AreEqual(GateDerivedMutationStatus.Committed, (await store.CommitAsync(newBatch)).Status);
        IGateDerivedReadLease lease = store.OpenActiveView();
        bool released = false;
        try
        {
            GateCandidateReservationResult reserved = store.ReplacementControl.ReserveCandidate(
                Inspection, Run, run.Generation, new("plan"), new(2), new(Guid.NewGuid()));
            GateStoreOnlyCandidateDescriptor candidate = store.ReplacementControl.MaterializeCandidate(
                reserved.Reservation!, Manifest(newBatch, store.Revision));
            Assert.IsTrue(store.ReplacementControl.ActivateCandidate(
                new(candidate, reserved.Reservation!.Predecessor, Run, run.Generation)).IsSuccess);

            Assert.AreEqual(GateDerivedMutationStatus.Busy,
                store.ReplacementControl.ReclaimPreviousView().Status);
            Assert.AreEqual(candidate.CandidateViewId, store.ActiveView!.ViewId);
            Assert.AreEqual(1, store.Diagnostics.InvalidatedViewCount);
            Task<GateDerivedMutationResult> firstRetirement =
                store.ReplacementControl.CompletePreviousViewRetirementAsync().AsTask();
            Task<GateDerivedMutationResult> secondRetirement =
                store.ReplacementControl.CompletePreviousViewRetirementAsync().AsTask();
            Assert.IsFalse(firstRetirement.IsCompleted);
            Assert.IsFalse(secondRetirement.IsCompleted);
            lease.Dispose(); released = true;
            Assert.AreEqual(GateDerivedMutationStatus.Reclaimed,
                (await firstRetirement).Status);
            Assert.AreEqual(GateDerivedMutationStatus.Reclaimed,
                (await secondRetirement).Status);
            Assert.AreEqual(1, store.Diagnostics.Reclaims);
            Assert.AreEqual(GateDerivedMutationStatus.NotFound,
                store.ReplacementControl.ReclaimPreviousView().Status);
            Assert.AreEqual(0, store.Diagnostics.InvalidatedViewCount);
        }
        finally { if (!released) lease.Dispose(); }
    }

    [TestMethod]
    public async Task CandidateCapacityAcceptsExactLimitAndOneOverLeavesNoClaim()
    {
        await using var store = new InMemoryGateDerivedStore();
        GateStoreRunAuthoritySnapshot run = Open(store, Run);
        var reservations = new List<GateCandidateReservation>();
        for (uint revision = 1; revision <= 64; revision++)
        {
            CommitDesired(store, run, new("plan"), new(revision));
            GateCandidateReservationResult result = store.ReplacementControl.ReserveCandidate(
                Inspection, Run, run.Generation, new("plan"), new(revision), new(Guid.NewGuid()));
            Assert.IsTrue(result.IsReserved, $"revision {revision}");
            reservations.Add(result.Reservation!);
        }

        CommitDesired(store, run, new("plan"), new(65));
        Assert.AreEqual(GateCandidateReservationStatus.CapacityExceeded,
            store.ReplacementControl.ReserveCandidate(Inspection, Run, run.Generation,
                new("plan"), new(65), new(Guid.NewGuid())).Status);

        store.ReplacementControl.ReleaseReservation(reservations[0]);
        Assert.AreEqual(GateCandidateReservationStatus.Reserved,
            store.ReplacementControl.ReserveCandidate(Inspection, Run, run.Generation,
                new("plan"), new(65), new(Guid.NewGuid())).Status);
    }

    [TestMethod]
    public async Task CasAndDesiredReplacementSerializeInBothOrders()
    {
        await using var store = new InMemoryGateDerivedStore();
        GateEvaluationBatch firstBatch = Batch(1, 0);
        Assert.AreEqual(GateDerivedMutationStatus.Committed, (await store.CommitAsync(firstBatch)).Status);
        GateDerivedCoverageManifest firstManifest = Manifest(firstBatch, store.Revision);
        Assert.AreEqual(GateDerivedMutationStatus.Committed,
            (await store.ConfirmTerminalCoverageAsync([Terminal(firstManifest)])).Status);
        firstManifest = Manifest(firstBatch, store.Revision);
        GateStoreRunAuthoritySnapshot run = Open(store, Run);
        CommitDesired(store, run, new("plan"), new(1));
        GateCandidateReservationResult first = store.ReplacementControl.ReserveCandidate(
            Inspection, Run, run.Generation, new("plan"), new(1), new(Guid.NewGuid()));
        GateStoreOnlyCandidateDescriptor candidateA = store.ReplacementControl.MaterializeCandidate(
            first.Reservation!, firstManifest);

        Assert.IsTrue(store.ReplacementControl.ActivateCandidate(
            new(candidateA, first.Reservation!.Predecessor, Run, run.Generation)).IsSuccess);
        ActiveDerivedViewDescriptor activeA = store.ActiveView!;
        CommitDesired(store, run, new("plan"), new(2));
        GateCandidateReservationResult second = store.ReplacementControl.ReserveCandidate(
            Inspection, Run, run.Generation, new("plan"), new(2), new(Guid.NewGuid()));
        Assert.AreEqual(activeA, second.Reservation!.Predecessor);

        CommitDesired(store, run, new("plan"), new(3));
        GateCandidateActivationResult late = store.ReplacementControl.ActivateCandidate(
            new(candidateA, first.Reservation.Predecessor, Run, run.Generation));
        Assert.AreEqual(GateCandidateActivationStatus.TokenSuperseded, late.Status);
        Assert.AreEqual(activeA, store.ActiveView);
    }

    [TestMethod]
    public async Task CasAndAbandonExcludeEachOtherInBothOrdersAndActivatedCandidateIsNeverReclaimed()
    {
        await using var store = new InMemoryGateDerivedStore();
        GateEvaluationBatch batch = Batch(1, 0);
        Assert.AreEqual(GateDerivedMutationStatus.Committed, (await store.CommitAsync(batch)).Status);
        GateDerivedCoverageManifest manifest = Manifest(batch, store.Revision);
        Assert.AreEqual(GateDerivedMutationStatus.Committed,
            (await store.ConfirmTerminalCoverageAsync([Terminal(manifest)])).Status);
        manifest = Manifest(batch, store.Revision);
        GateStoreRunAuthoritySnapshot run = Open(store, Run);
        CommitDesired(store, run, new("plan"), new(1));

        GateCandidateReservationResult winner = store.ReplacementControl.ReserveCandidate(
            Inspection, Run, run.Generation, new("plan"), new(1), new(Guid.NewGuid()));
        GateStoreOnlyCandidateDescriptor activated = store.ReplacementControl.MaterializeCandidate(
            winner.Reservation!, manifest);
        Assert.IsTrue(store.ReplacementControl.ActivateCandidate(
            new(activated, winner.Reservation!.Predecessor, Run, run.Generation)).IsSuccess);
        Assert.AreEqual(GateCandidateAbandonStatus.AlreadyActivated,
            store.ReplacementControl.AbandonCandidate(activated).Status);
        Assert.AreEqual(GateCandidateReclaimStatus.AlreadyActivated,
            store.ReplacementControl.ReclaimCandidate(activated).Status);

        CommitDesired(store, run, new("plan"), new(2));
        GateCandidateReservationResult loser = store.ReplacementControl.ReserveCandidate(
            Inspection, Run, run.Generation, new("plan"), new(2), new(Guid.NewGuid()));
        GateStoreOnlyCandidateDescriptor abandonedCandidate = store.ReplacementControl.MaterializeCandidate(
            loser.Reservation!, new GateDerivedCoverageManifest(manifest.InspectionId, manifest.RunId,
                manifest.Streams, manifest.SourceFinalRevision, manifest.PlanId, new(2), manifest.PlanSignature,
                manifest.SchemaVersion, manifest.AlgorithmVersion, manifest.TotalEligibleTraceCount,
                manifest.CapturedStoreRevision));
        GateCandidateAbandonResult abandoned = store.ReplacementControl.AbandonCandidate(abandonedCandidate);
        Assert.AreEqual(GateCandidateActivationStatus.CandidateAbandoned,
            store.ReplacementControl.ActivateCandidate(
                new(abandonedCandidate, loser.Reservation!.Predecessor, Run, run.Generation)).Status);
        Assert.AreEqual(GateCandidateReclaimStatus.Reclaimed,
            store.ReplacementControl.ReclaimCandidate(abandoned.Candidate).Status);
    }

    private static GateStoreRunAuthoritySnapshot Open(InMemoryGateDerivedStore store, AcquisitionRunId runId)
    {
        PreparedGateRunTransition prepared = PrepareRun(store, null, runId);
        Assert.AreEqual(GatePreparedReservationResultStatus.Committed,
            store.DesiredReplacementControl.CommitRunTransition(prepared.Reservation,
                prepared.Reservation.Owner, prepared.Reservation.Generation).Status);
        return prepared.Incoming;
    }

    private static PreparedGateRunTransition PrepareRun(InMemoryGateDerivedStore store,
        GateStoreRunAuthoritySnapshot? outgoing, AcquisitionRunId incoming)
    {
        PrepareGateRunTransitionResult result = store.DesiredReplacementControl.PrepareRunTransition(
            new(outgoing, Inspection, incoming, Owner));
        Assert.IsTrue(result.IsPrepared);
        return result.Prepared;
    }

    private static void CommitDesired(InMemoryGateDerivedStore store, GateStoreRunAuthoritySnapshot run,
        GatePlanId plan, GatePlanRevision revision)
    {
        PrepareDesiredReplacementResult prepared = store.DesiredReplacementControl.Prepare(
            new(Inspection, Run, run.Generation, plan, revision, Owner));
        Assert.IsTrue(prepared.IsPrepared);
        Assert.AreEqual(GatePreparedReservationResultStatus.Committed,
            store.DesiredReplacementControl.Commit(prepared.Prepared.Reservation,
                prepared.Prepared.Reservation.Owner, prepared.Prepared.Reservation.Generation).Status);
    }

    private static GateDerivedCoverageManifest Manifest(AcquisitionRunId run,
        DerivedStoreRevision? captured = null)
    {
        CompiledGatePlan compiled = Compile();
        var stream = new UtStreamIdentity(Inspection, run, new("source"), new(0));
        byte[] header = new byte[6];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(header,
            GateDerivedStreamCoverage.DigestCanonicalVersion);
        return new(Inspection, run, [new GateDerivedStreamCoverage(stream, null, null, 0,
            System.Security.Cryptography.SHA256.HashData(header), [new(0, 0)])], new(1), new("plan"), new(1),
            compiled.Signature.Hex, compiled.Source.SchemaVersion, compiled.Source.AlgorithmVersion, 0,
            captured ?? new(1));
    }

    private static GateProcessingTerminalCoverage Terminal(GateDerivedCoverageManifest manifest) => new(
        manifest.InspectionId, manifest.RunId, manifest.SourceFinalRevision, manifest.PlanId,
        manifest.PlanRevision, manifest.PlanSignature, manifest.SchemaVersion, manifest.AlgorithmVersion,
        manifest.Streams, manifest.CoverageScope);

    private static GateEvaluationBatch Batch(uint revision, ulong index)
    {
        CompiledGatePlan plan = new GatePlanCompiler().Compile(
            GateTestPlan.Plan([GateTestPlan.Gate("A", durationSeconds: 0.000001)],
                id: "plan", revision: revision)).CompiledPlan!;
        var identity = new TraceIdentity(new(Inspection, Run, new("source"), new(0)), index);
        var descriptor = new TraceDescriptor(identity, index, TimeSpan.Zero, null, null,
            4, 10_000_000, 0, UtSignalMode.Rf, traceQuality: UtTraceQuality.Good);
        var trace = new ConfirmedGateTraceDescriptor(descriptor, identity, new(index + 1), UtTraceQuality.Good);
        return new GateEvaluator(new GateScratchPool()).Evaluate(plan, trace, new short[] { 1, 4, 2, 0 }).Batch!;
    }

    private static GateDerivedCoverageManifest Manifest(GateEvaluationBatch batch, DerivedStoreRevision revision)
    {
        GateEvaluationEntry entry = batch.Entries[0]; TraceIdentity identity = batch.Trace.ConfirmedIdentity;
        var coverage = new GateDerivedStreamCoverage(identity.Stream, identity.AcquisitionIndex,
            identity.AcquisitionIndex, 1, GateDerivedStreamCoverage.ComputeIdentityDigest([identity]));
        return new(identity.Stream.InspectionId, identity.Stream.AcquisitionRunId, [coverage],
            batch.Trace.SourceInspectionRevision, entry.PlanId, entry.PlanRevision, entry.PlanSignature.Hex,
            entry.SchemaVersion, entry.AlgorithmVersion, 1, revision);
    }

    private static CompiledGatePlan Compile() => new GatePlanCompiler().Compile(
        GateTestPlan.Plan([GateTestPlan.Gate("A")], id: "plan", revision: 1)).CompiledPlan!;
}
