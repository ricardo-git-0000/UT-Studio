using UTStudio.Acquisition.Simulator;
using UTStudio.Application;
using UTStudio.Contracts.Acquisition;
using UTStudio.Contracts.Application;
using UTStudio.Contracts.Inspection;
using UTStudio.Contracts.Processing;
using UTStudio.Domain.Acquisition;
using UTStudio.Domain.Inspection;
using UTStudio.Inspection.Core;
using UTStudio.Presentation;
using UTStudio.Visualization.Core;

namespace UTStudio.App.Wpf.Composition;

internal sealed class ProfiledSimulationSession : IApplicationSession, IGatePlanApplyPort,
    ISimulationProfileSelection, IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly SemaphoreSlim _preparationGate = new(1, 1);
    private readonly SimulatorUtFrameSource _source;
    private readonly InspectionDataStoreRegistry _stores;
    private readonly ConventionalAcquisitionConfiguration _configuration;
    private readonly ICommittedTraceSink _bScan, _aScan;
    private readonly GateEditingContextSource _gateContexts;
    private readonly DesktopGateTestHooks? _hooks;
    private readonly List<IObserver<SessionSnapshot>> _observers = [];
    private readonly IReadOnlyList<SimulationProfileOption> _options;
    private string _selected = SimulatorSignalProfiles.MovingV1.Id;
    private bool _frozen, _disposed;
    private ProductiveGateInspectionScope? _scope;
    private ApplicationSession? _session;
    private IDisposable? _subscription;
    private SessionSnapshot _snapshot;

    internal ProfiledSimulationSession(SimulatorUtFrameSource source, InspectionDataStoreRegistry stores,
        ConventionalAcquisitionConfiguration configuration, ICommittedTraceSink bScan, ICommittedTraceSink aScan,
        GateEditingContextSource gateContexts, DesktopGateTestHooks? hooks)
    {
        _source = source; _stores = stores; _configuration = configuration; _bScan = bScan; _aScan = aScan;
        _gateContexts = gateContexts; _hooks = hooks;
        _options = SimulatorSignalProfiles.All.Select(x => new SimulationProfileOption(x.Id, x.DisplayName)).ToArray();
        _snapshot = new(0, SessionPhase.Idle, source.SourceId, null, 0, 0, null, null, null, [], 0, canStart: true);
    }

    public SessionSnapshot Snapshot { get { lock (_gate) return _snapshot; } }
    public IReadOnlyList<SimulationProfileOption> Options => _options;
    public string SelectedProfileId { get { lock (_gate) return _selected; } }
    public bool CanSelectProfile { get { lock (_gate) return !_frozen && !_disposed; } }
    internal ProductiveGateInspectionScope? GateScope { get { lock (_gate) return _scope; } }
    internal SimulationInspectionConfiguration? FrozenConfiguration { get; private set; }

    public void SelectProfile(string profileId)
    {
        if (string.IsNullOrWhiteSpace(profileId)) throw new ArgumentException("A profile id is required.", nameof(profileId));
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_frozen) throw new InvalidOperationException("The simulation profile is immutable for this inspection.");
            if (!_options.Any(x => StringComparer.Ordinal.Equals(x.Id, profileId)))
                throw new ArgumentOutOfRangeException(nameof(profileId));
            _selected = profileId;
        }
    }

    public async Task StartAsync(ConventionalAcquisitionConfiguration configuration, AcquisitionRunId runId,
        CancellationToken cancellationToken = default)
    {
        ApplicationSession session = await EnsurePreparedAsync(configuration, cancellationToken).ConfigureAwait(false);
        await session.StartAsync(configuration, runId, cancellationToken).ConfigureAwait(false);
    }

    internal Task<ApplicationSession> PrepareAsync(CancellationToken cancellationToken = default) =>
        EnsurePreparedAsync(_configuration, cancellationToken);

    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        ApplicationSession? session; lock (_gate) session = _session;
        return session?.StopAsync(cancellationToken) ?? Task.CompletedTask;
    }

    public GatePlanApplySubmission TryApplyGatePlan(GatePlanApplyRequest request,
        CancellationToken cancellationToken = default)
    {
        ApplicationSession? session; lock (_gate) session = _session;
        return session?.TryApplyGatePlan(request, cancellationToken) ??
            new(GatePlanApplySubmissionStatus.AdmissionClosed);
    }

    private async Task<ApplicationSession> EnsurePreparedAsync(ConventionalAcquisitionConfiguration requested,
        CancellationToken token)
    {
        await _preparationGate.WaitAsync(token).ConfigureAwait(false);
        try { return await PrepareCoreAsync(requested, token).ConfigureAwait(false); }
        finally { _preparationGate.Release(); }
    }

    private async Task<ApplicationSession> PrepareCoreAsync(ConventionalAcquisitionConfiguration requested,
        CancellationToken token)
    {
        ProductiveGateInspectionScope? createdScope = null;
        string selected;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_session is not null) return _session;
            _frozen = true; selected = _selected;
        }
        try
        {
            if (!SameConfiguration(requested, _configuration))
                throw new SimulationProfileCompatibilityException("AcquisitionAxisIncompatible");
            SimulatorSignalProfile signal = SimulatorSignalProfiles.All.Single(x => x.Id == selected);
            CompiledGatePlan plan = selected == SimulatorSignalProfiles.StationaryV1.Id
                ? ProductiveGateProcessingFactory.CompileStationaryAbc(requested)
                : ProductiveGateProcessingFactory.CompileDefault(requested);
            string bootstrap = selected == SimulatorSignalProfiles.StationaryV1.Id
                ? "stationary-abc" : "productive-default";
            ValidatePair(signal, bootstrap, requested, plan);
            token.ThrowIfCancellationRequested();
            _source.SelectSignalProfile(signal);
            ProductiveGateInspectionScope scope = createdScope = _hooks is null
                ? await ProductiveGateInspectionScope.CreateAsync(_stores.InspectionId, _stores.Store,
                    requested, plan, token).ConfigureAwait(false)
                : await ProductiveGateInspectionScope.CreateCoreAsync(_stores.InspectionId, _stores.Store,
                    requested, _hooks.Options ?? new(), token,
                    epochDecorator: _hooks.EpochDecorator,
                    replacementDecorator: _hooks.ReplacementDecorator,
                    schedulerDecorator: _hooks.SchedulerDecorator,
                    beforeAuthorityClose: _hooks.BeforeAuthorityClose,
                    explicitInitialPlan: plan).ConfigureAwait(false);
            ApplicationSession session = scope.CreateSession(_source,
                runId => _hooks?.InspectionWriterDecorator?.Invoke(_stores.OpenRun(runId)) ?? _stores.OpenRun(runId),
                _bScan, _aScan);
            IDisposable subscription = session.Subscribe(new Forwarder(this));
            var manifest = new SimulationInspectionConfiguration(signal.Id, signal.Version, bootstrap, 1,
                _source.Seed, requested.PhysicalChannelId, requested.SampleCount, requested.SampleRateHz,
                requested.FirstSampleOffsetSeconds, requested.SignalMode);
            lock (_gate)
            {
                if (_disposed) throw new ObjectDisposedException(nameof(ProfiledSimulationSession));
                _scope = scope; _session = session; _subscription = subscription; FrozenConfiguration = manifest;
                createdScope = null;
            }
            _gateContexts.Publish(new(scope.AuthorityKey, scope.Authority, scope.PreviewCompiler,
                scope.GeometryEvaluator, session));
            return session;
        }
        catch
        {
            lock (_gate) if (_session is null) _frozen = false;
            if (createdScope is not null) await createdScope.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static void ValidatePair(SimulatorSignalProfile signal, string bootstrap,
        ConventionalAcquisitionConfiguration configuration, CompiledGatePlan plan)
    {
        SimulatorUtFrameSource.ValidateProfileConfiguration(configuration, signal);
        bool stationaryPair = signal.Id == SimulatorSignalProfiles.StationaryV1.Id && bootstrap == "stationary-abc";
        bool movingPair = signal.Id == SimulatorSignalProfiles.MovingV1.Id && bootstrap == "productive-default";
        if (!stationaryPair && !movingPair) throw new SimulationProfileCompatibilityException("ProfilePairMismatch");
        if (!stationaryPair) return;
        double traceStart = configuration.FirstSampleOffsetSeconds;
        double traceEnd = traceStart + configuration.SampleCount / configuration.SampleRateHz;
        if (signal.FirstEchoSeconds < 8.5e-6 || signal.FirstEchoSeconds >= 11.5e-6 ||
            signal.SecondEchoSeconds < 23.5e-6 || signal.SecondEchoSeconds >= 26.5e-6 ||
            traceStart > 8.5e-6 || traceEnd < 28e-6 || plan.Source.Gates.Count != 3)
            throw new SimulationProfileCompatibilityException("CausalWindowOutOfRange");
    }

    private static bool SameConfiguration(ConventionalAcquisitionConfiguration x,
        ConventionalAcquisitionConfiguration y) => x.PhysicalChannelId == y.PhysicalChannelId &&
        x.SampleCount == y.SampleCount && x.SampleRateHz == y.SampleRateHz &&
        x.FirstSampleOffsetSeconds == y.FirstSampleOffsetSeconds && x.SignalMode == y.SignalMode;

    public IDisposable Subscribe(IObserver<SessionSnapshot> observer)
    {
        ArgumentNullException.ThrowIfNull(observer);
        lock (_gate) { ObjectDisposedException.ThrowIf(_disposed, this); _observers.Add(observer); observer.OnNext(_snapshot); }
        return new Subscription(this, observer);
    }

    private void Publish(SessionSnapshot snapshot)
    {
        IObserver<SessionSnapshot>[] observers;
        lock (_gate) { if (_disposed) return; _snapshot = snapshot; observers = _observers.ToArray(); }
        foreach (IObserver<SessionSnapshot> observer in observers) observer.OnNext(snapshot);
    }

    public async ValueTask DisposeAsync()
    {
        ProductiveGateInspectionScope? scope; IDisposable? subscription; IObserver<SessionSnapshot>[] observers;
        lock (_gate)
        {
            if (_disposed) return; _disposed = true; scope = _scope; subscription = _subscription;
            observers = _observers.ToArray(); _observers.Clear();
        }
        if (scope is not null)
        {
            try { await scope.DisposeAsync().ConfigureAwait(false); }
            catch when (_session?.Snapshot is { Phase: SessionPhase.Disposed, PrimaryError: not null } terminal &&
                terminal.CleanupErrors.Count == 0)
            {
                // The productive session already reports the producer failure; cleanup itself completed.
            }
        }
        subscription?.Dispose();
        lock (_gate)
        {
            if (_session?.Snapshot is { Phase: SessionPhase.Disposed } innerTerminal)
                _snapshot = innerTerminal;
            else if (_snapshot.Phase != SessionPhase.Disposed)
                _snapshot = new(_snapshot.Version + 1, SessionPhase.Disposed, _snapshot.SourceId,
                    _snapshot.RunId, _snapshot.ReceivedFrames, _snapshot.ReleasedFrames, _snapshot.LastSequence,
                    _snapshot.LastMetadata, _snapshot.PrimaryError, _snapshot.CleanupErrors,
                    _snapshot.AdditionalErrorCount, _snapshot.VisualError, integrity: _snapshot.Integrity);
        }
        foreach (IObserver<SessionSnapshot> observer in observers) observer.OnCompleted();
    }

    private sealed class Forwarder(ProfiledSimulationSession owner) : IObserver<SessionSnapshot>
    { public void OnNext(SessionSnapshot value) => owner.Publish(value); public void OnError(Exception error) { } public void OnCompleted() { } }
    private sealed class Subscription(ProfiledSimulationSession owner, IObserver<SessionSnapshot> observer) : IDisposable
    { public void Dispose() { lock (owner._gate) owner._observers.Remove(observer); } }
}

internal sealed class SimulationProfileCompatibilityException(string code) : InvalidOperationException(code)
{ public string Code { get; } = code; }
