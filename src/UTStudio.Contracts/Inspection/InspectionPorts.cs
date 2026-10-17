using UTStudio.Domain.Acquisition;

namespace UTStudio.Contracts.Inspection;

public interface IInspectionDataWriter
{
    ValueTask<InspectionAppendResult> AppendAsync(InspectionTraceInput trace, CancellationToken cancellationToken = default);
}

public interface IInspectionRunWriter : IAsyncDisposable
{
    AcquisitionRunId RunId { get; }
    InspectionRunState State { get; }
    ValueTask<InspectionAppendResult> CommitAsync(InspectionCommitInput commit,
        CancellationToken cancellationToken = default);
    ValueTask<InspectionAppendResult> CommitGapAsync(GapRecord gap,
        CancellationToken cancellationToken = default);
    ValueTask<InspectionRunSealResult> SealRunAsync(CancellationToken cancellationToken = default);
}

public interface IInspectionTraceLease : IDisposable
{
    TraceDescriptor Descriptor { get; }
    IReadOnlyUtSamples Samples { get; }
}

public interface IReadOnlyUtSamples
{
    int Count { get; }
    short this[int index] { get; }
    void CopyTo(Span<short> destination);
}

public readonly struct InspectionTraceReadResult
{
    private InspectionTraceReadResult(InspectionReadStatus status, InspectionRevision revision,
        IInspectionTraceLease? lease, string? errorCode)
    {
        Status = status;
        Revision = revision;
        Lease = lease;
        ErrorCode = errorCode;
    }

    public InspectionReadStatus Status { get; }
    public InspectionRevision Revision { get; }
    public IInspectionTraceLease? Lease { get; }
    public string? ErrorCode { get; }

    public static InspectionTraceReadResult Available(InspectionRevision revision,
        IInspectionTraceLease lease)
    {
        ArgumentNullException.ThrowIfNull(lease);
        if (revision == InspectionRevision.Initial)
        { throw new ArgumentException("An available trace requires a committed revision.", nameof(revision)); }
        if (lease.Descriptor is null || !lease.Descriptor.Identity.IsValid)
        { throw new ArgumentException("The lease must expose a valid trace descriptor.", nameof(lease)); }
        return new(InspectionReadStatus.Available, revision, lease, null);
    }

    public static InspectionTraceReadResult Unavailable(InspectionReadStatus status,
        InspectionRevision revision, string? errorCode = null)
    {
        ValidateUnavailableStatus(status);
        if (status == InspectionReadStatus.Faulted && string.IsNullOrWhiteSpace(errorCode))
        { throw new ArgumentException("A faulted read requires an error code.", nameof(errorCode)); }
        if (errorCode is not null && string.IsNullOrWhiteSpace(errorCode))
        { throw new ArgumentException("An error code cannot be blank.", nameof(errorCode)); }
        return new(status, revision, null, errorCode);
    }

    internal static void ValidateUnavailableStatus(InspectionReadStatus status)
    {
        if (!Enum.IsDefined(status) || status is InspectionReadStatus.Unknown or InspectionReadStatus.Available)
        { throw new ArgumentOutOfRangeException(nameof(status)); }
    }
}

public readonly struct InspectionRangeItem
{
    private InspectionRangeItem(TraceIdentity identity, InspectionReadStatus status,
        IInspectionTraceLease? lease)
    {
        Identity = identity;
        Status = status;
        Lease = lease;
    }

    public TraceIdentity Identity { get; }
    public InspectionReadStatus Status { get; }
    public IInspectionTraceLease? Lease { get; }

    public static InspectionRangeItem Available(IInspectionTraceLease lease)
    {
        ArgumentNullException.ThrowIfNull(lease);
        if (lease.Descriptor is null || !lease.Descriptor.Identity.IsValid)
        { throw new ArgumentException("The lease must expose a valid trace descriptor.", nameof(lease)); }
        return new(lease.Descriptor.Identity, InspectionReadStatus.Available, lease);
    }

    public static InspectionRangeItem Unavailable(TraceIdentity identity, InspectionReadStatus status)
    {
        if (!identity.IsValid) { throw new ArgumentException("A valid identity is required.", nameof(identity)); }
        if (!Enum.IsDefined(status) || status is not (InspectionReadStatus.NotAvailable or
            InspectionReadStatus.Gap or InspectionReadStatus.OutOfRange))
        { throw new ArgumentOutOfRangeException(nameof(status)); }
        return new(identity, status, null);
    }
}

