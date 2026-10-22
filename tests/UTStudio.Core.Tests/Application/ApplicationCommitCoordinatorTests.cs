using UTStudio.Application;
using UTStudio.Contracts.Acquisition;
using UTStudio.Contracts.Application;
using UTStudio.Contracts.Inspection;
using UTStudio.Domain.Acquisition;
using UTStudio.Domain.Inspection;

namespace UTStudio.Core.Tests.Application;

[TestClass]
public sealed class ApplicationCommitCoordinatorTests
{
    private static readonly InspectionId Inspection = new(Guid.Parse("82000000-0000-0000-0000-000000000001"));
    private static readonly AcquisitionRunId Run = new(Guid.Parse("83000000-0000-0000-0000-000000000001"));
    private static readonly ConventionalAcquisitionConfiguration Configuration =
        new(new PhysicalChannelId(0), 8, 50_000_000, firstSampleOffsetSeconds: 0.000_001);

    [TestMethod]
    public async Task FourAdmissionsExecuteInOrdinalFifoWithoutBarging()
    {
        await using var coordinator = new ApplicationCommitCoordinator(Inspection, 4);
        var entered = Signal(); var release = Signal(); var events = new List<string>();
        ApplicationCommitAdmission a = coordinator.AdmitCommit(Run, ApplicationCommitOperationClass.RfCommit, async () =>
        { events.Add("A"); entered.TrySetResult(); await release.Task; return Committed(1); });
        await entered.Task;
        ApplicationCommitAdmission b = Admit(coordinator, "B", 2, events);
        ApplicationCommitAdmission c = Admit(coordinator, "C", 3, events);
        ApplicationCommitAdmission? d = null;
        await Task.Run(() => d = Admit(coordinator, "D", 4, events));

        CollectionAssert.AreEqual(new ulong[] { 1, 2, 3, 4 },
            new[] { a.AdmissionOrdinal, b.AdmissionOrdinal, c.AdmissionOrdinal, d!.AdmissionOrdinal });
        release.TrySetResult();
        await Task.WhenAll(a.Completion, b.Completion, c.Completion, d.Completion);
        CollectionAssert.AreEqual(new[] { "A", "B", "C", "D" }, events);
        ApplicationCommitCoordinatorSnapshot snapshot = coordinator.Snapshot;
        Assert.AreEqual((ulong)4, snapshot.LastTerminalOrdinal);
        Assert.AreEqual(ApplicationCommitCoordinatorLifecycle.Accepting, snapshot.Lifecycle);
    }

    [TestMethod]
    [DataRow("RfCommit", "GapCommit")]
    [DataRow("GapCommit", "RfCommit")]
    public async Task RfAndGapPreserveAdmissionOrder(string firstName, string secondName)
    {
        ApplicationCommitOperationClass firstKind = Enum.Parse<ApplicationCommitOperationClass>(firstName);
        ApplicationCommitOperationClass secondKind = Enum.Parse<ApplicationCommitOperationClass>(secondName);
        await using var coordinator = new ApplicationCommitCoordinator(Inspection, 2);
        var entered = Signal(); var release = Signal(); var events = new List<ApplicationCommitOperationClass>();
        ApplicationCommitAdmission firstAdmission = coordinator.AdmitCommit(Run, firstKind, async () =>
        { events.Add(firstKind); entered.TrySetResult(); await release.Task; return Committed(1); });
        await entered.Task;
        ApplicationCommitAdmission secondAdmission = coordinator.AdmitCommit(Run, secondKind, () =>
        { events.Add(secondKind); return ValueTask.FromResult(Committed(2)); });
        release.TrySetResult(); await firstAdmission.Completion; await secondAdmission.Completion;
        CollectionAssert.AreEqual(new[] { firstKind, secondKind }, events);
    }

    [TestMethod]
    public async Task CancellationBeforeAdmissionDoesNotAssignOrdinalOrInvokeWriter()
    {
        await using var coordinator = new ApplicationCommitCoordinator(Inspection, 1);
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        bool invoked = false;
        Assert.ThrowsExactly<OperationCanceledException>(() => coordinator.AdmitCommit(Run, ApplicationCommitOperationClass.RfCommit,
            () => { invoked = true; return ValueTask.FromResult(Committed(1)); }, cancellation.Token));
        Assert.IsFalse(invoked);
        Assert.AreEqual((ulong)0, coordinator.Snapshot.LastAdmissionOrdinal);
    }

    [TestMethod]
    public async Task CancellationAfterAdmissionCancelsOnlyWaitAndSlotRemainsOwnedUntilTerminal()
    {
        await using var coordinator = new ApplicationCommitCoordinator(Inspection, 1);
        var entered = Signal(); var release = Signal();
        ApplicationCommitAdmission admission = coordinator.AdmitCommit(Run, ApplicationCommitOperationClass.RfCommit, async () =>
        { entered.TrySetResult(); await release.Task; return Committed(1); });
        await entered.Task;
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsExactlyAsync<TaskCanceledException>(() => admission.WaitAsync(cancellation.Token));
        Assert.AreEqual(1, coordinator.Snapshot.Occupancy);
        Assert.ThrowsExactly<InvalidOperationException>(() => coordinator.AdmitCommit(Run, ApplicationCommitOperationClass.RfCommit,
            () => ValueTask.FromResult(Committed(2))));
        release.TrySetResult();
        ApplicationCommitTerminalResult terminal = await admission.Completion;
        Assert.AreEqual(ApplicationCommitTerminalStatus.Confirmed, terminal.Status);
        Assert.AreEqual(0, coordinator.Snapshot.Occupancy);
    }

    [TestMethod]
    public async Task CancellationAndAdmissionRaceHasExactlyOneLinearizableOutcome()
    {
        var coordinator = new ApplicationCommitCoordinator(Inspection, 1);
        using var cancellation = new CancellationTokenSource();
        using var start = new Barrier(3);
        var release = Signal();
        int writerInvocations = 0;
        Task<ApplicationCommitAdmission?> admitting = Task.Run(() =>
        {
            start.SignalAndWait();
            try
            {
                return coordinator.AdmitCommit(Run, ApplicationCommitOperationClass.RfCommit, async () =>
                {
                    Interlocked.Increment(ref writerInvocations);
                    await release.Task;
                    return Committed(1);
                }, cancellation.Token);
            }
            catch (OperationCanceledException) { return null; }
        });
        Task cancelling = Task.Run(() => { start.SignalAndWait(); cancellation.Cancel(); });
        start.SignalAndWait();
        ApplicationCommitAdmission? admission = await admitting;
        await cancelling;

        if (admission is null)
        {
            Assert.AreEqual(0, Volatile.Read(ref writerInvocations));
            Assert.AreEqual((ulong)0, coordinator.Snapshot.LastAdmissionOrdinal);
            Assert.AreEqual(0, coordinator.Snapshot.Occupancy);
        }
        else
        {
            Assert.AreEqual(1, Volatile.Read(ref writerInvocations));
            await Assert.ThrowsExactlyAsync<TaskCanceledException>(() => admission.WaitAsync(cancellation.Token));
            Assert.AreEqual(1, coordinator.Snapshot.Occupancy);
            release.TrySetResult();
            Assert.AreEqual(ApplicationCommitTerminalStatus.Confirmed, (await admission.Completion).Status);
            Assert.AreEqual(0, coordinator.Snapshot.Occupancy);
        }

        release.TrySetResult();
        await coordinator.DisposeAsync();
        Assert.AreEqual(ApplicationCommitCoordinatorLifecycle.Closed, coordinator.Snapshot.Lifecycle);
    }

