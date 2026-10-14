using UTStudio.Domain.Acquisition;
using UTStudio.Contracts.Inspection;
using UTStudio.Visualization.Core;
using UTStudio.Visualization.Core.Scan2D;

namespace UTStudio.Presentation;

public enum BScanViewPhase { NoData, Ready, Closing, Closed }

public readonly record struct BScanPresentedRegionToken(Guid Value);
public delegate void BScanIntensityVisitor(ReadOnlySpan<byte> pixels, ReadOnlySpan<byte> validityMask, int stride);

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
    TraceIdentity? SelectedColumn,
    TraceSelectionState Selection,
    string? AcquisitionError,
    string? BScanIngressError,
    string? BScanVisualError,
    string? SelectionError,
    string? UiInteractionError,
    bool IsSpatialAutoFit = true,
    Scan2DInterpolationMode RequestedResamplingMode = Scan2DInterpolationMode.None,
    Scan2DInterpolationMode? PresentedResamplingMode = null,
    BScanRenderSignature? ConfirmedRenderSignature = null,
    bool IsRenderPending = false,
    string? PersistenceError = null,
    string? ConfigurationError = null)
{
    public static BScanViewState Empty { get; } = new(BScanViewPhase.NoData, null, null, null, null,
        null, null, null, null, null, null, true, null, TraceSelectionState.Empty,
        null, null, null, null, null);
}
