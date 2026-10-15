using System.IO;
using System.ComponentModel;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Xml.Linq;
using UTStudio.Presentation;
using UTStudio.ScanVisualization.BScan.Wpf;
using UTStudio.Visualization.Core;
using UTStudio.Visualization.Core.Scan2D;
using UTStudio.Visualization.Wpf.Controls;

namespace UTStudio.Tests.Wpf;

[TestClass]
[DoNotParallelize]
public sealed class BScanViewTests
{
    [TestMethod]
    public void PublicApiAndProjectBoundariesRemainReusable()
    {
        Type type = typeof(BScanView);
        Assert.IsNotNull(type.GetProperty(nameof(BScanView.Context)));
        Assert.IsNotNull(type.GetProperty(nameof(BScanView.State)));
        Assert.IsNotNull(type.GetProperty(nameof(BScanView.Phase)));
        Assert.IsNotNull(type.GetProperty(nameof(BScanView.Error)));
        Assert.IsNotNull(type.GetMethod(nameof(BScanView.CloseAsync), Type.EmptyTypes));
        Assert.IsTrue(typeof(IAsyncDisposable).IsAssignableFrom(type));

        string root = FindRoot();
        string component = Path.Combine(root, "src", "UTStudio.ScanVisualization.BScan.Wpf",
            "UTStudio.ScanVisualization.BScan.Wpf.csproj");
        string demo = Path.Combine(root, "samples", "UTStudio.BScan.Wpf.Demo",
            "UTStudio.BScan.Wpf.Demo.csproj");
        string[] componentReferences = References(component);
        CollectionAssert.AreEquivalent(new[]
        {
            "../UTStudio.Presentation/UTStudio.Presentation.csproj",
            "../UTStudio.Visualization.Wpf/UTStudio.Visualization.Wpf.csproj"
        }, componentReferences);
        Assert.IsFalse(componentReferences.Any(path => path.Contains("App.Wpf", StringComparison.OrdinalIgnoreCase) ||
            path.Contains("Inspection.Core", StringComparison.OrdinalIgnoreCase)));
        Assert.IsFalse(References(demo).Any(path => path.Contains("App.Wpf", StringComparison.OrdinalIgnoreCase)));
        Assert.IsFalse(typeof(BScanView).Assembly.GetCustomAttributes<InternalsVisibleToAttribute>()
            .Any(attribute => attribute.AssemblyName.StartsWith("UTStudio.App.Wpf", StringComparison.Ordinal)));
        string appRoot = Path.Combine(root, "src", "UTStudio.App.Wpf");
        string appSource = string.Join('\n', Directory.EnumerateFiles(appRoot, "*.cs", SearchOption.AllDirectories)
            .Select(File.ReadAllText));
        Assert.DoesNotContain("AttachExisting", appSource, StringComparison.Ordinal);

        string[] neutralAssemblies = ["UTStudio.Domain", "UTStudio.Contracts", "UTStudio.Application",
            "UTStudio.Inspection.Core", "UTStudio.Visualization.Core", "UTStudio.Presentation"];
        foreach (string assemblyName in neutralAssemblies)
        {
            Assembly assembly = AppDomain.CurrentDomain.GetAssemblies().SingleOrDefault(candidate =>
                candidate.GetName().Name == assemblyName) ?? Assembly.Load(assemblyName);
            Assert.IsFalse(assembly.GetReferencedAssemblies().Any(reference =>
                reference.Name is "PresentationFramework" or "PresentationCore" or "WindowsBase"), assemblyName);
        }
    }