    [TestMethod]
    [Timeout(5000, CooperativeCancellation = true)]
    public async Task SynchronousWriterStartsBeforeAdmissionReturnsButDoesNotHoldCoordinatorGate()
    {
        var coordinator = new ApplicationCommitCoordinator(Inspection, 1);
        var writerEntered = Signal();
        var closeCompleted = Signal();
        Task<ApplicationCommitAdmission> admitting = Task.Run(() => coordinator.AdmitCommit(Run,
            ApplicationCommitOperationClass.RfCommit, () =>
            {
                writerEntered.TrySetResult();
                closeCompleted.Task.GetAwaiter().GetResult();
                return ValueTask.FromResult(Committed(1));
            }));

        await writerEntered.Task;
        Assert.IsFalse(admitting.IsCompleted);
        await Task.Run(() => { coordinator.CloseAdmission(); closeCompleted.TrySetResult(); });
        ApplicationCommitAdmission admission = await admitting;
        Assert.AreEqual(ApplicationCommitTerminalStatus.Confirmed, (await admission.Completion).Status);
        await coordinator.DisposeAsync();
    }

    [TestMethod]
    public async Task WriterFailureIsTerminalAndNextAdmissionStillExecutes()
    {
        await using var coordinator = new ApplicationCommitCoordinator(Inspection, 2);
        var entered = Signal(); var release = Signal();
        var error = new InvalidOperationException("writer");
        ApplicationCommitAdmission first = coordinator.AdmitCommit(Run, ApplicationCommitOperationClass.RfCommit, async () =>
        { entered.TrySetResult(); await release.Task; throw error; });
        await entered.Task;
        ApplicationCommitAdmission second = coordinator.AdmitCommit(Run, ApplicationCommitOperationClass.RfCommit,
            () => ValueTask.FromResult(Committed(1)));
        release.TrySetResult();
        ApplicationCommitTerminalResult failed = await first.Completion;
        ApplicationCommitTerminalResult succeeded = await second.Completion;
        Assert.AreEqual(ApplicationCommitTerminalStatus.WriterFaulted, failed.Status);
        Assert.AreSame(error, failed.Error);
        Assert.AreEqual(ApplicationCommitTerminalStatus.Confirmed, succeeded.Status);
    }

    [TestMethod]
    public async Task RevisionPolicyAcceptsGrowthRejectsRegressionAndThenProgresses()
    {
        await using var coordinator = new ApplicationCommitCoordinator(Inspection, 1);
        Assert.AreEqual(ApplicationCommitTerminalStatus.Confirmed, (await Commit(coordinator, 1)).Status);
        Assert.AreEqual(ApplicationCommitTerminalStatus.Confirmed, (await Commit(coordinator, 2)).Status);
        ApplicationCommitTerminalResult regression = await Commit(coordinator, 1);
        Assert.AreEqual(ApplicationCommitTerminalStatus.RevisionRegression, regression.Status);
        Assert.AreEqual(new InspectionRevision(2), regression.PreviousRevision);
        Assert.AreEqual(new InspectionRevision(1), regression.ObservedRevision);
        Assert.AreEqual(ApplicationCommitTerminalStatus.Confirmed, (await Commit(coordinator, 3)).Status);
        Assert.AreEqual(new InspectionRevision(3), coordinator.Snapshot.LastConfirmedRevision);
    }

    [TestMethod]
    public async Task EveryUnverifiableDuplicateIsRejectedWithoutMovingFrontierAndGrowthStillProgresses()
    {
        await using var coordinator = new ApplicationCommitCoordinator(Inspection, 1);
        _ = await Commit(coordinator, 2);
        ApplicationCommitTerminalResult equalCommit = await Commit(coordinator, 2);
        ApplicationCommitTerminalResult lowerDuplicate = await coordinator.AdmitCommit(Run, ApplicationCommitOperationClass.RfCommit,
            () => ValueTask.FromResult(Duplicate(1))).Completion;
        ApplicationCommitTerminalResult equalDuplicate = await coordinator.AdmitCommit(Run, ApplicationCommitOperationClass.RfCommit,
            () => ValueTask.FromResult(Duplicate(2))).Completion;
        ApplicationCommitTerminalResult higherDuplicate = await coordinator.AdmitCommit(Run, ApplicationCommitOperationClass.RfCommit,
            () => ValueTask.FromResult(Duplicate(3))).Completion;
        Assert.AreEqual(ApplicationCommitTerminalStatus.NonAdvancingRevision, equalCommit.Status);
        Assert.AreEqual(ApplicationCommitTerminalStatus.DuplicateUnverifiable, lowerDuplicate.Status);
        Assert.AreEqual(ApplicationCommitTerminalStatus.DuplicateUnverifiable, equalDuplicate.Status);
        Assert.AreEqual(ApplicationCommitTerminalStatus.DuplicateUnverifiable, higherDuplicate.Status);
        Assert.AreEqual(new InspectionRevision(2), coordinator.Snapshot.LastConfirmedRevision);
        Assert.AreEqual(ApplicationCommitTerminalStatus.Confirmed, (await Commit(coordinator, 3)).Status);
    }

