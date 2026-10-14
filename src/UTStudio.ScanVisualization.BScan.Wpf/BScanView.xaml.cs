using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using UTStudio.Contracts.Inspection;
using UTStudio.Presentation;
using UTStudio.Visualization.Core.Scan2D;
using UTStudio.Visualization.Wpf.Controls;

namespace UTStudio.ScanVisualization.BScan.Wpf;

/// <summary>Complete reusable WPF B-Scan presentation. All context services are borrowed.</summary>
public partial class BScanView : UserControl, IAsyncDisposable
{
    public static readonly DependencyProperty ContextProperty = DependencyProperty.Register(nameof(Context),
        typeof(BScanComponentContext), typeof(BScanView), new PropertyMetadata(null, OnContextChanged));
    public static readonly DependencyProperty PaletteLoaderProperty = DependencyProperty.Register(nameof(PaletteLoader),
        typeof(IBScanPaletteLoader), typeof(BScanView), new PropertyMetadata(null, OnPaletteLoaderChanged));
    private static readonly DependencyPropertyKey StatePropertyKey = DependencyProperty.RegisterReadOnly(nameof(State),
        typeof(BScanViewState), typeof(BScanView), new PropertyMetadata(null));
    public static readonly DependencyProperty StateProperty = StatePropertyKey.DependencyProperty;
    private static readonly DependencyPropertyKey PhasePropertyKey = DependencyProperty.RegisterReadOnly(nameof(Phase),
        typeof(BScanComponentPhase), typeof(BScanView), new PropertyMetadata(BScanComponentPhase.NoData));
    public static readonly DependencyProperty PhaseProperty = PhasePropertyKey.DependencyProperty;
    private static readonly DependencyPropertyKey ErrorPropertyKey = DependencyProperty.RegisterReadOnly(nameof(Error),
        typeof(string), typeof(BScanView), new PropertyMetadata(null));
    public static readonly DependencyProperty ErrorProperty = ErrorPropertyKey.DependencyProperty;

    private readonly object _closeGate = new();
    private BScanViewModel? _viewModel;
    private BScanControlAdapter? _adapter;
    private BScanControlAdapter? _diagnosticAdapter;
    private BScanAdapterDiagnostics? _lastAdapterDiagnostics;
    private Task? _activation;
    private Task? _close;
    private bool _contextAssigned;
    private bool _updatingResamplingSelector;
    private long _resamplingIntentCount;

