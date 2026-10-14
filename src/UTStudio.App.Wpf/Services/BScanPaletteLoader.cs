using System.IO;
using System.Windows;
using System.Xml;
using Microsoft.Win32;
using UTStudio.ScanVisualization.BScan.Wpf;
using UTStudio.Visualization.Wpf.Rendering;

namespace UTStudio.App.Wpf.Services;

internal sealed class BScanPaletteLoader : IBScanPaletteLoader
{
    public BScanPaletteSelection? SelectPalette(Window? owner)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Cargar paleta B-Scan",
            Filter = "Paleta XML (*.xml)|*.xml|Todos los archivos (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false
        };
        if (dialog.ShowDialog(owner) != true) { return null; }
        try
        {
            BScanColorPalette palette = BScanPaletteFile.Load(dialog.FileName);
            return new(palette.Name, palette);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or XmlException or InvalidDataException)
        { throw new InvalidOperationException($"No se pudo cargar la paleta: {error.Message}", error); }
    }
}
