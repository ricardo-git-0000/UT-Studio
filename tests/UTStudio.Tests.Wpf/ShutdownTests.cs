using Microsoft.Extensions.Logging.Abstractions;
using UTStudio.App.Wpf.Services;

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
            new("VM/session", async () => { calls.Add("VM/session"); entered.SetResult(); await held.Task; }),
            Step("source", calls), Step("visual", calls), Step("host.stop", calls), Step("host.dispose", calls)
        ], NullLogger.Instance, new ManualClock());
        Task first = coordinator.ShutdownAsync();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.AreSame(first, coordinator.ShutdownAsync());
        Assert.HasCount(1, calls);
        Assert.IsFalse(coordinator.Status.Completed);
        held.SetResult();
        await first;
        CollectionAssert.AreEqual(new[] { "VM/session", "source", "visual", "host.stop", "host.dispose" }, calls);
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
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        clock.Advance(TimeSpan.FromSeconds(5));
        while (!coordinator.Status.DiagnosticTimeout) { await Task.Yield(); }
        Assert.IsFalse(shutdown.IsCompleted);
        Assert.IsEmpty(calls);
        held.SetResult();
        await shutdown;
        Assert.IsTrue(coordinator.Status.Completed);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task UnconfirmedFailureBlocksLaterDisposalAndRemainsIdempotent(bool hostFailure)
    {
        List<string> calls = [];
        var coordinator = new ShutdownCoordinator([
            new(hostFailure ? "host.stop" : "session", () => Task.FromException(new InvalidOperationException("failed"))),
            Step("must not dispose", calls)
        ], NullLogger.Instance, new ManualClock());
        Task first = coordinator.ShutdownAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => first);
        Assert.IsFalse(coordinator.Status.Completed);
        Assert.AreEqual("failed", coordinator.Status.Error);
        Assert.AreSame(first, coordinator.ShutdownAsync());
        Assert.IsEmpty(calls);
    }

    [TestMethod]
    public async Task HistoricalFailureWithConfirmedReleaseAllowsRemainingCleanup()
    {
        List<string> calls = [];
        var coordinator = new ShutdownCoordinator([
            new("session", () => Task.FromException(new InvalidOperationException("primary")), () => true),
            Step("host", calls)
        ], NullLogger.Instance, new ManualClock());
        await coordinator.ShutdownAsync();
        Assert.IsTrue(coordinator.Status.Completed);
        Assert.AreEqual("primary", coordinator.Status.Error);
        Assert.HasCount(1, calls);
    }

    private static ShutdownStep Step(string name, List<string> calls) => new(name, () => { calls.Add(name); return Task.CompletedTask; });
}
