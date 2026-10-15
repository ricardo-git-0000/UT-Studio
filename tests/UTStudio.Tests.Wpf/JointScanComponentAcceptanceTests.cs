using System.ComponentModel;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Xml.Linq;
using UTStudio.Contracts.Application;
using UTStudio.Contracts.Inspection;
using UTStudio.Presentation;
using UTStudio.ScanVisualization.AScan.Wpf;
using UTStudio.ScanVisualization.BScan.Wpf;
using UTStudio.Visualization.Core;

namespace UTStudio.Tests.Wpf;

[TestClass]
[DoNotParallelize]
public sealed class JointScanComponentAcceptanceTests
{
    [TestMethod]
    [DataRow(true, false, false)]
    [DataRow(false, true, false)]
    [DataRow(true, true, false)]
    [DataRow(true, true, true)]
    public Task ComponentsSupportAloneTogetherAndEitherCreationOrder(bool includeA, bool includeB, bool bFirst) =>
        StaTest.Run(async () =>
        {
            await using JointFixture fixture = await JointFixture.CreateAsync();
            AScanView? a = null;
            BScanView? b = null;
            var panel = new Grid();
            var host = new Window { Content = panel, Width = 900, Height = 600, ShowInTaskbar = false };
            if (bFirst && includeB) { b = fixture.CreateB(); panel.Children.Add(b); }
            if (includeA) { a = fixture.CreateA(); panel.Children.Add(a); }
            if (!bFirst && includeB) { b = fixture.CreateB(); panel.Children.Add(b); }
            host.Show();
            host.UpdateLayout();
            if (b is not null) { await b.Activation; }

            await fixture.PublishAsync(500, includeA, includeB);
            if (a is not null)
            {
                await DiagnosticWait.Until(() => a.State?.AScan?.TraceIdentity?.AcquisitionIndex == 500,
                    "A presents committed AcquisitionIndex 500");
                Assert.AreNotEqual(a.State!.AScan!.Sequence, a.State.AScan.TraceIdentity!.Value.AcquisitionIndex,
                    "Sequence must deliberately differ from AcquisitionIndex");
            }
            if (b is not null)
            { await DiagnosticWait.Until(() => b.State?.Region is not null, "B presents committed raster"); }

            if (a is not null) { await a.CloseAsync(); }
            if (b is not null) { await b.CloseAsync(); }
            host.Close();
            fixture.AssertComponentBalances();
        });

