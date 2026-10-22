using UTStudio.Application;
using System.Text;
using System.Runtime.CompilerServices;
using UTStudio.Contracts.Processing;
using UTStudio.Core.Tests.Processing;
using UTStudio.Domain.Acquisition;
using UTStudio.Domain.Inspection;
using UTStudio.Domain.Processing;
using UTStudio.Processing.Core;

namespace UTStudio.Core.Tests.Application;

[TestClass]
public sealed class InMemoryGatePlanAuthorityPreparedCommitTests
{
    private static readonly InspectionId Inspection = new(Guid.Parse("82000000-0000-0000-0000-000000000001"));
    private static readonly AcquisitionRunId Run = new(Guid.Parse("82000000-0000-0000-0000-000000000002"));

    [TestMethod]
    public async Task PrepareIsInvisibleCommitIsAtomicAndPublicationIsDeferred()
    {
        CommittedGatePlanSnapshot initial = Committed("plan", 1);
        using var owner = new InMemoryGatePlanAuthorityOwner([initial]);
        IGatePlanAuthorityReader reader = owner.Seal();
        var observed = new List<GatePlanAuthoritySnapshot>();
        var initialObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var published = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using IDisposable subscription = reader.Observe(initial.AuthorityKey).Subscribe(
            new Observer<GatePlanAuthoritySnapshot>(value =>
            {
                lock (observed) observed.Add(value);
                if (value.Committed.Revision.Value == 1) initialObserved.TrySetResult();
                if (value.Committed.Revision.Value == 2) published.TrySetResult();
            }));
        await initialObserved.Task.WaitAsync(TimeSpan.FromSeconds(5));

        GatePlanAuthorityPrepareResult prepared = owner.PreparedCommits.Prepare(Request(reader, initial.AuthorityKey));
        Assert.IsTrue(prepared.IsPrepared);
        Assert.AreEqual(1U, reader.Read(initial.AuthorityKey).Committed.Revision.Value);
        Assert.IsNull(owner.PreparedCommits.ReadInstalled(initial.AuthorityKey));
        Assert.AreEqual(1, owner.PreparedCommits.Diagnostics.PreparedReservations);
        Assert.AreEqual(1, owner.PreparedCommits.Diagnostics.ProvisionalLedgerSlots);

        GatePlanAuthorityCommitResult committed = owner.PreparedCommits.Commit(prepared.Reservation,
            prepared.Reservation.Owner, prepared.Reservation.Generation);
        Assert.AreEqual(GatePreparedReservationResultStatus.Committed, committed.Operation.Status);
        GatePlanAuthorityInstalledState installed = owner.PreparedCommits.ReadInstalled(initial.AuthorityKey)!;
        Assert.AreEqual(2U, installed.Authority.Committed.Revision.Value);
        Assert.AreEqual(2UL, installed.Authority.Generation.Value);
        Assert.AreEqual(2U, installed.Geometry.Revision.Value);
        Assert.HasCount(1, installed.Geometry.Gates);
        Assert.AreEqual(GateApplyCommitState.Accepted, installed.Ledger.Commit);
        Assert.AreEqual(GateLiveEpochState.Active, installed.Ledger.LiveEpoch);
        Assert.AreEqual(GateHistoricalAdmissionState.Deferred, installed.Ledger.HistoricalAdmission);
        Assert.AreEqual(GateHistoricalExecutionState.Dormant, installed.Ledger.HistoricalExecution);
        Assert.AreEqual(0, owner.PreparedCommits.Diagnostics.ProvisionalLedgerSlots);
        Assert.AreEqual(1, owner.PreparedCommits.Diagnostics.RetainedLedgerSlots);
        Assert.AreEqual(1, owner.PreparedCommits.Diagnostics.ConfirmedPublicationsPending);
        lock (observed) Assert.HasCount(1, observed);

        Assert.IsTrue(committed.Publication!.Publish());
        await published.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.AreEqual(0, owner.PreparedCommits.Diagnostics.ConfirmedPublicationsPending);
        Assert.IsFalse(committed.Publication.Publish());
        lock (observed) Assert.HasCount(2, observed);
    }

    [TestMethod]
    public void AbortAndReplayTableAreTypedAndDoNotMutateAuthority()
    {
        CommittedGatePlanSnapshot initial = Committed("plan", 1);
        using var owner = new InMemoryGatePlanAuthorityOwner([initial]);
        IGatePlanAuthorityReader reader = owner.Seal();
        GatePlanAuthorityPrepareResult first = owner.PreparedCommits.Prepare(Request(reader, initial.AuthorityKey));
        Assert.AreEqual(GatePreparedReservationResultStatus.Aborted,
            owner.PreparedCommits.Abort(first.Reservation, first.Reservation.Owner, first.Reservation.Generation).Status);
        Assert.AreEqual(GatePreparedReservationResultStatus.AlreadyAborted,
            owner.PreparedCommits.Abort(first.Reservation, first.Reservation.Owner, first.Reservation.Generation).Status);
        Assert.AreEqual(GatePreparedReservationResultStatus.AlreadyAborted,
            owner.PreparedCommits.Commit(first.Reservation, first.Reservation.Owner,
                first.Reservation.Generation).Operation.Status);
        Assert.AreEqual(1U, reader.Read(initial.AuthorityKey).Committed.Revision.Value);
        Assert.AreEqual(0, owner.PreparedCommits.Diagnostics.ProvisionalLedgerSlots);

        GatePlanAuthorityPrepareResult second = owner.PreparedCommits.Prepare(Request(reader, initial.AuthorityKey));
        Assert.AreEqual(GatePreparedReservationResultStatus.Committed,
            owner.PreparedCommits.Commit(second.Reservation, second.Reservation.Owner,
                second.Reservation.Generation).Operation.Status);
        Assert.AreEqual(GatePreparedReservationResultStatus.AlreadyCommitted,
            owner.PreparedCommits.Commit(second.Reservation, second.Reservation.Owner,
                second.Reservation.Generation).Operation.Status);
        Assert.AreEqual(GatePreparedReservationResultStatus.AlreadyCommitted,
            owner.PreparedCommits.Abort(second.Reservation, second.Reservation.Owner,
                second.Reservation.Generation).Status);
        Assert.AreEqual(GatePreparedReservationResultStatus.InvalidReservation,
            owner.PreparedCommits.Abort(default, second.Reservation.Owner, second.Reservation.Generation).Status);
        var wrongOwner = new GatePreparedReservationOwner(Guid.NewGuid());
        Assert.AreEqual(GatePreparedReservationResultStatus.OwnerOrGenerationMismatch,
            owner.PreparedCommits.Commit(second.Reservation, wrongOwner,
                second.Reservation.Generation).Operation.Status);
        Assert.AreEqual(GatePreparedReservationResultStatus.OwnerOrGenerationMismatch,
            owner.PreparedCommits.Abort(second.Reservation, second.Reservation.Owner, new(99)).Status);
    }

