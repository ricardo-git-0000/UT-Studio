using System.Globalization;
using System.IO;
using System.Linq;
using System.Xml;
using System.Xml.Linq;

namespace UTStudio.Visualization.Wpf.Rendering;

public sealed class BScanColorPalette
{
    private readonly uint[] _bgra32;

    internal BScanColorPalette(uint[] bgra32, string name)
    {
        if (bgra32.Length != 256) { throw new ArgumentException("A palette must contain 256 colors.", nameof(bgra32)); }
        _bgra32 = bgra32;
        Name = name;
    }

    public string Name { get; }
    public ReadOnlyMemory<uint> Bgra32 => _bgra32;
}

public static class BScanPaletteFile
{
    public const long MaximumFileBytes = 1_048_576;

    public static BScanColorPalette Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var info = new FileInfo(path);
        if (!info.Exists) { throw new FileNotFoundException("Palette file not found.", path); }
        if (info.Length > MaximumFileBytes) { throw new InvalidDataException("Palette file exceeds 1 MiB."); }
        using FileStream stream = info.OpenRead();
        return Load(stream, info.Name);
    }

    public static BScanColorPalette Load(Stream stream, string name = "Imported palette")
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (stream.CanSeek && stream.Length > MaximumFileBytes)
        { throw new InvalidDataException("Palette file exceeds 1 MiB."); }
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = MaximumFileBytes,
            IgnoreComments = true,
            IgnoreWhitespace = true
        };
        using XmlReader reader = XmlReader.Create(stream, settings);
        XDocument document = XDocument.Load(reader, LoadOptions.None);
        XElement root = document.Root ?? throw new InvalidDataException("Palette XML has no root element.");
        if (root.Name.LocalName != "palette") { throw new InvalidDataException("Expected a palette root element."); }
        XElement colorsElement = root.Elements().SingleOrDefault(element => element.Name.LocalName == "allColors")
            ?? throw new InvalidDataException("Palette XML has no allColors element.");
        uint[] source = colorsElement.Elements().Where(element => element.Name.LocalName == "color")
            .Select(ReadColor).ToArray();
        if (source.Length < 2 || source.Length > 4096)
        { throw new InvalidDataException("allColors must contain between 2 and 4096 colors."); }
        uint noData = ReadOptionalEndpoint(root, "allMinColors") ?? BScanPalette.Bgra32[0];
        uint? maximum = ReadOptionalEndpoint(root, "allMaxColors");
        var values = new uint[256];
        values[0] = noData;
        for (int index = 1; index < values.Length; index++)
        {
            double position = (index - 1d) * (source.Length - 1) / 254d;
            int lower = (int)Math.Floor(position);
            int upper = Math.Min(lower + 1, source.Length - 1);
            values[index] = Interpolate(source[lower], source[upper], position - lower);
        }
        if (maximum is { } max) { values[255] = max; }
        return new(values, string.IsNullOrWhiteSpace(name) ? "Imported palette" : name);
    }

    private static uint? ReadOptionalEndpoint(XElement root, string elementName)
    {
        XElement? container = root.Elements().SingleOrDefault(element => element.Name.LocalName == elementName);
        XElement? color = container?.Elements().SingleOrDefault(element => element.Name.LocalName == "color");
        return color is null ? null : ReadColor(color);
    }

    private static uint ReadColor(XElement element) => Pack(ReadChannel(element, "red"),
        ReadChannel(element, "green"), ReadChannel(element, "blue"));

    private static byte ReadChannel(XElement element, string name)
    {
        string text = element.Attribute(name)?.Value
            ?? throw new InvalidDataException($"A color is missing its {name} channel.");
        if (!byte.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out byte value))
        { throw new InvalidDataException($"Color channel {name} must be an integer from 0 to 255."); }
        return value;
    }

    private static uint Interpolate(uint first, uint second, double amount) => Pack(
        Mix((byte)(first >> 16), (byte)(second >> 16), amount),
        Mix((byte)(first >> 8), (byte)(second >> 8), amount),
        Mix((byte)first, (byte)second, amount));

    private static byte Mix(byte first, byte second, double amount) =>
        checked((byte)Math.Round(first + (second - first) * amount, MidpointRounding.AwayFromZero));

    private static uint Pack(byte red, byte green, byte blue) =>
        0xFF000000u | ((uint)red << 16) | ((uint)green << 8) | blue;
}
