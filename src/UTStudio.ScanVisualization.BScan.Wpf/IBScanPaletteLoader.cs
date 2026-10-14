using System.Windows;
using UTStudio.Visualization.Wpf.Rendering;

namespace UTStudio.ScanVisualization.BScan.Wpf;

public sealed record BScanPaletteSelection(string Name, BScanColorPalette Palette);

/// <summary>Optional host policy for choosing a WPF B-Scan palette.</summary>
public interface IBScanPaletteLoader
{
    BScanPaletteSelection? SelectPalette(Window? owner);
}
