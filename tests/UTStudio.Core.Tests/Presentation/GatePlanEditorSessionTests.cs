using UTStudio.Application;
using UTStudio.Contracts.Processing;
using UTStudio.Core.Tests.Processing;
using UTStudio.Domain.Inspection;
using UTStudio.Domain.Processing;
using UTStudio.Presentation;
using UTStudio.Processing.Core;

namespace UTStudio.Core.Tests.Presentation;

[TestClass]
public sealed class GatePlanEditorSessionTests
{
    [TestMethod]
    public void PreviewCompilesCandidateNPlusOneAndMaximumRevisionIsTyped()
    {
        CommittedGatePlanSnapshot committed = Committed(1);
        using var authority = new InMemoryGatePlanAuthorityOwner([committed]);
        using var editor = new GatePlanEditorSession(committed.AuthorityKey, authority.Seal(),
            new GatePlanPreviewCompiler());
        editor.BeginEdit();

        GatePlanEditResult preview = editor.Preview();
        GatePreviewArtifact artifact = editor.BorrowPreviewCandidate()!;

        Assert.AreEqual(GatePlanEditOutcome.Previewed, preview.Outcome);
        Assert.AreEqual(committed.Revision, artifact.Source.BaseRevision);
        Assert.AreEqual(new GatePlanRevision(committed.Revision.Value + 1), artifact.Source.CandidateRevision);
        Assert.AreSame(artifact.Candidate.CompiledPlan, artifact.CompiledPlan);

        CompiledGatePlan maximum = Committed(uint.MaxValue).CompiledPlan;
        using var maximumAuthority = new InMemoryGatePlanAuthorityOwner([
            new CommittedGatePlanSnapshot(committed.InspectionId, maximum)]);
        using var maximumEditor = new GatePlanEditorSession(new(committed.InspectionId, maximum.Source.Id),
            maximumAuthority.Seal(), new GatePlanPreviewCompiler());
        maximumEditor.BeginEdit();
        Assert.AreEqual(GatePlanEditOutcome.RevisionExhausted, maximumEditor.Preview().Outcome);
        Assert.IsNull(maximumEditor.BorrowPreviewCandidate());
    }

    [TestMethod]
    public void TwoEditorsKeepIndependentBorrowedPreviewGuards()
    {
        CommittedGatePlanSnapshot committed = Committed(1);
        using var authority = new InMemoryGatePlanAuthorityOwner([committed]);
        IGatePlanAuthorityReader reader = authority.Seal();
        var compiler = new GatePlanPreviewCompiler();
        using var first = new GatePlanEditorSession(committed.AuthorityKey, reader, compiler);
        using var second = new GatePlanEditorSession(committed.AuthorityKey, reader, compiler);
        first.BeginEdit(); second.BeginEdit(); first.Preview(); second.Preview();
        GatePreviewArtifact a = first.BorrowPreviewCandidate()!;
        GatePreviewArtifact b = second.BorrowPreviewCandidate()!;

        Assert.AreNotEqual(a.Identity.Value, b.Identity.Value);
        Assert.AreEqual(GateGeometrySourceValidationStatus.Current, a.PreviewGuard.ValidatePreview(a.Source));
        Assert.AreEqual(GateGeometrySourceValidationStatus.Current, b.PreviewGuard.ValidatePreview(b.Source));
        Assert.AreEqual(GateGeometrySourceValidationStatus.Superseded, a.PreviewGuard.ValidatePreview(b.Source));
    }
    private static readonly InspectionId Inspection = new(Guid.Parse("82000000-0000-0000-0000-000000000001"));

