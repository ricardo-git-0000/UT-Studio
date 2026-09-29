using System.Windows.Media;
using UTStudio.Visualization.Core;

namespace UTStudio.Visualization.Wpf.Rendering;

public static class BScanPalette
{
    private static readonly uint[] Values = Build();

    public static Color GetColor(byte intensity)
    {
        uint value = Values[intensity];
        return Color.FromArgb((byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value);
    }

    internal static uint GetBgra(byte intensity) => Values[intensity];
    public static ReadOnlySpan<uint> Bgra32 => Values;

    public static void CopyToBgra32(ReadOnlySpan<byte> source, int sourceStride,
        Span<byte> destination, int destinationStride, BScanRasterRectangle affected)
        => CopyToBgra32(source, sourceStride, destination, destinationStride, affected, Values);

    public static void CopyToBgra32(ReadOnlySpan<byte> source, int sourceStride,
        Span<byte> destination, int destinationStride, BScanRasterRectangle affected,
        ReadOnlySpan<uint> palette)
    {
        if (palette.Length != 256) { throw new ArgumentException("A B-Scan palette must contain 256 colors.", nameof(palette)); }
        if (sourceStride < affected.Width) { throw new ArgumentException("Source stride is too small.", nameof(sourceStride)); }
        if (source.Length < checked((affected.Height - 1) * sourceStride + affected.Width))
        { throw new ArgumentException("Source is too small.", nameof(source)); }
        if (destinationStride < checked((affected.X + affected.Width) * 4))
        { throw new ArgumentException("Destination stride is too small.", nameof(destinationStride)); }
        if (destination.Length < checked((affected.Y + affected.Height - 1) * destinationStride +
            (affected.X + affected.Width) * 4))
        { throw new ArgumentException("Destination is too small.", nameof(destination)); }
        for (int row = 0; row < affected.Height; row++)
        {
            ReadOnlySpan<byte> input = source.Slice(checked(row * sourceStride), affected.Width);
            Span<byte> output = destination.Slice(checked((affected.Y + row) * destinationStride +
                affected.X * 4), checked(affected.Width * 4));
            for (int column = 0; column < affected.Width; column++)
            {
                uint color = palette[input[column]];
                int offset = column * 4;
                output[offset] = (byte)color;
                output[offset + 1] = (byte)(color >> 8);
                output[offset + 2] = (byte)(color >> 16);
                output[offset + 3] = (byte)(color >> 24);
            }
        }
    }

    private static uint[] Build()
    {
        var values = new uint[256];
        values[0] = Pack(0x10, 0x14, 0x1C);
        for (int value = 1; value < 128; value++)
        {
            byte strength = (byte)(255 - (value - 1) * 127 / 126);
            values[value] = Pack(24, (byte)(48 + (255 - strength) / 3), strength);
        }
        values[128] = Pack(0x72, 0x78, 0x82);
        for (int value = 129; value < 256; value++)
        {
            byte strength = (byte)(128 + (value - 129) * 127 / 126);
            values[value] = Pack(strength, (byte)(48 + (strength - 128) / 2), 24);
        }
        return values;
    }

    private static uint Pack(byte red, byte green, byte blue) =>
        0xFF000000u | ((uint)red << 16) | ((uint)green << 8) | blue;
}