    [TestMethod]
    public Task StandaloneViewActivatesCommandsAutomationDpiAndBalancesConsumer() => StaTest.Run(async () =>
    {
        await using var fixture = await ProductiveBScanFixture.CreateAsync();
        int initialConsumers = fixture.Delivery.ActiveConsumerCount;
        var inheritedData = new object();
        BScanView? view = null;
        view = new BScanView
        {
            DataContext = inheritedData,
            Context = new(fixture.Delivery, fixture.SharedViewport, fixture.Selection)
        };
        Assert.AreEqual(initialConsumers, fixture.Delivery.ActiveConsumerCount);
        Load(view);
        Assert.AreSame(inheritedData, view.DataContext);
        Assert.IsNotNull(view.ViewModel);
        Assert.AreEqual(initialConsumers + 1, fixture.Delivery.ActiveConsumerCount);
        Assert.IsNotNull(view.ViewModel!.ZoomTimeInCommand);
        Assert.IsNotNull(view.ViewModel.ZoomSpatialInCommand);
        Assert.IsNotNull(view.ViewModel.ResetAllCommand);

        foreach (Scan2DInterpolationMode mode in Enum.GetValues<Scan2DInterpolationMode>())
        {
            view.SelectResamplingMode(mode);
            Assert.AreEqual(mode, view.SelectedResamplingMode);
        }
        Assert.IsTrue(view.IsLanczosWarningVisible);

        VisualTreeHelper.SetRootDpi(view, new DpiScale(1.5, 1.5));
        view.Measure(new Size(900, 600));
        view.Arrange(new Rect(0, 0, 900, 600));
        new RenderTargetBitmap(900, 600, 144, 144, PixelFormats.Pbgra32).Render(view);
        Assert.AreEqual(1.5, VisualTreeHelper.GetDpi(view).PixelsPerDip);
        var peer = UIElementAutomationPeer.CreatePeerForElement(view) ?? new FrameworkElementAutomationPeer(view);
        Assert.AreEqual("Componente B-Scan", peer.GetName());

        Task first = view.CloseAsync();
        Task second = view.CloseAsync();
        Assert.AreSame(first, second);
        await Task.WhenAll(first, second);
        Assert.AreEqual(BScanComponentPhase.Closed, view.Phase);
        Assert.AreEqual(initialConsumers, fixture.Delivery.ActiveConsumerCount);
    });

    [TestMethod]
    public Task ContextIsOneShotAndAClosedComponentCanBeReopenedAsANewInstance() => StaTest.Run(async () =>
    {
        await using var fixture = await ProductiveBScanFixture.CreateAsync(4);
        await fixture.WaitForRevisionAsync(4);
        var context = new BScanComponentContext(fixture.Delivery, fixture.SharedViewport, fixture.Selection);
        var first = new BScanView { Context = context };
        Load(first);
        Assert.ThrowsExactly<InvalidOperationException>(() => first.Context = context with { });
        await first.CloseAsync();
        var reopened = new BScanView { Context = context };
        Load(reopened);
        Assert.IsNotNull(reopened.ViewModel);
        await reopened.CloseAsync();
    });

    [TestMethod]
    [DataRow((int)BScanAdapterConstructionStage.AdapterCreated)]
    [DataRow((int)BScanAdapterConstructionStage.ViewModelSubscribed)]
    [DataRow((int)BScanAdapterConstructionStage.ControlConnected)]
    [DataRow((int)BScanAdapterConstructionStage.EventsRegistered)]
    [DataRow((int)BScanAdapterConstructionStage.InitialStateApplied)]
    [DataRow((int)BScanAdapterConstructionStage.InitialStatePublished)]
    [DataRow((int)BScanAdapterConstructionStage.Connected)]
    public Task AdapterConstructionFailureRollsBackBeforeItIsObserved(int failingStageValue) =>
        StaTest.Run(async () =>
        {
            var failingStage = (BScanAdapterConstructionStage)failingStageValue;
            await using var fixture = await ProductiveBScanFixture.CreateAsync();
            int consumers = fixture.Delivery.ActiveConsumerCount;
            var failure = new InvalidOperationException($"failure-{failingStage}");
            var observed = new List<BScanAdapterConstructionStage>();
            var view = new BScanView
            {
                Context = new(fixture.Delivery, fixture.SharedViewport, fixture.Selection),
                AdapterConstructionProbe = stage =>
                {
                    observed.Add(stage);
                    if (stage == failingStage) { throw failure; }
                }
            };

            Load(view);
            Exception error = await Assert.ThrowsAsync<Exception>(() => view.Activation);

            Assert.AreSame(failure, error is CleanupFailureException cleanup ? cleanup.PrimaryError : error);
            Assert.AreEqual(failingStage, observed[^1]);
            Assert.IsNull(view.ViewModel);
            Assert.IsNull(view.Renderer.AppliedSnapshot);
            Assert.AreEqual(0, view.Renderer.ActiveCandidateBitmaps);
            Assert.AreEqual(0, view.Renderer.ActivePooledRasterBytes);
            Assert.AreEqual(consumers, fixture.Delivery.ActiveConsumerCount);
            ulong callbacks = fixture.Delivery.Diagnostics.ObserverCallbacksCompleted;
            await System.Windows.Threading.Dispatcher.Yield();
            Assert.AreEqual(callbacks, fixture.Delivery.Diagnostics.ObserverCallbacksCompleted);
            await view.CloseAsync();
        });

