using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UTStudio.Contracts.Application;
using UTStudio.Domain.Acquisition;
using UTStudio.Contracts.Acquisition;
using System.ComponentModel;

namespace UTStudio.Presentation;

/// <summary>Host-owned session commands and state; contains no visual A-Scan presentation.</summary>
public sealed class AScanHostViewModel : ObservableObject, IAsyncDisposable
{
    private readonly object _disposeGate = new();
    private readonly IApplicationSession _session;
    private readonly ConventionalAcquisitionConfiguration _configuration;
    private readonly Func<AcquisitionRunId> _newRun;
    private readonly ISimulationProfileSelection? _profileSelection;
    private readonly UiLifetime _lifetime;
    private readonly UiAsyncCommand _start, _stop;
    private readonly IDisposable _subscription;
    private SessionSnapshot _state;
    private UtSourceError? _error;
    private Task? _disposal;
    private int _closing;

    public AScanHostViewModel(IApplicationSession session, IUiDispatcher dispatcher,
        ConventionalAcquisitionConfiguration configuration, Func<AcquisitionRunId>? newRun = null,
        ISimulationProfileSelection? profileSelection = null)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _newRun = newRun ?? (() => new AcquisitionRunId(Guid.NewGuid()));
        _profileSelection = profileSelection;
        _lifetime = new(dispatcher ?? throw new ArgumentNullException(nameof(dispatcher)));
        _state = session.Snapshot;
        _start = new(_lifetime, StartAsync, () => Volatile.Read(ref _closing) == 0 && State.CanStart && !_start!.IsRunning && !_stop!.IsRunning,
            ReportError, Refresh);
        _stop = new(_lifetime, StopAsync, () => Volatile.Read(ref _closing) == 0 && (State.CanStop || _start.IsRunning) && !_stop!.IsRunning,
            ReportError, Refresh);
        _start.Initialize(); _stop.Initialize();
        _subscription = session.Subscribe(new Observer(this));
    }

    public SessionSnapshot State { get => _state; private set { if (SetProperty(ref _state, value)) { OnPropertyChanged(nameof(Session)); Refresh(); } } }
    public SessionSnapshot Session => State;
    public IAsyncRelayCommand StartCommand => _start;
    public IAsyncRelayCommand StopCommand => _stop;
    public UtSourceError? Error => _error ?? State.PrimaryError;
    public IReadOnlyList<SimulationProfileOption> SimulationProfiles =>
        _profileSelection?.Options ?? Array.Empty<SimulationProfileOption>();
    public string? SelectedSimulationProfileId
    {
        get => _profileSelection?.SelectedProfileId;
        set
        {
            if (_profileSelection is null || value is null || value == _profileSelection.SelectedProfileId) return;
            _profileSelection.SelectProfile(value);
            OnPropertyChanged(); OnPropertyChanged(nameof(CanSelectSimulationProfile));
        }
    }
    public bool CanSelectSimulationProfile => _profileSelection?.CanSelectProfile == true;

    private async Task StartAsync(CancellationToken token)
    {
        await ClearErrorAsync().ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        try { await _session.StartAsync(_configuration, _newRun(), token).ConfigureAwait(false); }
        finally
        {
            await SynchronizeSessionAsync().ConfigureAwait(false);
            await _lifetime.InvokeAsync(() =>
            {
                OnPropertyChanged(nameof(CanSelectSimulationProfile));
                OnPropertyChanged(nameof(SelectedSimulationProfileId));
            }).ConfigureAwait(false);
        }
    }

    private async Task StopAsync(CancellationToken token)
    {
        Task startCancellation = _start.CancelPending();
        await ClearErrorAsync().ConfigureAwait(false);
        try { await _session.StopAsync(token).ConfigureAwait(false); }
        finally
        {
            await startCancellation.ConfigureAwait(false);
            await SynchronizeSessionAsync().ConfigureAwait(false);
        }
    }

    private Task ClearErrorAsync() => _lifetime.InvokeAsync(() =>
    { _error = null; OnPropertyChanged(nameof(Error)); });
    private async Task SynchronizeSessionAsync()
    {
        Publish(_session.Snapshot);
        await _lifetime.InvokeAsync(() => { }).ConfigureAwait(false);
    }
    private void Publish(SessionSnapshot state) => _lifetime.Post(() => State = state);
    private void Refresh() { _start.NotifyCanExecuteChanged(); _stop.NotifyCanExecuteChanged(); }
    private void ReportError(Exception error) => _lifetime.Post(() =>
    { _error = new("presentation.host", error.Message); OnPropertyChanged(nameof(Error)); });

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        try { base.OnPropertyChanged(e); }
        catch (Exception error)
        {
            if (_error is not null) { return; }
            _error = new("presentation.host.observer", error.Message);
            try { base.OnPropertyChanged(new PropertyChangedEventArgs(nameof(Error))); } catch { }
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (_disposeGate) { _disposal ??= CompleteDisposeAsync(); }
        return new(_disposal!);
    }

    private async Task CompleteDisposeAsync()
    {
        if (Interlocked.Exchange(ref _closing, 1) != 0) { return; }
        List<Exception> errors = [];
        _start.DisableForClose(); _stop.DisableForClose();
        try { _subscription.Dispose(); } catch (Exception error) { errors.Add(error); }
        try { await Task.WhenAll(_start.FinishForCloseAsync(), _stop.FinishForCloseAsync()).ConfigureAwait(false); }
        catch (Exception error) { errors.Add(error); }
        try { await _lifetime.InvokeAsync(_lifetime.Close).ConfigureAwait(false); }
        catch (Exception error) { _lifetime.Close(); errors.Add(error); }
        try { await _lifetime.BarrierAsync().ConfigureAwait(false); }
        catch (Exception error) { errors.Add(error); }
        if (errors.Count != 0) { throw new AggregateException("A-Scan host cleanup failed.", errors); }
    }

    private sealed class Observer(AScanHostViewModel owner) : IObserver<SessionSnapshot>
    { public void OnNext(SessionSnapshot value) => owner.Publish(value); public void OnError(Exception error) => owner.ReportError(error); public void OnCompleted() { } }
}
