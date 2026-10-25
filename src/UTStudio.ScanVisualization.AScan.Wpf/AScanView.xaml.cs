using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.ComponentModel;
using System.Globalization;
using System.Windows.Media;
using System.Windows.Automation;
using System.Windows.Threading;
using UTStudio.Presentation;
using UTStudio.Contracts.Application;
using UTStudio.Domain.Acquisition;
using UTStudio.Domain.Processing;
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
    private bool _visualPublishPending;
    private readonly Dictionary<TextBox, ulong> _numericEditGenerations = [];
    private readonly AScanVisualSnapshotAdapter _visualAdapter = new();

    public AScanView()
    {
        InitializeComponent(); Loaded += OnLoaded; SizeChanged += OnVisualContextChanged;
        Scan.VisualContextChanged += OnVisualContextChanged;
        Scan.GateBeginCaptureRequested += OnGateBeginCaptureRequested;
        Scan.GateBeginWindowCaptureRequested += OnGateBeginWindowCaptureRequested;
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
            context.TimeViewport, context.SelectionScope, context.GateEditing, context.DeferredGateEditing);
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
        _numericEditGenerations.Clear();
        Loaded -= OnLoaded;
        AScanViewModel? viewModel = _viewModel;
        _viewModel = null;
        if (viewModel is not null) { viewModel.PropertyChanged -= OnViewModelChanged; }
        Root.DataContext = null;
        Scan.VisualSnapshot = null;
        Scan.VisualContextChanged -= OnVisualContextChanged;
        Scan.GateBeginCaptureRequested -= OnGateBeginCaptureRequested;
        Scan.GateBeginWindowCaptureRequested -= OnGateBeginWindowCaptureRequested;
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
        PublishGateEditorFields();
    }

    private void PublishGateEditorFields()
    {
        GateEditorViewCoordinator? editor = _viewModel?.GateEditor;
        if (editor?.SelectedGateId is null) ClearAllNumericFieldErrors();
        PublishNumericField(GateStartValue, editor?.SelectedGateStartMicroseconds, "0.###");
        PublishNumericField(GateEndValue, editor?.SelectedGateEndMicroseconds, "0.###");
        PublishNumericField(GateThresholdValue, editor?.SelectedGateThresholdPercent, "0.##");
        PublishNumericField(GateRadiusValue, editor?.SelectedGateNeighborhoodRadius, "0");
        PublishNumericField(GateProminenceValue, editor?.SelectedGateMinimumProminence, "0.###");
        PublishNumericField(GateDistanceValue, editor?.SelectedGateMinimumDistance, "0");
        if (editor?.SelectedGateHasThreshold != true)
            ClearNumericFieldError(GateThresholdValue, GateHandleKind.Threshold);
        if (editor?.SelectedGateHasLocalParameters != true)
        {
            GateRadiusValidation.Text = string.Empty; GateRadiusValidation.Visibility = Visibility.Collapsed;
            GateProminenceValidation.Text = string.Empty; GateProminenceValidation.Visibility = Visibility.Collapsed;
            GateDistanceValidation.Text = string.Empty; GateDistanceValidation.Visibility = Visibility.Collapsed;
            AutomationProperties.SetItemStatus(GateRadiusValue, string.Empty);
            AutomationProperties.SetItemStatus(GateProminenceValue, string.Empty);
            AutomationProperties.SetItemStatus(GateDistanceValue, string.Empty);
        }
    }

    private void OnGateParameterLostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs args)
    {
        if (sender is not TextBox field || _viewModel?.GateEditor is not { } editor || field.Tag is not string name) return;
        if (!double.TryParse(field.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out double value) ||
            !double.IsFinite(value) || name is "Radius" or "Distance" &&
                (value < 1 || value > int.MaxValue || value != Math.Truncate(value)) ||
            name == "Prominence" && value < 0)
        {
            const string message = "Introduzca un valor válido para el parámetro.";
            TextBlock error = ParameterError(name);
            error.Text = message; error.Visibility = Visibility.Visible;
            AutomationProperties.SetItemStatus(field, $"Error: {message}");
            editor.SetDefinitionInputValidity(name, false); return;
        }
        AutomationProperties.SetItemStatus(field, string.Empty);
        TextBlock validation = ParameterError(name);
        validation.Text = string.Empty; validation.Visibility = Visibility.Collapsed;
        editor.SetDefinitionInputValidity(name, true);
        switch (name)
        {
            case "Radius": editor.SelectedGateNeighborhoodRadius = checked((int)value); break;
            case "Prominence": editor.SelectedGateMinimumProminence = value; break;
            case "Distance": editor.SelectedGateMinimumDistance = checked((int)value); break;
        }
    }

    private TextBlock ParameterError(string name) => name switch
    {
        "Radius" => GateRadiusValidation,
        "Prominence" => GateProminenceValidation,
        "Distance" => GateDistanceValidation,
        _ => throw new ArgumentOutOfRangeException(nameof(name))
    };

    private static void PublishNumericField(TextBox field, double? value, string format)
    {
        if (field.IsKeyboardFocusWithin || !string.IsNullOrEmpty(AutomationProperties.GetItemStatus(field))) return;
        field.Text = value?.ToString(format, CultureInfo.CurrentCulture) ?? string.Empty;
    }

    private async void OnGateNumericTextChanged(object sender, TextChangedEventArgs args)
    {
        if (sender is not TextBox field || !field.IsKeyboardFocusWithin || _close is not null) return;
        ulong generation = NextNumericEditGeneration(field);
        string text = field.Text;
        await Task.Delay(TimeSpan.FromMilliseconds(150));
        if (_close is not null || !_numericEditGenerations.TryGetValue(field, out ulong current) ||
            current != generation || field.Text != text) return;
        await CommitGateNumericAsync(field);
    }

    private async void OnGateNumericLostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs args)
    {
        if (sender is not TextBox field) return;
        NextNumericEditGeneration(field);
        await CommitGateNumericAsync(field);
    }

    private ulong NextNumericEditGeneration(TextBox field)
    {
        _numericEditGenerations.TryGetValue(field, out ulong generation);
        generation = checked(generation + 1);
        _numericEditGenerations[field] = generation;
        return generation;
    }

    private async Task CommitGateNumericAsync(TextBox field)
    {
        if (_viewModel?.GateEditor is not { } editor ||
            editor.SelectedGateId is not { } selected) return;
        GateHandleKind kind = (field.Tag as string) switch
        {
            "Start" => GateHandleKind.Start,
            "End" => GateHandleKind.End,
            "Threshold" => GateHandleKind.Threshold,
            _ => GateHandleKind.Unknown
        };
        if (kind == GateHandleKind.Unknown) return;
        if (!double.TryParse(field.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out double displayed) ||
            !double.IsFinite(displayed))
        {
            SetNumericFieldError(field, kind, "Introduzca un número válido.");
            editor.SetNumericInputValidity(kind, false);
            return;
        }
        ClearNumericFieldError(field, kind);
        editor.SetNumericInputValidity(kind, true);
        double value = kind == GateHandleKind.Threshold ? (displayed + 100d) / 200d : displayed / 1_000_000d;
        if (kind == GateHandleKind.End && editor.SelectedGateReference != "origin" &&
            editor.SelectedGateStartMicroseconds is { } startMicroseconds)
            value += startMicroseconds / 1_000_000d;
        try { await editor.PreviewHandleValueAsync(new GateId(selected), kind, value); }
        catch (Exception error) { SetNumericFieldError(field, kind, error.Message); editor.SetNumericInputValidity(kind, false); }
        finally { PublishGateEditorFields(); }
    }

    private void SetNumericFieldError(TextBox field, GateHandleKind kind, string message)
    {
        TextBlock error = NumericError(kind);
        error.Text = message; error.Visibility = Visibility.Visible;
        AutomationProperties.SetItemStatus(field, $"Error: {message}");
    }

    private void ClearNumericFieldError(TextBox field, GateHandleKind kind)
    {
        TextBlock error = NumericError(kind);
        error.Text = string.Empty; error.Visibility = Visibility.Collapsed;
        AutomationProperties.SetItemStatus(field, string.Empty);
    }

    private TextBlock NumericError(GateHandleKind kind) => kind switch
    {
        GateHandleKind.Start => GateStartValidation,
        GateHandleKind.End => GateEndValidation,
        GateHandleKind.Threshold => GateThresholdValidation,
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    private void ClearAllNumericFieldErrors()
    {
        ClearNumericFieldError(GateStartValue, GateHandleKind.Start);
        ClearNumericFieldError(GateEndValue, GateHandleKind.End);
        ClearNumericFieldError(GateThresholdValue, GateHandleKind.Threshold);
    }

    private void OnVisualContextChanged(object? sender, EventArgs args) => PublishVisualSnapshot();

    private void PublishVisualSnapshot()
    {
        if (_publishingVisual)
        {
            _visualPublishPending = true;
            return;
        }
        if (_close is not null || Scan.ActualWidth <= 0 || Scan.ActualHeight <= 0) return;
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
        finally
        {
            _publishingVisual = false;
            if (_visualPublishPending && _close is null)
            {
                _visualPublishPending = false;
                _ = Dispatcher.BeginInvoke(DispatcherPriority.Render, PublishVisualSnapshot);
            }
        }
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

    private void OnGateBeginWindowCaptureRequested(object? sender, GateBeginWindowCaptureRequestedEventArgs args)
    {
        if (_viewModel?.GateEditor?.TryBeginWindowCapture(args.Snapshot, args.GateId,
            out GateCaptureGeneration capture, out double initialStart) == true)
            args.Accept(capture, initialStart);
    }

    private void OnGateInteractionRequested(object? sender, GateInteractionRequestedEventArgs args) =>
        _viewModel?.GateEditor?.HandleInteraction(new(args.Kind, args.Snapshot, args.Handle,
            args.Capture, args.Value));
}