    [TestMethod]
    public Task ActivationPreservesPrimaryAndStructuresEveryRollbackFailure() => StaTest.Run(async () =>
    {
        await using var fixture = await ProductiveBScanFixture.CreateAsync();
        var primary = new InvalidOperationException("construction-primary");
        var cleanupFailure = new InvalidOperationException("cleanup-secondary");
        BScanView? view = null;
        view = new BScanView
        {
            Context = new(fixture.Delivery, fixture.SharedViewport, fixture.Selection),
            AdapterConstructionProbe = stage =>
            {
                if (stage == BScanAdapterConstructionStage.AdapterCreated) { throw primary; }
            },
            ViewModelFactory = (context, dispatcher) => new(context.Visuals, context.TimeViewport,
                context.SelectionScope, dispatcher, _ => throw cleanupFailure)
        };

        Load(view);
        CleanupFailureException error = await Assert.ThrowsAsync<CleanupFailureException>(() => view.Activation);

        Assert.AreSame(primary, error.PrimaryError);
        Assert.IsTrue(error.SecondaryErrors.Contains(cleanupFailure));
        Assert.IsTrue(error.SecondaryFailures.All(failure => !string.IsNullOrWhiteSpace(failure.Stage)));
        Assert.AreEqual(1, fixture.Delivery.ActiveConsumerCount);
        Assert.IsNull(view.ViewModel);
        await view.CloseAsync();
    });

    [TestMethod]
    public Task CloseAwaitsAdapterCleanupAndReportsItsFailure() => StaTest.Run(async () =>
    {
        await using var fixture = await ProductiveBScanFixture.CreateAsync();
        var cleanupFailure = new InvalidOperationException("adapter-cleanup");
        BScanView? view = null;
        view = new BScanView
        {
            Context = new(fixture.Delivery, fixture.SharedViewport, fixture.Selection),
            AdapterCleanupProbe = stage =>
            {
                if (stage == "control-event-raster-size") { throw cleanupFailure; }
            }
        };
        Load(view);
        await view.Activation;

        CleanupFailureException error = await Assert.ThrowsAsync<CleanupFailureException>(() => view.CloseAsync());

        Assert.IsTrue(Contains(error, cleanupFailure));
        Assert.AreEqual(BScanComponentPhase.Error, view.Phase);
        Assert.AreEqual(1, fixture.Delivery.ActiveConsumerCount);
        Assert.AreEqual(0, view.Renderer.ActiveCandidateBitmaps);
        Assert.AreEqual(0, view.Renderer.ActivePooledRasterBytes);
        Assert.AreSame(view.CloseAsync(), view.CloseAsync());
    });

    [TestMethod]
    public Task DirectViewAppliesFullAndIncrementalWithoutLeakingStaging() => StaTest.Run(async () =>
    {
        await using var fixture = await ProductiveBScanFixture.CreateAsync(4);
        await fixture.WaitForRevisionAsync(4);
        var view = new BScanView { Context = new(fixture.Delivery, fixture.SharedViewport, fixture.Selection) };
        Load(view);
        await view.Activation;
        BScanViewModel viewModel = view.ViewModel!;
        viewModel.SetRasterSize(128, 16, 1);
        await fixture.Clock.NextTimerAsync();
        fixture.Clock.Advance(BScanVisualDelivery.MinimumPublicationInterval);
        await WaitForStateAsync(viewModel, state => state.Region is not null);
        Assert.IsTrue(viewModel.State.Region!.Snapshot.RequiresFullRefresh);
        Assert.IsNotNull(view.Renderer.AppliedSnapshot);

        BScanVisualSnapshot full = view.Renderer.AppliedSnapshot!;
        var affected = new BScanRasterRectangle(0, 0, 1, full.RasterHeight);
        BScanVisualSnapshot incremental = full with
        {
            SnapshotVersion = full.SnapshotVersion + 1,
            BaseStoreRevision = full.StoreRevision,
            StoreRevision = full.StoreRevision + 1,
            DirtyRange = new(full.RepresentedRange.First, full.RepresentedRange.First,
                full.StoreRevision, full.StoreRevision + 1),
            AffectedRegion = affected,
            RequiresFullRefresh = false,
            FullRefreshReason = BScanFullRefreshReason.None
        };
        Assert.IsTrue(view.Renderer.ApplyRaster(incremental, (address, length, _) =>
        {
            Surface(address, length).Clear();
            return true;
        }, 96, 96));
        Assert.IsFalse(view.Renderer.AppliedSnapshot!.RequiresFullRefresh);
        Assert.AreEqual(0, view.Renderer.ActiveCandidateBitmaps);

        await view.CloseAsync();
        Assert.AreEqual(1, fixture.Delivery.ActiveConsumerCount);
        Assert.AreEqual(0, view.Renderer.ActiveCandidateBitmaps);
        Assert.AreEqual(0, view.Renderer.ActivePooledRasterBytes);
    });

