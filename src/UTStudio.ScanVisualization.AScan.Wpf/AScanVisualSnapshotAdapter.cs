using UTStudio.Presentation;
using UTStudio.Visualization.Core;

namespace UTStudio.ScanVisualization.AScan.Wpf;

/// <summary>Temporary no-gates adapter. It never creates authority or detector geometry.</summary>
internal sealed class AScanVisualSnapshotAdapter
{
    private ulong _generation, _transformVersion;

    internal AScanCompositeVisualSnapshot? Adapt(AScanViewState? state, double widthDip, double heightDip,
        double dpiScaleX, double dpiScaleY)
    {
        if (state?.VisualContent is not { IsCoherent: true } content || widthDip <= 84 || heightDip <= 54 ||
            !double.IsFinite(widthDip) || !double.IsFinite(heightDip) || dpiScaleX <= 0 || dpiScaleY <= 0) return null;
        if (state.CompositeVisual is { } composed && Compatible(composed.Transform, widthDip, heightDip, dpiScaleX, dpiScaleY))
            return composed;
        _generation = checked(_generation + 1); _transformVersion = checked(_transformVersion + 1);
        var overlay = new GateOverlayVisualSnapshot(GateOverlayGeometryStatus.NotRequested, null, null,
            default, [], [], null, GateHandleKind.Unknown, _generation, 0, null);
        var transform = new GateVisualTransform(_transformVersion, content.ViewportBinding.Viewport,
            GateAmplitudePresentationKind.RawRf, 64, widthDip - 84, heightDip - 38, -(heightDip - 54),
            widthDip, heightDip, dpiScaleX, dpiScaleY);
        return new(content.Signal, content.Cursors, overlay, transform);
    }

    private static bool Compatible(GateVisualTransform transform, double width, double height, double dpiX, double dpiY) =>
        Equal(transform.WidthDip, width) && Equal(transform.HeightDip, height) &&
        Equal(transform.DpiScaleX, dpiX) && Equal(transform.DpiScaleY, dpiY);
    private static bool Equal(double left, double right) => Math.Abs(left - right) <= 1e-6 * Math.Max(1, Math.Max(Math.Abs(left), Math.Abs(right)));
}
