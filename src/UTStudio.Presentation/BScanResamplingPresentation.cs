using UTStudio.Visualization.Core.Scan2D;

namespace UTStudio.Presentation;

public enum BScanResamplingUsage { Standard, InteractiveRecommended, HighQualityReducedCadence }

public sealed record BScanResamplingOption(Scan2DInterpolationMode Mode, BScanResamplingUsage Usage);

public static class BScanResamplingOptions
{
    public static IReadOnlyList<BScanResamplingOption> All { get; } =
    [
        new(Scan2DInterpolationMode.None, BScanResamplingUsage.Standard),
        new(Scan2DInterpolationMode.NearestNeighbor, BScanResamplingUsage.Standard),
        new(Scan2DInterpolationMode.Bilinear, BScanResamplingUsage.InteractiveRecommended),
        new(Scan2DInterpolationMode.Bicubic, BScanResamplingUsage.InteractiveRecommended),
        new(Scan2DInterpolationMode.Lanczos, BScanResamplingUsage.HighQualityReducedCadence)
    ];
}

public interface IBScanResamplingPreferenceWriter
{
    ValueTask<string?> SaveAsync(Scan2DInterpolationMode mode, CancellationToken cancellationToken = default);
}