    [TestMethod]
    public void BeginEditCopiesCommittedAndDraftMutationIsIsolated()
    {
        using var authority = Authority(out CommittedGatePlanSnapshot committed);
        using var editor = new GatePlanEditorSession(committed.AuthorityKey, authority.Reader, new GatePlanPreviewCompiler());
        GatePlanEditorSnapshot editing = editor.BeginEdit();
        Assert.AreEqual(GatePlanEditorState.Editing, editing.State);
        Assert.AreSame(committed, editing.Committed);
        Assert.AreEqual(committed.Revision, editing.Draft!.BaseRevision);
        Assert.AreEqual(committed.AuthorityKey, editing.Draft.Authority);

        List<GateDefinition> external = [WithWindowAndThreshold(committed.Plan.Gates[0], 0.000002, 0.000003, 0.4)];
        var changed = new GatePlanDraft(committed.AuthorityKey, committed.Revision,
            committed.Plan.SchemaVersion, committed.Plan.AlgorithmVersion, external);
        GatePlanEditorSnapshot updated = editor.UpdateDraft(changed);
        external.Add(GateTestPlan.Gate("C"));
        Assert.HasCount(1, updated.Draft!.Gates);
        Assert.AreEqual(0.000002, updated.Draft.Gates[0].Window.StartOffsetSeconds);
        Assert.HasCount(1, authority.Reader.Read(committed.AuthorityKey).Committed.Plan.Gates);
        Assert.AreEqual("A", authority.Reader.Read(committed.AuthorityKey).Committed.Plan.Gates[0].Id.Value);
        Assert.ThrowsExactly<ArgumentException>(() => editor.UpdateDraft(new GatePlanDraft(
            new(Inspection, new("other")), committed.Revision, committed.Plan.SchemaVersion,
            committed.Plan.AlgorithmVersion, [GateTestPlan.Gate("X")])));
        Assert.ThrowsExactly<ArgumentException>(() => editor.UpdateDraft(new GatePlanDraft(
            committed.AuthorityKey, committed.Revision, committed.Plan.SchemaVersion,
            committed.Plan.AlgorithmVersion, [GateTestPlan.Gate("B")])));
    }

    [TestMethod]
    public void PreviewValidAndInvalidNeverConsumeCommittedRevisionOrCreateEffects()
    {
        using var authority = Authority(out CommittedGatePlanSnapshot committed);
        using var editor = new GatePlanEditorSession(committed.AuthorityKey, authority.Reader, new GatePlanPreviewCompiler());
        editor.BeginEdit();
        GatePlanEditResult valid = editor.Preview();
        Assert.AreEqual(GatePlanEditOutcome.Previewed, valid.Outcome);
        Assert.AreEqual(GatePlanEditorState.Previewed, valid.Snapshot.State);
        Assert.IsNull(valid.ResultingRevision);
        Assert.AreEqual(new GatePlanRevision(1), authority.Reader.Read(committed.AuthorityKey).Committed.Revision);

        var invalid = new GatePlanDraft(committed.AuthorityKey, committed.Revision,
            committed.Plan.SchemaVersion, committed.Plan.AlgorithmVersion,
            [WithWindowAndThreshold(committed.Plan.Gates[0], 0, 0, null)]);
        editor.UpdateDraft(invalid);
        GatePlanEditResult failed = editor.Preview();
        Assert.AreEqual(GatePlanEditOutcome.ValidationFailed, failed.Outcome);
        Assert.AreEqual(GatePlanEditorState.ValidationFailed, failed.Snapshot.State);
        Assert.IsNotEmpty(failed.ValidationErrors);
        CollectionAssert.AreEqual(failed.ValidationErrors.ToArray(), failed.Snapshot.DraftErrors.ToArray());
        Assert.IsNull(failed.ResultingRevision);
        Assert.AreEqual(new GatePlanRevision(1), authority.Reader.Read(committed.AuthorityKey).Committed.Revision);
    }

