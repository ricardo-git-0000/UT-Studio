using UTStudio.Contracts.Acquisition;
using UTStudio.Domain.Acquisition;
using UTStudio.Presentation;
using UTStudio.Visualization.Core;
using UTStudio.Application;
using UTStudio.Processing.Core;
using UTStudio.Contracts.Inspection;

namespace UTStudio.App.Wpf.Composition;

/// <summary>Internal deterministic composition seam for productive lifecycle tests.</summary>
internal static class DesktopRuntimeFactory
{
    internal static Task<DesktopRuntime> CreateAsync(IUiDispatcher dispatcher, IUtFrameSource source,
        ConventionalAcquisitionConfiguration configuration, BScanOptions bScanOptions, TimeProvider clock,
        IByteOwnerPool? rasterPool = null,
        Action<string>? lifecycleObserver = null,
        DesktopGateTestHooks? gateHooks = null) =>
        DesktopRuntime.CreateAsync(dispatcher, [], clock, null, source, configuration, bScanOptions, rasterPool,
            lifecycleObserver, gateHooks);
}

internal sealed record DesktopGateTestHooks(
    GateHeadlessInspectionOptions? Options = null,
    Func<IPreparedGatePlanEpochPort, IPreparedGatePlanEpochPort>? EpochDecorator = null,
    Func<IGateReplacementStore, IGateReplacementStore>? ReplacementDecorator = null,
    Func<IOwnedGateReprocessingScheduler, IOwnedGateReprocessingScheduler>? SchedulerDecorator = null,
    Func<IInspectionRunWriter, IInspectionRunWriter>? InspectionWriterDecorator = null,
    Action<ProductiveGateInspectionDiagnostics>? BeforeAuthorityClose = null);