    [TestMethod]
    public void RejectionsDoNotConsumeRevisionGenerationOrCapacity()
    {
        CommittedGatePlanSnapshot initial = Committed("plan", 1);
        using var owner = new InMemoryGatePlanAuthorityOwner([initial]);
        Assert.AreEqual(GatePlanAuthorityPrepareStatus.NotOperational,
            owner.PreparedCommits.Prepare(Request(initial, initial.AuthorityKey)).Status);
        IGatePlanAuthorityReader reader = owner.Seal();
        GatePlanAuthorityPrepareRequest current = Request(reader, initial.AuthorityKey);
        GatePlanDraft staleDraft = Draft(initial, new(2));
        Assert.AreEqual(GatePlanAuthorityPrepareStatus.Conflict, owner.PreparedCommits.Prepare(
            current with { ExpectedRevision = new(2), Draft = staleDraft }).Status);
        Assert.AreEqual(GatePlanAuthorityPrepareStatus.InvalidIdentity, owner.PreparedCommits.Prepare(
            current with { Authority = new(Inspection, new("missing")) }).Status);
        Assert.AreEqual(0, owner.PreparedCommits.Diagnostics.PreparedReservations);
        Assert.AreEqual(0, owner.PreparedCommits.Diagnostics.ProvisionalLedgerSlots);
        GatePlanAuthorityPrepareResult accepted = owner.PreparedCommits.Prepare(current);
        Assert.IsTrue(accepted.IsPrepared);
        Assert.AreEqual(2U, accepted.Reservation.IsValid ? 2U : 0U);
        Assert.AreEqual(GatePreparedReservationResultStatus.Aborted,
            owner.PreparedCommits.Abort(accepted.Reservation, accepted.Reservation.Owner,
                accepted.Reservation.Generation).Status);
    }

    [TestMethod]
    public void SuppliedCompiledPlanMustRepresentTheSameDraftGates()
    {
        CommittedGatePlanSnapshot initial = Committed("plan", 1);
        using var owner = new InMemoryGatePlanAuthorityOwner([initial]);
        IGatePlanAuthorityReader reader = owner.Seal();
        GatePlanAuthorityPrepareRequest request = Request(reader, initial.AuthorityKey);
        GatePlanCompilationResult other = new GatePlanCompiler().Compile(
            GateTestPlan.Plan([GateTestPlan.Gate("A", threshold: 0.75)], "plan", revision: 2));
        Assert.IsTrue(other.IsCompiled);

        GatePlanAuthorityPrepareResult result = owner.PreparedCommits.Prepare(
            request with { CompiledPlan = other.CompiledPlan });

        Assert.AreEqual(GatePlanAuthorityPrepareStatus.InvalidIdentity, result.Status);
        Assert.AreEqual(0, owner.PreparedCommits.Diagnostics.PreparedReservations);
        Assert.AreEqual(0, owner.PreparedCommits.Diagnostics.ProvisionalLedgerSlots);
    }

    [TestMethod]
    public void OnlyOnePreparedReservationIsAcceptedAndValidationFailureConsumesNothing()
    {
        CommittedGatePlanSnapshot initial = Committed("plan", 1);
        using var owner = new InMemoryGatePlanAuthorityOwner([initial]);
        IGatePlanAuthorityReader reader = owner.Seal();
        GatePlanAuthorityPrepareRequest request = Request(reader, initial.AuthorityKey);
        GatePlanAuthorityPrepareResult first = owner.PreparedCommits.Prepare(request);
        Assert.IsTrue(first.IsPrepared);
        GatePlanAuthorityPreparedCommitDiagnostics beforeBusy = owner.PreparedCommits.Diagnostics;
        string[] retainedBeforeBusy = owner.PreparedCommits.ReadRetainedLedgers(Inspection)
            .Select(ledger => ledger.PlanId.Value).ToArray();
        GatePlanAuthoritySnapshot authorityBeforeBusy = reader.Read(initial.AuthorityKey);
        Assert.AreEqual(GatePlanAuthorityPrepareStatus.AlreadyPrepared,
            owner.PreparedCommits.Prepare(request with
            {
                Owner = new(Guid.NewGuid()),
                OperationGeneration = new(2)
            }).Status);
        Assert.AreEqual(beforeBusy, owner.PreparedCommits.Diagnostics);
        CollectionAssert.AreEqual(retainedBeforeBusy, owner.PreparedCommits.ReadRetainedLedgers(Inspection)
            .Select(ledger => ledger.PlanId.Value).ToArray());
        Assert.AreEqual(authorityBeforeBusy.Generation, reader.Read(initial.AuthorityKey).Generation);
        Assert.AreEqual(authorityBeforeBusy.Committed.Revision, reader.Read(initial.AuthorityKey).Committed.Revision);
        owner.PreparedCommits.Abort(first.Reservation, first.Reservation.Owner, first.Reservation.Generation);

        GatePlanDraft invalid = new(request.Draft.Authority, request.Draft.BaseRevision,
            request.Draft.SchemaVersion, request.Draft.AlgorithmVersion,
            [GateTestPlan.Gate("A", "B"), GateTestPlan.Gate("B", "A")]);
        Assert.AreEqual(GatePlanAuthorityPrepareStatus.ValidationFailed,
            owner.PreparedCommits.Prepare(request with { Draft = invalid }).Status);
        Assert.AreEqual(0, owner.PreparedCommits.Diagnostics.PreparedReservations);
        Assert.AreEqual(0, owner.PreparedCommits.Diagnostics.ProvisionalLedgerSlots);

        GatePlanAuthorityPrepareResult afterFailure = owner.PreparedCommits.Prepare(request with
        {
            Owner = new(Guid.NewGuid()),
            OperationGeneration = new(3)
        });
        Assert.IsTrue(afterFailure.IsPrepared);
        GatePlanAuthorityCommitResult committed = owner.PreparedCommits.Commit(afterFailure.Reservation,
            afterFailure.Reservation.Owner, afterFailure.Reservation.Generation);
        Assert.AreEqual(2U, committed.Publication!.Metadata.Revision.Value);
        Assert.AreEqual(2UL, committed.Publication.Metadata.Generation.Value);
    }

