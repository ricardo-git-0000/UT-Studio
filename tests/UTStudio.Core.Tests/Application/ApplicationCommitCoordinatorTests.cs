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
    private static readonly AcquisitionRunId Run = new(Guid.Parse("83000000-0000-0000-0000-000000000001"));
    private static readonly ConventionalAcquisitionConfiguration Configuration =
        new(new PhysicalChannelId(0), 8, 50_000_000, firstSampleOffsetSeconds: 0.000_001);

    [TestMethod]
    public async Task FourAdmissionsExecuteInOrdinalFifoWithoutBarging()
    {
        await using var coordinator = new ApplicationCommitCoordinator(4);
        var entered = Signal(); var release = Signal(); var events = new List<string>();
        ApplicationCommitAdmission a = coordinator.AdmitCommit(Run, ApplicationCommitKind.Rf, async () =>
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
        Assert.AreEqual((ulong)4, snapshot.LastCompletionOrdinal);
        Assert.AreEqual(ApplicationCommitCoordinatorLifecycle.Accepting, snapshot.Lifecycle);
    }

    [TestMethod]
    [DataRow("Rf", "Gap")]
    [DataRow("Gap", "Rf")]
    public async Task RfAndGapPreserveAdmissionOrder(string firstName, string secondName)
    {
        ApplicationCommitKind firstKind = Enum.Parse<ApplicationCommitKind>(firstName);
        ApplicationCommitKind secondKind = Enum.Parse<ApplicationCommitKind>(secondName);
        await using var coordinator = new ApplicationCommitCoordinator(2);
        var entered = Signal(); var release = Signal(); var events = new List<ApplicationCommitKind>();
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
        await using var coordinator = new ApplicationCommitCoordinator(1);
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        bool invoked = false;
        Assert.ThrowsExactly<OperationCanceledException>(() => coordinator.AdmitCommit(Run, ApplicationCommitKind.Rf,
            () => { invoked = true; return ValueTask.FromResult(Committed(1)); }, cancellation.Token));
        Assert.IsFalse(invoked);
        Assert.AreEqual((ulong)0, coordinator.Snapshot.LastAdmissionOrdinal);
    }

    [TestMethod]
    public async Task CancellationAfterAdmissionCancelsOnlyWaitAndSlotRemainsOwnedUntilTerminal()
    {
        await using var coordinator = new ApplicationCommitCoordinator(1);
        var entered = Signal(); var release = Signal();
        ApplicationCommitAdmission admission = coordinator.AdmitCommit(Run, ApplicationCommitKind.Rf, async () =>
        { entered.TrySetResult(); await release.Task; return Committed(1); });
        await entered.Task;
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsExactlyAsync<TaskCanceledException>(() => admission.WaitAsync(cancellation.Token));
        Assert.AreEqual(1, coordinator.Snapshot.Occupancy);
        Assert.ThrowsExactly<InvalidOperationException>(() => coordinator.AdmitCommit(Run, ApplicationCommitKind.Rf,
            () => ValueTask.FromResult(Committed(2))));
        release.TrySetResult();
        ApplicationCommitTerminalResult terminal = await admission.Completion;
        Assert.AreEqual(ApplicationCommitTerminalStatus.Confirmed, terminal.Status);
        Assert.AreEqual(0, coordinator.Snapshot.Occupancy);
    }

    [TestMethod]
    [Timeout(5000, CooperativeCancellation = true)]
    public async Task SynchronousWriterStartsBeforeAdmissionReturnsButDoesNotHoldCoordinatorGate()
    {
        var coordinator = new ApplicationCommitCoordinator(1);
        var writerEntered = Signal();
        var closeCompleted = Signal();
        Task<ApplicationCommitAdmission> admitting = Task.Run(() => coordinator.AdmitCommit(Run,
            ApplicationCommitKind.Rf, () =>
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
        await using var coordinator = new ApplicationCommitCoordinator(2);
        var entered = Signal(); var release = Signal();
        var error = new InvalidOperationException("writer");
        ApplicationCommitAdmission first = coordinator.AdmitCommit(Run, ApplicationCommitKind.Rf, async () =>
        { entered.TrySetResult(); await release.Task; throw error; });
        await entered.Task;
        ApplicationCommitAdmission second = coordinator.AdmitCommit(Run, ApplicationCommitKind.Rf,
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
        await using var coordinator = new ApplicationCommitCoordinator(1);
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
        await using var coordinator = new ApplicationCommitCoordinator(1);
        _ = await Commit(coordinator, 2);
        ApplicationCommitTerminalResult equalCommit = await Commit(coordinator, 2);
        ApplicationCommitTerminalResult lowerDuplicate = await coordinator.AdmitCommit(Run, ApplicationCommitKind.Rf,
            () => ValueTask.FromResult(Duplicate(1))).Completion;
        ApplicationCommitTerminalResult equalDuplicate = await coordinator.AdmitCommit(Run, ApplicationCommitKind.Rf,
            () => ValueTask.FromResult(Duplicate(2))).Completion;
        ApplicationCommitTerminalResult higherDuplicate = await coordinator.AdmitCommit(Run, ApplicationCommitKind.Rf,
            () => ValueTask.FromResult(Duplicate(3))).Completion;
        Assert.AreEqual(ApplicationCommitTerminalStatus.NonAdvancingRevision, equalCommit.Status);
        Assert.AreEqual(ApplicationCommitTerminalStatus.DuplicateUnverifiable, lowerDuplicate.Status);
        Assert.AreEqual(ApplicationCommitTerminalStatus.DuplicateUnverifiable, equalDuplicate.Status);
        Assert.AreEqual(ApplicationCommitTerminalStatus.DuplicateUnverifiable, higherDuplicate.Status);
        Assert.AreEqual(new InspectionRevision(2), coordinator.Snapshot.LastConfirmedRevision);
        Assert.AreEqual(ApplicationCommitTerminalStatus.Confirmed, (await Commit(coordinator, 3)).Status);
    }

    [TestMethod]
    public async Task FailedExternalReconciliationAfterCompletionDoesNotBlockLaterCommit()
    {
        await using var coordinator = new ApplicationCommitCoordinator(1);
        ApplicationCommitTerminalResult first = await Commit(coordinator, 1);
        Assert.AreEqual(ApplicationCommitTerminalStatus.Confirmed, first.Status);
        Assert.ThrowsExactly<InvalidOperationException>(() => throw new InvalidOperationException("publication"));
        ApplicationCommitTerminalResult second = await Commit(coordinator, 2);
        Assert.AreEqual(ApplicationCommitTerminalStatus.Confirmed, second.Status);
    }

    [TestMethod]
    public async Task CapacityIncludesRunnerAndQueueAndRecordsOneOverRejection()
    {
        await using var coordinator = new ApplicationCommitCoordinator(2);
        var entered = Signal(); var release = Signal();
        ApplicationCommitAdmission first = coordinator.AdmitCommit(Run, ApplicationCommitKind.Rf, async () =>
        { entered.TrySetResult(); await release.Task; return Committed(1); });
        await entered.Task;
        ApplicationCommitAdmission second = coordinator.AdmitCommit(Run, ApplicationCommitKind.Gap,
            () => ValueTask.FromResult(Committed(2)));
        Assert.ThrowsExactly<InvalidOperationException>(() => coordinator.AdmitCommit(Run, ApplicationCommitKind.Rf,
            () => ValueTask.FromResult(Committed(3))));
        ApplicationCommitCoordinatorSnapshot full = coordinator.Snapshot;
        Assert.AreEqual(2, full.Occupancy); Assert.AreEqual(1, full.Queued);
        Assert.AreEqual((ulong)1, full.CapacityRejections);
        release.TrySetResult(); await first.Completion; await second.Completion;
    }

    [TestMethod]
    public async Task EnqueueBeforeCloseIsAdmittedAndDrained()
    {
        var coordinator = new ApplicationCommitCoordinator(1);
        var entered = Signal(); var release = Signal();
        ApplicationCommitAdmission admitted = coordinator.AdmitCommit(Run, ApplicationCommitKind.Rf, async () =>
        { entered.TrySetResult(); await release.Task; return Committed(1); });
        await entered.Task; coordinator.CloseAdmission();
        Assert.AreEqual(ApplicationCommitCoordinatorLifecycle.Closing, coordinator.Snapshot.Lifecycle);
        release.TrySetResult(); await admitted.Completion; await coordinator.DisposeAsync();
        Assert.AreEqual(ApplicationCommitCoordinatorLifecycle.Closed, coordinator.Snapshot.Lifecycle);
    }

    [TestMethod]
    public async Task CloseBeforeEnqueueRejectsAndCountsStableClosedRejection()
    {
        var coordinator = new ApplicationCommitCoordinator(1);
        coordinator.CloseAdmission(); coordinator.CloseAdmission();
        Assert.ThrowsExactly<ObjectDisposedException>(() => coordinator.AdmitCommit(Run, ApplicationCommitKind.Rf,
            () => ValueTask.FromResult(Committed(1))));
        Assert.AreEqual((ulong)1, coordinator.Snapshot.ClosedRejections);
        await coordinator.DisposeAsync(); await coordinator.DisposeAsync();
    }

    [TestMethod]
    public async Task RealCallbackAfterCompletionCanAdmitAndSynchronouslyWaitForNextCommit()
    {
        await using var coordinator = new ApplicationCommitCoordinator(1);
        ApplicationCommitAdmission first = coordinator.AdmitCommit(Run, ApplicationCommitKind.Rf,
            () => ValueTask.FromResult(Committed(1)));
        _ = await first.Completion;
        ApplicationCommitTerminalResult second = await Task.Run(() => coordinator.AdmitCommit(Run,
            ApplicationCommitKind.Rf, () => ValueTask.FromResult(Committed(2))).Completion.GetAwaiter().GetResult());
        Assert.AreEqual(ApplicationCommitTerminalStatus.Confirmed, second.Status);
        Assert.AreEqual((ulong)2, second.AdmissionOrdinal);
    }

    [TestMethod]
    public async Task DifferentRunIsRejectedBeforeWriterAndBeforeOrdinalAssignment()
    {
        await using var coordinator = new ApplicationCommitCoordinator(1);
        _ = await Commit(coordinator, 1);
        bool invoked = false;
        var other = new AcquisitionRunId(Guid.Parse("83000000-0000-0000-0000-000000000002"));
        Assert.ThrowsExactly<InvalidOperationException>(() => coordinator.AdmitCommit(other, ApplicationCommitKind.Rf,
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
            typeof(ApplicationCommitKind)
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

    private static ApplicationCommitAdmission Admit(ApplicationCommitCoordinator coordinator, string name,
        ulong revision, List<string> events) => coordinator.AdmitCommit(Run, ApplicationCommitKind.Rf,
        () => { events.Add(name); return ValueTask.FromResult(Committed(revision)); });

    private static Task<ApplicationCommitTerminalResult> Commit(ApplicationCommitCoordinator coordinator,
        ulong revision) => coordinator.AdmitCommit(Run, ApplicationCommitKind.Rf,
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
