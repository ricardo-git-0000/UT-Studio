using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using UTStudio.Contracts.Inspection;
using UTStudio.Contracts.Processing;
using UTStudio.Domain.Acquisition;
using UTStudio.Domain.Inspection;
using UTStudio.Domain.Processing;
using UTStudio.Processing.Core;
using UTStudio.Visualization.Core;
using UTStudio.Visualization.Wpf.Controls;

namespace UTStudio.Tests.Wpf;

[TestClass]
public sealed class AScanGateInteractionTests
{
    [TestMethod]
    public Task BeginCaptureRequiresConsumerAcceptanceAndReconcilesRejection() => StaTest.Run(() =>
    {
        using var fixture = Fixture.Create(); AScanControl control = fixture.Control;
        Assert.IsFalse(control.BeginGateDrag(fixture.HandlePoint)); Assert.IsFalse(control.IsMouseCaptured);
        control.GateBeginCaptureRequested += (_, _) => { };
        Assert.IsFalse(control.BeginGateDrag(fixture.HandlePoint)); Assert.IsFalse(control.IsMouseCaptured);
        return Task.CompletedTask;
    });

    [TestMethod]
    public Task CompatibleOwnSnapshotsPreserveDragAndExternalChangeCancels() => StaTest.Run(() =>
    {
        using var fixture = Fixture.Create(); AScanControl control = fixture.Control;
        GateCaptureGeneration token = fixture.Capture(11);
        var intents = new List<GateInteractionRequestedEventArgs>();
        control.GateBeginCaptureRequested += (_, request) => request.Accept(token);
        control.GateInteractionRequested += (_, request) => intents.Add(request);
        Assert.IsTrue(control.BeginGateDrag(fixture.HandlePoint)); Assert.AreEqual(token, control.ActiveGateCapture);

        control.VisualSnapshot = fixture.Composite(capture: token, evaluation: 2, threshold: .3);
        fixture.Render();
        control.ContinueGateDrag(new(fixture.HandlePoint.X, fixture.HandlePoint.Y - 10));
        Assert.AreEqual(token, control.ActiveGateCapture);
        Assert.IsTrue(intents.Any(x => x.Kind == GateOverlayInteractionKind.MoveCapture && x.Capture == token));

        control.VisualSnapshot = fixture.Composite(capture: null, evaluation: 3, threshold: .4);
        fixture.Render();
        Assert.AreEqual(token, control.ActiveGateCapture);

        control.VisualSnapshot = fixture.NotRequested();
        Assert.IsNull(control.ActiveGateCapture); Assert.IsFalse(control.IsMouseCaptured);
        Assert.AreEqual(1, intents.Count(x => x.Kind == GateOverlayInteractionKind.CancelPreview && x.Capture == token));
        return Task.CompletedTask;
    });

    [TestMethod]
    public Task OwnPreviewSourceMayAdvanceWhileStableCaptureTokenContinuesDragging() => StaTest.Run(() =>
    {
        using var fixture = Fixture.Create(); AScanControl control = fixture.Control;
        GateCaptureGeneration token = fixture.Capture(25);
        var intents = new List<GateInteractionRequestedEventArgs>();
        control.GateBeginCaptureRequested += (_, request) => request.Accept(token);
        control.GateInteractionRequested += (_, request) => intents.Add(request);

        Assert.IsTrue(control.BeginGateDrag(fixture.HandlePoint));
        control.VisualSnapshot = fixture.Composite(capture: token, evaluation: 2, advancePreview: true);
        fixture.Render();
        control.ContinueGateDrag(new(fixture.HandlePoint.X + 40, fixture.HandlePoint.Y));

        Assert.AreEqual(token, control.ActiveGateCapture);
        Assert.IsTrue(intents.Any(x => x.Kind == GateOverlayInteractionKind.MoveCapture && x.Capture == token));
        Assert.IsFalse(intents.Any(x => x.Kind == GateOverlayInteractionKind.CancelPreview));
        control.EndInteraction();
        return Task.CompletedTask;
    });

    [TestMethod]
    public Task TransientMissingLiveCompositionDoesNotCancelBoundaryDrag() => StaTest.Run(() =>
    {
        using var fixture = Fixture.Create(); AScanControl control = fixture.Control;
        GateCaptureGeneration token = fixture.Capture(18);
        var intents = new List<GateInteractionRequestedEventArgs>();
        control.GateBeginCaptureRequested += (_, request) => request.Accept(token);
        control.GateInteractionRequested += (_, request) => intents.Add(request);

        Assert.IsTrue(control.BeginGateDrag(fixture.HandlePoint));
        control.VisualSnapshot = null;

        Assert.AreEqual(token, control.ActiveGateCapture);
        Assert.IsTrue(control.IsMouseCaptured);
        Assert.IsFalse(intents.Any(x => x.Kind == GateOverlayInteractionKind.CancelPreview));

        control.VisualSnapshot = fixture.Composite(capture: token, evaluation: 2, threshold: .3);
        fixture.Render();
        control.ContinueGateDrag(new(fixture.HandlePoint.X, fixture.HandlePoint.Y - 10));
        Assert.IsTrue(intents.Any(x => x.Kind == GateOverlayInteractionKind.MoveCapture && x.Capture == token));
        control.EndInteraction();
        return Task.CompletedTask;
    });

