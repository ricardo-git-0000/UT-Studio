using UTStudio.Domain.Acquisition;
using UTStudio.Visualization.Core;

namespace UTStudio.Presentation;

public enum BScanViewPhase { NoData, Ready, Closing, Closed }

public readonly record struct BScanPresentedRegionToken(Guid Value);

/// <summary>Immutable metadata for a region retained internally by the ViewModel.</summary>
public sealed record BScanPresentedRegion(
    BScanPresentedRegionToken Token,
    BScanVisualSnapshot Snapshot,
    int X,
    int Y,
    int Width,
    int Height,
    int Stride,
    BScanRasterFormat Format,
    long PresentationGeneration);

public sealed record BScanViewState(
    BScanViewPhase Phase,
    AcquisitionRunId? RunId,
    ulong? SnapshotVersion,
    ulong? StoreRevision,
    BScanVisualDiagnostics? Diagnostics,
    BScanAcquisitionRange? ResidentRange,
    ScanTimeViewport? TemporalDomain,
    BScanSpatialViewport? SpatialViewport,
    ScanTimeViewportBinding? TimeViewportBinding,
    BScanPresentedRegion? Region,
    BScanVisualRevision? DisplayedRevision,
    bool RequiresFullRefresh,
    BScanTraceIdentity? SelectedColumn,
    BScanSelectionState Selection,
    string? AcquisitionError,
    string? BScanIngressError,
    string? BScanVisualError,
    string? SelectionError,
    string? UiInteractionError,
    bool IsSpatialAutoFit = true)
{
    public static BScanViewState Empty { get; } = new(BScanViewPhase.NoData, null, null, null, null,
        null, null, null, null, null, null, true, null, BScanSelectionState.Empty,
        null, null, null, null, null);
}
