using UTStudio.Contracts.Inspection;
using UTStudio.Contracts.Processing;
using UTStudio.Domain.Acquisition;
using UTStudio.Domain.Inspection;
using UTStudio.Domain.Processing;
using UTStudio.Processing.Core;

namespace UTStudio.Core.Tests.Processing;

[TestClass]
public sealed class InMemoryGateDerivedStorePreparedRunTransitionTests
{
    private static readonly InspectionId Inspection = new(Guid.Parse("11111111-1111-1111-1111-111111111111"));
    private static readonly AcquisitionRunId RunA = new(Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"));
    private static readonly AcquisitionRunId RunB = new(Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"));
    private static readonly GatePreparedReservationOwner Owner = new(
        Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc"));

    [TestMethod]
    public async Task FirstRunAbortDoesNotConsumeRunGenerationButDoesConsumeOperationGeneration()
    {
        await using var store = new InMemoryGateDerivedStore();
        DerivedStoreRevision revision = store.Revision;
        PreparedGateRunTransition first = Prepare(store, null, Inspection, RunA);
        Assert.AreEqual(1UL, first.Incoming.Generation.Value);
        Assert.AreEqual(GateStoreRunLifecycle.None, store.DesiredReplacementControl.RunDiagnostics.Current.Lifecycle);

        Assert.AreEqual(GatePreparedReservationResultStatus.Aborted, Abort(store, first).Status);
        PreparedGateRunTransition retry = Prepare(store, null, Inspection, RunA);

        Assert.AreEqual(1UL, retry.Incoming.Generation.Value);
        Assert.AreEqual(first.Reservation.Generation.Value + 1, retry.Reservation.Generation.Value);
        Assert.AreEqual(GatePreparedReservationResultStatus.Committed, Commit(store, retry).Status);
        Assert.AreEqual(retry.Incoming, store.DesiredReplacementControl.RunDiagnostics.Current);
        Assert.AreEqual(revision, store.Revision);
    }

    [TestMethod]
    public async Task StopIsExactIdempotentAndDoesNotChangeRevision()
    {
        await using var store = new InMemoryGateDerivedStore();
        GateStoreRunAuthoritySnapshot open = Open(store, Inspection, RunA);
        DerivedStoreRevision revision = store.Revision;
        GateStoreRunAuthoritySnapshot wrong = open with { RunId = RunB };

        Assert.AreEqual(GateStoreRunStopStatus.IdentityMismatch,
            store.DesiredReplacementControl.StopCurrentRun(wrong).Status);
        GateStoreRunStopResult stopped = store.DesiredReplacementControl.StopCurrentRun(open);
        Assert.AreEqual(GateStoreRunStopStatus.Stopped, stopped.Status);
        Assert.AreEqual(GateStoreRunLifecycle.Stopped, stopped.Current.Lifecycle);
        Assert.AreEqual(GateStoreRunStopStatus.AlreadyStopped,
            store.DesiredReplacementControl.StopCurrentRun(open).Status);
        Assert.AreEqual(revision, store.Revision);
    }

    [TestMethod]
    public async Task TransitionRequiresExactStoppedOutgoingAndAdvancesGenerationOnCommit()
    {
        await using var store = new InMemoryGateDerivedStore();
        GateStoreRunAuthoritySnapshot open = Open(store, Inspection, RunA);
        Assert.AreEqual(PrepareGateRunTransitionStatus.OutgoingNotStopped,
            store.DesiredReplacementControl.PrepareRunTransition(Request(open, Inspection, RunB)).Status);
        GateStoreRunAuthoritySnapshot stopped = store.DesiredReplacementControl.StopCurrentRun(open).Current;
        Assert.AreEqual(PrepareGateRunTransitionStatus.IncomingMatchesOutgoing,
            store.DesiredReplacementControl.PrepareRunTransition(Request(stopped, Inspection, RunA)).Status);
        Assert.AreEqual(PrepareGateRunTransitionStatus.OutgoingMismatch,
            store.DesiredReplacementControl.PrepareRunTransition(Request(
                stopped with { Generation = new(9) }, Inspection, RunB)).Status);

        PreparedGateRunTransition prepared = Prepare(store, stopped, Inspection, RunB);
        Assert.AreEqual(open.Generation.Value + 1, prepared.Incoming.Generation.Value);
        Assert.AreEqual(stopped, store.DesiredReplacementControl.RunDiagnostics.Current);
        Commit(store, prepared);
        Assert.AreEqual(prepared.Incoming, store.DesiredReplacementControl.RunDiagnostics.Current);
    }

    [TestMethod]
    public async Task FinalReconciliationBlocksDesiredAndOldCompletionCannotChangeNewRun()
    {
        await using var store = new InMemoryGateDerivedStore();
        GateStoreRunAuthoritySnapshot open = Open(store, Inspection, RunA);
        GateStoreRunStopResult finalizingResult = store.DesiredReplacementControl
            .BeginFinalReconciliation(open);
        GateStoreRunAuthoritySnapshot finalizing = finalizingResult.Current;

        Assert.AreEqual(GateStoreRunLifecycle.FinalReconciling, finalizing.Lifecycle);
        Assert.AreEqual(PrepareDesiredReplacementStatus.RunNotOpen,
            store.DesiredReplacementControl.Prepare(new(Inspection, RunA, open.Generation,
                new("P"), new(1), Owner)).Status);
        PreparedGateRunTransition next = Prepare(store, finalizing, Inspection, RunB);
        Commit(store, next);

        Assert.AreEqual(GateStoreRunStopStatus.IdentityMismatch, store.DesiredReplacementControl
            .CompleteFinalReconciliation(finalizing).Status);
        Assert.AreEqual(next.Incoming, store.DesiredReplacementControl.RunDiagnostics.Current);
    }

    [TestMethod]
    public async Task AbortPreservesStoppedOutgoingAndInstalledDesire()
    {
        await using var store = new InMemoryGateDerivedStore();
        GateStoreRunAuthoritySnapshot open = Open(store, Inspection, RunA);
        PreparedGateReplacement desired = PrepareDesired(store, open, "P");
        CommitDesired(store, desired);
        GateStoreRunAuthoritySnapshot stopped = store.DesiredReplacementControl.StopCurrentRun(open).Current;

        PreparedGateRunTransition transition = Prepare(store, stopped, Inspection, RunB);
        Abort(store, transition);

        Assert.AreEqual(stopped, store.DesiredReplacementControl.RunDiagnostics.Current);
        Assert.IsNotNull(store.DesiredReplacementControl.FindInstalled(desired.Key));
    }

    [TestMethod]
    public async Task CommitRemovesOnlyOutgoingDesiresAndPreservesReplacementCounters()
    {
        await using var store = new InMemoryGateDerivedStore();
        GateStoreRunAuthoritySnapshot firstRun = Open(store, Inspection, RunA);
        PreparedGateReplacement first = PrepareDesired(store, firstRun, "P");
        CommitDesired(store, first);
        GateStoreRunAuthoritySnapshot stopped = store.DesiredReplacementControl.StopCurrentRun(firstRun).Current;
        Commit(store, Prepare(store, stopped, Inspection, RunB));

        Assert.IsNull(store.DesiredReplacementControl.FindInstalled(first.Key));
        GateStoreRunAuthoritySnapshot secondRun = store.DesiredReplacementControl.RunDiagnostics.Current;
        PreparedGateReplacement second = PrepareDesired(store, secondRun, "P");
        Assert.AreEqual(first.TentativeToken.Value + 1, second.TentativeToken.Value);
        Assert.AreEqual(first.TentativeGeneration.Value + 1, second.TentativeGeneration.Value);
    }

    [TestMethod]
    public async Task DesiredAndTransitionUseOneGlobalPreparedSlotInBothOrders()
    {
        await using var store = new InMemoryGateDerivedStore();
        GateStoreRunAuthoritySnapshot open = Open(store, Inspection, RunA);
        PreparedGateReplacement desired = PrepareDesired(store, open, "P");
        Assert.AreEqual(PrepareGateRunTransitionStatus.AlreadyPrepared,
            store.DesiredReplacementControl.PrepareRunTransition(Request(open, Inspection, RunB)).Status);
        AbortDesired(store, desired);

        GateStoreRunAuthoritySnapshot stopped = store.DesiredReplacementControl.StopCurrentRun(open).Current;
        PreparedGateRunTransition transition = Prepare(store, stopped, Inspection, RunB);
        PrepareDesiredReplacementRequest desiredRequest = DesiredRequest(stopped, "Q");
        Assert.AreEqual(PrepareDesiredReplacementStatus.AlreadyPrepared,
            store.DesiredReplacementControl.Prepare(desiredRequest).Status);
        Abort(store, transition);
    }

    [TestMethod]
    public async Task DesiredRequiresExactOpenRunAuthority()
    {
        await using var unconfigured = new InMemoryGateDerivedStore();
        Assert.AreEqual(PrepareDesiredReplacementStatus.RunNotOpen,
            unconfigured.DesiredReplacementControl.Prepare(DesiredRequest(
                new(Inspection, RunA, new(1), GateStoreRunLifecycle.Open), "P")).Status);

        await using var store = new InMemoryGateDerivedStore();
        GateStoreRunAuthoritySnapshot open = Open(store, Inspection, RunA);
        Assert.AreEqual(PrepareDesiredReplacementStatus.RunNotOpen,
            store.DesiredReplacementControl.Prepare(DesiredRequest(open with { RunId = RunB }, "P")).Status);
        GateStoreRunAuthoritySnapshot stopped = store.DesiredReplacementControl.StopCurrentRun(open).Current;
        Assert.AreEqual(PrepareDesiredReplacementStatus.RunNotOpen,
            store.DesiredReplacementControl.Prepare(DesiredRequest(stopped, "P")).Status);
    }

    [TestMethod]
    public async Task StopAndDesiredPrepareAreLinearizableInBothOrders()
    {
        await using (var prepareFirst = new InMemoryGateDerivedStore())
        {
            GateStoreRunAuthoritySnapshot open = Open(prepareFirst, Inspection, RunA);
            PreparedGateReplacement desired = PrepareDesired(prepareFirst, open, "P");
            GateStoreRunStopResult stopped = prepareFirst.DesiredReplacementControl.StopCurrentRun(open);
            Assert.AreEqual(GateStoreRunStopStatus.Stopped, stopped.Status);
            Assert.IsNull(prepareFirst.ActiveView);
            Assert.AreEqual(GatePreparedReservationResultStatus.Committed,
                CommitDesired(prepareFirst, desired).Status);
            Assert.IsNull(prepareFirst.ActiveView);
        }

        await using (var stopFirst = new InMemoryGateDerivedStore())
        {
            GateStoreRunAuthoritySnapshot open = Open(stopFirst, Inspection, RunA);
            stopFirst.DesiredReplacementControl.StopCurrentRun(open);
            DesiredReplacementControlDiagnostics before = stopFirst.DesiredReplacementControl.Diagnostics;
            Assert.AreEqual(PrepareDesiredReplacementStatus.RunNotOpen,
                stopFirst.DesiredReplacementControl.Prepare(DesiredRequest(open, "P")).Status);
            Assert.AreEqual(before, stopFirst.DesiredReplacementControl.Diagnostics);
        }

        await using (var prepareThenAbort = new InMemoryGateDerivedStore())
        {
            GateStoreRunAuthoritySnapshot open = Open(prepareThenAbort, Inspection, RunA);
            PreparedGateReplacement desired = PrepareDesired(prepareThenAbort, open, "P");
            prepareThenAbort.DesiredReplacementControl.StopCurrentRun(open);
            Assert.AreEqual(GatePreparedReservationResultStatus.Aborted,
                AbortDesired(prepareThenAbort, desired).Status);
            Assert.IsNull(prepareThenAbort.ActiveView);
        }
    }

    [TestMethod]
    public async Task StopAndTransitionPrepareRespectOpenThenStoppedOrder()
    {
        await using var store = new InMemoryGateDerivedStore();
        GateStoreRunAuthoritySnapshot open = Open(store, Inspection, RunA);
        Assert.AreEqual(PrepareGateRunTransitionStatus.OutgoingNotStopped,
            store.DesiredReplacementControl.PrepareRunTransition(Request(open, Inspection, RunB)).Status);
        GateStoreRunAuthoritySnapshot stopped = store.DesiredReplacementControl.StopCurrentRun(open).Current;
        PreparedGateRunTransition prepared = Prepare(store, stopped, Inspection, RunB);
        Assert.IsTrue(store.DesiredReplacementControl.RunDiagnostics.HasPreparedTransition);
        Assert.AreEqual(PrepareGateRunTransitionStatus.AlreadyPrepared,
            store.DesiredReplacementControl.PrepareRunTransition(Request(stopped, Inspection, RunB)).Status);
    }

    [TestMethod]
    public async Task TransitionReplayCredentialPrecedenceAndEvictionAreDeterministic()
    {
        await using var store = new InMemoryGateDerivedStore(null, new(4, 4, 2));
        Assert.AreEqual(GatePreparedReservationResultStatus.InvalidReservation,
            store.DesiredReplacementControl.CommitRunTransition(default, default, default).Status);
        PreparedGateRunTransition first = Prepare(store, null, Inspection, RunA);
        var wrongOwner = new GatePreparedReservationOwner(Guid.NewGuid());
        Assert.AreEqual(GatePreparedReservationResultStatus.OwnerOrGenerationMismatch,
            store.DesiredReplacementControl.CommitRunTransition(first.Reservation, wrongOwner,
                first.Reservation.Generation).Status);
        var wrongGeneration = new GatePreparedOperationGeneration(first.Reservation.Generation.Value + 1);
        Assert.AreEqual(GatePreparedReservationResultStatus.OwnerOrGenerationMismatch,
            store.DesiredReplacementControl.CommitRunTransition(first.Reservation, first.Reservation.Owner,
                wrongGeneration).Status);
        Abort(store, first);
        GateStoreRunAuthoritySnapshot openA = Open(store, Inspection, RunA);
        GateStoreRunAuthoritySnapshot stoppedA = store.DesiredReplacementControl.StopCurrentRun(openA).Current;
        PreparedGateRunTransition second = Prepare(store, stoppedA, Inspection, RunB); Commit(store, second);
        GateStoreRunAuthoritySnapshot stoppedB = store.DesiredReplacementControl.StopCurrentRun(second.Incoming).Current;
        PreparedGateRunTransition third = Prepare(store, stoppedB, Inspection, RunA); Abort(store, third);

        Assert.AreEqual(2, store.DesiredReplacementControl.RunDiagnostics.TerminalTransitionTombstones);
        Assert.AreEqual(GatePreparedReservationResultStatus.InvalidReservation, Commit(store, first).Status);
        Assert.AreEqual(GatePreparedReservationResultStatus.AlreadyCommitted, Commit(store, second).Status);
        Assert.AreEqual(GatePreparedReservationResultStatus.AlreadyCommitted, Abort(store, second).Status);
        Assert.AreEqual(GatePreparedReservationResultStatus.AlreadyAborted, Abort(store, third).Status);
        Assert.AreEqual(GatePreparedReservationResultStatus.AlreadyAborted, Commit(store, third).Status);
        Assert.AreEqual(GatePreparedReservationResultStatus.OwnerOrGenerationMismatch,
            store.DesiredReplacementControl.CommitRunTransition(third.Reservation, wrongOwner,
                third.Reservation.Generation).Status);
        Assert.AreEqual(GatePreparedReservationResultStatus.OwnerOrGenerationMismatch,
            store.DesiredReplacementControl.CommitRunTransition(third.Reservation, third.Reservation.Owner,
                new(third.Reservation.Generation.Value + 1)).Status);
        Assert.AreEqual(GatePreparedReservationResultStatus.InvalidReservation,
            store.DesiredReplacementControl.AbortRunTransition(new(new(Guid.NewGuid()), Owner,
                new(100), GatePreparedReservationState.Prepared), Owner, new(100)).Status);
    }

    [TestMethod]
    public async Task CommitAbortRaceHasExactlyOneFirstConsumption()
    {
        await using var store = new InMemoryGateDerivedStore();
        PreparedGateRunTransition prepared = Prepare(store, null, Inspection, RunA);
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
        Assert.IsFalse(store.DesiredReplacementControl.RunDiagnostics.HasPreparedTransition);
    }

    [TestMethod]
    public async Task CommitAndDisposeRaceIsLinearizableAndAlwaysClearsAuthority()
    {
        var store = new InMemoryGateDerivedStore();
        PreparedGateRunTransition prepared = Prepare(store, null, Inspection, RunA);
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
        Assert.AreEqual(GateStoreRunLifecycle.None,
            store.DesiredReplacementControl.RunDiagnostics.Current.Lifecycle);
        Assert.IsFalse(store.DesiredReplacementControl.RunDiagnostics.HasPreparedTransition);
    }

    [TestMethod]
    public async Task DisposeAbortsTransitionClearsAuthorityAndClosesControlPlane()
    {
        var store = new InMemoryGateDerivedStore();
        PreparedGateRunTransition prepared = Prepare(store, null, Inspection, RunA);
        await store.DisposeAsync();
        await store.DisposeAsync();

        Assert.AreEqual(GateStoreRunLifecycle.None,
            store.DesiredReplacementControl.RunDiagnostics.Current.Lifecycle);
        Assert.AreEqual(GatePreparedReservationResultStatus.AlreadyAborted, Abort(store, prepared).Status);
        Assert.AreEqual(PrepareGateRunTransitionStatus.Closed,
            store.DesiredReplacementControl.PrepareRunTransition(Request(null, Inspection, RunA)).Status);
        Assert.AreEqual(GateStoreRunStopStatus.Closed,
            store.DesiredReplacementControl.StopCurrentRun(prepared.Incoming).Status);
    }

    [TestMethod]
    public async Task AbortAndDisposeHaveDeterministicOutcomesInBothOrders()
    {
        var abortFirst = new InMemoryGateDerivedStore();
        PreparedGateRunTransition aborted = Prepare(abortFirst, null, Inspection, RunA);
        Assert.AreEqual(GatePreparedReservationResultStatus.Aborted, Abort(abortFirst, aborted).Status);
        await abortFirst.DisposeAsync();
        Assert.AreEqual(GatePreparedReservationResultStatus.AlreadyAborted, Abort(abortFirst, aborted).Status);
        Assert.AreEqual(GateStoreRunLifecycle.None,
            abortFirst.DesiredReplacementControl.RunDiagnostics.Current.Lifecycle);

        var disposeFirst = new InMemoryGateDerivedStore();
        PreparedGateRunTransition disposed = Prepare(disposeFirst, null, Inspection, RunA);
        await disposeFirst.DisposeAsync();
        Assert.AreEqual(GatePreparedReservationResultStatus.AlreadyAborted, Abort(disposeFirst, disposed).Status);
        Assert.AreEqual(GateStoreRunLifecycle.None,
            disposeFirst.DesiredReplacementControl.RunDiagnostics.Current.Lifecycle);
    }

    [TestMethod]
    public async Task VisibilityTracksPhysicalViewAtCommitAndOldRunCannotBecomeVisible()
    {
        await using var store = new InMemoryGateDerivedStore();
        await Activate(store, "legacy-1");
        PreparedGateRunTransition first = Prepare(store, null, TraceInspection(), TraceRun());
        await Activate(store, "legacy-2");
        Commit(store, first);
        Assert.AreEqual(new DerivedViewId("legacy-2"), store.ActiveView!.ViewId,
            "First-open visibility must be decided from the physical view present at Commit.");
        using IGateDerivedReadLease lease = store.OpenActiveView();
        PreparedGateReplacement stoppedDesired = PrepareDesired(store, first.Incoming, "stopped");

        GateStoreRunAuthoritySnapshot stopped = store.DesiredReplacementControl
            .StopCurrentRun(first.Incoming).Current;
        Assert.IsNull(store.ActiveView);
        Assert.ThrowsExactly<InvalidOperationException>(() => store.OpenActiveView());
        Assert.AreEqual(GatePreparedReservationResultStatus.Committed,
            CommitDesired(store, stoppedDesired).Status);
        Assert.AreEqual(new DerivedViewId("legacy-2"),
            store.DesiredReplacementControl.FindInstalled(stoppedDesired.Key)!.Predecessor!.ViewId);
        Assert.AreEqual(new DerivedViewId("legacy-2"), lease.View.ViewId);
        GateDerivedStreamCoverage coverage = lease.View.Manifest.Streams.Single();
        GateDerivedRangePage page = lease.ReadRange(new(lease.View.ViewId, coverage.Stream, 7, 7,
            new("A"), [GateMeasureKind.Amplitude], 1, GateDerivedNoDataMode.Omit));
        Assert.HasCount(1, page.Items);
        DerivedStoreRevision stoppedRevision = store.Revision;
        PreparedGateRunTransition aborted = Prepare(store, stopped, Inspection, RunA);
        Abort(store, aborted);
        Assert.AreEqual(stopped, store.DesiredReplacementControl.RunDiagnostics.Current);
        Assert.IsNull(store.ActiveView);
        Assert.AreEqual(stoppedRevision, store.Revision);
        Assert.AreEqual(new DerivedViewId("legacy-2"), lease.View.ViewId);

        await Activate(store, "stopped-finalization");
        Assert.IsNull(store.ActiveView, "Stopped authority must hide every later activation.");
        PreparedGateRunTransition next = Prepare(store, stopped, Inspection, RunA);
        Commit(store, next);
        Assert.IsNull(store.ActiveView);
        PreparedGateReplacement desired = PrepareDesired(store, next.Incoming, "P");
        CommitDesired(store, desired);
        Assert.AreEqual(new DerivedViewId("stopped-finalization"),
            store.DesiredReplacementControl.FindInstalled(desired.Key)!.Predecessor!.ViewId,
            "Desired replacement must capture the retained physical predecessor.");

        await Activate(store, "old-run-finalization");
        Assert.IsNull(store.ActiveView, "An activation from a non-current run must remain physically hidden.");
    }

    [TestMethod]
    public async Task LateActivateOnCompleteAdmittedBeforeTransitionCannotRepublishOutgoingRun()
    {
        await using var store = new InMemoryGateDerivedStore();
        GateDerivedCoverageManifest manifest = await PrepareActivation(store);
        GateStoreRunAuthoritySnapshot open = Open(store, TraceInspection(), TraceRun());
        var admitted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<GateDerivedMutationResult> late = ActivateOnCompleteAsync();
        await admitted.Task;

        GateStoreRunAuthoritySnapshot stopped = store.DesiredReplacementControl.StopCurrentRun(open).Current;
        Commit(store, Prepare(store, stopped, Inspection, RunA));
        release.SetResult();
        GateDerivedMutationResult result = await late;

        Assert.AreEqual(GateDerivedMutationStatus.Activated, result.Status);
        Assert.IsNull(store.ActiveView);
        Assert.ThrowsExactly<InvalidOperationException>(() => store.OpenActiveView());

        async Task<GateDerivedMutationResult> ActivateOnCompleteAsync()
        {
            admitted.SetResult();
            await release.Task;
            return await store.ActivateAsync(manifest, new("late-outgoing"));
        }
    }

    [TestMethod]
    public async Task OldHandlesCannotConsumePreparedOperationOfOtherKind()
    {
        await using (var desiredThenTransition = new InMemoryGateDerivedStore())
        {
            GateStoreRunAuthoritySnapshot open = Open(desiredThenTransition, Inspection, RunA);
            PreparedGateReplacement oldDesired = PrepareDesired(desiredThenTransition, open, "P");
            AbortDesired(desiredThenTransition, oldDesired);
            GateStoreRunAuthoritySnapshot stopped = desiredThenTransition.DesiredReplacementControl
                .StopCurrentRun(open).Current;
            PreparedGateRunTransition currentTransition = Prepare(
                desiredThenTransition, stopped, Inspection, RunB);

            Assert.AreEqual(GatePreparedReservationResultStatus.AlreadyAborted,
                CommitDesired(desiredThenTransition, oldDesired).Status);
            Assert.IsTrue(desiredThenTransition.DesiredReplacementControl.RunDiagnostics.HasPreparedTransition);
            Assert.AreEqual(GatePreparedReservationResultStatus.Aborted,
                Abort(desiredThenTransition, currentTransition).Status);
        }

        await using (var transitionThenDesired = new InMemoryGateDerivedStore())
        {
            PreparedGateRunTransition oldTransition = Prepare(transitionThenDesired, null, Inspection, RunA);
            Commit(transitionThenDesired, oldTransition);
            PreparedGateReplacement currentDesired = PrepareDesired(
                transitionThenDesired, oldTransition.Incoming, "P");

            Assert.AreEqual(GatePreparedReservationResultStatus.AlreadyCommitted,
                Abort(transitionThenDesired, oldTransition).Status);
            Assert.IsTrue(transitionThenDesired.DesiredReplacementControl.Diagnostics.HasPreparedReservation);
            Assert.AreEqual(GatePreparedReservationResultStatus.Aborted,
                AbortDesired(transitionThenDesired, currentDesired).Status);
        }


        await using (var evictedDesired = new InMemoryGateDerivedStore(null, new(4, 1, 4)))
        {
            GateStoreRunAuthoritySnapshot open = Open(evictedDesired, Inspection, RunA);
            PreparedGateReplacement old = PrepareDesired(evictedDesired, open, "P1");
            AbortDesired(evictedDesired, old);
            PreparedGateReplacement replacement = PrepareDesired(evictedDesired, open, "P2");
            AbortDesired(evictedDesired, replacement);
            GateStoreRunAuthoritySnapshot stopped = evictedDesired.DesiredReplacementControl
                .StopCurrentRun(open).Current;
            PreparedGateRunTransition current = Prepare(evictedDesired, stopped, Inspection, RunB);
            Assert.AreEqual(GatePreparedReservationResultStatus.InvalidReservation,
                CommitDesired(evictedDesired, old).Status);
            Assert.IsTrue(evictedDesired.DesiredReplacementControl.RunDiagnostics.HasPreparedTransition);
            Abort(evictedDesired, current);
        }

        await using (var evictedTransition = new InMemoryGateDerivedStore(null, new(4, 4, 1)))
        {
            PreparedGateRunTransition old = Prepare(evictedTransition, null, Inspection, RunA);
            Commit(evictedTransition, old);
            GateStoreRunAuthoritySnapshot stopped = evictedTransition.DesiredReplacementControl
                .StopCurrentRun(old.Incoming).Current;
            PreparedGateRunTransition replacement = Prepare(evictedTransition, stopped, Inspection, RunB);
            Commit(evictedTransition, replacement);
            PreparedGateReplacement current = PrepareDesired(evictedTransition, replacement.Incoming, "P");
            Assert.AreEqual(GatePreparedReservationResultStatus.InvalidReservation,
                Abort(evictedTransition, old).Status);
            Assert.IsTrue(evictedTransition.DesiredReplacementControl.Diagnostics.HasPreparedReservation);
            AbortDesired(evictedTransition, current);
        }
    }

    private static PrepareGateRunTransitionRequest Request(GateStoreRunAuthoritySnapshot? outgoing,
        InspectionId inspection, AcquisitionRunId run) => new(outgoing, inspection, run, Owner);

    private static PreparedGateRunTransition Prepare(InMemoryGateDerivedStore store,
        GateStoreRunAuthoritySnapshot? outgoing, InspectionId inspection, AcquisitionRunId run)
    {
        PrepareGateRunTransitionResult result = store.DesiredReplacementControl.PrepareRunTransition(
            Request(outgoing, inspection, run));
        Assert.AreEqual(PrepareGateRunTransitionStatus.Prepared, result.Status);
        return result.Prepared;
    }

    private static GateStoreRunAuthoritySnapshot Open(InMemoryGateDerivedStore store,
        InspectionId inspection, AcquisitionRunId run)
    {
        PreparedGateRunTransition prepared = Prepare(store, null, inspection, run);
        Commit(store, prepared);
        return prepared.Incoming;
    }

    private static GatePreparedReservationResult Commit(InMemoryGateDerivedStore store,
        PreparedGateRunTransition prepared) => store.DesiredReplacementControl.CommitRunTransition(
            prepared.Reservation, prepared.Reservation.Owner, prepared.Reservation.Generation);

    private static GatePreparedReservationResult Abort(InMemoryGateDerivedStore store,
        PreparedGateRunTransition prepared) => store.DesiredReplacementControl.AbortRunTransition(
            prepared.Reservation, prepared.Reservation.Owner, prepared.Reservation.Generation);

    private static PrepareDesiredReplacementRequest DesiredRequest(GateStoreRunAuthoritySnapshot run,
        string plan) => new(run.InspectionId, run.RunId, run.Generation, new(plan), new(1), Owner);

    private static PreparedGateReplacement PrepareDesired(InMemoryGateDerivedStore store,
        GateStoreRunAuthoritySnapshot run, string plan)
    {
        PrepareDesiredReplacementResult result = store.DesiredReplacementControl.Prepare(DesiredRequest(run, plan));
        Assert.AreEqual(PrepareDesiredReplacementStatus.Prepared, result.Status);
        return result.Prepared;
    }

    private static GatePreparedReservationResult CommitDesired(InMemoryGateDerivedStore store,
        PreparedGateReplacement prepared) => store.DesiredReplacementControl.Commit(prepared.Reservation,
            prepared.Reservation.Owner, prepared.Reservation.Generation);

    private static GatePreparedReservationResult AbortDesired(InMemoryGateDerivedStore store,
        PreparedGateReplacement prepared) => store.DesiredReplacementControl.Abort(prepared.Reservation,
            prepared.Reservation.Owner, prepared.Reservation.Generation);

    private static InspectionId TraceInspection() =>
        new(Guid.Parse("10000000-0000-0000-0000-000000000001"));

    private static AcquisitionRunId TraceRun() =>
        new(Guid.Parse("20000000-0000-0000-0000-000000000002"));

    private static async Task Activate(InMemoryGateDerivedStore store, string viewId)
    {
        GateDerivedCoverageManifest manifest = await PrepareActivation(store);
        Assert.AreEqual(GateDerivedMutationStatus.Activated,
            store.Activate(manifest, new DerivedViewId(viewId)).Status);
    }

    private static async Task<GateDerivedCoverageManifest> PrepareActivation(InMemoryGateDerivedStore store)
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
        return manifest;
    }
}
