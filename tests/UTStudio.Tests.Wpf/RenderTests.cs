using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using UTStudio.App.Wpf;
using UTStudio.App.Wpf.Composition;
using UTStudio.App.Wpf.Controls;
using UTStudio.App.Wpf.Services;
using UTStudio.Presentation;

namespace UTStudio.Tests.Wpf;

[TestClass]
public sealed class RenderTests
{
    [TestMethod]
    public Task IntegratedSimulatorSnapshotDrawsWithoutPerPointVisuals() => StaTest.Run(async () =>
    {
        var clock = new ManualClock();
        var runtime = await DesktopRuntime.CreateAsync(new WpfUiDispatcher(Dispatcher.CurrentDispatcher), clock: clock);
        var window = runtime.Host.Services.GetRequiredService<MainWindow>();
        string capturePath = Path.Combine(Path.GetTempPath(), $"UTStudio-Wpf-{Guid.NewGuid():N}.png");
        try
        {
            await DiagnosticWait.For(runtime.Host.StartAsync());
            var vm = (AScanViewModel)window.DataContext;
            var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            vm.PropertyChanged += (_, args) => { if (args.PropertyName == nameof(vm.AScan) && vm.AScan is not null) { received.TrySetResult(); } };
            await DiagnosticWait.For(vm.StartCommand.ExecuteAsync(null));
            await DiagnosticWait.For(received.Task, "first A-Scan in ViewModel");
            Assert.IsNotNull(vm.AScan);
            Assert.AreEqual(2048, vm.AScan.OriginalSampleCount);
            Assert.IsLessThanOrEqualTo(1024, vm.Points.Count);
            var control = (AScanControl)window.FindName("Scan");
            window.Show();
            control.Snapshot = vm.AScan;
            control.RefreshSnapshot();
            window.Measure(new Size(1100, 680));
            window.Arrange(new Rect(0, 0, 1100, 680));
            window.UpdateLayout();
            clock.Advance(TimeSpan.FromMilliseconds(200));
            window.UpdateStatus(null, EventArgs.Empty);
            window.UpdateLayout();
            var content = (FrameworkElement)window.Content;
            int pixelWidth = (int)Math.Ceiling(content.ActualWidth);
            int pixelHeight = (int)Math.Ceiling(content.ActualHeight);
            var bitmap = new RenderTargetBitmap(pixelWidth, pixelHeight, 96, 96, PixelFormats.Pbgra32);
            var capture = new DrawingVisual();
            using (var drawing = capture.RenderOpen())
            {
                var bounds = new Rect(0, 0, pixelWidth, pixelHeight);
                drawing.DrawRectangle(window.Background, null, bounds);
                drawing.DrawRectangle(new VisualBrush(content)
                {
                    ViewboxUnits = BrushMappingMode.Absolute,
                    Viewbox = new Rect(0, 0, content.ActualWidth, content.ActualHeight)
                }, null, bounds);
            }
            bitmap.Render(capture);
            Assert.AreEqual(0, VisualTreeHelper.GetChildrenCount(control));
            byte[] pixels = new byte[pixelWidth * pixelHeight * 4];
            bitmap.CopyPixels(pixels, pixelWidth * 4, 0);
            int curvePixels = 0;
            for (int i = 0; i < pixels.Length; i += 4)
            { if (pixels[i + 1] > 170 && pixels[i + 2] < 100 && pixels[i] > 120) { curvePixels++; } }
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (var file = File.Create(capturePath)) { encoder.Save(file); }
            Assert.IsGreaterThan(100, curvePixels);
            control.Measure(new Size(640, 300));
            control.Arrange(new Rect(0, 0, 640, 300));
            var resized = new RenderTargetBitmap(640, 300, 96, 96, PixelFormats.Pbgra32);
            resized.Render(control);
        }
        finally
        {
            try
            {
                if (window.IsVisible)
                {
                    var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    window.Closed += (_, _) => closed.TrySetResult();
                    window.Close();
                    await DiagnosticWait.For(closed.Task, "render test window closed through its lifecycle");
                }
                await DiagnosticWait.For(runtime.Shutdown.ShutdownAsync());
            }
            finally { File.Delete(capturePath); }
        }
        Assert.IsFalse(File.Exists(capturePath));
    });
}