    [TestMethod]
    public void GlobalPreparedReservationSerializesKeysAndGenerationsNeverRegress()
    {
        CommittedGatePlanSnapshot one = Committed("one", 1);
        CommittedGatePlanSnapshot two = Committed("two", 1);
        using var owner = new InMemoryGatePlanAuthorityOwner([one, two]);
        IGatePlanAuthorityReader reader = owner.Seal();
        GatePlanAuthorityPrepareResult abortedOne = owner.PreparedCommits.Prepare(
            Request(reader, one.AuthorityKey, new(1)));
        Assert.AreEqual(GatePlanAuthorityPrepareStatus.AlreadyPrepared,
            owner.PreparedCommits.Prepare(Request(reader, two.AuthorityKey, new(2))).Status);
        owner.PreparedCommits.Abort(abortedOne.Reservation, abortedOne.Reservation.Owner,
            abortedOne.Reservation.Generation);
        GatePlanAuthorityPrepareResult reused = owner.PreparedCommits.Prepare(
            Request(reader, two.AuthorityKey, new(2)));
        Assert.AreEqual(abortedOne.PreparedGeneration, reused.PreparedGeneration);
        owner.PreparedCommits.Abort(reused.Reservation, reused.Reservation.Owner, reused.Reservation.Generation);

        GatePlanAuthorityPrepareResult first = owner.PreparedCommits.Prepare(
            Request(reader, one.AuthorityKey, new(3)));
        GatePlanAuthorityCommitResult committedOne = owner.PreparedCommits.Commit(first.Reservation,
            first.Reservation.Owner, first.Reservation.Generation);
        GatePlanAuthorityPrepareResult second = owner.PreparedCommits.Prepare(
            Request(reader, two.AuthorityKey, new(4)));
        GatePlanAuthorityCommitResult committedTwo = owner.PreparedCommits.Commit(second.Reservation,
            second.Reservation.Owner, second.Reservation.Generation);
        GatePlanAuthorityPrepareResult third = owner.PreparedCommits.Prepare(
            Request(reader, one.AuthorityKey, new(5)));
        GatePlanAuthorityCommitResult committedAgain = owner.PreparedCommits.Commit(third.Reservation,
            third.Reservation.Owner, third.Reservation.Generation);
        CollectionAssert.AreEqual(new ulong[] { 3, 4, 5 }, new[]
        {
            committedOne.Publication!.Metadata.Generation.Value,
            committedTwo.Publication!.Metadata.Generation.Value,
            committedAgain.Publication!.Metadata.Generation.Value
        });
        Assert.AreEqual(3U, reader.Read(one.AuthorityKey).Committed.Revision.Value);
        Assert.AreEqual(2U, reader.Read(two.AuthorityKey).Committed.Revision.Value);
        Assert.AreEqual(0, owner.PreparedCommits.Diagnostics.ProvisionalLedgerSlots);
        Assert.AreEqual(3, owner.PreparedCommits.Diagnostics.RetainedLedgerSlots);
    }

    [TestMethod]
    public void RevisionGenerationAndEstimateOverflowAreRejectedWithoutState()
    {
        CommittedGatePlanSnapshot maximumRevision = Committed("max", uint.MaxValue);
        using (var owner = new InMemoryGatePlanAuthorityOwner([maximumRevision]))
        {
            IGatePlanAuthorityReader reader = owner.Seal();
            Assert.AreEqual(GatePlanAuthorityPrepareStatus.RevisionExhausted,
                owner.PreparedCommits.Prepare(Request(reader, maximumRevision.AuthorityKey)).Status);
        }
        CommittedGatePlanSnapshot initial = Committed("generation", 1);
        using (var owner = new InMemoryGatePlanAuthorityOwner([initial], new(ulong.MaxValue)))
        {
            IGatePlanAuthorityReader reader = owner.Seal();
            Assert.AreEqual(GatePlanAuthorityPrepareStatus.GenerationExhausted,
                owner.PreparedCommits.Prepare(Request(reader, initial.AuthorityKey)).Status);
        }
        using (var owner = new InMemoryGatePlanAuthorityOwner([initial], default,
            new(estimatedLedgerFixedBytes: long.MaxValue), null))
        {
            IGatePlanAuthorityReader reader = owner.Seal();
            Assert.AreEqual(GatePlanAuthorityPrepareStatus.LedgerEstimateOverflow,
                owner.PreparedCommits.Prepare(Request(reader, initial.AuthorityKey)).Status);
            Assert.AreEqual(0, owner.PreparedCommits.Diagnostics.ProvisionalLedgerSlots);
        }
    }