    [TestMethod]
    public Task BoundaryDragTracksPhysicalPointerWhenRoutedMouseMoveIsCoalesced() => StaTest.Run(() =>
    {
        using var fixture = Fixture.Create(); AScanControl control = fixture.Control;
        GateCaptureGeneration token = fixture.Capture(23);
        var intents = new List<GateInteractionRequestedEventArgs>();
        control.GateBeginCaptureRequested += (_, request) => request.Accept(token);
        control.GateInteractionRequested += (_, request) => intents.Add(request);

        Assert.IsTrue(control.BeginGateDrag(fixture.HandlePoint));
        fixture.CurrentPointer = new(fixture.HandlePoint.X, fixture.HandlePoint.Y - 20);
        control.RefreshCursorGesture();

        Assert.IsTrue(intents.Any(x => x.Kind == GateOverlayInteractionKind.MoveCapture && x.Capture == token));
        control.EndInteraction();
        return Task.CompletedTask;
    });

    [TestMethod]
    public Task GateDragContinuesFromPhysicalPointerAfterLostCaptureLikeCursorDrag() => StaTest.Run(() =>
    {
        using var fixture = Fixture.Create(); AScanControl control = fixture.Control;
        GateCaptureGeneration token = fixture.Capture(24);
        var intents = new List<GateInteractionRequestedEventArgs>();
        control.GateBeginCaptureRequested += (_, request) => request.Accept(token);
        control.GateInteractionRequested += (_, request) => intents.Add(request);

        Assert.IsTrue(control.BeginGateDrag(fixture.HandlePoint));
        Assert.IsTrue(control.IsMouseCaptured);
        Mouse.Capture(null);
        fixture.CurrentPointer = new(fixture.HandlePoint.X + 30, fixture.HandlePoint.Y);
        control.RefreshCursorGesture();

        Assert.AreEqual(token, control.ActiveGateCapture);
        Assert.IsFalse(control.IsMouseCaptured);
        Assert.IsFalse(intents.Any(x => x.Kind == GateOverlayInteractionKind.CancelPreview));
        Assert.IsTrue(intents.Any(x => x.Kind == GateOverlayInteractionKind.MoveCapture && x.Capture == token));
        control.EndInteraction();
        return Task.CompletedTask;
    });

    [TestMethod]
    public Task DisplayedLiveFrameMayLagAcceptedGateCaptureContext() => StaTest.Run(() =>
    {
        using var fixture = Fixture.Create(); AScanControl control = fixture.Control;
        GateCaptureGeneration newer = fixture.Capture(16, contextGeneration: 2);
        control.GateBeginCaptureRequested += (_, request) => request.Accept(newer);

        Assert.IsTrue(control.BeginGateDrag(fixture.HandlePoint));
        Assert.AreEqual(newer, control.ActiveGateCapture);
        control.EndInteraction();
        return Task.CompletedTask;
    });

    [TestMethod]
    public Task ReentrantExternalSnapshotAfterAcceptedBeginDoesNotAcquireCapture() => StaTest.Run(() =>
    {
        using var fixture = Fixture.Create(); AScanControl control = fixture.Control;
        GateCaptureGeneration token = fixture.Capture(15);
        var intents = new List<GateInteractionRequestedEventArgs>();
        control.GateInteractionRequested += (_, request) => intents.Add(request);
        control.GateBeginCaptureRequested += (_, request) =>
        {
            request.Accept(token);
            control.VisualSnapshot = fixture.NotRequested();
        };

        Assert.IsFalse(control.BeginGateDrag(fixture.HandlePoint));
        Assert.IsFalse(control.IsMouseCaptured);
        Assert.IsNull(control.ActiveGateCapture);
        Assert.AreEqual(1, intents.Count(x => x.Kind == GateOverlayInteractionKind.CancelPreview && x.Capture == token));
        return Task.CompletedTask;
    });

    [TestMethod]
    public Task ReentrantCompatibleLiveSnapshotWithoutProjectedTokenStillAcquiresCapture() => StaTest.Run(() =>
    {
        using var fixture = Fixture.Create(); AScanControl control = fixture.Control;
        GateCaptureGeneration token = fixture.Capture(22);
        control.GateBeginCaptureRequested += (_, request) =>
        {
            request.Accept(token);
            control.VisualSnapshot = fixture.Composite(capture: null, evaluation: 2, threshold: .4);
        };

        Assert.IsTrue(control.BeginGateDrag(fixture.HandlePoint));
        Assert.AreEqual(token, control.ActiveGateCapture);
        Assert.IsTrue(control.IsMouseCaptured);
        control.EndInteraction();
        return Task.CompletedTask;
    });

    [TestMethod]
    public Task ReentrantTransientMissingSnapshotAfterAcceptedBeginStillAcquiresCapture() => StaTest.Run(() =>
    {
        using var fixture = Fixture.Create(); AScanControl control = fixture.Control;
        GateCaptureGeneration token = fixture.Capture(19);
        control.GateBeginCaptureRequested += (_, request) =>
        {
            request.Accept(token);
            control.VisualSnapshot = null;
        };

        Assert.IsTrue(control.BeginGateDrag(fixture.HandlePoint));
        Assert.AreEqual(token, control.ActiveGateCapture);
        Assert.IsTrue(control.IsMouseCaptured);
        control.EndInteraction();
        return Task.CompletedTask;
    });

