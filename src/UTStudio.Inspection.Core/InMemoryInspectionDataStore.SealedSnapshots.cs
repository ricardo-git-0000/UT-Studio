using System.Buffers.Binary;
using System.Security.Cryptography;
using UTStudio.Contracts.Inspection;
using UTStudio.Domain.Acquisition;
using UTStudio.Domain.Inspection;

namespace UTStudio.Inspection.Core;

public sealed partial class InMemoryInspectionDataStore
{
    public ValueTask<InspectionSealedRunSnapshotOpenResult> OpenSealedRunSnapshotAsync(
        InspectionSealedRunSnapshotRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        try { return OpenSealedRunSnapshotCore(request, cancellationToken); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        { return ValueTask.FromResult(InspectionSealedRunSnapshotOpenResult.Unavailable(InspectionSealedRunSnapshotOpenStatus.Cancelled)); }
        catch (Exception error) when (error is not ArgumentException and not ObjectDisposedException)
        { return ValueTask.FromResult(InspectionSealedRunSnapshotOpenResult.Unavailable(InspectionSealedRunSnapshotOpenStatus.Faulted, error.GetType().Name)); }
    }

    private ValueTask<InspectionSealedRunSnapshotOpenResult> OpenSealedRunSnapshotCore(
        InspectionSealedRunSnapshotRequest request, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
            return ValueTask.FromResult(InspectionSealedRunSnapshotOpenResult.Unavailable(
                InspectionSealedRunSnapshotOpenStatus.Cancelled));
        SealedRunIndex sealedIndex;
        lock (_gate)
        {
            if (_closing || _closed || !_leasesAllowed)
                return ValueTask.FromResult(InspectionSealedRunSnapshotOpenResult.Unavailable(
                    InspectionSealedRunSnapshotOpenStatus.Closed));
            if (request.InspectionId != _inspectionId || !_runs.TryGetValue(request.RunId, out RunState? run))
                return ValueTask.FromResult(InspectionSealedRunSnapshotOpenResult.Unavailable(
                    InspectionSealedRunSnapshotOpenStatus.NotAvailable));
            if (run.State != InspectionRunState.Sealed || run.FinalRevision is null || run.SealedIndex is null)
                return ValueTask.FromResult(InspectionSealedRunSnapshotOpenResult.Unavailable(
                    InspectionSealedRunSnapshotOpenStatus.RunNotSealed));
            if (run.FinalRevision.Value != request.SourceFinalRevision)
                return ValueTask.FromResult(InspectionSealedRunSnapshotOpenResult.Unavailable(
                    InspectionSealedRunSnapshotOpenStatus.RevisionMismatch));
            foreach (UtStreamIdentity stream in request.Streams)
                if (stream.InspectionId != request.InspectionId || stream.AcquisitionRunId != request.RunId ||
                    !run.SealedIndex.TryGetStream(stream, out _))
                    return ValueTask.FromResult(InspectionSealedRunSnapshotOpenResult.Unavailable(
                        InspectionSealedRunSnapshotOpenStatus.NotAvailable));
            sealedIndex = run.SealedIndex;
            if (sealedIndex.InspectionId != request.InspectionId || sealedIndex.RunId != request.RunId ||
                sealedIndex.FinalRevision != request.SourceFinalRevision)
                return ValueTask.FromResult(InspectionSealedRunSnapshotOpenResult.Unavailable(
                    InspectionSealedRunSnapshotOpenStatus.Faulted, "sealed-index-identity-mismatch"));
            _activeLeases++;
            _maximumLeases = Math.Max(_maximumLeases, _activeLeases);
        }
        try
        {
            var snapshot = new SealedRunSnapshot(this, request, sealedIndex);
            return ValueTask.FromResult(InspectionSealedRunSnapshotOpenResult.Available(snapshot));
        }
        catch
        {
            ReleaseLease();
            throw;
        }
    }

    private sealed class SealedRunSnapshot : IInspectionSealedRunSnapshot
    {
        private readonly object _snapshotGate = new();
        private readonly InMemoryInspectionDataStore _owner;
        private readonly SealedRunIndex _sealedIndex;
        private readonly Guid _snapshotId = Guid.NewGuid();
        private readonly byte[] _cursorKey = RandomNumberGenerator.GetBytes(32);
        private readonly int _maximumPageSize;
        private readonly int _maximumPreparedQueries;
        private readonly long _preparedQueryBudgetBytes;
        private readonly UtStreamIdentity[] _requestedStreams;
        private readonly Dictionary<UtStreamIdentity, int> _requestedStreamOrdinals;
        private readonly IReadOnlyList<InspectionSealedStreamSnapshot> _streams;
        private readonly Dictionary<string, PreparedQuery> _activeQueries = [];
        private bool _disposed;
        private long _pagesRead, _identitiesVisited, _continuationSearches, _continuationSearchComparisons;
        private long _normalizedRanges, _lowerBounds, _upperBounds, _entriesReturned, _cursorResumes;
        private long _preparedReservedBytes, _preparedOwnedBytes, _preparedPeakBytes, _preparedRejections;

        internal SealedRunSnapshot(InMemoryInspectionDataStore owner,
            InspectionSealedRunSnapshotRequest request, SealedRunIndex sealedIndex)
        {
            _owner = owner; _sealedIndex = sealedIndex; InspectionId = request.InspectionId;
            RunId = request.RunId; SourceFinalRevision = request.SourceFinalRevision;
            _maximumPageSize = request.MaximumPageSize; _requestedStreams = request.Streams.ToArray();
            _requestedStreamOrdinals = _requestedStreams.Select((stream, ordinal) => (stream, ordinal))
                .ToDictionary(static item => item.stream, static item => item.ordinal);
            _maximumPreparedQueries = request.MaximumPreparedQueries;
            _preparedQueryBudgetBytes = request.PreparedQueryBudgetBytes;
            _streams = Array.AsReadOnly(_requestedStreams.Select(stream =>
            {
                _sealedIndex.TryGetStream(stream, out SealedStreamIndex index);
                TraceIdentity[] identities = index.Identities;
                return new InspectionSealedStreamSnapshot(stream,
                    identities.Length == 0 ? null : identities[0].AcquisitionIndex,
                    identities.Length == 0 ? null : identities[^1].AcquisitionIndex,
                    identities.Length, index.Digest);
            }).ToArray());
        }

        public ushort ContractVersion => 2;
        public InspectionId InspectionId { get; }
        public AcquisitionRunId RunId { get; }
        public InspectionRevision SourceFinalRevision { get; }
        public InspectionRunState RunState => InspectionRunState.Sealed;
        public int MaximumPageSize => _maximumPageSize;
        public IReadOnlyList<InspectionSealedStreamSnapshot> Streams => _streams;
        public InspectionSealedRunEnumerationDiagnostics EnumerationDiagnostics
        {
            get
            {
                lock (_snapshotGate)
                {
                    EnsureAlive(); return new(_pagesRead, _identitiesVisited,
                _continuationSearches, _continuationSearchComparisons, _normalizedRanges, _lowerBounds,
                _upperBounds, _entriesReturned, _cursorResumes, 0, _preparedReservedBytes,
                _preparedOwnedBytes, _preparedPeakBytes, _activeQueries.Count, _preparedRejections);
                }
            }
        }

        public long CountIdentities(InspectionSealedRunRangeQuery query)
        {
            ArgumentNullException.ThrowIfNull(query);
            lock (_snapshotGate) { return GetOrPrepare(query).SelectedCount; }
        }

        public InspectionSealedRunIdentityPage ReadIdentities(UtStreamIdentity stream,
            InspectionSealedRunCursor? cursor = null) => ReadIdentities(
                new InspectionSealedRunRangeQuery(stream,
                    [new InspectionSealedRunIndexRange(0, ulong.MaxValue)], _maximumPageSize), cursor);

        public InspectionSealedRunIdentityPage ReadIdentities(InspectionSealedRunRangeQuery query,
            InspectionSealedRunCursor? cursor = null)
        {
            ArgumentNullException.ThrowIfNull(query);
            lock (_snapshotGate)
            {
                EnsureAlive();
                if (!_requestedStreamOrdinals.TryGetValue(query.Stream, out int streamIndex))
                    throw new ArgumentException("The stream is not part of this snapshot.", nameof(query));
                if (query.PageSize > _maximumPageSize)
                    throw new ArgumentException("The query page size exceeds the snapshot limit.", nameof(query));
                if (cursor is not null) ValidateCursorBinding(cursor.Value, streamIndex, query);
                PreparedQuery prepared = GetOrPrepare(query);
                TraceIdentity[] identities = prepared.Stream.Identities;
                CursorState state = cursor is null ? prepared.InitialState : DecodeCursor(cursor.Value, streamIndex, query, prepared);
                var page = new List<TraceIdentity>(query.PageSize);
                int rangeOrdinal = state.RangeOrdinal, nextOrdinal = state.NextOrdinal;
                ulong last = state.LastAcquisitionIndex;
                while (page.Count < query.PageSize && rangeOrdinal < prepared.Spans.Length)
                {
                    OrdinalSpan span = prepared.Spans[rangeOrdinal];
                    if (nextOrdinal < span.Start) nextOrdinal = span.Start;
                    while (page.Count < query.PageSize && nextOrdinal < span.EndExclusive)
                    { TraceIdentity identity = identities[nextOrdinal++]; page.Add(identity); last = identity.AcquisitionIndex; }
                    if (nextOrdinal >= span.EndExclusive && page.Count < query.PageSize)
                    { rangeOrdinal++; if (rangeOrdinal < prepared.Spans.Length) nextOrdinal = prepared.Spans[rangeOrdinal].Start; }
                }
                InspectionSealedRunCursor? next = HasRemaining(prepared.Spans, rangeOrdinal, nextOrdinal)
                    ? EncodeCursor(streamIndex, query, new(rangeOrdinal, nextOrdinal, last)) : null;
                _pagesRead = checked(_pagesRead + 1); _identitiesVisited = checked(_identitiesVisited + page.Count);
                _entriesReturned = checked(_entriesReturned + page.Count);
                return new(page, next);
            }
        }

        public ValueTask<InspectionTraceReadResult> ReadAsync(TraceIdentity identity,
            CancellationToken cancellationToken = default)
        {
            if (!identity.IsValid) throw new ArgumentException("A valid identity is required.", nameof(identity));
            try { return ReadCoreAsync(identity, cancellationToken); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            { return ValueTask.FromResult(InspectionTraceReadResult.Unavailable(InspectionReadStatus.Cancelled, SourceFinalRevision)); }
            catch (Exception error) when (error is not ArgumentException and not ObjectDisposedException)
            { return ValueTask.FromResult(InspectionTraceReadResult.Unavailable(InspectionReadStatus.Faulted, SourceFinalRevision, error.GetType().Name)); }
        }

        private ValueTask<InspectionTraceReadResult> ReadCoreAsync(TraceIdentity identity, CancellationToken token)
        {
            if (token.IsCancellationRequested) return ValueTask.FromResult(InspectionTraceReadResult.Unavailable(
                InspectionReadStatus.Cancelled, SourceFinalRevision));
            lock (_snapshotGate)
            {
                EnsureAlive();
                if (!_requestedStreamOrdinals.ContainsKey(identity.Stream))
                    return ValueTask.FromResult(InspectionTraceReadResult.Unavailable(
                        InspectionReadStatus.OutOfRange, SourceFinalRevision));
                lock (_owner._gate)
                {
                    if (_owner._closed) return ValueTask.FromResult(InspectionTraceReadResult.Unavailable(
                        InspectionReadStatus.Closed, SourceFinalRevision));
                    if (!_sealedIndex.TryGetStream(identity.Stream, out SealedStreamIndex sealedStream))
                        return ValueTask.FromResult(InspectionTraceReadResult.Unavailable(
                            InspectionReadStatus.NotAvailable, SourceFinalRevision));
                    int ordinal = FindIdentity(sealedStream.Identities, identity.AcquisitionIndex);
                    if (ordinal < 0 || sealedStream.Identities[ordinal] != identity ||
                        sealedStream.Entries[ordinal].Descriptor.Identity != identity)
                        return ValueTask.FromResult(InspectionTraceReadResult.Unavailable(
                            InspectionReadStatus.NotAvailable, SourceFinalRevision));
                    Entry entry = sealedStream.Entries[ordinal];
                    return ValueTask.FromResult(InspectionTraceReadResult.Available(entry.Revision,
                        _owner.CreateLeaseUnderLock(entry)));
                }
            }
        }

        public void Dispose()
        {
            bool release; long preparedBytes; lock (_snapshotGate)
            {
                release = !_disposed; _disposed = true;
                preparedBytes = release ? _preparedOwnedBytes : 0;
                if (release) { _activeQueries.Clear(); _preparedReservedBytes = 0; _preparedOwnedBytes = 0; }
            }
            if (release) { if (preparedBytes != 0) _owner.ReleasePreparedQueryCapacity(preparedBytes); _owner.ReleaseLease(); }
        }

        private int LowerBound(TraceIdentity[] identities, ulong value)
        {
            _continuationSearches = checked(_continuationSearches + 1); _lowerBounds++; int low = 0, high = identities.Length;
            while (low < high)
            {
                _continuationSearchComparisons = checked(_continuationSearchComparisons + 1);
                int middle = low + ((high - low) >> 1); if (identities[middle].AcquisitionIndex < value) low = middle + 1; else high = middle;
            }
            return low;
        }
        private static int FindIdentity(TraceIdentity[] identities, ulong value)
        {
            int low = 0, high = identities.Length - 1;
            while (low <= high)
            {
                int middle = low + ((high - low) >> 1);
                ulong current = identities[middle].AcquisitionIndex;
                if (current < value) low = middle + 1;
                else if (current > value) high = middle - 1;
                else return middle;
            }
            return -1;
        }
        private static bool HasRemaining(OrdinalSpan[] spans, int rangeOrdinal, int nextOrdinal)
        {
            if (rangeOrdinal >= spans.Length) return false;
            if (nextOrdinal < spans[rangeOrdinal].EndExclusive) return true;
            for (int i = rangeOrdinal + 1; i < spans.Length; i++)
                if (spans[i].Start < spans[i].EndExclusive) return true;
            return false;
        }
        private int UpperBound(TraceIdentity[] identities, ulong value)
        {
            _continuationSearches = checked(_continuationSearches + 1); _upperBounds++; int low = 0, high = identities.Length;
            while (low < high)
            {
                _continuationSearchComparisons = checked(_continuationSearchComparisons + 1);
                int middle = low + ((high - low) >> 1); if (identities[middle].AcquisitionIndex <= value) low = middle + 1; else high = middle;
            }
            return low;
        }

        private InspectionSealedRunCursor EncodeCursor(int streamIndex, InspectionSealedRunRangeQuery query, CursorState state)
        {
            Span<byte> encoded = stackalloc byte[127]; encoded[0] = 3;
            _snapshotId.TryWriteBytes(encoded[1..17]); InspectionId.Value.TryWriteBytes(encoded[17..33]);
            RunId.Value.TryWriteBytes(encoded[33..49]); BinaryPrimitives.WriteUInt64LittleEndian(encoded[49..57], SourceFinalRevision.Value);
            BinaryPrimitives.WriteUInt16LittleEndian(encoded[57..59], checked((ushort)query.PageSize));
            BinaryPrimitives.WriteInt32LittleEndian(encoded[59..63], streamIndex); Convert.FromHexString(query.RangeDigest).CopyTo(encoded[63..95]);
            BinaryPrimitives.WriteInt32LittleEndian(encoded[95..99], state.RangeOrdinal);
            BinaryPrimitives.WriteInt32LittleEndian(encoded[99..103], state.NextOrdinal);
            BinaryPrimitives.WriteInt32LittleEndian(encoded[103..107], 0);
            BinaryPrimitives.WriteUInt64LittleEndian(encoded[107..115], state.LastAcquisitionIndex);
            HMACSHA256.HashData(_cursorKey, encoded[..115]).AsSpan(0, 12).CopyTo(encoded[115..127]);
            return new(Convert.ToBase64String(encoded));
        }

        private CursorState DecodeCursor(InspectionSealedRunCursor cursor, int expectedStream,
            InspectionSealedRunRangeQuery query, PreparedQuery prepared)
        {
            if (!cursor.IsValid) throw new ArgumentException("A valid cursor is required.", nameof(cursor));
            try
            {
                byte[] encoded = Convert.FromBase64String(cursor.Value);
                if (encoded.Length != 127 || encoded[0] != 3 || new Guid(encoded.AsSpan(1, 16)) != _snapshotId ||
                    new Guid(encoded.AsSpan(17, 16)) != InspectionId.Value || new Guid(encoded.AsSpan(33, 16)) != RunId.Value ||
                    BinaryPrimitives.ReadUInt64LittleEndian(encoded.AsSpan(49, 8)) != SourceFinalRevision.Value ||
                    BinaryPrimitives.ReadUInt16LittleEndian(encoded.AsSpan(57, 2)) != query.PageSize ||
                    BinaryPrimitives.ReadInt32LittleEndian(encoded.AsSpan(59, 4)) != expectedStream ||
                    !CryptographicOperations.FixedTimeEquals(encoded.AsSpan(63, 32), Convert.FromHexString(query.RangeDigest)) ||
                    !CryptographicOperations.FixedTimeEquals(encoded.AsSpan(115, 12), HMACSHA256.HashData(_cursorKey, encoded.AsSpan(0, 115)).AsSpan(0, 12)))
                    throw new FormatException();
                var state = new CursorState(BinaryPrimitives.ReadInt32LittleEndian(encoded.AsSpan(95, 4)),
                    BinaryPrimitives.ReadInt32LittleEndian(encoded.AsSpan(99, 4)),
                    BinaryPrimitives.ReadUInt64LittleEndian(encoded.AsSpan(107, 8)));
                if (state.RangeOrdinal < 0 || state.RangeOrdinal >= prepared.Spans.Length)
                    throw new FormatException();
                OrdinalSpan span = prepared.Spans[state.RangeOrdinal];
                if (state.NextOrdinal <= span.Start || state.NextOrdinal > span.EndExclusive ||
                    state.NextOrdinal > prepared.Stream.Identities.Length ||
                    prepared.Stream.Identities[state.NextOrdinal - 1].AcquisitionIndex != state.LastAcquisitionIndex)
                    throw new FormatException();
                _cursorResumes = checked(_cursorResumes + 1);
                return state;
            }
            catch (Exception error) when (error is FormatException or ArgumentException)
            { throw new ArgumentException("The cursor is not compatible with this snapshot query.", nameof(cursor)); }
        }

        private void ValidateCursorBinding(InspectionSealedRunCursor cursor, int expectedStream,
            InspectionSealedRunRangeQuery query)
        {
            if (!cursor.IsValid) throw new ArgumentException("A valid cursor is required.", nameof(cursor));
            try
            {
                byte[] encoded = Convert.FromBase64String(cursor.Value);
                if (encoded.Length != 127 || encoded[0] != 3 || new Guid(encoded.AsSpan(1, 16)) != _snapshotId ||
                    new Guid(encoded.AsSpan(17, 16)) != InspectionId.Value || new Guid(encoded.AsSpan(33, 16)) != RunId.Value ||
                    BinaryPrimitives.ReadUInt64LittleEndian(encoded.AsSpan(49, 8)) != SourceFinalRevision.Value ||
                    BinaryPrimitives.ReadUInt16LittleEndian(encoded.AsSpan(57, 2)) != query.PageSize ||
                    BinaryPrimitives.ReadInt32LittleEndian(encoded.AsSpan(59, 4)) != expectedStream ||
                    !CryptographicOperations.FixedTimeEquals(encoded.AsSpan(63, 32), Convert.FromHexString(query.RangeDigest)) ||
                    !CryptographicOperations.FixedTimeEquals(encoded.AsSpan(115, 12),
                        HMACSHA256.HashData(_cursorKey, encoded.AsSpan(0, 115)).AsSpan(0, 12)))
                    throw new FormatException();
            }
            catch (Exception error) when (error is FormatException or ArgumentException)
            { throw new ArgumentException("The cursor is not compatible with this snapshot query.", nameof(cursor)); }
        }

        private void EnsureAlive()
        { if (_disposed) throw new ObjectDisposedException(nameof(IInspectionSealedRunSnapshot)); }
        private PreparedQuery GetOrPrepare(InspectionSealedRunRangeQuery query)
        {
            EnsureAlive();
            if (!_requestedStreamOrdinals.ContainsKey(query.Stream))
                throw new ArgumentException("The stream is not part of this snapshot.", nameof(query));
            string key = QueryKey(query);
            if (_activeQueries.TryGetValue(key, out PreparedQuery? existing)) return existing;
            _sealedIndex.TryGetStream(query.Stream, out SealedStreamIndex stream);
            long bytes = checked(128L + 24L * query.Ranges.Count);
            if (_activeQueries.Count >= _maximumPreparedQueries ||
                bytes > _preparedQueryBudgetBytes - _preparedOwnedBytes - _preparedReservedBytes)
            { _preparedRejections++; throw new InvalidOperationException("The snapshot prepared-query capacity is exhausted."); }
            if (!_owner.TryReservePreparedQueryCapacity(bytes))
            { _preparedRejections++; throw new InvalidOperationException("The store prepared-query capacity is exhausted."); }
            _preparedReservedBytes = checked(_preparedReservedBytes + bytes);
            OrdinalSpan[] spans;
            try { spans = new OrdinalSpan[query.Ranges.Count]; }
            catch { _preparedReservedBytes -= bytes; _owner.RollbackPreparedQueryCapacity(bytes); throw; }
            long selected = 0;
            try
            {
                for (int i = 0; i < spans.Length; i++)
                {
                    int start = LowerBound(stream.Identities, query.Ranges[i].FirstInclusive);
                    int end = UpperBound(stream.Identities, query.Ranges[i].LastInclusive);
                    spans[i] = new(start, end); selected = checked(selected + end - start);
                }
                _normalizedRanges = checked(_normalizedRanges + spans.Length);
                var prepared = new PreparedQuery(stream, spans, selected, bytes);
                _activeQueries.Add(key, prepared);
                _preparedReservedBytes -= bytes; _preparedOwnedBytes = checked(_preparedOwnedBytes + bytes);
                _owner.ConfirmPreparedQueryCapacity(bytes);
                _preparedPeakBytes = Math.Max(_preparedPeakBytes, _preparedOwnedBytes);
                return prepared;
            }
            catch { _preparedReservedBytes -= bytes; _owner.RollbackPreparedQueryCapacity(bytes); throw; }
        }

        private static string QueryKey(InspectionSealedRunRangeQuery query) =>
            $"{query.Stream.SourceId.Value}\u001f{query.Stream.PhysicalChannelId.Value:D10}\u001f" +
            $"{query.Stream.BeamId?.Value ?? -1:D10}\u001f{query.Stream.FocalLawId?.Value ?? -1:D10}:" +
            $"{query.PageSize}:{query.RangeDigest}";
        private readonly record struct OrdinalSpan(int Start, int EndExclusive);
        private sealed record PreparedQuery(SealedStreamIndex Stream, OrdinalSpan[] Spans,
            long SelectedCount, long CapacityBytes)
        {
            internal CursorState InitialState => Spans.Length == 0
                ? new(0, 0, 0) : new(0, Spans[0].Start, 0);
        }
        private readonly record struct CursorState(int RangeOrdinal, int NextOrdinal, ulong LastAcquisitionIndex);
    }
}