    [TestMethod]
    public async Task ThrowingSessionObserverIsDetachedAndLaterCommitStillProgresses()
    {
        await using var source = new ManualSessionSource();
        var writer = new ScriptedWriter(Run, Committed(1), Committed(2), Committed(3));
        await using var session = new ApplicationSession(source, new InspectionId(Guid.NewGuid()), _ => writer);
        var observerFailed = Signal();
        int failures = 0;
        using IDisposable subscription = session.Subscribe(new SnapshotObserver(snapshot =>
        {
            if (snapshot.LastSequence != 2 || Interlocked.Exchange(ref failures, 1) != 0) return;
            observerFailed.TrySetResult();
            throw new InvalidOperationException("observer-publication");
        }));

        await session.StartAsync(Configuration, Run);
        ManualSessionSource.CountingOwner first = source.QueueFrame(0);
        await first.Returned.Task;
        ManualSessionSource.CountingOwner publishing = source.QueueFrame(2);
        await publishing.Returned.Task;
        await observerFailed.Task;
        ManualSessionSource.CountingOwner later = source.QueueFrame(3);
        await later.Returned.Task;
        await session.StopAsync();

        Assert.AreEqual(1, Volatile.Read(ref failures));
        Assert.AreEqual((ulong)3, session.Snapshot.LastSequence);
        Assert.AreEqual(3L, session.Snapshot.ReleasedFrames);
        Assert.AreEqual(1, first.DisposeCount);
        Assert.AreEqual(1, publishing.DisposeCount);
        Assert.AreEqual(1, later.DisposeCount);
        Assert.AreEqual(InspectionRunState.Sealed, writer.State);
    }

    [TestMethod]
    public async Task CapacityIncludesRunnerAndQueueAndRecordsOneOverRejection()
    {
        await using var coordinator = new ApplicationCommitCoordinator(Inspection, 2);
        var entered = Signal(); var release = Signal();
        ApplicationCommitAdmission first = coordinator.AdmitCommit(Run, ApplicationCommitOperationClass.RfCommit, async () =>
        { entered.TrySetResult(); await release.Task; return Committed(1); });
        await entered.Task;
        ApplicationCommitAdmission second = coordinator.AdmitCommit(Run, ApplicationCommitOperationClass.GapCommit,
            () => ValueTask.FromResult(Committed(2)));
        Assert.ThrowsExactly<InvalidOperationException>(() => coordinator.AdmitCommit(Run, ApplicationCommitOperationClass.RfCommit,
            () => ValueTask.FromResult(Committed(3))));
        ApplicationCommitCoordinatorSnapshot full = coordinator.Snapshot;
        Assert.AreEqual(2, full.Occupancy); Assert.AreEqual(1, full.Queued);
        Assert.AreEqual((ulong)1,
            full.CapacityRejections.For(ApplicationCommitOperationClass.RfCommit));
        release.TrySetResult(); await first.Completion; await second.Completion;
    }

    [TestMethod]
    public async Task EnqueueBeforeCloseIsAdmittedAndDrained()
    {
        var coordinator = new ApplicationCommitCoordinator(Inspection, 1);
        var entered = Signal(); var release = Signal();
        ApplicationCommitAdmission admitted = coordinator.AdmitCommit(Run, ApplicationCommitOperationClass.RfCommit, async () =>
        { entered.TrySetResult(); await release.Task; return Committed(1); });
        await entered.Task; coordinator.CloseAdmission();
        Assert.AreEqual(ApplicationCommitCoordinatorLifecycle.Closing, coordinator.Snapshot.Lifecycle);
        release.TrySetResult(); await admitted.Completion; await coordinator.DisposeAsync();
        Assert.AreEqual(ApplicationCommitCoordinatorLifecycle.Closed, coordinator.Snapshot.Lifecycle);
    }

    [TestMethod]
    public async Task CloseBeforeEnqueueRejectsAndCountsStableClosedRejection()
    {
        var coordinator = new ApplicationCommitCoordinator(Inspection, 1);
        coordinator.CloseAdmission(); coordinator.CloseAdmission();
        Assert.ThrowsExactly<ObjectDisposedException>(() => coordinator.AdmitCommit(Run, ApplicationCommitOperationClass.RfCommit,
            () => ValueTask.FromResult(Committed(1))));
        Assert.AreEqual((ulong)1,
            coordinator.Snapshot.ClosedRejections.For(ApplicationCommitOperationClass.RfCommit));
        await coordinator.DisposeAsync(); await coordinator.DisposeAsync();
    }

    [TestMethod]
    public async Task CloseAndEnqueueRaceHasExactlyOneLinearizableOutcome()
    {
        var coordinator = new ApplicationCommitCoordinator(Inspection, 1);
        using var start = new Barrier(3);
        var release = Signal();
        int writerInvocations = 0;
        Task<ApplicationCommitAdmission?> admitting = Task.Run(() =>
        {
            start.SignalAndWait();
            try
            {
                return coordinator.AdmitCommit(Run, ApplicationCommitOperationClass.RfCommit, async () =>
                {
                    Interlocked.Increment(ref writerInvocations);
                    await release.Task;
                    return Committed(1);
                });
            }
            catch (ObjectDisposedException) { return null; }
        });
        Task closing = Task.Run(() => { start.SignalAndWait(); coordinator.CloseAdmission(); });
        start.SignalAndWait();
        ApplicationCommitAdmission? admission = await admitting;
        await closing;

        if (admission is null)
        {
            Assert.AreEqual(0, Volatile.Read(ref writerInvocations));
            Assert.AreEqual((ulong)0, coordinator.Snapshot.LastAdmissionOrdinal);
        }
        else
        {
            Assert.AreEqual(1, Volatile.Read(ref writerInvocations));
            release.TrySetResult();
            Assert.AreEqual(ApplicationCommitTerminalStatus.Confirmed, (await admission.Completion).Status);
        }

        release.TrySetResult();
        await coordinator.DisposeAsync();
        await coordinator.DisposeAsync();
        ApplicationCommitCoordinatorSnapshot closed = coordinator.Snapshot;
        Assert.AreEqual(ApplicationCommitCoordinatorLifecycle.Closed, closed.Lifecycle);
        Assert.AreEqual(0, closed.Occupancy);
        Assert.AreEqual(0, closed.Queued);
        Assert.ThrowsExactly<ObjectDisposedException>(() => coordinator.AdmitCommit(Run, ApplicationCommitOperationClass.RfCommit,
            () => ValueTask.FromResult(Committed(2))));
    }

    [TestMethod]
    public async Task RealCallbackAfterCompletionCanAdmitAndSynchronouslyWaitForNextCommit()
    {
        await using var coordinator = new ApplicationCommitCoordinator(Inspection, 1);
        ApplicationCommitAdmission first = coordinator.AdmitCommit(Run, ApplicationCommitOperationClass.RfCommit,
            () => ValueTask.FromResult(Committed(1)));
        _ = await first.Completion;
        ApplicationCommitTerminalResult second = await Task.Run(() => coordinator.AdmitCommit(Run,
            ApplicationCommitOperationClass.RfCommit, () => ValueTask.FromResult(Committed(2))).Completion.GetAwaiter().GetResult());
        Assert.AreEqual(ApplicationCommitTerminalStatus.Confirmed, second.Status);
        Assert.AreEqual((ulong)2, second.AdmissionOrdinal);
    }