public sealed class InspectionRangeReadResult : IDisposable
{
    private Batch? _batch;

    private InspectionRangeReadResult(InspectionReadStatus status, InspectionRevision revision,
        IReadOnlyList<InspectionRangeItem> items, string? errorCode = null)
    {
        ArgumentNullException.ThrowIfNull(items);
        Status = status;
        Revision = revision;
        IReadOnlyList<InspectionRangeItem> snapshot = Array.AsReadOnly(items.ToArray());
        ErrorCode = errorCode;
        _batch = new(snapshot, snapshot.Where(static item =>
            item.Status == InspectionReadStatus.Available).Select(static item => item.Lease!).ToArray());
    }

    public InspectionReadStatus Status { get; }
    public InspectionRevision Revision { get; }
    public IReadOnlyList<InspectionRangeItem> Items =>
        Volatile.Read(ref _batch)?.Items ?? Array.Empty<InspectionRangeItem>();
    public string? ErrorCode { get; }

    public static InspectionRangeReadResult Page(InspectionRevision revision,
        IReadOnlyList<InspectionRangeItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        if (items.Any(static item => item.Status == InspectionReadStatus.Unknown ||
            !item.Identity.IsValid || (item.Status == InspectionReadStatus.Available) != (item.Lease is not null)))
        { throw new ArgumentException("Every range item must be valid and internally consistent.", nameof(items)); }
        bool hasAvailable = items.Any(static item => item.Status == InspectionReadStatus.Available);
        if (hasAvailable && revision == InspectionRevision.Initial)
        { throw new ArgumentException("An available range requires a committed revision.", nameof(revision)); }
        InspectionReadStatus status = hasAvailable
            ? InspectionReadStatus.Available : InspectionReadStatus.NotAvailable;
        return new(status, revision, items);
    }

    public static InspectionRangeReadResult Terminal(InspectionReadStatus status,
        InspectionRevision revision, string? errorCode = null)
    {
        if (!Enum.IsDefined(status) || status is not (InspectionReadStatus.Cancelled or
            InspectionReadStatus.Closed or InspectionReadStatus.Faulted or InspectionReadStatus.OutOfRange))
        { throw new ArgumentOutOfRangeException(nameof(status)); }
        if (status == InspectionReadStatus.Faulted)
        {
            if (string.IsNullOrWhiteSpace(errorCode))
            { throw new ArgumentException("A faulted range requires an error code.", nameof(errorCode)); }
        }
        else if (errorCode is not null)
        { throw new ArgumentException("Only a faulted range may carry an error code.", nameof(errorCode)); }
        return new(status, revision, [], errorCode);
    }

    public void Dispose()
    {
        Batch? batch = Interlocked.Exchange(ref _batch, null);
        if (batch is null) { return; }
        IInspectionTraceLease?[] leases = batch.OwnedLeases;
        List<Exception>? failures = null;
        for (int index = leases.Length - 1; index >= 0; index--)
        {
            IInspectionTraceLease? lease = Interlocked.Exchange(ref leases[index], null);
            if (lease is null) { continue; }
            try { lease.Dispose(); }
            catch (Exception error)
            {
                failures ??= [];
                AddFlattened(failures, error);
            }
        }
        if (failures is { Count: 1 })
        { System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failures[0]).Throw(); }
        if (failures is { Count: > 1 })
        { throw new InspectionRangeDisposeException(failures[0], failures.Skip(1).ToArray()); }
    }

    private static void AddFlattened(List<Exception> failures, Exception error)
    {
        if (error is AggregateException aggregate)
        {
            foreach (Exception inner in aggregate.Flatten().InnerExceptions) { failures.Add(inner); }
        }
        else { failures.Add(error); }
    }

    private sealed record Batch(IReadOnlyList<InspectionRangeItem> Items,
        IInspectionTraceLease?[] OwnedLeases);
}

