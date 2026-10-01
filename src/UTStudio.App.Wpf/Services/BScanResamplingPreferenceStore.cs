using System.Diagnostics;
using System.IO;
using System.Text.Json;
using UTStudio.Presentation;
using UTStudio.Visualization.Core.Scan2D;

namespace UTStudio.App.Wpf.Services;

internal sealed class BScanResamplingPreferenceStore(string path) : IBScanResamplingPreferenceWriter
{
    internal const int CurrentSchemaVersion = 1;
    private readonly SemaphoreSlim _serial = new(1, 1);
    private Scan2DInterpolationMode _current = Scan2DInterpolationMode.None;
    private Scan2DInterpolationMode? _persisted;
    private bool _loaded;
    internal static string DefaultPath => Path.Combine(Environment.GetFolderPath(
        Environment.SpecialFolder.LocalApplicationData), "UTStudio", "settings.json");
    internal Scan2DInterpolationMode Current => _current;
    internal string? LastError { get; private set; }
    private static JsonSerializerOptions JsonOptions { get; } = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    internal async ValueTask<Scan2DInterpolationMode> LoadAsync(CancellationToken cancellationToken = default)
    {
        await _serial.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_loaded) return _current;
            _loaded = true;
            if (!File.Exists(path)) return _current;
            try
            {
                await using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                    4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
                SettingsDocument? document = await JsonSerializer.DeserializeAsync<SettingsDocument>(stream,
                    JsonOptions, cancellationToken).ConfigureAwait(false);
                if (document?.SchemaVersion != CurrentSchemaVersion || document.BScan is null ||
                    !Enum.TryParse(document.BScan.ResamplingMode, false, out Scan2DInterpolationMode mode) ||
                    !Enum.IsDefined(mode)) return Fail("Preferencia B-Scan incompatible; se usa None.");
                _current = mode; _persisted = mode;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
            { return Fail($"No se pudo leer la preferencia B-Scan; se usa None. {error.Message}"); }
            catch (Exception error) when (error is not OperationCanceledException)
            { return Fail($"No se pudo cargar la preferencia B-Scan; se usa None. {error.Message}"); }
            return _current;
        }
        finally { _serial.Release(); }
    }

    public async ValueTask<string?> SaveAsync(Scan2DInterpolationMode mode, CancellationToken cancellationToken = default)
    {
        if (!Enum.IsDefined(mode)) mode = Scan2DInterpolationMode.None;
        _current = mode; _loaded = true;
        await _serial.WaitAsync(cancellationToken).ConfigureAwait(false);
        string? temporary = null;
        try
        {
            mode = _current;
            if (mode == _persisted && File.Exists(path)) return null;
            string directory = Path.GetDirectoryName(path) ?? throw new InvalidOperationException("Ruta inválida.");
            Directory.CreateDirectory(directory);
            temporary = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
            await using (FileStream stream = new(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, new SettingsDocument(CurrentSchemaVersion,
                    new(mode.ToString())), JsonOptions, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(true);
            }
            File.Move(temporary, path, true); temporary = null;
            _persisted = mode; LastError = null;
            return null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        { LastError = $"No se pudo guardar la preferencia B-Scan. {error.Message}"; Trace.WriteLine(LastError); return LastError; }
        finally { if (temporary is not null) try { File.Delete(temporary); } catch { } _serial.Release(); }
    }

    private Scan2DInterpolationMode Fail(string error)
    { LastError = error; Trace.WriteLine(error); _current = Scan2DInterpolationMode.None; return _current; }
    private sealed record SettingsDocument(int SchemaVersion, BScanSettings? BScan);
    private sealed record BScanSettings(string ResamplingMode);
}
