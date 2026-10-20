using UTStudio.Application;
using UTStudio.Contracts.Processing;
using UTStudio.Core.Tests.Processing;
using UTStudio.Domain.Inspection;
using UTStudio.Domain.Processing;
using UTStudio.Processing.Core;

namespace UTStudio.Core.Tests.Application;

[TestClass]
public sealed class InMemoryGatePlanAuthorityTests
{
    private static readonly InspectionId Inspection = new(Guid.Parse("81000000-0000-0000-0000-000000000001"));

    [TestMethod]
    public void RecoveringRequiresSealAndSealIrreversiblyInvalidatesCapability()
    {
        CommittedGatePlanSnapshot one = Committed("plan", 1);
        using var owner = new InMemoryGatePlanAuthorityOwner([one]);
        Assert.ThrowsExactly<InvalidOperationException>(() => _ = owner.Reader);
        using var recovery = owner.TakeRecoveryCapability();
        Assert.ThrowsExactly<InvalidOperationException>(() => owner.TakeRecoveryCapability());
        GatePlanRecoveryInstallResult installed = recovery.InstallRecovered(Committed("plan", 2));
        Assert.AreEqual(GatePlanRecoveryInstallStatus.Installed, installed.Status);
        GatePlanAuthoritySnapshot two = installed.Installed!;
        Assert.AreEqual(2UL, two.Generation.Value);
        IGatePlanAuthorityReader reader = owner.Seal();
        Assert.AreSame(reader, owner.Reader);
        Assert.ThrowsExactly<InvalidOperationException>(() => owner.Seal());
        Assert.AreEqual(GatePlanRecoveryInstallStatus.OwnerSealed,
            recovery.InstallRecovered(Committed("plan", 3)).Status);
        Assert.AreSame(two, reader.Read(one.AuthorityKey));
    }

    [TestMethod]
    public void RecoveryRejectsUnknownEqualInferiorDisposedAndOverflowWithoutPartialState()
    {
        CommittedGatePlanSnapshot one = Committed("plan", 2);
        var owner = new InMemoryGatePlanAuthorityOwner([one], new(ulong.MaxValue));
        using var recovery = owner.TakeRecoveryCapability();
        Assert.AreEqual(GatePlanRecoveryInstallStatus.IdentityNotBootstrapped,
            recovery.InstallRecovered(Committed("other", 3)).Status);
        Assert.AreEqual(GatePlanRecoveryInstallStatus.RevisionNotIncreasing,
            recovery.InstallRecovered(Committed("plan", 2)).Status);
        Assert.AreEqual(GatePlanRecoveryInstallStatus.RevisionNotIncreasing,
            recovery.InstallRecovered(Committed("plan", 1)).Status);
        Assert.AreEqual(GatePlanRecoveryInstallStatus.GenerationExhausted,
            recovery.InstallRecovered(Committed("plan", 3)).Status);
        IGatePlanAuthorityReader reader = owner.Seal();
        Assert.AreEqual(ulong.MaxValue, reader.Read(one.AuthorityKey).Generation.Value);
        owner.Dispose(); owner.Dispose();
        Assert.AreEqual(GatePlanRecoveryInstallStatus.OwnerClosingOrDisposed,
            recovery.InstallRecovered(Committed("plan", 3)).Status);
        Assert.ThrowsExactly<ObjectDisposedException>(() => reader.Read(one.AuthorityKey));
    }

    [TestMethod]
    public void DisposeDuringRecoveringIsTerminal()
    {
        CommittedGatePlanSnapshot one = Committed("plan", 1);
        var owner = new InMemoryGatePlanAuthorityOwner([one]);
        using var recovery = owner.TakeRecoveryCapability();
        owner.Dispose(); owner.Dispose();
        Assert.AreEqual(GatePlanRecoveryInstallStatus.OwnerClosingOrDisposed,
            recovery.InstallRecovered(Committed("plan", 2)).Status);
        Assert.ThrowsExactly<InvalidOperationException>(() => owner.Seal());
        Assert.ThrowsExactly<InvalidOperationException>(() => _ = owner.Reader);
    }

    [TestMethod]
    public void ReadStampCapturesExactIdentitySequenceAndCreatesNoPendingResource()
    {
        CommittedGatePlanSnapshot one = Committed("plan", 1);
        using var owner = new InMemoryGatePlanAuthorityOwner([one]);
        IGatePlanAuthorityReader reader = owner.Seal();
        GatePlanAuthoritySnapshot current = reader.Read(one.AuthorityKey);
        GatePlanAuthorityReadStampResult acquired = reader.TryReadStamp(one.AuthorityKey, current.Generation);
        Assert.AreEqual(GatePlanAuthorityReadStampStatus.Acquired, acquired.Status);
        Assert.AreSame(current, acquired.Stamp.Snapshot);
        Assert.AreEqual(one.AuthorityKey, acquired.Stamp.Authority);
        Assert.AreEqual(current.Generation, acquired.Stamp.Generation);
        Assert.AreEqual(current.PublicationSequence, acquired.Stamp.PublicationSequence);
        Assert.AreEqual(GatePlanAuthorityReadStampStatus.NotFound, reader.TryReadStamp(
            new(Inspection, new("missing")), current.Generation).Status);
        Assert.ThrowsExactly<ArgumentException>(() => reader.TryReadStamp(one.AuthorityKey, default));
        Assert.AreEqual(GatePlanAuthorityReadStampStatus.GenerationChanged,
            reader.TryReadStamp(one.AuthorityKey, new(current.Generation.Value + 1)).Status);
        owner.Dispose();
        Assert.AreEqual(GatePlanAuthorityReadStampStatus.Closed,
            reader.TryReadStamp(one.AuthorityKey, current.Generation).Status);
    }

