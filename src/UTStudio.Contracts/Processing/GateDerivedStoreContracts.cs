using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text;
using UTStudio.Contracts.Inspection;
using UTStudio.Domain.Acquisition;
using UTStudio.Domain.Inspection;
using UTStudio.Domain.Processing;

namespace UTStudio.Contracts.Processing;

public enum GateDerivedMutationStatus
{
    Unknown, Committed, Duplicate, Conflict, CapacityExceeded, Cancelled, Closed, Faulted,
    Activated, Invalidated, Reclaimed, Busy, NotFound, CoverageMismatch
}

public enum GateDerivedNoDataMode { Unknown, Omit, Include }
public enum GateDerivedNoDataReason { Unknown, MissingResult, MeasureUnavailable }
public enum GateDerivedUnit { Unknown, DigitalCounts, Seconds, StatusCode, QualityCode }

public readonly struct GateSha256Digest : IEquatable<GateSha256Digest>
{
    private readonly ulong _a, _b, _c, _d;
    public GateSha256Digest(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length != 32) throw new ArgumentException("A SHA-256 digest must contain 32 bytes.", nameof(bytes));
        _a = System.Buffers.Binary.BinaryPrimitives.ReadUInt64BigEndian(bytes);
        _b = System.Buffers.Binary.BinaryPrimitives.ReadUInt64BigEndian(bytes[8..]);
        _c = System.Buffers.Binary.BinaryPrimitives.ReadUInt64BigEndian(bytes[16..]);
        _d = System.Buffers.Binary.BinaryPrimitives.ReadUInt64BigEndian(bytes[24..]);
    }
    public void CopyTo(Span<byte> destination)
    {
        if (destination.Length < 32) throw new ArgumentException("Destination is too short.", nameof(destination));
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64BigEndian(destination, _a);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64BigEndian(destination[8..], _b);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64BigEndian(destination[16..], _c);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64BigEndian(destination[24..], _d);
    }
    public bool Equals(GateSha256Digest other) => _a == other._a && _b == other._b && _c == other._c && _d == other._d;
    public override bool Equals(object? obj) => obj is GateSha256Digest other && Equals(other);
    public override int GetHashCode()
    {
        ulong mixed = _a ^ Rotate(_b, 13) ^ Rotate(_c, 29) ^ Rotate(_d, 47);
        return unchecked((int)(mixed ^ (mixed >> 32)));
    }
    public override string ToString() { Span<byte> bytes = stackalloc byte[32]; CopyTo(bytes); return Convert.ToHexString(bytes); }
    public static bool operator ==(GateSha256Digest left, GateSha256Digest right) => left.Equals(right);
    public static bool operator !=(GateSha256Digest left, GateSha256Digest right) => !left.Equals(right);
    private static ulong Rotate(ulong value, int bits) => (value << bits) | (value >> (64 - bits));
}

public readonly record struct GateDerivedBatchKey
{
    public GateDerivedBatchKey(TraceIdentity traceIdentity, InspectionRevision sourceInspectionRevision,
        GatePlanId planId, GatePlanRevision planRevision, string planSignature,
        GateSchemaVersion schemaVersion, GateAlgorithmVersion algorithmVersion)
    {
        TraceIdentity = traceIdentity; SourceInspectionRevision = sourceInspectionRevision; PlanId = planId;
        PlanRevision = planRevision; PlanSignature = planSignature; SchemaVersion = schemaVersion;
        AlgorithmVersion = algorithmVersion;
    }
    public TraceIdentity TraceIdentity { get; }
    public InspectionRevision SourceInspectionRevision { get; }
    public GatePlanId PlanId { get; }
    public GatePlanRevision PlanRevision { get; }
    public string PlanSignature { get; }
    public GateSchemaVersion SchemaVersion { get; }
    public GateAlgorithmVersion AlgorithmVersion { get; }
    public bool IsValid => TraceIdentity.IsValid && SourceInspectionRevision != InspectionRevision.Initial &&
        PlanId.IsValid && PlanRevision.IsValid && PlanSignature is { Length: 64 } && PlanSignature.All(Uri.IsHexDigit) &&
        SchemaVersion.IsValid && AlgorithmVersion.IsValid;
}

