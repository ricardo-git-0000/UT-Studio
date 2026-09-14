using UTStudio.Application;
using UTStudio.Contracts.Application;
using UTStudio.Domain.Acquisition;

namespace UTStudio.Core.Tests.Application;

[TestClass]
public sealed class ApplicationSessionPortTests
{
    [TestMethod]
    public async Task ContractReportsAdmissionThroughPendingReleaseAndHealthyStop()
    {
        await using var source = new ManualSessionSource { HoldReleaseBarrier = true };
        await using var implementation = new ApplicationSession(source);
        IApplicationSession session = implementation;
        try
        {
            Assert.IsTrue(session.Snapshot.CanStart);
            Assert.IsFalse(session.Snapshot.CanStop);
            await session.StartAsync(new(new PhysicalChannelId(0), 8, 50_000_000), new(Guid.NewGuid())).WaitAsync(TimeSpan.FromSeconds(10));
            Assert.IsFalse(session.Snapshot.CanStart);
            Assert.IsTrue(session.Snapshot.CanStop);
            var stop = session.StopAsync();
            await ApplicationSessionTests.WaitForPhaseAsync(implementation, SessionPhase.AwaitingFramesReleased);
            Assert.IsFalse(session.Snapshot.CanStart);
            Assert.IsFalse(session.Snapshot.CanStop);
            source.Released.TrySetResult();
            await stop.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.IsTrue(session.Snapshot.CanStart);
            Assert.IsFalse(session.Snapshot.CanStop);
            await implementation.DisposeAsync();
            Assert.IsFalse(session.Snapshot.CanStart);
            Assert.IsFalse(session.Snapshot.CanStop);
        }
        finally { source.Released.TrySetResult(); }
    }
}
