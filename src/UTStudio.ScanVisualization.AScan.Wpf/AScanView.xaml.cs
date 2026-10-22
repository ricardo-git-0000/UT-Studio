using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.ComponentModel;
using System.Windows.Media;
using UTStudio.Presentation;
using UTStudio.Contracts.Application;
using UTStudio.Domain.Acquisition;
using UTStudio.Visualization.Core;
using UTStudio.Visualization.Wpf.Controls;

namespace UTStudio.ScanVisualization.AScan.Wpf;

/// <summary>Complete reusable WPF A-Scan presentation. All context services are borrowed.</summary>
public partial class AScanView : UserControl, IAsyncDisposable
{
    public static readonly DependencyProperty ContextProperty = DependencyProperty.Register(nameof(Context),
        typeof(AScanComponentContext), typeof(AScanView), new PropertyMetadata(null, OnContextChanged));
    private static readonly DependencyPropertyKey StatePropertyKey = DependencyProperty.RegisterReadOnly(nameof(State),
        typeof(AScanViewState), typeof(AScanView), new PropertyMetadata(null));
    public static readonly DependencyProperty StateProperty = StatePropertyKey.DependencyProperty;
    private static readonly DependencyPropertyKey ErrorPropertyKey = DependencyProperty.RegisterReadOnly(nameof(Error),
        typeof(UtSourceError), typeof(AScanView), new PropertyMetadata(null));
    public static readonly DependencyProperty ErrorProperty = ErrorPropertyKey.DependencyProperty;
    private AScanViewModel? _viewModel;
    private Task? _close;
    private bool _contextAssigned;
    private bool _publishingVisual;
    private readonly AScanVisualSnapshotAdapter _visualAdapter = new();

    public AScanView()
    {
        InitializeComponent(); Loaded += OnLoaded; SizeChanged += OnVisualContextChanged;
        Scan.VisualContextChanged += OnVisualContextChanged;
        Scan.GateBeginCaptureRequested += OnGateBeginCaptureRequested;
        Scan.GateInteractionRequested += OnGateInteractionRequested;
    }
    public AScanComponentContext? Context { get => (AScanComponentContext?)GetValue(ContextProperty); set => SetValue(ContextProperty, value); }
    public AScanViewState? State => (AScanViewState?)GetValue(StateProperty);
    public UtSourceError? Error => (UtSourceError?)GetValue(ErrorProperty);
    internal AScanControl Renderer => Scan;
    internal AScanViewModel? ViewModel => _viewModel;

