using System.Collections.Concurrent;
using System.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UTStudio.Contracts.Application;
using UTStudio.Domain.Acquisition;
using UTStudio.Presentation;
using UTStudio.Visualization.Core;

namespace UTStudio.Core.Tests.Presentation;

[TestClass]
public sealed class AScanViewModelTests
{
    private static readonly ConventionalAcquisitionConfiguration Configuration = new(new PhysicalChannelId(0), 8, 50_000_000);
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static Task Watch(Task task) => task.WaitAsync(TimeSpan.FromSeconds(10));
    private static AcquisitionRunId NewRun() => new(Guid.NewGuid());
    private static AScanSnapshot Visual(AcquisitionRunId run, ulong version, ulong sequence = 0) => new AScanProjector().Project(
        new(ManualApplicationSession.Source, run, Configuration, DateTimeOffset.UnixEpoch), sequence, TimeSpan.Zero,
        new short[Configuration.SampleCount], version);

    [TestMethod]
    public void SharedTimeViewportAppliesOrderedIntentionsOnlyOnUi()
    {
        var ui = new ManualUiDispatcher();
        var shared = new SharedScanTimeViewport(ui);
        var notifications = new ConcurrentQueue<bool>();
        shared.PropertyChanged += (_, _) => notifications.Enqueue(ui.CheckAccess());

        shared.ReconcileDomain(0, 100e-6);
        Assert.IsNull(shared.Viewport);
        Assert.IsTrue(ui.RunNext());
        Assert.IsNotNull(shared.Viewport);
        shared.Zoom(25e-6, 2);
        shared.Pan(1);
        Assert.AreEqual(2, ui.Pending);
        Assert.IsTrue(ui.RunLast());
        Assert.AreEqual(50e-6, shared.Viewport.VisibleSpanSeconds, 1e-15);
        Assert.IsTrue(ui.RunLast());
        Assert.AreEqual(100e-6, shared.Viewport.VisibleMaximumSeconds, 1e-15);
        Assert.IsTrue(notifications.All(access => access));
    }

    [TestMethod]
    public async Task SharedViewportIntersectsConsumersSynchronizesAndInvalidatesPendingWorkOnClose()
    {
        var ui = new ManualUiDispatcher();
        var shared = new SharedScanTimeViewport(ui);
        var run = NewRun();
        Task register = ui.InvokeAsync(() =>
        {
            shared.ReconcileDomain("a", run, 1, 0, 100e-6);
            shared.ReconcileDomain("b", run, 4, 20e-6, 80e-6);
        });
        await ui.DriveAsync(register);
        Assert.AreEqual(20e-6, shared.Viewport!.DomainMinimumSeconds, 1e-15);
        Assert.AreEqual(80e-6, shared.Viewport.DomainMaximumSeconds, 1e-15);

        shared.Zoom(50e-6, 2);
        Assert.IsTrue(ui.RunNext());
        Assert.AreEqual(30e-6, shared.Viewport.VisibleSpanSeconds, 1e-15);
        shared.RemoveConsumer("b");
        Assert.IsTrue(ui.RunNext());
        Assert.AreEqual(0, shared.Viewport.DomainMinimumSeconds, 1e-15);
        Assert.AreEqual(100e-6, shared.Viewport.DomainMaximumSeconds, 1e-15);

        var nextRun = NewRun();
        shared.ReconcileDomain("a", nextRun, 1, -40e-6, 40e-6);
        Assert.IsTrue(ui.RunNext());
        Assert.AreEqual(nextRun, shared.RunId);
        Assert.IsTrue(shared.Viewport!.IsReset);
        shared.ReconcileDomain("b", run, 99, 0, 10e-6); // Late snapshot from the retired run.
        Assert.IsTrue(ui.RunNext());
        Assert.AreEqual(nextRun, shared.RunId);
        Assert.AreEqual(-40e-6, shared.Viewport.DomainMinimumSeconds, 1e-15);

        var stable = shared.Viewport;
        shared.Pan(10e-6);
        Task close = shared.DisposeAsync().AsTask();
        await ui.DriveAsync(close);
        Assert.IsTrue(shared.IsClosed);
        Assert.IsNull(shared.Viewport);
        Assert.AreNotSame(stable, shared.Viewport);
    }

