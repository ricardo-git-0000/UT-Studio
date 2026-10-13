using System.Collections.Concurrent;
using System.Reflection;
using UTStudio.Contracts.Application;
using UTStudio.Contracts.Inspection;
using UTStudio.Domain.Acquisition;
using UTStudio.Presentation;

namespace UTStudio.Core.Tests.Presentation;

[TestClass]
public sealed class AScanHostViewModelTests
{
    private static readonly ConventionalAcquisitionConfiguration Configuration =
        new(new PhysicalChannelId(0), 8, 50_000_000);

    [TestMethod]
    public async Task InitialStateConnectsOnceAndDisposeDisconnectsOnce()
    {
        var session = new ManualApplicationSession();
        var ui = new ManualUiDispatcher();
        var vm = new AScanHostViewModel(session, ui, Configuration);

        await ui.DriveUntilAsync(() => ui.Pending == 0);
        Assert.AreEqual(SessionPhase.Idle, vm.State.Phase);
        Assert.AreSame(vm.State, vm.Session);
        Assert.IsTrue(vm.StartCommand.CanExecute(null));
        Assert.IsFalse(vm.StopCommand.CanExecute(null));
        Assert.AreEqual(1, session.Updates.Subscribers);

        await ui.DriveAsync(vm.DisposeAsync().AsTask());
        Assert.AreEqual(0, session.Updates.Subscribers);
        Assert.AreEqual(1, session.Updates.Unsubscriptions);
        Assert.IsFalse(vm.StartCommand.CanExecute(null));
        Assert.IsFalse(vm.StopCommand.CanExecute(null));
    }

    [TestMethod]
    public async Task SessionUpdatesAreForwardedOnUiAndCommandsTrackCanExecute()
    {
        var session = new ManualApplicationSession();
        var ui = new ManualUiDispatcher();
        var vm = new AScanHostViewModel(session, ui, Configuration);
        var access = new ConcurrentQueue<bool>();
        vm.PropertyChanged += (_, _) => access.Enqueue(ui.CheckAccess());

        var run = new AcquisitionRunId(Guid.NewGuid());
        session.Set(ManualApplicationSession.Create(4, SessionPhase.Running, run, canStop: true, received: 12));
        await ui.DriveUntilAsync(() => vm.State.Version == 4);

        Assert.AreEqual(12, vm.State.ReceivedFrames);
        Assert.IsFalse(vm.StartCommand.CanExecute(null));
        Assert.IsTrue(vm.StopCommand.CanExecute(null));
        Assert.IsTrue(access.All(value => value));
        await ui.DriveAsync(vm.DisposeAsync().AsTask());
    }

    [TestMethod]
    public async Task StartFailureIsVisibleAndSuccessfulRetryRecovers()
    {
        var session = new ManualApplicationSession
        {
            StartAction = _ => Task.FromException(new InvalidOperationException("synthetic start failure"))
        };
        var ui = new ManualUiDispatcher();
        var vm = new AScanHostViewModel(session, ui, Configuration);

        await ui.DriveAsync(vm.StartCommand.ExecuteAsync(null));
        await ui.DriveUntilAsync(() => vm.Error is not null);
        Assert.AreEqual("presentation.host", vm.Error!.Code);
        Assert.AreEqual(SessionPhase.Idle, vm.State.Phase);

        session.StartAction = null;
        await ui.DriveAsync(vm.StartCommand.ExecuteAsync(null));
        Assert.IsNull(vm.Error);
        Assert.AreEqual(SessionPhase.Running, vm.State.Phase);
        await ui.DriveAsync(vm.DisposeAsync().AsTask());
    }

    [TestMethod]
    public async Task ThrowingBindingIsDiagnosedAndLaterStateStillApplies()
    {
        var session = new ManualApplicationSession();
        var ui = new ManualUiDispatcher();
        var vm = new AScanHostViewModel(session, ui, Configuration);
        bool throwOnce = true;
        vm.PropertyChanged += (_, _) =>
        {
            if (throwOnce) { throwOnce = false; throw new InvalidOperationException("binding failed"); }
        };

        session.Set(ManualApplicationSession.Create(2, SessionPhase.Running, canStop: true));
        await ui.DriveUntilAsync(() => vm.State.Version == 2);
        Assert.AreEqual("presentation.host.observer", vm.Error!.Code);

        session.Set(ManualApplicationSession.Create(3, SessionPhase.Idle, canStart: true));
        await ui.DriveUntilAsync(() => vm.State.Version == 3);
        Assert.AreEqual(SessionPhase.Idle, vm.State.Phase);
        await ui.DriveAsync(vm.DisposeAsync().AsTask());
    }

