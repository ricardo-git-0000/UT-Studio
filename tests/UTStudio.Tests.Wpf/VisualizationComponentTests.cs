using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Input;
using System.Windows.Threading;
using System.Xml.Linq;
using UTStudio.App.Wpf;
using UTStudio.Contracts.Inspection;
using UTStudio.Domain.Acquisition;
using UTStudio.ScanVisualization.AScan.Wpf;
using UTStudio.Visualization.Core;
using UTStudio.Visualization.Wpf.Controls;

namespace UTStudio.Tests.Wpf;

[TestClass]
public sealed class VisualizationComponentTests
{
    [TestMethod]
    public void WpfDoesNotReferenceAuthoritativeTraceOwnershipTypes()
    {
        Type[] forbidden = [typeof(IUtTraceProvider), typeof(IInspectionTraceLease), typeof(IReadOnlyUtSamples)];
        var violations = new List<string>();
        foreach (Assembly assembly in new[] { typeof(MainWindow).Assembly, typeof(AScanControl).Assembly, typeof(AScanView).Assembly })
            foreach (Type type in assembly.GetTypes())
            {
                foreach (FieldInfo field in type.GetFields(MemberFlags)) Add(field, field.FieldType);
                foreach (PropertyInfo property in type.GetProperties(MemberFlags)) Add(property, property.PropertyType);
                foreach (MethodInfo method in type.GetMethods(MemberFlags))
                { Add(method, method.ReturnType); foreach (ParameterInfo parameter in method.GetParameters()) Add(method, parameter.ParameterType); }
            }
        Assert.IsEmpty(violations, string.Join(Environment.NewLine, violations));
        void Add(MemberInfo member, Type candidate)
        { if (Contains(candidate)) violations.Add($"{member.DeclaringType?.FullName}.{member.Name} -> {candidate}"); }
        bool Contains(Type candidate) => candidate.IsByRef || candidate.IsPointer || candidate.IsArray
            ? Contains(candidate.GetElementType()!) : forbidden.Contains(candidate) || candidate.IsGenericType &&
                candidate.GetGenericArguments().Any(Contains);
    }
    private const BindingFlags MemberFlags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance |
        BindingFlags.Static | BindingFlags.DeclaredOnly;

    [TestMethod]
    public void AScanControlHasOneVisualInputDependencyProperty()
    {
        FieldInfo[] visual = typeof(AScanControl).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(field => field.FieldType == typeof(DependencyProperty)).ToArray();
        CollectionAssert.AreEqual(new[] { nameof(AScanControl.VisualSnapshotProperty) }, visual.Select(x => x.Name).ToArray());
    }

    [TestMethod]
    public Task ControlRendersFallbackWithoutAuthorityOutsideMainWindow() => StaTest.Run(() =>
    {
        AScanSnapshot signal = Snapshot();
        var control = new AScanControl();
        VisualTreeHelper.SetRootDpi(control, new DpiScale(1.5, 1.5));
        control.Measure(new(640, 300)); control.Arrange(new(0, 0, 640, 300));
        control.VisualSnapshot = Composite(control, signal, AScanCursorMeasurements.Create(signal));
        var bitmap = new RenderTargetBitmap(640, 300, 144, 144, PixelFormats.Pbgra32); bitmap.Render(control);
        Assert.AreEqual(640, bitmap.PixelWidth); Assert.IsTrue(control.HasDisplayedSnapshot);
        Assert.IsFalse(control.DisplayedVisualSnapshot!.HasAuthoritativeIdentity);
        Assert.AreEqual(GateOverlayGeometryStatus.NotRequested, control.DisplayedVisualSnapshot.Gates.Status);
        Assert.IsNull(control.HitTestGate(new(100, 100)));
        return Task.CompletedTask;
    });

