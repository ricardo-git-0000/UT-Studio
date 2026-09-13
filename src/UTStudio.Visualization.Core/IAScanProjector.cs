using UTStudio.Domain.Acquisition;

namespace UTStudio.Visualization.Core;

/// <summary>Synchronous projection of borrowed samples; implementations must not retain aliases.</summary>
public interface IAScanProjector
{
    AScanSnapshot Project(ConventionalUtFrameMetadata metadata, ulong sequence, TimeSpan elapsedSinceRunStart,
        ReadOnlySpan<short> samples, ulong version);
}