    [TestMethod]
    public Task DirectViewPreservesBitmapOnFalseAndThrowThenRecoversReentrantly() => StaTest.Run(async () =>
    {
        await using var fixture = await ProductiveBScanFixture.CreateAsync(4);
        int reentered = 0;
        BScanView? view = null;
        view = new BScanView
        { Context = new(fixture.Delivery, fixture.SharedViewport, fixture.Selection) };
        Load(view);
        await view.Activation;
        BScanViewModel viewModel = view.ViewModel!;
        viewModel.SetRasterSize(128, 16, 1);
        await fixture.Clock.NextTimerAsync();
        fixture.Clock.Advance(BScanVisualDelivery.MinimumPublicationInterval);
        await WaitForStateAsync(viewModel, state => state.Region is not null && view.Renderer.AppliedSnapshot is not null);
        BScanVisualSnapshot applied = view.Renderer.AppliedSnapshot!;
        byte[] pixels = BitmapBytes((BitmapSource)view.Renderer.RasterSource!);

        BScanVisualSnapshot candidate = applied with
        {
            SnapshotVersion = applied.SnapshotVersion + 1,
            StoreRevision = applied.StoreRevision + 1
        };
        Assert.IsFalse(view.Renderer.ApplyRaster(candidate, (_, _, _) => false, 96, 96));
        Assert.AreSame(applied, view.Renderer.AppliedSnapshot);
        CollectionAssert.AreEqual(pixels, BitmapBytes((BitmapSource)view.Renderer.RasterSource!));

        Assert.ThrowsExactly<InvalidOperationException>(() => view.Renderer.ApplyRaster(candidate,
            (_, _, _) => throw new InvalidOperationException("copy-failure"), 96, 96));
        Assert.AreSame(applied, view.Renderer.AppliedSnapshot);
        CollectionAssert.AreEqual(pixels, BitmapBytes((BitmapSource)view.Renderer.RasterSource!));

        Assert.IsFalse(view.Renderer.ApplyRaster(candidate, (_, _, _) =>
        {
            if (Interlocked.Exchange(ref reentered, 1) == 0) { viewModel.Refresh(); }
            return false;
        }, 96, 96));
        Assert.AreEqual(1, reentered);
        Assert.AreSame(applied, view.Renderer.AppliedSnapshot);
        CollectionAssert.AreEqual(pixels, BitmapBytes((BitmapSource)view.Renderer.RasterSource!));
        Assert.AreEqual(0, view.Renderer.ActiveCandidateBitmaps);
        await view.CloseAsync();
    });

    [TestMethod]
    public Task RealViewRejectsReentrantlyStaleCopyAndRecoversOnce() => StaTest.Run(async () =>
    {
        await using var fixture = await ProductiveBScanFixture.CreateAsync(4);
        await fixture.WaitForRevisionAsync(4);
        await fixture.DetachLegacyCompositionAsync();
        bool armed = false;
        int invalidations = 0;
        bool stalePreserved = false;
        ImageSource? expectedBitmap = null;
        byte[]? expectedPixels = null;
        BScanVisualSnapshot? expectedIdentity = null;
        var copyCompleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        BScanView? view = null;
        view = new BScanView
        {
            Context = new(fixture.Delivery, fixture.SharedViewport, fixture.Selection),
            RasterCopyProbe = (stage, _) =>
            {
                if (!armed) { return; }
                if (stage == BScanRasterCopyStage.Started && expectedBitmap is null)
                {
                    expectedBitmap = view!.Renderer.RasterSource;
                    expectedPixels = BitmapBytes((BitmapSource)view.Renderer.RasterSource!);
                    expectedIdentity = view.Renderer.AppliedSnapshot;
                }
                if (stage == BScanRasterCopyStage.Admitted && Interlocked.CompareExchange(ref invalidations, 1, 0) == 0)
                { view!.ViewModel!.Refresh(); }
                if (stage == BScanRasterCopyStage.Completed && !copyCompleted.Task.IsCompleted)
                {
                    stalePreserved = ReferenceEquals(expectedBitmap, view!.Renderer.RasterSource) &&
                        Equals(expectedIdentity, view.Renderer.AppliedSnapshot) &&
                        expectedPixels!.SequenceEqual(BitmapBytes((BitmapSource)view.Renderer.RasterSource!));
                    copyCompleted.TrySetResult();
                }
            }
        };
        Window host = Show(view);
        await view.Activation;
        view.ViewModel!.SetRasterSize(128, 16, 1);
        await fixture.Clock.NextTimerAsync();
        fixture.Clock.Advance(BScanVisualDelivery.MinimumPublicationInterval);
        await WaitForStateAsync(view.ViewModel, state => state.Region is not null && view.Renderer.AppliedSnapshot is not null);
        ImageSource bitmap = view.Renderer.RasterSource!;
        byte[] pixels = BitmapBytes((BitmapSource)bitmap);
        BScanVisualSnapshot identity = view.Renderer.AppliedSnapshot!;
        long fullBefore = fixture.Delivery.FullIdentityRequestCount;

        armed = true;
        await fixture.AddIndexedAsync(4);
        await fixture.Clock.NextTimerAsync();
        fixture.Clock.Advance(BScanVisualDelivery.MinimumPublicationInterval);
        await WaitForStateAsync(view.ViewModel, state => state.StoreRevision >= 5);
        await copyCompleted.Task;

        try
        {
            Assert.IsTrue(stalePreserved, "stale candidate must preserve bitmap, pixels and identity before recovery");
            await WaitForStateAsync(view.ViewModel, state => state.Region is not null &&
                view.Renderer.AppliedSnapshot is { RequiresFullRefresh: true });
            Assert.AreEqual(fullBefore + 1, fixture.Delivery.FullIdentityRequestCount, "single full recovery");
            Assert.AreEqual(1, invalidations, "single reentrant invalidation");
            AssertOpenComponentBalances(view, fixture);
        }
        finally { await view.CloseAsync(); }
        AssertClosedComponentBalances(view, fixture, 0);
    });