    [TestMethod]
    public Task LoadUnloadStopsRefreshReleasesSnapshotAndIsReentrantSafe() => StaTest.Run(async () =>
    {
        var clock = new ManualClock(); AScanSnapshot signal = Snapshot();
        var control = new AScanControl(clock);
        control.Measure(new(640, 300)); control.Arrange(new(0, 0, 640, 300));
        control.VisualSnapshot = Composite(control, signal, AScanCursorMeasurements.Create(signal));
        control.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent)); Render(control);
        Assert.IsTrue(control.IsRefreshTimerEnabled); Assert.IsTrue(control.HasDisplayedSnapshot);
        control.VisualContextChanged += (_, _) => control.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));
        control.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));
        Assert.IsFalse(control.IsRefreshTimerEnabled); Assert.IsFalse(control.HasDisplayedSnapshot);
        clock.Advance(AScanVisualDelivery.MinimumPublicationInterval);
        control.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent)); control.RefreshSnapshot();
        await Dispatcher.Yield(DispatcherPriority.Render); Render(control);
        control.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));
    });

    [TestMethod]
    public Task CursorsPanZoomAndTransformInvalidationRemainDeterministic() => StaTest.Run(() =>
    {
        AScanSnapshot signal = Snapshot(); AScanCursorState cursors = AScanCursorMeasurements.Create(signal);
        var control = new AScanControl();
        var host = new Window { Content = control, Width = 640, Height = 300, WindowStyle = WindowStyle.None, ShowInTaskbar = false };
        try
        {
            host.Show(); host.UpdateLayout(); control.VisualSnapshot = Composite(control, signal, cursors); Render(control);
            double x = 64 + (control.ActualWidth - 84) * .25;
            Assert.AreEqual(AScanCursorId.A, control.HitTestCursor(new(x, 120)));
            AScanCursorMoveRequestedEventArgs? moved = null; control.CursorMoveRequested += (_, args) => moved = args;
            Assert.IsTrue(control.BeginCursorDrag(new(x, 120))); Assert.IsFalse(control.BeginPan(new(300, 120)));
            control.ContinueCursorDrag(new(10_000, 120)); Assert.AreEqual(signal.MaximumTimeSeconds, moved!.TimeSeconds);
            control.EndCursorDrag(); Assert.IsFalse(control.IsMouseCaptured);
            ScanTimeZoomRequestedEventArgs? zoom = null; ScanTimePanRequestedEventArgs? pan = null;
            control.TimeZoomRequested += (_, args) => zoom = args; control.TimePanRequested += (_, args) => pan = args;
            Point center = new(64 + (control.ActualWidth - 84) / 2, 120);
            Assert.IsTrue(control.RequestTimeZoom(center, 120)); Assert.IsGreaterThan(1, zoom!.Factor);
            Assert.IsTrue(control.BeginPan(center)); control.ContinuePan(new(center.X + 50, center.Y));
            Assert.IsNotNull(pan); control.EndInteraction();
            host.Width += 100; host.UpdateLayout(); Render(control);
            Assert.IsFalse(control.HasCurrentTransform); Assert.IsNull(control.HitTestCursor(new(x, 120)));
            control.VisualSnapshot = Composite(control, signal, cursors); Render(control); Assert.IsTrue(control.HasCurrentTransform);
        }
        finally { host.Close(); }
        return Task.CompletedTask;
    });

    [TestMethod]
    public Task RoutedMouseDragMovesBothCursorsWhileTheControlOwnsCapture() => StaTest.Run(() =>
    {
        AScanSnapshot signal = Snapshot(); Point position = default;
        var control = new AScanControl(TimeProvider.System, () => ModifierKeys.None, _ => position,
            args => args.ClickCount, () => position);
        var focusTarget = new TextBox(); var panel = new Grid();
        panel.Children.Add(control); panel.Children.Add(focusTarget);
        var host = new Window { Content = panel, Width = 640, Height = 300, WindowStyle = WindowStyle.None, ShowInTaskbar = false };
        try
        {
            host.Show(); host.UpdateLayout();
            AScanCursorState cursors = AScanCursorMeasurements.Create(signal);
            control.VisualSnapshot = Composite(control, signal, cursors); Render(control);
            var moves = new List<(AScanCursorId Cursor, double Time)>();
            control.CursorMoveRequested += (_, args) => moves.Add((args.Cursor, args.TimeSeconds));
            Rect plot = new(64, 18, control.ActualWidth - 84, control.ActualHeight - 54);

            Drag(cursors.A.TimeSeconds, .40, AScanCursorId.A);
            Drag(cursors.B.TimeSeconds, .60, AScanCursorId.B);

            void Drag(double initialTime, double targetFraction, AScanCursorId expected)
            {
                position = new(plot.Left + initialTime / signal.MaximumTimeSeconds * plot.Width, plot.Top + plot.Height / 2);
                control.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
                { RoutedEvent = Mouse.MouseDownEvent, Source = control });
                Assert.IsTrue(control.IsMouseCaptured); Assert.AreEqual(expected, control.DraggingCursor);
                Assert.IsTrue(control.IsCursorInputTrackingEnabled);
                focusTarget.Focus();
                Assert.IsTrue(control.IsMouseCaptured, "Keyboard focus must not cancel an owned mouse gesture.");
                position = new(plot.Left + .35 * plot.Width, position.Y);
                control.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount)
                { RoutedEvent = Mouse.MouseMoveEvent, Source = control, Handled = true });
                Assert.IsTrue(moves.Any(x => x.Cursor == expected));
                Mouse.Capture(null);
                Assert.AreEqual(expected, control.DraggingCursor,
                    "A transient capture loss must preserve the accepted cursor gesture until mouse-up.");
                AScanCompositeVisualSnapshot coherent = control.VisualSnapshot!;
                control.VisualSnapshot = null;
                Assert.AreEqual(expected, control.DraggingCursor,
                    "A transient live-composition gap must not terminate the physical cursor gesture.");
                Assert.IsTrue(control.IsCursorInputTrackingEnabled);
                control.VisualSnapshot = coherent;
                Render(control);

                position = new(plot.Left + targetFraction * plot.Width, position.Y);
                control.RefreshCursorGesture();
                Assert.IsTrue(moves.Any(x => x.Cursor == expected &&
                    Math.Abs(x.Time - signal.MaximumTimeSeconds * targetFraction) < 1e-12));

                control.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
                { RoutedEvent = Mouse.MouseUpEvent, Source = control });
                Assert.IsFalse(control.IsMouseCaptured); Assert.IsNull(control.DraggingCursor);
                Assert.IsFalse(control.IsCursorInputTrackingEnabled);
            }
        }
        finally { host.Close(); }
        return Task.CompletedTask;
    });

    [TestMethod]
    public Task RoutedDragFromFreePlotAreaMovesTheNearestVisibleCursor() => StaTest.Run(() =>
    {
        AScanSnapshot signal = Snapshot(); Point position = default;
        var control = new AScanControl(TimeProvider.System, () => ModifierKeys.None, _ => position,
            args => args.ClickCount, () => position);
        var host = new Window { Content = control, Width = 640, Height = 300, WindowStyle = WindowStyle.None, ShowInTaskbar = false };
        try
        {
            host.Show(); host.UpdateLayout();
            control.VisualSnapshot = Composite(control, signal, AScanCursorMeasurements.Create(signal)); Render(control);
            AScanCursorMoveRequestedEventArgs? moved = null;
            control.CursorMoveRequested += (_, args) => moved = args;
            position = new(64 + (control.ActualWidth - 84) * .15, 80);
            Assert.IsNull(control.HitTestCursor(position), "The gesture starts outside the narrow cursor hit zone.");
            control.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
            { RoutedEvent = Mouse.MouseDownEvent, Source = control });
            Assert.AreEqual(AScanCursorId.A, control.DraggingCursor);
            position = new(64 + (control.ActualWidth - 84) * .40, 80);
            control.RefreshCursorGesture();
            Assert.AreEqual(AScanCursorId.A, moved!.Cursor);
            Assert.AreEqual(signal.MaximumTimeSeconds * .40, moved.TimeSeconds, 1e-12);
            control.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
            { RoutedEvent = Mouse.MouseUpEvent, Source = control });
        }
        finally { host.Close(); }
        return Task.CompletedTask;
    });

    [TestMethod]
    public Task ThrottleAdmitsOnlyWholeSnapshots() => StaTest.Run(async () =>
    {
        var clock = new ManualClock(); var control = new AScanControl(clock);
        control.Measure(new(640, 300)); control.Arrange(new(0, 0, 640, 300));
        AScanSnapshot first = Snapshot(1), second = Snapshot(2, first.Metadata.RunId);
        control.VisualSnapshot = Composite(control, first, AScanCursorMeasurements.Create(first)); Render(control);
        Assert.AreEqual(1ul, control.DisplayedCursorSnapshotVersion);
        control.VisualSnapshot = Composite(control, second, AScanCursorMeasurements.Create(second)); Render(control);
        Assert.AreEqual(1ul, control.DisplayedCursorSnapshotVersion);
        clock.Advance(AScanVisualDelivery.MinimumPublicationInterval); control.RefreshSnapshot();
        await Dispatcher.Yield(DispatcherPriority.Render); Render(control);
        Assert.AreEqual(2ul, control.DisplayedCursorSnapshotVersion);
        Assert.AreSame(control.VisualSnapshot, control.DisplayedVisualSnapshot);
    });

    [TestMethod]
    public Task LostCaptureAndUnloadStopCursorAndPanUpdates() => StaTest.Run(() =>
    {
        AScanSnapshot signal = Snapshot(); var clock = new ManualClock(); var control = new AScanControl(clock);
        var host = new Window { Content = control, Width = 640, Height = 300, WindowStyle = WindowStyle.None, ShowInTaskbar = false };
        try
        {
            host.Show(); host.UpdateLayout(); control.VisualSnapshot = Composite(control, signal, AScanCursorMeasurements.Create(signal)); Render(control);
            int cursorMoves = 0, pans = 0; control.CursorMoveRequested += (_, _) => cursorMoves++;
            control.TimePanRequested += (_, _) => pans++;
            double x = 64 + (control.ActualWidth - 84) * .25;
            Assert.IsTrue(control.BeginCursorDrag(new(x, 120))); Mouse.Capture(null);
            Assert.IsTrue(control.IsCursorInputTrackingEnabled);
            control.ContinueCursorDrag(new(500, 120)); Assert.IsGreaterThan(0, cursorMoves);
            control.EndCursorDrag();
            Assert.IsFalse(control.IsCursorInputTrackingEnabled);
            Assert.IsTrue(control.BeginPan(new(300, 120))); control.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));
            control.ContinuePan(new(400, 120)); Assert.AreEqual(0, pans); Assert.IsFalse(control.IsMouseCaptured);
        }
        finally { host.Close(); }
        return Task.CompletedTask;
    });

    [TestMethod]
    public Task SuccessiveViewportSnapshotsDriveZoomAndPanFromDisplayedInstance() => StaTest.Run(() =>
    {
        AScanSnapshot signal = Snapshot(); var clock = new ManualClock(); var control = new AScanControl(clock);
        control.Measure(new(640, 300)); control.Arrange(new(0, 0, 640, 300));
        ScanTimeViewport full = ScanTimeViewportOperations.Create(signal.MinimumTimeSeconds, signal.MaximumTimeSeconds);
        ScanTimeViewport first = ScanTimeViewportOperations.Zoom(full, full.DomainMinimumSeconds, 2);
        ScanTimeViewport second = ScanTimeViewportOperations.Zoom(full, full.DomainMaximumSeconds, 4);
        control.VisualSnapshot = Composite(control, signal, viewport: first); Render(control);
        AssertAnchor(first);
        control.VisualSnapshot = Composite(control, signal, viewport: second);
        // A received snapshot is not interactable until admitted; the old displayed one remains authoritative.
        AssertAnchor(first);
        clock.Advance(AScanVisualDelivery.MinimumPublicationInterval);
        control.RefreshSnapshot(); Render(control); AssertAnchor(second);
        return Task.CompletedTask;

        void AssertAnchor(ScanTimeViewport expected)
        {
            ScanTimeZoomRequestedEventArgs? request = null;
            EventHandler<ScanTimeZoomRequestedEventArgs> handler = (_, args) => request = args;
            control.TimeZoomRequested += handler;
            Assert.IsTrue(control.RequestTimeZoom(new(64 + (control.ActualWidth - 84) * .25, 120), 120));
            control.TimeZoomRequested -= handler;
            Assert.AreEqual(expected.VisibleMinimumSeconds + expected.VisibleSpanSeconds * .25, request!.AnchorSeconds, 1e-15);
        }
    });

    [TestMethod]
    public void ProjectReferencesRespectVisualizationBoundary()
    {
        string root = FindRepositoryRoot();
        string project = Path.Combine(root, "src", "UTStudio.Visualization.Wpf", "UTStudio.Visualization.Wpf.csproj");
        CollectionAssert.AreEqual(new[] { "../UTStudio.Visualization.Core/UTStudio.Visualization.Core.csproj" },
            XDocument.Load(project).Descendants("ProjectReference")
                .Select(node => (node.Attribute("Include")?.Value ?? string.Empty).Replace('\\', '/')).ToArray());
    }

    internal static AScanCompositeVisualSnapshot Composite(AScanControl control, AScanSnapshot signal,
        AScanCursorState? cursors = null, ScanTimeViewport? viewport = null)
    {
        viewport ??= ScanTimeViewportOperations.Create(signal.MinimumTimeSeconds, signal.MaximumTimeSeconds);
        DpiScale dpi = VisualTreeHelper.GetDpi(control);
        var overlay = new GateOverlayVisualSnapshot(GateOverlayGeometryStatus.NotRequested, null, null, default,
            [], [], null, GateHandleKind.Unknown, 1, 0, null);
        return new(signal, cursors, overlay, new(1, viewport, GateAmplitudePresentationKind.RawRf,
            64, control.ActualWidth - 84, control.ActualHeight - 38, -(control.ActualHeight - 54),
            control.ActualWidth, control.ActualHeight, dpi.DpiScaleX, dpi.DpiScaleY));
    }
    internal static AScanSnapshot Snapshot(ulong version = 1, AcquisitionRunId? runId = null)
    {
        var configuration = new ConventionalAcquisitionConfiguration(new PhysicalChannelId(0), 4, 50_000_000);
        var metadata = new ConventionalUtFrameMetadata(new UtSourceId("standalone"), runId ?? new(Guid.NewGuid()),
            configuration, DateTimeOffset.UnixEpoch);
        return new AScanProjector(4).Project(metadata, 1, TimeSpan.Zero, [short.MinValue, 0, short.MaxValue, 0], version);
    }
    private static void Render(AScanControl control) => new RenderTargetBitmap(Math.Max(1, (int)control.ActualWidth),
        Math.Max(1, (int)control.ActualHeight), 96, 96, PixelFormats.Pbgra32).Render(control);
    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "UTStudio.sln"))) directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("Repository root was not found.");
    }
}