    [TestMethod]
    public Task ExactSelectionSharedViewportCloseOrdersAndReopenRemainIndependent() => StaTest.Run(async () =>
    {
        await using JointFixture fixture = await JointFixture.CreateAsync();
        var a = fixture.CreateA();
        var b = fixture.CreateB();
        var panel = new Grid();
        panel.Children.Add(a);
        panel.Children.Add(b);
        var host = new Window { Content = panel, Width = 900, Height = 600, ShowInTaskbar = false };
        string phase = "activation";
        AScanView? reopenedA = null;
        BScanView? reopenedB = null;
        AScanView? finalA = null;
        BScanView? finalB = null;
        try
        {
            host.Show(); host.UpdateLayout();
            await b.Activation;
            phase = "initial publication";
            await fixture.PublishAsync(700);
            await DiagnosticWait.Until(() => b.State?.Region is not null && a.State?.AScan is not null,
                "joint initial publication");

            phase = "selection";
            await b.ViewModel!.SelectColumnAsync(700);
            await DiagnosticWait.Until(() => a.State?.DisplayMode == AScanDisplayMode.Selected &&
                a.State.Selection?.Identity?.AcquisitionIndex == 700, "exact B to A selection");
            Assert.AreEqual((ulong)700, a.State!.AScan!.TraceIdentity!.Value.AcquisitionIndex);
            Assert.AreNotEqual(a.State.AScan.Sequence, a.State.AScan.TraceIdentity.Value.AcquisitionIndex);

            phase = "A initiated viewport";
            ScanTimeViewport? beforeZoom = fixture.Viewport.Viewport;
            a.ViewModel!.ZoomTime(a.State.AScan.MinimumTimeSeconds, 2);
            await DiagnosticWait.Until(() => fixture.Viewport.Viewport is not null &&
                fixture.Viewport.Viewport != beforeZoom &&
                a.Renderer.ViewportBinding?.Viewport == fixture.Viewport.Viewport &&
                b.State?.TimeViewportBinding?.Viewport == fixture.Viewport.Viewport,
                "bidirectional shared viewport publication");
            phase = "B initiated viewport";
            ScanTimeViewport fromA = fixture.Viewport.Viewport!;
            b.ViewModel!.ZoomTimeAt(fromA.VisibleMaximumSeconds, 2);
            await DiagnosticWait.Until(() => fixture.Viewport.Viewport is { } fromB && fromB != fromA &&
                a.Renderer.ViewportBinding?.Viewport == fromB,
                "B initiated viewport change is reflected by A");

            phase = "return to live";
            a.ViewModel.ReturnToLiveCommand.Execute(null);
            await DiagnosticWait.Until(() => a.State?.DisplayMode == AScanDisplayMode.Live, "return to live");

            phase = "close B";
            await b.CloseAsync();
            await fixture.PublishAsync(701, expectA: true, expectB: false);
            await DiagnosticWait.Until(() => a.State?.AScan?.TraceIdentity?.AcquisitionIndex == 701,
                "A continues after B closes");
            phase = "reopen B";
            reopenedB = fixture.CreateB();
            panel.Children.Add(reopenedB);
            await reopenedB.Activation;
            phase = "publication after reopening B";
            await fixture.PublishAsync(702);
            await DiagnosticWait.Until(() => reopenedB.State?.Region is not null, "reopened B");

            await a.CloseAsync();
            phase = "publication after closing A";
            await fixture.PublishAsync(703, expectA: false, expectB: true);
            await DiagnosticWait.Until(() => reopenedB.State?.StoreRevision is >= 4, "B continues after A closes");
            phase = "reopen A";
            reopenedA = fixture.CreateA();
            panel.Children.Add(reopenedA);
            host.UpdateLayout();
            await System.Windows.Threading.Dispatcher.Yield();
            Assert.IsNotNull(reopenedA.ViewModel, "reopened A activated before publication");
            phase = "publication after reopening A";
            await fixture.PublishAsync(704);
            await DiagnosticWait.Until(() => reopenedA.State?.AScan?.TraceIdentity?.AcquisitionIndex == 704,
                "reopened A with a new context");

            phase = "concurrent close before joint reopen";
            await Task.WhenAll(reopenedA.CloseAsync(), reopenedB.CloseAsync());
            phase = "joint reopen with two new contexts";
            finalA = fixture.CreateA();
            finalB = fixture.CreateB();
            panel.Children.Add(finalA); panel.Children.Add(finalB);
            host.UpdateLayout();
            await System.Windows.Threading.Dispatcher.Yield();
            Assert.IsNotNull(finalA.ViewModel, "jointly reopened A activated before publication");
            await finalB.Activation;
            await fixture.PublishAsync(705);
            await DiagnosticWait.Until(() => finalA.State?.AScan?.TraceIdentity?.AcquisitionIndex == 705 &&
                finalB.State?.Region is not null, "both components reopened together");
            phase = "final concurrent close";
            await Task.WhenAll(finalA.CloseAsync(), finalB.CloseAsync());
            host.Close();
            fixture.AssertComponentBalances();
        }
        catch (TimeoutException error) { throw new TimeoutException($"Joint phase timed out: {phase}", error); }
        finally
        {
            if (reopenedA is not null) { await reopenedA.CloseAsync(); }
            else { await a.CloseAsync(); }
            if (reopenedB is not null) { await reopenedB.CloseAsync(); }
            else { await b.CloseAsync(); }
            if (finalA is not null) { await finalA.CloseAsync(); }
            if (finalB is not null) { await finalB.CloseAsync(); }
            host.Close();
        }
    });

