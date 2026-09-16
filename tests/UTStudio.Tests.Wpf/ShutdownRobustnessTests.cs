using System.Collections.Concurrent;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using UTStudio.App.Wpf;
using UTStudio.App.Wpf.Composition;
using UTStudio.App.Wpf.Services;
using UTStudio.Presentation;

namespace UTStudio.Tests.Wpf;

[TestClass]
public sealed class ShutdownRobustnessTests
{
    [TestMethod]
    [DataRow(false, false)]
    [DataRow(true, false)]
    [DataRow(true, true)]
    public async Task DiagnosticDisposalPreservesCleanupResult(bool timerFails, bool cleanupFails)
    {
        var clock = new DisposalClock(timerFails);
        var logger = new RecordingLogger();
        var primary = new InvalidOperationException("primary cleanup failure");
        int calls = 0;
        var coordinator = new ShutdownCoordinator([new("cleanup", () =>
        {
            calls++;
            return cleanupFails ? Task.FromException(primary) : Task.CompletedTask;
        })], logger, clock);
        Task shutdown = coordinator.ShutdownAsync();
        if (cleanupFails)
        {
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => DiagnosticWait.For(shutdown));
            Assert.AreSame(primary, error);
            Assert.AreEqual(primary.Message, coordinator.Status.Error);
        }
        else
        {
            await DiagnosticWait.For(shutdown);
            Assert.IsNull(coordinator.Status.Error);
        }
        Assert.AreEqual(!cleanupFails, coordinator.Status.Completed);
        Assert.AreEqual(1, calls);
        Assert.AreEqual(1, clock.Disposals);
        Assert.AreSame(shutdown, coordinator.ShutdownAsync());
        Assert.AreEqual(timerFails, logger.Entries.Any(entry => entry.Level == LogLevel.Warning &&
            entry.Error?.ToString().Contains("diagnostic disposal failure", StringComparison.Ordinal) == true));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public Task CompletedShutdownDefersOneFinalClose(bool timerFails) => StaTest.Run(async () =>
    {
        var runtime = await DesktopRuntime.CreateAsync(new WpfUiDispatcher(Dispatcher.CurrentDispatcher));
        var logger = new RecordingLogger();
        int cleanupCalls = 0;
        var coordinator = new ShutdownCoordinator([new("cleanup", () => { cleanupCalls++; return Task.CompletedTask; })],
            logger, new DisposalClock(timerFails));
        var window = CreateWindow(runtime, coordinator);
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        bool insideRequest = false, closedInsideRequest = false;
        int cancelled = 0, allowed = 0, closes = 0;
        window.Closing += (_, args) => { if (args.Cancel) { cancelled++; } else { allowed++; } };
        window.Closed += (_, _) => { closes++; closedInsideRequest = insideRequest; closed.TrySetResult(); };
        try
        {
            window.Show();
            Task shutdown = coordinator.ShutdownAsync();
            await DiagnosticWait.For(shutdown, "shutdown completed before first Closing");
            insideRequest = true;
            window.Close();
            window.Close();
            window.Close();
            Assert.IsTrue(window.IsVisible);
            Assert.IsFalse(closed.Task.IsCompleted);
            Assert.AreEqual(3, cancelled);
            Assert.AreEqual(0, allowed);
            insideRequest = false;
            await DiagnosticWait.For(closed.Task, "deferred final Closing on Dispatcher");
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Assert.IsFalse(closedInsideRequest);
            Assert.AreEqual(1, allowed);
            Assert.AreEqual(1, closes);
            Assert.AreEqual(1, cleanupCalls);
            Assert.AreSame(shutdown, coordinator.ShutdownAsync());
        }
        finally
        {
            insideRequest = false;
            ForceTestWindowCleanup(window);
            await DiagnosticWait.For(runtime.Shutdown.ShutdownAsync());
        }
    });

    [TestMethod]
    public Task FailedShutdownKeepsWindowVisibleAndDoesNotRepeatCleanup() => StaTest.Run(async () =>
    {
        var runtime = await DesktopRuntime.CreateAsync(new WpfUiDispatcher(Dispatcher.CurrentDispatcher));
        int calls = 0;
        var failure = new InvalidOperationException("unconfirmed cleanup");
        var coordinator = new ShutdownCoordinator([new("cleanup", () => { calls++; return Task.FromException(failure); })],
            NullLogger.Instance, new DisposalClock(true));
        var window = CreateWindow(runtime, coordinator);
        try
        {
            window.Show();
            window.Close();
            await DiagnosticWait.Until(() => ((TextBlock)window.FindName("ShutdownText")).Text.Contains(failure.Message),
                "shutdown failure displayed by MainWindow");
            window.Close();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Assert.IsTrue(window.IsVisible);
            Assert.IsFalse(coordinator.Status.Completed);
            Assert.AreEqual(1, calls);
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => DiagnosticWait.For(coordinator.ShutdownAsync()));
            Assert.AreSame(failure, error);
        }
        finally
        {
            ForceTestWindowCleanup(window);
            await DiagnosticWait.For(runtime.Shutdown.ShutdownAsync());
        }
    });

    private static MainWindow CreateWindow(DesktopRuntime runtime, ShutdownCoordinator coordinator) => new(
        runtime.Host.Services.GetRequiredService<AScanViewModel>(), coordinator,
        runtime.Host.Services.GetRequiredService<VisualMetrics>(), new ManualClock(), NullLogger<MainWindow>.Instance);

    // Test-only teardown after assertions, including the intentionally unconfirmed-failure case.
    private static void ForceTestWindowCleanup(MainWindow window)
    {
        if (!window.IsVisible) { return; }
        window.Closing += (_, args) => args.Cancel = false;
        window.Close();
    }

    private sealed class RecordingLogger : ILogger
    {
        internal ConcurrentQueue<(LogLevel Level, Exception? Error)> Entries { get; } = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Entries.Enqueue((logLevel, exception));
    }

    // Never fires: completion is driven by cleanup, cancellation disposes the diagnostic timer.
    private sealed class DisposalClock(bool fail) : TimeProvider
    {
        internal int Disposals;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) => new Timer(this, fail);
        private sealed class Timer(DisposalClock owner, bool fail) : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period) => throw new NotSupportedException();
            public void Dispose()
            {
                Interlocked.Increment(ref owner.Disposals);
                if (fail) { throw new InvalidOperationException("diagnostic disposal failure"); }
            }
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
}
