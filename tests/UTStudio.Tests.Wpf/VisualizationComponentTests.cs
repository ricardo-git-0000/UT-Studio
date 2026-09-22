using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Input;
using System.Windows.Threading;
using System.Xml.Linq;
using UTStudio.App.Wpf;
using UTStudio.Domain.Acquisition;
using UTStudio.Visualization.Core;
using UTStudio.Visualization.Wpf.Controls;

namespace UTStudio.Tests.Wpf;

[TestClass]
public sealed class VisualizationComponentTests
{
    [TestMethod]
    public Task ControlCanRenderOutsideMainWindow() => StaTest.Run(() =>
    {
        var configuration = new ConventionalAcquisitionConfiguration(new PhysicalChannelId(0), 4, 50_000_000);
        var metadata = new ConventionalUtFrameMetadata(new UtSourceId("standalone"),
            new AcquisitionRunId(Guid.NewGuid()), configuration, DateTimeOffset.UnixEpoch);
        short[] samples = [short.MinValue, 0, short.MaxValue, 0];
        var snapshot = new AScanProjector(4).Project(metadata, 1, TimeSpan.Zero, samples);
        var control = new AScanControl { Snapshot = snapshot };
        VisualTreeHelper.SetRootDpi(control, new DpiScale(1.5, 1.5));
        Assert.AreEqual(1.5, VisualTreeHelper.GetDpi(control).PixelsPerDip);
        control.Measure(new Size(640, 300));
        control.Arrange(new Rect(0, 0, 640, 300));
        var bitmap = new RenderTargetBitmap(640, 300, 144, 144, PixelFormats.Pbgra32);
        bitmap.Render(control);
        Assert.AreEqual(640, bitmap.PixelWidth);
        Assert.AreEqual(300, bitmap.PixelHeight);
        Assert.AreEqual(0, VisualTreeHelper.GetChildrenCount(control));
        Assert.AreEqual("UTStudio.Visualization.Wpf", typeof(AScanControl).Assembly.GetName().Name);
        Assert.IsNull(typeof(MainWindow).Assembly.GetType("UTStudio.App.Wpf.Controls.AScanControl"));
        return Task.CompletedTask;
    });

    [TestMethod]
    public Task LoadUnloadStopsRefreshAndReleasesDisplayedSnapshot() => StaTest.Run(async () =>
    {
        var clock = new ManualClock();
        var control = new AScanControl(clock) { Snapshot = Snapshot() };
        control.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
        control.Measure(new Size(640, 300));
        control.Arrange(new Rect(0, 0, 640, 300));
        new RenderTargetBitmap(640, 300, 96, 96, PixelFormats.Pbgra32).Render(control);
        Assert.IsTrue(control.IsRefreshTimerEnabled);
        Assert.IsTrue(control.HasDisplayedSnapshot);

        control.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));
        Assert.IsFalse(control.IsRefreshTimerEnabled);
        Assert.IsFalse(control.HasDisplayedSnapshot);

