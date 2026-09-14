using UTStudio.Contracts.Application;
using UTStudio.Domain.Acquisition;
using UTStudio.Visualization.Core;

namespace UTStudio.Presentation;

/// <summary>Atomic immutable state for bindings; visual points are shared, never copied or reduced here.</summary>
public sealed record AScanViewState(SessionSnapshot Session, AScanSnapshot? AScan,
    AScanDeliveryStatistics? VisualStatistics, UtSourceError? CommandError, UtSourceError? FeedError);