public readonly record struct GateDerivedLayerKey
{
    public GateDerivedLayerKey(GateDerivedBatchKey batch, GateId gateId, GateRevision gateRevision,
        GateMeasureKind measure, GateDerivedUnit unit, GateSignalDomain domain)
    {
        if (!batch.IsValid || !gateId.IsValid || !gateRevision.IsValid || !Enum.IsDefined(measure) ||
            measure == GateMeasureKind.Unknown || !Enum.IsDefined(unit) || unit == GateDerivedUnit.Unknown ||
            !Enum.IsDefined(domain) || domain == GateSignalDomain.Unknown || unit != UnitFor(measure))
        { throw new ArgumentException("The derived layer key is invalid."); }
        Batch = batch; GateId = gateId; GateRevision = gateRevision; Measure = measure;
        Unit = unit; Domain = domain;
    }
    public GateDerivedBatchKey Batch { get; }
    public GateId GateId { get; }
    public GateRevision GateRevision { get; }
    public GateMeasureKind Measure { get; }
    public GateDerivedUnit Unit { get; }
    public GateSignalDomain Domain { get; }
    private static GateDerivedUnit UnitFor(GateMeasureKind measure) => measure switch
    {
        GateMeasureKind.Amplitude => GateDerivedUnit.DigitalCounts,
        GateMeasureKind.DetectionTime or GateMeasureKind.TimeOfFlight => GateDerivedUnit.Seconds,
        GateMeasureKind.Status => GateDerivedUnit.StatusCode,
        GateMeasureKind.Quality => GateDerivedUnit.QualityCode,
        _ => GateDerivedUnit.Unknown
    };
}

public sealed class GateDerivedStreamCoverage
{
    public const ushort DigestCanonicalVersion = 1;
    public GateDerivedStreamCoverage(UtStreamIdentity stream, ulong? minimumAcquisitionIndex,
        ulong? maximumAcquisitionIndex, int eligibleTraceCount, ReadOnlySpan<byte> identityDigest)
        : this(stream, minimumAcquisitionIndex, maximumAcquisitionIndex, eligibleTraceCount,
            new GateSha256Digest(identityDigest))
    { }
    public GateDerivedStreamCoverage(UtStreamIdentity stream, ulong? minimumAcquisitionIndex,
        ulong? maximumAcquisitionIndex, int eligibleTraceCount, GateSha256Digest identityDigest)
    {
        if (!stream.IsValid || Encoding.UTF8.GetByteCount(stream.SourceId.Value) > 128)
        { throw new ArgumentException("A valid bounded stream is required.", nameof(stream)); }
        ArgumentOutOfRangeException.ThrowIfNegative(eligibleTraceCount);
        if ((minimumAcquisitionIndex is null) != (maximumAcquisitionIndex is null) ||
            (eligibleTraceCount == 0) != (minimumAcquisitionIndex is null) ||
            minimumAcquisitionIndex > maximumAcquisitionIndex)
        { throw new ArgumentException("Watermarks and eligible count are inconsistent."); }
        Stream = stream; MinimumAcquisitionIndex = minimumAcquisitionIndex;
        MaximumAcquisitionIndex = maximumAcquisitionIndex; EligibleTraceCount = eligibleTraceCount;
        IdentityDigest = identityDigest;
    }
    public UtStreamIdentity Stream { get; }
    public ulong? MinimumAcquisitionIndex { get; }
    public ulong? MaximumAcquisitionIndex { get; }
    public int EligibleTraceCount { get; }
    public GateSha256Digest IdentityDigest { get; }

