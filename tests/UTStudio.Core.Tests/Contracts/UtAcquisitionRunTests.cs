using System.Threading.Channels;
using UTStudio.Contracts.Acquisition;
using UTStudio.Core.Tests.TestDoubles;

namespace UTStudio.Core.Tests.Contracts;

[TestClass]
public sealed class UtAcquisitionRunTests
{
    [TestMethod]
    public void DescriptorRejectsNullDependencies()
    {
        var metadata = ConventionalUtFrameTests.Metadata();
        var channel = Channel.CreateBounded<ConventionalUtFrame>(1);
        Assert.ThrowsExactly<ArgumentNullException>(() => new UtAcquisitionRun(null!, channel.Reader, Task.CompletedTask, Task.CompletedTask));
        Assert.ThrowsExactly<ArgumentNullException>(() => new UtAcquisitionRun(metadata, null!, Task.CompletedTask, Task.CompletedTask));
        Assert.ThrowsExactly<ArgumentNullException>(() => new UtAcquisitionRun(metadata, channel.Reader, null!, Task.CompletedTask));
        Assert.ThrowsExactly<ArgumentNullException>(() => new UtAcquisitionRun(metadata, channel.Reader, Task.CompletedTask, null!));
    }

    [TestMethod]
    public async Task ProducerDrainageAndReleaseAreIndependentMilestones()
    {
        var metadata = ConventionalUtFrameTests.Metadata();
        var channel = Channel.CreateBounded<ConventionalUtFrame>(1);
        var producer = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var released = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var run = new UtAcquisitionRun(metadata, channel.Reader, producer.Task, released.Task);
        var owner = new CountingMemoryOwner([1, 2, 3]);
        using var frame = new ConventionalUtFrame(metadata, 0, TimeSpan.Zero, owner);

        Assert.AreSame(metadata, run.Metadata);
        Assert.AreSame(channel.Reader, run.Frames);
        Assert.AreSame(producer.Task, run.ProducerCompletion);
        Assert.AreSame(released.Task, run.AllFramesReleased);
        Assert.IsFalse(run.ProducerCompletion.IsCompleted);
        Assert.IsFalse(run.AllFramesReleased.IsCompleted);
        Assert.IsTrue(channel.Writer.TryWrite(frame));
        channel.Writer.Complete();
        producer.SetResult();
        await run.ProducerCompletion;

        Assert.IsFalse(run.Frames.Completion.IsCompleted);
        Assert.IsFalse(run.AllFramesReleased.IsCompleted);
        Assert.IsTrue(run.Frames.TryRead(out var received));
        Assert.AreSame(frame, received);
        await run.Frames.Completion;
        Assert.AreEqual(0, owner.DisposeCount);
        Assert.IsFalse(run.AllFramesReleased.IsCompleted);

        received.Dispose();
        Assert.AreEqual(1, owner.DisposeCount);
        // The future source supplies the barrier; the descriptor does not track leases.
        released.SetResult();
        await run.AllFramesReleased;
    }

    [TestMethod]
    public async Task SuppliedTaskFailureAndCancellationRemainObservable()
    {
        var channel = Channel.CreateBounded<ConventionalUtFrame>(1);
        var failure = new InvalidOperationException("Producer failed.");
        var run = new UtAcquisitionRun(ConventionalUtFrameTests.Metadata(), channel.Reader,
            Task.FromException(failure), Task.FromCanceled(new CancellationToken(true)));

        var observed = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => run.ProducerCompletion);
        Assert.AreSame(failure, observed);
        await Assert.ThrowsExactlyAsync<TaskCanceledException>(() => run.AllFramesReleased);
        Assert.IsFalse(run.Frames.Completion.IsCompleted);
    }
}