    private static void OnContextChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        var view = (AScanView)sender;
        if (view._contextAssigned || view._close is not null)
        { throw new InvalidOperationException("AScanView context is one-shot and cannot be replaced after activation."); }
        if (args.NewValue is AScanComponentContext context)
        {
            view._contextAssigned = true;
            if (view.IsLoaded) { view.Activate(context); }
        }
    }

    private void OnLoaded(object sender, RoutedEventArgs args)
    {
        if (_viewModel is null && _close is null && Context is { } context) { Activate(context); }
    }

    private void Activate(AScanComponentContext context)
    {
        ArgumentNullException.ThrowIfNull(context.SessionStates);
        ArgumentNullException.ThrowIfNull(context.Visuals);
        _viewModel = new(context.SessionStates, context.InitialSession, context.Visuals,
            new WpfUiDispatcher(Dispatcher), context.ReadStatistics, context.VisualStatus,
            context.TimeViewport, context.SelectionScope, context.GateEditing);
        Root.DataContext = _viewModel;
        _viewModel.PropertyChanged += OnViewModelChanged;
        PublishState();
        PublishVisualSnapshot();
        foreach (KeyBinding binding in InputBindings.OfType<KeyBinding>())
        {
            binding.Command = (binding.Key, binding.Modifiers) switch
            {
                (Key.Home, _) => _viewModel.ResetViewportCommand,
                (Key.Add, _) => _viewModel.ZoomInCommand,
                (Key.Subtract, _) => _viewModel.ZoomOutCommand,
                (Key.Left, ModifierKeys.Alt) => _viewModel.PanLeftCommand,
                (Key.Right, ModifierKeys.Alt) => _viewModel.PanRightCommand,
                _ => binding.Command
            };
        }
    }

    public Task CloseAsync()
    {
        if (!Dispatcher.CheckAccess())
        { return Dispatcher.InvokeAsync(CloseOnDispatcher).Task.Unwrap(); }
        return CloseOnDispatcher();
    }

    /// <summary>Cancels only this caller's wait; component cleanup continues to completion.</summary>
    public ValueTask CloseAsync(CancellationToken cancellationToken) =>
        new(CloseAsync().WaitAsync(cancellationToken));

    private Task CloseOnDispatcher() => _close ??= CloseCoreAsync();

    private async Task CloseCoreAsync()
    {
        Loaded -= OnLoaded;
        AScanViewModel? viewModel = _viewModel;
        _viewModel = null;
        if (viewModel is not null) { viewModel.PropertyChanged -= OnViewModelChanged; }
        Root.DataContext = null;
        Scan.VisualSnapshot = null;
        Scan.VisualContextChanged -= OnVisualContextChanged;
        Scan.GateBeginCaptureRequested -= OnGateBeginCaptureRequested;
        Scan.GateInteractionRequested -= OnGateInteractionRequested;
        SizeChanged -= OnVisualContextChanged;
        SetValue(StatePropertyKey, null);
        SetValue(ErrorPropertyKey, null);
        if (viewModel is not null)
        {
            try { await Task.Run(async () => await viewModel.DisposeAsync().ConfigureAwait(false)).ConfigureAwait(true); }
            catch (Exception error)
            {
                SetValue(ErrorPropertyKey, new UtSourceError("presentation.ascan.close", error.Message));
                throw;
            }
        }
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs args)
    { PublishState(); PublishVisualSnapshot(); }
    private void PublishState()
    {
        SetValue(StatePropertyKey, _viewModel?.State);
        SetValue(ErrorPropertyKey, _viewModel?.Error ?? _viewModel?.VisualError ?? _viewModel?.NotificationError);
    }

    private void OnVisualContextChanged(object? sender, EventArgs args) => PublishVisualSnapshot();

    private void PublishVisualSnapshot()
    {
        if (_publishingVisual || _close is not null || Scan.ActualWidth <= 0 || Scan.ActualHeight <= 0) return;
        _publishingVisual = true;
        try
        {
            DpiScale dpi = VisualTreeHelper.GetDpi(Scan);
            AScanViewState? state = _viewModel?.State;
            if (state?.VisualContent is { IsCoherent: true } content && _viewModel?.GateEditor is not null)
            {
                GateVisualTransform transform = _visualAdapter.CreateTransform(content, Scan.ActualWidth,
                    Scan.ActualHeight, dpi.DpiScaleX, dpi.DpiScaleY);
                _viewModel.SetGateVisualContext(transform);
                state = _viewModel.State;
            }
            Scan.VisualSnapshot = _visualAdapter.Adapt(state, Scan.ActualWidth, Scan.ActualHeight,
                dpi.DpiScaleX, dpi.DpiScaleY);
        }
        finally { _publishingVisual = false; }
    }

    public ValueTask DisposeAsync() => new(CloseAsync());

    private void OnCursorActivated(object sender, AScanCursorActivatedEventArgs args) => _viewModel?.ActivateCursor(args.Cursor);
    private void OnCursorMoveRequested(object sender, AScanCursorMoveRequestedEventArgs args) => _viewModel?.MoveCursor(args.Cursor, args.TimeSeconds);
    private void OnTimeZoomRequested(object sender, ScanTimeZoomRequestedEventArgs args) => _viewModel?.ZoomTime(args.AnchorSeconds, args.Factor);
    private void OnTimePanRequested(object sender, ScanTimePanRequestedEventArgs args) => _viewModel?.PanTime(args.DeltaSeconds);
    private void OnTimeResetRequested(object? sender, EventArgs args)
    { if (_viewModel?.ResetViewportCommand.CanExecute(null) == true) { _viewModel.ResetViewportCommand.Execute(null); } }

    private void OnGateBeginCaptureRequested(object? sender, GateBeginCaptureRequestedEventArgs args)
    {
        if (_viewModel?.GateEditor?.TryBeginCapture(args.Snapshot, args.Handle.Identity, out GateCaptureGeneration capture) == true)
            args.Accept(capture);
    }

    private void OnGateInteractionRequested(object? sender, GateInteractionRequestedEventArgs args) =>
        _viewModel?.GateEditor?.HandleInteraction(new(args.Kind, args.Snapshot, args.Handle,
            args.Capture, args.Value));
}
