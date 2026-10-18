using System.Collections.ObjectModel;
using UTStudio.Domain.Acquisition;
using UTStudio.Domain.Inspection;

namespace UTStudio.Contracts.Inspection;

public enum InspectionSealedRunSnapshotOpenStatus
{
    Unknown = 0, Available = 1, NotAvailable = 2, RevisionMismatch = 3,
    RunNotSealed = 4, Cancelled = 5, Closed = 6, Faulted = 7
}

public sealed class InspectionSealedRunSnapshotRequest
{
    private readonly ReadOnlyCollection<UtStreamIdentity> _streams;

    public InspectionSealedRunSnapshotRequest(InspectionId inspectionId, AcquisitionRunId runId,
        InspectionRevision sourceFinalRevision, IEnumerable<UtStreamIdentity> streams, int maximumPageSize,
        int maximumPreparedQueries = 64, long preparedQueryBudgetBytes = 1_048_576)
    {
        ArgumentNullException.ThrowIfNull(streams);
        UtStreamIdentity[] captured = streams.Distinct().OrderBy(StreamSortKey, StringComparer.Ordinal).ToArray();
        if (!inspectionId.IsValid || !runId.IsValid || sourceFinalRevision == InspectionRevision.Initial ||
            captured.Length == 0 || maximumPageSize is < 1 or > 256 || maximumPreparedQueries is < 1 or > 1024 ||
            preparedQueryBudgetBytes < 128 ||
            captured.Any(x => !x.IsValid || x.InspectionId != inspectionId || x.AcquisitionRunId != runId))
        { throw new ArgumentException("The sealed-run snapshot request is invalid."); }
        InspectionId = inspectionId; RunId = runId; SourceFinalRevision = sourceFinalRevision;
        _streams = Array.AsReadOnly(captured); MaximumPageSize = maximumPageSize;
        MaximumPreparedQueries = maximumPreparedQueries; PreparedQueryBudgetBytes = preparedQueryBudgetBytes;
    }

    public InspectionId InspectionId { get; }
    public AcquisitionRunId RunId { get; }
    public InspectionRevision SourceFinalRevision { get; }
    public IReadOnlyList<UtStreamIdentity> Streams => _streams;
    public int MaximumPageSize { get; }
    public int MaximumPreparedQueries { get; }
    public long PreparedQueryBudgetBytes { get; }

    internal static string StreamSortKey(UtStreamIdentity value) =>
        $"{value.SourceId.Value}\u001f{value.PhysicalChannelId.Value:D10}\u001f{value.BeamId?.Value ?? -1:D10}\u001f{value.FocalLawId?.Value ?? -1:D10}";
}

public sealed class InspectionSealedStreamSnapshot
{
    public InspectionSealedStreamSnapshot(UtStreamIdentity stream, ulong? minimumAcquisitionIndex,
        ulong? maximumAcquisitionIndex, int confirmedTraceCount, string identityDigest)
    {
        if (!stream.IsValid || confirmedTraceCount < 0 ||
            (minimumAcquisitionIndex is null) != (maximumAcquisitionIndex is null) ||
            (confirmedTraceCount == 0) != (minimumAcquisitionIndex is null) ||
            minimumAcquisitionIndex > maximumAcquisitionIndex || identityDigest is not { Length: 64 } ||
            !identityDigest.All(Uri.IsHexDigit))
        { throw new ArgumentException("The sealed stream snapshot is invalid."); }
        Stream = stream; MinimumAcquisitionIndex = minimumAcquisitionIndex;
        MaximumAcquisitionIndex = maximumAcquisitionIndex; ConfirmedTraceCount = confirmedTraceCount;
        IdentityDigest = identityDigest.ToUpperInvariant();
    }
    public UtStreamIdentity Stream { get; }
    public ulong? MinimumAcquisitionIndex { get; }
    public ulong? MaximumAcquisitionIndex { get; }
    public int ConfirmedTraceCount { get; }
    public string IdentityDigest { get; }
}

public readonly record struct InspectionSealedRunCursor(string Value)
{
    public bool IsValid => !string.IsNullOrWhiteSpace(Value) && Value.Length <= 256;
}

public readonly record struct InspectionSealedRunIndexRange
{
    public InspectionSealedRunIndexRange(ulong firstInclusive, ulong lastInclusive)
    {
        if (firstInclusive > lastInclusive)
            throw new ArgumentException("The inclusive range is empty or inverted.");
        FirstInclusive = firstInclusive;
        LastInclusive = lastInclusive;
    }

    public ulong FirstInclusive { get; }
    public ulong LastInclusive { get; }
}

public sealed class InspectionSealedRunRangeQuery
{
    private readonly ReadOnlyCollection<InspectionSealedRunIndexRange> _ranges;

    public InspectionSealedRunRangeQuery(UtStreamIdentity stream,
        IEnumerable<InspectionSealedRunIndexRange> ranges, int pageSize)
    {
        ArgumentNullException.ThrowIfNull(ranges);
        if (!stream.IsValid || pageSize is < 1 or > 256)
            throw new ArgumentException("The sealed-run range query is invalid.");
        var normalized = new List<InspectionSealedRunIndexRange>();
        foreach (InspectionSealedRunIndexRange range in ranges.OrderBy(x => x.FirstInclusive)
                     .ThenBy(x => x.LastInclusive))
        {
            if (normalized.Count == 0)
            {
                normalized.Add(range);
                continue;
            }
            InspectionSealedRunIndexRange previous = normalized[^1];
            bool adjacent = previous.LastInclusive != ulong.MaxValue &&
                range.FirstInclusive == previous.LastInclusive + 1;
            if (range.FirstInclusive <= previous.LastInclusive || adjacent)
                normalized[^1] = new(previous.FirstInclusive,
                    Math.Max(previous.LastInclusive, range.LastInclusive));
            else
                normalized.Add(range);
        }
        Stream = stream;
        _ranges = Array.AsReadOnly(normalized.ToArray());
        PageSize = pageSize;
        RangeDigest = ComputeDigest(_ranges);
    }

