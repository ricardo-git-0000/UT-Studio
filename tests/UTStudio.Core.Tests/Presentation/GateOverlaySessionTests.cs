using UTStudio.Application;
using UTStudio.Contracts.Application;
using UTStudio.Contracts.Inspection;
using UTStudio.Contracts.Processing;
using UTStudio.Core.Tests.Processing;
using UTStudio.Domain.Acquisition;
using UTStudio.Domain.Inspection;
using UTStudio.Domain.Processing;
using UTStudio.Presentation;
using UTStudio.Processing.Core;
using UTStudio.Visualization.Core;

namespace UTStudio.Core.Tests.Presentation;

[TestClass]
public sealed class GateOverlaySessionTests
{
    private static readonly InspectionId Inspection = new(Guid.Parse("71000000-0000-0000-0000-000000000001"));
    private static readonly AcquisitionRunId Run = new(Guid.Parse("72000000-0000-0000-0000-000000000002"));

    [TestMethod]
    public async Task PreviewTransfersExactCompiledArtifactAndPublishesCoherentComposite()
    {
        Fixture fixture = Fixture.Create();
        await using GateOverlaySession overlay = fixture.Overlay;
        GatePlanDraft draft = fixture.BeginDraft(.1);

        GateOverlayRequestOutcome outcome = await overlay.PreviewAsync(draft);

        Assert.AreEqual(GateOverlayRequestOutcome.Completed, outcome);
        Assert.AreSame(fixture.Compiler.LastCompiled, fixture.Evaluator.ReceivedPlans.Single());
        Assert.AreEqual(GateGeometrySourceKind.Preview, overlay.Snapshot.Source!.Kind);
        Assert.AreEqual(draft.BaseRevision, overlay.Snapshot.Source.Revision);
        Assert.IsTrue(overlay.Snapshot.Source.Preview.IsValid);
        AScanCompositeVisualSnapshot composite = overlay.CompositeSnapshot!;
        Assert.AreSame(fixture.Signal, composite.Signal);
        Assert.AreSame(overlay.Snapshot, composite.Gates);
        Assert.AreEqual(fixture.Signal.TraceIdentity, composite.Gates.TraceIdentity);
        Assert.AreEqual(fixture.Signal.InspectionRevision, composite.Gates.InspectionRevision);
        Assert.IsFalse(typeof(AScanCompositeVisualSnapshot).GetProperties()
            .Any(property => property.PropertyType == typeof(CompiledGatePlan)));
    }

