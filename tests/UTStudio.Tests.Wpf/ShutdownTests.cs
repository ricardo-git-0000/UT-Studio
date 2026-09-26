using Microsoft.Extensions.Logging.Abstractions;
using UTStudio.App.Wpf.Services;
using UTStudio.Presentation;

namespace UTStudio.Tests.Wpf;

[TestClass]
public sealed class ShutdownTests
{
    [TestMethod]
    public async Task CleanupOrderAndRepeatedCloseShareOneTask()
    {
        List<string> calls = [];
        var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var coordinator = new ShutdownCoordinator([
            new("block", async () => { calls.Add("block"); entered.SetResult(); await held.Task; }),
            Step("B", calls), Step("A", calls), Step("session", calls), Step("producer-consumer", calls),
            Step("workers-callbacks-owners", calls), Step("owned-components", calls),
            Step("host.stop", calls), new("host.dispose", () => { calls.Add("host.dispose"); return Task.CompletedTask; },
                RequiresConfirmedCleanup: true)
        ], NullLogger.Instance, new ManualClock());
        Task first = coordinator.ShutdownAsync();
        try
        {
            await DiagnosticWait.For(entered.Task, "first cleanup phase entered");
            Assert.AreSame(first, coordinator.ShutdownAsync());
            Assert.HasCount(1, calls);
            Assert.IsFalse(coordinator.Status.Completed);
        }
        finally { held.TrySetResult(); await DiagnosticWait.For(first, "cleanup after releasing held phase"); }
        CollectionAssert.AreEqual(new[] { "block", "B", "A", "session", "producer-consumer",
            "workers-callbacks-owners", "owned-components", "host.stop", "host.dispose" }, calls);
        Assert.IsTrue(coordinator.Status.Completed);
        Assert.AreSame(first, coordinator.ShutdownAsync());
    }

    [TestMethod]
    public async Task DiagnosticTimeoutDoesNotCancelOrReleaseHeldPhase()
    {
        var clock = new ManualClock();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        List<string> calls = [];
        var coordinator = new ShutdownCoordinator([
            new("drain", async () => { entered.SetResult(); await held.Task; }), Step("host", calls)
        ], NullLogger.Instance, clock);
        Task shutdown = coordinator.ShutdownAsync();
        try
        {
            await DiagnosticWait.For(entered.Task, "drain phase entered");
            clock.Advance(TimeSpan.FromSeconds(5));
            await DiagnosticWait.Until(() => coordinator.Status.DiagnosticTimeout, "five-second shutdown diagnostic");
            Assert.IsFalse(shutdown.IsCompleted);
            Assert.IsEmpty(calls);
        }
        finally { held.TrySetResult(); await DiagnosticWait.For(shutdown, "cleanup after drain released"); }
        Assert.IsTrue(coordinator.Status.Completed);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task UnconfirmedFailureStillRunsLaterDisposalAndRemainsIdempotent(bool hostFailure)
    {
        List<string> calls = [];
        var coordinator = new ShutdownCoordinator([
            new(hostFailure ? "host.stop" : "session", () => Task.FromException(new InvalidOperationException("failed"))),
            Step("must dispose", calls)
        ], NullLogger.Instance, new ManualClock());
        Task first = coordinator.ShutdownAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => DiagnosticWait.For(first));
        Assert.IsFalse(coordinator.Status.Completed);
        Assert.AreEqual("failed", coordinator.Status.Error);
        Assert.AreSame(first, coordinator.ShutdownAsync());
        Assert.HasCount(1, calls);
    }

    [TestMethod]
    public async Task HistoricalFailureWithConfirmedReleaseAllowsRemainingCleanup()
    {
        List<string> calls = [];
        var coordinator = new ShutdownCoordinator([
            new("session", () => Task.FromException(new InvalidOperationException("primary")), () => true),
            Step("host", calls)
        ], NullLogger.Instance, new ManualClock());
        await DiagnosticWait.For(coordinator.ShutdownAsync());
        Assert.IsTrue(coordinator.Status.Completed);
        Assert.AreEqual("primary", coordinator.Status.Error);
        Assert.HasCount(1, calls);
    }

    [TestMethod]
    public async Task UnconfirmedCleanupSkipsHostDisposalAndPreservesStructuredFailures()
    {
        List<string> calls = [];
        var primary = new InvalidOperationException("primary");
        var secondary = new InvalidOperationException("secondary");
        var coordinator = new ShutdownCoordinator([
            new("session", () => Task.FromException(primary)),
            new("visual", () => Task.FromException(secondary)),
            Step("host.stop", calls),
            new("host.dispose", () => { calls.Add("host.dispose"); return Task.CompletedTask; },
                RequiresConfirmedCleanup: true)
        ], NullLogger.Instance, new ManualClock());

        Exception error = await Assert.ThrowsAsync<InvalidOperationException>(() => DiagnosticWait.For(coordinator.ShutdownAsync()));

        Assert.AreSame(primary, error);
        Assert.IsFalse(coordinator.Status.Completed);
        Assert.AreEqual("session", coordinator.Status.PrimaryFailure!.Phase);
        Assert.AreSame(primary, coordinator.Status.PrimaryFailure.Error);
        Assert.HasCount(1, coordinator.Status.CleanupFailures);
        Assert.AreSame(secondary, coordinator.Status.CleanupFailures[0].Error);
        CollectionAssert.AreEqual(new[] { "host.stop" }, calls);
    }

    [TestMethod]
    public async Task NestedCleanupFailureIsFlattenedInGlobalPrimaryAndSecondaryOrder()
    {
        var primary = new InvalidOperationException("B primary");
        var secondary = new InvalidOperationException("B secondary");
        var viewModel = new CleanupFailureException("VM cleanup",
            new CleanupStageFailure("subscription", primary), [new("ui-barrier", secondary)]);
        var window = new CleanupFailureException("Window cleanup",
            new CleanupStageFailure("viewmodel", viewModel), []);
        var coordinator = new ShutdownCoordinator([
            new("B", () => Task.FromException(window))
        ], NullLogger.Instance, new ManualClock());

        Exception error = await Assert.ThrowsAsync<InvalidOperationException>(() => DiagnosticWait.For(coordinator.ShutdownAsync()));

        Assert.AreSame(primary, error);
        Assert.AreSame(primary, coordinator.Status.PrimaryFailure!.Error);
        Assert.AreEqual("B/viewmodel/subscription", coordinator.Status.PrimaryFailure.Phase);
        Assert.HasCount(1, coordinator.Status.CleanupFailures);
        Assert.AreSame(secondary, coordinator.Status.CleanupFailures[0].Error);
        Assert.AreEqual("B/viewmodel/ui-barrier", coordinator.Status.CleanupFailures[0].Phase);
    }

    private static ShutdownStep Step(string name, List<string> calls) => new(name, () => { calls.Add(name); return Task.CompletedTask; });
}
