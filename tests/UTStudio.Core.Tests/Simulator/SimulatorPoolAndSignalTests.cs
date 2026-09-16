using UTStudio.Acquisition.Simulator;
using UTStudio.Domain.Acquisition;
using UTStudio.Core.Tests.TestDoubles;

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
        using var first = await DiagnosticWait.For(pool.RentAsync(default).AsTask());
        var memory = first.Memory;
        first.Dispose();
        Assert.IsFalse(pool.AllFramesReleased.IsCompleted);
        using var second = await DiagnosticWait.For(pool.RentAsync(default).AsTask());
        Assert.IsTrue(memory.Equals(second.Memory));
        first.Dispose();
        Assert.AreEqual(1, pool.Outstanding);
        pool.Seal();
        pool.Seal();
        Assert.IsFalse(pool.AllFramesReleased.IsCompleted);
        second.Dispose();
        second.Dispose();
        await DiagnosticWait.For(pool.AllFramesReleased);
        Assert.AreEqual(0, pool.Outstanding);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => DiagnosticWait.For(pool.RentAsync(default).AsTask()));
    }

    [TestMethod]
    public async Task PoolWaitObservesCancellationAndSeal()
    {
        var pool = new BoundedSampleBufferPool(1, 2);
        using var owner = await DiagnosticWait.For(pool.RentAsync(default).AsTask());
        using var cancellation = new CancellationTokenSource();
        var pending = pool.RentAsync(cancellation.Token).AsTask();
        Assert.IsFalse(pending.IsCompleted);
        cancellation.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => DiagnosticWait.For(pending, "pool wait observes caller cancellation"));
        var sealedWait = pool.RentAsync(default).AsTask();
        Assert.IsFalse(sealedWait.IsCompleted);
        pool.Seal();
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => DiagnosticWait.For(sealedWait, "pool wait observes sealing"));
        Assert.IsFalse(pool.AllFramesReleased.IsCompleted);
        owner.Dispose();
        await DiagnosticWait.For(pool.AllFramesReleased);
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
                await DiagnosticWait.For(start.Task, "rent/seal race start signal");
                try { return await pool.RentAsync(default); }
                catch (InvalidOperationException) { return null; }
            });
            var seal = Task.Run(async () => { await DiagnosticWait.For(start.Task, "seal start signal"); pool.Seal(); });
            start.SetResult();
            try
            {
                await DiagnosticWait.For(seal);
                using var owner = await DiagnosticWait.For(rent);
                if (owner is not null) { Assert.IsFalse(pool.AllFramesReleased.IsCompleted); }
            }
            finally
            {
                pool.Seal();
                // Even if the diagnostic wait failed, a later successful rent still returns its owner.
                _ = rent.ContinueWith(completed =>
                {
                    if (completed.IsCompletedSuccessfully) { completed.Result?.Dispose(); }
                    else { _ = completed.Exception; }
                },
                    CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }
            await DiagnosticWait.For(pool.AllFramesReleased, "sealed racing pool has no outstanding loans");
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
