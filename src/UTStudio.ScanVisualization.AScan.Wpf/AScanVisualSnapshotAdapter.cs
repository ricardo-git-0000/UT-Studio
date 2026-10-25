using UTStudio.Presentation;
using UTStudio.Visualization.Core;

namespace UTStudio.ScanVisualization.AScan.Wpf;

/// <summary>Admits the coherent productive composite, or a visual-only no-gates fallback.</summary>
internal sealed class AScanVisualSnapshotAdapter
{
    private ulong _generation, _transformVersion;
    private GateVisualTransform? _lastTransform;

    internal AScanCompositeVisualSnapshot? Adapt(AScanViewState? state, double widthDip, double heightDip,
        double dpiScaleX, double dpiScaleY)
    {
        if (state?.VisualContent is not { IsCoherent: true } content || widthDip <= 84 || heightDip <= 54 ||
            !double.IsFinite(widthDip) || !double.IsFinite(heightDip) || dpiScaleX <= 0 || dpiScaleY <= 0) return null;
        if (state.CompositeVisual is { } composed && Compatible(composed.Transform, widthDip, heightDip, dpiScaleX, dpiScaleY))
        {
            AScanCursorState? cursors = content.Cursors is { } requested &&
                requested.RunId == composed.Signal.Metadata.RunId
                ? AScanCursorMeasurements.Reconcile(requested, composed.Signal)
                : composed.Cursors;
            return Equals(cursors, composed.Cursors) ? composed :
                new(composed.Signal, cursors, composed.Gates, composed.Transform);
        }
        GateVisualTransform transform = CreateTransform(content, widthDip, heightDip, dpiScaleX, dpiScaleY);
        if (state.GateEditor is { } gateEditor)
            return new(content.Signal, content.Cursors, gateEditor.Overlay, transform);
        _generation = checked(_generation + 1);
        var overlay = new GateOverlayVisualSnapshot(GateOverlayGeometryStatus.NotRequested, null, null,
            default, [], [], null, GateHandleKind.Unknown, _generation, 0, null);
        return new(content.Signal, content.Cursors, overlay, transform);
    }

    internal GateVisualTransform CreateTransform(AScanVisualContentSnapshot content, double widthDip,
        double heightDip, double dpiScaleX, double dpiScaleY)
    {
        if (_lastTransform is { } previous && previous.TimeViewport == content.ViewportBinding.Viewport &&
            Compatible(previous, widthDip, heightDip, dpiScaleX, dpiScaleY)) return previous;
        _transformVersion = checked(_transformVersion + 1);
        return (_lastTransform = new(_transformVersion, content.ViewportBinding.Viewport,
            GateAmplitudePresentationKind.RawRf, 64, widthDip - 84, heightDip - 38, -(heightDip - 54),
            widthDip, heightDip, dpiScaleX, dpiScaleY)).Value;
    }

    private static bool Compatible(GateVisualTransform transform, double width, double height, double dpiX, double dpiY) =>
        Equal(transform.WidthDip, width) && Equal(transform.HeightDip, height) &&
        Equal(transform.DpiScaleX, dpiX) && Equal(transform.DpiScaleY, dpiY);
    private static bool Equal(double left, double right) => Math.Abs(left - right) <= 1e-6 * Math.Max(1, Math.Max(Math.Abs(left), Math.Abs(right)));
}
