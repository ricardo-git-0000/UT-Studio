using System.Windows;
using System.Windows.Input;
using UTStudio.Contracts.Inspection;
using UTStudio.Domain.Acquisition;
using UTStudio.Domain.Inspection;
using UTStudio.Visualization.Core;
using UTStudio.Visualization.Wpf.Controls;

namespace UTStudio.Tests.Wpf;

[TestClass]
public sealed class BScanInteractionTests
{
    [TestMethod]
    public Task Routed_wheel_click_and_double_click_emit_exact_intentions() => StaTest.Run(() =>
    {
        Point point = new(200, 120); ModifierKeys modifiers = ModifierKeys.None; int clicks = 1;
        var control = Control(() => modifiers, _ => point, _ => clicks);
        using var host = new TestWindow(control);
        int timeZoom = 0, spatialZoom = 0, selected = 0, resets = 0;
        BScanTimeZoomRequestedEventArgs? time = null;
        control.TimeZoomRequested += (_, e) => { timeZoom++; time = e; };
        control.SpatialZoomRequested += (_, _) => spatialZoom++;
        control.ColumnRequested += (_, _) => selected++;
        control.ResetAllRequested += (_, _) => resets++;

        var wheel = Wheel(120); control.RaiseEvent(wheel);
        Assert.IsTrue(wheel.Handled); Assert.AreEqual(1, timeZoom);
        double expectedRatio = (point.Y - 16) / (control.ActualHeight - 58);
        Assert.AreEqual(expectedRatio, time!.AnchorSeconds, 1e-12);
        modifiers = ModifierKeys.Control;
        wheel = Wheel(120); control.RaiseEvent(wheel);
        Assert.IsTrue(wheel.Handled); Assert.AreEqual(1, spatialZoom);

        modifiers = ModifierKeys.None; point = new(300, 120); clicks = 1;
        control.RaiseEvent(Button(MouseButton.Left, UIElement.MouseLeftButtonDownEvent));
        Assert.AreEqual(1, selected);
        point = new(70, 120); clicks = 1;
        control.RaiseEvent(Button(MouseButton.Left, UIElement.MouseLeftButtonDownEvent));
        Assert.AreEqual(2, selected);
        clicks = 2;
        control.RaiseEvent(Button(MouseButton.Left, UIElement.MouseLeftButtonDownEvent));
        Assert.AreEqual(1, resets); Assert.AreEqual(2, selected);
        control.RaiseEvent(KeyEvent(control, Key.Home));
        Assert.AreEqual(2, resets);
        point = new(2, 2); clicks = 1;
        control.RaiseEvent(Wheel(120));
        control.RaiseEvent(Button(MouseButton.Left, UIElement.MouseLeftButtonDownEvent));
        Assert.AreEqual(1, timeZoom); Assert.AreEqual(2, selected);
        return Task.CompletedTask;
    });

    [TestMethod]
    public Task Incremental_snapshot_allows_selection_from_the_complete_resident_range() => StaTest.Run(() =>
    {
        Point point = default;
        var control = Control(() => ModifierKeys.None, _ => point, _ => 1);
        using var host = new TestWindow(control);
        point = new(64 + (control.ActualWidth - 84) * (1.5d / 11d), 100);
        ulong? selected = null;
        control.ColumnRequested += (_, args) => selected = args.AcquisitionIndex;

        control.RaiseEvent(Button(MouseButton.Left, UIElement.MouseLeftButtonDownEvent));

        Assert.AreEqual(1UL, selected);
        Assert.IsLessThan(control.Snapshot!.RepresentedRange.First, selected.GetValueOrDefault());
        return Task.CompletedTask;
    });

    [TestMethod]
    public Task Routed_middle_and_shift_pan_capture_release_and_selection_has_priority() => StaTest.Run(() =>
    {
        Point point = new(250, 120); ModifierKeys modifiers = ModifierKeys.None;
        var control = Control(() => modifiers, _ => point, _ => 1);
        control.SelectedAcquisitionIndex = 5;
        using var host = new TestWindow(control);
        int pans = 0, selections = 0;
        control.PanRequested += (_, _) => pans++;
        control.ColumnRequested += (_, _) => selections++;

        control.RaiseEvent(Button(MouseButton.Middle, Mouse.MouseDownEvent));
        Assert.IsTrue(control.IsMouseCaptured);
        point = new(300, 150); control.RaiseEvent(Move()); Assert.AreEqual(1, pans);
        control.RaiseEvent(Button(MouseButton.Middle, Mouse.MouseUpEvent)); Assert.IsFalse(control.IsMouseCaptured);
        point = new(250, 120); control.RaiseEvent(Button(MouseButton.Middle, Mouse.MouseDownEvent));
        Assert.IsTrue(control.IsMouseCaptured);
        control.RaiseEvent(KeyEvent(control, Key.Escape)); Assert.IsFalse(control.IsMouseCaptured);

        modifiers = ModifierKeys.Shift; point = new(200, 100);
        control.RaiseEvent(Button(MouseButton.Left, UIElement.MouseLeftButtonDownEvent));
        Assert.IsTrue(control.IsMouseCaptured);
        Mouse.Capture(null); int before = pans;
        point = new(400, 150); control.RaiseEvent(Move()); Assert.AreEqual(before, pans);

        double marker = 64 + (control.ActualWidth - 84) * (5.5d / 11d);
        point = new(marker, 100);
        Assert.IsTrue(control.HitSelection(point));
        control.RaiseEvent(Button(MouseButton.Left, UIElement.MouseLeftButtonDownEvent));
        Assert.AreEqual(1, selections); Assert.IsTrue(control.IsMouseCaptured);
        point = new(64 + (control.ActualWidth - 84) * (7d / 11d), 100);
        control.RaiseEvent(Move());
        Assert.AreEqual(2, selections);
        control.RaiseEvent(Button(MouseButton.Left, Mouse.MouseUpEvent));
        Assert.IsFalse(control.IsMouseCaptured);
        return Task.CompletedTask;
    });