    [TestMethod]
    public void RejectedTransactionalCandidatePreservesValidDraftAndApplicablePreview()
    {
        using var authority = Authority(out CommittedGatePlanSnapshot committed);
        using var editor = new GatePlanEditorSession(committed.AuthorityKey, authority.Reader, new GatePlanPreviewCompiler());
        GatePlanDraft validDraft = editor.BeginEdit().Draft!;
        Assert.AreEqual(GatePlanEditOutcome.Previewed, editor.Preview().Outcome);
        GatePreviewArtifact validArtifact = editor.BorrowPreviewCandidate()!;
        GateDefinition gate = validDraft.Gates.Single();
        var selfReference = new GateDefinition(gate.Id, gate.Revision,
            new(GateTimeReference.GateEvent(gate.Id), gate.Window.StartOffsetSeconds, gate.Window.DurationSeconds),
            gate.SignalDomain, gate.Detector, gate.RequestedMeasures);
        var rejected = new GatePlanDraft(validDraft.Authority, validDraft.BaseRevision,
            validDraft.SchemaVersion, validDraft.AlgorithmVersion, [selfReference]);

        GatePlanEditResult result = editor.Preview(rejected, preserveCurrentOnFailure: true);

        Assert.AreEqual(GatePlanEditOutcome.CandidateRejected, result.Outcome);
        Assert.IsTrue(result.ValidationErrors.Any(x => x.Code == GateCompilationErrorCode.SelfDependency));
        Assert.AreSame(validDraft, editor.Snapshot.Draft);
        Assert.AreSame(validArtifact, editor.BorrowApplicablePreview());
        Assert.AreEqual(GatePlanEditorState.Previewed, editor.Snapshot.State);
    }

    [TestMethod]
    public void CancelIsDeterministicIdempotentAndClearsDraftAndErrors()
    {
        using var authority = Authority(out CommittedGatePlanSnapshot committed);
        using var editor = new GatePlanEditorSession(committed.AuthorityKey, authority.Reader, new GatePlanPreviewCompiler());
        editor.BeginEdit();
        editor.UpdateDraft(new(committed.AuthorityKey, committed.Revision, committed.Plan.SchemaVersion,
            committed.Plan.AlgorithmVersion, [WithWindowAndThreshold(committed.Plan.Gates[0], 0, 0, null)]));
        _ = editor.Preview();
        GatePlanEditResult first = editor.Cancel();
        GatePlanEditResult second = editor.Cancel();
        Assert.AreEqual(GatePlanEditOutcome.Cancelled, first.Outcome);
        Assert.AreEqual(GatePlanEditOutcome.Cancelled, second.Outcome);
        Assert.AreEqual(GatePlanEditorState.Cancelled, second.Snapshot.State);
        Assert.IsNull(second.Snapshot.Draft); Assert.IsEmpty(second.Snapshot.DraftErrors);
        Assert.AreSame(committed, second.Snapshot.Committed);
    }

    [TestMethod]
    public void ExternalCommittedRevisionMakesDraftStaleAndCancelRestoresLatest()
    {
        CommittedGatePlanSnapshot one = Committed(1);
        var authority = new TestAuthorityReader(one);
        using var editor = new GatePlanEditorSession(one.AuthorityKey, authority, new GatePlanPreviewCompiler());
        editor.BeginEdit();
        CommittedGatePlanSnapshot two = Committed(2);
        authority.Install(two);
        Assert.AreEqual(GatePlanEditorState.Stale, editor.Snapshot.State);
        Assert.AreEqual(new GatePlanRevision(1), editor.Snapshot.Draft!.BaseRevision);
        Assert.AreSame(two, editor.Snapshot.Committed);
        GatePlanEditResult conflict = editor.Preview();
        Assert.AreEqual(GatePlanEditOutcome.Conflict, conflict.Outcome);
        Assert.AreEqual(1u, conflict.ExpectedRevision.Value);
        Assert.AreEqual(2u, conflict.CurrentRevision.Value);
        GatePlanEditResult cancelled = editor.Cancel();
        Assert.AreSame(two, cancelled.Snapshot.Committed);
        Assert.IsNull(cancelled.Snapshot.Draft);
    }

