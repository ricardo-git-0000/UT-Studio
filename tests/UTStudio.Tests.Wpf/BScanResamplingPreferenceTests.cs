using System.IO;
using System.Text.Json;
using UTStudio.App.Wpf;
using UTStudio.App.Wpf.Services;
using UTStudio.ScanVisualization.BScan.Wpf;
using UTStudio.Visualization.Core.Scan2D;

namespace UTStudio.Tests.Wpf;

[TestClass]
public sealed class BScanResamplingPreferenceTests
{
    [TestMethod]
    public async Task Missing_corrupt_unknown_and_future_preferences_fall_back_to_none()
    {
        string directory = NewDirectory();
        try
        {
            string path = Path.Combine(directory, "settings.json");
            var missing = new BScanResamplingPreferenceStore(path);
            Assert.AreEqual(Scan2DInterpolationMode.None, await missing.LoadAsync());

            await File.WriteAllTextAsync(path, "{broken");
            var corrupt = new BScanResamplingPreferenceStore(path);
            Assert.AreEqual(Scan2DInterpolationMode.None, await corrupt.LoadAsync());
            Assert.IsNotNull(corrupt.LastError);

            await File.WriteAllTextAsync(path, "{\"schemaVersion\":1,\"bScan\":{\"resamplingMode\":\"Kriging\"}}");
            var unknown = new BScanResamplingPreferenceStore(path);
            Assert.AreEqual(Scan2DInterpolationMode.None, await unknown.LoadAsync());
            Assert.IsNotNull(unknown.LastError);
            Assert.IsNull(await unknown.SaveAsync(unknown.Current));
            using (JsonDocument canonical = JsonDocument.Parse(await File.ReadAllTextAsync(path)))
            {
                Assert.AreEqual("None", canonical.RootElement.GetProperty("bScan")
                    .GetProperty("resamplingMode").GetString());
            }

            await File.WriteAllTextAsync(path, "{\"schemaVersion\":2,\"bScan\":{\"resamplingMode\":\"Bicubic\"}}");
            Assert.AreEqual(Scan2DInterpolationMode.None,
                await new BScanResamplingPreferenceStore(path).LoadAsync());
        }
        finally { Directory.Delete(directory, true); }
    }

    [TestMethod]
    public async Task All_five_modes_round_trip_with_versioned_camel_case_schema_and_no_temp_files()
    {
        string directory = NewDirectory();
        try
        {
            string path = Path.Combine(directory, "settings.json");
            foreach (Scan2DInterpolationMode mode in Enum.GetValues<Scan2DInterpolationMode>())
            {
                var writer = new BScanResamplingPreferenceStore(path);
                Assert.IsNull(await writer.SaveAsync(mode));
                var reader = new BScanResamplingPreferenceStore(path);
                Assert.AreEqual(mode, await reader.LoadAsync());
            }
            using JsonDocument document = JsonDocument.Parse(await File.ReadAllTextAsync(path));
            Assert.AreEqual(1, document.RootElement.GetProperty("schemaVersion").GetInt32());
            Assert.AreEqual("Lanczos", document.RootElement.GetProperty("bScan")
                .GetProperty("resamplingMode").GetString());
            Assert.HasCount(1, Directory.GetFiles(directory));
        }
        finally { Directory.Delete(directory, true); }
    }

    [TestMethod]
    public async Task Concurrent_writes_are_serialized_and_last_call_wins()
    {
        string directory = NewDirectory();
        try
        {
            string path = Path.Combine(directory, "settings.json");
            var store = new BScanResamplingPreferenceStore(path);
            Task<string?>[] writes =
            [store.SaveAsync(Scan2DInterpolationMode.NearestNeighbor).AsTask(),
             store.SaveAsync(Scan2DInterpolationMode.Bilinear).AsTask(),
             store.SaveAsync(Scan2DInterpolationMode.Bicubic).AsTask()];
            await Task.WhenAll(writes).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.AreEqual(Scan2DInterpolationMode.Bicubic,
                await new BScanResamplingPreferenceStore(path).LoadAsync());
            Assert.HasCount(1, Directory.GetFiles(directory));
        }
        finally { Directory.Delete(directory, true); }
    }

    [TestMethod]
    public void Display_options_are_exactly_the_five_supported_modes()
    {
        CollectionAssert.AreEqual(Enum.GetValues<Scan2DInterpolationMode>(),
            BScanView.DisplayOptions.Select(option => option.Mode).ToArray());
        StringAssert.Contains(BScanView.DisplayOptions[^1].DisplayName, "alta calidad");
    }

    private static string NewDirectory()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"utstudio-resampling-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        return directory;
    }
}
