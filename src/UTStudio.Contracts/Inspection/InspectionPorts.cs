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

public readonly record struct InspectionTraceReadResult(InspectionReadStatus Status,
    InspectionRevision Revision, IInspectionTraceLease? Lease = null, string? ErrorCode = null);

public readonly record struct InspectionRangeItem(TraceIdentity Identity, InspectionReadStatus Status,
    IInspectionTraceLease? Lease = null);

public sealed class InspectionRangeReadResult : IDisposable
{
    private int _disposed;

    public InspectionRangeReadResult(InspectionReadStatus status, InspectionRevision revision,
        IReadOnlyList<InspectionRangeItem> items, string? errorCode = null)
    {
        ArgumentNullException.ThrowIfNull(items);
        Status = status;
        Revision = revision;
        Items = Array.AsReadOnly(items.ToArray());
        ErrorCode = errorCode;
    }

    public InspectionReadStatus Status { get; }
    public InspectionRevision Revision { get; }
    public IReadOnlyList<InspectionRangeItem> Items { get; }
    public string? ErrorCode { get; }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) { return; }
        for (int index = Items.Count - 1; index >= 0; index--) { Items[index].Lease?.Dispose(); }
    }
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

public interface IInspectionRangeProvider
{
    ValueTask<InspectionRangeReadResult> ReadRangeAsync(InspectionRangeQuery query, CancellationToken cancellationToken = default);
}

public interface IInspectionDataStore : IInspectionDataWriter, IUtTraceProvider, IInspectionRangeProvider, IAsyncDisposable
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