    [TestMethod]
    public void SlotAndByteBudgetsAcceptExactAndRejectOneOverWithoutLeaks()
    {
        CommittedGatePlanSnapshot initial = Committed("budget", 1);
        long exactBytes;
        using (var probe = new InMemoryGatePlanAuthorityOwner([initial]))
        {
            IGatePlanAuthorityReader reader = probe.Seal();
            GatePlanAuthorityPrepareResult prepared = probe.PreparedCommits.Prepare(Request(reader, initial.AuthorityKey));
            exactBytes = prepared.EstimatedLedgerBytes;
            probe.PreparedCommits.Abort(prepared.Reservation, prepared.Reservation.Owner, prepared.Reservation.Generation);
        }
        using (var exact = new InMemoryGatePlanAuthorityOwner([initial], default,
            new(maximumRetainedLedgersPerInspection: 1, maximumEstimatedLedgerBytesPerInspection: exactBytes), null))
        {
            IGatePlanAuthorityReader reader = exact.Seal();
            GatePlanAuthorityPrepareResult prepared = exact.PreparedCommits.Prepare(Request(reader, initial.AuthorityKey));
            Assert.IsTrue(prepared.IsPrepared);
            exact.PreparedCommits.Commit(prepared.Reservation, prepared.Reservation.Owner, prepared.Reservation.Generation);
            GatePlanAuthorityPrepareResult overSlot = exact.PreparedCommits.Prepare(Request(reader, initial.AuthorityKey));
            Assert.AreEqual(GatePlanAuthorityPrepareStatus.LedgerSlotCapacityExceeded, overSlot.Status);
            Assert.AreEqual(GatePlanCapacityResource.LedgerRevisions, overSlot.CapacityResource);
        }
        using (var oneUnder = new InMemoryGatePlanAuthorityOwner([initial], default,
            new(maximumEstimatedLedgerBytesPerInspection: exactBytes - 1), null))
        {
            IGatePlanAuthorityReader reader = oneUnder.Seal();
            GatePlanAuthorityPrepareResult rejected = oneUnder.PreparedCommits.Prepare(Request(reader, initial.AuthorityKey));
            Assert.AreEqual(GatePlanAuthorityPrepareStatus.LedgerByteCapacityExceeded, rejected.Status);
            Assert.AreEqual(0, oneUnder.PreparedCommits.Diagnostics.ProvisionalLedgerBytes);
        }
    }

    [TestMethod]
    public void LedgerByteEstimateUsesUtf8AndCanonicalCauseCount()
    {
        CommittedGatePlanSnapshot initial = Committed("estimate", 1);
        using var owner = new InMemoryGatePlanAuthorityOwner([initial]);
        owner.Seal();
        GateApplyLedgerSnapshot ascii = TerminalLedger("e");
        GateApplyLedgerSnapshot multibyte = TerminalLedger("é");
        Assert.AreEqual(128 + Encoding.UTF8.GetByteCount("e"), owner.PreparedCommits.EstimateLedgerBytes(ascii));
        Assert.AreEqual(128 + Encoding.UTF8.GetByteCount("é"), owner.PreparedCommits.EstimateLedgerBytes(multibyte));

        GateApplyLedgerSnapshot caused = GateApplyLedgerSnapshot.Create(Inspection, Run, new(1), new("causes"), new(1),
            GateApplyCommitState.Accepted, GateLiveEpochState.Faulted, GateHistoricalAdmissionState.Rejected,
            GateHistoricalExecutionState.NotApplicable, GateCandidateCoverageState.NotApplicable,
            GateActivationState.NotApplicable, GateCandidateRetirementState.NotApplicable,
            GatePreviousViewRetirementState.NotApplicable, primaryCause:
                new(GateLedgerStage.LiveEpoch, GateLedgerCauseKind.Faulted), secondaryCauses:
                [new(GateLedgerStage.HistoricalAdmission, GateLedgerCauseKind.Rejected)]);
        Assert.AreEqual(128 + Encoding.UTF8.GetByteCount("causes") + (2 * 24),
            owner.PreparedCommits.EstimateLedgerBytes(caused));
    }

