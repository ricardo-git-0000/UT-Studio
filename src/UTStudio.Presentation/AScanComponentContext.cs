using UTStudio.Contracts.Application;
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
    BScanSelectionCoordinator? SelectionCoordinator = null);