    public static GateSha256Digest ComputeIdentityDigest(IEnumerable<TraceIdentity> identities)
    {
        ArgumentNullException.ThrowIfNull(identities);
        TraceIdentity[] ordered = identities.OrderBy(x => x.Stream.InspectionId.Value)
            .ThenBy(x => x.Stream.AcquisitionRunId.Value).ThenBy(x => x.Stream.SourceId.Value, StringComparer.Ordinal)
            .ThenBy(x => x.Stream.PhysicalChannelId.Value).ThenBy(x => x.Stream.BeamId?.Value ?? -1)
            .ThenBy(x => x.Stream.FocalLawId?.Value ?? -1).ThenBy(x => x.AcquisitionIndex).ToArray();
        using var payload = new MemoryStream();
        using (var writer = new BinaryWriter(payload, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(DigestCanonicalVersion); writer.Write(ordered.Length);
            foreach (TraceIdentity identity in ordered)
            {
                writer.Write(identity.Stream.InspectionId.Value.ToByteArray());
                writer.Write(identity.Stream.AcquisitionRunId.Value.ToByteArray());
                byte[] source = Encoding.UTF8.GetBytes(identity.Stream.SourceId.Value);
                writer.Write(source.Length); writer.Write(source);
                writer.Write(identity.Stream.PhysicalChannelId.Value);
                writer.Write(identity.Stream.BeamId?.Value ?? -1);
                writer.Write(identity.Stream.FocalLawId?.Value ?? -1);
                writer.Write(identity.AcquisitionIndex);
            }
        }
        return new(SHA256.HashData(payload.GetBuffer().AsSpan(0, checked((int)payload.Length))));
    }
}

public sealed class GateDerivedCoverageManifest
{
    private readonly ReadOnlyCollection<GateDerivedStreamCoverage> _streams;
    public GateDerivedCoverageManifest(InspectionId inspectionId, AcquisitionRunId runId,
        IEnumerable<GateDerivedStreamCoverage> streams, InspectionRevision sourceFinalRevision,
        GatePlanId planId, GatePlanRevision planRevision, string planSignature,
        GateSchemaVersion schemaVersion, GateAlgorithmVersion algorithmVersion,
        int totalEligibleTraceCount, DerivedStoreRevision capturedStoreRevision)
    {
        ArgumentNullException.ThrowIfNull(streams);
        GateDerivedStreamCoverage[] captured = streams.ToArray();
        if (!inspectionId.IsValid || !runId.IsValid || sourceFinalRevision == InspectionRevision.Initial ||
            !planId.IsValid || !planRevision.IsValid || !IsSha256Hex(planSignature) ||
            !schemaVersion.IsValid || !algorithmVersion.IsValid ||
            captured.Length == 0 || captured.Any(x => x.Stream.InspectionId != inspectionId ||
                x.Stream.AcquisitionRunId != runId) || captured.Select(x => x.Stream).Distinct().Count() != captured.Length)
        { throw new ArgumentException("The coverage manifest is invalid."); }
        int sum = checked(captured.Sum(x => x.EligibleTraceCount));
        if (totalEligibleTraceCount != sum) { throw new ArgumentException("The total eligible count is inconsistent."); }
        InspectionId = inspectionId; RunId = runId; _streams = Array.AsReadOnly(captured);
        SourceFinalRevision = sourceFinalRevision; PlanId = planId; PlanRevision = planRevision;
        PlanSignature = planSignature; SchemaVersion = schemaVersion; AlgorithmVersion = algorithmVersion;
        TotalEligibleTraceCount = totalEligibleTraceCount; CapturedStoreRevision = capturedStoreRevision;
    }
    public InspectionId InspectionId { get; }
    public AcquisitionRunId RunId { get; }
    public IReadOnlyList<GateDerivedStreamCoverage> Streams => _streams;
    public InspectionRevision SourceFinalRevision { get; }
    public GatePlanId PlanId { get; }
    public GatePlanRevision PlanRevision { get; }
    public string PlanSignature { get; }
    public GateSchemaVersion SchemaVersion { get; }
    public GateAlgorithmVersion AlgorithmVersion { get; }
    public int TotalEligibleTraceCount { get; }
    public DerivedStoreRevision CapturedStoreRevision { get; }
    private static bool IsSha256Hex(string value) => value is { Length: 64 } && value.All(Uri.IsHexDigit);
}

public sealed class ActiveDerivedViewDescriptor
{
    public ActiveDerivedViewDescriptor(DerivedViewId viewId, DerivedStoreRevision activationRevision,
        GateDerivedCoverageManifest manifest, ulong logicalActivationOrder)
    {
        if (!viewId.IsValid || !activationRevision.IsValid || logicalActivationOrder == 0)
        { throw new ArgumentException("A valid view identity, revision, and order are required."); }
        ViewId = viewId; ActivationRevision = activationRevision;
        Manifest = manifest ?? throw new ArgumentNullException(nameof(manifest));
        LogicalActivationOrder = logicalActivationOrder;
    }
    public DerivedViewId ViewId { get; }
    public DerivedStoreRevision ActivationRevision { get; }
    public GateDerivedCoverageManifest Manifest { get; }
    public ulong LogicalActivationOrder { get; }
}

public readonly struct GateDerivedMutationResult
{
    public GateDerivedMutationResult(GateDerivedMutationStatus status, DerivedStoreRevision revision,
        string? errorCode = null)
    {
        if (!Enum.IsDefined(status) || status == GateDerivedMutationStatus.Unknown)
            throw new ArgumentOutOfRangeException(nameof(status));
        if (errorCode is { Length: > 128 }) throw new ArgumentOutOfRangeException(nameof(errorCode));
        Status = status; Revision = revision; ErrorCode = errorCode;
    }
    public GateDerivedMutationStatus Status { get; }
    public DerivedStoreRevision Revision { get; }
    public string? ErrorCode { get; }
}

public sealed record GateDerivedStoreOptions
{
    public GateDerivedStoreOptions(int maximumBatchCount = 4096, long logicalPayloadBudgetBytes = 16 * 1024 * 1024)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumBatchCount, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(logicalPayloadBudgetBytes, 1);
        MaximumBatchCount = maximumBatchCount; LogicalPayloadBudgetBytes = logicalPayloadBudgetBytes;
    }
    public int MaximumBatchCount { get; }
    public long LogicalPayloadBudgetBytes { get; }
}

