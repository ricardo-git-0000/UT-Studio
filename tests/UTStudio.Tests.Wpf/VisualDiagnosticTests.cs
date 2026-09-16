using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using UTStudio.App.Wpf;
using UTStudio.App.Wpf.Composition;
using UTStudio.App.Wpf.Services;
using UTStudio.Contracts.Application;
using UTStudio.Presentation;
using UTStudio.Visualization.Core;

namespace UTStudio.Tests.Wpf;

[TestClass]
public sealed class VisualDiagnosticTests
{
    [TestMethod]
    public Task PublisherErrorIsBoundInWpfWithoutAnotherAScanOrErrorPolling() => StaTest.Run(async () =>
    {
        var clock = new ManualClock();
        var runtime = await DesktopRuntime.CreateAsync(new WpfUiDispatcher(Dispatcher.CurrentDispatcher), clock: clock);
        var window = runtime.Host.Services.GetRequiredService<MainWindow>();
        var vm = (AScanViewModel)window.DataContext;
        var delivery = runtime.Host.Services.GetRequiredService<AScanVisualDelivery>();
        var session = runtime.Host.Services.GetRequiredService<IApplicationSession>();
        try
        {
            await DiagnosticWait.For(runtime.Host.StartAsync());
            window.Show();
            await DiagnosticWait.For(vm.StartCommand.ExecuteAsync(null));
            await DiagnosticWait.Until(() => vm.AScan is not null && clock.RegisteredTimers > 0,
                "first curve and simulator waiting on its manual timer");
            var first = vm.AScan!;
            clock.FailNextTimer = true;
            // Source remains paused at its manual timer. This second input fails the publisher's timer.
            delivery.Accept(first.Metadata, first.Sequence + 1, TimeSpan.FromTicks(1), new short[2048]);
            var text = (TextBlock)window.FindName("VisualErrorText");
            await DiagnosticWait.Until(() => text.Text.Contains("Synthetic clock failure", StringComparison.Ordinal),
                "terminal visual error visible through WPF binding");
            Assert.AreEqual(1L, delivery.Statistics.Published);
            Assert.AreEqual(SessionPhase.Running, session.Snapshot.Phase);
            Assert.IsNull(session.Snapshot.PrimaryError);
            Assert.IsNull(vm.AScan);
        }
        finally
        {
            if (window.IsVisible)
            {
                var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                window.Closed += (_, _) => closed.TrySetResult();
                window.Close();
                await DiagnosticWait.For(closed.Task, "diagnostic test window closed through its lifecycle");
            }
            await DiagnosticWait.For(runtime.Shutdown.ShutdownAsync());
        }
    });
}
