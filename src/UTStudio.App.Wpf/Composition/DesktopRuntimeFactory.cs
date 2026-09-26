using UTStudio.Contracts.Acquisition;
using UTStudio.Domain.Acquisition;
using UTStudio.Presentation;
using UTStudio.Visualization.Core;

namespace UTStudio.App.Wpf.Composition;

/// <summary>Internal deterministic composition seam for productive lifecycle tests.</summary>
internal static class DesktopRuntimeFactory
{
    internal static Task<DesktopRuntime> CreateAsync(IUiDispatcher dispatcher, IUtFrameSource source,
        ConventionalAcquisitionConfiguration configuration, BScanOptions bScanOptions, TimeProvider clock,
        IShortArrayPool? tracePool = null, IByteOwnerPool? rasterPool = null,
        Action<string>? lifecycleObserver = null) =>
        DesktopRuntime.CreateAsync(dispatcher, [], clock, null, source, configuration, bScanOptions, tracePool, rasterPool,
            lifecycleObserver);
}