public sealed class GateDerivedStoreDiagnostics
{
    public GateDerivedStoreDiagnostics(DerivedStoreRevision revision, int batchCount, long logicalPayloadBytes,
        int reservedBatchCount, long reservedLogicalPayloadBytes, int activeLeases, int admittedWriters,
        long commits, long duplicates, long conflicts,
        long capacityExceeded, long activations, long invalidations, long reclaims, bool admissionClosed, bool closed,
        long writersAdmitted = 0, long writersCompleted = 0, int activeReaders = 0,
        long readersAdmitted = 0, long readersCompleted = 0, int invalidatedBatchCount = 0,
        int reclaimableBatchCount = 0, int manifestCount = 0, int terminalEvidenceCount = 0,
        int activeViewCount = 0, int inactiveViewCount = 0, int invalidatedViewCount = 0,
        int tombstoneCount = 0, int indexCount = 0, int membershipCount = 0,
        long invalidatedLogicalBytes = 0, long reclaimableLogicalBytes = 0, string? lastErrorCode = null)
    {
        if (batchCount < 0 || logicalPayloadBytes < 0 || reservedBatchCount < 0 ||
            reservedLogicalPayloadBytes < 0 || activeLeases < 0 || admittedWriters < 0 ||
            commits < 0 || duplicates < 0 || conflicts < 0 || capacityExceeded < 0 || activations < 0 ||
            invalidations < 0 || reclaims < 0 || writersAdmitted < 0 || writersCompleted < 0 ||
            activeReaders < 0 || readersAdmitted < 0 || readersCompleted < 0 || invalidatedBatchCount < 0 ||
            reclaimableBatchCount < 0 || manifestCount < 0 || terminalEvidenceCount < 0 || activeViewCount < 0 ||
            inactiveViewCount < 0 || invalidatedViewCount < 0 || tombstoneCount < 0 || indexCount < 0 ||
            membershipCount < 0 || invalidatedLogicalBytes < 0 || reclaimableLogicalBytes < 0 ||
            writersCompleted > writersAdmitted || readersCompleted > readersAdmitted ||
            writersAdmitted - writersCompleted != admittedWriters || readersAdmitted - readersCompleted != activeReaders ||
            lastErrorCode is { Length: > 128 })
            throw new ArgumentOutOfRangeException(nameof(batchCount), "Derived-store diagnostics are inconsistent.");
        Revision = revision; BatchCount = batchCount; LogicalPayloadBytes = logicalPayloadBytes;
        ReservedBatchCount = reservedBatchCount; ReservedLogicalPayloadBytes = reservedLogicalPayloadBytes;
        ActiveLeases = activeLeases;
        AdmittedWriters = admittedWriters; Commits = commits; Duplicates = duplicates; Conflicts = conflicts;
        CapacityExceeded = capacityExceeded; Activations = activations; Invalidations = invalidations;
        Reclaims = reclaims; AdmissionClosed = admissionClosed; Closed = closed;
        WritersAdmitted = writersAdmitted; WritersCompleted = writersCompleted; ActiveReaders = activeReaders;
        ReadersAdmitted = readersAdmitted; ReadersCompleted = readersCompleted;
        InvalidatedBatchCount = invalidatedBatchCount; ReclaimableBatchCount = reclaimableBatchCount;
        ManifestCount = manifestCount; TerminalEvidenceCount = terminalEvidenceCount;
        ActiveViewCount = activeViewCount; InactiveViewCount = inactiveViewCount;
        InvalidatedViewCount = invalidatedViewCount; TombstoneCount = tombstoneCount;
        IndexCount = indexCount; MembershipCount = membershipCount;
        InvalidatedLogicalBytes = invalidatedLogicalBytes; ReclaimableLogicalBytes = reclaimableLogicalBytes;
        LastErrorCode = lastErrorCode;
    }
    public DerivedStoreRevision Revision { get; }
    public int BatchCount { get; }
    public long LogicalPayloadBytes { get; }
    public int ReservedBatchCount { get; }
    public long ReservedLogicalPayloadBytes { get; }
    public int ActiveLeases { get; }
    public int AdmittedWriters { get; }
    public long Commits { get; }
    public long Duplicates { get; }
    public long Conflicts { get; }
    public long CapacityExceeded { get; }
    public long Activations { get; }
    public long Invalidations { get; }
    public long Reclaims { get; }
    public bool AdmissionClosed { get; }
    public bool Closed { get; }
    public long WritersAdmitted { get; }
    public long WritersCompleted { get; }
    public int ActiveReaders { get; }
    public long ReadersAdmitted { get; }
    public long ReadersCompleted { get; }
    public int InvalidatedBatchCount { get; }
    public int ReclaimableBatchCount { get; }
    public int ManifestCount { get; }
    public int TerminalEvidenceCount { get; }
    public int ActiveViewCount { get; }
    public int InactiveViewCount { get; }
    public int InvalidatedViewCount { get; }
    public int TombstoneCount { get; }
    public int IndexCount { get; }
    public int MembershipCount { get; }
    public long InvalidatedLogicalBytes { get; }
    public long ReclaimableLogicalBytes { get; }
    public string? LastErrorCode { get; }
}