    [TestMethod]
    public Task RealViewReentrantInvalidationCloseRejectsSecondVisitAndDrainsFirst() => StaTest.Run(async () =>
    {
        await using var fixture = await ProductiveBScanFixture.CreateAsync(4);
        await fixture.WaitForRevisionAsync(4);
        await fixture.DetachLegacyCompositionAsync();
        bool armed = false;
        Task? close = null;
        BScanAdapterDiagnostics? during = null;
        bool secondVisitAccepted = true;
        var admitted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        BScanView? view = null;
        view = new BScanView
        {
            Context = new(fixture.Delivery, fixture.SharedViewport, fixture.Selection),
            RasterCopyProbe = (stage, token) =>
            {
                if (!armed || stage != BScanRasterCopyStage.Admitted) { return; }
                view!.ViewModel!.Refresh();
                close = view!.CloseAsync();
                during = view.AdapterDiagnostics;
                secondVisitAccepted = view.TryDiagnosticVisit(token);
                Assert.AreEqual(BScanAdapterLifecycle.Closing, during!.Value.Lifecycle,
                    "adapter must remain Closing while the first visit is active");
                Assert.IsFalse(secondVisitAccepted, "a reentrant second visit must be rejected after Closing linearizes");
                Assert.IsFalse(close.IsCompleted, "close must wait for the admitted protected visit");
                admitted.TrySetResult();
            }
        };
        Window host = Show(view);
        await view.Activation;
        view.ViewModel!.SetRasterSize(128, 16, 1);
        await fixture.Clock.NextTimerAsync();
        fixture.Clock.Advance(BScanVisualDelivery.MinimumPublicationInterval);
        await WaitForStateAsync(view.ViewModel, state => state.Region is not null && view.Renderer.AppliedSnapshot is not null);
        armed = true;

        await fixture.AddIndexedAsync(4);
        await fixture.Clock.NextTimerAsync();
        fixture.Clock.Advance(BScanVisualDelivery.MinimumPublicationInterval);
        await WaitForStateAsync(view.ViewModel, state => state.StoreRevision >= 5 || close is not null);
        await admitted.Task;
        await close!;

        Assert.AreEqual(1, during!.Value.ActiveCopies, "active admitted copy");
        Assert.AreEqual(during.Value.CopiesStarted, during.Value.CopiesAdmitted, "admitted callbacks");
        Assert.IsGreaterThanOrEqualTo(1, view.AdapterDiagnostics!.Value.RejectedVisits,
            "the diagnostic second visit is rejected during Closing");
        Assert.AreEqual(BScanAdapterLifecycle.Closed, view.AdapterDiagnostics.Value.Lifecycle, "closed after first visit returns");
        Assert.AreEqual(view.AdapterDiagnostics.Value.CopiesStarted, view.AdapterDiagnostics.Value.CopiesCompleted,
            "first visit completed before close completed");
        long callbacks = view.AdapterDiagnostics.Value.CopiesStarted;
        await fixture.AddIndexedAsync(5);
        await System.Windows.Threading.Dispatcher.Yield();
        Assert.AreEqual(callbacks, view.AdapterDiagnostics.Value.CopiesStarted, "no later component callbacks");
        AssertClosedComponentBalances(view, fixture, 0);
    });