    [TestMethod]
    public Task TwoPairsUseIndependentSelectionAndViewportScopes() => StaTest.Run(async () =>
    {
        await using JointFixture first = await JointFixture.CreateAsync();
        await using JointFixture second = await JointFixture.CreateAsync();
        var firstA = first.CreateA(); var firstB = first.CreateB();
        var secondA = second.CreateA(); var secondB = second.CreateB();
        var panel = new Grid();
        panel.Children.Add(firstA); panel.Children.Add(firstB); panel.Children.Add(secondA); panel.Children.Add(secondB);
        var host = new Window { Content = panel, Width = 900, Height = 600, ShowInTaskbar = false };
        host.Show(); host.UpdateLayout();
        await Task.WhenAll(firstB.Activation, secondB.Activation);
        await first.PublishAsync(900); await second.PublishAsync(1200);
        await firstB.ViewModel!.SelectColumnAsync(900);
        await DiagnosticWait.Until(() => firstA.State?.DisplayMode == AScanDisplayMode.Selected,
            "first pair selected");
        Assert.AreEqual(AScanDisplayMode.Live, secondA.State!.DisplayMode);
        Assert.IsNull(second.Selection.State.Identity);

        firstA.ViewModel!.ZoomTime(firstA.State!.AScan!.MinimumTimeSeconds, 2);
        Assert.AreNotEqual(first.Viewport.Viewport, second.Viewport.Viewport);

        await Task.WhenAll(firstA.CloseAsync(), firstB.CloseAsync(), secondA.CloseAsync(), secondB.CloseAsync());
        host.Close();
        first.AssertComponentBalances(); second.AssertComponentBalances();
    });

    [TestMethod]
    public Task SelectionSupersessionAndFailedCallbacksStayInsideTheirBranch() => StaTest.Run(async () =>
    {
        var blocking = new BlockingSelector();
        await using JointFixture fixture = await JointFixture.CreateAsync(blocking.Attach);
        var a = fixture.CreateA(); var b = fixture.CreateB();
        var panel = new Grid(); panel.Children.Add(a); panel.Children.Add(b);
        var host = new Window { Content = panel, Width = 900, Height = 600, ShowInTaskbar = false };
        host.Show(); host.UpdateLayout(); await b.Activation;
        await fixture.PublishAsync(800); await fixture.PublishAsync(801);

        Task superseded = b.ViewModel!.SelectColumnAsync(800);
        await blocking.Entered;
        Task latest = b.ViewModel.SelectColumnAsync(801);
        await Task.WhenAll(superseded, latest);
        await DiagnosticWait.Until(() => a.State?.Selection?.Identity?.AcquisitionIndex == 801,
            "latest exact selection supersedes its predecessor");
        Assert.AreEqual((ulong)801, a.State!.AScan!.TraceIdentity!.Value.AcquisitionIndex);

        using IDisposable failedA = fixture.SubscribeFailingA();
        await fixture.PublishAsync(802);
        await DiagnosticWait.Until(() => b.State?.StoreRevision is >= 3,
            "B remains live after a failed A observer callback");
        failedA.Dispose();

        using IDisposable failedB = fixture.SubscribeFailingB();
        await fixture.PublishAsync(803);
        await DiagnosticWait.Until(() => a.State?.LatestCompatibleLive?.TraceIdentity?.AcquisitionIndex == 803,
            "A remains live after a failed B observer callback");

        await Task.WhenAll(a.CloseAsync(), b.CloseAsync());
        host.Close();
        fixture.AssertComponentBalances();
    });

