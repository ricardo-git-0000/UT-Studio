using UTStudio.Acquisition.Simulator;
using UTStudio.Domain.Acquisition;

namespace UTStudio.Core.Tests.Simulator;

[TestClass]
public sealed class SimulatorPoolAndSignalTests
{
    [TestMethod]
    public void OptionsValidateCadenceCapacityAndNoise()
    {
        var defaults = new SimulatorOptions();
        Assert.AreEqual(4, defaults.ChannelCapacity);
        Assert.AreEqual(8, defaults.BufferCount);
        Assert.AreEqual(TimeSpan.FromMilliseconds(10), defaults.Period);
        foreach (var rate in new[] { 0, -1, 101, double.NaN, double.PositiveInfinity, double.Epsilon })
        {
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new SimulatorOptions(maxAScansPerSecond: rate));
        }

        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new SimulatorOptions(channelCapacity: 0));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new SimulatorOptions(bufferCount: 7));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new SimulatorOptions(channelCapacity: int.MaxValue));
        foreach (var noise in new[] { -1, 0.02, double.NaN, double.PositiveInfinity })
        {
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new SimulatorOptions(noiseAmplitude: noise));
        }
    }

    [TestMethod]
    public async Task SealAndLastReturnControlBarrierAndOldLeaseCannotReturnNewLoan()
    {
        var pool = new BoundedSampleBufferPool(1, 2);
        Assert.IsFalse(pool.AllFramesReleased.IsCompleted);
        var first = await pool.RentAsync(default);
        var memory = first.Memory;
        first.Dispose();
        Assert.IsFalse(pool.AllFramesReleased.IsCompleted);
        var second = await pool.RentAsync(default);
        Assert.IsTrue(memory.Equals(second.Memory));
        first.Dispose();
        Assert.AreEqual(1, pool.Outstanding);
        pool.Seal();
        pool.Seal();
        Assert.IsFalse(pool.AllFramesReleased.IsCompleted);
        second.Dispose();
        second.Dispose();
        await pool.AllFramesReleased;
        Assert.AreEqual(0, pool.Outstanding);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => pool.RentAsync(default).AsTask());
    }

    [TestMethod]
    public async Task PoolWaitObservesCancellationAndSeal()
    {
        var pool = new BoundedSampleBufferPool(1, 2);
        using var owner = await pool.RentAsync(default);
        using var cancellation = new CancellationTokenSource();
        var pending = pool.RentAsync(cancellation.Token).AsTask();
        Assert.IsFalse(pending.IsCompleted);
        cancellation.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => pending);
        var sealedWait = pool.RentAsync(default).AsTask();
        Assert.IsFalse(sealedWait.IsCompleted);
        pool.Seal();
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => sealedWait);
        Assert.IsFalse(pool.AllFramesReleased.IsCompleted);
        owner.Dispose();
        await pool.AllFramesReleased;
    }

    [TestMethod]
    public async Task RacingSealAndRentNeverCompletesWithOutstandingLoans()
    {
        for (int i = 0; i < 30; i++)
        {
            var pool = new BoundedSampleBufferPool(1, 2);
            var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var rent = Task.Run(async () =>
            {
                await start.Task;
                try { return await pool.RentAsync(default); }
                catch (InvalidOperationException) { return null; }
            });
            var seal = Task.Run(async () => { await start.Task; pool.Seal(); });
            start.SetResult();
            await seal;
            var owner = await rent;
            if (owner is not null)
            {
                Assert.IsFalse(pool.AllFramesReleased.IsCompleted);
                owner.Dispose();
            }

            await pool.AllFramesReleased;
            Assert.AreEqual(0, pool.Outstanding);
        }
    }

    [TestMethod]
    public void RfEchoShapeAndQuantizationAreKnownWithoutNoise()
    {
        var configuration = SimulatorUtFrameSource.DefaultConfiguration;
        var samples = new short[configuration.SampleCount];
        SyntheticRfGenerator.Fill(samples, configuration, new SimulatorOptions(noiseAmplitude: 0), 0, default);
        Assert.AreEqual(0, samples[500]); // First pulse center at 10 us.
        Assert.IsTrue(samples[502] > 18000 && samples[502] < 20000);
        Assert.IsTrue(samples[498] < -18000 && samples[498] > -20000);
        Assert.IsTrue(samples[1252] > 9000 && samples[1252] < 10000);
        Assert.AreEqual(0, samples[0]);
        Assert.ThrowsExactly<OperationCanceledException>(() =>
            SyntheticRfGenerator.Fill(samples, configuration, new SimulatorOptions(), 0, new CancellationToken(true)));
    }
}