public sealed class GateDerivedRangeQuery
{
    public GateDerivedRangeQuery(DerivedViewId viewId, UtStreamIdentity stream, ulong firstAcquisitionIndex,
        ulong lastAcquisitionIndex, GateId gateId, IReadOnlyList<GateMeasureKind> measures, int pageSize,
        GateDerivedNoDataMode noDataMode, string? cursor = null)
    {
        ArgumentNullException.ThrowIfNull(measures);
        GateMeasureKind[] captured = measures.ToArray();
        if (!viewId.IsValid || !stream.IsValid || firstAcquisitionIndex > lastAcquisitionIndex ||
            !gateId.IsValid || captured.Length == 0 || captured.Any(x => !Enum.IsDefined(x) || x == GateMeasureKind.Unknown) ||
            captured.Distinct().Count() != captured.Length || pageSize is < 1 or > 256 ||
            !Enum.IsDefined(noDataMode) || noDataMode == GateDerivedNoDataMode.Unknown)
        { throw new ArgumentException("The derived range query is invalid."); }
        ViewId = viewId; Stream = stream; FirstAcquisitionIndex = firstAcquisitionIndex;
        LastAcquisitionIndex = lastAcquisitionIndex; GateId = gateId; Measures = Array.AsReadOnly(captured);
        PageSize = pageSize; NoDataMode = noDataMode; Cursor = cursor;
    }
    public DerivedViewId ViewId { get; }
    public UtStreamIdentity Stream { get; }
    public ulong FirstAcquisitionIndex { get; }
    public ulong LastAcquisitionIndex { get; }
    public GateId GateId { get; }
    public IReadOnlyList<GateMeasureKind> Measures { get; }
    public int PageSize { get; }
    public GateDerivedNoDataMode NoDataMode { get; }
    public string? Cursor { get; }
}