    [TestMethod]
    public void EligibleEvictionIsFifoAndPendingOrDormantLedgersAreNotEvicted()
    {
        CommittedGatePlanSnapshot initial = Committed("current", 1);
        GateApplyLedgerSnapshot oldest = TerminalLedger("oldest");
        GateApplyLedgerSnapshot newer = TerminalLedger("newer");
        using (var owner = new InMemoryGatePlanAuthorityOwner([initial], default,
            new(maximumRetainedLedgersPerInspection: 2), [new(oldest), new(newer)]))
        {
            IGatePlanAuthorityReader reader = owner.Seal();
            GatePlanAuthorityPrepareResult prepared = owner.PreparedCommits.Prepare(Request(reader, initial.AuthorityKey));
            Assert.IsTrue(prepared.IsPrepared);
            owner.PreparedCommits.Commit(prepared.Reservation, prepared.Reservation.Owner, prepared.Reservation.Generation);
            CollectionAssert.AreEqual(new[] { "newer", "current" }, owner.PreparedCommits
                .ReadRetainedLedgers(Inspection).Select(ledger => ledger.PlanId.Value).ToArray());
            Assert.AreEqual(owner.PreparedCommits.ReadRetainedLedgers(Inspection)
                .Sum(owner.PreparedCommits.EstimateLedgerBytes), owner.PreparedCommits.Diagnostics.RetainedLedgerBytes);
        }
        GateApplyLedgerSnapshot dormant = PreparingLedger("dormant");
        using (var owner = new InMemoryGatePlanAuthorityOwner([initial], default,
            new(maximumRetainedLedgersPerInspection: 1), [new(dormant)]))
        {
            IGatePlanAuthorityReader reader = owner.Seal();
            Assert.AreEqual(GatePlanAuthorityPrepareStatus.LedgerSlotCapacityExceeded,
                owner.PreparedCommits.Prepare(Request(reader, initial.AuthorityKey)).Status);
        }
        GateApplyLedgerSnapshot pending = GateApplyLedgerSnapshot.Create(Inspection, Run, new(1), new("pending"), new(1),
            GateApplyCommitState.Accepted, GateLiveEpochState.NotApplicable, GateHistoricalAdmissionState.NotApplicable,
            GateHistoricalExecutionState.NotApplicable, GateCandidateCoverageState.NotApplicable,
            GateActivationState.NotApplicable, GateCandidateRetirementState.NotApplicable,
            GatePreviousViewRetirementState.NotApplicable, pendingResources: GateApplyLedgerPendingResource.Observer);
        using var pendingOwner = new InMemoryGatePlanAuthorityOwner([initial], default,
            new(maximumRetainedLedgersPerInspection: 1), [new(pending)]);
        IGatePlanAuthorityReader pendingReader = pendingOwner.Seal();
        Assert.AreEqual(GatePlanAuthorityPrepareStatus.LedgerSlotCapacityExceeded,
            pendingOwner.PreparedCommits.Prepare(Request(pendingReader, initial.AuthorityKey)).Status);
    }

    [TestMethod]
    public void ByteOnlyEvictionAndInspectionBudgetsAreIndependent()
    {
        CommittedGatePlanSnapshot initial = Committed("fresh", 1);
        GateApplyLedgerSnapshot old = TerminalLedger("older");
        long byteLimit;
        using (var probe = new InMemoryGatePlanAuthorityOwner([initial]))
        {
            probe.Seal();
            byteLimit = Math.Max(probe.PreparedCommits.EstimateLedgerBytes(old), 128 + "fresh"u8.Length);
        }
        using (var owner = new InMemoryGatePlanAuthorityOwner([initial], default,
            new(maximumRetainedLedgersPerInspection: 4, maximumEstimatedLedgerBytesPerInspection: byteLimit),
            [new(old)]))
        {
            IGatePlanAuthorityReader reader = owner.Seal();
            GatePlanAuthorityPrepareResult prepared = owner.PreparedCommits.Prepare(Request(reader, initial.AuthorityKey));
            Assert.IsTrue(prepared.IsPrepared);
            owner.PreparedCommits.Commit(prepared.Reservation, prepared.Reservation.Owner, prepared.Reservation.Generation);
            CollectionAssert.AreEqual(new[] { "fresh" }, owner.PreparedCommits
                .ReadRetainedLedgers(Inspection).Select(ledger => ledger.PlanId.Value).ToArray());
        }

        InspectionId otherInspection = new(Guid.Parse("82000000-0000-0000-0000-000000000099"));
        GateApplyLedgerSnapshot other = TerminalLedger("other", otherInspection);
        using var isolated = new InMemoryGatePlanAuthorityOwner([initial], default,
            new(maximumRetainedLedgersPerInspection: 1), [new(other)]);
        IGatePlanAuthorityReader isolatedReader = isolated.Seal();
        Assert.IsTrue(isolated.PreparedCommits.Prepare(Request(isolatedReader, initial.AuthorityKey)).IsPrepared);
        Assert.HasCount(1, isolated.PreparedCommits.ReadRetainedLedgers(otherInspection));
    }

    [TestMethod]
    public void OnePrepareEvictsTwoCandidatesAndAbortReleasesItsReservation()
    {
        string largePlanId = new('n', GatePlanId.MaximumUtf8Bytes);
        CommittedGatePlanSnapshot initial = Committed(largePlanId, 1);
        GateApplyLedgerSnapshot first = TerminalLedger("a");
        GateApplyLedgerSnapshot second = TerminalLedger("b");
        GateApplyLedgerSnapshot third = TerminalLedger("c");
        using (var owner = new InMemoryGatePlanAuthorityOwner([initial], default,
            new(maximumRetainedLedgersPerInspection: 4, maximumEstimatedLedgerBytesPerInspection: 387),
            [new(first), new(second), new(third)]))
        {
            IGatePlanAuthorityReader reader = owner.Seal();
            GatePlanAuthorityPrepareResult prepared = owner.PreparedCommits.Prepare(Request(reader, initial.AuthorityKey));
            Assert.IsTrue(prepared.IsPrepared);
            owner.PreparedCommits.Commit(prepared.Reservation, prepared.Reservation.Owner, prepared.Reservation.Generation);
            CollectionAssert.AreEqual(new[] { "c", largePlanId }, owner.PreparedCommits
                .ReadRetainedLedgers(Inspection).Select(ledger => ledger.PlanId.Value).ToArray());
        }

        CommittedGatePlanSnapshot retryInitial = Committed("retry", 1);
        GateApplyLedgerSnapshot reusable = TerminalLedger("old");
        using var retryOwner = new InMemoryGatePlanAuthorityOwner([retryInitial], default,
            new(maximumRetainedLedgersPerInspection: 1), [new(reusable)]);
        IGatePlanAuthorityReader retryReader = retryOwner.Seal();
        GatePlanAuthorityPrepareResult aborted = retryOwner.PreparedCommits.Prepare(
            Request(retryReader, retryInitial.AuthorityKey));
        retryOwner.PreparedCommits.Abort(aborted.Reservation, aborted.Reservation.Owner, aborted.Reservation.Generation);
        CollectionAssert.AreEqual(new[] { "old" }, retryOwner.PreparedCommits
            .ReadRetainedLedgers(Inspection).Select(ledger => ledger.PlanId.Value).ToArray());
        GatePlanAuthorityPrepareResult retried = retryOwner.PreparedCommits.Prepare(
            Request(retryReader, retryInitial.AuthorityKey, new(2)));
        Assert.IsTrue(retried.IsPrepared);
        retryOwner.PreparedCommits.Commit(retried.Reservation, retried.Reservation.Owner, retried.Reservation.Generation);
        CollectionAssert.AreEqual(new[] { "retry" }, retryOwner.PreparedCommits
            .ReadRetainedLedgers(Inspection).Select(ledger => ledger.PlanId.Value).ToArray());
    }

