using System.Windows;
using System.IO;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Xml.Linq;
using UTStudio.Contracts.Application;
using UTStudio.Domain.Acquisition;
using UTStudio.Presentation;
using UTStudio.ScanVisualization.AScan.Wpf;
using UTStudio.Visualization.Core;

namespace UTStudio.Tests.Wpf;

[TestClass]
public sealed class AScanViewTests
{
    [TestMethod]
    public void ComponentAndDemoHaveNoApplicationOrStoreImplementationReferences()
    {
        string root = FindRoot();
        string component = Path.Combine(root, "src", "UTStudio.ScanVisualization.AScan.Wpf", "UTStudio.ScanVisualization.AScan.Wpf.csproj");
        string demo = Path.Combine(root, "samples", "UTStudio.AScan.Wpf.Demo", "UTStudio.AScan.Wpf.Demo.csproj");
        string[] componentReferences = References(component);
        CollectionAssert.AreEquivalent(new[]
        {
            "../UTStudio.Presentation/UTStudio.Presentation.csproj",
            "../UTStudio.Visualization.Wpf/UTStudio.Visualization.Wpf.csproj"
        }, componentReferences);
        Assert.IsFalse(componentReferences.Any(path => path.Contains("Application", StringComparison.OrdinalIgnoreCase) ||
            path.Contains("Inspection", StringComparison.OrdinalIgnoreCase) || path.Contains("App.Wpf", StringComparison.OrdinalIgnoreCase)));
        CollectionAssert.AreEqual(new[]
        { "../../src/UTStudio.ScanVisualization.AScan.Wpf/UTStudio.ScanVisualization.AScan.Wpf.csproj" }, References(demo));
    }

    [TestMethod]
    public Task StandaloneViewOwnsVisualScopeAndPreservesInheritedDataContext() => StaTest.Run(async () =>
    {
        var sessions = new ManualObservable<SessionSnapshot>();
        var visuals = new ManualObservable<AScanSnapshot>();
        var hostData = new object();
        var view = new AScanView { DataContext = hostData };
        view.Context = new(sessions, Session(0, SessionPhase.Idle), visuals);
        Assert.AreEqual(0, sessions.Subscriptions);
        Assert.IsNull(view.State);
        Load(view);
        Assert.AreSame(hostData, view.DataContext);
        Assert.IsNotNull(view.State);
        Assert.IsFalse(view.ViewModel!.StartCommand.CanExecute(null));
        Assert.IsFalse(view.ViewModel.StopCommand.CanExecute(null));
        Assert.AreEqual(1, sessions.Subscriptions);
        Assert.AreEqual(1, visuals.Subscriptions);
        await view.CloseAsync();
        Assert.AreEqual(1, sessions.Unsubscriptions);
        Assert.AreEqual(1, visuals.Unsubscriptions);
        Assert.AreSame(view.CloseAsync(), view.CloseAsync());
    });

    [TestMethod]
    public Task CursorCommandsEventsResizeDpiAndAutomationRemainAvailable() => StaTest.Run(async () =>
    {
        var sessions = new ManualObservable<SessionSnapshot>();
        var visuals = new ManualObservable<AScanSnapshot>();
        var run = new AcquisitionRunId(Guid.NewGuid());
        var view = new AScanView();
        view.Context = new(sessions, Session(1, SessionPhase.Running, run), visuals);
        Load(view);
        sessions.Emit(Session(1, SessionPhase.Running, run));
        var configuration = new ConventionalAcquisitionConfiguration(new PhysicalChannelId(0), 4, 50_000_000);
        var metadata = new ConventionalUtFrameMetadata(new UtSourceId("fixture"), run, configuration, DateTimeOffset.UnixEpoch);
        visuals.Emit(new AScanProjector(4).Project(metadata, 1, TimeSpan.Zero, [short.MinValue, 0, short.MaxValue, 0], 1));
        await System.Windows.Threading.Dispatcher.Yield();
        Assert.IsNotNull(view.ViewModel!.Cursors);
        double resetA = view.ViewModel.Cursors!.A.TimeSeconds;
        view.ViewModel.MoveCursor(AScanCursorId.A, view.ViewModel.AScan!.MaximumTimeSeconds);
        await System.Windows.Threading.Dispatcher.Yield();
        Assert.AreNotEqual(resetA, view.ViewModel.Cursors.A.TimeSeconds);
        Assert.IsTrue(view.ViewModel.ResetCursorsCommand.CanExecute(null));
        view.ViewModel.ResetCursorsCommand.Execute(null);
        await System.Windows.Threading.Dispatcher.Yield();
        Assert.AreEqual(resetA, view.ViewModel.Cursors.A.TimeSeconds, 1e-15);
        view.ViewModel.ToggleCursorsCommand.Execute(null);
        await System.Windows.Threading.Dispatcher.Yield();
        Assert.IsFalse(view.ViewModel.Cursors!.IsVisible);
        VisualTreeHelper.SetRootDpi(view, new DpiScale(1.5, 1.5));
        view.Measure(new Size(800, 500)); view.Arrange(new Rect(0, 0, 800, 500));
        new RenderTargetBitmap(800, 500, 144, 144, PixelFormats.Pbgra32).Render(view);
        Assert.AreEqual(1.5, VisualTreeHelper.GetDpi(view).PixelsPerDip);
        var peer = UIElementAutomationPeer.CreatePeerForElement(view) ?? new FrameworkElementAutomationPeer(view);
        Assert.AreEqual("Componente A-Scan", peer.GetName());
        await view.CloseAsync();
    });