    [TestMethod]
    public void DemoAndPackagesKeepPublicArchitectureBoundaries()
    {
        string root = FindRoot();
        string demo = Path.Combine(root, "samples", "UTStudio.ScanVisualization.Wpf.Demo");
        string project = Path.Combine(demo, "UTStudio.ScanVisualization.Wpf.Demo.csproj");
        string[] references = XDocument.Load(project).Descendants("ProjectReference")
            .Select(node => (node.Attribute("Include")?.Value ?? string.Empty).Replace('\\', '/')).ToArray();
        Assert.IsFalse(references.Any(reference => reference.Contains("App.Wpf", StringComparison.OrdinalIgnoreCase)));
        string source = string.Join('\n', Directory.EnumerateFiles(demo, "*.cs", SearchOption.AllDirectories)
            .Select(File.ReadAllText));
        Assert.DoesNotContain(".ViewModel", source, StringComparison.Ordinal);
        Assert.DoesNotContain("InternalsVisibleTo", source, StringComparison.Ordinal);
        Assert.DoesNotContain("static event", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ServiceLocator", source, StringComparison.OrdinalIgnoreCase);

        Assembly a = typeof(AScanView).Assembly;
        Assembly b = typeof(BScanView).Assembly;
        Assert.IsFalse(a.GetReferencedAssemblies().Any(reference => reference.Name == b.GetName().Name));
        Assert.IsFalse(b.GetReferencedAssemblies().Any(reference => reference.Name == a.GetName().Name));
        Assert.IsTrue(typeof(TraceSelectionScope).Assembly == typeof(SharedScanTimeViewport).Assembly);
        Assert.IsFalse(a.GetCustomAttributes<InternalsVisibleToAttribute>()
            .Any(attribute => attribute.AssemblyName.Contains("Demo", StringComparison.Ordinal)));
        Assert.IsFalse(b.GetCustomAttributes<InternalsVisibleToAttribute>()
            .Any(attribute => attribute.AssemblyName.Contains("Demo", StringComparison.Ordinal)));
        foreach (Assembly component in new[] { a, b })
        {
            Assert.IsFalse(component.GetTypes().SelectMany(type => type.GetEvents(
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static))
                .Any(), $"{component.GetName().Name} must not expose or retain static events");
            Assert.IsFalse(component.GetTypes().Any(type =>
                type.Name.Contains("ServiceLocator", StringComparison.OrdinalIgnoreCase) ||
                type.GetMembers(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance)
                    .Any(member => member.Name.Contains("ServiceLocator", StringComparison.OrdinalIgnoreCase))),
                $"{component.GetName().Name} must not contain a Service Locator surface");
        }
    }

    private sealed class JointFixture : IAsyncDisposable
    {
        private readonly ProductiveBScanFixture _b;
        private readonly ManualObservable<SessionSnapshot> _sessions = new();
        private readonly AScanVisualDelivery _aVisuals;
        private readonly SharedScanTimeViewport _viewport;
        private readonly TraceSelectionScope _selection;
        private readonly SessionSnapshot _session;

        private JointFixture(ProductiveBScanFixture b, IAuthoritativeAScanSelector selector)
        {
            _b = b;
            _aVisuals = new(b.TraceProvider);
            _aVisuals.OpenRun(b.Metadata.RunId);
            b.AfterCommitted = _aVisuals.NotifyCommitted;
            _viewport = new(b.Dispatcher);
            _selection = new(b.Dispatcher, selector);
            _session = new(1, SessionPhase.Running, b.Metadata.SourceId, b.Metadata.RunId,
                0, 0, null, b.Metadata, null, [], 0, canStop: true);
            _sessions.Publish(_session);
        }

        internal TraceSelectionScope Selection => _selection;
        internal SharedScanTimeViewport Viewport => _viewport;

        internal static async Task<JointFixture> CreateAsync(
            Func<IUtTraceProvider, IAuthoritativeAScanSelector>? selectorFactory = null)
        {
            ProductiveBScanFixture b = await ProductiveBScanFixture.CreateAsync();
            await b.DetachLegacyCompositionAsync();
            return new(b, selectorFactory?.Invoke(b.TraceProvider) ?? new UtTraceAScanSelector(b.TraceProvider));
        }

        internal AScanView CreateA() => new()
        {
            Context = new(_sessions, _session, _aVisuals, _aVisuals.StatusChanges,
                () => _aVisuals.Statistics, _viewport, _selection)
        };

        internal BScanView CreateB() => new()
        { Context = new(_b.Delivery, _viewport, _selection) };

        internal IDisposable SubscribeFailingA() => _aVisuals.Subscribe(new ThrowingObserver<AScanSnapshot>());
        internal IDisposable SubscribeFailingB() => _b.Delivery.Subscribe(new ThrowingObserver<BScanVisualUpdate>());

        internal Task PublishAsync(ulong acquisitionIndex) => PublishAsync(acquisitionIndex, expectA: true, expectB: true);

        internal async Task PublishAsync(ulong acquisitionIndex, bool expectA, bool expectB)
        {
            await _b.AddIndexedAsync(acquisitionIndex);
            if (expectB)
            {
                _b.Clock.Advance(BScanVisualDelivery.MinimumPublicationInterval);
                await System.Windows.Threading.Dispatcher.Yield();
            }
            if (expectA)
            {
                await DiagnosticWait.Until(() => _aVisuals.Current?.TraceIdentity?.AcquisitionIndex == acquisitionIndex,
                    $"A delivery publishes {acquisitionIndex}");
            }
        }

        internal void AssertComponentBalances()
        {
            Assert.AreEqual(0, _b.Delivery.ActiveConsumerCount, "B consumers");
            Assert.AreEqual(0, _b.Ingress.Diagnostics.ActiveReadLeases, "B leases");
            Assert.AreEqual(0, _b.RasterOwnersOutstanding, "B raster owners");
            Assert.AreEqual(0, _sessions.ObserverCount, "A session subscriptions");
            Assert.IsNull(_viewport.Viewport, "shared viewport domains removed by component consumers");
            Assert.AreEqual(_b.Delivery.Diagnostics.ObserverCallbacksStarted,
                _b.Delivery.Diagnostics.ObserverCallbacksCompleted, "B callbacks balanced");
            Assert.AreEqual(0, _b.Delivery.Diagnostics.ActiveRasterOwners, "B delivery owners");
            Assert.IsLessThanOrEqualTo(1, _b.Clock.RegisteredTimers,
                "no component timer; at most the host-owned delivery timer may remain scheduled");
            Assert.IsFalse(_selection.State.IsPending, "selection callbacks drained");
        }

        public async ValueTask DisposeAsync()
        {
            _b.AfterCommitted = null;
            await _aVisuals.DisposeAsync();
            await _selection.DisposeAsync();
            await _viewport.DisposeAsync();
            await _b.DisposeAsync();
        }
    }

    private sealed class ManualObservable<T> : IObservable<T>
    {
        private readonly List<IObserver<T>> _observers = [];
        internal int ObserverCount => _observers.Count;
        public IDisposable Subscribe(IObserver<T> observer)
        { _observers.Add(observer); return new Subscription(_observers, observer); }
        internal void Publish(T value)
        { foreach (IObserver<T> observer in _observers.ToArray()) { observer.OnNext(value); } }
        private sealed class Subscription(List<IObserver<T>> observers, IObserver<T> observer) : IDisposable
        { public void Dispose() => observers.Remove(observer); }
    }

    private sealed class ThrowingObserver<T> : IObserver<T>
    {
        public void OnCompleted() { }
        public void OnError(Exception error) { }
        public void OnNext(T value) => throw new InvalidOperationException("synthetic observer failure");
    }

    private sealed class BlockingSelector : IAuthoritativeAScanSelector
    {
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private IAuthoritativeAScanSelector? _inner;
        private int _first;
        internal Task Entered => _entered.Task;
        internal IAuthoritativeAScanSelector Attach(IUtTraceProvider provider)
        { _inner = new UtTraceAScanSelector(provider); return this; }

        public async ValueTask<AScanSelectionResult> ResolveAsync(TraceIdentity identity, ulong visualVersion,
            CancellationToken cancellationToken = default)
        {
            if (Interlocked.CompareExchange(ref _first, 1, 0) == 0)
            {
                _entered.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            return await _inner!.ResolveAsync(identity, visualVersion, cancellationToken);
        }
    }

    private static string FindRoot()
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current is not null && !File.Exists(Path.Combine(current.FullName, "UTStudio.sln")))
        { current = current.Parent; }
        return current?.FullName ?? throw new DirectoryNotFoundException("Repository root not found.");
    }
}