    [TestMethod]
    public async Task DifferentRunIsRejectedBeforeWriterAndBeforeOrdinalAssignment()
    {
        await using var coordinator = new ApplicationCommitCoordinator(Inspection, 1);
        _ = await Commit(coordinator, 1);
        bool invoked = false;
        var other = new AcquisitionRunId(Guid.Parse("83000000-0000-0000-0000-000000000002"));
        Assert.ThrowsExactly<InvalidOperationException>(() => coordinator.AdmitCommit(other, ApplicationCommitOperationClass.RfCommit,
            () => { invoked = true; return ValueTask.FromResult(Committed(2)); }));
        Assert.IsFalse(invoked); Assert.AreEqual((ulong)1, coordinator.Snapshot.LastAdmissionOrdinal);
    }

    [TestMethod]
    public void CoordinatorTypesAreNotPartOfThePublicClrSurface()
    {
        Type[] internalTypes =
        [
            typeof(ApplicationCommitCoordinator), typeof(ApplicationCommitAdmission),
            typeof(ApplicationCommitCoordinatorLifecycle), typeof(ApplicationCommitCoordinatorSnapshot),
            typeof(ApplicationCommitTerminalResult), typeof(ApplicationCommitTerminalStatus),
            typeof(ApplicationCommitOperationClass), typeof(ApplicationCommitOperationIdentity),
            typeof(ApplicationCommitOperationDescriptor), typeof(ApplicationCommitClassCounters),
            typeof(ApplicationCommitCoordinatorTestSynchronization)
        ];
        Assert.IsTrue(internalTypes.All(type => !type.IsPublic && !type.IsNestedPublic));
    }