    [TestMethod]
    public Task ContextIsOneShotAndNewInstanceCanReopen() => StaTest.Run(async () =>
    {
        var first = new AScanView();
        var context = new AScanComponentContext(new ManualObservable<SessionSnapshot>(),
            Session(0, SessionPhase.Idle), new ManualObservable<AScanSnapshot>());
        first.Context = context;
        Load(first);
        Assert.ThrowsExactly<InvalidOperationException>(() => first.Context = context with { });
        await first.CloseAsync();
        var reopened = new AScanView { Context = context };
        Load(reopened);
        Assert.IsNotNull(reopened.State);
        await reopened.CloseAsync();
    });

    [TestMethod]
    public Task PhaseErrorRecoveryAndViewportCommandsAreExposedByCompleteView() => StaTest.Run(async () =>
    {
        var sessions = new ManualObservable<SessionSnapshot>();
        var visuals = new ManualObservable<AScanSnapshot>();
        var statuses = new ManualObservable<AScanDeliveryStatus>();
        var run = new AcquisitionRunId(Guid.NewGuid());
        var view = new AScanView
        {
            Context = new(sessions, Session(0, SessionPhase.Idle), visuals, statuses)
        };
        Load(view);
        Assert.AreEqual(AScanComponentPhase.Empty, view.ViewModel!.ComponentPhase);

        sessions.Emit(Session(1, SessionPhase.Running, run));
        var configuration = new ConventionalAcquisitionConfiguration(new PhysicalChannelId(0), 4, 50_000_000);
        var metadata = new ConventionalUtFrameMetadata(new UtSourceId("fixture"), run, configuration, DateTimeOffset.UnixEpoch);
        visuals.Emit(new AScanProjector(4).Project(metadata, 1, TimeSpan.Zero, [short.MinValue, 0, short.MaxValue, 0], 1));
        await System.Windows.Threading.Dispatcher.Yield();
        Assert.AreEqual(AScanComponentPhase.Live, view.ViewModel.ComponentPhase);
        Assert.IsTrue(view.ViewModel!.ZoomInCommand.CanExecute(null));
        view.ViewModel.ZoomInCommand.Execute(null);
        await System.Windows.Threading.Dispatcher.Yield();
        Assert.IsTrue(view.ViewModel.ResetViewportCommand.CanExecute(null));
        Assert.IsTrue(view.ViewModel.PanLeftCommand.CanExecute(null));
        Assert.IsTrue(view.ViewModel.PanRightCommand.CanExecute(null));
        double beforePan = view.ViewModel.TimeViewport!.VisibleMinimumSeconds;
        view.ViewModel.PanRightCommand.Execute(null);
        await System.Windows.Threading.Dispatcher.Yield();
        Assert.IsGreaterThan(beforePan, view.ViewModel.TimeViewport!.VisibleMinimumSeconds);
        view.ViewModel.ResetViewportCommand.Execute(null);
        await System.Windows.Threading.Dispatcher.Yield();
        Assert.IsTrue(view.ViewModel.TimeViewport!.IsReset);

        statuses.Emit(new(2, new("fixture.error", "synthetic")));
        await System.Windows.Threading.Dispatcher.Yield();
        Assert.AreEqual(AScanComponentPhase.Error, view.ViewModel.ComponentPhase);
        Assert.AreEqual("fixture.error", view.Error!.Code);
        statuses.Emit(new(3, null));
        await System.Windows.Threading.Dispatcher.Yield();
        Assert.AreEqual(AScanComponentPhase.Live, view.ViewModel.ComponentPhase);
        Assert.IsNull(view.Error);

        sessions.Emit(Session(2, SessionPhase.Idle, run));
        await System.Windows.Threading.Dispatcher.Yield();
        Assert.AreEqual(AScanComponentPhase.Frozen, view.ViewModel.ComponentPhase);
        await view.CloseAsync();
        Assert.AreEqual(1, statuses.Unsubscriptions);
    });

    private static SessionSnapshot Session(ulong version, SessionPhase phase, AcquisitionRunId? run = null) =>
        new(version, phase, new UtSourceId("fixture"), run, 0, 0, null, null, null, [], 0,
            canStart: phase == SessionPhase.Idle, canStop: phase == SessionPhase.Running);

    private static void Load(AScanView view) =>
        view.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent, view));

    private sealed class ManualObservable<T> : IObservable<T>
    {
        private readonly List<IObserver<T>> _observers = [];
        internal int Subscriptions { get; private set; }
        internal int Unsubscriptions { get; private set; }
        public IDisposable Subscribe(IObserver<T> observer)
        {
            _observers.Add(observer); Subscriptions++;
            return new Subscription(this, observer);
        }
        internal void Emit(T value) { foreach (IObserver<T> observer in _observers.ToArray()) { observer.OnNext(value); } }
        private sealed class Subscription(ManualObservable<T> owner, IObserver<T> observer) : IDisposable
        {
            private int _disposed;
            public void Dispose()
            {
                if (Interlocked.Exchange(ref _disposed, 1) != 0) { return; }
                owner._observers.Remove(observer); owner.Unsubscriptions++;
            }
        }
    }

    private static string[] References(string project) => XDocument.Load(project).Descendants("ProjectReference")
        .Select(node => (node.Attribute("Include")?.Value ?? string.Empty).Replace('\\', '/')).ToArray();

    private static string FindRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "UTStudio.sln"))) { directory = directory.Parent; }
        return directory?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }
}
