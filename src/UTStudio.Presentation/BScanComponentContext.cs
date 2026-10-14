using UTStudio.Visualization.Core;
using UTStudio.Visualization.Core.Scan2D;

namespace UTStudio.Presentation;

public enum BScanComponentPhase { NoData, Live, Selected, Frozen, Error, Closed }

/// <summary>Borrowed neutral inputs used by one complete B-Scan presentation scope.</summary>
/// <remarks>The component owns its ViewModel/consumer, but never disposes source, viewport or selection scope.</remarks>
public sealed record BScanComponentContext(
    IBScanVisualSource Visuals,
    SharedScanTimeViewport TimeViewport,
    TraceSelectionScope SelectionScope,
    Scan2DInterpolationMode InitialResamplingMode = Scan2DInterpolationMode.None,
    IBScanResamplingPreferenceWriter? ResamplingPreferences = null);