    [TestMethod]
    public Task ReentrantExternalSnapshotAfterAcceptedWindowBeginDoesNotAcquireCapture() => StaTest.Run(() =>
    {
        using var fixture = Fixture.Create(); AScanControl control = fixture.Control;
        GateCaptureGeneration token = fixture.WindowCapture(17);
        var intents = new List<GateInteractionRequestedEventArgs>();
        control.GateInteractionRequested += (_, request) => intents.Add(request);
        control.GateBeginWindowCaptureRequested += (_, request) =>
        {
            request.Accept(token, 0);
            control.VisualSnapshot = fixture.NotRequested();
        };

        Assert.IsFalse(control.BeginGateWindowDrag(new(225, 120)));
        Assert.IsFalse(control.IsMouseCaptured);
        Assert.IsNull(control.ActiveGateCapture);
        Assert.AreEqual(1, intents.Count(x => x.Kind == GateOverlayInteractionKind.CancelPreview && x.Capture == token));
        return Task.CompletedTask;
    });

    [TestMethod]
    public Task ReentrantTransientMissingSnapshotAfterAcceptedWindowBeginStillAcquiresCapture() => StaTest.Run(() =>
    {
        using var fixture = Fixture.Create(); AScanControl control = fixture.Control;
        GateCaptureGeneration token = fixture.WindowCapture(21);
        control.GateBeginWindowCaptureRequested += (_, request) =>
        {
            request.Accept(token, 0);
            control.VisualSnapshot = null;
        };

        Assert.IsTrue(control.BeginGateWindowDrag(new(225, 120)));
        Assert.AreEqual(token, control.ActiveGateCapture);
        Assert.IsTrue(control.IsMouseCaptured);
        control.EndInteraction();
        return Task.CompletedTask;
    });

    [TestMethod]
    public Task GateWinsOnlyWhenAcceptedAndCursorOtherwiseRetainsGesture() => StaTest.Run(() =>
    {
        using var fixture = Fixture.Create(overlapCursor: true); AScanControl control = fixture.Control;
        Assert.IsFalse(control.BeginGateDrag(fixture.HandlePoint));
        Assert.IsTrue(control.BeginCursorDrag(fixture.HandlePoint)); control.EndCursorDrag();
        GateCaptureGeneration token = fixture.Capture(12);
        control.GateBeginCaptureRequested += (_, request) => request.Accept(token);
        Assert.IsTrue(control.BeginGateDrag(fixture.HandlePoint));
        Assert.IsFalse(control.BeginCursorDrag(fixture.HandlePoint));
        control.EndInteraction();
        return Task.CompletedTask;
    });

    [TestMethod]
    public Task EntireVisibleStartAndEndLinesAreActionable() => StaTest.Run(() =>
    {
        using var fixture = Fixture.Create();
        var start = new GateHandleLogicalIdentity(new("A"), new(1), GateHandleKind.Start);
        fixture.ShowHandles(start); fixture.Render();
        Assert.AreEqual(start, fixture.Control.HitTestGate(new(160, 40))!.Identity);
        Assert.AreEqual(GateHandleKind.End, fixture.Control.HitTestGate(new(200, 160))!.Kind);
        return Task.CompletedTask;
    });

    [TestMethod]
    public Task EntireVisibleThresholdLineIsActionableAndDragsWithLeftCapture() => StaTest.Run(() =>
    {
        using var fixture = Fixture.Create();
        AScanControl control = fixture.Control;
        Point linePoint = new(260, fixture.HandlePoint.Y);
        GateHandleVisualState hit = control.HitTestGate(linePoint)!;
        Assert.AreEqual(GateHandleKind.Threshold, hit.Kind);

        GateCaptureGeneration token = fixture.Capture(26);
        var intents = new List<GateInteractionRequestedEventArgs>();
        control.GateBeginCaptureRequested += (_, request) => request.Accept(token);
        control.GateInteractionRequested += (_, request) => intents.Add(request);
        Assert.IsTrue(control.BeginGateDrag(linePoint));
        control.ContinueGateDrag(new(linePoint.X, linePoint.Y - 20));

        GateInteractionRequestedEventArgs move = intents.Last(x =>
            x.Kind == GateOverlayInteractionKind.MoveCapture);
        Assert.AreEqual(GateHandleKind.Threshold, move.Handle!.Value.Kind);
        Assert.IsGreaterThan(.25, move.Value!.Value);
        control.EndInteraction();
        return Task.CompletedTask;
    });

    [TestMethod]
    public Task ExactVisibleBoundaryRemainsActionableWhenHandleProjectionLags() => StaTest.Run(() =>
    {
        using var fixture = Fixture.Create();
        fixture.ShowLaggingStartHandle(); fixture.Render();

        GateHandleVisualState hit = fixture.Control.HitTestGate(new(260, 160))!;
        Assert.AreEqual(new GateId("A"), hit.GateId);
        Assert.AreEqual(GateHandleKind.Start, hit.Kind);
        return Task.CompletedTask;
    });

