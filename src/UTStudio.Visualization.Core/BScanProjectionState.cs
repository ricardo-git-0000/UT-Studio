using UTStudio.Contracts.Inspection;
using UTStudio.Domain.Acquisition;
namespace UTStudio.Visualization.Core;

internal sealed record BScanStoreVisualState(
    ulong StoreRevision,
    ulong WrapGeneration,
    BScanAcquisitionRange? ResidentRange,
    InspectionRunState RunState,
    ulong? FinalRevision,
    bool AreWatermarksFinal,
    int CommittedTraceCount,
    int CommittedGapCount);

internal sealed record BScanPageTraversalResult(
    IReadOnlySet<InspectionReadStatus> Statuses,
    InspectionReadStatus? TerminalStatus,
    bool Stale,
    int PageCount,
    int AvailableCount);

