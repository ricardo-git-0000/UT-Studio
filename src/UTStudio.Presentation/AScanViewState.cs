using UTStudio.Contracts.Application;
using UTStudio.Domain.Acquisition;
using UTStudio.Visualization.Core;

namespace UTStudio.Presentation;

/// <summary>One coherent, portable capture used by the WPF fallback adapter.</summary>
public sealed record AScanVisualContentSnapshot(
    AScanSnapshot Signal, AScanCursorState? Cursors, ScanTimeViewportBinding ViewportBinding)
{
    public bool IsCoherent => ViewportBinding.RunId == Signal.Metadata.RunId &&
        ViewportBinding.SnapshotVersion == Signal.Version &&
        (Cursors is null || Cursors.RunId == Signal.Metadata.RunId && Cursors.SnapshotVersion == Signal.Version);
}

/// <summary>Atomic immutable state for bindings; visual points are shared, never copied or reduced here.</summary>
public sealed record AScanViewState(SessionSnapshot Session, AScanSnapshot? AScan,
    AScanDeliveryStatistics? VisualStatistics, UtSourceError? CommandError, UtSourceError? FeedError,
    AScanDeliveryStatus? VisualStatus = null, AScanCursorState? Cursors = null,
    AScanDisplayMode DisplayMode = AScanDisplayMode.NoData,
    AScanSnapshot? LatestCompatibleLive = null,
    TraceSelectionState? Selection = null,
    AScanCompositeVisualSnapshot? CompositeVisual = null,
    ScanTimeViewportBinding? ViewportBinding = null,
    AScanVisualContentSnapshot? VisualContent = null);
