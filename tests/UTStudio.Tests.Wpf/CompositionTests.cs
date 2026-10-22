using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.Windows.Automation;
using System.Buffers;
using System.Threading.Channels;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using UTStudio.Acquisition.Simulator;
using UTStudio.Application;
using UTStudio.Inspection.Core;
using UTStudio.App.Wpf;
using UTStudio.App.Wpf.Composition;
using UTStudio.App.Wpf.Services;
using UTStudio.Contracts.Acquisition;
using UTStudio.Contracts.Application;
using UTStudio.Contracts.Inspection;
using UTStudio.Contracts.Processing;
using UTStudio.Domain.Acquisition;
using UTStudio.Domain.Inspection;
using UTStudio.Domain.Processing;
using UTStudio.Presentation;
using UTStudio.Processing.Core;
using UTStudio.Visualization.Core;
using UTStudio.Visualization.Core.Scan2D;
using UTStudio.Visualization.Wpf.Controls;
using UTStudio.Visualization.Wpf.Rendering;

namespace UTStudio.Tests.Wpf;

[TestClass]
public sealed class CompositionTests
{
    [TestMethod]
    public Task Productive_gate_vertical_apply_stop_and_close_is_owned_and_repeatable() => StaTest.Run(async () =>
    {
        var lifecycle = new List<string>();
        var source = new GateControlledSource();
        var historical = new HistoricalAdmissionProbe();
        ProductiveGateInspectionDiagnostics? finalGateDiagnostics = null;
        var configuration = new ConventionalAcquisitionConfiguration(new(0), 16, 1_000_000, 0, UtSignalMode.Rf);
        DesktopRuntime runtime = await DesktopRuntimeFactory.CreateAsync(
            new WpfUiDispatcher(Dispatcher.CurrentDispatcher), source, configuration,
            new BScanOptions(4_000_000, 8, 2, 2), TimeProvider.System, lifecycleObserver: lifecycle.Add,
            gateHooks: new(SchedulerDecorator: historical.Decorate,
                BeforeAuthorityClose: snapshot => finalGateDiagnostics = snapshot));
        bool shutdownCompleted = false;
        try
        {
            static async Task ExecuteWhenEnabled(IAsyncRelayCommand command)
            {
                if (!command.CanExecute(null))
                {
                    var enabled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    void Changed(object? _, EventArgs __)
                    { if (command.CanExecute(null)) enabled.TrySetResult(); }
                    command.CanExecuteChanged += Changed;
                    try { await enabled.Task.WaitAsync(TimeSpan.FromSeconds(5)); }
                    finally { command.CanExecuteChanged -= Changed; }
                }
                await command.ExecuteAsync(null).WaitAsync(TimeSpan.FromSeconds(10));
            }
            var services = runtime.Host.Services;
            var window = services.GetRequiredService<MainWindow>();
            var host = services.GetRequiredService<AScanHostViewModel>();
            ProductiveGateInspectionScope scope = services.GetRequiredService<ProductiveGateInspectionScope>();
            window.Show(); window.AScan.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent, window.AScan));
            window.UpdateLayout();
            await runtime.Host.StartAsync();
            await host.StartCommand.ExecuteAsync(null);
            GateEditorViewCoordinator editor = window.AScan.ViewModel!.GateEditor!;
            async Task WaitForNumericAsync(Func<bool> condition)
            {
                if (condition()) return;
                var changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                void NumericChanged(object? _, System.ComponentModel.PropertyChangedEventArgs __)
                { if (condition()) changed.TrySetResult(); }
                editor.PropertyChanged += NumericChanged;
                try { await changed.Task.WaitAsync(TimeSpan.FromSeconds(5)); }
                finally { editor.PropertyChanged -= NumericChanged; }
            }
            var visualReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            void ObserveEditor(object? _, System.ComponentModel.PropertyChangedEventArgs __)
            {
                if (editor.CompositeVisual?.HasAuthoritativeIdentity == true) visualReady.TrySetResult();
            }
            editor.PropertyChanged += ObserveEditor;
            GateControlledSource.FrameOwner frame = source.QueueFrame();
            await Task.WhenAll(frame.Returned.Task, visualReady.Task).WaitAsync(TimeSpan.FromSeconds(5));
            editor.PropertyChanged -= ObserveEditor;

            await ExecuteWhenEnabled(editor.BeginEditCommand);
            window.UpdateLayout();
            await Dispatcher.Yield();
            if (editor.State.Overlay.Status != GateOverlayGeometryStatus.Current ||
                editor.State.Overlay.Handles.Count != 2)
            {
                var editable = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                void EditingReady(object? _, System.ComponentModel.PropertyChangedEventArgs __)
                {
                    if (editor.State.Overlay.Status == GateOverlayGeometryStatus.Current &&
                        editor.State.Overlay.Handles.Count == 2) editable.TrySetResult();
                }
                editor.PropertyChanged += EditingReady;
                try { await editable.Task.WaitAsync(TimeSpan.FromSeconds(5)); }
                finally { editor.PropertyChanged -= EditingReady; }
            }
            GatePlanDraft initialDraft = editor.State.Editor.Draft!;
            GateDefinition initialGate = initialDraft.Gates.Single();
            Assert.AreEqual("default-global-maximum", editor.SelectedGateId);
            Assert.AreEqual(0d, editor.SelectedGateStartMicroseconds);
            Assert.AreEqual(16d, editor.SelectedGateEndMicroseconds);
            Assert.IsFalse(editor.SelectedGateHasThreshold);
            Assert.IsFalse(editor.State.Overlay.Handles.Any(x => x.Kind == GateHandleKind.Threshold));
            Assert.AreEqual(Visibility.Visible,
                ((FrameworkElement)window.AScan.FindName("GateEditorPanel")).Visibility);
            var startEditor = (TextBox)window.AScan.FindName("GateStartValue");
            var endEditor = (TextBox)window.AScan.FindName("GateEndValue");
            Assert.AreEqual(Visibility.Visible, startEditor.Visibility);
            Assert.AreEqual(Visibility.Visible, endEditor.Visibility);
            Assert.AreEqual(Visibility.Collapsed,
                ((FrameworkElement)window.AScan.FindName("GateThresholdPanel")).Visibility);
            GateHandleVisualState startHandle = editor.State.Overlay.Handles.Single(x => x.Kind == GateHandleKind.Start);
            GateHandleVisualState endHandle = editor.State.Overlay.Handles.Single(x => x.Kind == GateHandleKind.End);
            AScanControl renderer = window.AScan.Renderer;
            await Task.Delay(AScanVisualDelivery.MinimumPublicationInterval);
            renderer.RefreshSnapshot();
            GateVisualTransform editingTransform = renderer.DisplayedVisualSnapshot!.Transform;
            Assert.IsGreaterThanOrEqualTo(editingTransform.TimeOrigin, startHandle.X);
            Assert.IsLessThanOrEqualTo(editingTransform.TimeOrigin + editingTransform.TimeLength,
                endHandle.X);
            Assert.AreSame(startHandle, GateOverlayHitTester.HitTest(editor.State.Overlay,
                startHandle.X, startHandle.Y, 1));
            Assert.AreSame(endHandle, GateOverlayHitTester.HitTest(editor.State.Overlay,
                endHandle.X, endHandle.Y, 1));
            Assert.AreEqual(initialGate.Window.StartOffsetSeconds, startHandle.Value, 1e-15);
            Assert.AreEqual(initialGate.Window.StartOffsetSeconds + initialGate.Window.DurationSeconds,
                endHandle.Value, 1e-15);
            Assert.IsTrue(LinearAxisTransform.TryDataToCoordinate(startHandle.Value,
                editingTransform.TimeViewport.VisibleMinimumSeconds,
                editingTransform.TimeViewport.VisibleMaximumSeconds, editingTransform.TimeOrigin,
                editingTransform.TimeLength, out double exactStart));
            Assert.IsTrue(LinearAxisTransform.TryDataToCoordinate(endHandle.Value,
                editingTransform.TimeViewport.VisibleMinimumSeconds,
                editingTransform.TimeViewport.VisibleMaximumSeconds, editingTransform.TimeOrigin,
                editingTransform.TimeLength, out double exactEnd));
            Assert.AreEqual(exactStart, startHandle.X + startHandle.DragCoordinateOffset, 1e-9);
            Assert.AreEqual(exactEnd, endHandle.X + endHandle.DragCoordinateOffset, 1e-9);

            AScanCompositeVisualSnapshot shown = editor.CompositeVisual!;
            Assert.IsTrue(editor.TryBeginCapture(shown, startHandle.Identity, out GateCaptureGeneration startCapture));
            for (int index = 1; index <= 1_000; index++)
                editor.HandleInteraction(new(GateOverlayInteractionKind.MoveCapture, shown, startHandle.Identity,
                    startCapture, index * 1e-9));
            editor.HandleInteraction(new(GateOverlayInteractionKind.EndCapture, shown, startHandle.Identity,
                startCapture, null));
            await WaitForNumericAsync(() => Math.Abs(editor.SelectedGateStartMicroseconds!.Value - 1d) < 1e-9);
            await WaitForNumericAsync(() => editor.State.Overlay.Capture is null &&
                editor.State.Phase == GateEditorViewPhase.Previewed);

            GateHandleVisualState draggedEndHandle = editor.State.Overlay.Handles
                .Single(x => x.Kind == GateHandleKind.End);
            shown = editor.CompositeVisual!;
            Assert.IsTrue(editor.TryBeginCapture(shown, draggedEndHandle.Identity,
                out GateCaptureGeneration endCapture));
            editor.HandleInteraction(new(GateOverlayInteractionKind.MoveCapture, shown, draggedEndHandle.Identity,
                endCapture, 15e-6));
            editor.HandleInteraction(new(GateOverlayInteractionKind.EndCapture, shown, draggedEndHandle.Identity,
                endCapture, null));
            await WaitForNumericAsync(() => editor.SelectedGateEndMicroseconds < 16d);
            await WaitForNumericAsync(() => editor.State.Overlay.Capture is null &&
                editor.State.Phase == GateEditorViewPhase.Previewed);

            GatePlanDraft validBeforeParseError = editor.State.Editor.Draft!;
            double validStartBeforeParseError = editor.SelectedGateStartMicroseconds!.Value;
            Assert.IsTrue(editor.ApplyCommand.CanExecute(null));
            Assert.IsTrue(startEditor.Focus());
            startEditor.Text = "no-es-un-numero";
            Assert.IsTrue(endEditor.Focus());
            await Dispatcher.Yield();
            var startValidation = (TextBlock)window.AScan.FindName("GateStartValidation");
            Assert.AreEqual(Visibility.Visible, startValidation.Visibility);
            Assert.AreEqual("Introduzca un número válido.", startValidation.Text);
            Assert.AreEqual(AutomationLiveSetting.Assertive,
                AutomationProperties.GetLiveSetting(startValidation));
            Assert.Contains("Error", AutomationProperties.GetItemStatus(startEditor));
            Assert.AreEqual("no-es-un-numero", startEditor.Text);
            Assert.AreSame(validBeforeParseError, editor.State.Editor.Draft);
            Assert.AreEqual(validStartBeforeParseError, editor.SelectedGateStartMicroseconds);
            Assert.IsFalse(editor.ApplyCommand.CanExecute(null));
            GateControlledSource.FrameOwner invalidTypingFrame = source.QueueFrame();
            await invalidTypingFrame.Returned.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Dispatcher.Yield();
            Assert.AreEqual("no-es-un-numero", startEditor.Text,
                "A live publication must preserve invalid text so it can be corrected.");

            Assert.IsTrue(startEditor.Focus());
            startEditor.Text = "2";
            Assert.IsTrue(endEditor.Focus());
            await WaitForNumericAsync(() => editor.SelectedGateStartMicroseconds == 2d);
            Assert.AreEqual(Visibility.Collapsed, startValidation.Visibility);
            Assert.AreEqual(string.Empty, AutomationProperties.GetItemStatus(startEditor));
            Assert.IsNull(editor.NumericValidationMessage);
            Assert.IsTrue(editor.ApplyCommand.CanExecute(null));

            Assert.AreEqual(GateOverlayRequestOutcome.NotAvailable,
                await editor.PreviewHandleValueAsync(initialGate.Id, GateHandleKind.Start, 1));
            Assert.AreEqual(GateEditorViewPhase.ValidationFailed, editor.State.Phase);
            Assert.IsNotNull(editor.NumericValidationMessage);
            Assert.IsFalse(editor.ApplyCommand.CanExecute(null));
            Assert.IsTrue(startEditor.Focus());
            startEditor.Text = "2";
            GateControlledSource.FrameOwner startTypingFrame = source.QueueFrame();
            await startTypingFrame.Returned.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Dispatcher.Yield();
            Assert.AreEqual("2", startEditor.Text,
                "A live visual publication must not overwrite an in-progress numeric edit.");
            Assert.IsTrue(endEditor.Focus());
            await WaitForNumericAsync(() => editor.SelectedGateStartMicroseconds == 2d);

            endEditor.Text = "12";
            GateControlledSource.FrameOwner endTypingFrame = source.QueueFrame();
            await endTypingFrame.Returned.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Dispatcher.Yield();
            Assert.AreEqual("12", endEditor.Text,
                "A live visual publication must not overwrite an in-progress numeric edit.");
            Keyboard.ClearFocus();
            await WaitForNumericAsync(() => editor.SelectedGateEndMicroseconds == 12d);
            Assert.AreEqual(2d, editor.SelectedGateStartMicroseconds);
            Assert.AreEqual(12d, editor.SelectedGateEndMicroseconds);
            await ExecuteWhenEnabled(editor.PreviewCommand);
            if (editor.State.Phase != GateEditorViewPhase.Previewed)
            {
                var previewed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                void PreviewChanged(object? _, System.ComponentModel.PropertyChangedEventArgs __)
                { if (editor.State.Phase == GateEditorViewPhase.Previewed) previewed.TrySetResult(); }
                editor.PropertyChanged += PreviewChanged;
                try
                {
                    await previewed.Task.WaitAsync(TimeSpan.FromSeconds(5));
                }
                catch (TimeoutException error)
                {
                    throw new TimeoutException($"Preview did not stabilize: phase={editor.State.Phase}; " +
                        $"editor={editor.State.Editor.State}; overlay={editor.State.Overlay.Status}; " +
                        $"source={editor.State.Overlay.Source?.Kind}.", error);
                }
                finally { editor.PropertyChanged -= PreviewChanged; }
            }
            var authorityCommitted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var before = scope.Authority.Read(scope.AuthorityKey).Committed.Revision;
            using IDisposable authoritySubscription = scope.Authority.Observe(scope.AuthorityKey).Subscribe(
                new ActionObserver<GatePlanAuthoritySnapshot>(snapshot =>
                {
                    if (snapshot.Committed.Revision.Value == before.Value + 1) authorityCommitted.TrySetResult();
                }));
            Task applyWaiter = editor.ApplyCommand.ExecuteAsync(null);
            if (editor.ApplyCommand.IsRunning) editor.ApplyCommand.Cancel();
            await applyWaiter.WaitAsync(TimeSpan.FromSeconds(5));
            if (scope.Authority.Read(scope.AuthorityKey).Committed.Revision.Value != before.Value + 1)
                await authorityCommitted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            if (editor.State.Phase != GateEditorViewPhase.Committed)
            {
                var converged = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                void Converged(object? _, System.ComponentModel.PropertyChangedEventArgs __)
                { if (editor.State.Phase == GateEditorViewPhase.Committed) converged.TrySetResult(); }
                editor.PropertyChanged += Converged;
                try { await converged.Task.WaitAsync(TimeSpan.FromSeconds(5)); }
                finally { editor.PropertyChanged -= Converged; }
            }
            Assert.AreEqual(GateEditorViewPhase.Committed, editor.State.Phase);
            GateDefinition committedGate = scope.Authority.Read(scope.AuthorityKey).Committed.Plan.Gates.Single();
            Assert.AreEqual(2e-6, committedGate.Window.StartOffsetSeconds, 1e-15);
            Assert.AreEqual(10e-6, committedGate.Window.DurationSeconds, 1e-15);
            await host.StopCommand.ExecuteAsync(null);
            Assert.AreEqual(SessionPhase.Idle, services.GetRequiredService<ApplicationSession>().Snapshot.Phase);
            GateReprocessingProgressSnapshot historicalTerminal = await historical.Terminal.Task
                .WaitAsync(TimeSpan.FromSeconds(10));
            await scope.WaitForHistoricalObserversAsync().WaitAsync(TimeSpan.FromSeconds(10));
            await scope.WaitForHistoricalSchedulerIdleAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Assert.AreEqual(GateReprocessingPublicationPolicy.StoreOnly, historical.Descriptor!.PublicationPolicy);
            Assert.AreEqual(before.Value + 1, historical.Descriptor.Plan.Source.Revision.Value);
            Assert.AreEqual(GateReprocessingLifecycle.Completed, historicalTerminal.Lifecycle);

            ProductiveGateInspectionDiagnostics beforeClose = scope.ReadDiagnostics();
            GateApplyLedgerSnapshot ledger = AssertSingleTerminalLedger(beforeClose, new(before.Value + 1));
            Assert.AreEqual(GateActivationState.Activated, ledger.Activation);
            Assert.AreEqual(GateCandidateRetirementState.Activated, ledger.CandidateRetirement);
            Assert.AreEqual(GateApplyLedgerPendingResource.None, ledger.PendingResources);
            Assert.AreEqual(1, beforeClose.Store.ActiveViewCount,
                "The activated view is retained product state, not a provisional resource.");
            AssertStableProvisionalBalances(beforeClose);

            Task first = runtime.Shutdown.ShutdownAsync();
            Assert.AreSame(first, runtime.Shutdown.ShutdownAsync());
            await first.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.IsTrue(runtime.Shutdown.Status.Completed);
            Assert.IsNotNull(finalGateDiagnostics);
            AssertStableProvisionalBalances(finalGateDiagnostics);
            Assert.IsTrue(finalGateDiagnostics.LedgerLifecycle.IsStableAfterDrain);
            shutdownCompleted = true;
            Assert.AreEqual(1, source.DisposeCount);
            CollectionAssert.AreEqual(new[]
            {
                "phase5.stop", "phase6.wait", "phase6.drain", "phase7.dispose", "host.stop", "host.dispose"
            }, lifecycle);
        }
        finally { if (!shutdownCompleted) await runtime.Shutdown.ShutdownAsync(); }
    }, TimeSpan.FromSeconds(40));

    [TestMethod]
    public Task Productive_FIFO_saturation_is_visible_and_recovers_without_consuming_a_fake_result() => StaTest.Run(async () =>
    {
        var source = new GateControlledSource();
        var blocker = new BlockingInspectionWriter();
        var configuration = new ConventionalAcquisitionConfiguration(new(0), 16, 1_000_000, 0, UtSignalMode.Rf);
        DesktopRuntime runtime = await DesktopRuntimeFactory.CreateAsync(
            new WpfUiDispatcher(Dispatcher.CurrentDispatcher), source, configuration,
            new BScanOptions(4_000_000, 8, 2, 2), TimeProvider.System,
            gateHooks: new(InspectionWriterDecorator: blocker.Decorate));
        try
        {
            var services = runtime.Host.Services;
            var window = services.GetRequiredService<MainWindow>();
            var host = services.GetRequiredService<AScanHostViewModel>();
            var session = services.GetRequiredService<ApplicationSession>();
            var scope = services.GetRequiredService<ProductiveGateInspectionScope>();
            window.Show(); window.AScan.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent, window.AScan));
            window.UpdateLayout(); await runtime.Host.StartAsync(); await host.StartCommand.ExecuteAsync(null);
            GateEditorViewCoordinator view = window.AScan.ViewModel!.GateEditor!;
            var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            void Changed(object? _, System.ComponentModel.PropertyChangedEventArgs __)
            { if (view.CompositeVisual?.HasAuthoritativeIdentity == true) ready.TrySetResult(); }
            view.PropertyChanged += Changed;
            GateControlledSource.FrameOwner initial = source.QueueFrame();
            await Task.WhenAll(initial.Returned.Task, ready.Task).WaitAsync(TimeSpan.FromSeconds(5));
            view.PropertyChanged -= Changed;

            using var filler = new GatePlanEditorSession(scope.AuthorityKey, scope.Authority, scope.PreviewCompiler);
            GatePlanEditorSnapshot editing = filler.BeginEdit();
            GatePlanEditResult preview = filler.Preview();
            Assert.AreEqual(GatePlanEditOutcome.Previewed, preview.Outcome);
            GatePlanPreviewCandidate candidate = filler.BorrowApplicablePreview()!.Candidate;
            var request = new GatePlanApplyRequest(new(GatePlanEditIntentKind.Apply, scope.AuthorityKey,
                editing.Committed.Revision, editing.Draft!), candidate);

            blocker.Arm(); GateControlledSource.FrameOwner blocked = source.QueueFrame();
            await blocker.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            GatePlanApplySubmission[] admitted = Enumerable.Range(0, 63)
                .Select(_ => session.TryApplyGatePlan(request)).ToArray();
            Assert.IsTrue(admitted.All(x => x.Status == GatePlanApplySubmissionStatus.Admitted));
            await view.BeginEditCommand.ExecuteAsync(null); await view.PreviewCommand.ExecuteAsync(null);
            await view.ApplyCommand.ExecuteAsync(null);
            Assert.AreEqual(GateEditorViewPhase.Busy, view.State.Phase);
            Assert.AreEqual(GatePlanApplySubmissionStatus.CoordinatorCapacityExceeded, view.State.Admission);
            Assert.IsNull(view.State.Result, "Pre-admission saturation must not fabricate GatePlanEditResult.");

            blocker.Release(); await blocked.Returned.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Task.WhenAll(admitted.Select(x => x.Completion!)).WaitAsync(TimeSpan.FromSeconds(10));
            await view.BeginEditCommand.ExecuteAsync(null); await view.PreviewCommand.ExecuteAsync(null);
            await view.ApplyCommand.ExecuteAsync(null).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.AreNotEqual(GateEditorViewPhase.Busy, view.State.Phase);
            await runtime.Shutdown.ShutdownAsync().WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally { blocker.Release(); if (!runtime.Shutdown.Status.Completed) await runtime.Shutdown.ShutdownAsync(); }
    }, TimeSpan.FromSeconds(50));

    [TestMethod]
    public Task Productive_scope_lends_two_views_independent_preview_identities() => StaTest.Run(async () =>
    {
        DesktopRuntime runtime = await DesktopRuntime.CreateAsync(new WpfUiDispatcher(Dispatcher.CurrentDispatcher));
        GatePlanEditorSession? first = null; GatePlanEditorSession? second = null;
        try
        {
            GateEditingComponentContext context = runtime.Host.Services
                .GetRequiredService<AScanComponentContext>().GateEditing!;
            first = new(context.AuthorityKey, context.Authority, context.PreviewCompiler);
            second = new(context.AuthorityKey, context.Authority, context.PreviewCompiler);
            first.BeginEdit(); second.BeginEdit(); first.Preview(); second.Preview();
            GatePreviewArtifact a = first.BorrowApplicablePreview()!;
            GatePreviewArtifact b = second.BorrowApplicablePreview()!;
            Assert.AreNotEqual(a.Candidate.PreviewIdentity, b.Candidate.PreviewIdentity);
            Assert.AreEqual(a.Candidate.BaseRevision, b.Candidate.BaseRevision);
            Assert.AreNotSame(a.Checkpoint, b.Checkpoint,
                "Each independent view owns its bounded draft/checkpoint.");
        }
        finally
        {
            first?.Dispose(); second?.Dispose();
            await runtime.Shutdown.ShutdownAsync();
        }
    });

    [TestMethod]
    public Task Productive_historical_candidate_is_abandoned_when_a_new_run_supersedes_CAS() => StaTest.Run(async () =>
    {
        var source = new GateControlledSource();
        var historical = new HistoricalAdmissionProbe();
        var cas = new BlockingReplacementStore();
        var configuration = new ConventionalAcquisitionConfiguration(new(0), 16, 1_000_000, 0, UtSignalMode.Rf);
        DesktopRuntime runtime = await DesktopRuntimeFactory.CreateAsync(
            new WpfUiDispatcher(Dispatcher.CurrentDispatcher), source, configuration,
            new BScanOptions(4_000_000, 8, 2, 2), TimeProvider.System,
            gateHooks: new(ReplacementDecorator: cas.Decorate, SchedulerDecorator: historical.Decorate));
        try
        {
            var services = runtime.Host.Services;
            var window = services.GetRequiredService<MainWindow>();
            var host = services.GetRequiredService<AScanHostViewModel>();
            var session = services.GetRequiredService<ApplicationSession>();
            var scope = services.GetRequiredService<ProductiveGateInspectionScope>();
            window.Show(); window.AScan.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent, window.AScan));
            window.UpdateLayout(); await runtime.Host.StartAsync(); await host.StartCommand.ExecuteAsync(null);
            GateEditorViewCoordinator editor = window.AScan.ViewModel!.GateEditor!;
            var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            void Changed(object? _, System.ComponentModel.PropertyChangedEventArgs __)
            { if (editor.CompositeVisual?.HasAuthoritativeIdentity == true) ready.TrySetResult(); }
            editor.PropertyChanged += Changed;
            GateControlledSource.FrameOwner frame = source.QueueFrame();
            await Task.WhenAll(frame.Returned.Task, ready.Task).WaitAsync(TimeSpan.FromSeconds(5));
            editor.PropertyChanged -= Changed;
            await editor.BeginEditCommand.ExecuteAsync(null); await editor.PreviewCommand.ExecuteAsync(null);
            GatePlanRevision revision = new(scope.Authority.Read(scope.AuthorityKey).Committed.Revision.Value + 1);
            await editor.ApplyCommand.ExecuteAsync(null).WaitAsync(TimeSpan.FromSeconds(5));
            await host.StopCommand.ExecuteAsync(null).WaitAsync(TimeSpan.FromSeconds(5));
            await cas.ActivationEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await session.StartAsync(configuration, new AcquisitionRunId(Guid.NewGuid()));
            cas.Release();
            await historical.Terminal.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await scope.WaitForHistoricalObserversAsync().WaitAsync(TimeSpan.FromSeconds(10));
            await scope.WaitForHistoricalSchedulerIdleAsync().WaitAsync(TimeSpan.FromSeconds(10));
            await session.StopAsync().WaitAsync(TimeSpan.FromSeconds(5));
            ProductiveGateInspectionDiagnostics diagnostics = scope.ReadDiagnostics();
            GateApplyLedgerSnapshot ledger = AssertSingleTerminalLedger(diagnostics, revision);
            Assert.IsTrue(ledger.Activation is GateActivationState.RunSuperseded or GateActivationState.Superseded);
            Assert.AreEqual(GateCandidateRetirementState.Reclaimed, ledger.CandidateRetirement);
            Assert.AreEqual(GateApplyLedgerPendingResource.None, ledger.PendingResources);
            AssertStableProvisionalBalances(diagnostics);
            await runtime.Shutdown.ShutdownAsync().WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally { cas.Release(); if (!runtime.Shutdown.Status.Completed) await runtime.Shutdown.ShutdownAsync(); }
    }, TimeSpan.FromSeconds(40));

    [TestMethod]
    public Task Closing_view_after_apply_admission_does_not_cancel_productive_reconciliation() => StaTest.Run(async () =>
    {
        var source = new GateControlledSource();
        var blocker = new BlockingInspectionWriter();
        ProductiveGateInspectionDiagnostics? finalDiagnostics = null;
        var configuration = new ConventionalAcquisitionConfiguration(new(0), 16, 1_000_000, 0, UtSignalMode.Rf);
        DesktopRuntime runtime = await DesktopRuntimeFactory.CreateAsync(
            new WpfUiDispatcher(Dispatcher.CurrentDispatcher), source, configuration,
            new BScanOptions(4_000_000, 8, 2, 2), TimeProvider.System,
            gateHooks: new(InspectionWriterDecorator: blocker.Decorate,
                BeforeAuthorityClose: value => finalDiagnostics = value));
        try
        {
            var services = runtime.Host.Services;
            var window = services.GetRequiredService<MainWindow>();
            var host = services.GetRequiredService<AScanHostViewModel>();
            var scope = services.GetRequiredService<ProductiveGateInspectionScope>();
            window.Show(); window.AScan.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent, window.AScan));
            window.UpdateLayout(); await runtime.Host.StartAsync(); await host.StartCommand.ExecuteAsync(null);
            GateEditorViewCoordinator editor = window.AScan.ViewModel!.GateEditor!;
            var visualReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            void Visual(object? _, System.ComponentModel.PropertyChangedEventArgs __)
            { if (editor.CompositeVisual?.HasAuthoritativeIdentity == true) visualReady.TrySetResult(); }
            editor.PropertyChanged += Visual;
            GateControlledSource.FrameOwner first = source.QueueFrame();
            await Task.WhenAll(first.Returned.Task, visualReady.Task).WaitAsync(TimeSpan.FromSeconds(5));
            blocker.Arm();
            GateControlledSource.FrameOwner blockedFrame = source.QueueFrame();
            await blocker.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            editor.PropertyChanged -= Visual;
            await editor.BeginEditCommand.ExecuteAsync(null);
            await editor.PreviewCommand.ExecuteAsync(null);
            GatePlanRevision before = scope.Authority.Read(scope.AuthorityKey).Committed.Revision;
            int publicationsAfterClose = 0;
            void Published(object? _, System.ComponentModel.PropertyChangedEventArgs __) => publicationsAfterClose++;
            editor.PropertyChanged += Published;

            Task waiter = editor.ApplyCommand.ExecuteAsync(null);
            Assert.IsFalse(waiter.IsCompleted, "The admitted Apply must still be awaiting its terminal.");
            await window.CloseAScanAsync().WaitAsync(TimeSpan.FromSeconds(5));
            int atClose = publicationsAfterClose;
            blocker.Release();
            await blockedFrame.Returned.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await waiter.WaitAsync(TimeSpan.FromSeconds(5));
            var committed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using IDisposable subscription = scope.Authority.Observe(scope.AuthorityKey).Subscribe(
                new ActionObserver<GatePlanAuthoritySnapshot>(snapshot =>
                { if (snapshot.Committed.Revision.Value == before.Value + 1) committed.TrySetResult(); }));
            if (scope.Authority.Read(scope.AuthorityKey).Committed.Revision.Value != before.Value + 1)
                await committed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            Assert.AreEqual(atClose, publicationsAfterClose,
                "A closed view must not receive the productive terminal or authority publication.");
            editor.PropertyChanged -= Published;

            Task shutdown = runtime.Shutdown.ShutdownAsync();
            Assert.AreSame(shutdown, runtime.Shutdown.ShutdownAsync());
            await shutdown.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.IsNotNull(finalDiagnostics);
            AssertStableProvisionalBalances(finalDiagnostics);
        }
        finally { blocker.Release(); if (!runtime.Shutdown.Status.Completed) await runtime.Shutdown.ShutdownAsync(); }
    }, TimeSpan.FromSeconds(40));

    private static GateApplyLedgerSnapshot AssertSingleTerminalLedger(ProductiveGateInspectionDiagnostics diagnostics,
        GatePlanRevision revision)
    {
        GateApplyLedgerSnapshot ledger = diagnostics.Ledgers.Single(x => x.PlanRevision == revision);
        Assert.AreEqual(GateHistoricalAdmissionState.Admitted, ledger.HistoricalAdmission);
        Assert.AreEqual(GateHistoricalExecutionState.Completed, ledger.HistoricalExecution);
        Assert.AreEqual(GateCandidateCoverageState.Complete, ledger.CandidateCoverage);
        Assert.AreEqual(GateApplyLedgerPendingResource.None, ledger.PendingResources);
        return ledger;
    }

    private static void AssertStableProvisionalBalances(ProductiveGateInspectionDiagnostics diagnostics)
    {
        Assert.AreEqual(0, diagnostics.ActiveHistoricalObservers);
        Assert.AreEqual(0, diagnostics.Scheduler.PendingAdmissions);
        Assert.AreEqual(0, diagnostics.Scheduler.ActiveJobs);
        Assert.AreEqual(0, diagnostics.Scheduler.ActiveSampleBuffers);
        Assert.AreEqual(0, diagnostics.Scheduler.ActiveScratchRentals);
        Assert.AreEqual(0, diagnostics.Store.ReservedBatchCount);
        Assert.AreEqual(0, diagnostics.Store.ActiveLeases);
        Assert.AreEqual(0, diagnostics.Store.AdmittedWriters);
        Assert.AreEqual(0, diagnostics.Store.ActiveReaders);
        Assert.AreEqual(0, diagnostics.Store.ActiveCoverageWriters);
        Assert.AreEqual(0, diagnostics.Store.ActiveActivationWriters);
        Assert.AreEqual(0, diagnostics.Store.ReservedCoverageCount);
        Assert.AreEqual(0, diagnostics.Store.ReservedActivationCount);
        Assert.AreEqual(0, diagnostics.Store.InvalidatedViewCount);
        Assert.AreEqual(0, diagnostics.Store.ReclaimableBatchCount);
        Assert.AreEqual(0, diagnostics.Store.TombstoneCount);
        Assert.AreEqual(0, diagnostics.Live.ActiveLeases);
        Assert.AreEqual(0, diagnostics.Live.ActiveWorkers);
        Assert.AreEqual(0, diagnostics.Live.ActiveCallbacks);
        Assert.AreEqual(0, diagnostics.Live.SchedulerDepth);
        Assert.AreEqual(0, diagnostics.Live.OpenRuns);
        foreach (GateProcessingRunDiagnostics run in diagnostics.Live.ActiveRuns.Concat(diagnostics.Live.TerminalRuns))
        {
            Assert.AreEqual(0, run.WorkPending);
            Assert.AreEqual(0, run.ActiveLeases);
            Assert.AreEqual(0, run.ActiveWorkers);
            Assert.AreEqual(0, run.ActiveCallbacks);
            Assert.AreEqual(0, run.ProvisionalEpochSlots);
            Assert.AreEqual(0, run.PreparedEpochPins);
        }
    }

    [TestMethod]
    public Task ProductiveGateInspectionScopeIsTheSingleRegisteredGateGraph() => StaTest.Run(async () =>
    {
        var runtime = await DesktopRuntime.CreateAsync(new WpfUiDispatcher(Dispatcher.CurrentDispatcher));
        try
        {
            ProductiveGateInspectionScope scope = runtime.Host.Services.GetRequiredService<ProductiveGateInspectionScope>();
            ApplicationSession session = runtime.Host.Services.GetRequiredService<ApplicationSession>();
            AScanComponentContext context = runtime.Host.Services.GetRequiredService<AScanComponentContext>();
            Assert.AreSame(session, runtime.Host.Services.GetRequiredService<IApplicationSession>());
            Assert.IsNull(runtime.Host.Services.GetService<ICommittedGateProcessingIngress>());
            Assert.AreSame(scope.Authority, context.GateEditing!.Authority);
            Assert.AreSame(scope.PreviewCompiler, context.GateEditing.PreviewCompiler);
            Assert.AreSame(scope.GeometryEvaluator, context.GateEditing.GeometryEvaluator);
            Assert.AreSame(session, context.GateEditing.Apply);
            Assert.AreEqual(scope.AuthorityKey, context.GateEditing.AuthorityKey);
            Assert.IsNull(runtime.Host.Services.GetService<ProductiveGateProcessing>());
        }
        finally { await runtime.Shutdown.ShutdownAsync(); }
    });

    [TestMethod]
    [DataRow("visual")]
    [DataRow("host")]
    public Task PartialStartupRetainsOwnerAndCleansCreatedResources(string stage) => StaTest.Run(async () =>
    {
        var failure = new InvalidOperationException("startup checkpoint");
        var runtime = await DesktopRuntime.CreateAsync(new WpfUiDispatcher(Dispatcher.CurrentDispatcher), null,
            new ManualClock(), current => { if (current == stage) { throw failure; } });
        Assert.AreSame(failure, runtime.InitializationError);
        Task first = runtime.Shutdown.ShutdownAsync();
        await first;
        Assert.IsTrue(runtime.Shutdown.Status.Completed);
        Assert.AreSame(first, runtime.Shutdown.ShutdownAsync());
    });

    [TestMethod]
    public Task HistoricalProducerFailureDoesNotPreventSafeHostDisposal() => StaTest.Run(async () =>
    {
        var clock = new ManualClock { FailNextTimer = true };
        var runtime = await DesktopRuntime.CreateAsync(new WpfUiDispatcher(Dispatcher.CurrentDispatcher), clock: clock);
        var session = runtime.Host.Services.GetRequiredService<ApplicationSession>();
        var source = runtime.Host.Services.GetRequiredService<SimulatorUtFrameSource>();
        try
        {
            await session.StartAsync(SimulatorUtFrameSource.DefaultConfiguration, new UTStudio.Domain.Acquisition.AcquisitionRunId(Guid.NewGuid()));
        }
        catch (InvalidOperationException) { } // A fast producer failure may race the start acknowledgement.
        await clock.TimerFailed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await runtime.Shutdown.ShutdownAsync();
        Assert.AreEqual(SessionPhase.Disposed, session.Snapshot.Phase);
        Assert.AreEqual(UTStudio.Domain.Acquisition.UtConnectionState.Disconnected, source.State.Connection);
        Assert.IsNotNull(session.Snapshot.PrimaryError);
        Assert.IsTrue(runtime.Shutdown.Status.Completed);
    });

    [TestMethod]
    public Task StartupDiagnosticRemainsVisibleUntilCleanupIsConfirmed() => StaTest.Run(async () =>
    {
        var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var coordinator = new ShutdownCoordinator([new("held", () => held.Task)],
            Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance, new ManualClock());
        var window = new StartupFailureWindow(new InvalidOperationException("startup"), coordinator);
        window.Show();
        Task cleanup = coordinator.ShutdownAsync();
        window.Close();
        Assert.IsTrue(window.IsVisible);
        held.SetResult();
        await cleanup;
        window.ConfirmCleanup();
        window.Close();
        Assert.IsFalse(window.IsVisible);
    });

    [TestMethod]
    public Task ContainerResolvesOneInstanceAndWindowDataContext() => StaTest.Run(async () =>
    {
        var runtime = await DesktopRuntime.CreateAsync(new WpfUiDispatcher(Dispatcher.CurrentDispatcher));
        try
        {
            var services = runtime.Host.Services;
            Assert.AreSame(services.GetRequiredService<SimulatorUtFrameSource>(), services.GetRequiredService<IUtFrameSource>());
            Assert.AreSame(services.GetRequiredService<ApplicationSession>(), services.GetRequiredService<IApplicationSession>());
            Assert.IsNotNull(services.GetRequiredService<InspectionDataStoreRegistry>());
            Assert.IsNotNull(services.GetRequiredService<BScanIngress>());
            Assert.AreSame(services.GetRequiredService<AScanVisualDelivery>(), services.GetRequiredService<IObservable<AScanSnapshot>>());
            var vm = services.GetRequiredService<AScanHostViewModel>();
            var session = services.GetRequiredService<IApplicationSession>();
            var window = services.GetRequiredService<MainWindow>();
            Assert.AreSame(window, services.GetRequiredService<MainWindow>());
            Assert.AreSame(vm, window.DataContext);
            window.Show();
            await runtime.Host.StartAsync();
            await vm.StartCommand.ExecuteAsync(null);
            Assert.AreEqual(SessionPhase.Running, services.GetRequiredService<IApplicationSession>().Snapshot.Phase);
            await DiagnosticWait.Until(() => window.AScan.ViewModel?.Session.Phase == SessionPhase.Running,
                "host session state forwarded to AScanView");
            Assert.AreEqual(vm.Session.Version, window.AScan.ViewModel!.Session.Version);
            await runtime.Shutdown.ShutdownAsync();
            Assert.AreEqual(SessionPhase.Disposed, session.Snapshot.Phase);
        }
        finally { await runtime.Shutdown.ShutdownAsync(); }
    });

    [TestMethod]
    public Task Productive_simulator_started_before_opening_bscan_presents_live_raster() => StaTest.Run(async () =>
    {
        var runtime = await DesktopRuntime.CreateAsync(new WpfUiDispatcher(Dispatcher.CurrentDispatcher),
            ["--Simulator:AScansPerSecond=100"]);
        var services = runtime.Host.Services;
        var host = services.GetRequiredService<AScanHostViewModel>();
        var mainWindow = services.GetRequiredService<MainWindow>();
        mainWindow.AScan.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent, mainWindow.AScan));
        var aScan = mainWindow.AScan.ViewModel!;
        var windows = services.GetRequiredService<BScanWindowManager>();
        try
        {
            await runtime.Host.StartAsync();
            await host.StartCommand.ExecuteAsync(null);
            await DiagnosticWait.Until(() => aScan.AScan is not null, "productive simulator A-Scan");

            await windows.OpenAsync();
            BScanWindow window = windows.ActiveWindow!;
            BScanViewModel bScan = window.Component.ViewModel!;
            await DiagnosticWait.Until(() => bScan.State.Region is not null, "productive simulator B-Scan raster");
            BScanControl control = window.Component.Renderer;

            Assert.AreEqual(BScanViewPhase.Ready, bScan.State.Phase);
            Assert.IsNull(bScan.State.BScanIngressError);
            Assert.IsNull(bScan.State.BScanVisualError);
            Assert.IsNotNull(bScan.State.Region);
            Assert.IsNotNull(control.RasterSource);
            Assert.IsNotNull(control.Snapshot);
            await DiagnosticWait.Until(() => control.RasterSource is BitmapSource { PixelWidth: > 1, PixelHeight: > 1 },
                "productive simulator sized B-Scan raster");
            AssertRasterContainsSignal(control);

            await DiagnosticWait.Until(() => aScan.AScan?.Sequence >= 100,
                "productive simulator mature B-Scan store");
            await window.CloseOwnedAsync();
            await windows.OpenAsync();
            BScanWindow reopened = windows.ActiveWindow!;
            BScanViewModel reopenedViewModel = reopened.Component.ViewModel!;
            BScanControl reopenedControl = reopened.Component.Renderer;
            await DiagnosticWait.Until(() => reopenedViewModel.State.Region is not null &&
                reopenedControl.RasterSource is not null, "reopened productive simulator B-Scan raster");
            await DiagnosticWait.Until(() => reopenedControl.RasterSource is BitmapSource
            { PixelWidth: > 1, PixelHeight: > 1 }, "reopened productive simulator sized B-Scan raster");
            AssertRasterContainsSignal(reopenedControl);
        }
        finally { await runtime.Shutdown.ShutdownAsync(); }
    });

    [TestMethod]
    [Timeout(30_000, CooperativeCancellation = true)]
    public Task Open_bilinear_bscan_keeps_advancing_during_continuous_productive_simulation() => StaTest.Run(async () =>
    {
        var runtime = await DesktopRuntime.CreateAsync(new WpfUiDispatcher(Dispatcher.CurrentDispatcher),
            ["--Simulator:AScansPerSecond=100"]);
        var services = runtime.Host.Services;
        var host = services.GetRequiredService<AScanHostViewModel>();
        var mainWindow = services.GetRequiredService<MainWindow>();
        mainWindow.AScan.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent, mainWindow.AScan));
        var aScan = mainWindow.AScan.ViewModel!;
        var windows = services.GetRequiredService<BScanWindowManager>();
        try
        {
            await runtime.Host.StartAsync();
            await windows.OpenAsync();
            BScanWindow window = windows.ActiveWindow!;
            BScanViewModel bScan = window.Component.ViewModel!;
            BScanControl control = window.Component.Renderer;
            bScan.SetResamplingMode(Scan2DInterpolationMode.Bilinear);
            await host.StartCommand.ExecuteAsync(null);
            try
            {
                await DiagnosticWait.Until(() => control.AppliedSnapshot?.StoreRevision >= 450,
                    "open Bilinear B-Scan reaches revision 450");
            }
            catch (TimeoutException error)
            {
                throw new AssertFailedException($"sequence={aScan.AScan?.Sequence}; " +
                    $"ingress={services.GetRequiredService<BScanIngress>().CurrentVisualState?.StoreState.StoreRevision}; " +
                    $"view={bScan.State.StoreRevision}; displayed={bScan.State.DisplayedRevision?.StoreRevision}; " +
                    $"region={bScan.State.Region?.Snapshot.StoreRevision}; control={control.AppliedSnapshot?.StoreRevision}; " +
                    $"regionSize={bScan.State.Region?.Snapshot.RasterWidth}x{bScan.State.Region?.Snapshot.RasterHeight}; " +
                    $"regionConfig={bScan.State.Region?.Snapshot.RenderConfigurationVersion}; " +
                    $"full={bScan.State.Region?.Snapshot.RequiresFullRefresh}; reason={bScan.State.Region?.Snapshot.FullRefreshReason}; " +
                    $"request={services.GetRequiredService<BScanVisualDelivery>().GetLastRequest(bScan.ConsumerId)}; " +
                    $"pending={bScan.State.IsRenderPending}; visual={bScan.State.BScanVisualError}; " +
                    $"ui={bScan.State.UiInteractionError}; reject={control.LastIncrementalRejection}; " +
                    $"delivery={services.GetRequiredService<BScanVisualDelivery>().Diagnostics}", error);
            }
            ulong first = control.AppliedSnapshot!.StoreRevision;
            ulong configuration = control.AppliedSnapshot.RenderConfigurationVersion;
            await DiagnosticWait.Until(() => aScan.AScan?.Sequence >= first + 100,
                "productive simulator advances 100 further traces");
            await DiagnosticWait.Until(() => control.AppliedSnapshot?.StoreRevision >= first + 75,
                $"B-Scan catches up after revision {first}; ingress=" +
                $"{services.GetRequiredService<BScanIngress>().CurrentVisualState?.StoreState.StoreRevision}; " +
                $"view={bScan.State.StoreRevision}; displayed={bScan.State.DisplayedRevision?.StoreRevision}; " +
                $"region={bScan.State.Region?.Snapshot.StoreRevision}; pending={bScan.State.IsRenderPending}; " +
                $"error={bScan.State.BScanVisualError}; ui={bScan.State.UiInteractionError}");
            Assert.AreEqual(configuration, control.AppliedSnapshot!.RenderConfigurationVersion);
            Assert.IsGreaterThan(0UL,
                services.GetRequiredService<BScanVisualDelivery>().Diagnostics.IncrementalUpdatesBuilt);
        }
        finally { await runtime.Shutdown.ShutdownAsync(); }
    }, TimeSpan.FromSeconds(30));

    private static void AssertRasterContainsSignal(BScanControl control)
    {
        var bitmap = (WriteableBitmap)control.RasterSource!;
        byte[] pixels = new byte[bitmap.BackBufferStride * bitmap.PixelHeight];
        bitmap.CopyPixels(pixels, bitmap.BackBufferStride, 0);
        var colors = Enumerable.Range(0, pixels.Length / 4)
            .Select(index => (B: pixels[index * 4], G: pixels[index * 4 + 1],
                R: pixels[index * 4 + 2], A: pixels[index * 4 + 3]))
            .Distinct()
            .Take(3)
            .ToArray();
        Assert.IsGreaterThanOrEqualTo(2, colors.Length, "B-Scan raster is uniformly colored.");
        var noData = BScanPalette.GetColor(BScanBipolarIntensity8.NoData);
        Assert.IsTrue(colors.Any(color => color != (noData.B, noData.G, noData.R, noData.A)));
    }

    [TestMethod]
    public Task Internal_factory_composes_controlled_source_and_small_productive_ring() => StaTest.Run(async () =>
    {
        var source = new ControlledSource();
        var clock = new ManualClock();
        var configuration = new ConventionalAcquisitionConfiguration(new(0), 16, 1_000_000, 0, UtSignalMode.Rf);
        var options = new BScanOptions(1_000_000, 8, 2, 2);
        var runtime = await DesktopRuntimeFactory.CreateAsync(new WpfUiDispatcher(Dispatcher.CurrentDispatcher),
            source, configuration, options, clock);
        try
        {
            Assert.AreSame(source, runtime.Host.Services.GetRequiredService<IUtFrameSource>());
            Assert.AreEqual(8, runtime.Host.Services.GetRequiredService<BScanIngress>().Options.MaximumColumns);
            Assert.IsNotNull(runtime.Host.Services.GetRequiredService<BScanWindowManager>());
        }
        finally { await runtime.Shutdown.ShutdownAsync(); }
        Assert.AreEqual(1, source.DisposeCount);
    });

    [TestMethod]
    public Task Productive_factory_observes_complete_shutdown_phase_order() => StaTest.Run(async () =>
    {
        var source = new ControlledSource();
        var events = new List<string>();
        var configuration = new ConventionalAcquisitionConfiguration(new(0), 16, 1_000_000, 0, UtSignalMode.Rf);
        var runtime = await DesktopRuntimeFactory.CreateAsync(new WpfUiDispatcher(Dispatcher.CurrentDispatcher),
            source, configuration, new BScanOptions(4_000_000, 8, 2, 2), new ManualClock(),
            lifecycleObserver: events.Add);

        await runtime.Shutdown.ShutdownAsync();

        CollectionAssert.AreEqual(new[]
        {
            "phase5.stop", "phase6.wait", "phase6.drain", "phase7.dispose", "host.stop", "host.dispose"
        }, events);
        Assert.IsTrue(runtime.Shutdown.Status.Completed);
        Assert.AreEqual(1, source.DisposeCount);
    });

    [TestMethod]
    public Task Productive_shutdown_waits_for_raster_owner_before_disposal_and_host() => StaTest.Run(async () =>
    {
        var source = new ControlledSource();
        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var drainEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var drained = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var disposed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var configuration = new ConventionalAcquisitionConfiguration(new(0), 16, 1_000_000, 0, UtSignalMode.Rf);
        var runtime = await DesktopRuntimeFactory.CreateAsync(new WpfUiDispatcher(Dispatcher.CurrentDispatcher),
            source, configuration, new BScanOptions(4_000_000, 8, 2, 2), new ManualClock(),
            lifecycleObserver: phase =>
            {
                if (phase == "phase5.stop") { stopped.TrySetResult(); }
                else if (phase == "phase6.wait") { drainEntered.TrySetResult(); }
                else if (phase == "phase6.drain") { drained.TrySetResult(); }
                else if (phase == "phase7.dispose") { disposed.TrySetResult(); }
            });
        var services = runtime.Host.Services;
        var ingress = services.GetRequiredService<BScanIngress>();
        var delivery = services.GetRequiredService<BScanVisualDelivery>();
        var run = new AcquisitionRunId(Guid.NewGuid());
        var stores = services.GetRequiredService<InspectionDataStoreRegistry>();
        await using IInspectionRunWriter writer = stores.OpenRun(run);
        ingress.OpenRun(run);
        var stream = new UtStreamIdentity(stores.InspectionId, run, source.SourceId,
            configuration.PhysicalChannelId);
        var identity = new TraceIdentity(stream, 0);
        var descriptor = new TraceDescriptor(identity, 0, TimeSpan.Zero, DateTimeOffset.UnixEpoch,
            null, 16, 1_000_000, 0, UtSignalMode.Rf);
        InspectionAppendResult commit = await writer.CommitAsync(new InspectionCommitInput(
            new InspectionTraceInput(descriptor, new short[16])));
        ingress.NotifyCommitted(identity, commit.Revision);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (ingress.CurrentVisualState?.StoreState.StoreRevision != commit.Revision.Value)
        { await Task.Delay(10, timeout.Token); }
        BScanRasterRegion owner = (await delivery.GetRegionAsync(new(run,
            BScanSpatialViewportOperations.Reset(new(0, 0)),
            ScanTimeViewportOperations.Create(0, .000016), 1, 1, 1))).Region!;
        Task shutdown = runtime.Shutdown.ShutdownAsync();
        await stopped.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await drainEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.IsFalse(shutdown.IsCompleted);
        Assert.IsFalse(drained.Task.IsCompleted);
        Assert.IsFalse(runtime.Shutdown.Status.Completed);
        Assert.AreEqual(0, source.DisposeCount);
        Assert.AreEqual(1, delivery.Diagnostics.ActiveRasterOwners);

        owner.Dispose();
        await drained.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await disposed.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await shutdown.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.IsTrue(runtime.Shutdown.Status.Completed);
        Assert.AreEqual(0, delivery.Diagnostics.ActiveRasterOwners);
        Assert.AreEqual(BScanVisualCloseState.Closed, delivery.Diagnostics.CloseState);
        Assert.AreEqual(1, source.DisposeCount);
    });

    [TestMethod]
    public Task NavigationKeyBindingsTargetTheExactViewportCommands() => StaTest.Run(async () =>
    {
        var runtime = await DesktopRuntime.CreateAsync(new WpfUiDispatcher(Dispatcher.CurrentDispatcher));
        try
        {
            var window = runtime.Host.Services.GetRequiredService<MainWindow>();
            window.Show();
            window.UpdateLayout();
            var vm = window.AScan.ViewModel!;
            AssertBinding(Key.Home, ModifierKeys.None, vm.ResetViewportCommand);
            AssertBinding(Key.Add, ModifierKeys.None, vm.ZoomInCommand);
            AssertBinding(Key.Subtract, ModifierKeys.None, vm.ZoomOutCommand);
            AssertBinding(Key.Left, ModifierKeys.Alt, vm.PanLeftCommand);
            AssertBinding(Key.Right, ModifierKeys.Alt, vm.PanRightCommand);

            void AssertBinding(Key key, ModifierKeys modifiers, System.Windows.Input.ICommand expected)
            {
                var binding = window.AScan.InputBindings.OfType<KeyBinding>().Single(item =>
                    item.Key == key && item.Modifiers == modifiers);
                Assert.AreSame(expected, binding.Command);
            }
        }
        finally { await runtime.Shutdown.ShutdownAsync(); }
    });

    [TestMethod]
    public Task BScan_window_activates_single_instance_closes_and_reopens_with_new_consumer() => StaTest.Run(async () =>
    {
        var runtime = await DesktopRuntime.CreateAsync(new WpfUiDispatcher(Dispatcher.CurrentDispatcher));
        try
        {
            var manager = runtime.Host.Services.GetRequiredService<BScanWindowManager>();
            await manager.OpenAsync();
            BScanWindow first = manager.ActiveWindow!;
            string firstConsumer = first.ConsumerId;
            await manager.OpenAsync();
            Assert.AreSame(first, manager.ActiveWindow);
            Assert.AreEqual(1, manager.CreationCount);

            await first.CloseOwnedAsync();
            Assert.IsNull(manager.ActiveWindow);
            await manager.OpenAsync();
            BScanWindow second = manager.ActiveWindow!;
            Assert.AreNotSame(first, second);
            Assert.AreNotEqual(firstConsumer, second.ConsumerId);
            Assert.AreEqual(2, manager.CreationCount);

            await runtime.Shutdown.ShutdownAsync();
            Assert.IsNull(manager.ActiveWindow);
        }
        finally { await runtime.Shutdown.ShutdownAsync(); }
    });

    [TestMethod]
    public Task Failed_BScan_Show_releases_window_viewmodel_and_consumer() => StaTest.Run(async () =>
    {
        var runtime = await DesktopRuntime.CreateAsync(new WpfUiDispatcher(Dispatcher.CurrentDispatcher));
        try
        {
            var services = runtime.Host.Services;
            var delivery = services.GetRequiredService<BScanVisualDelivery>();
            var manager = new BScanWindowManager(delivery,
                services.GetRequiredService<SharedScanTimeViewport>(),
                services.GetRequiredService<TraceSelectionScope>(),
                Dispatcher.CurrentDispatcher, null, null, null,
                _ => throw new InvalidOperationException("show failed"));

            InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(() => manager.OpenAsync());

            Assert.AreEqual("show failed", error.Message);
            Assert.IsNull(manager.ActiveWindow);
            Assert.AreEqual(0, delivery.ActiveConsumerCount);
            Assert.AreEqual(0, delivery.PendingBootstrapCount);
            Assert.AreEqual(0, delivery.RetainedSnapshotCount);
        }
        finally { await runtime.Shutdown.ShutdownAsync(); }
    });

    [TestMethod]
    public Task BScan_window_close_is_idempotent_through_public_component_context() => StaTest.Run(async () =>
    {
        var runtime = await DesktopRuntime.CreateAsync(new WpfUiDispatcher(Dispatcher.CurrentDispatcher));
        BScanWindow? window = null;
        try
        {
            var services = runtime.Host.Services;
            var delivery = services.GetRequiredService<BScanVisualDelivery>();
            window = new BScanWindow(new(delivery, services.GetRequiredService<SharedScanTimeViewport>(),
                services.GetRequiredService<TraceSelectionScope>()));
            window.Show();
            await window.Component.Activation;

            Task first = window.CloseOwnedAsync();
            Task second = window.CloseOwnedAsync();
            await first;

            Assert.AreSame(first, second);
            Assert.IsFalse(window.IsVisible);
            Assert.AreEqual(0, delivery.ActiveConsumerCount);
        }
        finally
        {
            if (window?.IsVisible == true) { window.Close(); }
            await runtime.Shutdown.ShutdownAsync();
        }
    });

    [TestMethod]
    public Task BScan_window_reopens_with_a_new_public_context_after_close() => StaTest.Run(async () =>
    {
        var runtime = await DesktopRuntime.CreateAsync(new WpfUiDispatcher(Dispatcher.CurrentDispatcher));
        try
        {
            var services = runtime.Host.Services;
            var delivery = services.GetRequiredService<BScanVisualDelivery>();
            BScanComponentContext Context() => new(delivery,
                services.GetRequiredService<SharedScanTimeViewport>(), services.GetRequiredService<TraceSelectionScope>());
            var first = new BScanWindow(Context());
            first.Show();
            await first.Component.Activation;
            string firstConsumer = first.ConsumerId;
            await first.CloseOwnedAsync();
            var second = new BScanWindow(Context());
            second.Show();
            await second.Component.Activation;
            Assert.AreNotEqual(firstConsumer, second.ConsumerId);
            await second.CloseOwnedAsync();
            Assert.AreEqual(0, delivery.ActiveConsumerCount);
        }
        finally { await runtime.Shutdown.ShutdownAsync(); }
    });

    [TestMethod]
    public Task Shutdown_started_off_ui_blocks_entire_shell_before_cleanup() => StaTest.Run(async () =>
    {
        var runtime = await DesktopRuntime.CreateAsync(new WpfUiDispatcher(Dispatcher.CurrentDispatcher));
        var window = runtime.Host.Services.GetRequiredService<MainWindow>();
        var manager = runtime.Host.Services.GetRequiredService<BScanWindowManager>();
        var delivery = runtime.Host.Services.GetRequiredService<BScanVisualDelivery>();
        window.Show();
        await manager.OpenAsync(window);
        Assert.IsNotNull(manager.ActiveWindow);
        Assert.AreEqual(1, delivery.ActiveConsumerCount);
        Task shutdown = Task.Run(() => runtime.Shutdown.ShutdownAsync());

        await DiagnosticWait.Until(() => !window.IsEnabled, "shell interaction gate");
        await shutdown;

        Assert.IsFalse(window.IsEnabled);
        Assert.IsTrue(runtime.Shutdown.Status.Completed);
        Assert.IsNull(manager.ActiveWindow);
        Assert.AreEqual(0, delivery.ActiveConsumerCount);
        window.Close();
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Assert.IsFalse(window.IsVisible);
    });

    [TestMethod]
    public Task DispatcherRunsOnStaPropagatesExceptionAndHonorsCancellation() => StaTest.Run(async () =>
    {
        var dispatcher = new WpfUiDispatcher(Dispatcher.CurrentDispatcher);
        int thread = Environment.CurrentManagedThreadId;
        await Task.Run(() => dispatcher.InvokeAsync(() =>
        {
            Assert.IsTrue(dispatcher.CheckAccess());
            Assert.AreEqual(thread, Environment.CurrentManagedThreadId);
        }));
        await Assert.ThrowsAsync<InvalidOperationException>(() => dispatcher.InvokeAsync(() => throw new InvalidOperationException()));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        bool executed = false;
        await Assert.ThrowsAsync<TaskCanceledException>(() => dispatcher.InvokeAsync(() => executed = true, cancellation.Token));
        Assert.IsFalse(executed);
    });

    [TestMethod]
    public Task FirstClosingIsCancelledUntilCleanupAndRepeatedCloseDoesNotReenter() => StaTest.Run(async () =>
    {
        var runtime = await DesktopRuntime.CreateAsync(new WpfUiDispatcher(Dispatcher.CurrentDispatcher));
        var window = runtime.Host.Services.GetRequiredService<MainWindow>();
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int cancelled = 0;
        window.Closing += (_, args) => { if (args.Cancel) { cancelled++; } };
        window.Closed += (_, _) => closed.TrySetResult();
        window.Show();
        await runtime.Host.StartAsync();
        await ((AScanHostViewModel)window.DataContext).StartCommand.ExecuteAsync(null);
        window.Close();
        window.Close();
        Assert.IsTrue(window.IsVisible);
        await closed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.IsGreaterThanOrEqualTo(2, cancelled);
        Assert.IsTrue(runtime.Shutdown.Status.Completed);
    });

    private sealed class ActionObserver<T>(Action<T> next) : IObserver<T>
    {
        public void OnNext(T value) => next(value);
        public void OnError(Exception error) => Assert.Fail(error.ToString());
        public void OnCompleted() { }
    }

    private sealed class HistoricalAdmissionProbe : IOwnedGateReprocessingScheduler
    {
        private IOwnedGateReprocessingScheduler? _inner;
        internal GateReprocessingJobDescriptor? Descriptor { get; private set; }
        internal TaskCompletionSource<GateReprocessingProgressSnapshot> Terminal { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal IOwnedGateReprocessingScheduler Decorate(IOwnedGateReprocessingScheduler inner)
        { _inner = inner; return this; }

        public async ValueTask<GateReprocessingAdmissionResult> AdmitOwnedAsync(
            GateReprocessingJobDescriptor descriptor, IGateReprocessingAdmissionOwner owner,
            CancellationToken cancellationToken = default)
        {
            Descriptor = descriptor;
            GateReprocessingAdmissionResult result = await _inner!.AdmitOwnedAsync(descriptor, owner, cancellationToken);
            if (result.Job is { } job) _ = ObserveAsync(job);
            return result;
        }

        private async Task ObserveAsync(IGateReprocessingJob job)
        {
            try { Terminal.TrySetResult(await job.Completion.ConfigureAwait(false)); }
            catch (Exception error) { Terminal.TrySetException(error); }
        }
    }

    private sealed class BlockingInspectionWriter : IInspectionRunWriter
    {
        private IInspectionRunWriter? _inner;
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _armed;
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal IInspectionRunWriter Decorate(IInspectionRunWriter inner)
        { _inner = inner; return this; }
        internal void Arm() => Interlocked.Exchange(ref _armed, 1);
        internal void Release() => _release.TrySetResult();
        public AcquisitionRunId RunId => _inner!.RunId;
        public InspectionRunState State => _inner!.State;
        public async ValueTask<InspectionAppendResult> CommitAsync(InspectionCommitInput commit,
            CancellationToken cancellationToken = default)
        {
            if (Volatile.Read(ref _armed) != 0)
            {
                Entered.TrySetResult();
                await _release.Task.ConfigureAwait(false);
            }
            return await _inner!.CommitAsync(commit, cancellationToken).ConfigureAwait(false);
        }
        public ValueTask<InspectionAppendResult> CommitGapAsync(GapRecord gap,
            CancellationToken cancellationToken = default) => _inner!.CommitGapAsync(gap, cancellationToken);
        public ValueTask<InspectionRunSealResult> SealRunAsync(CancellationToken cancellationToken = default) =>
            _inner!.SealRunAsync(cancellationToken);
        public ValueTask DisposeAsync() => _inner!.DisposeAsync();
    }

    private sealed class BlockingReplacementStore : IGateReplacementStore
    {
        private IGateReplacementStore? _inner;
        private readonly ManualResetEventSlim _release = new(false);
        internal TaskCompletionSource ActivationEntered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal IGateReplacementStore Decorate(IGateReplacementStore inner) { _inner = inner; return this; }
        internal void Release() => _release.Set();
        public GateCandidateReservationResult ReserveCandidate(InspectionId inspectionId, AcquisitionRunId runId,
            GateRunGeneration runGeneration, GatePlanId planId, GatePlanRevision planRevision,
            GateReprocessingAttemptId attemptId) => _inner!.ReserveCandidate(inspectionId, runId, runGeneration,
                planId, planRevision, attemptId);
        public GateStoreOnlyCandidateDescriptor MaterializeCandidate(GateCandidateReservation reservation,
            GateDerivedCoverageManifest manifest) => _inner!.MaterializeCandidate(reservation, manifest);
        public void ReleaseReservation(GateCandidateReservation reservation) => _inner!.ReleaseReservation(reservation);
        public GateCandidateActivationResult ActivateCandidate(GateCandidateActivationRequest request)
        { ActivationEntered.TrySetResult(); _release.Wait(); return _inner!.ActivateCandidate(request); }
        public GateCandidateAbandonResult AbandonCandidate(GateStoreOnlyCandidateDescriptor candidate) =>
            _inner!.AbandonCandidate(candidate);
        public GateCandidateReclaimResult ReclaimCandidate(GateStoreOnlyCandidateDescriptor candidate) =>
            _inner!.ReclaimCandidate(candidate);
        public GateDerivedMutationResult ReclaimPreviousView() => _inner!.ReclaimPreviousView();
        public ValueTask<GateDerivedMutationResult> CompletePreviousViewRetirementAsync() =>
            _inner!.CompletePreviousViewRetirementAsync();
    }

    private sealed class GateControlledSource : IUtFrameSource
    {
        private readonly Channel<ConventionalUtFrame> _frames = Channel.CreateBounded<ConventionalUtFrame>(1);
        private readonly TaskCompletionSource _producer = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private ConventionalUtFrameMetadata? _metadata;
        private ConventionalAcquisitionConfiguration? _configuration;
        private FrameOwner? _owner;
        private ulong _nextAcquisitionIndex;
        private bool _stopped;
        public UtSourceId SourceId { get; } = new("gate-controlled-wpf");
        public UtSourceCapabilities Capabilities { get; } = new([new PhysicalChannelId(0)], 64, 1_000_000, true);
        public UtSourceState State { get; private set; } = new(0, UtConnectionState.Disconnected, UtAcquisitionState.Idle);
        internal int DisposeCount { get; private set; }

        public Task ConnectAsync(CancellationToken cancellationToken = default)
        { State = new(1, UtConnectionState.Connected, UtAcquisitionState.Idle); return Task.CompletedTask; }
        public Task ConfigureAsync(ConventionalAcquisitionConfiguration configuration,
            CancellationToken cancellationToken = default)
        { _configuration = configuration; return Task.CompletedTask; }
        public Task<UtAcquisitionRun> StartAsync(AcquisitionRunId runId, CancellationToken cancellationToken = default)
        {
            _metadata = new(SourceId, runId, _configuration!, DateTimeOffset.UnixEpoch);
            State = new(2, UtConnectionState.Connected, UtAcquisitionState.Running);
            return Task.FromResult(new UtAcquisitionRun(_metadata, _frames.Reader, _producer.Task, _released.Task));
        }
        internal FrameOwner QueueFrame()
        {
            var owner = _owner = new FrameOwner(_metadata!.Configuration.SampleCount, ReleaseOwner);
            for (int index = 0; index < owner.Memory.Length; index++) owner.Memory.Span[index] = (short)(index * 100);
            ulong acquisitionIndex = _nextAcquisitionIndex++;
            if (!_frames.Writer.TryWrite(new(_metadata, acquisitionIndex, acquisitionIndex,
                TimeSpan.FromTicks((long)acquisitionIndex), null, owner)))
                throw new InvalidOperationException("The controlled frame was not admitted.");
            return owner;
        }
        public Task StopAsync(CancellationToken cancellationToken = default)
        {
            _stopped = true; _frames.Writer.TryComplete(); _producer.TrySetResult();
            if (_owner?.IsReturned != false) _released.TrySetResult();
            State = new(3, UtConnectionState.Connected, UtAcquisitionState.Idle);
            return Task.CompletedTask;
        }
        private void ReleaseOwner()
        { if (_stopped) _released.TrySetResult(); }
        public Task DisconnectAsync(CancellationToken cancellationToken = default)
        { State = new(4, UtConnectionState.Disconnected, UtAcquisitionState.Idle); return Task.CompletedTask; }
        public ValueTask DisposeAsync() { DisposeCount++; return ValueTask.CompletedTask; }

        internal sealed class FrameOwner(int count, Action returned) : IMemoryOwner<short>
        {
            private short[]? _samples = new short[count];
            internal TaskCompletionSource Returned { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
            internal bool IsReturned => _samples is null;
            public Memory<short> Memory => _samples ?? throw new ObjectDisposedException(nameof(FrameOwner));
            public void Dispose()
            {
                if (Interlocked.Exchange(ref _samples, null) is null) return;
                returned(); Returned.TrySetResult();
            }
        }
    }

    private sealed class ControlledSource : IUtFrameSource
    {
        public UtSourceId SourceId { get; } = new("controlled-wpf");
        public UtSourceCapabilities Capabilities { get; } = new([new PhysicalChannelId(0)], 64, 1_000_000, true);
        public UtSourceState State { get; private set; } = new(0, UtConnectionState.Disconnected, UtAcquisitionState.Idle);
        internal int DisposeCount { get; private set; }
        public Task ConnectAsync(CancellationToken cancellationToken = default)
        { State = new(1, UtConnectionState.Connected, UtAcquisitionState.Idle); return Task.CompletedTask; }
        public Task ConfigureAsync(ConventionalAcquisitionConfiguration configuration,
            CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<UtAcquisitionRun> StartAsync(AcquisitionRunId runId, CancellationToken cancellationToken = default) =>
            Task.FromException<UtAcquisitionRun>(new InvalidOperationException("No run requested by this composition test."));
        public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task DisconnectAsync(CancellationToken cancellationToken = default)
        { State = new(State.Version + 1, UtConnectionState.Disconnected, UtAcquisitionState.Idle); return Task.CompletedTask; }
        public ValueTask DisposeAsync() { DisposeCount++; return ValueTask.CompletedTask; }
    }

}
