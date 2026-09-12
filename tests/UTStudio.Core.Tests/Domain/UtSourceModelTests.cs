using UTStudio.Domain.Acquisition;

namespace UTStudio.Core.Tests.Domain;

[TestClass]
public sealed class UtSourceModelTests
{
    [TestMethod]
    public void CapabilitiesCopyChannelsAndExposeReadOnlyCollection()
    {
        var channels = new[] { new PhysicalChannelId(0), new PhysicalChannelId(7) };
        var capabilities = new UtSourceCapabilities(channels, 2048, 50_000_000, true);
        channels[0] = new PhysicalChannelId(8);

        Assert.AreEqual(new PhysicalChannelId(0), capabilities.PhysicalChannels[0]);
        Assert.ThrowsExactly<NotSupportedException>(
            () => ((IList<PhysicalChannelId>)capabilities.PhysicalChannels)[0] = new PhysicalChannelId(9));
        Assert.AreEqual(2048, capabilities.MaxSampleCount);
        Assert.AreEqual(50_000_000d, capabilities.MaxSampleRateHz);
        Assert.IsTrue(capabilities.CanPauseProduction);
    }

    [TestMethod]
    public void CapabilitiesRejectMissingDuplicateChannelsAndInvalidLimits()
    {
        Assert.ThrowsExactly<ArgumentNullException>(() => new UtSourceCapabilities(null!, 1, 1, false));
        Assert.ThrowsExactly<ArgumentException>(() => new UtSourceCapabilities([], 1, 1, false));
        Assert.ThrowsExactly<ArgumentException>(
            () => new UtSourceCapabilities([default, default], 1, 1, false));
        foreach (var count in new[] { 0, -1, 65_536 })
        {
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(
                () => new UtSourceCapabilities([default], count, 1, false));
        }

        foreach (var rate in new[] { 0, -1, 100_000_001, double.NaN, double.PositiveInfinity, double.NegativeInfinity })
        {
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(
                () => new UtSourceCapabilities([default], 1, rate, false));
        }

        var maximum = new UtSourceCapabilities([default], 65_535, 100_000_000, false);
        Assert.AreEqual(65_535, maximum.MaxSampleCount);
    }

    [TestMethod]
    public void StateRejectsUnknownEnumsAndRunningWithoutConnection()
    {
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => new UtSourceState(0, (UtConnectionState)99, UtAcquisitionState.Idle));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => new UtSourceState(0, UtConnectionState.Connected, (UtAcquisitionState)99));
        foreach (var state in Enum.GetValues<UtConnectionState>())
        {
            if (state == UtConnectionState.Connected)
            {
                continue;
            }

            Assert.ThrowsExactly<ArgumentException>(() => new UtSourceState(0, state, UtAcquisitionState.Running));
        }

        var running = new UtSourceState(1, UtConnectionState.Connected, UtAcquisitionState.Running);
        Assert.AreEqual(1UL, running.Version);
        Assert.AreEqual(UtAcquisitionState.Running, running.Acquisition);
    }

    [TestMethod]
    public void StatePreservesPrimaryErrorAndCopiesCleanupErrors()
    {
        var primary = new UtSourceError("read.failed", "Read failed.");
        var cleanup = new UtSourceError("release.failed", "Release failed.");
        var input = new[] { cleanup };
        var state = new UtSourceState(2, UtConnectionState.Connected, UtAcquisitionState.Faulted, primary, input);
        input[0] = primary;

        Assert.AreSame(primary, state.PrimaryError);
        Assert.AreSame(cleanup, state.CleanupErrors[0]);
        Assert.ThrowsExactly<NotSupportedException>(() => ((IList<UtSourceError>)state.CleanupErrors).Clear());
        Assert.ThrowsExactly<ArgumentException>(
            () => new UtSourceState(0, UtConnectionState.Disconnected, UtAcquisitionState.Idle, cleanupErrors: [null!]));
        Assert.IsEmpty(new UtSourceState(0, UtConnectionState.Disconnected, UtAcquisitionState.Idle).CleanupErrors);
    }

    [TestMethod]
    public void OperationalErrorRequiresCodeAndMessage()
    {
        Assert.ThrowsExactly<ArgumentNullException>(() => new UtSourceError(null!, "message"));
        Assert.ThrowsExactly<ArgumentNullException>(() => new UtSourceError("code", null!));
        Assert.ThrowsExactly<ArgumentException>(() => new UtSourceError(" ", "message"));
        Assert.ThrowsExactly<ArgumentException>(() => new UtSourceError("code", ""));
    }
}
