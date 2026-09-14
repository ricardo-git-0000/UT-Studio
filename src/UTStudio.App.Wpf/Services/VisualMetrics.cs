using UTStudio.Visualization.Core;

namespace UTStudio.App.Wpf.Services;

/// <summary>Borrowed read-only statistics query for the WPF status strip; no frame access.</summary>
public sealed class VisualMetrics(Func<AScanDeliveryStatistics> read)
{
    public AScanDeliveryStatistics Read() => read();
}
