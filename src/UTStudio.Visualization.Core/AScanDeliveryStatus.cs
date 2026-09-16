using UTStudio.Domain.Acquisition;

namespace UTStudio.Visualization.Core;

/// <summary>Publisher lifetime diagnosis, independent of A-Scan arrival or acquisition state.</summary>
public sealed record AScanDeliveryStatus(ulong Version, UtSourceError? TerminalError);
