using System.IO;
using System.Windows;
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

    private static AScanSnapshot Snapshot(ulong version = 0)
    {
        var configuration = new ConventionalAcquisitionConfiguration(new PhysicalChannelId(0), 4, 50_000_000);
        var metadata = new ConventionalUtFrameMetadata(new UtSourceId("standalone"),
            new AcquisitionRunId(Guid.NewGuid()), configuration, DateTimeOffset.UnixEpoch);
        short[] samples = [short.MinValue, 0, short.MaxValue, 0];
        return new AScanProjector(4).Project(metadata, 1, TimeSpan.Zero, samples, version);
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
