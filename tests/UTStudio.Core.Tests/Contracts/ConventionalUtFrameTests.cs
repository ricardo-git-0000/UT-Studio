using UTStudio.Contracts.Acquisition;
using UTStudio.Core.Tests.TestDoubles;
using UTStudio.Domain.Acquisition;

namespace UTStudio.Core.Tests.Contracts;

[TestClass]
public sealed class ConventionalUtFrameTests
{
    internal static ConventionalUtFrameMetadata Metadata(int count = 3) =>
        new(new UtSourceId("synthetic"), new AcquisitionRunId(Guid.NewGuid()),
            new ConventionalAcquisitionConfiguration(default, count, 50_000_000), DateTimeOffset.UnixEpoch);

    [TestMethod]
    public void SamplesExposeOnlyValidRfValuesWithoutCopying()
    {
        short[] samples = [short.MinValue, 0, short.MaxValue, 123];
        var owner = new CountingMemoryOwner(samples);
        ReadOnlyMemory<short> expectedMemory = owner.Memory[..3];
        using var frame = new ConventionalUtFrame(Metadata(), ulong.MaxValue, TimeSpan.Zero, owner);

        Assert.AreEqual(3, frame.Samples.Length);
        CollectionAssert.AreEqual(new short[] { short.MinValue, 0, short.MaxValue }, frame.Samples.ToArray());
        Assert.IsTrue(frame.Samples.Equals(expectedMemory));
        Assert.AreEqual(ulong.MaxValue, frame.Sequence);
        Assert.AreEqual(TimeSpan.Zero, frame.ElapsedSinceRunStart);
        Assert.AreEqual(0, owner.DisposeCount);
    }

    [TestMethod]
    public void ShortMemoryIsRejectedWithoutAdoptingOrDisposingOwner()
    {
        var owner = new CountingMemoryOwner([1, 2]);
        Assert.ThrowsExactly<ArgumentException>(() => new ConventionalUtFrame(Metadata(), 0, TimeSpan.Zero, owner));
        Assert.AreEqual(0, owner.DisposeCount);
        using (var valid = new ConventionalUtFrame(Metadata(2), 0, TimeSpan.Zero, owner))
        {
            Assert.AreEqual(2, valid.Samples.Length);
        }

        Assert.AreEqual(1, owner.DisposeCount);
    }

    [TestMethod]
    public void NullArgumentsAndNegativeElapsedDoNotTransferOwnership()
    {
        var owner = new CountingMemoryOwner([1, 2, 3]);
        Assert.ThrowsExactly<ArgumentNullException>(
            () => new ConventionalUtFrame(null!, 0, TimeSpan.Zero, owner));
        Assert.ThrowsExactly<ArgumentNullException>(
            () => new ConventionalUtFrame(Metadata(), 0, TimeSpan.Zero, null!));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => new ConventionalUtFrame(Metadata(), 0, TimeSpan.FromTicks(-1), owner));
        Assert.AreEqual(0, owner.DisposeCount);
        owner.Dispose();
        Assert.AreEqual(1, owner.DisposeCount);
    }

    [TestMethod]
    public void ThrowingMemoryGetterLeavesOwnershipWithCaller()
    {
        var owner = new CountingMemoryOwner([1, 2, 3]) { ThrowOnMemoryAccess = true };
        Assert.ThrowsExactly<InvalidOperationException>(
            () => new ConventionalUtFrame(Metadata(), 0, TimeSpan.Zero, owner));
        Assert.AreEqual(0, owner.DisposeCount);
        owner.Dispose();
        Assert.AreEqual(1, owner.DisposeCount);
    }

    [TestMethod]
    public void RepeatedDisposeReturnsOwnerOnceAndRejectsFurtherAccess()
    {
        var owner = new CountingMemoryOwner([1, 2, 3]);
        var metadata = Metadata();
        var frame = new ConventionalUtFrame(metadata, 0, TimeSpan.Zero, owner);
        frame.Dispose();
        frame.Dispose();

        Assert.AreEqual(1, owner.DisposeCount);
        Assert.ThrowsExactly<ObjectDisposedException>(() => _ = frame.Samples);
        Assert.AreSame(metadata, frame.Metadata);
    }

    [TestMethod]
    public async Task ConcurrentDisposeInvokesOwnerExactlyOnce()
    {
        var owner = new CountingMemoryOwner([1, 2, 3]);
        var frame = new ConventionalUtFrame(Metadata(), 0, TimeSpan.Zero, owner);
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tasks = Enumerable.Range(0, 32).Select(_ => Task.Run(async () =>
        {
            await start.Task;
            frame.Dispose();
        })).ToArray();

        start.SetResult();
        await Task.WhenAll(tasks);
        Assert.AreEqual(1, owner.DisposeCount);
        Assert.ThrowsExactly<ObjectDisposedException>(() => _ = frame.Samples);
    }

    [TestMethod]
    public void FailingOwnerDisposalPropagatesWithoutRetry()
    {
        var owner = new CountingMemoryOwner([1, 2, 3]) { ThrowOnDispose = true };
        var frame = new ConventionalUtFrame(Metadata(), 0, TimeSpan.Zero, owner);
        Assert.ThrowsExactly<InvalidOperationException>(() => frame.Dispose());
        frame.Dispose();

        Assert.AreEqual(1, owner.DisposeCount);
        Assert.ThrowsExactly<ObjectDisposedException>(() => _ = frame.Samples);
    }
}
