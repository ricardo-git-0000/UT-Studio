using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
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

    private static AScanSnapshot Snapshot()
    {
        var configuration = new ConventionalAcquisitionConfiguration(new PhysicalChannelId(0), 4, 50_000_000);
        var metadata = new ConventionalUtFrameMetadata(new UtSourceId("standalone"),
            new AcquisitionRunId(Guid.NewGuid()), configuration, DateTimeOffset.UnixEpoch);
        short[] samples = [short.MinValue, 0, short.MaxValue, 0];
        return new AScanProjector(4).Project(metadata, 1, TimeSpan.Zero, samples);
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