    [TestMethod]
    public Task RealViewActivationRollbackAfterInitialRasterRestoresEmptyControlExactly() => StaTest.Run(async () =>
    {
        await using var fixture = await ProductiveBScanFixture.CreateAsync(4);
        await fixture.WaitForRevisionAsync(4);
        await fixture.DetachLegacyCompositionAsync();
        var primary = new InvalidOperationException("after-initial-raster");
        ImageSource? applied = null;
        byte[]? appliedBytes = null;
        BScanVisualSnapshot? appliedIdentity = null;
        var cleanupStages = new List<string>();
        BScanView? view = null;
        view = new BScanView
        {
            Context = new(fixture.Delivery, fixture.SharedViewport, fixture.Selection),
            AdapterCleanupProbe = cleanupStages.Add,
            BeforeActivationCommit = async viewModel =>
            {
                viewModel.SetRasterSize(128, 16, 1);
                await fixture.Clock.NextTimerAsync();
                fixture.Clock.Advance(BScanVisualDelivery.MinimumPublicationInterval);
                await WaitForStateAsync(viewModel, state => state.Region is not null);
            },
            AdapterConstructionProbe = stage =>
            {
                if (stage != BScanAdapterConstructionStage.InitialStatePublished) { return; }
                applied = view!.Renderer.RasterSource;
                appliedBytes = BitmapBytes((BitmapSource)applied!);
                appliedIdentity = view.Renderer.AppliedSnapshot;
                throw primary;
            },
        };
        Window host = Show(view);

        Exception error = await Assert.ThrowsAsync<Exception>(() => view.Activation);

        Assert.AreSame(primary, error is CleanupFailureException cleanup ? cleanup.PrimaryError : error);
        Assert.IsNotNull(applied, "a real initial bitmap was applied before failure");
        Assert.IsGreaterThan(0, appliedBytes!.Length, "initial bitmap bytes");
        Assert.IsNotNull(appliedIdentity, "initial identity installed");
        Assert.IsNull(view.Renderer.RasterSource, "bitmap restored to pre-connection instance");
        Assert.IsNull(view.Renderer.AppliedSnapshot, "identity restored");
        Assert.IsNull(view.Renderer.TimeViewport, "time viewport restored");
        Assert.IsNull(view.Renderer.SpatialViewport, "spatial viewport restored");
        Assert.IsNull(view.Renderer.ResidentRange, "resident range restored");
        Assert.IsNull(view.Renderer.SelectedAcquisitionIndex, "selection restored");
        Assert.IsTrue(view.InputBindings.OfType<KeyBinding>().All(binding => binding.Command is null), "bindings restored");
        Assert.AreEqual(7, cleanupStages.Count(stage => stage.StartsWith("control-event-", StringComparison.Ordinal)),
            "all handlers removed independently");
        CollectionAssert.Contains(cleanupStages, "viewmodel-subscription", "adapter subscription removed");
        Assert.IsNull(view.AdapterDiagnostics, "component never published connected adapter");
        Assert.AreEqual(0, fixture.Delivery.ActiveConsumerCount, "consumer rollback");
        Assert.AreEqual(0, view.Renderer.ActiveStagingBuffers, "rollback staging buffers");
        Assert.AreEqual(0, view.Renderer.ActiveStagingBytes, "rollback staging bytes");
        Assert.AreEqual(0, view.Renderer.ActiveBackupBuffers, "rollback backup buffers");
        Assert.AreEqual(0, view.Renderer.ActiveBackupBytes, "rollback backup bytes");
        Assert.AreEqual(0, view.Renderer.ActiveCandidateBitmaps, "rollback candidates");
        Assert.AreEqual(0, fixture.Ingress.Diagnostics.ActiveReadLeases, "rollback leases");
        Assert.AreEqual(0, fixture.RasterOwnersOutstanding, "rollback raster owners");
        Assert.AreEqual(fixture.Delivery.Diagnostics.ObserverCallbacksStarted,
            fixture.Delivery.Diagnostics.ObserverCallbacksCompleted, "rollback callbacks");
        Assert.AreEqual(1, fixture.Clock.RegisteredTimers, "backend timer ownership");
    });