    [TestMethod]
    public async Task DeferredPublicationSuppressesLateAndDuplicatePayloadsAndAllowsReentry()
    {
        CommittedGatePlanSnapshot initial = Committed("plan", 1);
        using var owner = new InMemoryGatePlanAuthorityOwner([initial]);
        IGatePlanAuthorityReader reader = owner.Seal();
        var seen = new List<uint>();
        var initialObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var latest = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using IDisposable subscription = reader.Observe(initial.AuthorityKey).Subscribe(new Observer<GatePlanAuthoritySnapshot>(value =>
        {
            lock (seen) seen.Add(value.Committed.Revision.Value);
            _ = reader.Read(value.Authority);
            if (value.Committed.Revision.Value == 1) initialObserved.TrySetResult();
            if (value.Committed.Revision.Value == 3) latest.TrySetResult();
        }));
        await initialObserved.Task.WaitAsync(TimeSpan.FromSeconds(5));
        GatePlanAuthorityPrepareResult a = owner.PreparedCommits.Prepare(Request(reader, initial.AuthorityKey));
        GatePlanAuthorityDeferredPublication publishA = owner.PreparedCommits.Commit(a.Reservation,
            a.Reservation.Owner, a.Reservation.Generation).Publication!;
        GatePlanAuthorityPrepareResult b = owner.PreparedCommits.Prepare(Request(reader, initial.AuthorityKey));
        GatePlanAuthorityDeferredPublication publishB = owner.PreparedCommits.Commit(b.Reservation,
            b.Reservation.Owner, b.Reservation.Generation).Publication!;
        Assert.IsTrue(publishB.Publish());
        Assert.IsTrue(publishA.Publish());
        await latest.Task.WaitAsync(TimeSpan.FromSeconds(5));
        lock (seen) CollectionAssert.AreEqual(new uint[] { 1, 2, 3 }, seen);

        int between = 0;
        var subscribed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using IDisposable afterCommit = reader.Observe(initial.AuthorityKey).Subscribe(
            new Observer<GatePlanAuthoritySnapshot>(_ =>
            {
                Interlocked.Increment(ref between);
                subscribed.TrySetResult();
            }));
        await subscribed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.IsFalse(publishB.Publish());
        await Task.Yield();
        Assert.AreEqual(1, Volatile.Read(ref between));
    }

    [TestMethod]
    public async Task ConfirmedPublicationAloneRootsDeliveryThenReleasesItsPayload()
    {
        PublicationGcFixture fixture = CreatePublicationGcFixture();
        ForceFullCollection();
        Assert.IsTrue(fixture.Reader.IsAlive);
        Assert.IsTrue(fixture.Publication.Publish());
        await fixture.Delivered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.IsTrue(fixture.Publication.IsPayloadReleased);
        Assert.IsFalse(fixture.Publication.TryGetPendingSnapshot(out _));
        Assert.IsFalse(fixture.Publication.Publish());

        ForceFullCollection();
        Assert.IsFalse(fixture.Reader.IsAlive);
        Assert.IsFalse(fixture.Committed.IsAlive);
        Assert.IsFalse(fixture.Geometry.IsAlive);
        Assert.IsFalse(fixture.Ledger.IsAlive);
    }

    [TestMethod]
    public async Task RetainedObservableCompletesWithoutRetainingOrReplayingState()
    {
        CommittedGatePlanSnapshot initial = Committed("observable", 1);
        var owner = new InMemoryGatePlanAuthorityOwner([initial]);
        IGatePlanAuthorityReader reader = owner.Seal();
        IObservable<GatePlanAuthoritySnapshot> observable = reader.Observe(initial.AuthorityKey);
        var initialDelivered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int completed = 0;
        using IDisposable subscription = observable.Subscribe(new Observer<GatePlanAuthoritySnapshot>(
            _ => initialDelivered.TrySetResult(), () => Interlocked.Increment(ref completed)));
        await initialDelivered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        owner.Dispose(); await owner.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.IsFalse(((IGatePlanAuthorityObservableDiagnostics)observable).HasRetainedState);

        int replayed = 0;
        var lateCompleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using IDisposable late = observable.Subscribe(new Observer<GatePlanAuthoritySnapshot>(
            _ => Interlocked.Increment(ref replayed), () => lateCompleted.TrySetResult()));
        await lateCompleted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.AreEqual(0, replayed);
        Assert.AreEqual(1, Volatile.Read(ref completed));
        Assert.IsFalse(((IGatePlanAuthorityObservableDiagnostics)observable).HasRetainedState);
    }

