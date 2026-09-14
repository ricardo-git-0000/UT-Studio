namespace UTStudio.Contracts.Application;

/// <summary>The session's lifecycle, distinct from the source's acquisition state.</summary>
public enum SessionPhase
{
    Idle,
    Connecting,
    Configuring,
    Starting,
    Running,
    Stopping,
    AwaitingFramesReleased,
    Disconnecting,
    Faulted,
    Disposed
}
