using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using UTStudio.Contracts.Inspection;
using UTStudio.Contracts.Processing;
using UTStudio.Domain.Acquisition;
using UTStudio.Domain.Processing;
using UTStudio.Visualization.Core;

namespace UTStudio.Visualization.Wpf.Controls;

internal readonly record struct GateHandleAutomationIdentity(
    AcquisitionRunId RunId, TraceIdentity TraceIdentity, GatePlanAuthorityKey Authority,
    GatePlanRevision PlanRevision, GateId GateId, GateRevision GateRevision, GateHandleKind Kind,
    GateId AnchorGateId, GateRevision AnchorRevision)
{
    internal static GateHandleAutomationIdentity Create(AScanCompositeVisualSnapshot snapshot,
        GateHandleVisualState handle) => new(snapshot.Signal.Metadata.RunId, snapshot.Signal.TraceIdentity!.Value,
            snapshot.Gates.Source!.Authority, snapshot.Gates.Source.Revision,
            handle.GateId, handle.GateRevision, handle.Kind, handle.AnchorGateId, handle.AnchorRevision);

    internal bool Matches(AScanCompositeVisualSnapshot snapshot, GateHandleVisualState handle) =>
        snapshot.Signal.Metadata.RunId == RunId && snapshot.Signal.TraceIdentity == TraceIdentity &&
        snapshot.Gates.Source?.Authority == Authority && snapshot.Gates.Source?.Revision == PlanRevision &&
        handle.GateId == GateId && handle.GateRevision == GateRevision && handle.Kind == Kind &&
        handle.AnchorGateId == AnchorGateId && handle.AnchorRevision == AnchorRevision;
}

internal sealed class AScanControlAutomationPeer : FrameworkElementAutomationPeer
{
    private readonly Dictionary<GateHandleAutomationIdentity, GateHandleAutomationPeer> _peers = [];
    private GateHandleAutomationPeer[] _ordered = [];

    internal AScanControlAutomationPeer(AScanControl owner) : base(owner) { }
    private AScanControl Control => (AScanControl)Owner;
    protected override string GetClassNameCore() => nameof(AScanControl);
    protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Pane;

    internal void Update(AScanCompositeVisualSnapshot? snapshot)
    {
        Control.OnDispatcher(() =>
        {
            GateHandleAutomationIdentity[] identities = snapshot is { HasAuthoritativeIdentity: true } &&
                snapshot.Gates.Status is GateOverlayGeometryStatus.Current or GateOverlayGeometryStatus.Evaluating &&
                snapshot.Gates.Source is not null
                ? snapshot.Gates.Handles.Select(handle => GateHandleAutomationIdentity.Create(snapshot, handle))
                    .Distinct().ToArray() : [];
            bool structureChanged = !_ordered.Select(peer => peer.Identity).SequenceEqual(identities);
            var retained = identities.ToHashSet();
            foreach ((GateHandleAutomationIdentity identity, GateHandleAutomationPeer peer) in _peers.ToArray())
                if (!retained.Contains(identity)) { peer.Retire(); _peers.Remove(identity); }
            foreach (GateHandleAutomationIdentity identity in identities)
            {
                GateHandleVisualState handle = snapshot!.Gates.Handles.First(candidate =>
                    GateHandleAutomationIdentity.Create(snapshot, candidate) == identity);
                if (_peers.TryGetValue(identity, out GateHandleAutomationPeer? peer)) peer.Update(handle);
                else _peers.Add(identity, new(Control, this, identity, handle));
            }
            _ordered = identities.Select(identity => _peers[identity]).ToArray();
            if (structureChanged) { ResetChildrenCache(); InvalidatePeer(); }
        });
    }

    protected override List<AutomationPeer>? GetChildrenCore() => Control.OnDispatcher(() =>
        _ordered.Length == 0 ? null : _ordered.Cast<AutomationPeer>().ToList());
}

internal sealed class GateHandleAutomationPeer : AutomationPeer, IRangeValueProvider, ISelectionItemProvider
{
    private readonly AScanControl _owner;
    private readonly AScanControlAutomationPeer _parent;
    private readonly GateHandleAutomationIdentity _identity;
    private GateHandleVisualState _lastKnown;
    private bool _retired;

    internal GateHandleAutomationPeer(AScanControl owner, AScanControlAutomationPeer parent,
        GateHandleAutomationIdentity identity, GateHandleVisualState initial)
    { _owner = owner; _parent = parent; _identity = identity; _lastKnown = initial; }