public sealed class InspectionRangeDisposeException : Exception
{
    public InspectionRangeDisposeException(Exception primaryException,
        IReadOnlyList<Exception> secondaryExceptions)
        : base("One or more inspection range leases failed to dispose.", primaryException)
    {
        ArgumentNullException.ThrowIfNull(primaryException);
        ArgumentNullException.ThrowIfNull(secondaryExceptions);
        PrimaryException = primaryException;
        SecondaryExceptions = Array.AsReadOnly(secondaryExceptions.ToArray());
    }

    public Exception PrimaryException { get; }
    public IReadOnlyList<Exception> SecondaryExceptions { get; }
}

public interface IUtTraceProvider
{
    ValueTask<InspectionTraceReadResult> ReadAsync(TraceIdentity identity, CancellationToken cancellationToken = default);
}

/// <summary>Bounded visual notification of an already committed trace; never carries sample memory.</summary>
public interface ICommittedTraceSink
{
    void OpenRun(AcquisitionRunId runId);
    void NotifyCommitted(TraceIdentity identity, InspectionRevision revision);
    void CloseRun(AcquisitionRunId runId);
    ValueTask WaitForCloseAsync(AcquisitionRunId runId) => ValueTask.CompletedTask;
}

/// <summary>
/// Optional monotonic lifecycle for a committed-trace projection that must reconcile a sealed
/// authoritative revision before it becomes a frozen, read-only view.
/// </summary>
public interface ICommittedTraceLifecycleSink : ICommittedTraceSink
{
    void RequestFinalReconciliation(AcquisitionRunId runId, InspectionRevision finalRevision);
    void StopAdmission(AcquisitionRunId runId);
    ValueTask WaitForDrainAsync(AcquisitionRunId runId);
    void FreezeRun(AcquisitionRunId runId);
}

public interface IInspectionRangeProvider
{
    ValueTask<InspectionRangeReadResult> ReadRangeAsync(InspectionRangeQuery query, CancellationToken cancellationToken = default);
}

public interface IInspectionStreamStateProvider
{
    ValueTask<InspectionStreamStateReadResult> ReadStreamStateAsync(UtStreamIdentity stream,
        CancellationToken cancellationToken = default);
}

public interface IInspectionDataStore : IInspectionDataWriter, IUtTraceProvider, IInspectionRangeProvider,
    IInspectionStreamStateProvider, IInspectionChangeProvider, IAsyncDisposable
{
    InspectionRevision Revision { get; }
    StoreDiagnostics Diagnostics { get; }
    ValueTask<IInspectionRunWriter> OpenRunAsync(AcquisitionRunId runId,
        CancellationToken cancellationToken = default);
    ValueTask<IReadOnlyList<GapRecord>> ReadGapsAsync(UtStreamIdentity stream, int maximumCount,
        CancellationToken cancellationToken = default);
}

public sealed class InspectionStoreCloseException : Exception
{
    public InspectionStoreCloseException(string primaryCode, IReadOnlyList<string> secondaryCodes,
        Exception? innerException = null)
        : base($"Inspection store cleanup failed: {primaryCode}.", innerException)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(primaryCode);
        ArgumentNullException.ThrowIfNull(secondaryCodes);
        PrimaryCode = primaryCode;
        SecondaryCodes = Array.AsReadOnly(secondaryCodes.ToArray());
    }

    public string PrimaryCode { get; }
    public IReadOnlyList<string> SecondaryCodes { get; }
}

public sealed class InspectionRangeAcquisitionException : Exception
{
    public InspectionRangeAcquisitionException(Exception primaryException,
        IReadOnlyList<Exception> cleanupExceptions)
        : base("Inspection range acquisition failed and was rolled back.", primaryException)
    {
        ArgumentNullException.ThrowIfNull(primaryException);
        ArgumentNullException.ThrowIfNull(cleanupExceptions);
        PrimaryException = primaryException;
        CleanupExceptions = Array.AsReadOnly(cleanupExceptions.ToArray());
    }

    public Exception PrimaryException { get; }
    public IReadOnlyList<Exception> CleanupExceptions { get; }
}