    [TestMethod]
    public async Task RunnerCoalescesToOneActiveAndOneLatestPendingWithoutRegressing()
    {
        var evaluator = new ControlledEvaluator();
        Fixture fixture = Fixture.Create(evaluator);
        await using GateOverlaySession overlay = fixture.Overlay;
        Task<GateOverlayRequestOutcome> first = overlay.PreviewAsync(fixture.BeginDraft(.10)).AsTask();
        await evaluator.FirstEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Task<GateOverlayRequestOutcome> second = overlay.PreviewAsync(fixture.Draft(.20)).AsTask();
        Task<GateOverlayRequestOutcome> third = overlay.PreviewAsync(fixture.Draft(.30)).AsTask();
        Assert.AreEqual(GateOverlayRequestOutcome.Superseded,
            await second.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.AreEqual(1, evaluator.Calls);

        evaluator.ReleaseFirst.TrySetResult();
        Assert.AreEqual(GateOverlayRequestOutcome.Superseded,
            await first.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.AreEqual(GateOverlayRequestOutcome.Completed,
            await third.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.AreEqual(2, evaluator.Calls);
        Assert.AreEqual(.30, fixture.Editor.Snapshot.Draft!.Gates[0].Detector.Threshold!.NormalizedValue);
        Assert.AreEqual(3ul, overlay.Snapshot.EvaluationSequence);
    }

    [TestMethod]
    public async Task ContinuousCaptureKeepsNonceAndContextWhileLateOwnResultCannotRegress()
    {
        var evaluator = new ControlledEvaluator();
        evaluator.ReleaseFirst.TrySetResult();
        Fixture fixture = Fixture.Create(evaluator);
        await using GateOverlaySession overlay = fixture.Overlay;
        Assert.AreEqual(GateOverlayRequestOutcome.Completed,
            await overlay.PreviewAsync(fixture.BeginDraft(.10)));
        Assert.IsTrue(overlay.Select(new GateId("A"), GateHandleKind.Threshold));
        GateCaptureGeneration capture = overlay.BeginGateCapture()!.Value;

        Task<GateOverlayRequestOutcome> firstMove = overlay.MoveCapturedHandleAsync(.20).AsTask();
        Task<GateOverlayRequestOutcome> latestMove = overlay.MoveCapturedHandleAsync(.30).AsTask();
        _ = await firstMove; _ = await latestMove;

        Assert.AreEqual(capture.Nonce, overlay.Snapshot.Capture!.Value.Nonce);
        Assert.AreEqual(capture.ContextGeneration, overlay.Snapshot.Capture!.Value.ContextGeneration);
        Assert.AreEqual(.30, fixture.Editor.Snapshot.Draft!.Gates[0].Detector.Threshold!.NormalizedValue);
        overlay.SetVisualContext(fixture.Signal, null, Fixture.Transform(2));
        Assert.IsNull(overlay.Snapshot.Capture);
        Assert.AreEqual(PortablePointerCaptureOwner.None, overlay.GestureOwner);
    }

    [TestMethod]
    public async Task EscapeRestoresCompatibleCheckpointWithoutRecompileAndCloseDrainsInflight()
    {
        var evaluator = new ControlledEvaluator();
        evaluator.ReleaseFirst.TrySetResult();
        Fixture fixture = Fixture.Create(evaluator);
        GateOverlaySession overlay = fixture.Overlay;
        Assert.AreEqual(GateOverlayRequestOutcome.Completed,
            await overlay.PreviewAsync(fixture.BeginDraft(.10)));
        int compilations = fixture.Compiler.Compilations;
        Assert.IsTrue(overlay.Select(new GateId("A"), GateHandleKind.Threshold));
        Assert.IsNotNull(overlay.BeginGateCapture());

        fixture.Editor.UpdateDraft(fixture.Draft(.40));
        Assert.IsTrue(overlay.RestoreStablePreview());
        Assert.AreEqual(compilations, fixture.Compiler.Compilations);
        Assert.AreEqual(.10, fixture.Editor.Snapshot.Draft!.Gates[0].Detector.Threshold!.NormalizedValue);

        var blocked = new ControlledEvaluator();
        Fixture closing = Fixture.Create(blocked);
        GateOverlaySession closingOverlay = closing.Overlay;
        Task<GateOverlayRequestOutcome> active = closingOverlay.PreviewAsync(closing.BeginDraft(.2)).AsTask();
        await blocked.FirstEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Task dispose = closingOverlay.DisposeAsync().AsTask();
        blocked.ReleaseFirst.TrySetResult();
        await dispose.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.AreEqual(GateOverlayRequestOutcome.Closed, await active);
        Assert.AreEqual(GateOverlayGeometryStatus.Closed, closingOverlay.Snapshot.Status);
        await overlay.DisposeAsync();
    }

    [TestMethod]
    public async Task GestureArbitrationAndHitTestingArePortableAndExclusive()
    {
        Fixture fixture = Fixture.Create();
        await using GateOverlaySession overlay = fixture.Overlay;
        Assert.AreEqual(GateOverlayRequestOutcome.Completed,
            await overlay.PreviewAsync(fixture.BeginDraft(.25)));
        GateHandleVisualState threshold = overlay.Snapshot.Handles.Single(x => x.Kind == GateHandleKind.Threshold);
        Assert.AreSame(threshold, GateOverlayHitTester.HitTest(overlay.Snapshot, threshold.X, threshold.Y, 1));
        Assert.IsTrue(overlay.TryBeginCursorCapture());
        Assert.IsNull(overlay.BeginGateCapture());
        overlay.EndCapture();
        Assert.IsTrue(overlay.Select(threshold.GateId, threshold.Kind));
        Assert.IsNotNull(overlay.BeginGateCapture());
        Assert.IsFalse(overlay.TryBeginCursorCapture());
        Assert.AreEqual(GateOverlayRequestOutcome.Completed,
            await overlay.HandleKeyAsync(GateOverlayKeyIntent.Increment));
        Assert.AreEqual(.26, fixture.Editor.Snapshot.Draft!.Gates[0].Detector.Threshold!.NormalizedValue,
            1e-12);
        Assert.AreEqual(GateOverlayRequestOutcome.Completed,
            await overlay.HandleKeyAsync(GateOverlayKeyIntent.ConfirmPreview));
        Assert.AreEqual(PortablePointerCaptureOwner.None, overlay.GestureOwner);
    }

    [TestMethod]
    public async Task ExternalDraftChangeInvalidatesCaptureAndCapacityFailureRecovers()
    {
        var evaluator = new CapacityThenSuccessEvaluator();
        Fixture fixture = Fixture.Create(evaluator);
        await using GateOverlaySession overlay = fixture.Overlay;
        Assert.AreEqual(GateOverlayRequestOutcome.CapacityExceeded,
            await overlay.PreviewAsync(fixture.BeginDraft(.10)));
        Assert.AreEqual(GateOverlayGeometryStatus.Unavailable, overlay.Snapshot.Status);
        Assert.AreEqual(GateOverlayRequestOutcome.Completed,
            await overlay.PreviewAsync(fixture.Draft(.20)));
        Assert.IsTrue(overlay.Select(new GateId("A"), GateHandleKind.Threshold));
        GateCaptureGeneration capture = overlay.BeginGateCapture()!.Value;
        var stale = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        overlay.SnapshotChanged += snapshot =>
        { if (snapshot.Status == GateOverlayGeometryStatus.Stale) stale.TrySetResult(); };

        fixture.Editor.BeginEdit();
        await stale.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.IsNull(overlay.Snapshot.Capture);
        Assert.IsGreaterThan(capture.ContextGeneration, overlay.Snapshot.ContextGeneration);
        Assert.AreEqual(PortablePointerCaptureOwner.None, overlay.GestureOwner);
        Assert.AreEqual(2, evaluator.Calls);
    }

    [TestMethod]
    public async Task EvaluatorFaultIsTypedAndSelectionChangeCancelsObservedCapture()
    {
        Fixture faulted = Fixture.Create(new ThrowingEvaluator());
        await using (GateOverlaySession overlay = faulted.Overlay)
        {
            Assert.AreEqual(GateOverlayRequestOutcome.Faulted,
                await overlay.PreviewAsync(faulted.BeginDraft(.1)));
            Assert.AreEqual(GateOverlayGeometryStatus.Faulted, overlay.Snapshot.Status);
            Assert.AreEqual("gate-overlay-evaluation-fault", overlay.Snapshot.ErrorCode);
        }

        Fixture fixture = Fixture.Create();
        await using GateOverlaySession current = fixture.Overlay;
        Assert.AreEqual(GateOverlayRequestOutcome.Completed,
            await current.PreviewAsync(fixture.BeginDraft(.2)));
        GateHandleVisualState start = current.Snapshot.Handles.Single(x => x.Kind == GateHandleKind.Start);
        GateHandleVisualState threshold = current.Snapshot.Handles.Single(x => x.Kind == GateHandleKind.Threshold);
        Assert.IsTrue(current.Select(start.GateId, start.Kind));
        GateCaptureGeneration capture = current.BeginGateCapture()!.Value;
        Assert.IsTrue(current.Select(threshold.GateId, threshold.Kind));
        Assert.IsNull(current.Snapshot.Capture);
        Assert.AreEqual(PortablePointerCaptureOwner.None, current.GestureOwner);
        Assert.IsGreaterThan(capture.ContextGeneration, current.Snapshot.ContextGeneration);
    }

    [TestMethod]
    public async Task DragCoalescesWhileEvaluationIsBlockedWithoutChangingCaptureIdentity()
    {
        var evaluator = new BlockSecondEvaluator();
        Fixture fixture = Fixture.Create(evaluator);
        await using GateOverlaySession overlay = fixture.Overlay;
        Assert.AreEqual(GateOverlayRequestOutcome.Completed,
            await overlay.PreviewAsync(fixture.BeginDraft(.10)));
        Assert.IsTrue(overlay.Select(new GateId("A"), GateHandleKind.Threshold));
        GateCaptureGeneration capture = overlay.BeginGateCapture()!.Value;

        Task<GateOverlayRequestOutcome> move1 = overlay.MoveCapturedHandleAsync(.20).AsTask();
        await evaluator.SecondEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Task<GateOverlayRequestOutcome> move2 = overlay.MoveCapturedHandleAsync(.30).AsTask();
        Task<GateOverlayRequestOutcome> move3 = overlay.MoveCapturedHandleAsync(.40).AsTask();
        Assert.AreEqual(GateOverlayRequestOutcome.Superseded, await move2);
        evaluator.ReleaseSecond.TrySetResult();
        Assert.AreEqual(GateOverlayRequestOutcome.Superseded, await move1);
        Assert.AreEqual(GateOverlayRequestOutcome.Completed, await move3);
        Assert.AreEqual(3, evaluator.Calls);
        Assert.AreEqual(.40, fixture.Editor.Snapshot.Draft!.Gates[0].Detector.Threshold!.NormalizedValue);
        Assert.AreEqual(capture.Nonce, overlay.Snapshot.Capture!.Value.Nonce);
        Assert.AreEqual(capture.ContextGeneration, overlay.Snapshot.Capture!.Value.ContextGeneration);
    }

    [TestMethod]
    public async Task CallerCancellationRecoversAndDisposeIsSharedAndIdempotent()
    {
        var evaluator = new ControlledEvaluator();
        Fixture fixture = Fixture.Create(evaluator);
        GateOverlaySession overlay = fixture.Overlay;
        using var cancellation = new CancellationTokenSource();
        Task<GateOverlayRequestOutcome> cancelled = overlay.PreviewAsync(
            fixture.BeginDraft(.1), cancellation.Token).AsTask();
        await evaluator.FirstEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        Assert.AreEqual(GateOverlayRequestOutcome.Cancelled, await cancelled);
        Assert.AreEqual(GateOverlayRequestOutcome.Completed,
            await overlay.PreviewAsync(fixture.Draft(.2)));
        ValueTask firstDispose = overlay.DisposeAsync();
        ValueTask secondDispose = overlay.DisposeAsync();
        await firstDispose; await secondDispose;
        Assert.AreEqual(GateOverlayGeometryStatus.Closed, overlay.Snapshot.Status);
    }

    [TestMethod]
    public async Task KeyboardFocusBoundsAndReentrantPublicationRemainCoherent()
    {
        Fixture fixture = Fixture.Create();
        await using GateOverlaySession overlay = fixture.Overlay;
        bool reentered = false;
        var observed = new List<ulong>();
        overlay.SnapshotChanged += snapshot =>
        {
            observed.Add(snapshot.ContextGeneration);
            if (!reentered && snapshot.Status == GateOverlayGeometryStatus.Current)
            {
                reentered = true;
                overlay.SetVisualContext(fixture.Signal, null, Fixture.Transform(1));
            }
        };
        Assert.AreEqual(GateOverlayRequestOutcome.Completed,
            await overlay.PreviewAsync(fixture.BeginDraft(.99)));
        Assert.AreEqual(GateOverlayRequestOutcome.Completed,
            await overlay.HandleKeyAsync(GateOverlayKeyIntent.FocusNext));
        Assert.IsNotNull(overlay.Snapshot.SelectedGate);
        Assert.AreNotEqual(GateHandleKind.Unknown, overlay.Snapshot.FocusedHandle);
        Assert.IsTrue(overlay.Select(new GateId("A"), GateHandleKind.Threshold));
        Assert.IsNotNull(overlay.BeginGateCapture());
        Assert.AreEqual(GateOverlayRequestOutcome.Completed,
            await overlay.MoveCapturedHandleAsync(4));
        Assert.AreEqual(1d, fixture.Editor.Snapshot.Draft!.Gates[0].Detector.Threshold!.NormalizedValue);
        Assert.IsTrue(observed.Zip(observed.Skip(1), (left, right) => right >= left).All(x => x));
    }

    [TestMethod]
    public async Task ConcurrentPreviewCallersKeepTheirOwnCompiledArtifacts()
    {
        var evaluator = new ControlledEvaluator();
        Fixture fixture = Fixture.Create(evaluator);
        await using GateOverlaySession overlay = fixture.Overlay;
        fixture.Editor.BeginEdit();
        GatePlanDraft firstDraft = fixture.Draft(.11);
        GatePlanDraft secondDraft = fixture.Draft(.22);

        Task<GateOverlayRequestOutcome> first = Task.Run(async () => await overlay.PreviewAsync(firstDraft));
        await evaluator.FirstEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Task<GateOverlayRequestOutcome> second = Task.Run(async () => await overlay.PreviewAsync(secondDraft));
        Assert.IsTrue(SpinWait.SpinUntil(() => fixture.Compiler.Compilations == 2, TimeSpan.FromSeconds(5)));
        Assert.AreEqual(.11, evaluator.ReceivedPlans.Single().Source.Gates[0].Detector.Threshold!.NormalizedValue);
        evaluator.ReleaseFirst.TrySetResult();
        Assert.AreEqual(GateOverlayRequestOutcome.Superseded, await first);
        Assert.AreEqual(GateOverlayRequestOutcome.Completed, await second);

        double[] thresholds = fixture.Evaluator.ReceivedPlans.Select(plan =>
            plan.Source.Gates[0].Detector.Threshold!.NormalizedValue).ToArray();
        CollectionAssert.AreEqual(new[] { .11, .22 }, thresholds);
        Assert.AreEqual(2, fixture.Compiler.Compilations);
    }

    private sealed class Fixture
    {
        private Fixture(TrackingCompiler compiler, GatePlanEditorSession editor,
            RecordingEvaluator evaluator, GateOverlaySession overlay, AScanSnapshot signal)
        { Compiler = compiler; Editor = editor; Evaluator = evaluator; Overlay = overlay; Signal = signal; }
        internal TrackingCompiler Compiler { get; }
        internal GatePlanEditorSession Editor { get; }
        internal RecordingEvaluator Evaluator { get; }
        internal GateOverlaySession Overlay { get; }
        internal AScanSnapshot Signal { get; }

        internal static Fixture Create(RecordingEvaluator? evaluator = null)
        {
            GateDefinition gate = GateTestPlan.Gate("A", detector: GateDetectorKind.FirstRisingCrossing,
                threshold: .05, durationSeconds: 6e-6);
            GatePlanCompilationResult committedResult = new GatePlanCompiler().Compile(GateTestPlan.Plan([gate]));
            var committed = new CommittedGatePlanSnapshot(Inspection, committedResult.CompiledPlan!);
            var authority = new InMemoryGatePlanAuthorityOwner([committed]); authority.Seal();
            var compiler = new TrackingCompiler();
            var editor = new GatePlanEditorSession(committed.AuthorityKey, authority.Reader, compiler);
            evaluator ??= new RecordingEvaluator();
            var overlay = new GateOverlaySession(editor, evaluator);
            AScanSnapshot signal = SignalSnapshot();
            overlay.SetVisualContext(signal, null, Transform(1));
            return new(compiler, editor, evaluator, overlay, signal);
        }

        internal GatePlanDraft BeginDraft(double threshold)
        { Editor.BeginEdit(); return Draft(threshold); }
        internal GatePlanDraft Draft(double threshold)
        {
            GatePlanEditorSnapshot snapshot = Editor.Snapshot;
            GatePlanDraft current = snapshot.Draft!;
            GateDefinition gate = current.Gates[0];
            var changed = new GateDefinition(gate.Id, gate.Revision, gate.Window, gate.SignalDomain,
                new(gate.Detector.Kind, new(threshold), gate.Detector.Hysteresis, gate.Detector.LocalExtrema),
                gate.RequestedMeasures);
            return new(current.Authority, current.BaseRevision, current.SchemaVersion,
                current.AlgorithmVersion, [changed]);
        }

        internal static GateVisualTransform Transform(ulong version) => new(version,
            ScanTimeViewportOperations.Create(0, 8e-6), GateAmplitudePresentationKind.RawRf,
            0, 800, 200, -200, 800, 400, 1, 1);

        private static AScanSnapshot SignalSnapshot()
        {
            var configuration = new ConventionalAcquisitionConfiguration(new PhysicalChannelId(0), 8, 1_000_000);
            var metadata = new ConventionalUtFrameMetadata(new UtSourceId("overlay-test"), Run,
                configuration, DateTimeOffset.UnixEpoch);
            AScanSnapshot signal = new AScanProjector().Project(metadata, 7, TimeSpan.Zero,
                new short[] { 0, 100, 0, 0, 0, 0, 0, 0 }, 1);
            var stream = new UtStreamIdentity(Inspection, Run, metadata.SourceId, configuration.PhysicalChannelId);
            signal.SetAuthority(new(stream, 1), new(1));
            return signal;
        }
    }

    private sealed class TrackingCompiler : IGatePlanPreviewCompiler
    {
        internal int Compilations { get; private set; }
        internal CompiledGatePlan? LastCompiled { get; private set; }
        public GatePlanCompilationResult Compile(GatePlanDraft draft)
        {
            Compilations++;
            var plan = new GatePlan(draft.Authority.PlanId, draft.BaseRevision, draft.SchemaVersion,
                draft.AlgorithmVersion, draft.Gates);
            GatePlanCompilationResult result = new GatePlanCompiler().Compile(plan);
            LastCompiled = result.CompiledPlan; return result;
        }
    }

    private class RecordingEvaluator : IGateGeometryEvaluator
    {
        internal List<CompiledGatePlan> ReceivedPlans { get; } = [];
        internal virtual ValueTask BeforeAsync(CancellationToken token) => ValueTask.CompletedTask;
        public virtual async ValueTask<GateGeometryEvaluationResult> EvaluateAsync(GateGeometryEvaluationRequest request,
            CompiledGatePlan compiledPlan, CancellationToken cancellationToken = default)
        {
            ReceivedPlans.Add(compiledPlan); await BeforeAsync(cancellationToken);
            GateGeometryItemSnapshot[] gates = compiledPlan.DeclaredGates.Select(compiled =>
            {
                GateDefinition gate = compiled.Definition;
                double start = gate.Window.StartOffsetSeconds;
                return new GateGeometryItemSnapshot(gate.Id, gate.Revision, gate.SignalDomain,
                    GateResultStatus.Detected, null, start, start + gate.Window.DurationSeconds,
                    gate.Detector.Threshold?.NormalizedValue);
            }).ToArray();
            return new(GateGeometryEvaluationStatus.Completed, request,
                new(request.Source, request.TraceIdentity, request.ExpectedInspectionRevision, gates));
        }
    }

    private sealed class ControlledEvaluator : RecordingEvaluator
    {
        private int _calls;
        internal int Calls => Volatile.Read(ref _calls);
        internal TaskCompletionSource FirstEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource ReleaseFirst { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal override async ValueTask BeforeAsync(CancellationToken token)
        {
            if (Interlocked.Increment(ref _calls) != 1) return;
            FirstEntered.TrySetResult(); await ReleaseFirst.Task.WaitAsync(token);
        }
    }

    private sealed class CapacityThenSuccessEvaluator : RecordingEvaluator
    {
        private int _calls;
        internal int Calls => _calls;
        public override async ValueTask<GateGeometryEvaluationResult> EvaluateAsync(GateGeometryEvaluationRequest request,
            CompiledGatePlan compiledPlan, CancellationToken cancellationToken = default)
        {
            _calls++;
            if (_calls == 1) return new(GateGeometryEvaluationStatus.CapacityExceeded, request);
            return await base.EvaluateAsync(request, compiledPlan, cancellationToken);
        }
    }

    private sealed class ThrowingEvaluator : RecordingEvaluator
    {
        public override ValueTask<GateGeometryEvaluationResult> EvaluateAsync(GateGeometryEvaluationRequest request,
            CompiledGatePlan compiledPlan, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("synthetic evaluator fault");
    }

    private sealed class BlockSecondEvaluator : RecordingEvaluator
    {
        private int _calls;
        internal int Calls => _calls;
        internal TaskCompletionSource SecondEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource ReleaseSecond { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal override async ValueTask BeforeAsync(CancellationToken token)
        {
            if (Interlocked.Increment(ref _calls) != 2) return;
            SecondEntered.TrySetResult(); await ReleaseSecond.Task.WaitAsync(token);
        }
    }
}