    [TestMethod]
    public async Task ViewportNotificationUsesTheSnapshotWhoseDomainWasReconciled()
    {
        var ui = new ManualUiDispatcher();
        var session = new ManualApplicationSession();
        var feed = new ManualObservable<AScanSnapshot>();
        var shared = new SharedScanTimeViewport(ui);
        var vm = new AScanViewModel(session, feed, ui, Configuration, timeViewport: shared);
        try
        {
            var run = NewRun();
            session.Set(ManualApplicationSession.Create(1, SessionPhase.Running, run, canStop: true));
            feed.Emit(Visual(run, 1));
            await ui.DriveUntilAsync(() => vm.ViewportBinding?.SnapshotVersion == 1);

            var observed = new List<ScanTimeViewportBinding>();
            int sharedViewportNotifications = 0;
            bool reentered = false;
            shared.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(SharedScanTimeViewport.Viewport)) { sharedViewportNotifications++; }
            };
            vm.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(AScanViewModel.ViewportBinding) && vm.ViewportBinding is { } binding)
                { observed.Add(binding); }
                if (!reentered && args.PropertyName == nameof(AScanViewModel.AScan) && vm.AScan?.Version == 2)
                {
                    reentered = true;
                    var viewport = vm.TimeViewport!;
                    vm.ZoomTime((viewport.VisibleMinimumSeconds + viewport.VisibleMaximumSeconds) / 2, 2);
                }
            };
            var wider = new ConventionalAcquisitionConfiguration(new PhysicalChannelId(0), 16, 50_000_000);
            feed.Emit(new AScanProjector().Project(
                new(ManualApplicationSession.Source, run, wider, DateTimeOffset.UnixEpoch), 2, TimeSpan.Zero,
                new short[wider.SampleCount], 2));
            await ui.DriveUntilAsync(() => vm.ViewportBinding?.SnapshotVersion == 2);

            Assert.IsFalse(observed.Any(binding => binding.SnapshotVersion == 1 &&
                binding.Viewport.DomainMaximumSeconds == vm.AScan!.MaximumTimeSeconds));
            Assert.IsFalse(observed.Any(binding => binding.SnapshotVersion == 2 &&
                binding.Viewport.DomainMaximumSeconds != vm.AScan!.MaximumTimeSeconds));
            Assert.AreEqual(vm.AScan!.MaximumTimeSeconds, vm.ViewportBinding!.Viewport.DomainMaximumSeconds, 1e-15);
            Assert.AreEqual(1, sharedViewportNotifications);
            Assert.HasCount(1, observed);
            Assert.IsGreaterThan(1, vm.TimeViewport!.ZoomFactor);
        }
        finally
        {
            await ui.DriveAsync(vm.DisposeAsync().AsTask());
            await ui.DriveAsync(shared.DisposeAsync().AsTask());
        }
    }

    [TestMethod]
    public async Task ViewportBindingNotifiesOncePerDistinctCompleteValueWithoutTransientPairs()
    {
        var ui = new ManualUiDispatcher();
        var session = new ManualApplicationSession();
        var feed = new ManualObservable<AScanSnapshot>();
        var shared = new SharedScanTimeViewport(ui);
        var vm = new AScanViewModel(session, feed, ui, Configuration, timeViewport: shared);
        var observed = new List<ScanTimeViewportBinding?>();
        vm.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName != nameof(AScanViewModel.ViewportBinding)) { return; }
            var value = vm.ViewportBinding;
            if (value is not null)
            {
                Assert.IsNotNull(vm.AScan);
                Assert.AreEqual(vm.AScan.Metadata.RunId, value.RunId);
                Assert.AreEqual(vm.AScan.Version, value.SnapshotVersion);
            }
            observed.Add(value);
        };
        try
        {
            var run = NewRun();
            session.Set(ManualApplicationSession.Create(1, SessionPhase.Running, run, canStop: true));
            feed.Emit(Visual(run, 1));
            await ui.DriveUntilAsync(() => vm.ViewportBinding?.SnapshotVersion == 1);
            Assert.HasCount(1, observed);

            feed.Emit(Visual(run, 1));
            while (ui.RunNext()) { }
            Assert.HasCount(1, observed);

            feed.Emit(Visual(run, 2, sequence: 2));
            await ui.DriveUntilAsync(() => vm.ViewportBinding?.SnapshotVersion == 2);
            Assert.HasCount(2, observed);

            var viewport = shared.Viewport!;
            shared.Zoom((viewport.DomainMinimumSeconds + viewport.DomainMaximumSeconds) / 2, 2);
            Assert.IsTrue(ui.RunNext());
            Assert.HasCount(3, observed);

            shared.ReconcileDomain("peer", run, 1, 1, 2);
            Assert.IsTrue(ui.RunNext());
            Assert.IsNull(vm.ViewportBinding);
            Assert.HasCount(4, observed);
            shared.ReconcileDomain("peer", run, 1, 1, 2);
            Assert.IsTrue(ui.RunNext());
            Assert.HasCount(4, observed);

            shared.ReconcileDomain("peer", run, 2, vm.AScan!.MinimumTimeSeconds,
                (vm.AScan.MinimumTimeSeconds + vm.AScan.MaximumTimeSeconds) / 2);
            Assert.IsTrue(ui.RunNext());
            Assert.IsNotNull(vm.ViewportBinding);
            Assert.HasCount(5, observed);
            await ui.DriveAsync(shared.RemoveConsumerAsync("peer"));
            Assert.HasCount(6, observed);

            var nextRun = NewRun();
            session.Set(ManualApplicationSession.Create(2, SessionPhase.Running, nextRun, canStop: true));
            feed.Emit(Visual(nextRun, 3));
            await ui.DriveUntilAsync(() => vm.ViewportBinding?.RunId == nextRun);
            Assert.HasCount(7, observed);
            Assert.IsTrue(observed.Zip(observed.Skip(1), (left, right) => left != right).All(distinct => distinct));
        }
        finally
        {
            await ui.DriveAsync(vm.DisposeAsync().AsTask());
            await ui.DriveAsync(shared.DisposeAsync().AsTask());
        }
    }

    [TestMethod]
    public async Task ClosingOnUiPublishesOnlyEffectiveCommandTransitionsAndNothingLate()
    {
        var ui = new ManualUiDispatcher();
        var session = new ManualApplicationSession();
        var feed = new ManualObservable<AScanSnapshot>();
        var vm = new AScanViewModel(session, feed, ui, Configuration);
        var commands = Commands(vm);
        var counts = commands.ToDictionary(command => command, _ => 0);
        var access = new List<bool>();
        foreach (var command in commands)
        {
            command.CanExecuteChanged += (_, _) =>
            {
                counts[command]++;
                access.Add(ui.CheckAccess());
                Assert.IsFalse(command.CanExecute(null));
                command.Execute(null);
            };
        }

        Task? close = null;
        await ui.DriveAsync(ui.InvokeAsync(() => close = vm.DisposeAsync().AsTask()));
        await ui.DriveAsync(close!);
        Assert.AreEqual(1, counts[vm.StartCommand]);
        Assert.AreEqual(0, counts[vm.StopCommand]);
        Assert.IsTrue(commands.Skip(2).All(command => counts[command] == 1));
        Assert.IsTrue(access.All(value => value));
        int eventCount = counts.Values.Sum();
        session.Set(ManualApplicationSession.Create(1, SessionPhase.Idle, canStart: true));
        feed.Emit(Visual(NewRun(), 1));
        foreach (var command in commands) { command.Execute(null); }
        while (ui.RunNext()) { }
        Assert.AreEqual(eventCount, counts.Values.Sum());
        Assert.IsTrue(commands.All(command => !command.CanExecute(null)));
        Assert.AreSame(close, vm.DisposeAsync().AsTask());
    }

    [TestMethod]
    public async Task ConcurrentNonUiCloseSharesCompletionAndRaisesOneFinalCommandEvent()
    {
        var ui = new ManualUiDispatcher();
        var session = new ManualApplicationSession();
        var vm = new AScanViewModel(session, new ManualObservable<AScanSnapshot>(), ui, Configuration);
        var commands = Commands(vm);
        var counts = commands.ToDictionary(command => command, _ => 0);
        foreach (var command in commands) { command.CanExecuteChanged += (_, _) => counts[command]++; }

        Task<object>[] callers = Enumerable.Range(0, 8)
            .Select(_ => Task.Run(() => (object)vm.DisposeAsync().AsTask()))
            .ToArray();
        await Task.WhenAll(callers);
        Task close = (Task)callers[0].Result;
        Assert.IsTrue(callers.All(caller => ReferenceEquals(close, caller.Result)));
        await vm.StartCommand.ExecuteAsync(null);
        Assert.AreEqual(0, session.StartCalls);
        await ui.DriveAsync(close);
        Assert.AreEqual(1, counts[vm.StartCommand]);
        Assert.AreEqual(0, counts[vm.StopCommand]);
        Assert.IsTrue(commands.Skip(2).All(command => counts[command] == 1));
        Assert.IsTrue(commands.All(command => !command.CanExecute(null)));
        Assert.AreSame(close, vm.DisposeAsync().AsTask());
        while (ui.RunNext()) { }
        Assert.AreEqual(1, counts[vm.StartCommand]);
        Assert.AreEqual(0, counts[vm.StopCommand]);
        Assert.IsTrue(commands.Skip(2).All(command => counts[command] == 1));
    }

    [TestMethod]
    public async Task ClosingActiveCommandCompletesItsUiStateBeforeFinalCommandNotification()
    {
        var ui = new ManualUiDispatcher();
        var session = new ManualApplicationSession();
        var entered = Signal();
        var never = Signal();
        session.StartAction = async token =>
        {
            entered.TrySetResult();
            await never.Task.WaitAsync(token);
        };
        var vm = new AScanViewModel(session, new ManualObservable<AScanSnapshot>(), ui, Configuration);
        Task start = vm.StartCommand.ExecuteAsync(null);
        await ui.DriveAsync(entered.Task);
        Assert.IsTrue(vm.StartCommand.IsRunning);
        var commands = Commands(vm);
        var counts = commands.ToDictionary(command => command, _ => 0);
        foreach (var command in commands)
        {
            command.CanExecuteChanged += (_, _) =>
            {
                counts[command]++;
                Assert.IsTrue(ui.CheckAccess());
            };
        }

        Task close = vm.DisposeAsync().AsTask();
        Assert.IsFalse(vm.StartCommand.CanExecute(null));
        Assert.IsFalse(vm.StopCommand.CanExecute(null));
        await ui.DriveAsync(close);
        await ui.DriveAsync(start);
        Assert.IsFalse(vm.StartCommand.IsRunning);
        Assert.IsFalse(vm.StartCommand.CanBeCanceled);
        Assert.IsFalse(vm.StartCommand.IsCancellationRequested);
        Assert.IsTrue(vm.StartCommand.ExecutionTask?.IsCompleted);
        Assert.AreEqual(0, counts[vm.StartCommand]);
        Assert.AreEqual(1, counts[vm.StopCommand]);
        Assert.IsTrue(commands.Skip(2).All(command => counts[command] == 1));
        while (ui.RunNext()) { }
        Assert.AreEqual(0, counts[vm.StartCommand]);
        Assert.AreEqual(1, counts[vm.StopCommand]);
        Assert.IsTrue(commands.Skip(2).All(command => counts[command] == 1));
    }

    [TestMethod]
    public async Task PendingRefreshAndCloseShareOneSemanticNotificationInEitherDispatcherOrder()
    {
        await Verify(reverse: false);
        await Verify(reverse: true);

        static async Task Verify(bool reverse)
        {
            var ui = new ManualUiDispatcher { AlwaysQueue = true };
            var session = new ManualApplicationSession { EmitSnapshotOnSubscribe = false };
            var vm = new AScanViewModel(session, new ManualObservable<AScanSnapshot>(), ui, Configuration);
            int startEvents = 0, stopEvents = 0;
            var access = new List<bool>();
            vm.StartCommand.CanExecuteChanged += (_, _) => { startEvents++; access.Add(ui.CheckAccess()); };
            vm.StopCommand.CanExecuteChanged += (_, _) => { stopEvents++; access.Add(ui.CheckAccess()); };

            vm.StartCommand.NotifyCanExecuteChanged();
            vm.StartCommand.NotifyCanExecuteChanged();
            vm.StartCommand.NotifyCanExecuteChanged();
            Assert.AreEqual(1, ui.Pending);
            Task close = vm.DisposeAsync().AsTask();
            Assert.IsFalse(close.IsCompleted);
            Assert.AreEqual(2, ui.Pending); // One shared Start drain and one terminal Stop drain.

            Assert.IsTrue(reverse ? ui.RunLast() : ui.RunNext());
            Assert.IsTrue(reverse ? ui.RunLast() : ui.RunNext());
            await ui.DriveAsync(close);
            Assert.AreEqual(1, startEvents);
            Assert.AreEqual(0, stopEvents);
            Assert.IsTrue(access.All(value => value));

            int stableEvents = startEvents + stopEvents;
            vm.StartCommand.NotifyCanExecuteChanged();
            vm.StopCommand.NotifyCanExecuteChanged();
            while (ui.RunNext()) { }
            Assert.AreEqual(stableEvents, startEvents + stopEvents);
            Assert.AreSame(close, vm.DisposeAsync().AsTask());
        }
    }

    [TestMethod]
    public async Task NormalCommandCompletionConcurrentWithCloseConvergesBeforeDisposeCompletes()
    {
        var ui = new ManualUiDispatcher();
        var session = new ManualApplicationSession();
        var entered = Signal();
        var release = Signal();
        session.StartAction = _ =>
        {
            entered.TrySetResult();
            return release.Task;
        };
        var vm = new AScanViewModel(session, new ManualObservable<AScanSnapshot>(), ui, Configuration);
        Task start = vm.StartCommand.ExecuteAsync(null);
        await ui.DriveAsync(entered.Task);
        int startEvents = 0, stopEvents = 0;
        vm.StartCommand.CanExecuteChanged += (_, _) => startEvents++;
        vm.StopCommand.CanExecuteChanged += (_, _) => stopEvents++;

        Task close = vm.DisposeAsync().AsTask();
        Assert.IsFalse(close.IsCompleted);
        release.TrySetResult();
        await ui.DriveAsync(close);
        await ui.DriveAsync(start);
        Assert.IsFalse(vm.StartCommand.IsRunning);
        Assert.AreEqual(0, startEvents);
        Assert.AreEqual(1, stopEvents);
        int stableEvents = startEvents + stopEvents;
        while (ui.RunNext()) { }
        Assert.AreEqual(stableEvents, startEvents + stopEvents);
    }

    private static IRelayCommand[] Commands(AScanViewModel vm) =>
    [
        vm.StartCommand, vm.StopCommand, vm.ToggleCursorsCommand, vm.ResetCursorsCommand,
        vm.ResetViewportCommand, vm.ZoomInCommand, vm.ZoomOutCommand, vm.PanLeftCommand, vm.PanRightCommand
    ];

    [TestMethod]
    public async Task InactiveViewModelRemovesItsDomainFromSharedViewport()
    {
        var ui = new ManualUiDispatcher();
        var session = new ManualApplicationSession();
        var feed = new ManualObservable<AScanSnapshot>();
        var shared = new SharedScanTimeViewport(ui);
        var vm = new AScanViewModel(session, feed, ui, Configuration, timeViewport: shared);
        var run = NewRun();
        session.Set(ManualApplicationSession.Create(1, SessionPhase.Running, run, canStop: true));
        feed.Emit(Visual(run, 1));
        await ui.DriveUntilAsync(() => vm.TimeViewport is not null);
        shared.ReconcileDomain("peer", run, 1, -1, 1);
        Assert.IsTrue(ui.RunNext());
        Assert.AreEqual(vm.AScan!.MinimumTimeSeconds, shared.Viewport!.DomainMinimumSeconds, 1e-15);

        session.Set(ManualApplicationSession.Create(2, SessionPhase.Idle, run, canStart: true));
        await ui.DriveUntilAsync(() => vm.AScan is null);
        Assert.AreEqual(-1, shared.Viewport!.DomainMinimumSeconds);
        Assert.AreEqual(1, shared.Viewport.DomainMaximumSeconds);
        await ui.DriveAsync(vm.DisposeAsync().AsTask());
        await ui.DriveAsync(shared.DisposeAsync().AsTask());
    }

    [TestMethod]
    public async Task SharedViewportEmptyIntersectionRecoversByUpdateRemovalAndRunRules()
    {
        var ui = new ManualUiDispatcher();
        var shared = new SharedScanTimeViewport(ui);
        var run = NewRun();
        Task initial = ui.InvokeAsync(() =>
        {
            shared.ReconcileDomain("a", run, 1, 0, 10);
            shared.ReconcileDomain("b", run, 1, 20, 30);
        });
        await ui.DriveAsync(initial);
        Assert.IsNull(shared.Viewport); // Empty intersection is represented by absence, never an invalid range.

        shared.ReconcileDomain("b", run, 1, 5, 15); // Same version is stale.
        Assert.IsTrue(ui.RunNext());
        Assert.IsNull(shared.Viewport);
        shared.ReconcileDomain("b", run, 2, 5, 15);
        Assert.IsTrue(ui.RunNext());
        Assert.AreEqual(5, shared.Viewport!.DomainMinimumSeconds);
        Assert.AreEqual(10, shared.Viewport.DomainMaximumSeconds);
        Assert.IsTrue(double.IsFinite(shared.Viewport.DomainSpanSeconds));
        Assert.IsGreaterThan(0, shared.Viewport.DomainSpanSeconds);

        shared.ReconcileDomain("b", run, 3, 20, 30);
        Assert.IsTrue(ui.RunNext());
        Assert.IsNull(shared.Viewport);
        await ui.DriveAsync(shared.RemoveConsumerAsync("b"));
        Assert.AreEqual(0, shared.Viewport!.DomainMinimumSeconds);
        Assert.AreEqual(10, shared.Viewport.DomainMaximumSeconds);

        var nextRun = NewRun();
        shared.ReconcileDomain("b", nextRun, 1, -5, 5);
        Assert.IsTrue(ui.RunNext());
        Assert.AreEqual(nextRun, shared.RunId);
        Assert.AreEqual(-5, shared.Viewport!.DomainMinimumSeconds);
        shared.ReconcileDomain("a", run, 99, 0, 10);
        Assert.IsTrue(ui.RunNext());
        Assert.AreEqual(nextRun, shared.RunId); // Retired run cannot reactivate the group.
        Assert.AreEqual(-5, shared.Viewport.DomainMinimumSeconds);
        await ui.DriveAsync(shared.DisposeAsync().AsTask());
    }

    [TestMethod]
    public async Task ViewportCommandsRespectLimitsUiAffinityAndClose()
    {
        var ui = new ManualUiDispatcher();
        var session = new ManualApplicationSession();
        var feed = new ManualObservable<AScanSnapshot>();
        var shared = new SharedScanTimeViewport(ui);
        var vm = new AScanViewModel(session, feed, ui, Configuration, timeViewport: shared);
        Assert.IsFalse(vm.ResetViewportCommand.CanExecute(null));
        Assert.IsFalse(vm.ZoomInCommand.CanExecute(null));
        Assert.IsFalse(vm.ZoomOutCommand.CanExecute(null));
        Assert.IsFalse(vm.PanLeftCommand.CanExecute(null));
        Assert.IsFalse(vm.PanRightCommand.CanExecute(null));
        var run = NewRun();
        session.Set(ManualApplicationSession.Create(1, SessionPhase.Running, run, canStop: true));
        feed.Emit(Visual(run, 1));
        await ui.DriveUntilAsync(() => vm.TimeViewport is not null);
        shared.ReconcileDomain("peer", run, 1, vm.TimeViewport!.DomainMinimumSeconds,
            vm.TimeViewport.DomainMaximumSeconds);
        Assert.IsTrue(ui.RunNext());

        Assert.IsTrue(vm.ZoomInCommand.CanExecute(null));
        Assert.IsFalse(vm.ZoomOutCommand.CanExecute(null));
        Assert.IsFalse(vm.ResetViewportCommand.CanExecute(null));
        Assert.IsFalse(vm.PanLeftCommand.CanExecute(null));
        Assert.IsFalse(vm.PanRightCommand.CanExecute(null));

        await Task.Run(() => vm.ZoomInCommand.Execute(null));
        Assert.IsTrue(ui.RunNext());
        Assert.IsGreaterThan(1, vm.TimeViewport!.ZoomFactor);
        Assert.IsTrue(vm.ZoomOutCommand.CanExecute(null));
        Assert.IsTrue(vm.ResetViewportCommand.CanExecute(null));
        Assert.IsTrue(vm.PanLeftCommand.CanExecute(null));
        Assert.IsTrue(vm.PanRightCommand.CanExecute(null));

        vm.PanTime(-1);
        Assert.IsTrue(ui.RunNext());
        Assert.IsFalse(vm.PanLeftCommand.CanExecute(null));
        Assert.IsTrue(vm.PanRightCommand.CanExecute(null));

        int guard = 0;
        while (vm.ZoomInCommand.CanExecute(null) && guard++ < 100)
        {
            vm.ZoomInCommand.Execute(null);
            Assert.IsTrue(ui.RunNext());
        }
        Assert.IsFalse(vm.ZoomInCommand.CanExecute(null));
        Assert.AreEqual(ScanTimeViewportOperations.MaximumZoomFactor, vm.TimeViewport!.ZoomFactor, 1e-9);

        vm.ResetViewportCommand.Execute(null);
        Assert.IsTrue(ui.RunNext());
        Assert.IsTrue(vm.TimeViewport.IsReset);
        Assert.IsFalse(vm.ResetViewportCommand.CanExecute(null));
        Assert.IsFalse(vm.ZoomOutCommand.CanExecute(null));
        Assert.IsFalse(vm.PanLeftCommand.CanExecute(null));
        Assert.IsFalse(vm.PanRightCommand.CanExecute(null));

        await ui.DriveAsync(vm.DisposeAsync().AsTask());
        Assert.IsFalse(vm.ResetViewportCommand.CanExecute(null));
        Assert.IsFalse(vm.ZoomInCommand.CanExecute(null));
        Assert.IsFalse(vm.ZoomOutCommand.CanExecute(null));
        Assert.IsFalse(vm.PanLeftCommand.CanExecute(null));
        Assert.IsFalse(vm.PanRightCommand.CanExecute(null));
        var stable = shared.Viewport;
        await Task.Run(() => { vm.ZoomTime(0, 2); vm.PanTime(1); vm.ResetTimeZoom(); });
        while (ui.RunNext()) { }
        Assert.AreSame(stable, shared.Viewport);
        await ui.DriveAsync(shared.DisposeAsync().AsTask());
    }

    [TestMethod]
    public async Task InitialStateAndBorrowedSubscriptions()
    {
        var ui = new ManualUiDispatcher();
        var session = new ManualApplicationSession();
        var feed = new ManualObservable<AScanSnapshot>();
        var vm = new AScanViewModel(session, feed, ui, Configuration);
        Assert.AreSame(session.Snapshot, vm.Session);
        Assert.IsNull(vm.AScan);
        Assert.HasCount(0, vm.Points);
        Assert.IsNull(vm.Error);
        Assert.IsTrue(vm.StartCommand.CanExecute(null));
        Assert.IsFalse(vm.StopCommand.CanExecute(null));
        Assert.IsFalse(vm.StartCommand.IsRunning);
        Assert.AreEqual(1, session.Updates.Subscribers);
        Assert.AreEqual(1, feed.Subscribers);
        await ui.DriveAsync(vm.DisposeAsync().AsTask());
        Assert.AreEqual(1, session.Updates.Unsubscriptions);
        Assert.AreEqual(1, feed.Unsubscriptions);
        Assert.AreEqual(0, session.StartCalls);
        Assert.AreEqual(0, session.StopCalls);
    }

    [TestMethod]
    public async Task SnapshotsReplaceImmutableReferencesAndExposeCountersOnUi()
    {
        var ui = new ManualUiDispatcher();
        var session = new ManualApplicationSession();
        var feed = new ManualObservable<AScanSnapshot>();
        var statistics = new AScanDeliveryStatistics(10, 2, 7, 0, 1, 0, false, null);
        var vm = new AScanViewModel(session, feed, ui, Configuration, readVisualStatistics: () => statistics);
        var events = Trace(vm, ui);
        try
        {
            var run = NewRun();
            var state = ManualApplicationSession.Create(3, SessionPhase.Running, run, canStop: true, received: 10, released: 10);
            session.Set(state);
            var scan = Visual(run, 9);
            feed.Emit(scan);
            await ui.DriveUntilAsync(() => vm.AScan == scan);
            Assert.AreSame(scan.Points, vm.Points);
            Assert.AreSame(scan.Metadata, vm.Metadata);
            Assert.AreSame(state, vm.Session);
            Assert.AreSame(statistics, vm.VisualStatistics);
            Assert.AreEqual(10L, vm.ReceivedFrames);
            Assert.AreEqual(10L, vm.ReleasedFrames);
            Assert.IsTrue(events.Count > 0 && events.All(access => access));
            Assert.IsNull(vm.NotificationError);
        }
        finally { await ui.DriveAsync(vm.DisposeAsync().AsTask()); }
    }

    [TestMethod]
    public async Task SlowUiCoalescesManyInputsIntoOnePendingDispatch()
    {
        var ui = new ManualUiDispatcher();
        var session = new ManualApplicationSession();
        var feed = new ManualObservable<AScanSnapshot>();
        var vm = new AScanViewModel(session, feed, ui, Configuration);
        try
        {
            await ManualUiDispatcher.UntilAsync(() => ui.Pending == 1);
            var run = NewRun();
            session.Set(ManualApplicationSession.Create(1, SessionPhase.Running, run, canStop: true));
            AScanSnapshot? latest = null;
            for (ulong i = 1; i <= 100; i++) { latest = Visual(run, i, i); feed.Emit(latest); }
            Assert.AreEqual(1, ui.Pending);
            Assert.IsNull(vm.AScan);
            await ui.DriveUntilAsync(() => vm.AScan == latest);
            Assert.AreSame(latest!.Points, vm.Points);
        }
        finally { await ui.DriveAsync(vm.DisposeAsync().AsTask()); }
    }

    [TestMethod]
    public async Task VisualBeforeRunningIsRetainedButStaleRunsAndVersionsCannotRepaint()
    {
        var ui = new ManualUiDispatcher();
        var session = new ManualApplicationSession();
        var feed = new ManualObservable<AScanSnapshot>();
        var vm = new AScanViewModel(session, feed, ui, Configuration);
        try
        {
            var run1 = NewRun();
            var early = Visual(run1, 1);
            feed.Emit(early);
            session.Set(ManualApplicationSession.Create(1, SessionPhase.Starting, run1, canStop: true));
            await ui.DriveUntilAsync(() => vm.Session.Phase == SessionPhase.Starting);
            Assert.IsNull(vm.AScan);
            session.Set(ManualApplicationSession.Create(2, SessionPhase.Running, run1, canStop: true));
            await ui.DriveUntilAsync(() => vm.AScan == early);
            var run2 = NewRun();
            var latest = Visual(run2, 10);
            feed.Emit(latest);
            session.Set(ManualApplicationSession.Create(4, SessionPhase.Running, run2, canStop: true));
            await ui.DriveUntilAsync(() => vm.AScan == latest);
            feed.Emit(Visual(run1, 2));
            session.Updates.Emit(ManualApplicationSession.Create(3, SessionPhase.Idle, run1, canStart: true));
            session.Set(ManualApplicationSession.Create(5, SessionPhase.Running, run2, canStop: true));
            await ui.DriveUntilAsync(() => vm.Session.Version == 5);
            Assert.AreSame(latest, vm.AScan);
            session.Set(ManualApplicationSession.Create(6, SessionPhase.Stopping, run2));
            await ui.DriveUntilAsync(() => vm.Session.Phase == SessionPhase.Stopping);
            Assert.IsNull(vm.AScan);
        }
        finally { await ui.DriveAsync(vm.DisposeAsync().AsTask()); }
    }

    [TestMethod]
    public async Task StartStopCommandsExecuteOnceAndAllTheirEventsUseUiDispatcher()
    {
        var ui = new ManualUiDispatcher();
        var session = new ManualApplicationSession();
        var entered = Signal();
        var proceed = Signal();
        session.StartAction = async token => { entered.TrySetResult(); await proceed.Task.WaitAsync(token); };
        var run = NewRun();
        var vm = new AScanViewModel(session, new ManualObservable<AScanSnapshot>(), ui, Configuration, () => run);
        var events = Trace(vm, ui);
        try
        {
            var start = vm.StartCommand.ExecuteAsync(null); // Deliberately outside UI and without SynchronizationContext.
            await ui.DriveAsync(entered.Task);
            Assert.IsTrue(vm.StartCommand.IsRunning);
            Assert.IsTrue(vm.StartCommand.CanBeCanceled);
            Assert.IsFalse(vm.StartCommand.CanExecute(null));
            Assert.IsTrue(vm.StopCommand.CanExecute(null));
            await Watch(vm.StartCommand.ExecuteAsync(null));
            Assert.AreEqual(1, session.StartCalls);
            proceed.TrySetResult();
            await ui.DriveAsync(start);
            Assert.AreEqual(run, session.LastRun);
            Assert.AreSame(Configuration, session.LastConfiguration);
            Assert.IsFalse(vm.StartCommand.IsRunning);
            Assert.IsTrue(vm.StopCommand.CanExecute(null));
            await ui.DriveAsync(vm.StopCommand.ExecuteAsync(null));
            Assert.AreEqual(1, session.StopCalls);
            Assert.IsTrue(vm.StartCommand.CanExecute(null));
            Assert.IsFalse(vm.StopCommand.CanExecute(null));
            Assert.IsTrue(events.Count > 0 && events.All(access => access));
        }
        finally { proceed.TrySetResult(); await ui.DriveAsync(vm.DisposeAsync().AsTask()); }
    }

    [TestMethod]
    public async Task StopCompletionWaitsForPendingSessionSnapshotBeforeRefreshingStart()
    {
        var ui = new ManualUiDispatcher();
        var session = new ManualApplicationSession();
        var run = NewRun();
        var idlePublished = Signal();
        var releaseStop = Signal();
        var vm = new AScanViewModel(session, new ManualObservable<AScanSnapshot>(), ui, Configuration);
        try
        {
            await ui.DriveUntilAsync(() => ui.Pending == 0);
            session.Set(ManualApplicationSession.Create(1, SessionPhase.Running, run, canStop: true));
            await ui.DriveUntilAsync(() => vm.Session.Phase == SessionPhase.Running);
            session.StopAction = async _ =>
            {
                session.Set(ManualApplicationSession.Create(2, SessionPhase.Idle, run, canStart: true));
                idlePublished.TrySetResult();
                await releaseStop.Task;
            };

            Task stop = vm.StopCommand.ExecuteAsync(null);
            int commandEvents = 0;
            vm.StartCommand.CanExecuteChanged += (_, _) => commandEvents++;
            vm.StopCommand.CanExecuteChanged += (_, _) => commandEvents++;
            await ui.DriveAsync(idlePublished.Task);
            Assert.AreEqual(SessionPhase.Running, vm.Session.Phase);
            await ManualUiDispatcher.UntilAsync(() => ui.Pending >= 1);
            releaseStop.TrySetResult();
            await ManualUiDispatcher.UntilAsync(() => ui.Pending >= 2);

            // Execute the newest UI item before the older coalesced pump. The command-level barrier
            // must apply Idle here; without it this item is command finalization over stale Running.
            Assert.IsTrue(ui.RunLast());
            Assert.AreEqual(SessionPhase.Idle, vm.Session.Phase);
            Assert.IsFalse(stop.IsCompleted);
            await ManualUiDispatcher.UntilAsync(() => ui.Pending >= 2);
            Assert.IsTrue(ui.RunLast());
            await stop;
            Assert.IsTrue(vm.StartCommand.CanExecute(null));
            Assert.IsFalse(vm.StopCommand.CanExecute(null));
            int afterCompletion = commandEvents;
            Assert.IsTrue(ui.RunNext()); // Previously scheduled coalesced pump.
            Assert.AreEqual(afterCompletion, commandEvents);
        }
        finally
        {
            releaseStop.TrySetResult();
            await ui.DriveAsync(vm.DisposeAsync().AsTask());
        }
    }

    [TestMethod]
    public async Task StartCompletionWaitsForPendingSessionSnapshotBeforeRefreshingStop()
    {
        var ui = new ManualUiDispatcher();
        var session = new ManualApplicationSession();
        var run = NewRun();
        var runningPublished = Signal();
        var releaseStart = Signal();
        session.StartAction = async _ =>
        {
            session.Set(ManualApplicationSession.Create(2, SessionPhase.Running, run, canStop: true));
            runningPublished.TrySetResult();
            await releaseStart.Task;
        };
        var vm = new AScanViewModel(session, new ManualObservable<AScanSnapshot>(), ui, Configuration, () => run);
        try
        {
            Task start = vm.StartCommand.ExecuteAsync(null);
            await ui.DriveAsync(runningPublished.Task);
            Assert.AreEqual(SessionPhase.Idle, vm.Session.Phase);
            await ManualUiDispatcher.UntilAsync(() => ui.Pending >= 1);
            releaseStart.TrySetResult();
            await ManualUiDispatcher.UntilAsync(() => ui.Pending >= 2);

            Assert.IsTrue(ui.RunLast());
            Assert.AreEqual(SessionPhase.Running, vm.Session.Phase);
            Assert.IsFalse(start.IsCompleted);
            await ui.DriveAsync(start);
            Assert.IsFalse(vm.StartCommand.CanExecute(null));
            Assert.IsTrue(vm.StopCommand.CanExecute(null));
        }
        finally
        {
            releaseStart.TrySetResult();
            await ui.DriveAsync(vm.DisposeAsync().AsTask());
        }
    }

    [TestMethod]
    public async Task StopCanCancelPendingStartWithoutWaitingForItsCompletionFirst()
    {
        var ui = new ManualUiDispatcher();
        var session = new ManualApplicationSession();
        var entered = Signal();
        var never = Signal();
        session.StartAction = async token => { entered.TrySetResult(); await never.Task.WaitAsync(token); };
        var vm = new AScanViewModel(session, new ManualObservable<AScanSnapshot>(), ui, Configuration);
        try
        {
            var start = vm.StartCommand.ExecuteAsync(null);
            await ui.DriveAsync(entered.Task);
            var stop = vm.StopCommand.ExecuteAsync(null);
            await ui.DriveAsync(Task.WhenAll(start, stop));
            Assert.AreEqual(1, session.StopCalls);
            Assert.IsFalse(vm.StartCommand.IsRunning);
            Assert.IsNull(vm.Error);
        }
        finally { await ui.DriveAsync(vm.DisposeAsync().AsTask()); }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ExpectedCommandErrorsBecomeObservableState(bool failStop)
    {
        var ui = new ManualUiDispatcher();
        var session = new ManualApplicationSession();
        if (failStop) { session.StopAction = _ => Task.FromException(new InvalidOperationException("stop rejected")); }
        else { session.StartAction = _ => Task.FromException(new InvalidOperationException("start rejected")); }
        var vm = new AScanViewModel(session, new ManualObservable<AScanSnapshot>(), ui, Configuration);
        var events = Trace(vm, ui);
        try
        {
            await ui.DriveAsync(vm.StartCommand.ExecuteAsync(null));
            if (failStop) { await ui.DriveAsync(vm.StopCommand.ExecuteAsync(null)); }
            Assert.AreEqual(failStop ? "stop rejected" : "start rejected", vm.Error!.Message);
            Assert.IsFalse(vm.StartCommand.IsRunning);
            Assert.IsFalse(vm.StopCommand.IsRunning);
            Assert.IsTrue(events.All(access => access));
        }
        finally { await ui.DriveAsync(vm.DisposeAsync().AsTask()); }
    }

    [TestMethod]
    public async Task FaultedSessionUsesAuthoritativeAdmissionFlags()
    {
        var ui = new ManualUiDispatcher();
        var session = new ManualApplicationSession();
        session.Set(ManualApplicationSession.Create(1, SessionPhase.Faulted, NewRun(), error: new("barrier", "owners outstanding")));
        var vm = new AScanViewModel(session, new ManualObservable<AScanSnapshot>(), ui, Configuration);
        try
        {
            Assert.IsFalse(vm.StartCommand.CanExecute(null));
            await ui.DriveAsync(vm.StartCommand.ExecuteAsync(null));
            Assert.AreEqual(0, session.StartCalls);
            session.Set(ManualApplicationSession.Create(2, SessionPhase.Faulted, canStart: true));
            await ui.DriveUntilAsync(() => vm.StartCommand.CanExecute(null));
            await ui.DriveAsync(vm.StartCommand.ExecuteAsync(null));
            Assert.AreEqual(1, session.StartCalls);
        }
        finally { await ui.DriveAsync(vm.DisposeAsync().AsTask()); }
    }

    [TestMethod]
    public async Task ClosingInvalidatesQueuedAndLateCallbacksWithoutStoppingSession()
    {
        var ui = new ManualUiDispatcher();
        var session = new ManualApplicationSession();
        var feed = new ManualObservable<AScanSnapshot>();
        var vm = new AScanViewModel(session, feed, ui, Configuration);
        var events = Trace(vm, ui);
        var original = vm.State;
        var run = NewRun();
        session.Set(ManualApplicationSession.Create(1, SessionPhase.Running, run, canStop: true));
        feed.Emit(Visual(run, 1));
        await ManualUiDispatcher.UntilAsync(() => ui.Pending != 0);
        var lateSession = session.Updates.LastObserver!;
        var lateVisual = feed.LastObserver!;
        var close = vm.DisposeAsync().AsTask();
        Assert.AreSame(close, vm.DisposeAsync().AsTask());
        await ui.DriveAsync(close);
        int eventCount = events.Count;
        lateSession.OnNext(ManualApplicationSession.Create(99, SessionPhase.Running, run, canStop: true));
        lateVisual.OnNext(Visual(run, 99));
        lateVisual.OnError(new InvalidOperationException("late failure"));
        while (ui.RunNext()) { }
        await Watch(vm.StartCommand.ExecuteAsync(null));
        Assert.AreSame(original, vm.State);
        Assert.HasCount(eventCount, events);
        Assert.AreEqual(1, session.Updates.Unsubscriptions);
        Assert.AreEqual(1, feed.Unsubscriptions);
        Assert.AreEqual(0, session.StopCalls);
        Assert.AreEqual(0, session.StartCalls);
    }

    [TestMethod]
    public async Task CallbackThatFinishesAfterCloseCannotChangeBindings()
    {
        var ui = new ManualUiDispatcher();
        var session = new ManualApplicationSession();
        var feed = new ManualObservable<AScanSnapshot>();
        var entered = Signal();
        var release = Signal();
        var vm = new AScanViewModel(session, feed, ui, Configuration, readVisualStatistics: () =>
        {
            entered.TrySetResult();
            Watch(release.Task).GetAwaiter().GetResult();
            return new(1, 1, 0, 0, 0, 0, false, null);
        });
        var oldState = vm.State;
        var callback = Task.Run(() => feed.Emit(Visual(NewRun(), 1)));
        try
        {
            await Watch(entered.Task);
            await ui.DriveAsync(vm.DisposeAsync().AsTask());
            release.TrySetResult();
            await Watch(callback);
            while (ui.RunNext()) { }
            Assert.AreSame(oldState, vm.State);
        }
        finally { release.TrySetResult(); await Watch(callback); await ui.DriveAsync(vm.DisposeAsync().AsTask()); }
    }

    [TestMethod]
    public async Task FailingPropertyAndCommandObserversDoNotStrandUpdates()
    {
        var ui = new ManualUiDispatcher();
        var session = new ManualApplicationSession();
        var feed = new ManualObservable<AScanSnapshot>();
        var vm = new AScanViewModel(session, feed, ui, Configuration);
        vm.PropertyChanged += (_, _) => throw new InvalidOperationException("binding failed");
        vm.StartCommand.CanExecuteChanged += (_, _) => throw new InvalidOperationException("command observer failed");
        try
        {
            session.Set(ManualApplicationSession.Create(1, SessionPhase.Idle, canStart: true));
            await ui.DriveUntilAsync(() => vm.NotificationError is not null);
            await ui.DriveAsync(vm.StartCommand.ExecuteAsync(null));
            Assert.AreEqual(1, session.StartCalls);
            Assert.AreEqual("presentation.observer", vm.NotificationError!.Code);
            session.Set(ManualApplicationSession.Create(99, SessionPhase.Running, session.LastRun, canStop: true));
            await ui.DriveUntilAsync(() => vm.Session.Version == 99);
        }
        finally { await ui.DriveAsync(vm.DisposeAsync().AsTask()); }
    }

    [TestMethod]
    public async Task FeedErrorUsesUiStateAndUnsubscribeFailureDoesNotSkipOtherSubscription()
    {
        var ui = new ManualUiDispatcher();
        var session = new ManualApplicationSession();
        var feed = new ManualObservable<AScanSnapshot>();
        var vm = new AScanViewModel(session, feed, ui, Configuration);
        var events = Trace(vm, ui);
        feed.Fail(new InvalidOperationException("visual feed failed"));
        await ui.DriveUntilAsync(() => vm.Error is not null);
        Assert.AreEqual("presentation.feed", vm.Error!.Code);
        Assert.IsTrue(events.All(access => access));
        session.Updates.FailUnsubscribe = true;
        var close = vm.DisposeAsync().AsTask();
        await Assert.ThrowsExactlyAsync<AggregateException>(() => ui.DriveAsync(close));
        Assert.AreEqual(1, feed.Unsubscriptions);
        Assert.AreEqual(1, session.Updates.Unsubscriptions);
        Assert.AreSame(close, vm.DisposeAsync().AsTask());
    }

    [TestMethod]
    public async Task RepeatedCancellationPreservesCallbackBarrierAndItsFailure()
    {
        var ui = new ManualUiDispatcher();
        var session = new ManualApplicationSession();
        var entered = Signal();
        var callbackEntered = Signal();
        var releaseCallback = Signal();
        var never = Signal();
        CancellationTokenRegistration registration = default;
        session.StartAction = async token =>
        {
            registration = token.Register(() =>
            {
                callbackEntered.TrySetResult();
                Watch(releaseCallback.Task).GetAwaiter().GetResult();
                throw new InvalidOperationException("cancel callback failed");
            });
            entered.TrySetResult();
            await never.Task.WaitAsync(token);
        };
        var vm = new AScanViewModel(session, new ManualObservable<AScanSnapshot>(), ui, Configuration);
        Task? close = null;
        var start = vm.StartCommand.ExecuteAsync(null);
        try
        {
            await ui.DriveAsync(entered.Task);
            vm.StartCommand.Cancel();
            await ui.DriveAsync(callbackEntered.Task);
            vm.StartCommand.Cancel();
            close = vm.DisposeAsync().AsTask();
            await ManualUiDispatcher.UntilAsync(() => session.Updates.Unsubscriptions == 1);
            while (ui.RunNext()) { }
            Assert.IsFalse(close.IsCompleted);
            Assert.IsFalse(start.IsCompleted);
            releaseCallback.TrySetResult();
            await Assert.ThrowsExactlyAsync<AggregateException>(() => ui.DriveAsync(close));
            await ui.DriveAsync(start);
            Assert.IsFalse(vm.StartCommand.IsRunning);
            Assert.IsFalse(vm.StartCommand.CanBeCanceled);
            Assert.IsTrue(vm.StartCommand.IsCancellationRequested);
            Assert.IsTrue(vm.StartCommand.ExecutionTask?.IsCompleted);
        }
        finally
        {
            releaseCallback.TrySetResult();
            registration.Dispose();
            if (close is null) { await ui.DriveAsync(vm.DisposeAsync().AsTask()); }
        }
    }

    [TestMethod]
    public async Task CommandDispatcherFailureIsObservedWithoutOffThreadMutation()
    {
        var ui = new ManualUiDispatcher { FailDispatch = true };
        var session = new ManualApplicationSession();
        var vm = new AScanViewModel(session, new ManualObservable<AScanSnapshot>(), ui, Configuration);
        var events = Trace(vm, ui);
        try
        {
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => Watch(vm.StartCommand.ExecuteAsync(null)));
            Assert.AreEqual(0, session.StartCalls);
            Assert.HasCount(0, events);
        }
        finally { ui.FailDispatch = false; await ui.DriveAsync(vm.DisposeAsync().AsTask()); }
    }

    [TestMethod]
    public async Task PresentationOwnsCursorStateReconcilesSnapshotsAndStopsAfterDispose()
    {
        var ui = new ManualUiDispatcher();
        var session = new ManualApplicationSession();
        var feed = new ManualObservable<AScanSnapshot>();
        var vm = new AScanViewModel(session, feed, ui, Configuration);
        var run = NewRun();
        session.Set(ManualApplicationSession.Create(1, SessionPhase.Running, run, canStop: true));
        feed.Emit(Visual(run, 1));
        await ui.DriveUntilAsync(() => vm.Cursors is not null);
        vm.MoveCursor(AScanCursorId.A, vm.AScan!.MinimumTimeSeconds);
        Assert.IsTrue(ui.RunNext());
        vm.ActivateCursor(AScanCursorId.B);
        Assert.IsTrue(ui.RunNext());
        double movedTime = vm.Cursors!.A.TimeSeconds;
        Assert.AreEqual(AScanCursorId.B, vm.Cursors.ActiveCursor);
        Assert.IsTrue(vm.ToggleCursorsCommand.CanExecute(null));
        vm.ToggleCursorsCommand.Execute(null);
        Assert.IsTrue(ui.RunNext());
        Assert.IsFalse(vm.Cursors.IsVisible);
        vm.ResetCursorsCommand.Execute(null);
        Assert.IsTrue(ui.RunNext());
        Assert.IsFalse(vm.Cursors.IsVisible);
        Assert.AreNotEqual(movedTime, vm.Cursors.A.TimeSeconds);
        double resetTime = vm.Cursors.A.TimeSeconds;

        feed.Emit(Visual(run, 2, sequence: 2));
        await ui.DriveUntilAsync(() => vm.AScan?.Version == 2);
        Assert.AreEqual(resetTime, vm.Cursors!.A.TimeSeconds);
        double stable = vm.Cursors.A.TimeSeconds;
        await ui.DriveAsync(vm.DisposeAsync().AsTask());
        var disposedState = vm.State;
        vm.MoveCursor(AScanCursorId.A, vm.AScan!.MaximumTimeSeconds);
        vm.ActivateCursor(AScanCursorId.A);
        vm.ToggleCursorsCommand.Execute(null);
        vm.ResetCursorsCommand.Execute(null);
        Assert.AreSame(disposedState, vm.State);
        Assert.AreEqual(stable, vm.Cursors!.A.TimeSeconds);
    }

    [TestMethod]
    public async Task CursorIntentionsDispatchOnceInOrderAndOnlyNotifyOnUi()
    {
        var ui = new ManualUiDispatcher();
        var session = new ManualApplicationSession();
        var feed = new ManualObservable<AScanSnapshot>();
        var vm = new AScanViewModel(session, feed, ui, Configuration);
        try
        {
            var run = NewRun();
            session.Set(ManualApplicationSession.Create(1, SessionPhase.Running, run, canStop: true));
            feed.Emit(Visual(run, 1));
            await ui.DriveUntilAsync(() => vm.Cursors is not null);
            var notifications = Trace(vm, ui);

            var beforeMove = vm.State;
            vm.MoveCursor(AScanCursorId.A, vm.AScan!.MaximumTimeSeconds);
            Assert.AreSame(beforeMove, vm.State);
            Assert.AreEqual(1, ui.Pending);
            Assert.IsTrue(ui.RunNext());
            Assert.AreEqual(vm.AScan.MaximumTimeSeconds, vm.Cursors!.A.TimeSeconds);

            var beforeActivate = vm.State;
            vm.ActivateCursor(AScanCursorId.B);
            Assert.AreSame(beforeActivate, vm.State);
            Assert.AreEqual(1, ui.Pending);
            Assert.IsTrue(ui.RunNext());
            Assert.AreEqual(AScanCursorId.B, vm.Cursors.ActiveCursor);

            var beforeToggle = vm.State;
            vm.ToggleCursorsCommand.Execute(null);
            Assert.AreSame(beforeToggle, vm.State);
            Assert.AreEqual(1, ui.Pending);
            Assert.IsTrue(ui.RunNext());
            Assert.IsFalse(vm.Cursors.IsVisible);

            var beforeReset = vm.State;
            vm.ResetCursorsCommand.Execute(null);
            Assert.AreSame(beforeReset, vm.State);
            Assert.AreEqual(1, ui.Pending);
            Assert.IsTrue(ui.RunNext());
            Assert.AreNotEqual(vm.AScan.MaximumTimeSeconds, vm.Cursors.A.TimeSeconds);
            Assert.IsTrue(notifications.Count > 0 && notifications.All(access => access));

            vm.MoveCursor(AScanCursorId.A, vm.AScan.MaximumTimeSeconds);
            vm.ResetCursorsCommand.Execute(null);
            vm.MoveCursor(AScanCursorId.A, vm.AScan.MinimumTimeSeconds);
            Assert.AreEqual(3, ui.Pending);
            Assert.IsTrue(ui.RunLast());
            Assert.AreEqual(vm.AScan.MaximumTimeSeconds, vm.Cursors.A.TimeSeconds);
            Assert.IsTrue(ui.RunLast());
            Assert.AreNotEqual(vm.AScan.MaximumTimeSeconds, vm.Cursors.A.TimeSeconds);
            Assert.AreNotEqual(vm.AScan.MinimumTimeSeconds, vm.Cursors.A.TimeSeconds);
            Assert.IsTrue(ui.RunLast());
            Assert.AreEqual(vm.AScan.MinimumTimeSeconds, vm.Cursors.A.TimeSeconds);
        }
        finally { await ui.DriveAsync(vm.DisposeAsync().AsTask()); }
    }

    [TestMethod]
    public async Task CursorIntentionsExecuteInlineOnUiAndPendingOnesAreInvalidatedByClose()
    {
        var ui = new ManualUiDispatcher();
        var session = new ManualApplicationSession();
        var feed = new ManualObservable<AScanSnapshot>();
        var vm = new AScanViewModel(session, feed, ui, Configuration);
        var run = NewRun();
        session.Set(ManualApplicationSession.Create(1, SessionPhase.Running, run, canStop: true));
        feed.Emit(Visual(run, 1));
        await ui.DriveUntilAsync(() => vm.Cursors is not null);

        vm.MoveCursor(AScanCursorId.A, vm.AScan!.MaximumTimeSeconds);
        var inline = ui.InvokeAsync(() =>
        {
            vm.MoveCursor(AScanCursorId.A, vm.AScan!.MinimumTimeSeconds);
            vm.ActivateCursor(AScanCursorId.B);
            Assert.AreEqual(vm.AScan.MinimumTimeSeconds, vm.Cursors!.A.TimeSeconds);
            Assert.AreEqual(AScanCursorId.B, vm.Cursors!.ActiveCursor);
        });
        Assert.AreEqual(2, ui.Pending);
        Assert.IsTrue(ui.RunLast());
        await inline;
        Assert.AreEqual(vm.AScan.MinimumTimeSeconds, vm.Cursors!.A.TimeSeconds);
        Assert.IsTrue(ui.RunNext()); // Callback already consumed inline; it must be inert.
        Assert.AreEqual(vm.AScan.MinimumTimeSeconds, vm.Cursors.A.TimeSeconds);
        Assert.AreEqual(AScanCursorId.B, vm.Cursors.ActiveCursor);

        var stable = vm.State;
        vm.MoveCursor(AScanCursorId.A, vm.AScan!.MaximumTimeSeconds);
        Assert.AreEqual(1, ui.Pending);
        var close = vm.DisposeAsync().AsTask();
        Assert.AreSame(stable, vm.State);
        await ui.DriveAsync(close);
        Assert.AreSame(stable, vm.State);
    }

    private static ConcurrentQueue<bool> Trace(AScanViewModel vm, ManualUiDispatcher ui)
    {
        var access = new ConcurrentQueue<bool>();
        vm.PropertyChanged += (_, _) => access.Enqueue(ui.CheckAccess());
        vm.StartCommand.PropertyChanged += (_, _) => access.Enqueue(ui.CheckAccess());
        vm.StopCommand.PropertyChanged += (_, _) => access.Enqueue(ui.CheckAccess());
        vm.StartCommand.CanExecuteChanged += (_, _) => access.Enqueue(ui.CheckAccess());
        vm.StopCommand.CanExecuteChanged += (_, _) => access.Enqueue(ui.CheckAccess());
        return access;
    }
}