    [TestMethod]
    public Task RightDragMovesWholeGateByDeltaWithoutPublishingAWindowHandle() => StaTest.Run(() =>
    {
        using var fixture = Fixture.Create(); AScanControl control = fixture.Control;
        GateCaptureGeneration token = fixture.WindowCapture(20);
        var intents = new List<GateInteractionRequestedEventArgs>();
        control.GateBeginWindowCaptureRequested += (_, request) => request.Accept(token, 0);
        control.GateInteractionRequested += (_, request) => intents.Add(request);
        Point inside = new(225, 120);
        Assert.AreEqual(new GateId("A"), control.HitTestGateWindow(inside)!.GateId);
        Assert.IsFalse(control.DisplayedVisualSnapshot!.Gates.Handles.Any(x => x.Kind == GateHandleKind.Window));
        Assert.IsTrue(control.BeginGateWindowDrag(inside));
        control.VisualSnapshot = fixture.Composite(capture: token, evaluation: 2);
        fixture.Render();

        control.ContinueGateDrag(new(275, inside.Y));
        GateInteractionRequestedEventArgs move = intents.Last(x => x.Kind == GateOverlayInteractionKind.MoveCapture);
        double expectedDelta = 50 / (control.ActualWidth - 84) *
            control.DisplayedVisualSnapshot.Transform.TimeViewport.VisibleSpanSeconds;
        Assert.AreEqual(expectedDelta, move.Value!.Value, 1e-12);
        Assert.AreEqual(token, move.Capture);
        control.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Right)
        { RoutedEvent = Mouse.MouseUpEvent, Source = control });
        Assert.IsNull(control.ActiveGateCapture);
        Assert.IsTrue(intents.Any(x => x.Kind == GateOverlayInteractionKind.EndCapture));
        return Task.CompletedTask;
    });

    [TestMethod]
    public Task AutomationPeersAreStableRetiredAndMarshalCrossThread() => StaTest.Run(async () =>
    {
        using var fixture = Fixture.Create(); AScanControl control = fixture.Control;
        var root = (AScanControlAutomationPeer)(UIElementAutomationPeer.CreatePeerForElement(control) ??
            new AScanControlAutomationPeer(control));
        control.VisualSnapshot = fixture.Composite(evaluation: 2, threshold: .35); fixture.Render();
        AutomationPeer peer1 = root.GetChildren()!.Single(x => x.GetName().Contains("Umbral", StringComparison.Ordinal));
        control.VisualSnapshot = fixture.Composite(evaluation: 3, threshold: .4); fixture.Render();
        AutomationPeer peer2 = root.GetChildren()!.Single(x => x.GetName().Contains("Umbral", StringComparison.Ordinal));
        Assert.AreSame(peer1, peer2);
        var automationIntents = new List<GateInteractionRequestedEventArgs>();
        control.GateInteractionRequested += (_, request) => automationIntents.Add(request);
        string name = await Task.Run(peer1.GetName);
        var range = (IRangeValueProvider)peer1.GetPattern(PatternInterface.RangeValue)!;
        var selection = (ISelectionItemProvider)peer1.GetPattern(PatternInterface.SelectionItem)!;
        double value = await Task.Run(() => range.Value);
        Assert.Contains("Gate A", name); Assert.AreEqual(.4, value, 1e-12);
        await Task.Run(() => range.SetValue(.45));
        await Task.Run(selection.Select);
        Assert.IsTrue(automationIntents.Any(x => x.Value == .45));
        Assert.IsTrue(automationIntents.Any(x => x.Kind == GateOverlayInteractionKind.Select));
        Assert.IsFalse(range.IsReadOnly); Assert.AreEqual(0, range.Minimum); Assert.AreEqual(1, range.Maximum);
        Rect bounds = peer1.GetBoundingRectangle();
        Point expectedTopLeft = control.PointToScreen(new(fixture.HandlePoint.X - 6, fixture.HandlePoint.Y - 6));
        Point expectedBottomRight = control.PointToScreen(new(fixture.HandlePoint.X + 6, fixture.HandlePoint.Y + 6));
        Assert.AreEqual(expectedBottomRight.X - expectedTopLeft.X, bounds.Width, 1e-9);
        Assert.AreEqual(expectedBottomRight.Y - expectedTopLeft.Y, bounds.Height, 1e-9);

        control.VisualSnapshot = fixture.Composite(includeHandles: false); fixture.Render();
        Assert.IsNull(root.GetChildren());
        Assert.Contains("Gate A", await Task.Run(peer1.GetName));
        Assert.IsTrue(await Task.Run(() => range.IsReadOnly));
        Assert.AreEqual(.4, await Task.Run(() => range.Value), 1e-12);
        Assert.IsFalse(await Task.Run(() => selection.IsSelected));
        Assert.IsFalse(await Task.Run(peer1.IsKeyboardFocusable));
        Assert.IsFalse(await Task.Run(peer1.IsEnabled));
        Assert.IsTrue(await Task.Run(peer1.IsOffscreen));
        Assert.AreEqual(Rect.Empty, await Task.Run(peer1.GetBoundingRectangle));
        Assert.IsFalse(await Task.Run(peer1.HasKeyboardFocus));
        await Assert.ThrowsExactlyAsync<System.Windows.Automation.ElementNotAvailableException>(async () =>
            await Task.Run(() => range.SetValue(.5)));
        await Assert.ThrowsExactlyAsync<System.Windows.Automation.ElementNotAvailableException>(async () =>
            await Task.Run(selection.Select));
        await Assert.ThrowsExactlyAsync<System.Windows.Automation.ElementNotAvailableException>(async () =>
            await Task.Run(selection.AddToSelection));
        await Assert.ThrowsExactlyAsync<System.Windows.Automation.ElementNotAvailableException>(async () =>
            await Task.Run(selection.RemoveFromSelection));
        await Assert.ThrowsExactlyAsync<System.Windows.Automation.ElementNotAvailableException>(async () =>
            await Task.Run(peer1.SetFocus));
    });

    [TestMethod]
    public Task LiveEvaluationKeepsLastCoherentGateUntilReplacementIsComplete() => StaTest.Run(() =>
    {
        using var fixture = Fixture.Create();
        AScanControl control = fixture.Control;
        AScanCompositeVisualSnapshot complete = control.DisplayedVisualSnapshot!;
        Assert.AreEqual(1, control.RenderedDetectedGateEventCount);

        control.VisualSnapshot = fixture.EvaluatingSameDefinition();
        fixture.Render();

        Assert.AreSame(complete, control.DisplayedVisualSnapshot);
        Assert.IsNotNull(control.RenderedSignalSnapshotVersion);
        Assert.IsGreaterThan(complete.Signal.Version, control.RenderedSignalSnapshotVersion.Value,
            "RF must advance while the complete gate overlay is retained.");
        Assert.AreEqual(GateOverlayGeometryStatus.Current, control.DisplayedVisualSnapshot!.Gates.Status);
        Assert.IsNotEmpty(control.DisplayedVisualSnapshot.Gates.Gates);
        Assert.AreEqual(0, control.RenderedDetectedGateEventCount,
            "An event from the retained trace must not be drawn over newer RF.");

        control.VisualSnapshot = fixture.NotRequested();
        fixture.Render();
        Assert.AreNotSame(complete, control.DisplayedVisualSnapshot);
        Assert.AreEqual(GateOverlayGeometryStatus.NotRequested, control.DisplayedVisualSnapshot!.Gates.Status);
        return Task.CompletedTask;
    });

    [TestMethod]
    public Task OffViewportGateHasNoFalseHitOrAutomationAndRestoresWhenVisible() => StaTest.Run(async () =>
    {
        using var fixture = Fixture.Create(); AScanControl control = fixture.Control;
        var root = (AScanControlAutomationPeer)(UIElementAutomationPeer.CreatePeerForElement(control) ??
            new AScanControlAutomationPeer(control));
        AutomationPeer visiblePeer = root.GetChildren()!.Single();
        var staleRange = (IRangeValueProvider)visiblePeer.GetPattern(PatternInterface.RangeValue)!;

        control.VisualSnapshot = fixture.Composite(evaluation: 2, includeHandles: false); fixture.Render();
        Assert.IsNull(GateOverlayHitTester.HitTest(control.DisplayedVisualSnapshot!.Gates,
            fixture.HandlePoint.X, fixture.HandlePoint.Y, 12));
        Assert.IsNull(root.GetChildren());
        Assert.Contains("Gate A", await Task.Run(visiblePeer.GetName));
        Assert.IsTrue(await Task.Run(() => staleRange.IsReadOnly));
        await Assert.ThrowsExactlyAsync<System.Windows.Automation.ElementNotAvailableException>(async () =>
            await Task.Run(() => staleRange.SetValue(.5)));

        control.VisualSnapshot = fixture.Composite(evaluation: 3); fixture.Render();
        Assert.IsNotNull(GateOverlayHitTester.HitTest(control.DisplayedVisualSnapshot!.Gates,
            fixture.HandlePoint.X, fixture.HandlePoint.Y, 12));
        Assert.HasCount(1, root.GetChildren()!);
    });

    [TestMethod]
    public Task ResizeAndUnloadInvalidateBeforeLateGestureCanAct() => StaTest.Run(() =>
    {
        using var fixture = Fixture.Create(); AScanControl control = fixture.Control;
        GateCaptureGeneration token = fixture.Capture(13); int moves = 0;
        control.GateBeginCaptureRequested += (_, request) => request.Accept(token);
        control.GateInteractionRequested += (_, request) => { if (request.Kind == GateOverlayInteractionKind.MoveCapture) moves++; };
        Assert.IsTrue(control.BeginGateDrag(fixture.HandlePoint));
        fixture.Host.Width += 100; fixture.Host.UpdateLayout();
        control.ContinueGateDrag(fixture.HandlePoint); Assert.AreEqual(0, moves); Assert.IsNull(control.ActiveGateCapture);
        control.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));
        Assert.IsFalse(control.IsMouseCaptured); Assert.IsFalse(control.HasDisplayedSnapshot);
        return Task.CompletedTask;
    });

    [TestMethod]
    public Task KeyboardEnterNeverAppliesAndTabLeavesAtStableEnd() => StaTest.Run(() =>
    {
        using var fixture = Fixture.Create(); AScanControl control = fixture.Control;
        var intents = new List<GateInteractionRequestedEventArgs>();
        control.GateInteractionRequested += (_, request) => intents.Add(request);
        control.Focus();
        var enter = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(control),
            Environment.TickCount, Key.Enter)
        { RoutedEvent = Keyboard.PreviewKeyDownEvent };
        control.RaiseEvent(enter);
        Assert.IsTrue(enter.Handled);
        Assert.AreEqual(GateOverlayInteractionKind.ConfirmPreview, intents.Single().Kind);
        Assert.IsFalse(Enum.GetNames<GateOverlayInteractionKind>().Any(name => name.Contains("Apply", StringComparison.Ordinal)));
        intents.Clear();
        var tab = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(control),
            Environment.TickCount, Key.Tab)
        { RoutedEvent = Keyboard.PreviewKeyDownEvent };
        control.RaiseEvent(tab);
        Assert.IsFalse(tab.Handled); Assert.IsEmpty(intents);
        return Task.CompletedTask;
    });

    [TestMethod]
    public Task TabAndShiftTabTraverseStableEditableHandleOrderAndLeaveAtEdges() => StaTest.Run(() =>
    {
        using var fixture = Fixture.Create(); AScanControl control = fixture.Control;
        GateHandleLogicalIdentity aStart = new(new("A"), new(1), GateHandleKind.Start);
        GateHandleLogicalIdentity aEnd = new(new("A"), new(1), GateHandleKind.End);
        GateHandleLogicalIdentity bThreshold = new(new("B"), new(1), GateHandleKind.Threshold);
        var intents = new List<GateInteractionRequestedEventArgs>();
        control.GateInteractionRequested += (_, request) => intents.Add(request);

        fixture.ShowHandles(aStart); fixture.Render();
        AssertFocusMove(ModifierKeys.None, GateOverlayInteractionKind.FocusNext, aEnd);
        fixture.ShowHandles(aEnd); fixture.Render();
        AssertFocusMove(ModifierKeys.None, GateOverlayInteractionKind.FocusNext, bThreshold);
        fixture.ShowHandles(bThreshold); fixture.Render();
        AssertLeaves(ModifierKeys.None);

        AssertFocusMove(ModifierKeys.Shift, GateOverlayInteractionKind.FocusPrevious, aEnd);
        fixture.ShowHandles(aEnd); fixture.Render();
        AssertFocusMove(ModifierKeys.Shift, GateOverlayInteractionKind.FocusPrevious, aStart);
        fixture.ShowHandles(aStart); fixture.Render();
        AssertLeaves(ModifierKeys.Shift);
        return Task.CompletedTask;

        void AssertFocusMove(ModifierKeys modifiers, GateOverlayInteractionKind kind,
            GateHandleLogicalIdentity expected)
        {
            intents.Clear(); fixture.Modifiers = modifiers;
            KeyEventArgs args = fixture.Key(Key.Tab); control.RaiseEvent(args);
            Assert.IsTrue(args.Handled);
            GateInteractionRequestedEventArgs intent = intents.Single();
            Assert.AreEqual(kind, intent.Kind); Assert.AreEqual(expected, intent.Handle);
        }

        void AssertLeaves(ModifierKeys modifiers)
        {
            intents.Clear(); fixture.Modifiers = modifiers;
            KeyEventArgs args = fixture.Key(Key.Tab); control.RaiseEvent(args);
            Assert.IsFalse(args.Handled); Assert.IsEmpty(intents);
        }
    });

    [TestMethod]
    public Task DpiTransitionInvalidatesCaptureUntilCompatibleSnapshotAndScalesAutomationBounds() => StaTest.Run(() =>
    {
        using var fixture = Fixture.Create(); AScanControl control = fixture.Control;
        GateCaptureGeneration original = fixture.Capture(20);
        GateCaptureGeneration accepted = original;
        control.GateBeginCaptureRequested += (_, request) => request.Accept(accepted);
        Assert.IsTrue(control.BeginGateDrag(fixture.HandlePoint));

        VisualTreeHelper.SetRootDpi(fixture.Host, new DpiScale(1.5, 1.5));
        fixture.Layout();
        Assert.AreEqual(1.5, VisualTreeHelper.GetDpi(control).DpiScaleX, 1e-12);
        Assert.IsNull(control.ActiveGateCapture); Assert.IsFalse(control.IsMouseCaptured);
        Assert.IsFalse(control.HasCurrentTransform);
        Assert.IsFalse(control.BeginGateDrag(fixture.HandlePoint));

        control.VisualSnapshot = fixture.Composite(evaluation: 2); fixture.Render(144);
        Assert.IsTrue(control.HasCurrentTransform);
        GateCaptureGeneration compatible = fixture.Capture(21);
        accepted = compatible;
        Assert.IsTrue(control.BeginGateDrag(fixture.HandlePoint)); control.EndInteraction();

        var root = (AScanControlAutomationPeer)(UIElementAutomationPeer.CreatePeerForElement(control) ??
            new AScanControlAutomationPeer(control));
        AutomationPeer peer = root.GetChildren()!.Single(); Rect bounds = peer.GetBoundingRectangle();
        Point topLeft = control.PointToScreen(new(fixture.HandlePoint.X - 6, fixture.HandlePoint.Y - 6));
        Point bottomRight = control.PointToScreen(new(fixture.HandlePoint.X + 6, fixture.HandlePoint.Y + 6));
        Assert.AreEqual(topLeft, bounds.TopLeft); Assert.AreEqual(bottomRight, bounds.BottomRight);
        Assert.AreEqual(bottomRight.X - topLeft.X, bounds.Width, 1e-9);
        Assert.AreEqual(bottomRight.Y - topLeft.Y, bounds.Height, 1e-9);
        Assert.AreEqual(1.5, control.DisplayedVisualSnapshot!.Transform.DpiScaleX, 1e-12);
        return Task.CompletedTask;
    });

    [TestMethod]
    public Task KeyboardDoesNotConfirmOrCancelWithoutPreview() => StaTest.Run(() =>
    {
        using var fixture = Fixture.Create(); AScanControl control = fixture.Control;
        control.VisualSnapshot = fixture.NotRequested(); fixture.Render();
        var intents = new List<GateInteractionRequestedEventArgs>();
        control.GateInteractionRequested += (_, request) => intents.Add(request);
        foreach (Key key in new[] { Key.Enter, Key.Escape })
        {
            var args = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(control),
                Environment.TickCount, key)
            { RoutedEvent = Keyboard.PreviewKeyDownEvent };
            control.RaiseEvent(args);
            Assert.IsFalse(args.Handled);
        }
        Assert.IsEmpty(intents);
        return Task.CompletedTask;
    });

    [TestMethod]
    public Task ReentrantUnloadAfterAcceptedBeginReconcilesPortableCapture() => StaTest.Run(() =>
    {
        using var fixture = Fixture.Create(); AScanControl control = fixture.Control;
        GateCaptureGeneration token = fixture.Capture(14); int cancellations = 0;
        control.GateBeginCaptureRequested += (_, request) =>
        {
            request.Accept(token);
            control.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));
        };
        control.GateInteractionRequested += (_, request) =>
        { if (request.Kind == GateOverlayInteractionKind.CancelPreview && request.Capture == token) cancellations++; };
        Assert.IsFalse(control.BeginGateDrag(fixture.HandlePoint));
        Assert.AreEqual(1, cancellations); Assert.IsFalse(control.IsMouseCaptured);
        return Task.CompletedTask;
    });

    private sealed class Fixture : IDisposable
    {
        private readonly AScanSnapshot _signal;
        private readonly GateGeometrySourceIdentity _source;
        private readonly TraceIdentity _trace;
        private readonly InspectionRevision _inspectionRevision = new(1);
        private readonly GateHandleVisualState _baseHandle;
        private readonly ManualClock _clock;
        private readonly ModifierState _modifierState;
        private readonly PointerState _pointerState;
        private Fixture(Window host, AScanControl control, AScanSnapshot signal, GateGeometrySourceIdentity source,
            TraceIdentity trace, GateHandleVisualState handle, ManualClock clock, ModifierState modifierState,
            PointerState pointerState)
        { Host = host; Control = control; _signal = signal; _source = source; _trace = trace; _baseHandle = handle; _clock = clock; _modifierState = modifierState; _pointerState = pointerState; }
        internal Window Host { get; }
        internal AScanControl Control { get; }
        internal Point HandlePoint => new(_baseHandle.X, _baseHandle.Y);
        internal Point CurrentPointer { set => _pointerState.Value = value; }
        internal ModifierKeys Modifiers { set => _modifierState.Value = value; }

        internal static Fixture Create(bool overlapCursor = false)
        {
            var run = new AcquisitionRunId(Guid.NewGuid()); var inspection = new InspectionId(Guid.NewGuid());
            AScanSnapshot signal = VisualizationComponentTests.Snapshot(1, run);
            var stream = new UtStreamIdentity(inspection, run, signal.Metadata.SourceId, signal.Metadata.Configuration.PhysicalChannelId);
            var trace = new TraceIdentity(stream, 1); signal.SetAuthority(trace, new(1));
            GateDefinition gate = new(new("A"), new(1), new(GateTimeReference.TraceOrigin(), 0, 6e-6),
                GateSignalDomain.RawSigned, new(GateDetectorKind.FirstRisingCrossing, new(.25)), [GateMeasureKind.Amplitude]);
            GatePlan plan = new(new("plan"), new(2), new(2),
                new(GatePlanCompilerOptions.CurrentAlgorithmVersion), [gate]);
            GatePlanSignature signature = new GatePlanCompiler().Compile(plan).CompiledPlan!.Signature;
            var source = GateGeometrySourceIdentity.PreviewSource(new(inspection, plan.Id), new(1),
                plan.Revision, signature, new(Guid.NewGuid(), 1));
            var clock = new ManualClock(); var modifierState = new ModifierState(); var pointerState = new PointerState();
            var control = new AScanControl(clock, () => modifierState.Value, null, null, () => pointerState.Value);
            var host = new Window { Content = control, Width = 640, Height = 300, WindowStyle = WindowStyle.None, ShowInTaskbar = false };
            host.Show(); host.UpdateLayout();
            double x = overlapCursor ? 64 + (control.ActualWidth - 84) * .25 : 180;
            var handle = new GateHandleVisualState(gate.Id, gate.Revision, GateHandleKind.Threshold,
                x, 100, .25, "normalized", 0, 1, .01, true, true, true);
            pointerState.Value = new(handle.X, handle.Y);
            var fixture = new Fixture(host, control, signal, source, trace, handle, clock, modifierState, pointerState);
            control.VisualSnapshot = fixture.Composite(); fixture.Render(); return fixture;
        }
        internal GateCaptureGeneration Capture(ulong nonce, ulong contextGeneration = 1) => new(contextGeneration, nonce, _signal.Metadata.RunId, _trace,
            _source.Kind, _source.Revision, _source.Preview, _baseHandle.GateId, _baseHandle.Kind,
            _baseHandle.AnchorGateId, _baseHandle.AnchorRevision, Control.DisplayedVisualSnapshot!.Transform.Version);
        internal GateCaptureGeneration WindowCapture(ulong nonce) => new(1, nonce, _signal.Metadata.RunId, _trace,
            _source.Kind, _source.Revision, _source.Preview, _baseHandle.GateId, GateHandleKind.Window,
            default, default, Control.DisplayedVisualSnapshot!.Transform.Version);
        internal AScanCompositeVisualSnapshot Composite(GateCaptureGeneration? capture = null, ulong evaluation = 1,
            double threshold = .25, bool includeHandles = true, bool advancePreview = false)
        {
            DpiScale dpi = VisualTreeHelper.GetDpi(Control);
            var transform = new GateVisualTransform(1, ScanTimeViewportOperations.Create(_signal.MinimumTimeSeconds, _signal.MaximumTimeSeconds),
                GateAmplitudePresentationKind.RawRf, 64, Control.ActualWidth - 84, Control.ActualHeight - 38,
                -(Control.ActualHeight - 54), Control.ActualWidth, Control.ActualHeight, dpi.DpiScaleX, dpi.DpiScaleY);
            GateHandleVisualState handle = new(_baseHandle.GateId, _baseHandle.GateRevision, _baseHandle.Kind,
                _baseHandle.X, _baseHandle.Y, threshold, _baseHandle.Unit, 0, 1, .01, true, true, true);
            var projected = new ProjectedGateGeometryItem(handle.GateId, handle.GateRevision,
                GateGeometryProjectionStatus.Projected, 150, 300, [new(0, threshold * 100, handle.Y)],
                new(225, 90, 4e-6, 100, 100, GateDetectionEventKind.RisingCrossing));
            GateGeometrySourceIdentity source = advancePreview
                ? GateGeometrySourceIdentity.PreviewSource(_source.Authority, _source.BaseRevision,
                    _source.CandidateRevision, _source.Signature, new(Guid.NewGuid(), _source.Preview.Generation + 1))
                : _source;
            var overlay = new GateOverlayVisualSnapshot(GateOverlayGeometryStatus.Current, source, _trace,
                _inspectionRevision, [projected], includeHandles ? [handle] : [], includeHandles ? handle.GateId : null,
                includeHandles ? handle.Kind : GateHandleKind.Unknown, 1, evaluation, capture);
            return new(_signal, AScanCursorMeasurements.Create(_signal), overlay, transform);
        }
        internal AScanCompositeVisualSnapshot NotRequested()
        {
            GateVisualTransform transform = Composite().Transform;
            var overlay = new GateOverlayVisualSnapshot(GateOverlayGeometryStatus.NotRequested, null, null,
                InspectionRevision.Initial, [], [], null, GateHandleKind.Unknown, 1, 0, null);
            return new(_signal, AScanCursorMeasurements.Create(_signal), overlay, transform);
        }
        internal AScanCompositeVisualSnapshot EvaluatingSameDefinition()
        {
            AScanSnapshot signal = VisualizationComponentTests.Snapshot(2, _signal.Metadata.RunId);
            var trace = new TraceIdentity(_trace.Stream, 2);
            signal.SetAuthority(trace, _inspectionRevision);
            GateVisualTransform transform = Composite().Transform;
            var overlay = new GateOverlayVisualSnapshot(GateOverlayGeometryStatus.Evaluating, _source, trace,
                _inspectionRevision, [], [], null, GateHandleKind.Unknown, 2, 2, null);
            return new(signal, AScanCursorMeasurements.Create(signal), overlay, transform);
        }
        internal void ShowHandles(GateHandleLogicalIdentity focused)
        {
            GateHandleVisualState[] handles =
            [
                Handle("B", GateHandleKind.Threshold, 260, .4, focused, true),
                Handle("A", GateHandleKind.Threshold, 220, .3, focused, false),
                Handle("A", GateHandleKind.End, 200, 8e-6, focused, true),
                Handle("A", GateHandleKind.Start, 160, 2e-6, focused, true)
            ];
            GateVisualTransform transform = Composite().Transform;
            var overlay = new GateOverlayVisualSnapshot(GateOverlayGeometryStatus.Current, _source, _trace,
                _inspectionRevision, [], handles, focused.GateId, focused.Kind, 1, 30, null);
            Control.VisualSnapshot = new(_signal, AScanCursorMeasurements.Create(_signal), overlay, transform);
        }
        internal void ShowLaggingStartHandle()
        {
            GateHandleVisualState handle = Handle("A", GateHandleKind.Start, 180, 2e-6,
                new(new("A"), new(1), GateHandleKind.Start), true);
            var projected = new ProjectedGateGeometryItem(handle.GateId, handle.GateRevision,
                GateGeometryProjectionStatus.Projected, 260, 320, [], null);
            GateVisualTransform transform = Composite().Transform;
            var overlay = new GateOverlayVisualSnapshot(GateOverlayGeometryStatus.Current, _source, _trace,
                _inspectionRevision, [projected], [handle], handle.GateId, handle.Kind, 1, 31, null);
            Control.VisualSnapshot = new(_signal, AScanCursorMeasurements.Create(_signal), overlay, transform);
        }
        internal KeyEventArgs Key(Key key) => new(Keyboard.PrimaryDevice, PresentationSource.FromVisual(Control),
            Environment.TickCount, key)
        { RoutedEvent = Keyboard.PreviewKeyDownEvent };
        internal void Layout() { Host.Measure(new Size(Host.Width, Host.Height)); Host.Arrange(new Rect(0, 0, Host.Width, Host.Height)); Host.UpdateLayout(); }
        internal void Render(double dpi = 96)
        {
            _clock.Advance(AScanVisualDelivery.MinimumPublicationInterval); Control.RefreshSnapshot();
            new RenderTargetBitmap(Math.Max(1, (int)Control.ActualWidth), Math.Max(1, (int)Control.ActualHeight),
                dpi, dpi, PixelFormats.Pbgra32).Render(Control);
        }
        private static GateHandleVisualState Handle(string gate, GateHandleKind kind, double x, double value,
            GateHandleLogicalIdentity focused, bool enabled) => new(new(gate), new(1), kind, x, 100, value,
                kind == GateHandleKind.Threshold ? "normalized" : "s", 0, 1, .01, true,
                focused == new GateHandleLogicalIdentity(new(gate), new(1), kind), enabled);
        public void Dispose() => Host.Close();
    }

    private sealed class ModifierState { internal ModifierKeys Value { get; set; } }
    private sealed class PointerState { internal Point Value { get; set; } }
}