    [TestMethod]
    public async Task DisposeAbortsPreparedPreservesCommittedAndIsConcurrentIdempotent()
    {
        CommittedGatePlanSnapshot initial = Committed("plan", 1);
        var owner = new InMemoryGatePlanAuthorityOwner([initial]);
        IGatePlanAuthorityReader reader = owner.Seal();
        GatePlanAuthorityPrepareResult prepared = owner.PreparedCommits.Prepare(Request(reader, initial.AuthorityKey));
        Task[] closes = Enumerable.Range(0, 8).Select(_ => Task.Run(owner.Dispose)).ToArray();
        await Task.WhenAll(closes); await owner.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.AreEqual(GatePreparedReservationResultStatus.AlreadyAborted,
            owner.PreparedCommits.Commit(prepared.Reservation, prepared.Reservation.Owner,
                prepared.Reservation.Generation).Operation.Status);
        Assert.AreEqual(GatePreparedReservationResultStatus.AlreadyAborted,
            owner.PreparedCommits.Abort(prepared.Reservation, prepared.Reservation.Owner,
                prepared.Reservation.Generation).Status);
        Assert.AreEqual(0, owner.PreparedCommits.Diagnostics.ProvisionalLedgerSlots);
        Assert.AreEqual(0, owner.PreparedCommits.Diagnostics.RetainedLedgerSlots);

        var committedOwner = new InMemoryGatePlanAuthorityOwner([initial]);
        IGatePlanAuthorityReader committedReader = committedOwner.Seal();
        var committedEvents = new List<string>();
        var initialDelivered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var observerCompleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using IDisposable committedSubscription = committedReader.Observe(initial.AuthorityKey).Subscribe(
            new Observer<GatePlanAuthoritySnapshot>(value =>
            {
                lock (committedEvents) committedEvents.Add($"N{value.Committed.Revision.Value}");
                if (value.Committed.Revision.Value == 1) initialDelivered.TrySetResult();
            }, () =>
            {
                lock (committedEvents) committedEvents.Add("C");
                observerCompleted.TrySetResult();
            }));
        await initialDelivered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        GatePlanAuthorityPrepareResult committed = committedOwner.PreparedCommits.Prepare(
            Request(committedReader, initial.AuthorityKey));
        GatePlanAuthorityCommitResult committedResult = committedOwner.PreparedCommits.Commit(committed.Reservation,
            committed.Reservation.Owner, committed.Reservation.Generation);
        committedOwner.Dispose(); await committedOwner.Completion;
        await observerCompleted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.IsTrue(committedResult.Publication!.Publish());
        lock (committedEvents) CollectionAssert.AreEqual(new[] { "N1", "N2", "C" }, committedEvents);
        Assert.AreEqual(0, committedOwner.PreparedCommits.Diagnostics.ConfirmedPublicationsPending);
        Assert.AreEqual(0, committedOwner.PreparedCommits.Diagnostics.RetainedLedgerSlots);
        Assert.AreEqual(GatePreparedReservationResultStatus.AlreadyCommitted,
            committedOwner.PreparedCommits.Abort(committed.Reservation, committed.Reservation.Owner,
                committed.Reservation.Generation).Status);
    }

    [TestMethod]
    public async Task CommitAndAbortRacesWithDisposeHaveOneTerminalWinner()
    {
        CommittedGatePlanSnapshot initial = Committed("race", 1);
        var commitOwner = new InMemoryGatePlanAuthorityOwner([initial]);
        IGatePlanAuthorityReader commitReader = commitOwner.Seal();
        var revisions = new List<uint>();
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using IDisposable subscription = commitReader.Observe(initial.AuthorityKey).Subscribe(
            new Observer<GatePlanAuthoritySnapshot>(value => { lock (revisions) revisions.Add(value.Committed.Revision.Value); },
                () => completed.TrySetResult()));
        GatePlanAuthorityPrepareResult prepared = commitOwner.PreparedCommits.Prepare(Request(commitReader, initial.AuthorityKey));
        using var commitBarrier = new Barrier(3);
        Task<GatePlanAuthorityCommitResult> commit = Task.Run(() =>
        {
            commitBarrier.SignalAndWait();
            return commitOwner.PreparedCommits.Commit(prepared.Reservation,
                prepared.Reservation.Owner, prepared.Reservation.Generation);
        });
        Task close = Task.Run(() => { commitBarrier.SignalAndWait(); commitOwner.Dispose(); });
        commitBarrier.SignalAndWait();
        GatePlanAuthorityCommitResult result = await commit;
        await close; await commitOwner.Completion; await completed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        lock (revisions)
        {
            Assert.AreEqual(1U, revisions[0]);
            if (result.Operation.Status == GatePreparedReservationResultStatus.Committed)
                CollectionAssert.AreEqual(new uint[] { 1, 2 }, revisions);
            else
            {
                Assert.AreEqual(GatePreparedReservationResultStatus.AlreadyAborted, result.Operation.Status);
                CollectionAssert.AreEqual(new uint[] { 1 }, revisions);
            }
        }
        Assert.AreEqual(0, commitOwner.PreparedCommits.Diagnostics.ProvisionalLedgerSlots);
        Assert.AreEqual(0, commitOwner.PreparedCommits.Diagnostics.ConfirmedPublicationsPending);

        var abortOwner = new InMemoryGatePlanAuthorityOwner([initial]);
        IGatePlanAuthorityReader abortReader = abortOwner.Seal();
        GatePlanAuthorityPrepareResult abortPrepared = abortOwner.PreparedCommits.Prepare(
            Request(abortReader, initial.AuthorityKey));
        using var abortBarrier = new Barrier(3);
        Task<GatePreparedReservationResult> abort = Task.Run(() =>
        {
            abortBarrier.SignalAndWait();
            return abortOwner.PreparedCommits.Abort(abortPrepared.Reservation,
                abortPrepared.Reservation.Owner, abortPrepared.Reservation.Generation);
        });
        Task abortClose = Task.Run(() => { abortBarrier.SignalAndWait(); abortOwner.Dispose(); });
        abortBarrier.SignalAndWait();
        GatePreparedReservationResult abortResult = await abort;
        await abortClose; await abortOwner.Completion;
        Assert.IsTrue(abortResult.Status is GatePreparedReservationResultStatus.Aborted or
            GatePreparedReservationResultStatus.AlreadyAborted);
        Assert.AreEqual(0, abortOwner.PreparedCommits.Diagnostics.ProvisionalLedgerSlots);
    }

