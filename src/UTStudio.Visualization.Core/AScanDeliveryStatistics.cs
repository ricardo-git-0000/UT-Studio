using UTStudio.Domain.Acquisition;

namespace UTStudio.Visualization.Core;

/// <summary>Lifetime counters, sampled on demand. They describe the visual branch only.</summary>
/// <remarks>
/// Received counts Accept calls; Published counts mailbox-to-store commits, not observer callbacks.
/// Replaced counts unpublished snapshots replaced in the mailbox. Dropped includes no interest,
/// invalidated/closed runs and failed projection. Observer coalescence is counted separately.
/// </remarks>
public sealed record AScanDeliveryStatistics(long Received, long Published, long Replaced, long Dropped,
    long ObserverCoalesced, long ObserverErrors, bool HasPending, UtSourceError? LastError);