public sealed class GateDerivedRangeItem
{
    public GateDerivedRangeItem(TraceIdentity traceIdentity, GateResultStatus? status,
        GateResultQuality? quality, UtTraceProvenance? provenance, GateDiagnosticCode? diagnosticCode,
        GateId? causalGateId, GateResultStatus? causalStatus, GateDerivedNoDataReason? noDataReason,
        IReadOnlyList<GateDerivedMeasureResult>? measures = null)
    {
        if (!traceIdentity.IsValid || (status.HasValue && !Enum.IsDefined(status.Value)) ||
            (quality.HasValue && !Enum.IsDefined(quality.Value)) ||
            (provenance.HasValue && !Enum.IsDefined(provenance.Value)) ||
            (diagnosticCode.HasValue && !Enum.IsDefined(diagnosticCode.Value)) ||
            (causalStatus.HasValue && !Enum.IsDefined(causalStatus.Value)) ||
            (noDataReason.HasValue && (!Enum.IsDefined(noDataReason.Value) || noDataReason == GateDerivedNoDataReason.Unknown)))
            throw new ArgumentOutOfRangeException(nameof(status));
        TraceIdentity = traceIdentity; Status = status; Quality = quality; Provenance = provenance;
        DiagnosticCode = diagnosticCode; CausalGateId = causalGateId; CausalStatus = causalStatus;
        NoDataReason = noDataReason;
        Measures = Array.AsReadOnly((measures ?? Array.Empty<GateDerivedMeasureResult>()).ToArray());
    }
    public TraceIdentity TraceIdentity { get; }
    public GateResultStatus? Status { get; }
    public GateResultQuality? Quality { get; }
    public UtTraceProvenance? Provenance { get; }
    public GateDiagnosticCode? DiagnosticCode { get; }
    public GateId? CausalGateId { get; }
    public GateResultStatus? CausalStatus { get; }
    public GateDerivedNoDataReason? NoDataReason { get; }
    public IReadOnlyList<GateDerivedMeasureResult> Measures { get; }
}