    internal void Retire() => _retired = true;
    internal void Update(GateHandleVisualState handle) => _lastKnown = handle;
    internal GateHandleAutomationIdentity Identity => _identity;
    private GateHandleVisualState Current() => _owner.OnDispatcher(() => !_retired &&
        _owner.FindCurrentHandle(_identity) is { } handle ? handle : throw new ElementNotAvailableException());
    private GateHandleVisualState PropertyState() => _owner.OnDispatcher(() =>
    {
        if (!_retired && _owner.FindCurrentHandle(_identity) is { } handle) _lastKnown = handle;
        return _lastKnown;
    });
    protected override string GetClassNameCore() => "GateHandle";
    protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Slider;
    protected override string GetAutomationIdCore() => $"gate-{_identity.GateId.Value}-{_identity.GateRevision.Value}-{_identity.Kind}";
    protected override string GetAcceleratorKeyCore() => string.Empty;
    protected override string GetAccessKeyCore() => string.Empty;
    protected override string GetItemTypeCore() => "Gate handle";
    protected override string GetItemStatusCore() => !_retired && PropertyState().IsEnabled ? "Editable" : "Unavailable";
    protected override AutomationOrientation GetOrientationCore() => _identity.Kind == GateHandleKind.Threshold
        ? AutomationOrientation.Vertical : AutomationOrientation.Horizontal;
    protected override AutomationPeer? GetLabeledByCore() => null;
    protected override List<AutomationPeer>? GetChildrenCore() => null;
    protected override bool IsPasswordCore() => false;
    protected override bool IsRequiredForFormCore() => false;
    protected override bool IsControlElementCore() => true;
    protected override bool IsContentElementCore() => true;
    protected override string GetNameCore() => $"Gate {_identity.GateId.Value} – {KindName(_identity.Kind)}";
    protected override string GetHelpTextCore() => _owner.OnDispatcher(() =>
    {
        GateHandleVisualState state = PropertyState();
        return $"{state.Unit}; mínimo {state.Minimum:G}, máximo {state.Maximum:G}, paso {state.Step:G}";
    });
    protected override bool IsEnabledCore() => !_retired && PropertyState().IsEnabled;
    protected override bool IsKeyboardFocusableCore() => !_retired;
    protected override bool HasKeyboardFocusCore() => !_retired && PropertyState().IsFocused;
    protected override bool IsOffscreenCore() => _owner.OnDispatcher(() =>
    {
        if (_retired) return true;
        GateHandleVisualState handle = PropertyState();
        return handle.X < 0 || handle.Y < 0 || handle.X > _owner.ActualWidth || handle.Y > _owner.ActualHeight;
    });
    protected override Rect GetBoundingRectangleCore() => _owner.OnDispatcher(() =>
    {
        if (_retired) return Rect.Empty;
        GateHandleVisualState handle = PropertyState();
        Point topLeft = _owner.PointToScreen(new(handle.X - 6, handle.Y - 6));
        Point bottomRight = _owner.PointToScreen(new(handle.X + 6, handle.Y + 6));
        return new Rect(topLeft, bottomRight);
    });
    protected override Point GetClickablePointCore() => _owner.OnDispatcher(() =>
    {
        if (_retired) return new(double.NaN, double.NaN);
        GateHandleVisualState handle = PropertyState(); return _owner.PointToScreen(new(handle.X, handle.Y));
    });
    protected override void SetFocusCore() => Select();
    public override object? GetPattern(PatternInterface patternInterface) =>
        patternInterface is PatternInterface.RangeValue or PatternInterface.SelectionItem ? this : null;

    bool IRangeValueProvider.IsReadOnly => _retired || !PropertyState().IsEnabled;
    double IRangeValueProvider.LargeChange => PropertyState().Step;
    double IRangeValueProvider.Maximum => PropertyState().Maximum;
    double IRangeValueProvider.Minimum => PropertyState().Minimum;
    double IRangeValueProvider.SmallChange => PropertyState().Step;
    double IRangeValueProvider.Value => PropertyState().Value;
    void IRangeValueProvider.SetValue(double value) => _owner.OnDispatcher(() =>
    {
        GateHandleVisualState handle = Current();
        if (!handle.IsEnabled) throw new InvalidOperationException("The gate handle is read-only.");
        if (!double.IsFinite(value) || value < handle.Minimum || value > handle.Maximum)
            throw new ArgumentOutOfRangeException(nameof(value));
        _owner.RequestAutomationValue(_identity, value);
    });
    bool ISelectionItemProvider.IsSelected => !_retired && PropertyState().IsSelected;
    IRawElementProviderSimple ISelectionItemProvider.SelectionContainer => ProviderFromPeer(_parent);
    void ISelectionItemProvider.AddToSelection() => Select();
    void ISelectionItemProvider.RemoveFromSelection() => _ = Current();
    void ISelectionItemProvider.Select() => Select();
    private void Select() => _owner.OnDispatcher(() =>
    {
        _ = Current(); _owner.RequestAutomationSelect(_identity);
    });
    private static string KindName(GateHandleKind kind) => kind switch
    { GateHandleKind.Start => "Inicio", GateHandleKind.End => "Fin", GateHandleKind.Threshold => "Umbral", _ => "Handle" };
}