    [TestMethod]
    public Task Live_viewport_shift_keeps_selection_drag_on_displayed_geometry() => StaTest.Run(() =>
    {
        Point point = default;
        var control = Control(() => ModifierKeys.None, _ => point, _ => 1);
        control.SelectedAcquisitionIndex = 5;
        using var host = new TestWindow(control);
        int selections = 0;
        control.ColumnRequested += (_, _) => selections++;
        point = new(64 + (control.ActualWidth - 84) * (5.5d / 11d), 100);

        control.RaiseEvent(Button(MouseButton.Left, UIElement.MouseLeftButtonDownEvent));
        Assert.IsTrue(control.IsMouseCaptured);
        Assert.AreEqual(1, selections);

        BScanVisualSnapshot shifted = control.Snapshot! with
        {
            SnapshotVersion = 2,
            SpatialViewport = new(1, 0, 11)
        };
        control.Snapshot = shifted;
        control.SpatialViewport = shifted.SpatialViewport;

        Assert.IsTrue(control.IsMouseCaptured);
        point = new(64 + (control.ActualWidth - 84) * (7d / 11d), 100);
        control.RaiseEvent(Move());
        Assert.AreEqual(2, selections);
        control.RaiseEvent(Button(MouseButton.Left, Mouse.MouseUpEvent));
        Assert.IsFalse(control.IsMouseCaptured);
        return Task.CompletedTask;
    });

    [TestMethod]
    public Task Unloaded_disables_all_routed_intentions_and_ends_capture() => StaTest.Run(() =>
    {
        Point point = new(220, 100); var control = Control(() => ModifierKeys.None, _ => point, _ => 1);
        using var host = new TestWindow(control);
        int calls = 0;
        control.TimeZoomRequested += (_, _) => calls++;
        control.ColumnRequested += (_, _) => calls++;
        control.RaiseEvent(Button(MouseButton.Middle, Mouse.MouseDownEvent));
        Assert.IsTrue(control.IsMouseCaptured);
        control.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));
        Assert.IsFalse(control.IsMouseCaptured);
        control.RaiseEvent(Wheel(120));
        control.RaiseEvent(Button(MouseButton.Left, UIElement.MouseLeftButtonDownEvent));
        Assert.AreEqual(0, calls);
        return Task.CompletedTask;
    });

    private static BScanControl Control(Func<ModifierKeys> modifiers, Func<IInputElement, Point> position,
        Func<MouseButtonEventArgs, int> clicks)
    {
        var range = new BScanAcquisitionRange(0, 10);
        return new BScanControl(modifiers, position, clicks)
        {
            SpatialViewport = BScanSpatialViewportOperations.Reset(range),
            ResidentRange = range,
            TimeViewport = ScanTimeViewportOperations.Create(0, 1),
            Snapshot = Snapshot()
        };
    }
    private static BScanVisualSnapshot Snapshot()
    {
        var run = new AcquisitionRunId(Guid.Parse("22222222-2222-2222-2222-222222222222"));
        var represented = new BScanAcquisitionRange(2, 8);
        var resident = new BScanAcquisitionRange(0, 10);
        var time = ScanTimeViewportOperations.Create(0, 1);
        return new(run, 1, 0, 1, 0, represented, resident, time,
            BScanSpatialViewportOperations.Reset(resident), time, 11, 10, 0,
            new(2, 8, 0, 1), new(0, 0, 11, 10), false, true, BScanFullRefreshReason.Initial)
        {
            Stream = new(new UtSourceId("events"), run, new PhysicalChannelId(0)),
            SelectableTraceIdentities = Array.AsReadOnly(Enumerable.Range(0, 11)
                .Select(index => (TraceIdentity?)new TraceIdentity(new(new InspectionId(
                    Guid.Parse("11111111-1111-1111-1111-111111111111")), run,
                    new UtSourceId("events"), new PhysicalChannelId(0)), (ulong)index)).ToArray())
        };
    }
    private static MouseWheelEventArgs Wheel(int delta) => new(Mouse.PrimaryDevice, Environment.TickCount, delta)
    { RoutedEvent = Mouse.MouseWheelEvent };
    private static MouseButtonEventArgs Button(MouseButton button, RoutedEvent routed) =>
        new(Mouse.PrimaryDevice, Environment.TickCount, button) { RoutedEvent = routed };
    private static MouseEventArgs Move() => new(Mouse.PrimaryDevice, Environment.TickCount)
    { RoutedEvent = Mouse.MouseMoveEvent };
    private static KeyEventArgs KeyEvent(BScanControl control, Key key) => new(Keyboard.PrimaryDevice,
        PresentationSource.FromVisual(control), Environment.TickCount, key)
    { RoutedEvent = Keyboard.KeyDownEvent };

    private sealed class TestWindow : IDisposable
    {
        private readonly Window _window;
        internal TestWindow(BScanControl control)
        { _window = new() { Content = control, Width = 640, Height = 300, ShowInTaskbar = false, WindowStyle = WindowStyle.None }; _window.Show(); _window.UpdateLayout(); }
        public void Dispose() => _window.Close();
    }
}