    [TestMethod]
    public async Task PublicationIsAtomicAndReentrantCancelSeesCompletePreviewSnapshot()
    {
        using var authority = Authority(out CommittedGatePlanSnapshot committed);
        using var editor = new GatePlanEditorSession(committed.AuthorityKey, authority.Reader, new GatePlanPreviewCompiler());
        var states = new List<GatePlanEditorState>();
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using IDisposable subscription = editor.Subscribe(new Observer<GatePlanEditorSnapshot>(snapshot =>
        {
            states.Add(snapshot.State);
            if (snapshot.State == GatePlanEditorState.Previewed)
            {
                Assert.IsNotNull(snapshot.Draft); Assert.IsEmpty(snapshot.DraftErrors);
                _ = editor.Cancel();
            }
            if (snapshot.State == GatePlanEditorState.Cancelled) cancelled.TrySetResult();
        }));
        editor.BeginEdit();
        GatePlanEditResult preview = editor.Preview();
        Assert.AreEqual(GatePlanEditOutcome.Previewed, preview.Outcome);
        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.AreEqual(GatePlanEditorState.Cancelled, editor.Snapshot.State);
        CollectionAssert.AreEqual(new[] { GatePlanEditorState.Committed, GatePlanEditorState.Editing,
            GatePlanEditorState.Previewed, GatePlanEditorState.Cancelled }, states);
    }

