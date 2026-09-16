using UTStudio.Contracts.Application;
using UTStudio.Core.Tests.Simulator;
using UTStudio.Core.Tests.TestDoubles;
using UTStudio.Domain.Acquisition;
using UTStudio.Presentation;
using UTStudio.Visualization.Core;

namespace UTStudio.Core.Tests.Presentation;

[TestClass]
public sealed class VisualDiagnosticTests
{
    [TestMethod]
    public async Task TerminalPublisherErrorReachesViewModelWithoutAnotherSnapshot()
    {
        var clock = new ManualSimulatorTimeProvider();
        await using var delivery = new AScanVisualDelivery(timeProvider: clock);
        var ui = new ManualUiDispatcher();
        var session = new ManualApplicationSession();
        var configuration = new ConventionalAcquisitionConfiguration(default, 8, 50_000_000);
        var run = new AcquisitionRunId(Guid.NewGuid());
        var metadata = new ConventionalUtFrameMetadata(ManualApplicationSession.Source, run, configuration, DateTimeOffset.UnixEpoch);
        var vm = new AScanViewModel(session, delivery, ui, configuration, visualStatus: delivery.StatusChanges);
        bool notificationsOnUi = true;
        vm.PropertyChanged += (_, _) => notificationsOnUi &= ui.CheckAccess();
        try
        {
            session.Set(ManualApplicationSession.Create(1, SessionPhase.Running, run, canStop: true));
            delivery.OpenRun(run);
            delivery.Accept(metadata, 0, TimeSpan.Zero, new short[8]);
            await ui.DriveUntilAsync(() => vm.AScan is not null);
            clock.FailTimerCreation = true;
            delivery.Accept(metadata, 1, TimeSpan.FromTicks(1), new short[8]);
            await ui.DriveUntilAsync(() => vm.VisualError is not null);
            Assert.AreEqual("visual.publication", vm.VisualError!.Code);
            Assert.AreEqual(1L, delivery.Statistics.Published);
            Assert.IsNull(vm.AScan);
            Assert.AreEqual(SessionPhase.Running, vm.Session.Phase);
            Assert.IsNull(vm.Session.PrimaryError);
            Assert.AreEqual(0, session.StopCalls);
            Assert.IsTrue(notificationsOnUi);

            var late = new ManualObservable<AScanSnapshot>();
            var replay = new AScanViewModel(session, late, ui, configuration, visualStatus: delivery.StatusChanges);
            try { await ui.DriveUntilAsync(() => replay.VisualError?.Code == "visual.publication"); }
            finally { await ui.DriveAsync(replay.DisposeAsync().AsTask()); }
        }
        finally { await ui.DriveAsync(vm.DisposeAsync().AsTask()); }
    }

    [TestMethod]
    public async Task StatusSubscriptionIsReleasedAndLateOrOlderStatusCannotMutateClosedViewModel()
    {
        var ui = new ManualUiDispatcher();
        var session = new ManualApplicationSession();
        var frames = new ManualObservable<AScanSnapshot>();
        var statuses = new ManualObservable<AScanDeliveryStatus>();
        var vm = new AScanViewModel(session, frames, ui, new(default, 8, 50_000_000), visualStatus: statuses);
        statuses.Emit(new(2, new("visual.publication", "failed")));
        await ui.DriveUntilAsync(() => vm.VisualError is not null);
        statuses.Emit(new(1, null));
        Assert.AreEqual("failed", vm.VisualError!.Message);
        await ui.DriveAsync(vm.DisposeAsync().AsTask());
        await ui.DriveAsync(vm.DisposeAsync().AsTask());
        Assert.AreEqual(1, statuses.Unsubscriptions);
        var state = vm.State;
        statuses.LastObserver!.OnNext(new(3, new("late", "must be ignored")));
        while (ui.RunNext()) { }
        Assert.AreSame(state, vm.State);
    }
}