    [TestMethod]
    [DataRow("regression", 1UL)]
    [DataRow("duplicate", 3UL)]
    public async Task RejectedRevisionDoesNotReconcileAuthoritativeSessionState(string rejection, ulong revision)
    {
        await using var source = new ManualSessionSource();
        var writer = new ScriptedWriter(Run,
            Committed(2),
            rejection == "duplicate" ? Duplicate(revision) : Committed(revision));
        var session = new ApplicationSession(source, new InspectionId(Guid.NewGuid()), _ => writer);
        await session.StartAsync(Configuration, Run);
        ManualSessionSource.CountingOwner first = source.QueueFrame(0);
        await first.Returned.Task;
        ManualSessionSource.CountingOwner rejected = source.QueueFrame(1);
        await rejected.Returned.Task;

        Assert.AreEqual((ulong)0, session.Snapshot.LastSequence);
        Assert.AreEqual(2L, session.Snapshot.ReceivedFrames);
        InvalidOperationException error = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => session.DisposeAsync().AsTask());
        StringAssert.Contains(error.Message, rejection == "duplicate" ? "DuplicateUnverifiable" :
            "RevisionRegression");
        Assert.AreEqual((ulong)0, session.Snapshot.LastSequence);
        Assert.AreEqual(2L, session.Snapshot.ReleasedFrames);
    }

    [TestMethod]
    [Timeout(5000, CooperativeCancellation = true)]
    public async Task SessionPublicationCallbackCanQueueAndWaitForAnotherCommitWithoutDeadlock()
    {
        await using var source = new ManualSessionSource();
        var writer = new ScriptedWriter(Run, Committed(1), Committed(2), Committed(3));
        await using var session = new ApplicationSession(source, new InspectionId(Guid.NewGuid()), _ => writer);
        var callbackCompleted = Signal();
        int invoked = 0;
        using IDisposable subscription = session.Subscribe(new SnapshotObserver(snapshot =>
        {
            if (snapshot.LastSequence != 2 || Interlocked.Exchange(ref invoked, 1) != 0) return;
            ManualSessionSource.CountingOwner reentrant = source.QueueFrame(3);
            reentrant.Returned.Task.GetAwaiter().GetResult();
            callbackCompleted.TrySetResult();
        }));

        await session.StartAsync(Configuration, Run);
        ManualSessionSource.CountingOwner first = source.QueueFrame(0);
        await first.Returned.Task;
        ManualSessionSource.CountingOwner withGap = source.QueueFrame(2);
        await withGap.Returned.Task;
        await callbackCompleted.Task;
        await session.StopAsync();
        Assert.AreEqual((ulong)3, session.Snapshot.LastSequence);
        Assert.AreEqual(3L, session.Snapshot.ReleasedFrames);
    }

    [TestMethod]
    public async Task FiveMixedOperationsUseOneGlobalFifoAndOrdinalSequence()
    {
        await using var coordinator = new ApplicationCommitCoordinator(Inspection, 5);
        var entered = Signal(); var release = Signal(); var events = new List<string>();
        ApplicationCommitAdmission a = coordinator.AdmitCommit(Run, ApplicationCommitOperationClass.RfCommit,
            async () => { events.Add("A"); entered.SetResult(); await release.Task; return Committed(1); });
        await entered.Task;
        ApplicationCommitAdmission b = Apply(coordinator, "B", events);
        ApplicationCommitAdmission c = coordinator.AdmitCommit(Run, ApplicationCommitOperationClass.GapCommit,
            () => { events.Add("C"); return ValueTask.FromResult(Committed(2)); });
        ApplicationCommitAdmission d = Transition(coordinator, "D", events);
        ApplicationCommitAdmission e = coordinator.AdmitCommit(Run, ApplicationCommitOperationClass.RfCommit,
            () => { events.Add("E"); return ValueTask.FromResult(Committed(3)); });
        CollectionAssert.AreEqual(new ulong[] { 1, 2, 3, 4, 5 },
            new[] { a.AdmissionOrdinal, b.AdmissionOrdinal, c.AdmissionOrdinal, d.AdmissionOrdinal,
                e.AdmissionOrdinal });
        release.SetResult();
        await Task.WhenAll(a.Completion, b.Completion, c.Completion, d.Completion, e.Completion);
        CollectionAssert.AreEqual(new[] { "A", "B", "C", "D", "E" }, events);
        Assert.AreEqual(new InspectionRevision(3), coordinator.Snapshot.LastConfirmedRevision);
    }

    [TestMethod]
    public async Task ConcurrentSixthAdmissionCannotBargeAheadOfMixedQueue()
    {
        await using var coordinator = new ApplicationCommitCoordinator(Inspection, 6);
        var enteredA = Signal(); var releaseA = Signal(); var enteredB = Signal(); var releaseB = Signal();
        var events = new List<string>();
        ApplicationCommitAdmission a = coordinator.AdmitCommit(Run, ApplicationCommitOperationClass.RfCommit,
            async () => { events.Add("A"); enteredA.SetResult(); await releaseA.Task; return Committed(1); });
        await enteredA.Task;
        ApplicationCommitAdmission b = coordinator.AdmitGatePlanApply(new("B"), async () =>
        { events.Add("B"); enteredB.SetResult(); await releaseB.Task; });
        ApplicationCommitAdmission c = coordinator.AdmitCommit(Run, ApplicationCommitOperationClass.GapCommit,
            () => { events.Add("C"); return ValueTask.FromResult(Committed(2)); });
        ApplicationCommitAdmission d = Transition(coordinator, "D", events);
        ApplicationCommitAdmission e = coordinator.AdmitCommit(Run, ApplicationCommitOperationClass.RfCommit,
            () => { events.Add("E"); return ValueTask.FromResult(Committed(3)); });
        releaseA.SetResult(); await enteredB.Task;
        var sixthTaskStarted = Signal();
        Task<ApplicationCommitAdmission> admitSixth = Task.Run(() =>
        {
            sixthTaskStarted.SetResult();
            return coordinator.AdmitCommit(Run, ApplicationCommitOperationClass.RfCommit,
                () => { events.Add("F"); return ValueTask.FromResult(Committed(4)); });
        });
        await sixthTaskStarted.Task;
        ApplicationCommitAdmission f = await admitSixth;
        Assert.AreEqual((ulong)6, f.AdmissionOrdinal);
        CollectionAssert.AreEqual(new[] { "A", "B" }, events);
        releaseB.SetResult();
        await Task.WhenAll(a.Completion, b.Completion, c.Completion, d.Completion, e.Completion, f.Completion);
        CollectionAssert.AreEqual(new[] { "A", "B", "C", "D", "E", "F" }, events);
    }

    [TestMethod]
    public async Task IndependentAdmissionClosuresRejectOnlyTheirClasses()
    {
        await using var applyClosed = new ApplicationCommitCoordinator(Inspection, 4);
        applyClosed.CloseApplyAdmission(); applyClosed.CloseApplyAdmission();
        Assert.ThrowsExactly<InvalidOperationException>(() => Apply(applyClosed, "A", []));
        _ = await Commit(applyClosed, 1); await Transition(applyClosed, "T", []).Completion;
        Assert.IsFalse(applyClosed.Snapshot.ApplyAdmissionOpen);
        Assert.IsTrue(applyClosed.Snapshot.AcquisitionAdmissionOpen);

        await using var acquisitionClosed = new ApplicationCommitCoordinator(Inspection, 3);
        acquisitionClosed.CloseAcquisitionAdmission();
        Assert.ThrowsExactly<InvalidOperationException>(() => acquisitionClosed.AdmitCommit(Run,
            ApplicationCommitOperationClass.RfCommit, () => ValueTask.FromResult(Committed(1))));
        Assert.ThrowsExactly<InvalidOperationException>(() => acquisitionClosed.AdmitCommit(Run,
            ApplicationCommitOperationClass.GapCommit, () => ValueTask.FromResult(Committed(1))));
        await Apply(acquisitionClosed, "A", []).Completion;
        await Transition(acquisitionClosed, "T", []).Completion;

        await using var transitionClosed = new ApplicationCommitCoordinator(Inspection, 3);
        transitionClosed.CloseRunTransitionAdmission(); transitionClosed.CloseRunTransitionAdmission();
        Assert.ThrowsExactly<InvalidOperationException>(() => Transition(transitionClosed, "T", []));
        _ = await Commit(transitionClosed, 1); await Apply(transitionClosed, "A", []).Completion;
        Assert.AreEqual((ulong)1, transitionClosed.Snapshot.ClosedRejections.For(
            ApplicationCommitOperationClass.GateRunTransition));
    }

    [TestMethod]
    [DataRow("RfCommit")]
    [DataRow("GapCommit")]
    [DataRow("GatePlanApply")]
    [DataRow("GateRunTransition")]
    public async Task CancellationWinsBeforeAdmissionForEveryClassWithoutOrdinal(string className)
    {
        await using var coordinator = new ApplicationCommitCoordinator(Inspection, 1);
        using var cancellation = new CancellationTokenSource();
        var callerReady = Signal(); var attemptAdmission = Signal(); int invoked = 0;
        Task admitting = Task.Run(async () =>
        {
            callerReady.SetResult();
            await attemptAdmission.Task;
            Assert.ThrowsExactly<OperationCanceledException>(() => AdmitClass(coordinator,
                Enum.Parse<ApplicationCommitOperationClass>(className), () => Interlocked.Increment(ref invoked),
                cancellation.Token));
        });
        await callerReady.Task;
        cancellation.Cancel();
        attemptAdmission.SetResult();
        await admitting;
        Assert.AreEqual(0, invoked); Assert.AreEqual((ulong)0, coordinator.Snapshot.LastAdmissionOrdinal);
    }

    [TestMethod]
    [DataRow("RfCommit")]
    [DataRow("GapCommit")]
    [DataRow("GatePlanApply")]
    [DataRow("GateRunTransition")]
    public async Task AdmissionWinsBeforeCallerCancellationForEveryClass(string className)
    {
        await using var coordinator = new ApplicationCommitCoordinator(Inspection, 1);
        var entered = Signal(); var release = Signal();
        using var cancellation = new CancellationTokenSource();
        ApplicationCommitAdmission admission = AdmitBlockedClass(coordinator,
            Enum.Parse<ApplicationCommitOperationClass>(className), entered, release, cancellation.Token);
        await entered.Task;
        cancellation.Cancel();
        await Assert.ThrowsExactlyAsync<TaskCanceledException>(() => admission.WaitAsync(cancellation.Token));
        Assert.AreEqual(1, coordinator.Snapshot.Occupancy);
        release.SetResult();
        Assert.IsTrue((await admission.Completion).IsSuccessful);
        Assert.AreEqual(0, coordinator.Snapshot.Occupancy);
    }

    [TestMethod]
    public async Task MixedCapacityCountsRunnerAndQueueAndRejectsOneOverByClass()
    {
        await using var coordinator = new ApplicationCommitCoordinator(Inspection, 3);
        var entered = Signal(); var release = Signal();
        ApplicationCommitAdmission a = coordinator.AdmitCommit(Run, ApplicationCommitOperationClass.RfCommit,
            async () => { entered.SetResult(); await release.Task; return Committed(1); });
        await entered.Task;
        ApplicationCommitAdmission b = Apply(coordinator, "B", []);
        ApplicationCommitAdmission c = Transition(coordinator, "C", []);
        Assert.ThrowsExactly<InvalidOperationException>(() => coordinator.AdmitCommit(Run,
            ApplicationCommitOperationClass.GapCommit, () => ValueTask.FromResult(Committed(2))));
        Assert.AreEqual((ulong)1, coordinator.Snapshot.CapacityRejections.For(
            ApplicationCommitOperationClass.GapCommit));
        Assert.AreEqual((ulong)3, coordinator.Snapshot.LastAdmissionOrdinal);
        release.SetResult(); await Task.WhenAll(a.Completion, b.Completion, c.Completion);
    }

    [TestMethod]
    public async Task DisposeClosesAllAdmissionsAndDrainsMixedOperations()
    {
        var coordinator = new ApplicationCommitCoordinator(Inspection, 3);
        var entered = Signal(); var release = Signal();
        ApplicationCommitAdmission a = coordinator.AdmitCommit(Run, ApplicationCommitOperationClass.RfCommit,
            async () => { entered.SetResult(); await release.Task; return Committed(1); });
        await entered.Task;
        ApplicationCommitAdmission b = Apply(coordinator, "B", []);
        ApplicationCommitAdmission c = Transition(coordinator, "C", []);
        Task dispose = coordinator.DisposeAsync().AsTask();
        Assert.ThrowsExactly<ObjectDisposedException>(() => Apply(coordinator, "late", []));
        Assert.ThrowsExactly<ObjectDisposedException>(() => coordinator.AdmitCommit(Run,
            ApplicationCommitOperationClass.GapCommit, () => ValueTask.FromResult(Committed(2))));
        release.SetResult(); await Task.WhenAll(a.Completion, b.Completion, c.Completion); await dispose;
        Assert.AreEqual(ApplicationCommitCoordinatorLifecycle.Closed, coordinator.Snapshot.Lifecycle);
        Assert.AreEqual(0, coordinator.Snapshot.Occupancy);
    }

    [TestMethod]
    public async Task PlaceholderFailureIsTerminalAndRfFrontierStillProgresses()
    {
        await using var coordinator = new ApplicationCommitCoordinator(Inspection, 2);
        var entered = Signal(); var release = Signal(); var error = new InvalidOperationException("apply");
        ApplicationCommitAdmission apply = coordinator.AdmitGatePlanApply(new("apply"), async () =>
        { entered.SetResult(); await release.Task; throw error; });
        await entered.Task;
        ApplicationCommitAdmission rf = coordinator.AdmitCommit(Run, ApplicationCommitOperationClass.RfCommit,
            () => ValueTask.FromResult(Committed(1)));
        release.SetResult();
        Assert.AreEqual(ApplicationCommitTerminalStatus.OperationFaulted, (await apply.Completion).Status);
        Assert.AreSame(error, (await apply.Completion).Error);
        Assert.AreEqual(ApplicationCommitTerminalStatus.Confirmed, (await rf.Completion).Status);
    }

    [TestMethod]
    public async Task GapWriterFailureIsTerminalAndNextOperationStillProgresses()
    {
        await using var coordinator = new ApplicationCommitCoordinator(Inspection, 2);
        var entered = Signal(); var release = Signal(); var error = new InvalidOperationException("gap");
        ApplicationCommitAdmission gap = coordinator.AdmitCommit(Run, ApplicationCommitOperationClass.GapCommit,
            async () => { entered.SetResult(); await release.Task; throw error; });
        await entered.Task;
        ApplicationCommitAdmission rf = coordinator.AdmitCommit(Run, ApplicationCommitOperationClass.RfCommit,
            () => ValueTask.FromResult(Committed(1)));
        release.SetResult();
        ApplicationCommitTerminalResult failed = await gap.Completion;
        Assert.AreEqual(ApplicationCommitTerminalStatus.WriterFaulted, failed.Status);
        Assert.AreSame(error, failed.Error);
        Assert.AreEqual(ApplicationCommitTerminalStatus.Confirmed, (await rf.Completion).Status);
    }

    [TestMethod]
    public async Task RunTransitionFailureIsTerminalAndNextOperationStillProgresses()
    {
        await using var coordinator = new ApplicationCommitCoordinator(Inspection, 2);
        var entered = Signal(); var release = Signal(); var error = new InvalidOperationException("transition");
        ApplicationCommitAdmission transition = coordinator.AdmitGateRunTransition(Run, new("transition"),
            async () => { entered.SetResult(); await release.Task; throw error; });
        await entered.Task;
        ApplicationCommitAdmission rf = coordinator.AdmitCommit(Run, ApplicationCommitOperationClass.RfCommit,
            () => ValueTask.FromResult(Committed(1)));
        release.SetResult();
        ApplicationCommitTerminalResult failed = await transition.Completion;
        Assert.AreEqual(ApplicationCommitTerminalStatus.OperationFaulted, failed.Status);
        Assert.AreSame(error, failed.Error);
        Assert.AreEqual(ApplicationCommitTerminalStatus.Confirmed, (await rf.Completion).Status);
    }

    [TestMethod]
    public async Task RfCompletionCanReenterApplyAndWaitWithCapacityOne()
    {
        await using var coordinator = new ApplicationCommitCoordinator(Inspection, 1);
        var entered = Signal(); var release = Signal();
        ApplicationCommitAdmission rf = coordinator.AdmitCommit(Run, ApplicationCommitOperationClass.RfCommit,
            async () => { entered.SetResult(); await release.Task; return Committed(1); });
        await entered.Task;
        Task<ApplicationCommitTerminalResult> reentrant = rf.Completion.ContinueWith(_ =>
            coordinator.AdmitGatePlanApply(new("reentrant"), () => ValueTask.CompletedTask)
                .Completion.GetAwaiter().GetResult(), TaskScheduler.Default);
        release.SetResult();
        ApplicationCommitTerminalResult apply = await reentrant;
        Assert.AreEqual(ApplicationCommitTerminalStatus.Completed, apply.Status);
        Assert.AreEqual((ulong)2, apply.AdmissionOrdinal);
    }

    [TestMethod]
    public async Task SnapshotDescribesBlockedRunnerAndMixedQueue()
    {
        await using var coordinator = new ApplicationCommitCoordinator(Inspection, 4);
        var entered = Signal(); var release = Signal();
        ApplicationCommitAdmission a = coordinator.AdmitCommit(Run, ApplicationCommitOperationClass.RfCommit,
            async () => { entered.SetResult(); await release.Task; return Committed(1); });
        await entered.Task;
        ApplicationCommitAdmission b = Apply(coordinator, "B", []);
        ApplicationCommitAdmission c = coordinator.AdmitCommit(Run, ApplicationCommitOperationClass.GapCommit,
            () => ValueTask.FromResult(Committed(2)));
        ApplicationCommitAdmission d = Transition(coordinator, "D", []);
        ApplicationCommitCoordinatorSnapshot snapshot = coordinator.Snapshot;
        Assert.AreEqual(4, snapshot.Occupancy); Assert.AreEqual(3, snapshot.Queued);
        Assert.AreEqual((ulong)5, snapshot.NextAdmissionOrdinal);
        Assert.AreEqual((ulong)4, snapshot.LastAdmissionOrdinal);
        Assert.AreEqual((ulong)0, snapshot.LastTerminalOrdinal);
        Assert.AreEqual(a.Descriptor, snapshot.RunningOperation);
        release.SetResult(); await Task.WhenAll(a.Completion, b.Completion, c.Completion, d.Completion);
        Assert.AreEqual((ulong)4, coordinator.Snapshot.LastTerminalOrdinal);
    }

    [TestMethod]
    public async Task PublishingTerminalIsObservableAndCloseDisposesAfterQueuedAdmissionDrains()
    {
        var synchronization = new ApplicationCommitCoordinatorTestSynchronization();
        var nextBodyEntered = Signal();
        var coordinator = new ApplicationCommitCoordinator(Inspection, 1, synchronization);
        ApplicationCommitAdmission first = coordinator.AdmitCommit(Run,
            ApplicationCommitOperationClass.RfCommit, () => ValueTask.FromResult(Committed(1)));
        await synchronization.PublishingTerminalReached;

        ApplicationCommitCoordinatorSnapshot emptyPublishing = coordinator.Snapshot;
        Assert.AreEqual(ApplicationCommitCoordinatorLifecycle.PublishingTerminal, emptyPublishing.Lifecycle);
        Assert.AreEqual(0, emptyPublishing.Occupancy);
        Assert.AreEqual(0, emptyPublishing.Queued);
        Assert.IsNull(emptyPublishing.RunningOperation);
        Assert.AreEqual((ulong)1, emptyPublishing.LastTerminalOrdinal);
        Assert.IsFalse(first.Completion.IsCompleted);

        ApplicationCommitAdmission next = await Task.Run(() => coordinator.AdmitGatePlanApply(new("queued"),
            () => { nextBodyEntered.SetResult(); return ValueTask.CompletedTask; }));
        ApplicationCommitCoordinatorSnapshot queuedPublishing = coordinator.Snapshot;
        Assert.AreEqual(ApplicationCommitCoordinatorLifecycle.PublishingTerminal, queuedPublishing.Lifecycle);
        Assert.AreEqual(1, queuedPublishing.Occupancy);
        Assert.AreEqual(1, queuedPublishing.Queued);
        Assert.IsNull(queuedPublishing.RunningOperation);
        Assert.IsFalse(nextBodyEntered.Task.IsCompleted);

        coordinator.CloseAdmission();
        Task dispose = coordinator.DisposeAsync().AsTask();
        Assert.AreEqual(ApplicationCommitCoordinatorLifecycle.Closing, coordinator.Snapshot.Lifecycle);
        Assert.IsFalse(dispose.IsCompleted);
        synchronization.ReleasePublishingTerminal();
        await Task.WhenAll(first.Completion, next.Completion, dispose);
        Assert.IsTrue(nextBodyEntered.Task.IsCompleted);
        ApplicationCommitCoordinatorSnapshot closed = coordinator.Snapshot;
        Assert.AreEqual(ApplicationCommitCoordinatorLifecycle.Closed, closed.Lifecycle);
        Assert.AreEqual(0, closed.Occupancy);
        Assert.AreEqual(0, closed.Queued);
        Assert.IsNull(closed.RunningOperation);
    }

    [TestMethod]
    [DataRow("RfCommit")]
    [DataRow("GapCommit")]
    [DataRow("GatePlanApply")]
    [DataRow("GateRunTransition")]
    [Timeout(5000, CooperativeCancellation = true)]
    public async Task EveryBodyRunsOutsideCoordinatorGateAndFastPathIsDirect(string className)
    {
        var coordinator = new ApplicationCommitCoordinator(Inspection, 1);
        var entered = Signal(); var gateOperationCompleted = Signal();
        Task<ApplicationCommitAdmission> admitting = Task.Run(() => AdmitGateProbe(coordinator,
            Enum.Parse<ApplicationCommitOperationClass>(className), () =>
        {
            entered.SetResult();
            Task.Run(() => { coordinator.CloseApplyAdmission(); gateOperationCompleted.SetResult(); })
                .GetAwaiter().GetResult();
        }));
        await entered.Task; await gateOperationCompleted.Task;
        ApplicationCommitAdmission admission = await admitting;
        Assert.IsTrue((await admission.Completion).IsSuccessful);
        await coordinator.DisposeAsync();
    }

    [TestMethod]
    public async Task InvariantFaultClosesAdmissionAndResolvesEveryQueuedCompletionWithoutRunningBodies()
    {
        var coordinator = new ApplicationCommitCoordinator(Inspection, 3);
        var entered = Signal(); var release = Signal(); int queuedInvocations = 0;
        ApplicationCommitAdmissionResult failing = coordinator.TryAdmitGatePlanApply(Run, new("fault"), async () =>
        {
            entered.TrySetResult(); await release.Task;
            throw new ApplicationCommitInvariantException("primary");
        });
        await entered.Task;
        ApplicationCommitAdmission queuedRf = coordinator.AdmitCommit(Run,
            ApplicationCommitOperationClass.RfCommit, () =>
            {
                Interlocked.Increment(ref queuedInvocations);
                return ValueTask.FromResult(Committed(1));
            });
        release.TrySetResult();

        ApplicationCommitTerminalResult failed = await failing.Admission!.Completion;
        ApplicationCommitTerminalResult skipped = await queuedRf.Completion;
        Assert.AreEqual(ApplicationCommitTerminalStatus.InvariantFaulted, failed.Status);
        Assert.AreEqual(ApplicationCommitTerminalStatus.SkippedAfterInvariantFault, skipped.Status);
        Assert.AreSame(failed.Error, skipped.Error);
        Assert.AreEqual(0, queuedInvocations);
        Assert.AreEqual((ulong)2, coordinator.Snapshot.LastTerminalOrdinal);
        Assert.AreEqual(ApplicationCommitCoordinatorLifecycle.Faulted, coordinator.Snapshot.Lifecycle);
        Assert.AreEqual(ApplicationCommitAdmissionStatus.AdmissionClosed,
            coordinator.TryAdmitGatePlanApply(Run, new("late"), () => ValueTask.FromResult<Action?>(null)).Status);
        await coordinator.DisposeAsync();
    }

    [TestMethod]
    public async Task PostCommitPublicationCanReenterAtCapacityOneAndPreservesOrdinalOrder()
    {
        await using var coordinator = new ApplicationCommitCoordinator(Inspection, 1);
        ApplicationCommitAdmissionResult reentered = default;
        ApplicationCommitAdmissionResult first = coordinator.TryAdmitGatePlanApply(Run, new("first"), () =>
            ValueTask.FromResult<Action?>(() =>
            {
                reentered = coordinator.TryAdmitGatePlanApply(Run, new("second"),
                    () => ValueTask.FromResult<Action?>(null));
            }));

        Assert.AreEqual(ApplicationCommitTerminalStatus.Completed, (await first.Admission!.Completion).Status);
        Assert.IsTrue(reentered.IsAdmitted);
        Assert.AreEqual(ApplicationCommitTerminalStatus.Completed, (await reentered.Admission!.Completion).Status);
        Assert.AreEqual((ulong)1, first.Admission.AdmissionOrdinal);
        Assert.AreEqual((ulong)2, reentered.Admission.AdmissionOrdinal);
        Assert.AreEqual((ulong)2, coordinator.Snapshot.LastTerminalOrdinal);
    }

    private static ApplicationCommitAdmission Admit(ApplicationCommitCoordinator coordinator, string name,
        ulong revision, List<string> events) => coordinator.AdmitCommit(Run, ApplicationCommitOperationClass.RfCommit,
        () => { events.Add(name); return ValueTask.FromResult(Committed(revision)); });

    private static ApplicationCommitAdmission Apply(ApplicationCommitCoordinator coordinator, string name,
        List<string> events) => coordinator.AdmitGatePlanApply(new(name), () =>
        { events.Add(name); return ValueTask.CompletedTask; });

    private static ApplicationCommitAdmission Transition(ApplicationCommitCoordinator coordinator, string name,
        List<string> events) => coordinator.AdmitGateRunTransition(Run, new(name), () =>
        { events.Add(name); return ValueTask.CompletedTask; });

    private static ApplicationCommitAdmission AdmitClass(ApplicationCommitCoordinator coordinator,
        ApplicationCommitOperationClass operationClass, Action invoked, CancellationToken cancellationToken) =>
        operationClass switch
        {
            ApplicationCommitOperationClass.RfCommit or ApplicationCommitOperationClass.GapCommit =>
                coordinator.AdmitCommit(Run, operationClass, () =>
                { invoked(); return ValueTask.FromResult(Committed(1)); }, cancellationToken),
            ApplicationCommitOperationClass.GatePlanApply => coordinator.AdmitGatePlanApply(new("apply"), () =>
            { invoked(); return ValueTask.CompletedTask; }, cancellationToken),
            ApplicationCommitOperationClass.GateRunTransition => coordinator.AdmitGateRunTransition(Run,
                new("transition"), () => { invoked(); return ValueTask.CompletedTask; }, cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(operationClass))
        };

    private static ApplicationCommitAdmission AdmitBlockedClass(ApplicationCommitCoordinator coordinator,
        ApplicationCommitOperationClass operationClass, TaskCompletionSource entered, TaskCompletionSource release,
        CancellationToken cancellationToken = default) =>
        operationClass switch
        {
            ApplicationCommitOperationClass.RfCommit or ApplicationCommitOperationClass.GapCommit =>
                coordinator.AdmitCommit(Run, operationClass, async () =>
                { entered.SetResult(); await release.Task; return Committed(1); }, cancellationToken),
            ApplicationCommitOperationClass.GatePlanApply => coordinator.AdmitGatePlanApply(new("apply"), async () =>
            { entered.SetResult(); await release.Task; }, cancellationToken),
            ApplicationCommitOperationClass.GateRunTransition => coordinator.AdmitGateRunTransition(Run,
                new("transition"), async () => { entered.SetResult(); await release.Task; }, cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(operationClass))
        };

    private static ApplicationCommitAdmission AdmitGateProbe(ApplicationCommitCoordinator coordinator,
        ApplicationCommitOperationClass operationClass, Action body) => operationClass switch
        {
            ApplicationCommitOperationClass.RfCommit or ApplicationCommitOperationClass.GapCommit =>
                coordinator.AdmitCommit(Run, operationClass, () =>
                { body(); return ValueTask.FromResult(Committed(1)); }),
            ApplicationCommitOperationClass.GatePlanApply => coordinator.AdmitGatePlanApply(new("apply"), () =>
            { body(); return ValueTask.CompletedTask; }),
            ApplicationCommitOperationClass.GateRunTransition => coordinator.AdmitGateRunTransition(Run,
                new("transition"), () => { body(); return ValueTask.CompletedTask; }),
            _ => throw new ArgumentOutOfRangeException(nameof(operationClass))
        };

    private static Task<ApplicationCommitTerminalResult> Commit(ApplicationCommitCoordinator coordinator,
        ulong revision) => coordinator.AdmitCommit(Run, ApplicationCommitOperationClass.RfCommit,
        () => ValueTask.FromResult(Committed(revision))).Completion;

    private static TaskCompletionSource Signal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static InspectionAppendResult Committed(ulong revision) =>
        new(InspectionAppendStatus.Committed, new(revision), wasAccepted: true);
    private static InspectionAppendResult Duplicate(ulong revision) =>
        new(InspectionAppendStatus.Duplicate, new(revision), wasAccepted: true);

    private sealed class ScriptedWriter(AcquisitionRunId runId, params InspectionAppendResult[] results) :
        IInspectionRunWriter
    {
        private readonly Queue<InspectionAppendResult> _results = new(results);
        public AcquisitionRunId RunId { get; } = runId;
        public InspectionRunState State { get; private set; } = InspectionRunState.Open;
        public ValueTask<InspectionAppendResult> CommitAsync(InspectionCommitInput commit,
            CancellationToken cancellationToken = default) => ValueTask.FromResult(_results.Dequeue());
        public ValueTask<InspectionAppendResult> CommitGapAsync(GapRecord gap,
            CancellationToken cancellationToken = default) => ValueTask.FromResult(_results.Dequeue());
        public ValueTask<InspectionRunSealResult> SealRunAsync(CancellationToken cancellationToken = default)
        {
            State = InspectionRunState.Sealed;
            return ValueTask.FromResult(new InspectionRunSealResult(RunId, new(2), null, null, null, null, 0, 0));
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class SnapshotObserver(Action<SessionSnapshot> onNext) : IObserver<SessionSnapshot>
    {
        public void OnCompleted() { }
        public void OnError(Exception error) { }
        public void OnNext(SessionSnapshot value) => onNext(value);
    }
}