    public BScanView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        ResamplingSelector.ItemsSource = DisplayOptions;
        ResamplingSelector.SelectedValuePath = nameof(BScanResamplingDisplayOption.Mode);
        ResamplingSelector.DisplayMemberPath = nameof(BScanResamplingDisplayOption.DisplayName);
        UpdatePaletteAvailability();
    }

    public BScanComponentContext? Context { get => (BScanComponentContext?)GetValue(ContextProperty); set => SetValue(ContextProperty, value); }
    public IBScanPaletteLoader? PaletteLoader { get => (IBScanPaletteLoader?)GetValue(PaletteLoaderProperty); set => SetValue(PaletteLoaderProperty, value); }
    public BScanViewState? State => (BScanViewState?)GetValue(StateProperty);
    public BScanComponentPhase Phase => (BScanComponentPhase)GetValue(PhaseProperty);
    public string? Error => (string?)GetValue(ErrorProperty);
    public string? ConsumerId => _viewModel?.ConsumerId;
    internal BScanViewModel? ViewModel => _viewModel;
    internal BScanControl Renderer => Scan;
    internal Scan2DInterpolationMode? SelectedResamplingMode => ResamplingSelector.SelectedValue as Scan2DInterpolationMode?;
    internal bool IsLanczosWarningVisible => LanczosWarning.Visibility == Visibility.Visible;
    internal long ResamplingIntentCount => Interlocked.Read(ref _resamplingIntentCount);
    internal void SelectResamplingMode(Scan2DInterpolationMode mode) => ResamplingSelector.SelectedValue = mode;
    internal Action<BScanAdapterConstructionStage>? AdapterConstructionProbe { get; set; }
    internal Action<string>? AdapterCleanupProbe { get; set; }
    internal Action<BScanRasterCopyStage, BScanPresentedRegionToken>? RasterCopyProbe { get; set; }
    internal Func<BScanViewModel, Task>? BeforeActivationCommit { get; set; }
    internal BScanAdapterDiagnostics? AdapterDiagnostics => _adapter?.Diagnostics ?? _diagnosticAdapter?.Diagnostics ?? _lastAdapterDiagnostics;
    internal bool TryDiagnosticVisit(BScanPresentedRegionToken token) =>
        (_adapter ?? _diagnosticAdapter)?.TryDiagnosticVisit(token) ?? false;
    internal Func<BScanComponentContext, IUiDispatcher, BScanViewModel>? ViewModelFactory { get; set; }
    internal Task Activation => _activation ?? Task.CompletedTask;

    private static void OnContextChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        var view = (BScanView)sender;
        if (view._contextAssigned || view._close is not null)
        { throw new InvalidOperationException("BScanView context is one-shot and cannot be replaced after activation."); }
        if (args.NewValue is BScanComponentContext context)
        {
            view._contextAssigned = true;
            if (view.IsLoaded) { view.ActivateAfterContextChanged(context); }
        }
    }

    private async void ActivateAfterContextChanged(BScanComponentContext context)
    {
        try { await StartActivation(context); }
        catch (Exception error)
        {
            SetValue(ErrorPropertyKey, error.Message);
            SetValue(PhasePropertyKey, BScanComponentPhase.Error);
        }
    }

    private static void OnPaletteLoaderChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args) =>
        ((BScanView)sender).UpdatePaletteAvailability();

    private async void OnLoaded(object sender, RoutedEventArgs args)
    {
        if (_viewModel is not null || _close is not null || Context is not { } context) { return; }
        try { await StartActivation(context); }
        catch (Exception error)
        {
            SetValue(ErrorPropertyKey, error.Message);
            SetValue(PhasePropertyKey, BScanComponentPhase.Error);
        }
    }

    private Task StartActivation(BScanComponentContext context) => _activation ??= ActivateAsync(context);

    private async Task ActivateAsync(BScanComponentContext context)
    {
        ArgumentNullException.ThrowIfNull(context.Visuals);
        ArgumentNullException.ThrowIfNull(context.TimeViewport);
        ArgumentNullException.ThrowIfNull(context.SelectionScope);
        IUiDispatcher dispatcher = new WpfUiDispatcher(Dispatcher);
        BScanViewModel viewModel = ViewModelFactory?.Invoke(context, dispatcher) ??
            new(context.Visuals, context.TimeViewport, context.SelectionScope, dispatcher,
                context.InitialResamplingMode, context.ResamplingPreferences);
        BScanControlAdapter? adapter = null;
        bool viewSubscription = false;
        try
        {
            adapter = new(viewModel, Scan, AdapterConstructionProbe, AdapterCleanupProbe, RasterCopyProbe);
            if (BeforeActivationCommit is not null) { await BeforeActivationCommit(viewModel).ConfigureAwait(true); }
            adapter.Activate(() =>
            {
                BindKeys(viewModel);
                _viewModel = viewModel;
                _adapter = adapter;
                viewModel.PropertyChanged += OnViewModelChanged;
                viewSubscription = true;
                Root.DataContext = viewModel;
                PublishState();
            }, AdapterConstructionProbe);
        }
        catch (Exception primary)
        {
            List<CleanupStageFailure> failures = [];
            _viewModel = null;
            _adapter = null;
            if (viewSubscription)
            {
                try { viewModel.PropertyChanged -= OnViewModelChanged; }
                catch (Exception error) { failures.Add(new("view-subscription", error)); }
            }
            try { adapter?.Dispose(); }
            catch (Exception error) { failures.Add(new("adapter", error)); }
            try { await viewModel.DisposeAsync().ConfigureAwait(true); }
            catch (CleanupFailureException error)
            {
                failures.Add(error.PrimaryFailure);
                failures.AddRange(error.SecondaryFailures);
            }
            catch (Exception error) { failures.Add(new("viewmodel", error)); }
            Root.DataContext = null;
            ClearKeyBindings();
            if (failures.Count == 0) { throw; }
            throw new CleanupFailureException("B-Scan activation failed and rollback reported failures.",
                new("activation", primary), failures);
        }
    }

    private void BindKeys(BScanViewModel viewModel)
    {
        foreach (KeyBinding binding in InputBindings.OfType<KeyBinding>())
        {
            binding.Command = (binding.Key, binding.Modifiers) switch
            {
                (Key.Home, _) => viewModel.ResetAllCommand,
                (Key.Add, _) => viewModel.ZoomTimeInCommand,
                (Key.Subtract, _) => viewModel.ZoomTimeOutCommand,
                (Key.Left, ModifierKeys.Alt) => viewModel.PanSpatialLeftCommand,
                (Key.Right, ModifierKeys.Alt) => viewModel.PanSpatialRightCommand,
                (Key.Up, ModifierKeys.Alt) => viewModel.PanTimeUpCommand,
                (Key.Down, ModifierKeys.Alt) => viewModel.PanTimeDownCommand,
                _ => binding.Command
            };
        }
    }

    private void ClearKeyBindings()
    {
        foreach (KeyBinding binding in InputBindings.OfType<KeyBinding>()) { binding.Command = null; }
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs args) => PublishState();

    private void PublishState()
    {
        BScanViewState? state = _viewModel?.State;
        SetValue(StatePropertyKey, state);
        SetValue(PhasePropertyKey, ComponentPhase(state));
        SetValue(ErrorPropertyKey, ComponentError(state));
        if (_viewModel is not null) { SynchronizeResamplingSelector(); }
    }

    private static BScanComponentPhase ComponentPhase(BScanViewState? state)
    {
        if (state is null) { return BScanComponentPhase.NoData; }
        if (state.Phase == BScanViewPhase.Closed) { return BScanComponentPhase.Closed; }
        if (ComponentError(state) is not null || state.Region?.Snapshot.RunState == InspectionRunState.Faulted)
        { return BScanComponentPhase.Error; }
        if (state.Phase == BScanViewPhase.NoData) { return BScanComponentPhase.NoData; }
        if (state.Selection.Identity is not null) { return BScanComponentPhase.Selected; }
        return state.Region?.Snapshot.RunState == InspectionRunState.Sealed
            ? BScanComponentPhase.Frozen : BScanComponentPhase.Live;
    }

    private static string? ComponentError(BScanViewState? state) => state?.SelectionError ?? state?.BScanVisualError ??
        state?.BScanIngressError ?? state?.UiInteractionError ?? state?.PersistenceError ?? state?.ConfigurationError ??
        state?.AcquisitionError;

    private void OnResamplingSelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        if (_updatingResamplingSelector || _viewModel is null || ResamplingSelector.SelectedValue is not Scan2DInterpolationMode mode)
        { return; }
        Interlocked.Increment(ref _resamplingIntentCount);
        if (_viewModel.ChangeResamplingModeCommand.CanExecute(mode)) { _viewModel.ChangeResamplingModeCommand.Execute(mode); }
    }

    private void SynchronizeResamplingSelector()
    {
        _updatingResamplingSelector = true;
        try
        {
            Scan2DInterpolationMode mode = _viewModel!.RequestedResamplingMode;
            ResamplingSelector.SelectedValue = mode;
            LanczosWarning.Visibility = mode == Scan2DInterpolationMode.Lanczos ? Visibility.Visible : Visibility.Collapsed;
        }
        finally { _updatingResamplingSelector = false; }
    }

    private void OnLoadPalette(object sender, RoutedEventArgs args)
    {
        if (PaletteLoader is null || _adapter is null) { return; }
        try
        {
            BScanPaletteSelection? selection = PaletteLoader.SelectPalette(Window.GetWindow(this));
            if (selection is null) { return; }
            _adapter.SetPalette(selection.Palette);
            PaletteStatus.Text = $"Paleta: {selection.Name}";
        }
        catch (Exception error)
        {
            _viewModel?.ReportUiInteractionError(error.Message);
            PaletteStatus.Text = $"Paleta no cargada: {error.Message}";
        }
    }

    private void UpdatePaletteAvailability() => LoadPaletteButton.IsEnabled = PaletteLoader is not null;

    public Task CloseAsync()
    {
        if (!Dispatcher.CheckAccess()) { return Dispatcher.InvokeAsync(CloseOnDispatcher).Task.Unwrap(); }
        return CloseOnDispatcher();
    }

    public ValueTask CloseAsync(CancellationToken cancellationToken) => new(CloseAsync().WaitAsync(cancellationToken));

    private Task CloseOnDispatcher()
    { lock (_closeGate) { return _close ??= CloseCoreAsync(); } }

    private async Task CloseCoreAsync()
    {
        List<CleanupStageFailure> failures = [];
        Loaded -= OnLoaded;
        if (_activation is { IsCompleted: false } activation)
        {
            try { await activation.ConfigureAwait(true); }
            catch { }
        }
        BScanViewModel? viewModel = _viewModel;
        _viewModel = null;
        if (viewModel is not null) { viewModel.PropertyChanged -= OnViewModelChanged; }
        try
        {
            if (RasterCopyProbe is not null) { _diagnosticAdapter = _adapter; }
            _adapter?.Dispose();
            _lastAdapterDiagnostics = _adapter?.Diagnostics;
        }
        catch (Exception error) { failures.Add(new("adapter", error)); }
        _adapter = null;
        Root.DataContext = null;
        ClearKeyBindings();
        if (viewModel is not null)
        {
            try { await viewModel.DisposeAsync().ConfigureAwait(true); }
            catch (Exception error) { failures.Add(new("viewmodel", error)); }
        }
        if (failures.Count == 0)
        {
            SetValue(StatePropertyKey, null);
            SetValue(PhasePropertyKey, BScanComponentPhase.Closed);
            return;
        }
        var cleanup = new CleanupFailureException("B-Scan component cleanup failed.", failures[0], failures.Skip(1).ToArray());
        SetValue(ErrorPropertyKey, cleanup.Message);
        SetValue(PhasePropertyKey, BScanComponentPhase.Error);
        throw cleanup;
    }

    public ValueTask DisposeAsync() => new(CloseAsync());

    internal sealed record BScanResamplingDisplayOption(Scan2DInterpolationMode Mode, string DisplayName);
    internal static IReadOnlyList<BScanResamplingDisplayOption> DisplayOptions { get; } =
    [
        new(Scan2DInterpolationMode.None, "Sin interpolación"),
        new(Scan2DInterpolationMode.NearestNeighbor, "Vecino más próximo"),
        new(Scan2DInterpolationMode.Bilinear, "Bilineal — recomendado"),
        new(Scan2DInterpolationMode.Bicubic, "Bicúbica — recomendado"),
        new(Scan2DInterpolationMode.Lanczos, "Lanczos-3 — alta calidad")
    ];
}