public sealed class GateDerivedMeasureResult
{
    public GateDerivedMeasureResult(GateDerivedLayerKey key, double? value, GateResultStatus status,
        GateResultQuality quality, UtTraceProvenance provenance, GateDerivedNoDataReason? noDataReason,
        GateId? causalGateId, GateResultStatus? causalStatus)
    {
        if (!Enum.IsDefined(status) || !Enum.IsDefined(quality) || !Enum.IsDefined(provenance) ||
            (noDataReason.HasValue && (!Enum.IsDefined(noDataReason.Value) || noDataReason == GateDerivedNoDataReason.Unknown)) ||
            (causalStatus.HasValue && !Enum.IsDefined(causalStatus.Value)))
            throw new ArgumentOutOfRangeException(nameof(status));
        Key = key; Value = value; Status = status; Quality = quality; Provenance = provenance;
        NoDataReason = noDataReason; CausalGateId = causalGateId; CausalStatus = causalStatus;
    }
    public GateDerivedLayerKey Key { get; }
    public double? Value { get; }
    public GateResultStatus Status { get; }
    public GateResultQuality Quality { get; }
    public UtTraceProvenance Provenance { get; }
    public GateDerivedNoDataReason? NoDataReason { get; }
    public GateId? CausalGateId { get; }
    public GateResultStatus? CausalStatus { get; }
}

public sealed class GateDerivedRangePage
{
    public GateDerivedRangePage(IReadOnlyList<GateDerivedRangeItem> items, string? nextCursor)
    { ArgumentNullException.ThrowIfNull(items); Items = Array.AsReadOnly(items.ToArray()); NextCursor = nextCursor; }
    public IReadOnlyList<GateDerivedRangeItem> Items { get; }
    public string? NextCursor { get; }
}

public enum GateDerivedExactReadStatus { Unknown, Available, NotAvailable, Closed, Cancelled, Faulted }
public sealed class GateDerivedExactReadResult
{
    public GateDerivedExactReadResult(GateDerivedExactReadStatus status, GateEvaluationBatch? batch,
        string? errorCode = null)
    {
        if (!Enum.IsDefined(status) || status == GateDerivedExactReadStatus.Unknown)
            throw new ArgumentOutOfRangeException(nameof(status));
        Status = status;
        Batch = batch;
        ErrorCode = errorCode;
    }

    public GateDerivedExactReadStatus Status { get; }
    public GateEvaluationBatch? Batch { get; }
    public string? ErrorCode { get; }
}

public interface IGateDerivedReadLease : IDisposable
{
    DerivedStoreRevision Revision { get; }
    ActiveDerivedViewDescriptor View { get; }
    GateEvaluationBatch? ReadExact(GateDerivedBatchKey key);
    ValueTask<GateDerivedExactReadResult> ReadExactAsync(GateDerivedBatchKey key,
        CancellationToken cancellationToken = default);
    GateDerivedRangePage ReadRange(GateDerivedRangeQuery query);
}

public interface IGateDerivedStore : IGateEvaluationBatchSink, IAsyncDisposable
{
    DerivedStoreRevision Revision { get; }
    ActiveDerivedViewDescriptor? ActiveView { get; }
    GateDerivedStoreDiagnostics Diagnostics { get; }
    ValueTask<GateDerivedMutationResult> CommitAsync(GateEvaluationBatch batch, CancellationToken cancellationToken = default);
    GateDerivedMutationResult Activate(GateDerivedCoverageManifest manifest, DerivedViewId viewId);
    GateDerivedMutationResult Invalidate(DerivedViewId viewId);
    ValueTask<GateDerivedMutationResult> ReclaimInvalidatedAsync(CancellationToken cancellationToken = default);
    IGateDerivedReadLease OpenActiveView();
}

public interface IGateProcessingTerminalCoverageSink
{
    GateProcessingTerminalCoverageStatus ConfirmTerminalCoverage(
        IReadOnlyList<GateProcessingTerminalCoverage> coverage);
}

public enum GateProcessingTerminalCoverageStatus
{ Unknown, Accepted, Duplicate, Conflict, CapacityExceeded, Closed, Faulted }