    [TestMethod]
    public async Task CloseRequestedInsideCallbackIsIdempotentAndSuppressesLateCallbacks()
    {
        var session = new ManualApplicationSession();
        var ui = new ManualUiDispatcher();
        var vm = new AScanHostViewModel(session, ui, Configuration);
        Task? callbackClose = null;
        int callbacks = 0;
        vm.PropertyChanged += (_, args) =>
        {
            callbacks++;
            if (args.PropertyName == nameof(vm.State)) { callbackClose ??= vm.DisposeAsync().AsTask(); }
        };

        session.Set(ManualApplicationSession.Create(2, SessionPhase.Running, canStop: true));
        await ui.DriveUntilAsync(() => callbackClose is not null);
        Task second = vm.DisposeAsync().AsTask();
        Assert.AreSame(callbackClose, second);
        await ui.DriveAsync(Task.WhenAll(callbackClose!, second));
        int afterClose = callbacks;

        session.Updates.LastObserver!.OnNext(ManualApplicationSession.Create(3, SessionPhase.Idle, canStart: true));
        while (ui.RunNext()) { }
        Assert.AreEqual(afterClose, callbacks);
        Assert.AreEqual(2UL, vm.State.Version);
        Assert.AreEqual(1, session.Updates.Unsubscriptions);
    }

    [TestMethod]
    public async Task ConcurrentCloseSharesOneCleanupAndUnsubscribeFailureDoesNotSkipBarrier()
    {
        var session = new ManualApplicationSession();
        session.Updates.FailUnsubscribe = true;
        var ui = new ManualUiDispatcher();
        var vm = new AScanHostViewModel(session, ui, Configuration);

        Task first = Task.Run(() => vm.DisposeAsync().AsTask());
        Task second = Task.Run(() => vm.DisposeAsync().AsTask());
        await ui.DriveUntilAsync(() => first.IsCompleted && second.IsCompleted);
        await Assert.ThrowsExactlyAsync<AggregateException>(() => first);
        await Assert.ThrowsExactlyAsync<AggregateException>(() => second);
        Assert.AreEqual(1, session.Updates.Unsubscriptions);
        Assert.IsFalse(vm.StartCommand.CanExecute(null));
        Assert.IsFalse(vm.StopCommand.CanExecute(null));
    }

    [TestMethod]
    public async Task HostBorrowsSessionAndHasNoProviderOrLeaseSurface()
    {
        var session = new ManualApplicationSession();
        var ui = new ManualUiDispatcher();
        var vm = new AScanHostViewModel(session, ui, Configuration);
        Type[] forbidden = [typeof(IUtTraceProvider), typeof(IInspectionTraceLease), typeof(IReadOnlyUtSamples)];
        var exposed = typeof(AScanHostViewModel).GetMembers(BindingFlags.Public | BindingFlags.Instance)
            .SelectMany(MemberTypes)
            .Where(type => forbidden.Any(candidate => Contains(type, candidate)))
            .ToArray();

        Assert.IsEmpty(exposed);
        await ui.DriveAsync(vm.DisposeAsync().AsTask());
        Assert.AreEqual(0, session.StartCalls);
        Assert.AreEqual(0, session.StopCalls);
    }

    private static IEnumerable<Type> MemberTypes(MemberInfo member) => member switch
    {
        PropertyInfo property => [property.PropertyType],
        MethodInfo method => [method.ReturnType, .. method.GetParameters().Select(parameter => parameter.ParameterType)],
        _ => []
    };

    private static bool Contains(Type type, Type forbidden)
    {
        if (type.IsArray || type.IsByRef || type.IsPointer) { return Contains(type.GetElementType()!, forbidden); }
        return type == forbidden || type.IsGenericType && type.GetGenericArguments().Any(argument => Contains(argument, forbidden));
    }
}