    public UtStreamIdentity Stream { get; }
    public IReadOnlyList<InspectionSealedRunIndexRange> Ranges => _ranges;
    public int PageSize { get; }
    public string RangeDigest { get; }

    private static string ComputeDigest(IReadOnlyList<InspectionSealedRunIndexRange> ranges)
    {
        using var hash = System.Security.Cryptography.IncrementalHash.CreateHash(
            System.Security.Cryptography.HashAlgorithmName.SHA256);
        Span<byte> value = stackalloc byte[18];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(value, 1);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(value[2..], ranges.Count);
        hash.AppendData(value[..6]);
        foreach (InspectionSealedRunIndexRange range in ranges)
        {
            System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(value, range.FirstInclusive);
            System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(value[8..], range.LastInclusive);
            hash.AppendData(value[..16]);
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }
}

public sealed class InspectionSealedRunIdentityPage
{
    public InspectionSealedRunIdentityPage(IReadOnlyList<TraceIdentity> identities,
        InspectionSealedRunCursor? nextCursor)
    {
        ArgumentNullException.ThrowIfNull(identities);
        if (identities.Count > 256 || identities.Any(x => !x.IsValid) || nextCursor is { IsValid: false })
        { throw new ArgumentException("The sealed-run identity page is invalid."); }
        Identities = Array.AsReadOnly(identities.ToArray()); NextCursor = nextCursor;
    }
    public IReadOnlyList<TraceIdentity> Identities { get; }
    public InspectionSealedRunCursor? NextCursor { get; }
}

public readonly record struct InspectionSealedRunEnumerationDiagnostics(
    long PagesRead, long IdentitiesVisited, long ContinuationSearches,
    long ContinuationSearchComparisons,
    long NormalizedRangeCount = 0, long LowerBoundSearches = 0,
    long UpperBoundSearches = 0, long EntriesReturned = 0,
    long CursorResumes = 0, long PrefixRescans = 0,
    long PreparedQueryReservedBytes = 0, long PreparedQueryOwnedBytes = 0,
    long PreparedQueryPeakBytes = 0, int ActivePreparedQueries = 0,
    long PreparedQueryRejections = 0);

public interface IInspectionSealedRunSnapshot : IDisposable
{
    const ushort CurrentContractVersion = 2;
    ushort ContractVersion { get; }
    InspectionId InspectionId { get; }
    AcquisitionRunId RunId { get; }
    InspectionRevision SourceFinalRevision { get; }
    InspectionRunState RunState { get; }
    int MaximumPageSize { get; }
    IReadOnlyList<InspectionSealedStreamSnapshot> Streams { get; }
    InspectionSealedRunEnumerationDiagnostics EnumerationDiagnostics { get; }
    long CountIdentities(InspectionSealedRunRangeQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        long count = 0;
        InspectionSealedRunCursor? cursor = null;
        do
        {
            InspectionSealedRunIdentityPage page = ReadIdentities(query, cursor);
            count = checked(count + page.Identities.Count);
            cursor = page.NextCursor;
        } while (cursor is not null);
        return count;
    }
    InspectionSealedRunIdentityPage ReadIdentities(InspectionSealedRunRangeQuery query,
        InspectionSealedRunCursor? cursor = null);
    InspectionSealedRunIdentityPage ReadIdentities(UtStreamIdentity stream,
        InspectionSealedRunCursor? cursor = null);
    ValueTask<InspectionTraceReadResult> ReadAsync(TraceIdentity identity,
        CancellationToken cancellationToken = default);
}

public readonly struct InspectionSealedRunSnapshotOpenResult
{
    private InspectionSealedRunSnapshotOpenResult(InspectionSealedRunSnapshotOpenStatus status,
        IInspectionSealedRunSnapshot? snapshot, string? errorCode)
    { Status = status; Snapshot = snapshot; ErrorCode = errorCode; }
    public InspectionSealedRunSnapshotOpenStatus Status { get; }
    public IInspectionSealedRunSnapshot? Snapshot { get; }
    public string? ErrorCode { get; }
    public static InspectionSealedRunSnapshotOpenResult Available(IInspectionSealedRunSnapshot snapshot) =>
        new(InspectionSealedRunSnapshotOpenStatus.Available,
            snapshot ?? throw new ArgumentNullException(nameof(snapshot)), null);
    public static InspectionSealedRunSnapshotOpenResult Unavailable(
        InspectionSealedRunSnapshotOpenStatus status, string? errorCode = null)
    {
        if (!Enum.IsDefined(status) || status is InspectionSealedRunSnapshotOpenStatus.Unknown or
            InspectionSealedRunSnapshotOpenStatus.Available)
        { throw new ArgumentOutOfRangeException(nameof(status)); }
        return new(status, null, errorCode);
    }
}

public interface IInspectionSealedRunSnapshotProvider
{
    ValueTask<InspectionSealedRunSnapshotOpenResult> OpenSealedRunSnapshotAsync(
        InspectionSealedRunSnapshotRequest request, CancellationToken cancellationToken = default);
}