    [TestMethod]
    public Task RealViewConnectedUpdateFailurePreservesConfirmedBitmapBytesAndIdentityThenRecovers() => StaTest.Run(async () =>
    {
        await using var fixture = await ProductiveBScanFixture.CreateAsync(4);
        await fixture.WaitForRevisionAsync(4);
        await fixture.DetachLegacyCompositionAsync();
        bool armed = false;
        int failures = 0;
        bool preservedAtFailure = false;
        var failedCopy = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ImageSource? bitmap = null;
        byte[]? bytes = null;
        BScanVisualSnapshot? identity = null;
        BScanView? view = null;
        view = new BScanView
        {
            Context = new(fixture.Delivery, fixture.SharedViewport, fixture.Selection),
            RasterCopyProbe = (stage, _) =>
            {
                if (!armed) { return; }
                if (stage == BScanRasterCopyStage.Failed && Interlocked.CompareExchange(ref failures, 1, 0) == 0)
                {
                    preservedAtFailure = ReferenceEquals(bitmap, view!.Renderer.RasterSource) &&
                        ReferenceEquals(identity, view.Renderer.AppliedSnapshot) &&
                        bytes!.SequenceEqual(BitmapBytes((BitmapSource)view.Renderer.RasterSource!));
                    failedCopy.TrySetResult();
                }
            }
        };
        Window host = Show(view);
        await view.Activation;
        view.ViewModel!.SetRasterSize(128, 16, 1);
        await fixture.Clock.NextTimerAsync();
        fixture.Clock.Advance(BScanVisualDelivery.MinimumPublicationInterval);
        await WaitForStateAsync(view.ViewModel, state => state.Region is not null && view.Renderer.AppliedSnapshot is not null);
        bitmap = view.Renderer.RasterSource!;
        bytes = BitmapBytes((BitmapSource)bitmap);
        identity = view.Renderer.AppliedSnapshot!;
        view.Renderer.FailIncrementalCommitAtRow = 1;

        try
        {
            armed = true;
            await fixture.AddIndexedAsync(4);
            await fixture.Clock.NextTimerAsync();
            fixture.Clock.Advance(BScanVisualDelivery.MinimumPublicationInterval);
            await failedCopy.Task;

            Assert.IsTrue(preservedAtFailure,
                "failed connected update keeps confirmed bitmap instance, bytes and identity before recovery");
            Assert.AreEqual(1, failures, "one injected connected-update failure");
            view.Renderer.FailIncrementalCommitAtRow = null;
            await fixture.AddIndexedAsync(5);
            await fixture.Clock.NextTimerAsync();
            fixture.Clock.Advance(BScanVisualDelivery.MinimumPublicationInterval);
            await WaitForStateAsync(view.ViewModel!, state => state.StoreRevision >= 6 &&
                view.Renderer.AppliedSnapshot is { StoreRevision: >= 6 });
            Assert.IsNotNull(view.Renderer.RasterSource, "the real BScanView publishes again after callback failure");
            Assert.AreNotSame(identity, view.Renderer.AppliedSnapshot,
                "recovery installs a later authoritative raster identity");
            AssertOpenComponentBalances(view, fixture);
        }
        finally { await view.CloseAsync(); }
        AssertClosedComponentBalances(view, fixture, 0);
    });

