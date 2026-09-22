using UTStudio.Domain.Acquisition;

namespace UTStudio.Application;

public enum VisualBranchOperation
{
    Open,
    Accept,
    Close
}

/// <summary>One bounded diagnostic entry for an isolated visual branch failure.</summary>
public sealed record VisualBranchFailure(
    string BranchId,
    VisualBranchOperation Operation,
    UtSourceError Error);

/// <summary>Bounded lifetime diagnostic for one visual fan-out branch.</summary>
public sealed record VisualBranchDiagnostics(
    string BranchId,
    bool IsOpen,
    bool IsDisabled,
    bool IsClosed,
    ulong FailureCount,
    VisualBranchFailure? FirstFailure,
    VisualBranchFailure? LatestFailure);