    [TestMethod]
    public async Task DisposeDuringCallbackClosesDraftReleasesObserversAndRejectsCommands()
    {
        using var authority = Authority(out CommittedGatePlanSnapshot committed);
        var editor = new GatePlanEditorSession(committed.AuthorityKey, authority.Reader, new GatePlanPreviewCompiler());
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using IDisposable subscription = editor.Subscribe(new Observer<GatePlanEditorSnapshot>(snapshot =>
        {
            if (snapshot.State == GatePlanEditorState.Editing) editor.Dispose();
        }, () => completed.TrySetResult()));
        _ = editor.BeginEdit();
        editor.Dispose();
        Assert.AreEqual(GatePlanEditorState.Closed, editor.Snapshot.State);
        Assert.IsNull(editor.Snapshot.Draft); Assert.IsEmpty(editor.Snapshot.DraftErrors);
        await completed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.ThrowsExactly<ObjectDisposedException>(() => editor.BeginEdit());
        Assert.ThrowsExactly<ObjectDisposedException>(() => editor.Cancel());
        Assert.ThrowsExactly<ObjectDisposedException>(() => editor.Preview());
        var lateCompleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using IDisposable late = editor.Subscribe(new Observer<GatePlanEditorSnapshot>(
            _ => Assert.Fail("Closed editor published state."), () => lateCompleted.TrySetResult()));
        await lateCompleted.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [TestMethod]
    public async Task AuthorityChangeAndEditorialChangesDuringCompilationReturnTypedLateResults()
    {
        await VerifyLateResultAsync((authority, editor, committed) =>
            authority.Install(Committed(2)), GatePlanEditOutcome.Conflict);
        await VerifyLateResultAsync((authority, editor, committed) => editor.Cancel(),
            GatePlanEditOutcome.Cancelled);
        await VerifyLateResultAsync((authority, editor, committed) => editor.BeginEdit(),
            GatePlanEditOutcome.Superseded);
        await VerifyLateResultAsync((authority, editor, committed) => editor.UpdateDraft(new(
            committed.AuthorityKey, committed.Revision, committed.Plan.SchemaVersion,
            committed.Plan.AlgorithmVersion,
            [WithWindowAndThreshold(committed.Plan.Gates[0], 0.000001, 0.000002, null)])),
            GatePlanEditOutcome.Superseded);
        await VerifyLateResultAsync((authority, editor, committed) => editor.Dispose(),
            GatePlanEditOutcome.Closed);
        await VerifyLateResultAsync((authority, editor, committed) =>
        { editor.Cancel(); authority.Install(Committed(2)); }, GatePlanEditOutcome.Cancelled);
        await VerifyLateResultAsync((authority, editor, committed) =>
        { editor.Dispose(); authority.Install(Committed(2)); }, GatePlanEditOutcome.Closed);
        await VerifyLateResultAsync((authority, editor, committed) =>
        { editor.BeginEdit(); authority.Install(Committed(2)); }, GatePlanEditOutcome.Superseded);
        await VerifyLateResultAsync((authority, editor, committed) =>
        {
            editor.UpdateDraft(new(committed.AuthorityKey, committed.Revision, committed.Plan.SchemaVersion,
                committed.Plan.AlgorithmVersion,
                [WithWindowAndThreshold(committed.Plan.Gates[0], 0.000001, 0.000002, null)]));
            authority.Install(Committed(2));
        }, GatePlanEditOutcome.Superseded);
    }

    [TestMethod]
    public async Task AuthorityChangeAfterCompilationBeforeAcceptanceReturnsConflict()
    {
        CommittedGatePlanSnapshot committed = Committed(1);
        var authority = new TestAuthorityReader(committed);
        var reader = new BlockingStampReader(authority);
        using var editor = new GatePlanEditorSession(committed.AuthorityKey, reader, new GatePlanPreviewCompiler());
        editor.BeginEdit();
        Task<GatePlanEditResult> preview = Task.Run(editor.Preview);
        await reader.Entered.Task;
        authority.Install(Committed(2));
        reader.Release.SetResult();
        GatePlanEditResult result = await preview;
        Assert.AreEqual(GatePlanEditOutcome.Conflict, result.Outcome);
        Assert.AreEqual(GatePlanEditorState.Stale, editor.Snapshot.State);
        Assert.AreEqual(1u, editor.Snapshot.Draft!.BaseRevision.Value);
        Assert.AreEqual(2u, editor.Snapshot.Committed.Revision.Value);
    }

    [TestMethod]
    public async Task PreviewWinningBeforeAuthorityChangeIsDeliveredBeforeStale()
    {
        CommittedGatePlanSnapshot committed = Committed(1);
        var authority = new TestAuthorityReader(committed);
        using var editor = new GatePlanEditorSession(committed.AuthorityKey, authority,
            new GatePlanPreviewCompiler());
        var states = new List<GatePlanEditorState>();
        var stale = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using IDisposable subscription = editor.Subscribe(new Observer<GatePlanEditorSnapshot>(snapshot =>
        { states.Add(snapshot.State); if (snapshot.State == GatePlanEditorState.Stale) stale.TrySetResult(); }));
        editor.BeginEdit(); _ = editor.Preview(); authority.Install(Committed(2));
        await stale.Task.WaitAsync(TimeSpan.FromSeconds(5));
        CollectionAssert.AreEqual(new[] { GatePlanEditorState.Committed, GatePlanEditorState.Editing,
            GatePlanEditorState.Previewed, GatePlanEditorState.Stale }, states);
    }

    [TestMethod]
    public void NewCommittedCallbackObservedAfterStampBeforePreviewPublicationReturnsConflict()
    {
        CommittedGatePlanSnapshot committed = Committed(1);
        var authority = new TestAuthorityReader(committed);
        var reader = new StampThenChangeReader(authority, () => authority.Install(Committed(2)));
        using var editor = new GatePlanEditorSession(committed.AuthorityKey, reader, new GatePlanPreviewCompiler());
        editor.BeginEdit();
        GatePlanEditResult result = editor.Preview();
        Assert.AreEqual(GatePlanEditOutcome.Conflict, result.Outcome);
        Assert.AreEqual(GatePlanEditorState.Stale, result.Snapshot.State);
        Assert.AreEqual(2u, result.Snapshot.Committed.Revision.Value);
    }

    [TestMethod]
    public async Task ThrowingAndSelfUnsubscribingObserversDoNotBlockOtherEditorObservers()
    {
        using var authority = Authority(out CommittedGatePlanSnapshot committed);
        using var editor = new GatePlanEditorSession(committed.AuthorityKey, authority.Reader,
            new GatePlanPreviewCompiler());
        editor.Subscribe(new Observer<GatePlanEditorSnapshot>(_ => throw new InvalidOperationException("observer")));
        IDisposable? self = null;
        self = editor.Subscribe(new Observer<GatePlanEditorSnapshot>(snapshot =>
        {
            if (snapshot.State == GatePlanEditorState.Editing) self!.Dispose();
        }));
        var survivor = new List<GatePlanEditorState>();
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using IDisposable subscription = editor.Subscribe(new Observer<GatePlanEditorSnapshot>(snapshot =>
        { survivor.Add(snapshot.State); if (snapshot.State == GatePlanEditorState.Cancelled) cancelled.TrySetResult(); }));
        editor.BeginEdit(); _ = editor.Preview(); editor.Cancel();
        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        CollectionAssert.AreEqual(new[] { GatePlanEditorState.Committed, GatePlanEditorState.Editing,
            GatePlanEditorState.Previewed, GatePlanEditorState.Cancelled }, survivor);
    }

    [TestMethod]
    public async Task SlowObserverDoesNotBlockProducerOrFastObserver()
    {
        using var authority = Authority(out CommittedGatePlanSnapshot committed);
        using var editor = new GatePlanEditorSession(committed.AuthorityKey, authority.Reader,
            new GatePlanPreviewCompiler());
        var slowEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSlow = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using IDisposable slow = editor.Subscribe(new Observer<GatePlanEditorSnapshot>(snapshot =>
        {
            if (snapshot.State == GatePlanEditorState.Editing)
            { slowEntered.TrySetResult(); releaseSlow.Task.GetAwaiter().GetResult(); }
        }));
        var fastEditing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using IDisposable fast = editor.Subscribe(new Observer<GatePlanEditorSnapshot>(snapshot =>
        { if (snapshot.State == GatePlanEditorState.Editing) fastEditing.TrySetResult(); }));
        GatePlanEditorSnapshot returned = editor.BeginEdit();
        Assert.AreEqual(GatePlanEditorState.Editing, returned.State);
        await slowEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await fastEditing.Task.WaitAsync(TimeSpan.FromSeconds(5));
        releaseSlow.TrySetResult();
    }

    [TestMethod]
    public void AcceptedApplyConvergesForBothAuthorityAndCompletionOrders()
    {
        VerifyAcceptedConvergence(authorityFirst: true);
        VerifyAcceptedConvergence(authorityFirst: false);
    }

    [TestMethod]
    public void LateAcceptedApplyCannotReplaceANewerEditorialGeneration()
    {
        CommittedGatePlanSnapshot committed = Committed(1);
        var authority = new TestAuthorityReader(committed);
        using var editor = new GatePlanEditorSession(committed.AuthorityKey, authority,
            new GatePlanPreviewCompiler());
        editor.BeginEdit();
        GatePlanEditResult preview = editor.Preview();
        GatePreviewArtifact artifact = editor.BorrowApplicablePreview()!;
        GateEditorApplyTicket ticket = editor.RegisterApply(artifact);
        var installed = new CommittedGatePlanSnapshot(Inspection, artifact.CompiledPlan);
        authority.Install(installed);

        GatePlanEditorSnapshot newer = editor.BeginEdit();
        GatePlanEditResult accepted = GatePlanEditResult.Accepted(preview.Snapshot,
            new(installed, state: GatePlanEditorState.Committed));
        Assert.IsTrue(editor.ReconcileApply(ticket, accepted));
        Assert.AreSame(newer.Draft, editor.Snapshot.Draft);
        Assert.AreEqual(installed.Revision, editor.Snapshot.Draft!.BaseRevision);
    }

    private static void VerifyAcceptedConvergence(bool authorityFirst)
    {
        CommittedGatePlanSnapshot committed = Committed(1);
        var authority = new TestAuthorityReader(committed);
        using var editor = new GatePlanEditorSession(committed.AuthorityKey, authority,
            new GatePlanPreviewCompiler());
        editor.BeginEdit();
        GatePlanEditResult preview = editor.Preview();
        GatePreviewArtifact artifact = editor.BorrowApplicablePreview()!;
        GateEditorApplyTicket ticket = editor.RegisterApply(artifact);
        var installed = new CommittedGatePlanSnapshot(Inspection, artifact.CompiledPlan);
        GatePlanEditResult accepted = GatePlanEditResult.Accepted(preview.Snapshot,
            new(installed, state: GatePlanEditorState.Committed));

        if (authorityFirst) authority.Install(installed);
        Assert.IsTrue(editor.ReconcileApply(ticket, accepted));
        if (!authorityFirst) authority.Install(installed);

        Assert.AreEqual(GatePlanEditorState.Committed, editor.Snapshot.State);
        Assert.AreEqual(installed.Revision, editor.Snapshot.Committed.Revision);
        Assert.IsNull(editor.Snapshot.Draft);
    }

    private static async Task VerifyLateResultAsync(
        Action<TestAuthorityReader, GatePlanEditorSession, CommittedGatePlanSnapshot> change,
        GatePlanEditOutcome expected)
    {
        CommittedGatePlanSnapshot committed = Committed(1);
        var authority = new TestAuthorityReader(committed);
        var compiler = new BlockingCompiler();
        using var editor = new GatePlanEditorSession(committed.AuthorityKey, authority, compiler);
        editor.BeginEdit();
        Task<GatePlanEditResult> preview = Task.Run(editor.Preview);
        await compiler.Entered.Task;
        change(authority, editor, committed);
        compiler.Release.SetResult();
        GatePlanEditResult result = await preview;
        Assert.AreEqual(expected, result.Outcome);
        Assert.AreNotEqual(GatePlanEditorState.Previewed, editor.Snapshot.State);
    }

    private static InMemoryGatePlanAuthorityOwner Authority(out CommittedGatePlanSnapshot committed)
    {
        committed = Committed(1); var owner = new InMemoryGatePlanAuthorityOwner([committed]); owner.Seal(); return owner;
    }
    private static CommittedGatePlanSnapshot Committed(uint revision)
    {
        GatePlanCompilationResult result = new GatePlanCompiler().Compile(
            GateTestPlan.Plan([GateTestPlan.Gate("A")], revision: revision));
        Assert.IsTrue(result.IsCompiled); return new(Inspection, result.CompiledPlan!);
    }
    private static GateDefinition WithWindowAndThreshold(GateDefinition gate, double offset, double duration,
        double? threshold)
    {
        var detector = new GateDetectorDefinition(gate.Detector.Kind,
            threshold.HasValue ? new GateThreshold(threshold.Value) : null,
            gate.Detector.Hysteresis, gate.Detector.LocalExtrema);
        return new(gate.Id, gate.Revision,
            new GateWindowDefinition(gate.Window.StartReference, offset, duration),
            gate.SignalDomain, detector, gate.RequestedMeasures);
    }
    private sealed class Observer<T>(Action<T> next, Action? completed = null) : IObserver<T>
    {
        public void OnNext(T value) => next(value);
        public void OnError(Exception error) => Assert.Fail(error.ToString());
        public void OnCompleted() => completed?.Invoke();
    }
    private sealed class BlockingCompiler : IGatePlanPreviewCompiler
    {
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public GatePlanPreviewCompilationResult Compile(GatePlanDraft draft, GatePreviewIdentity previewIdentity)
        {
            Entered.TrySetResult(); Release.Task.GetAwaiter().GetResult();
            return new GatePlanPreviewCompiler().Compile(draft, previewIdentity);
        }
    }
    private sealed class BlockingStampReader(IGatePlanAuthorityReader inner) : IGatePlanAuthorityReader
    {
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public GatePlanAuthoritySnapshot Read(GatePlanAuthorityKey authority) => inner.Read(authority);
        public IObservable<GatePlanAuthoritySnapshot> Observe(GatePlanAuthorityKey authority) => inner.Observe(authority);
        public GatePlanAuthorityReadStampResult TryReadStamp(GatePlanAuthorityKey authority,
            GateAuthorityGeneration expectedGeneration)
        {
            Entered.TrySetResult(); Release.Task.GetAwaiter().GetResult();
            return inner.TryReadStamp(authority, expectedGeneration);
        }
    }
    private sealed class StampThenChangeReader(IGatePlanAuthorityReader inner, Action afterStamp) : IGatePlanAuthorityReader
    {
        private int _changed;
        public GatePlanAuthoritySnapshot Read(GatePlanAuthorityKey authority) => inner.Read(authority);
        public IObservable<GatePlanAuthoritySnapshot> Observe(GatePlanAuthorityKey authority) => inner.Observe(authority);
        public GatePlanAuthorityReadStampResult TryReadStamp(GatePlanAuthorityKey authority,
            GateAuthorityGeneration expectedGeneration)
        {
            GatePlanAuthorityReadStampResult result = inner.TryReadStamp(authority, expectedGeneration);
            if (Interlocked.Exchange(ref _changed, 1) == 0) afterStamp();
            return result;
        }
    }
    private sealed class TestAuthorityReader : IGatePlanAuthorityReader
    {
        private readonly object _gate = new();
        private readonly List<IObserver<GatePlanAuthoritySnapshot>> _observers = [];
        private GatePlanAuthoritySnapshot _current;
        internal TestAuthorityReader(CommittedGatePlanSnapshot initial) => _current = new(initial, new(1), new(1));
        public GatePlanAuthoritySnapshot Read(GatePlanAuthorityKey authority)
        { lock (_gate) { EnsureKey(authority); return _current; } }
        public IObservable<GatePlanAuthoritySnapshot> Observe(GatePlanAuthorityKey authority)
        { EnsureKey(authority); return new Observable(this); }
        public GatePlanAuthorityReadStampResult TryReadStamp(GatePlanAuthorityKey authority,
            GateAuthorityGeneration expectedGeneration)
        {
            lock (_gate)
            {
                EnsureKey(authority);
                if (_current.Generation != expectedGeneration)
                    return new(GatePlanAuthorityReadStampStatus.GenerationChanged, _current);
                return new(GatePlanAuthorityReadStampStatus.Acquired, _current,
                    new(authority, _current, _current.Generation, _current.PublicationSequence));
            }
        }
        internal void Install(CommittedGatePlanSnapshot committed)
        {
            IObserver<GatePlanAuthoritySnapshot>[] observers;
            GatePlanAuthoritySnapshot next;
            lock (_gate)
            {
                GateAuthorityGeneration generation = new(_current.Generation.Value + 1);
                next = new(committed, generation, new(_current.PublicationSequence.Value + 1));
                _current = next; observers = _observers.ToArray();
            }
            foreach (IObserver<GatePlanAuthoritySnapshot> observer in observers) observer.OnNext(next);
        }
        private void EnsureKey(GatePlanAuthorityKey authority)
        { if (authority != _current.Authority) throw new KeyNotFoundException(); }
        private sealed class Observable(TestAuthorityReader owner) : IObservable<GatePlanAuthoritySnapshot>
        {
            public IDisposable Subscribe(IObserver<GatePlanAuthoritySnapshot> observer)
            {
                lock (owner._gate) { owner._observers.Add(observer); observer.OnNext(owner._current); }
                return new Subscription(owner, observer);
            }
        }
        private sealed class Subscription(TestAuthorityReader owner, IObserver<GatePlanAuthoritySnapshot> observer) : IDisposable
        {
            public void Dispose() { lock (owner._gate) owner._observers.Remove(observer); }
        }
    }
}