    private static void Load(BScanView view) =>
        view.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent, view));

    private static Window Show(BScanView view)
    {
        var window = new Window { Content = view, Width = 640, Height = 480, ShowInTaskbar = false };
        window.Show();
        window.UpdateLayout();
        return window;
    }

    private static void AssertOpenComponentBalances(BScanView view, ProductiveBScanFixture fixture)
    {
        BScanAdapterDiagnostics diagnostics = view.AdapterDiagnostics!.Value;
        Assert.AreEqual(BScanAdapterLifecycle.Active, diagnostics.Lifecycle, "adapter lifecycle");
        Assert.AreEqual(0, view.Renderer.ActiveStagingBuffers, "staging buffers");
        Assert.AreEqual(0, view.Renderer.ActiveStagingBytes, "staging bytes");
        Assert.AreEqual(0, view.Renderer.ActiveBackupBuffers, "backup buffers");
        Assert.AreEqual(0, view.Renderer.ActiveBackupBytes, "backup bytes");
        Assert.AreEqual(0, view.Renderer.ActiveCandidateBitmaps, "bitmap candidates");
        Assert.AreEqual(0, diagnostics.ActiveCopies, "active callbacks");
        Assert.AreEqual(diagnostics.CopiesStarted, diagnostics.CopiesCompleted, "started/completed callbacks");
        Assert.IsTrue(diagnostics.ViewModelSubscribed, "adapter subscription");
        Assert.AreEqual(7, diagnostics.RegisteredHandlers, "registered handlers");
        Assert.AreEqual(0, fixture.Ingress.Diagnostics.ActiveReadLeases, "read leases");
        Assert.AreEqual(fixture.Delivery.Diagnostics.ActiveRasterOwners, fixture.RasterOwnersOutstanding,
            "tracked raster owners");
        Assert.IsLessThanOrEqualTo(2, fixture.RasterOwnersOutstanding, "bounded raster owners");
        Assert.AreEqual(1, fixture.Delivery.ActiveConsumerCount, "consumers");
        Assert.AreEqual(fixture.Delivery.Diagnostics.ObserverCallbacksStarted,
            fixture.Delivery.Diagnostics.ObserverCallbacksCompleted, "delivery callbacks");
        Assert.IsLessThanOrEqualTo(1, fixture.Clock.RegisteredTimers,
            "at most the host-owned publication timer may remain scheduled");
    }

    private static void AssertClosedComponentBalances(BScanView view, ProductiveBScanFixture fixture, int baselineConsumers)
    {
        BScanAdapterDiagnostics diagnostics = view.AdapterDiagnostics!.Value;
        Assert.AreEqual(BScanAdapterLifecycle.Closed, diagnostics.Lifecycle, "closed adapter lifecycle");
        Assert.AreEqual(0, view.Renderer.ActiveStagingBuffers, "closed staging buffers");
        Assert.AreEqual(0, view.Renderer.ActiveStagingBytes, "closed staging bytes");
        Assert.AreEqual(0, view.Renderer.ActiveBackupBuffers, "closed backup buffers");
        Assert.AreEqual(0, view.Renderer.ActiveBackupBytes, "closed backup bytes");
        Assert.AreEqual(0, view.Renderer.ActiveCandidateBitmaps, "closed candidates");
        Assert.AreEqual(0, diagnostics.ActiveCopies, "closed active callbacks");
        Assert.AreEqual(diagnostics.CopiesStarted, diagnostics.CopiesCompleted, "closed started/completed callbacks");
        Assert.IsLessThanOrEqualTo(diagnostics.CopiesAdmitted, diagnostics.CopiesStarted, "closed admitted callbacks");
        Assert.IsFalse(diagnostics.ViewModelSubscribed, "closed adapter subscription");
        Assert.AreEqual(0, diagnostics.RegisteredHandlers, "closed handlers");
        Assert.AreEqual(0, fixture.Ingress.Diagnostics.ActiveReadLeases, "closed leases");
        Assert.AreEqual(0, fixture.RasterOwnersOutstanding, "closed test raster owners");
        Assert.AreEqual(baselineConsumers, fixture.Delivery.ActiveConsumerCount, "closed consumers");
        Assert.AreEqual(fixture.Delivery.Diagnostics.ObserverCallbacksStarted,
            fixture.Delivery.Diagnostics.ObserverCallbacksCompleted, "closed delivery callbacks");
        Assert.IsLessThanOrEqualTo(1, fixture.Clock.RegisteredTimers,
            "at most the backend timer owned by the fixture may remain scheduled");
    }

    private static async Task WaitForStateAsync(BScanViewModel viewModel, Func<BScanViewState, bool> predicate)
    {
        if (predicate(viewModel.State)) { return; }
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        PropertyChangedEventHandler handler = (_, args) =>
        {
            if (args.PropertyName == nameof(BScanViewModel.State) && predicate(viewModel.State))
            { completion.TrySetResult(); }
        };
        viewModel.PropertyChanged += handler;
        try
        {
            if (predicate(viewModel.State)) { completion.TrySetResult(); }
            await completion.Task;
        }
        finally { viewModel.PropertyChanged -= handler; }
    }

    private static byte[] BitmapBytes(BitmapSource bitmap)
    {
        int stride = (bitmap.PixelWidth * bitmap.Format.BitsPerPixel + 7) / 8;
        var bytes = new byte[stride * bitmap.PixelHeight];
        bitmap.CopyPixels(bytes, stride, 0);
        return bytes;
    }

    private static bool Contains(Exception candidate, Exception expected) =>
        ReferenceEquals(candidate, expected) || candidate is CleanupFailureException cleanup &&
        (Contains(cleanup.PrimaryError, expected) || cleanup.SecondaryErrors.Any(error => Contains(error, expected)));

    private static unsafe Span<byte> Surface(nint address, int length) => new(address.ToPointer(), length);

    private static string[] References(string project) => XDocument.Load(project).Descendants("ProjectReference")
        .Select(node => (node.Attribute("Include")?.Value ?? string.Empty).Replace('\\', '/')).ToArray();

    private static string FindRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "UTStudio.sln")))
        { directory = directory.Parent; }
        return directory?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }
}