    [TestMethod]
    public void TombstonesAreBoundedAndReaderHasNoPreparedMutationSurface()
    {
        CommittedGatePlanSnapshot initial = Committed("plan", 1);
        using var owner = new InMemoryGatePlanAuthorityOwner([initial], default,
            new(terminalReservationTombstoneCapacity: 2), null);
        IGatePlanAuthorityReader reader = owner.Seal();
        GatePreparedReservation[] reservations = new GatePreparedReservation[3];
        for (int index = 0; index < reservations.Length; index++)
        {
            GatePlanAuthorityPrepareResult prepared = owner.PreparedCommits.Prepare(Request(reader,
                initial.AuthorityKey, new((ulong)index + 1)));
            reservations[index] = prepared.Reservation;
            owner.PreparedCommits.Abort(prepared.Reservation, prepared.Reservation.Owner,
                prepared.Reservation.Generation);
        }
        Assert.AreEqual(2, owner.PreparedCommits.Diagnostics.TerminalReservationTombstones);
        Assert.AreEqual(GatePreparedReservationResultStatus.InvalidReservation,
            owner.PreparedCommits.Abort(reservations[0], reservations[0].Owner, reservations[0].Generation).Status);
        Assert.AreEqual(GatePreparedReservationResultStatus.AlreadyAborted,
            owner.PreparedCommits.Abort(reservations[2], reservations[2].Owner, reservations[2].Generation).Status);
        Assert.IsFalse(typeof(IGatePlanAuthorityReader).GetMethods().Any(method =>
            method.Name.Contains("Prepare", StringComparison.Ordinal) ||
            method.Name.Contains("Commit", StringComparison.Ordinal) ||
            method.Name.Contains("Abort", StringComparison.Ordinal)));
    }

    private static GatePlanAuthorityPrepareRequest Request(IGatePlanAuthorityReader reader,
        GatePlanAuthorityKey key, GatePreparedOperationGeneration generation = default)
    {
        CommittedGatePlanSnapshot current = reader.Read(key).Committed;
        return Request(current, key, generation);
    }

    private static GatePlanAuthorityPrepareRequest Request(CommittedGatePlanSnapshot current,
        GatePlanAuthorityKey key, GatePreparedOperationGeneration generation = default) =>
        new(key, current.Revision, Draft(current, current.Revision), Run, new(1),
            new(Guid.NewGuid()), generation.IsValid ? generation : new(1));

    private static GatePlanDraft Draft(CommittedGatePlanSnapshot current, GatePlanRevision baseRevision) =>
        new(current.AuthorityKey, baseRevision, current.Plan.SchemaVersion,
            current.Plan.AlgorithmVersion, current.Plan.Gates);

    private static CommittedGatePlanSnapshot Committed(string id, uint revision)
    {
        GatePlanCompilationResult result = new GatePlanCompiler().Compile(
            GateTestPlan.Plan([GateTestPlan.Gate("A")], id, revision));
        Assert.IsTrue(result.IsCompiled); return new(Inspection, result.CompiledPlan!);
    }

    private static GateApplyLedgerSnapshot PreparingLedger(string id) => GateApplyLedgerSnapshot.Create(
        Inspection, Run, new(1), new(id), new(1), GateApplyCommitState.Preparing,
        GateLiveEpochState.Dormant, GateHistoricalAdmissionState.Dormant,
        GateHistoricalExecutionState.Dormant, GateCandidateCoverageState.Dormant,
        GateActivationState.Dormant, GateCandidateRetirementState.Dormant,
        GatePreviousViewRetirementState.Dormant);

    private static GateApplyLedgerSnapshot TerminalLedger(string id, InspectionId inspectionId = default) => GateApplyLedgerSnapshot.Create(
        inspectionId.IsValid ? inspectionId : Inspection, Run, new(1), new(id), new(1), GateApplyCommitState.Accepted,
        GateLiveEpochState.NotApplicable, GateHistoricalAdmissionState.NotApplicable,
        GateHistoricalExecutionState.NotApplicable, GateCandidateCoverageState.NotApplicable,
        GateActivationState.NotApplicable, GateCandidateRetirementState.NotApplicable,
        GatePreviousViewRetirementState.NotApplicable);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static PublicationGcFixture CreatePublicationGcFixture()
    {
        CommittedGatePlanSnapshot initial = Committed("gc", 1);
        var owner = new InMemoryGatePlanAuthorityOwner([initial]);
        IGatePlanAuthorityReader reader = owner.Seal();
        var delivered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _ = reader.Observe(initial.AuthorityKey).Subscribe(new Observer<GatePlanAuthoritySnapshot>(value =>
        {
            if (value.Committed.Revision.Value == 2) delivered.TrySetResult();
        }));
        GatePlanAuthorityPrepareResult prepared = owner.PreparedCommits.Prepare(Request(reader, initial.AuthorityKey));
        GatePlanAuthorityCommitResult committed = owner.PreparedCommits.Commit(prepared.Reservation,
            prepared.Reservation.Owner, prepared.Reservation.Generation);
        GatePlanAuthorityInstalledState installed = owner.PreparedCommits.ReadInstalled(initial.AuthorityKey)!;
        return new(committed.Publication!, delivered, new(reader), new(installed.Authority.Committed),
            new(installed.Geometry), new(installed.Ledger));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ForceFullCollection()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }

    private sealed record PublicationGcFixture(GatePlanAuthorityDeferredPublication Publication,
        TaskCompletionSource Delivered, WeakReference Reader, WeakReference Committed,
        WeakReference Geometry, WeakReference Ledger);

    private sealed class Observer<T>(Action<T> next, Action? completed = null) : IObserver<T>
    {
        public void OnNext(T value) => next(value);
        public void OnError(Exception error) => Assert.Fail(error.ToString());
        public void OnCompleted() => completed?.Invoke();
    }
}
