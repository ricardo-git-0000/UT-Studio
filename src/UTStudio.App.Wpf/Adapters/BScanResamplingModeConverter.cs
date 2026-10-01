using System.Globalization;
using System.Windows.Data;
using UTStudio.Visualization.Core.Scan2D;

namespace UTStudio.App.Wpf.Adapters;

public sealed class BScanResamplingModeConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        Scan2DInterpolationMode.None => "Sin interpolación",
        Scan2DInterpolationMode.NearestNeighbor => "Vecino más próximo",
        Scan2DInterpolationMode.Bilinear => "Bilineal — recomendado",
        Scan2DInterpolationMode.Bicubic => "Bicúbica — recomendado",
        Scan2DInterpolationMode.Lanczos => "Lanczos-3 — alta calidad",
        _ => "Ninguno"
    };
    public object ConvertBack(object value, Type targetType, object? parameter, CultureInfo culture) =>
        Binding.DoNothing;
}
