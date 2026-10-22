using UTStudio.Contracts.Application;
using UTStudio.Contracts.Processing;
using UTStudio.Visualization.Core;

namespace UTStudio.Presentation;

public enum AScanComponentPhase { Empty, Live, Frozen, Error }

/// <summary>Borrowed neutral inputs used by one complete A-Scan presentation scope.</summary>
/// <remarks>The component never disposes these feeds, viewport or selection coordinator.</remarks>
public sealed record AScanComponentContext(
    IObservable<SessionSnapshot> SessionStates,
    SessionSnapshot InitialSession,
    IObservable<AScanSnapshot> Visuals,
    IObservable<AScanDeliveryStatus>? VisualStatus = null,
    Func<AScanDeliveryStatistics>? ReadStatistics = null,
    SharedScanTimeViewport? TimeViewport = null,
    TraceSelectionScope? SelectionScope = null,
    GateEditingComponentContext? GateEditing = null);

/// <summary>Borrowed neutral ports used to create one independent gate editor per A-Scan view.</summary>
public sealed record GateEditingComponentContext(
    GatePlanAuthorityKey AuthorityKey,
    IGatePlanAuthorityReader Authority,
    IGatePlanPreviewCompiler PreviewCompiler,
    IGateGeometryEvaluator GeometryEvaluator,
    IGatePlanApplyPort Apply)
{
    public bool IsValid => AuthorityKey.IsValid && Authority is not null && PreviewCompiler is not null &&
        GeometryEvaluator is not null && Apply is not null;
}
