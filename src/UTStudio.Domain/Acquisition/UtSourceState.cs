using System.Collections.ObjectModel;

namespace UTStudio.Domain.Acquisition;

/// <summary>An immutable, coherent state snapshot, not a transition engine.</summary>
/// <remarks>The source advances Version and preserves the primary error separately from cleanup errors.</remarks>
public sealed class UtSourceState
{
    public UtSourceState(
        ulong version,
        UtConnectionState connection,
        UtAcquisitionState acquisition,
        UtSourceError? primaryError = null,
        IEnumerable<UtSourceError>? cleanupErrors = null)
    {
        if (!Enum.IsDefined(connection))
        {
            throw new ArgumentOutOfRangeException(nameof(connection));
        }

        if (!Enum.IsDefined(acquisition))
        {
            throw new ArgumentOutOfRangeException(nameof(acquisition));
        }

        if (acquisition == UtAcquisitionState.Running && connection != UtConnectionState.Connected)
        {
            throw new ArgumentException("Running acquisition requires a connected source.", nameof(acquisition));
        }

        var errors = cleanupErrors?.ToArray() ?? [];
        if (errors.Any(error => error is null))
        {
            throw new ArgumentException("Cleanup errors cannot contain null.", nameof(cleanupErrors));
        }

        Version = version;
        Connection = connection;
        Acquisition = acquisition;
        PrimaryError = primaryError;
        CleanupErrors = Array.AsReadOnly(errors);
    }

    public ulong Version { get; }
    public UtConnectionState Connection { get; }
    public UtAcquisitionState Acquisition { get; }
    public UtSourceError? PrimaryError { get; }
    public ReadOnlyCollection<UtSourceError> CleanupErrors { get; }
}