        clock.Advance(AScanVisualDelivery.MinimumPublicationInterval);
        control.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
        control.RefreshSnapshot();
        await Dispatcher.Yield(DispatcherPriority.Render);
        new RenderTargetBitmap(640, 300, 96, 96, PixelFormats.Pbgra32).Render(control);
        Assert.IsTrue(control.IsRefreshTimerEnabled);
        Assert.IsTrue(control.HasDisplayedSnapshot);
        control.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));
    });

    [TestMethod]
    public void ProjectReferencesRespectVisualizationBoundary()
    {
        string root = FindRepositoryRoot();
        string visualization = Path.Combine(root, "src", "UTStudio.Visualization.Wpf", "UTStudio.Visualization.Wpf.csproj");
        string[] references = ProjectReferences(visualization);
        CollectionAssert.AreEqual(new[] { "../UTStudio.Visualization.Core/UTStudio.Visualization.Core.csproj" }, references);
        Assert.IsFalse(references.Any(reference => reference.Contains("Application", StringComparison.Ordinal)));
        Assert.IsFalse(references.Any(reference => reference.Contains("Presentation", StringComparison.Ordinal)));

        string[] neutralProjects = Directory.GetFiles(Path.Combine(root, "src"), "*.csproj", SearchOption.AllDirectories)
            .Where(path => !Path.GetFileNameWithoutExtension(path).EndsWith(".Wpf", StringComparison.Ordinal))
            .ToArray();
        foreach (string project in neutralProjects)
        {
            var document = XDocument.Load(project);
            Assert.IsFalse(document.Descendants("UseWPF").Any(node =>
                string.Equals(node.Value.Trim(), "true", StringComparison.OrdinalIgnoreCase)), project);
            Assert.IsFalse(document.Descendants("TargetFramework").Any(node =>
                node.Value.Contains("-windows", StringComparison.OrdinalIgnoreCase)), project);
            Assert.IsFalse(document.Descendants("ProjectReference").Any(node =>
                (node.Attribute("Include")?.Value ?? string.Empty).Contains(".Wpf", StringComparison.OrdinalIgnoreCase)), project);
            string[] wpfAssemblies = ["PresentationCore", "PresentationFramework", "WindowsBase", "System.Xaml"];
            Assert.IsFalse(document.Descendants("Reference").Any(node => wpfAssemblies.Contains(
                node.Attribute("Include")?.Value ?? string.Empty, StringComparer.OrdinalIgnoreCase)), project);
        }
    }

    [TestMethod]
    public Task CursorHitTestingResizeDpiVisibilityAndDragAreDeterministic() => StaTest.Run(() =>
    {
        var snapshot = Snapshot();
        var cursors = AScanCursorMeasurements.Create(snapshot);
        var control = new AScanControl { Snapshot = snapshot, CursorState = cursors };
        var host = new Window { Content = control, Width = 640, Height = 300, WindowStyle = WindowStyle.None, ShowInTaskbar = false };
        try
        {
            VisualTreeHelper.SetRootDpi(control, new DpiScale(1.5, 1.5));
            host.Show();
            host.UpdateLayout();
            new RenderTargetBitmap((int)control.ActualWidth, (int)control.ActualHeight, 144, 144, PixelFormats.Pbgra32).Render(control);
            double x = 64 + (control.ActualWidth - 84) * .25;
            Assert.AreEqual(AScanCursorId.A, control.HitTestCursor(new Point(x + 7, 120)));
            Assert.IsNull(control.HitTestCursor(new Point(x + 9, 120)));

            AScanCursorMoveRequestedEventArgs? requested = null;
            control.CursorMoveRequested += (_, args) => requested = args;
            Assert.IsTrue(control.BeginCursorDrag(new Point(x, 120)));
            Assert.IsTrue(control.IsMouseCaptured);
            Assert.AreEqual(AScanCursorId.A, control.DraggingCursor);
            Assert.IsFalse(control.BeginPan(new Point(300, 120)));
            control.ContinueCursorDrag(new Point(10_000, 120));
            Assert.AreEqual(snapshot.MaximumTimeSeconds, requested!.TimeSeconds);
            control.EndCursorDrag();
            Assert.IsFalse(control.IsMouseCaptured);
            Assert.IsNull(control.DraggingCursor);

            control.CursorState = cursors with { IsVisible = false };
            Assert.IsNull(control.HitTestCursor(new Point(x, 120)));
            control.CursorState = null;
            Assert.IsNull(control.DisplayedCursorSnapshotVersion);
            control.CursorState = cursors;
            host.Width = 840;
            host.Height = 400;
            host.UpdateLayout();
            new RenderTargetBitmap((int)control.ActualWidth, (int)control.ActualHeight, 144, 144, PixelFormats.Pbgra32).Render(control);
            double resizedX = 64 + (control.ActualWidth - 84) * .25;
            Assert.AreEqual(AScanCursorId.A, control.HitTestCursor(new Point(resizedX, 120)));
        }
        finally { host.Close(); }
        return Task.CompletedTask;
    });

    [TestMethod]
    public Task LostMouseCaptureCancelsDragWithoutFurtherUpdates() => StaTest.Run(() =>
    {
        var snapshot = Snapshot();
        var control = new AScanControl { Snapshot = snapshot, CursorState = AScanCursorMeasurements.Create(snapshot) };
        var host = new Window { Content = control, Width = 640, Height = 300, WindowStyle = WindowStyle.None, ShowInTaskbar = false };
        try
        {
            host.Show();
            host.UpdateLayout();
            int requests = 0;
            control.CursorMoveRequested += (_, _) => requests++;
            double x = 64 + (control.ActualWidth - 84) * .25;
            Assert.IsTrue(control.BeginCursorDrag(new Point(x, 120)));
            Assert.IsTrue(control.IsMouseCaptured);
            Mouse.Capture(null);
            Assert.IsNull(control.DraggingCursor);
            Assert.IsFalse(control.IsMouseCaptured);
            int before = requests;
            control.ContinueCursorDrag(new Point(200, 120));
            Assert.AreEqual(before, requests);
        }
        finally { host.Close(); }
        return Task.CompletedTask;
    });

    [TestMethod]
    public Task NarrowResizeAndUnloadDuringDragRemainSafe() => StaTest.Run(async () =>
    {
        var snapshot = Snapshot();
        var control = new AScanControl { Snapshot = snapshot, CursorState = AScanCursorMeasurements.Create(snapshot) };
        var host = new Window { Content = control, Width = 120, Height = 180, WindowStyle = WindowStyle.None, ShowInTaskbar = false };
        try
        {
            host.Show();
            host.UpdateLayout();
            var narrow = new RenderTargetBitmap((int)control.ActualWidth, (int)control.ActualHeight, 96, 96, PixelFormats.Pbgra32);
            narrow.Render(control);
            double x = 64 + (control.ActualWidth - 84) * .25;
            Assert.IsTrue(control.BeginCursorDrag(new Point(x, 80)));
            Assert.IsTrue(control.IsMouseCaptured);
            host.Content = null;
            host.UpdateLayout();
            await Dispatcher.Yield(DispatcherPriority.Loaded);
            Assert.IsNull(control.DraggingCursor);
            Assert.IsFalse(control.IsMouseCaptured);
        }
        finally { host.Close(); }
    });

    [TestMethod]
    public Task CursorMeasurementsArePairedWithDisplayedSnapshotVersion() => StaTest.Run(async () =>
    {
        var clock = new ManualClock();
        var first = Snapshot(version: 1);
        var control = new AScanControl(clock) { Snapshot = first, CursorState = AScanCursorMeasurements.Create(first) };
        control.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
        control.Measure(new Size(640, 300));
        control.Arrange(new Rect(0, 0, 640, 300));
        new RenderTargetBitmap(640, 300, 96, 96, PixelFormats.Pbgra32).Render(control);
        Assert.AreEqual(1UL, control.DisplayedCursorSnapshotVersion);

        var second = Snapshot(version: 2);
        control.CursorState = AScanCursorMeasurements.Create(second);
        Assert.AreEqual(1UL, control.DisplayedCursorSnapshotVersion);
        control.Snapshot = second;
        clock.Advance(AScanVisualDelivery.MinimumPublicationInterval);
        control.RefreshSnapshot();
        await Dispatcher.Yield(DispatcherPriority.Render);
        new RenderTargetBitmap(640, 300, 96, 96, PixelFormats.Pbgra32).Render(control);
        Assert.AreEqual(2UL, control.DisplayedCursorSnapshotVersion);
        control.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));
    });

    [TestMethod]
    public Task SameRunWithDifferentVersionDoesNotReplaceDisplayedCursorMeasurements() => StaTest.Run(() =>
    {
        var run = new AcquisitionRunId(Guid.Parse("11111111-1111-1111-1111-111111111111"));
        var visible = Snapshot(version: 7, runId: run, samples: [short.MinValue, 0, short.MaxValue, 0]);
        var control = new AScanControl { Snapshot = visible, CursorState = AScanCursorMeasurements.Create(visible) };
        control.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
        control.Measure(new Size(640, 300));
        control.Arrange(new Rect(0, 0, 640, 300));
        new RenderTargetBitmap(640, 300, 96, 96, PixelFormats.Pbgra32).Render(control);
        double originalAX = 64 + (640 - 84) * .25;

        var next = Snapshot(version: 8, runId: run, samples: [short.MaxValue, 0, short.MinValue, 0]);
        double requestedTime = visible.Points[2].TimeSeconds;
        var moved = AScanCursorMeasurements.Move(control.CursorState!, visible, AScanCursorId.A, requestedTime);
        var reconciled = AScanCursorMeasurements.Reconcile(moved, next);
        Assert.AreEqual(run, reconciled.RunId);
        Assert.AreEqual(8UL, reconciled.SnapshotVersion);
        Assert.AreEqual(requestedTime, reconciled.A.TimeSeconds);
        Assert.AreEqual(next.Points[2].AmplitudePercent, reconciled.A.AmplitudePercent);

        control.CursorState = reconciled;
        Assert.AreEqual(7UL, control.DisplayedCursorSnapshotVersion);
        Assert.AreEqual(AScanCursorId.A, control.HitTestCursor(new Point(originalAX, 120)));
        Assert.IsNull(control.HitTestCursor(new Point(64 + (640 - 84) * 2d / 3, 120)));
        control.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));
        return Task.CompletedTask;
    });

    [TestMethod]
    public Task DifferentRunWithSameVersionDoesNotReplaceDisplayedCursorMeasurements() => StaTest.Run(() =>
    {
        var visibleRun = new AcquisitionRunId(Guid.Parse("22222222-2222-2222-2222-222222222222"));
        var otherRun = new AcquisitionRunId(Guid.Parse("33333333-3333-3333-3333-333333333333"));
        const ulong version = 9;
        var visible = Snapshot(version, visibleRun, [short.MinValue, 0, short.MaxValue, 0]);
        var control = new AScanControl { Snapshot = visible, CursorState = AScanCursorMeasurements.Create(visible) };
        control.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
        control.Measure(new Size(640, 300));
        control.Arrange(new Rect(0, 0, 640, 300));
        new RenderTargetBitmap(640, 300, 96, 96, PixelFormats.Pbgra32).Render(control);
        double originalAX = 64 + (640 - 84) * .25;

        var other = Snapshot(version, otherRun, [short.MaxValue, 0, short.MinValue, 0]);
        double requestedTime = visible.Points[2].TimeSeconds;
        var moved = AScanCursorMeasurements.Move(control.CursorState!, visible, AScanCursorId.A, requestedTime);
        var reconciled = AScanCursorMeasurements.Reconcile(moved, other);
        Assert.AreEqual(otherRun, reconciled.RunId);
        Assert.AreEqual(version, reconciled.SnapshotVersion);
        Assert.AreEqual(requestedTime, reconciled.A.TimeSeconds);
        Assert.AreEqual(other.Points[2].AmplitudePercent, reconciled.A.AmplitudePercent);

        control.CursorState = reconciled;
        Assert.AreEqual(version, control.DisplayedCursorSnapshotVersion);
        Assert.AreEqual(AScanCursorId.A, control.HitTestCursor(new Point(originalAX, 120)));
        double foreignAX = 64 + (640 - 84) * 2d / 3;
        Assert.IsNull(control.HitTestCursor(new Point(foreignAX, 120)));
        control.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));
        return Task.CompletedTask;
    });

    [TestMethod]
    public Task TimeZoomAndPanPublishPhysicalIntentionsWithSafeCapture() => StaTest.Run(() =>
    {
        var snapshot = Snapshot();
        var viewport = ScanTimeViewportOperations.Create(snapshot.MinimumTimeSeconds, snapshot.MaximumTimeSeconds);
        var control = new AScanControl { Snapshot = snapshot, TimeViewport = viewport };
        var host = new Window { Content = control, Width = 640, Height = 300, WindowStyle = WindowStyle.None, ShowInTaskbar = false };
        try
        {
            host.Show();
            host.UpdateLayout();
            ScanTimeZoomRequestedEventArgs? zoom = null;
            ScanTimePanRequestedEventArgs? pan = null;
            control.TimeZoomRequested += (_, args) => zoom = args;
            control.TimePanRequested += (_, args) => pan = args;
            var center = new Point(64 + (control.ActualWidth - 84) / 2, 120);
            Assert.IsTrue(control.RequestTimeZoom(center, 120));
            Assert.AreEqual((snapshot.MinimumTimeSeconds + snapshot.MaximumTimeSeconds) / 2,
                zoom!.AnchorSeconds, 1e-15);
            Assert.IsGreaterThan(1, zoom.Factor);

            Assert.IsTrue(control.BeginPan(center));
            Assert.IsTrue(control.IsMouseCaptured);
            control.ContinuePan(new Point(center.X + 100, center.Y));
            Assert.IsNotNull(pan);
            Assert.IsLessThan(0, pan.DeltaSeconds);
            control.EndInteraction();
            Assert.IsFalse(control.IsMouseCaptured);
            Assert.IsFalse(control.RequestTimeZoom(new Point(0, 0), 120));
        }
        finally { host.Close(); }
        return Task.CompletedTask;
    });

    [TestMethod]
    public Task ViewportBindingMatchesDisplayedRunAndVersionAndResetAvoidsCursors() => StaTest.Run(async () =>
    {
        var clock = new ManualClock();
        var run = new AcquisitionRunId(Guid.Parse("44444444-4444-4444-4444-444444444444"));
        var snapshot = Snapshot(5, run);
        var full = ScanTimeViewportOperations.Create(snapshot.MinimumTimeSeconds, snapshot.MaximumTimeSeconds);
        var zoomed = ScanTimeViewportOperations.Zoom(full,
            (full.DomainMinimumSeconds + full.DomainMaximumSeconds) / 2, 2);
        var control = new AScanControl(clock)
        {
            Snapshot = snapshot,
            CursorState = AScanCursorMeasurements.Create(snapshot),
            ViewportBinding = new(run, 5, zoomed)
        };
        control.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
        control.Measure(new Size(640, 300));
        control.Arrange(new Rect(0, 0, 640, 300));
        new RenderTargetBitmap(640, 300, 96, 96, PixelFormats.Pbgra32).Render(control);

        int resets = 0;
        control.TimeResetRequested += (_, _) => resets++;
        Assert.IsTrue(control.RequestTimeReset(new Point(500, 120)));
        Assert.AreEqual(1, resets);
        double cursorX = 64;
        Assert.IsFalse(control.RequestTimeReset(new Point(cursorX, 120)));

        var future = ScanTimeViewportOperations.Zoom(full, full.DomainMaximumSeconds, 2);
        control.ViewportBinding = new(run, 6, future);
        ScanTimeZoomRequestedEventArgs? request = null;
        control.TimeZoomRequested += (_, args) => request = args;
        Assert.IsTrue(control.RequestTimeZoom(new Point(64 + (640 - 84) / 2d, 120), 120));
        Assert.AreEqual((zoomed.VisibleMinimumSeconds + zoomed.VisibleMaximumSeconds) / 2,
            request!.AnchorSeconds, 1e-15);
        control.Snapshot = Snapshot(6, run);
        clock.Advance(AScanVisualDelivery.MinimumPublicationInterval + TimeSpan.FromTicks(1));
        control.RefreshSnapshot();
        await Dispatcher.Yield(DispatcherPriority.Render);
        new RenderTargetBitmap(640, 300, 96, 96, PixelFormats.Pbgra32).Render(control);
        request = null;
        Assert.IsTrue(control.RequestTimeZoom(new Point(64 + (640 - 84) / 2d, 120), 120));
        Assert.AreEqual((future.VisibleMinimumSeconds + future.VisibleMaximumSeconds) / 2,
            request!.AnchorSeconds, 1e-15);
        control.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));
    });

    [TestMethod]
    public Task DirectTimeViewportTracksEverySuccessiveValueAcrossLoadCycle() => StaTest.Run(async () =>
    {
        var snapshot = Snapshot();
        var clock = new ManualClock();
        var full = ScanTimeViewportOperations.Create(snapshot.MinimumTimeSeconds, snapshot.MaximumTimeSeconds);
        var beforeLoad = ScanTimeViewportOperations.Zoom(full, full.DomainMinimumSeconds, 2);
        var control = new AScanControl(clock) { Snapshot = snapshot, TimeViewport = beforeLoad };
        var host = new Window { Content = control, Width = 640, Height = 300, WindowStyle = WindowStyle.None, ShowInTaskbar = false };
        try
        {
            host.Show();
            host.UpdateLayout();
            Render(control);
            AssertAnchor(beforeLoad);

            ScanTimeViewport[] successive =
            [
                ScanTimeViewportOperations.Zoom(full, full.DomainMinimumSeconds, 4),
                ScanTimeViewportOperations.Zoom(full, full.DomainMaximumSeconds, 3),
                ScanTimeViewportOperations.Zoom(full, (full.DomainMinimumSeconds + full.DomainMaximumSeconds) / 2, 8)
            ];
            foreach (var viewport in successive)
            {
                control.TimeViewport = viewport;
                Render(control);
                AssertAnchor(viewport);
            }

            host.Content = null;
            host.UpdateLayout();
            await Dispatcher.Yield(DispatcherPriority.Loaded);
            var afterUnload = ScanTimeViewportOperations.Zoom(full, full.DomainMaximumSeconds, 6);
            control.TimeViewport = afterUnload;
            clock.Advance(AScanVisualDelivery.MinimumPublicationInterval);
            host.Content = control;
            host.UpdateLayout();
            Render(control);
            AssertAnchor(afterUnload);
        }
        finally { host.Close(); }

        void AssertAnchor(ScanTimeViewport viewport)
        {
            ScanTimeZoomRequestedEventArgs? request = null;
            EventHandler<ScanTimeZoomRequestedEventArgs> handler = (_, args) => request = args;
            control.TimeZoomRequested += handler;
            double x = 64 + (control.ActualWidth - 84) * .25;
            Assert.IsTrue(control.RequestTimeZoom(new Point(x, 120), 120));
            control.TimeZoomRequested -= handler;
            Assert.AreEqual(viewport.VisibleMinimumSeconds + viewport.VisibleSpanSeconds * .25,
                request!.AnchorSeconds, 1e-15);
        }

        static void Render(AScanControl target) => new RenderTargetBitmap(
            (int)target.ActualWidth, (int)target.ActualHeight, 96, 96, PixelFormats.Pbgra32).Render(target);
    });

    [TestMethod]
    public Task ViewportBindingEffectiveSourceSelectsDirectOrCoordinatedMode() => StaTest.Run(async () =>
    {
        var snapshot = Snapshot();
        var full = ScanTimeViewportOperations.Create(snapshot.MinimumTimeSeconds, snapshot.MaximumTimeSeconds);
        var direct = ScanTimeViewportOperations.Zoom(full, full.DomainMinimumSeconds, 4);
        var coordinated = ScanTimeViewportOperations.Zoom(full, full.DomainMaximumSeconds, 4);
        var binding = new ScanTimeViewportBinding(snapshot.Metadata.RunId, snapshot.Version, coordinated);
        var control = new AScanControl { Snapshot = snapshot, TimeViewport = direct };
        var host = new Window { Content = control, Width = 640, Height = 300, WindowStyle = WindowStyle.None, ShowInTaskbar = false };
        try
        {
            host.Show();
            host.UpdateLayout();
            Render();
            AssertViewport(direct);

            control.ViewportBinding = null;
            AssertViewport(full);
            control.ClearValue(AScanControl.ViewportBindingProperty);
            AssertViewport(direct);

            control.ViewportBinding = binding;
            AssertViewport(coordinated);
            control.ClearValue(AScanControl.ViewportBindingProperty);

            var localSource = new ViewportBindingSource();
            BindingOperations.SetBinding(control, AScanControl.ViewportBindingProperty,
                new Binding(nameof(ViewportBindingSource.Value)) { Source = localSource });
            AssertViewport(full);
            localSource.Value = binding;
            await Dispatcher.Yield(DispatcherPriority.DataBind);
            AssertViewport(coordinated);
            localSource.Value = null;
            await Dispatcher.Yield(DispatcherPriority.DataBind);
            AssertViewport(full);
            BindingOperations.ClearBinding(control, AScanControl.ViewportBindingProperty);
            AssertViewport(direct);

            var literalStyle = new Style(typeof(AScanControl));
            literalStyle.Setters.Add(new Setter(AScanControl.ViewportBindingProperty, binding));
            control.Style = literalStyle;
            AssertViewport(coordinated);

            var styleSource = new ViewportBindingSource();
            var bindingStyle = new Style(typeof(AScanControl));
            bindingStyle.Setters.Add(new Setter(AScanControl.ViewportBindingProperty,
                new Binding(nameof(ViewportBindingSource.Value)) { Source = styleSource }));
            control.Style = bindingStyle;
            AssertViewport(full);
            styleSource.Value = binding;
            await Dispatcher.Yield(DispatcherPriority.DataBind);
            AssertViewport(coordinated);
            styleSource.Value = null;
            await Dispatcher.Yield(DispatcherPriority.DataBind);
            AssertViewport(full);

            var nullStyle = new Style(typeof(AScanControl));
            nullStyle.Setters.Add(new Setter(AScanControl.ViewportBindingProperty, null));
            control.Style = nullStyle;
            AssertViewport(full);
            control.Style = null;
            AssertViewport(direct);

            var incompatibleStyle = new Style(typeof(AScanControl));
            incompatibleStyle.Setters.Add(new Setter(AScanControl.ViewportBindingProperty,
                new ScanTimeViewportBinding(snapshot.Metadata.RunId, snapshot.Version + 1, coordinated)));
            control.Style = incompatibleStyle;
            AssertViewport(full);
        }
        finally { host.Close(); }

        void AssertViewport(ScanTimeViewport expected)
        {
            Render();
            ScanTimeZoomRequestedEventArgs? request = null;
            EventHandler<ScanTimeZoomRequestedEventArgs> handler = (_, args) => request = args;
            control.TimeZoomRequested += handler;
            Assert.IsTrue(control.RequestTimeZoom(new Point(64 + (control.ActualWidth - 84) * .25, 120), 120));
            control.TimeZoomRequested -= handler;
            Assert.AreEqual(expected.VisibleMinimumSeconds + expected.VisibleSpanSeconds * .25,
                request!.AnchorSeconds, 1e-15);
        }

        void Render() => new RenderTargetBitmap((int)control.ActualWidth, (int)control.ActualHeight,
            96, 96, PixelFormats.Pbgra32).Render(control);
    });

    [TestMethod]
    public Task RoutedMouseEventsHonorModifiersResetPanCursorPriorityAndCaptureLoss() => StaTest.Run(() =>
    {
        Point position = new(300, 120);
        ModifierKeys modifiers = ModifierKeys.None;
        int clickCount = 1;
        var snapshot = Snapshot();
        var full = ScanTimeViewportOperations.Create(snapshot.MinimumTimeSeconds, snapshot.MaximumTimeSeconds);
        var control = new AScanControl(TimeProvider.System, () => modifiers, _ => position, _ => clickCount)
        { Snapshot = snapshot, CursorState = AScanCursorMeasurements.Create(snapshot), TimeViewport = full };
        var host = new Window { Content = control, Width = 640, Height = 300, WindowStyle = WindowStyle.None, ShowInTaskbar = false };
        try
        {
            host.Show();
            host.UpdateLayout();
            new RenderTargetBitmap((int)control.ActualWidth, (int)control.ActualHeight, 96, 96,
                PixelFormats.Pbgra32).Render(control);
            int zooms = 0, pans = 0, resets = 0;
            control.TimeZoomRequested += (_, args) =>
            { zooms++; control.TimeViewport = ScanTimeViewportOperations.Zoom(control.TimeViewport!, args.AnchorSeconds, args.Factor); };
            control.TimePanRequested += (_, args) =>
            { pans++; control.TimeViewport = ScanTimeViewportOperations.Pan(control.TimeViewport!, args.DeltaSeconds); };
            control.TimeResetRequested += (_, _) =>
            { resets++; control.TimeViewport = ScanTimeViewportOperations.Reset(control.TimeViewport!); };

            var plainWheel = Wheel(120);
            control.RaiseEvent(plainWheel);
            Assert.IsTrue(plainWheel.Handled);
            Assert.AreEqual(1, zooms);
            Assert.IsGreaterThan(1, control.TimeViewport!.ZoomFactor);

            modifiers = ModifierKeys.Control;
            var controlWheel = Wheel(120);
            control.RaiseEvent(controlWheel);
            Assert.IsFalse(controlWheel.Handled);
            modifiers = ModifierKeys.Shift;
            var shiftWheel = Wheel(120);
            control.RaiseEvent(shiftWheel);
            Assert.IsFalse(shiftWheel.Handled);
            Assert.AreEqual(1, zooms);

            modifiers = ModifierKeys.None;
            clickCount = 2;
            position = new(340, 120);
            var doubleClick = Left(UIElement.MouseLeftButtonDownEvent);
            control.RaiseEvent(doubleClick);
            Assert.IsTrue(doubleClick.Handled);
            Assert.AreEqual(1, resets);
            Assert.IsTrue(control.TimeViewport!.IsReset);

            control.TimeViewport = ScanTimeViewportOperations.Zoom(full,
                (full.DomainMinimumSeconds + full.DomainMaximumSeconds) / 2, 2);
            modifiers = ModifierKeys.Shift;
            clickCount = 1;
            position = new(350, 120);
            control.RaiseEvent(Left(UIElement.MouseLeftButtonDownEvent));
            Assert.IsTrue(control.IsMouseCaptured);
            position = new(410, 120);
            control.RaiseEvent(Move());
            Assert.AreEqual(1, pans);
            control.RaiseEvent(Left(Mouse.MouseUpEvent));
            Assert.IsFalse(control.IsMouseCaptured);

            control.TimeViewport = full;
            position = new(64 + (control.ActualWidth - 84) * .25, 120);
            int pansBeforeCursor = pans;
            control.RaiseEvent(Left(UIElement.MouseLeftButtonDownEvent));
            Assert.AreEqual(AScanCursorId.A, control.DraggingCursor);
            Assert.AreEqual(pansBeforeCursor, pans);
            Mouse.Capture(null);
            Assert.IsNull(control.DraggingCursor);
            Assert.IsFalse(control.IsMouseCaptured);
            position = new(500, 120);
            control.RaiseEvent(Move());
            Assert.AreEqual(pansBeforeCursor, pans);

            control.CursorState = control.CursorState! with { IsVisible = false };
            control.TimeViewport = ScanTimeViewportOperations.Zoom(full,
                (full.DomainMinimumSeconds + full.DomainMaximumSeconds) / 2, 2);
            position = new(350, 120);
            control.RaiseEvent(Left(UIElement.MouseLeftButtonDownEvent));
            Assert.IsTrue(control.IsMouseCaptured);
            Mouse.Capture(null);
            int pansBeforeLostCaptureMove = pans;
            position = new(450, 120);
            control.RaiseEvent(Move());
            Assert.AreEqual(pansBeforeLostCaptureMove, pans);
        }
        finally { host.Close(); }
        return Task.CompletedTask;

        static MouseWheelEventArgs Wheel(int delta)
        {
            var args = new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, delta);
            args.RoutedEvent = Mouse.MouseWheelEvent;
            return args;
        }

        static MouseButtonEventArgs Left(RoutedEvent routedEvent)
        {
            var args = new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left);
            args.RoutedEvent = routedEvent;
            return args;
        }

        static MouseEventArgs Move()
        {
            var args = new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount);
            args.RoutedEvent = Mouse.MouseMoveEvent;
            return args;
        }
    });

    private sealed class ViewportBindingSource : INotifyPropertyChanged
    {
        private ScanTimeViewportBinding? _value;
        public ScanTimeViewportBinding? Value
        {
            get => _value;
            set
            {
                if (_value == value) { return; }
                _value = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Value)));
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
    }

    private static AScanSnapshot Snapshot(ulong version = 0, AcquisitionRunId? runId = null, short[]? samples = null)
    {
        var configuration = new ConventionalAcquisitionConfiguration(new PhysicalChannelId(0), 4, 50_000_000);
        var metadata = new ConventionalUtFrameMetadata(new UtSourceId("standalone"),
            runId ?? new AcquisitionRunId(Guid.NewGuid()), configuration, DateTimeOffset.UnixEpoch);
        return new AScanProjector(4).Project(metadata, 1, TimeSpan.Zero,
            samples ?? [short.MinValue, 0, short.MaxValue, 0], version);
    }

    private static string[] ProjectReferences(string path) => XDocument.Load(path)
        .Descendants("ProjectReference")
        .Select(node => node.Attribute("Include")?.Value.Replace('\\', '/') ?? string.Empty)
        .Where(value => value.Length > 0)
        .ToArray();

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "UTStudio.sln")))
        { directory = directory.Parent; }
        return directory?.FullName ?? throw new InvalidOperationException("Repository root was not found.");
    }
}