    [TestMethod]
    public async Task DisposeCompletesAutomaticallyWithoutExternalReadOwnership()
    {
        CommittedGatePlanSnapshot one = Committed("plan", 1);
        var owner = new InMemoryGatePlanAuthorityOwner([one]);
        IGatePlanAuthorityReader reader = owner.Seal();
        owner.Dispose();
        await owner.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.ThrowsExactly<ObjectDisposedException>(() => reader.Read(one.AuthorityKey));
    }

    [TestMethod]
    public void ReaderIsNeutralImmutableIsolatedAndHasNoMutationSurface()
    {
        CommittedGatePlanSnapshot first = Committed("first", 1);
        CommittedGatePlanSnapshot second = Committed("second", 1);
        using var owner = new InMemoryGatePlanAuthorityOwner([first, second]);
        IGatePlanAuthorityReader reader = owner.Seal();
        Assert.AreSame(first, reader.Read(first.AuthorityKey).Committed);
        Assert.AreSame(second, reader.Read(second.AuthorityKey).Committed);
        Assert.IsFalse(typeof(IGatePlanAuthorityReader).GetMethods().Any(method =>
            method.Name.Contains("Restore", StringComparison.Ordinal) || method.Name.Contains("Set", StringComparison.Ordinal) ||
            method.Name.Contains("Update", StringComparison.Ordinal)));
        Assert.ThrowsExactly<NotSupportedException>(() => ((IList<GateDefinition>)first.Plan.Gates)
            .Add(GateTestPlan.Gate("external")));
    }

    [TestMethod]
    public async Task SlowObserverDoesNotBlockSubscribeDisposeOrAnotherObserver()
    {
        CommittedGatePlanSnapshot one = Committed("plan", 1);
        var owner = new InMemoryGatePlanAuthorityOwner([one]);
        IGatePlanAuthorityReader reader = owner.Seal();
        var slowEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSlow = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var slowCompleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using IDisposable slow = reader.Observe(one.AuthorityKey).Subscribe(new Observer<GatePlanAuthoritySnapshot>(
            _ => { slowEntered.TrySetResult(); releaseSlow.Task.GetAwaiter().GetResult(); },
            () => slowCompleted.TrySetResult()));
        var fastInitial = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var fastCompleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using IDisposable fast = reader.Observe(one.AuthorityKey).Subscribe(new Observer<GatePlanAuthoritySnapshot>(
            _ => fastInitial.TrySetResult(), () => fastCompleted.TrySetResult()));
        await slowEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await fastInitial.Task.WaitAsync(TimeSpan.FromSeconds(5));
        owner.Dispose();
        await fastCompleted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsExactlyAsync<TaskCanceledException>(() => owner.WaitForCloseAsync(cancellation.Token));
        releaseSlow.TrySetResult();
        await slowCompleted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await owner.WaitForCloseAsync().WaitAsync(TimeSpan.FromSeconds(5));
    }

    [TestMethod]
    public void CapabilityLifecycleOrdersHaveExactTypedOutcomes()
    {
        CommittedGatePlanSnapshot one = Committed("plan", 1);
        using (var owner = new InMemoryGatePlanAuthorityOwner([one]))
        {
            var capability = owner.TakeRecoveryCapability(); capability.Dispose();
            Assert.AreEqual(GatePlanRecoveryInstallStatus.CapabilityDisposed,
                capability.InstallRecovered(Committed("plan", 2)).Status);
        }
        using (var owner = new InMemoryGatePlanAuthorityOwner([one]))
        using (var capability = owner.TakeRecoveryCapability())
        {
            Assert.AreEqual(GatePlanRecoveryInstallStatus.Installed,
                capability.InstallRecovered(Committed("plan", 2)).Status);
            capability.Dispose();
        }
        using (var owner = new InMemoryGatePlanAuthorityOwner([one]))
        using (var capability = owner.TakeRecoveryCapability())
        {
            owner.Seal();
            Assert.AreEqual(GatePlanRecoveryInstallStatus.OwnerSealed,
                capability.InstallRecovered(Committed("plan", 2)).Status);
        }
        using (var owner = new InMemoryGatePlanAuthorityOwner([one]))
        using (var capability = owner.TakeRecoveryCapability())
        {
            Assert.AreEqual(GatePlanRecoveryInstallStatus.Installed,
                capability.InstallRecovered(Committed("plan", 2)).Status);
            Assert.IsNotNull(owner.Seal());
        }
        using (var owner = new InMemoryGatePlanAuthorityOwner([one]))
        using (var capability = owner.TakeRecoveryCapability())
        {
            owner.Dispose();
            Assert.AreEqual(GatePlanRecoveryInstallStatus.OwnerClosingOrDisposed,
                capability.InstallRecovered(Committed("plan", 2)).Status);
        }
        using (var owner = new InMemoryGatePlanAuthorityOwner([one]))
        using (var capability = owner.TakeRecoveryCapability())
        {
            Assert.AreEqual(GatePlanRecoveryInstallStatus.Installed,
                capability.InstallRecovered(Committed("plan", 2)).Status);
            owner.Dispose();
        }
    }

    private static CommittedGatePlanSnapshot Committed(string planId, uint revision)
    {
        GatePlanCompilationResult result = new GatePlanCompiler().Compile(
            GateTestPlan.Plan([GateTestPlan.Gate("A")], planId, revision));
        Assert.IsTrue(result.IsCompiled);
        return new(Inspection, result.CompiledPlan!);
    }
    private sealed class Observer<T>(Action<T> next, Action? completed = null) : IObserver<T>
    {
        public void OnNext(T value) => next(value);
        public void OnError(Exception error) => Assert.Fail(error.ToString());
        public void OnCompleted() => completed?.Invoke();
    }
}