public sealed class GateProcessingTerminalCoverage
{
    public GateProcessingTerminalCoverage(InspectionId inspectionId, AcquisitionRunId runId,
        InspectionRevision finalRevision, GatePlanId planId, GatePlanRevision planRevision,
        string planSignature, GateSchemaVersion schemaVersion, GateAlgorithmVersion algorithmVersion,
        IReadOnlyList<GateDerivedStreamCoverage> streams)
    {
        ArgumentNullException.ThrowIfNull(planSignature); ArgumentNullException.ThrowIfNull(streams);
        GateDerivedStreamCoverage[] captured = streams.ToArray();
        if (!inspectionId.IsValid || !runId.IsValid || finalRevision == InspectionRevision.Initial ||
            !planId.IsValid || !planRevision.IsValid || planSignature is not { Length: 64 } || !planSignature.All(Uri.IsHexDigit) ||
            !schemaVersion.IsValid || !algorithmVersion.IsValid || captured.Length == 0 ||
            captured.Any(x => x.Stream.InspectionId != inspectionId || x.Stream.AcquisitionRunId != runId) ||
            captured.Select(x => x.Stream).Distinct().Count() != captured.Length)
        { throw new ArgumentException("Terminal processing coverage is invalid."); }
        InspectionId = inspectionId; RunId = runId; FinalRevision = finalRevision; PlanId = planId;
        PlanRevision = planRevision; PlanSignature = planSignature; SchemaVersion = schemaVersion;
        AlgorithmVersion = algorithmVersion; Streams = Array.AsReadOnly(captured);
    }
    public InspectionId InspectionId { get; }
    public AcquisitionRunId RunId { get; }
    public InspectionRevision FinalRevision { get; }
    public GatePlanId PlanId { get; }
    public GatePlanRevision PlanRevision { get; }
    public string PlanSignature { get; }
    public GateSchemaVersion SchemaVersion { get; }
    public GateAlgorithmVersion AlgorithmVersion { get; }
    public IReadOnlyList<GateDerivedStreamCoverage> Streams { get; }
}

public static class GateDerivedLogicalSize
{
    public const ushort FormulaVersion = 2;
    public static long Batch(GateEvaluationBatch batch)
    {
        ArgumentNullException.ThrowIfNull(batch);
        checked
        {
            long bytes = 160 + Identity(batch.Trace.ConfirmedIdentity) + Utf8(batch.Entries[0].PlanId.Value) + 32;
            foreach (GateEvaluationEntry entry in batch.Entries)
            {
                bytes += 176 + Utf8(entry.GateId.Value) + checked(4L * entry.RequestedMeasures.Count) +
                (entry.CausalGateId is { } causal ? Utf8(causal.Value) : 0);
            }
            return bytes;
        }
    }
    public static long Manifest(GateDerivedCoverageManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        checked
        {
            return 128 + Utf8(manifest.PlanId.Value) + Utf8(manifest.PlanSignature) +
            manifest.Streams.Sum(x => 144L + Utf8(x.Stream.SourceId.Value));
        }
    }
    public static long TerminalCoverage(GateProcessingTerminalCoverage coverage)
    {
        ArgumentNullException.ThrowIfNull(coverage);
        checked
        {
            return 128 + Utf8(coverage.PlanId.Value) + Utf8(coverage.PlanSignature) +
            coverage.Streams.Sum(x => 144L + Utf8(x.Stream.SourceId.Value));
        }
    }
    public static long View(ActiveDerivedViewDescriptor view) => checked(64 + Utf8(view.ViewId.Value));
    public static long Membership(int keyCount) => checked(32L + 96L * keyCount);
    private static int Identity(TraceIdentity value) => checked(64 + Utf8(value.Stream.SourceId.Value));
    private static int Utf8(string value) => Encoding.UTF8.GetByteCount(value);
}
