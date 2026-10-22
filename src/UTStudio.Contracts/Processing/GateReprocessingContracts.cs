using System.Collections.ObjectModel;
using UTStudio.Contracts.Inspection;
using UTStudio.Domain.Acquisition;
using UTStudio.Domain.Inspection;
using UTStudio.Domain.Processing;

namespace UTStudio.Contracts.Processing;

public enum GateReprocessingPublicationPolicy { Unknown = 0, StoreOnly = 1, ActivateOnComplete = 2 }
public enum GateReprocessingLifecycle
{
    Unknown = 0, Created = 1, Admitted = 2, Running = 3, Cancelling = 4,
    Cancelled = 5, Completing = 6, Completed = 7, Faulted = 8
}
public enum GateReprocessingAdmissionStatus
{ Unknown = 0, Admitted = 1, CapacityExceeded = 2, Cancelled = 3, Closed = 4, Rejected = 5, Faulted = 6 }

public readonly record struct AcquisitionIndexRange
{
    public AcquisitionIndexRange(ulong firstInclusive, ulong lastInclusive)
    {
        if (firstInclusive > lastInclusive) throw new ArgumentException("The inclusive range is empty or inverted.");
        FirstInclusive = firstInclusive; LastInclusive = lastInclusive;
    }
    public ulong FirstInclusive { get; }
    public ulong LastInclusive { get; }
    public bool Contains(ulong value) => value >= FirstInclusive && value <= LastInclusive;
}

public sealed class GateReprocessingStreamSelection
{
    public GateReprocessingStreamSelection(UtStreamIdentity stream, IEnumerable<AcquisitionIndexRange> ranges)
    {
        ArgumentNullException.ThrowIfNull(ranges);
        if (!stream.IsValid) throw new ArgumentException("A valid stream is required.", nameof(stream));
        Stream = stream; Ranges = Array.AsReadOnly(Normalize(ranges).ToArray());
        if (Ranges.Count == 0) throw new ArgumentException("At least one acquisition-index range is required.", nameof(ranges));
        using var payload = new MemoryStream();
        using (var writer = new BinaryWriter(payload, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            writer.Write((ushort)1); writer.Write(Ranges.Count);
            foreach (AcquisitionIndexRange range in Ranges)
            { writer.Write(range.FirstInclusive); writer.Write(range.LastInclusive); }
        }
        RangeDigest = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            payload.GetBuffer().AsSpan(0, checked((int)payload.Length))));
    }
    public UtStreamIdentity Stream { get; }
    public IReadOnlyList<AcquisitionIndexRange> Ranges { get; }
    public string RangeDigest { get; }
    public bool Contains(ulong value) => Ranges.Any(x => x.Contains(value));

    private static IEnumerable<AcquisitionIndexRange> Normalize(IEnumerable<AcquisitionIndexRange> ranges)
    {
        AcquisitionIndexRange[] ordered = ranges.Distinct().OrderBy(x => x.FirstInclusive)
            .ThenBy(x => x.LastInclusive).ToArray();
        if (ordered.Length == 0) yield break;
        ulong first = ordered[0].FirstInclusive, last = ordered[0].LastInclusive;
        for (int index = 1; index < ordered.Length; index++)
        {
            AcquisitionIndexRange next = ordered[index];
            bool adjacent = last != ulong.MaxValue && next.FirstInclusive == last + 1;
            if (next.FirstInclusive <= last || adjacent) { last = Math.Max(last, next.LastInclusive); continue; }
            yield return new(first, last); first = next.FirstInclusive; last = next.LastInclusive;
        }
        yield return new(first, last);
    }
}

public sealed class GateReprocessingJobDescriptor
{
    private readonly ReadOnlyCollection<GateReprocessingStreamSelection> _selection;
    public GateReprocessingJobDescriptor(GateReprocessingJobId jobId, GateReprocessingAttemptId attemptId,
        InspectionId inspectionId, AcquisitionRunId runId, InspectionRevision sourceFinalRevision,
        IEnumerable<GateReprocessingStreamSelection> selection, CompiledGatePlan plan, int blockSize,
        GateReprocessingPublicationPolicy publicationPolicy, DerivedViewId? derivedViewId = null,
        int snapshotPageSize = 256)
    {
        ArgumentNullException.ThrowIfNull(selection); ArgumentNullException.ThrowIfNull(plan);
        GateReprocessingStreamSelection[] captured = selection.OrderBy(x =>
            InspectionSealedRunSnapshotRequest.StreamSortKey(x.Stream), StringComparer.Ordinal).ToArray();
        if (!jobId.IsValid || !attemptId.IsValid || !inspectionId.IsValid || !runId.IsValid ||
            sourceFinalRevision == InspectionRevision.Initial || captured.Length == 0 ||
            captured.Any(x => x.Stream.InspectionId != inspectionId || x.Stream.AcquisitionRunId != runId) ||
            captured.Select(x => x.Stream).Distinct().Count() != captured.Length || blockSize is < 1 or > 4096 ||
            snapshotPageSize is < 1 or > 256 || !Enum.IsDefined(publicationPolicy) ||
            publicationPolicy == GateReprocessingPublicationPolicy.Unknown ||
            (publicationPolicy == GateReprocessingPublicationPolicy.ActivateOnComplete) != derivedViewId.HasValue ||
            derivedViewId is { IsValid: false })
        { throw new ArgumentException("The gate reprocessing job descriptor is invalid."); }
        JobId = jobId; AttemptId = attemptId; InspectionId = inspectionId; RunId = runId;
        SourceFinalRevision = sourceFinalRevision; _selection = Array.AsReadOnly(captured); Plan = plan;
        BlockSize = blockSize; SnapshotPageSize = snapshotPageSize; PublicationPolicy = publicationPolicy;
        DerivedViewId = derivedViewId;
    }
    public GateReprocessingJobId JobId { get; }
    public GateReprocessingAttemptId AttemptId { get; }
    public InspectionId InspectionId { get; }
    public AcquisitionRunId RunId { get; }
    public InspectionRevision SourceFinalRevision { get; }
    public IReadOnlyList<GateReprocessingStreamSelection> Selection => _selection;
    public CompiledGatePlan Plan { get; }
    public int BlockSize { get; }
    public int SnapshotPageSize { get; }
    public GateReprocessingPublicationPolicy PublicationPolicy { get; }
    public DerivedViewId? DerivedViewId { get; }
}

public sealed class GateReprocessingProgressSnapshot
{
    public GateReprocessingProgressSnapshot(GateReprocessingJobId jobId, GateReprocessingAttemptId attemptId,
        GateReprocessingLifecycle lifecycle, InspectionRevision sourceFinalRevision, GatePlanRevision planRevision,
        GateReprocessingPublicationPolicy publicationPolicy, long selected, long read, long evaluated,
        long newlyCommitted, long reused, long evaluatedNotPublished, int blocksTotal, int blocksCompleted,
        int activeLeases, int activeScratch, string? primaryError, IReadOnlyList<string>? secondaryErrors,
        DateTimeOffset createdUtc, DateTimeOffset updatedUtc,
        IReadOnlyList<GateReprocessingStreamSelection>? requestedCoverage = null,
        long pagesRead = 0, long continuationSearches = 0, long identitiesVisited = 0,
        long cancelledBeforeAdmission = 0, int peakLeases = 0,
        int activeBuffers = 0, int peakBuffers = 0, long reservedBufferBytes = 0,
        long ownedBufferBytes = 0, long peakOwnedBufferBytes = 0, long bufferCapacityBytes = 0,
        long bufferRents = 0, long bufferReturns = 0, long scratchRents = 0, long scratchReturns = 0,
        int peakScratch = 0, int activeWorkers = 0, int peakWorkers = 0,
        int activeCallbacks = 0, int peakCallbacks = 0, long leasesAcquired = 0,
        long leasesReleased = 0, long entriesReturned = 0, long binarySearchComparisons = 0,
        long normalizedRangeCount = 0, long lowerBoundSearches = 0, long upperBoundSearches = 0,
        long cursorResumes = 0, long prefixRescans = 0, long retainedBufferBytes = 0,
        bool cleanupConfirmed = true, long leaseReleaseFailed = 0,
        long bufferReturnFailed = 0, long scratchReturnFailed = 0,
        long snapshotReleaseFailed = 0,
        GateReprocessingPlanEpoch? planEpoch = null,
        IReadOnlyList<GateReprocessingStreamProgress>? streamProgress = null,
        GateReprocessingOperationProgress? commitProgress = null,
        GateReprocessingOperationProgress? coverageProgress = null,
        GateReprocessingOperationProgress? activationProgress = null,
        GateReprocessingResourceProgress? bufferProgress = null,
        GateReprocessingResourceProgress? scratchProgress = null,
        GateReprocessingResourceProgress? leaseProgress = null,
        GateStoreOnlyCandidateDescriptor? storeOnlyCandidate = null)
    {
        bool terminal = lifecycle is GateReprocessingLifecycle.Completed or
            GateReprocessingLifecycle.Cancelled or GateReprocessingLifecycle.Faulted;
        if (!jobId.IsValid || !attemptId.IsValid || !Enum.IsDefined(lifecycle) ||
            lifecycle == GateReprocessingLifecycle.Unknown || sourceFinalRevision == InspectionRevision.Initial ||
            !planRevision.IsValid || publicationPolicy == GateReprocessingPublicationPolicy.Unknown ||
            selected < 0 || read < 0 || evaluated < 0 || newlyCommitted < 0 || reused < 0 ||
            evaluatedNotPublished < 0 || blocksTotal < 0 || blocksCompleted < 0 || blocksCompleted > blocksTotal ||
            activeLeases < 0 || activeScratch < 0 || pagesRead < 0 || continuationSearches < 0 ||
            identitiesVisited < 0 || cancelledBeforeAdmission < 0 || peakLeases < activeLeases ||
            activeBuffers < 0 || peakBuffers < activeBuffers || reservedBufferBytes < 0 || ownedBufferBytes < 0 ||
            peakOwnedBufferBytes < ownedBufferBytes || bufferCapacityBytes < 0 ||
            bufferCapacityBytes != 0 && reservedBufferBytes + ownedBufferBytes > bufferCapacityBytes || updatedUtc < createdUtc ||
            read > selected || evaluated > read || newlyCommitted + reused > evaluated ||
            bufferRents < bufferReturns || scratchRents < scratchReturns || peakScratch < activeScratch ||
            activeWorkers < 0 || peakWorkers < activeWorkers || activeCallbacks < 0 || peakCallbacks < activeCallbacks ||
            leasesAcquired < leasesReleased || entriesReturned < 0 || binarySearchComparisons < 0 ||
            normalizedRangeCount < 0 || lowerBoundSearches < 0 || upperBoundSearches < 0 ||
            cursorResumes < 0 || prefixRescans < 0 || retainedBufferBytes < 0 ||
            bufferCapacityBytes != 0 && retainedBufferBytes > bufferCapacityBytes || leaseReleaseFailed < 0 ||
            bufferReturnFailed < 0 || scratchReturnFailed < 0 ||
            snapshotReleaseFailed < 0 ||
            cleanupConfirmed != (leaseReleaseFailed == 0 && bufferReturnFailed == 0 &&
                scratchReturnFailed == 0 && snapshotReleaseFailed == 0) ||
            terminal && cleanupConfirmed && (activeLeases != 0 || activeScratch != 0 || activeBuffers != 0 ||
                activeWorkers != 0 || activeCallbacks != 0 || bufferRents != bufferReturns ||
                scratchRents != scratchReturns || leasesAcquired != leasesReleased) ||
            lifecycle == GateReprocessingLifecycle.Completed && primaryError is not null ||
            lifecycle == GateReprocessingLifecycle.Faulted && string.IsNullOrWhiteSpace(primaryError))
            throw new ArgumentException("The gate-reprocessing progress snapshot is inconsistent.");
        GateReprocessingStreamSelection[] coverage = (requestedCoverage ?? []).ToArray();
        if (coverage.Select(x => x.Stream).Distinct().Count() != coverage.Length)
            throw new ArgumentException("Requested stream coverage must be unique.", nameof(requestedCoverage));
        JobId = jobId; AttemptId = attemptId; Lifecycle = lifecycle; SourceFinalRevision = sourceFinalRevision;
        PlanRevision = planRevision; PublicationPolicy = publicationPolicy; SelectedTraceCount = selected;
        ReadTraceCount = read; EvaluatedTraceCount = evaluated; NewlyCommittedBatchCount = newlyCommitted;
        ReusedBatchCount = reused; EvaluatedNotPublishedCount = evaluatedNotPublished;
        BlocksTotal = blocksTotal; BlocksCompleted = blocksCompleted; ActiveLeases = activeLeases;
        ActiveScratch = activeScratch; PrimaryError = primaryError;
        SecondaryErrors = Array.AsReadOnly((secondaryErrors ?? []).ToArray());
        CreatedUtc = createdUtc; UpdatedUtc = updatedUtc;
        RequestedCoverage = Array.AsReadOnly(coverage); PagesRead = pagesRead;
        ContinuationSearches = continuationSearches; IdentitiesVisited = identitiesVisited;
        CancelledBeforeAdmission = cancelledBeforeAdmission; PeakLeases = peakLeases;
        ActiveBuffers = activeBuffers; PeakBuffers = peakBuffers; ReservedBufferBytes = reservedBufferBytes;
        OwnedBufferBytes = ownedBufferBytes; PeakOwnedBufferBytes = peakOwnedBufferBytes;
        BufferCapacityBytes = bufferCapacityBytes;
        BufferRents = bufferRents; BufferReturns = bufferReturns;
        ScratchRents = scratchRents; ScratchReturns = scratchReturns; PeakScratch = peakScratch;
        ActiveWorkers = activeWorkers; PeakWorkers = peakWorkers;
        ActiveCallbacks = activeCallbacks; PeakCallbacks = peakCallbacks;
        LeasesAcquired = leasesAcquired; LeasesReleased = leasesReleased;
        EntriesReturned = entriesReturned; BinarySearchComparisons = binarySearchComparisons;
        NormalizedRangeCount = normalizedRangeCount; LowerBoundSearches = lowerBoundSearches;
        UpperBoundSearches = upperBoundSearches; CursorResumes = cursorResumes; PrefixRescans = prefixRescans;
        RetainedBufferBytes = retainedBufferBytes;
        CleanupConfirmed = cleanupConfirmed; LeaseReleaseFailed = leaseReleaseFailed;
        BufferReturnFailed = bufferReturnFailed; ScratchReturnFailed = scratchReturnFailed;
        SnapshotReleaseFailed = snapshotReleaseFailed;
        PlanEpoch = planEpoch;
        StreamProgress = Array.AsReadOnly((streamProgress ?? []).ToArray());
        CommitProgress = commitProgress; CoverageProgress = coverageProgress;
        ActivationProgress = activationProgress; BufferProgress = bufferProgress;
        ScratchProgress = scratchProgress; LeaseProgress = leaseProgress;
        if (PlanEpoch is not null && PlanEpoch.PlanRevision != PlanRevision)
            throw new ArgumentException("Plan epoch revision must match the progress revision.", nameof(planEpoch));
        if (StreamProgress.Count != 0)
        {
            if (StreamProgress.Count != RequestedCoverage.Count ||
                StreamProgress.Where((item, index) => item.Stream != RequestedCoverage[index].Stream).Any() ||
                StreamProgress.Sum(x => x.Selected) != SelectedTraceCount ||
                StreamProgress.Sum(x => x.Visited) != IdentitiesVisited ||
                StreamProgress.Sum(x => x.Read) != ReadTraceCount ||
                StreamProgress.Sum(x => x.Evaluated) != EvaluatedTraceCount)
                throw new ArgumentException("Per-stream progress is inconsistent.", nameof(streamProgress));
        }
        if (terminal && new[] { CommitProgress, CoverageProgress, ActivationProgress }
            .Where(x => x is not null).Any(x => x!.Active != 0))
            throw new ArgumentException("Terminal operation progress cannot retain active work.");
        if (storeOnlyCandidate is not null)
        {
            GateDerivedCoverageManifest manifest = storeOnlyCandidate.Manifest;
            if (lifecycle != GateReprocessingLifecycle.Completed ||
                publicationPolicy != GateReprocessingPublicationPolicy.StoreOnly ||
                storeOnlyCandidate.OwnershipState != GateCandidateOwnershipState.Available ||
                manifest.SourceFinalRevision != sourceFinalRevision || manifest.PlanRevision != planRevision ||
                manifest.TotalEligibleTraceCount != selected ||
                planEpoch is null || manifest.PlanId != planEpoch.PlanId ||
                !string.Equals(manifest.PlanSignature, planEpoch.Signature.Hex, StringComparison.Ordinal) ||
                manifest.SchemaVersion != planEpoch.SchemaVersion || manifest.AlgorithmVersion != planEpoch.AlgorithmVersion)
                throw new ArgumentException("The StoreOnly terminal receipt is inconsistent.", nameof(storeOnlyCandidate));
        }
        StoreOnlyCandidate = storeOnlyCandidate;
    }
    public GateReprocessingJobId JobId { get; }
    public GateReprocessingAttemptId AttemptId { get; }
    public GateReprocessingLifecycle Lifecycle { get; }
    public InspectionRevision SourceFinalRevision { get; }
    public GatePlanRevision PlanRevision { get; }
    public GateReprocessingPublicationPolicy PublicationPolicy { get; }
    public long SelectedTraceCount { get; }
    public long ReadTraceCount { get; }
    public long EvaluatedTraceCount { get; }
    public long NewlyCommittedBatchCount { get; }
    public long ReusedBatchCount { get; }
    public long EvaluatedNotPublishedCount { get; }
    public int BlocksTotal { get; }
    public int BlocksCompleted { get; }
    public int ActiveLeases { get; }
    public int ActiveScratch { get; }
    public string? PrimaryError { get; }
    public IReadOnlyList<string> SecondaryErrors { get; }
    public DateTimeOffset CreatedUtc { get; }
    public DateTimeOffset UpdatedUtc { get; }
    public IReadOnlyList<GateReprocessingStreamSelection> RequestedCoverage { get; }
    public long PagesRead { get; }
    public long ContinuationSearches { get; }
    public long IdentitiesVisited { get; }
    public long CancelledBeforeAdmission { get; }
    public int PeakLeases { get; }
    public int ActiveBuffers { get; }
    public int PeakBuffers { get; }
    public long ReservedBufferBytes { get; }
    public long OwnedBufferBytes { get; }
    public long PeakOwnedBufferBytes { get; }
    public long BufferCapacityBytes { get; }
    public long BufferRents { get; }
    public long BufferReturns { get; }
    public long ScratchRents { get; }
    public long ScratchReturns { get; }
    public int PeakScratch { get; }
    public int ActiveWorkers { get; }
    public int PeakWorkers { get; }
    public int ActiveCallbacks { get; }
    public int PeakCallbacks { get; }
    public long LeasesAcquired { get; }
    public long LeasesReleased { get; }
    public long EntriesReturned { get; }
    public long BinarySearchComparisons { get; }
    public long NormalizedRangeCount { get; }
    public long LowerBoundSearches { get; }
    public long UpperBoundSearches { get; }
    public long CursorResumes { get; }
    public long PrefixRescans { get; }
    public long RetainedBufferBytes { get; }
    public bool CleanupConfirmed { get; }
    public long LeaseReleaseFailed { get; }
    public long BufferReturnFailed { get; }
    public long ScratchReturnFailed { get; }
    public long SnapshotReleaseFailed { get; }
    public GateReprocessingPlanEpoch? PlanEpoch { get; }
    public IReadOnlyList<GateReprocessingStreamProgress> StreamProgress { get; }
    public GateReprocessingOperationProgress? CommitProgress { get; }
    public GateReprocessingOperationProgress? CoverageProgress { get; }
    public GateReprocessingOperationProgress? ActivationProgress { get; }
    public GateReprocessingResourceProgress? BufferProgress { get; }
    public GateReprocessingResourceProgress? ScratchProgress { get; }
    public GateReprocessingResourceProgress? LeaseProgress { get; }
    public GateStoreOnlyCandidateDescriptor? StoreOnlyCandidate { get; }
}

public sealed record GateReprocessingPlanEpoch
{
    public GateReprocessingPlanEpoch(GatePlanId planId, GatePlanRevision planRevision,
        GatePlanSignature signature, GateSchemaVersion schemaVersion, GateAlgorithmVersion algorithmVersion)
    {
        if (!planId.IsValid || !planRevision.IsValid || !schemaVersion.IsValid || !algorithmVersion.IsValid)
            throw new ArgumentException("A valid reproducible plan epoch is required.");
        ArgumentNullException.ThrowIfNull(signature);
        PlanId = planId; PlanRevision = planRevision; Signature = signature;
        SchemaVersion = schemaVersion; AlgorithmVersion = algorithmVersion;
    }
    public GatePlanId PlanId { get; }
    public GatePlanRevision PlanRevision { get; }
    public GatePlanSignature Signature { get; }
    public GateSchemaVersion SchemaVersion { get; }
    public GateAlgorithmVersion AlgorithmVersion { get; }
}

public sealed record GateReprocessingStreamProgress
{
    public GateReprocessingStreamProgress(UtStreamIdentity stream, long selected, long visited,
        long read, long evaluated, long terminal, ulong? lastVisitedAcquisitionIndex)
    {
        if (!stream.IsValid || selected < 0 || visited < 0 || read < 0 || evaluated < 0 || terminal < 0 ||
            visited > selected || read > visited || evaluated > read || terminal > visited ||
            (visited == 0) != !lastVisitedAcquisitionIndex.HasValue)
            throw new ArgumentException("Per-stream progress is inconsistent.");
        Stream = stream; Selected = selected; Visited = visited; Read = read;
        Evaluated = evaluated; Terminal = terminal; LastVisitedAcquisitionIndex = lastVisitedAcquisitionIndex;
    }
    public UtStreamIdentity Stream { get; }
    public long Selected { get; }
    public long Visited { get; }
    public long Read { get; }
    public long Evaluated { get; }
    public long Terminal { get; }
    public ulong? LastVisitedAcquisitionIndex { get; }
}

public sealed record GateReprocessingOperationProgress
{
    public GateReprocessingOperationProgress(long attempts, long admitted, long completed,
        long rejectedBeforeAdmission, long succeeded, long duplicates, long failed)
    {
        if (attempts < 0 || admitted < 0 || completed < 0 || rejectedBeforeAdmission < 0 ||
            succeeded < 0 || duplicates < 0 || failed < 0 || admitted > attempts || completed > admitted ||
            rejectedBeforeAdmission > attempts - admitted || succeeded + duplicates + failed > completed)
            throw new ArgumentException("Operation progress is inconsistent.");
        Attempts = attempts; Admitted = admitted; Completed = completed;
        RejectedBeforeAdmission = rejectedBeforeAdmission; Succeeded = succeeded;
        Duplicates = duplicates; Failed = failed;
    }
    public long Attempts { get; }
    public long Admitted { get; }
    public long Completed { get; }
    public long RejectedBeforeAdmission { get; }
    public long Succeeded { get; }
    public long Duplicates { get; }
    public long Failed { get; }
    public long Active => Admitted - Completed;
}

public sealed record GateReprocessingResourceProgress
{
    public GateReprocessingResourceProgress(long acquireAttempts, long acquired, long rejected,
        long releaseAttempts, long released, long releaseFailed, int active, int peakActive,
        long ownedCapacity = 0, long peakOwnedCapacity = 0)
    {
        if (acquireAttempts < 0 || acquired < 0 || rejected < 0 || releaseAttempts < 0 || released < 0 ||
            releaseFailed < 0 || active < 0 || peakActive < active || ownedCapacity < 0 ||
            peakOwnedCapacity < ownedCapacity || acquired + rejected != acquireAttempts ||
            released + releaseFailed != releaseAttempts || acquired - released != active)
            throw new ArgumentException("Resource progress is inconsistent.");
        AcquireAttempts = acquireAttempts; Acquired = acquired; Rejected = rejected;
        ReleaseAttempts = releaseAttempts; Released = released; ReleaseFailed = releaseFailed;
        Active = active; PeakActive = peakActive; OwnedCapacity = ownedCapacity;
        PeakOwnedCapacity = peakOwnedCapacity;
    }
    public long AcquireAttempts { get; }
    public long Acquired { get; }
    public long Rejected { get; }
    public long ReleaseAttempts { get; }
    public long Released { get; }
    public long ReleaseFailed { get; }
    public int Active { get; }
    public int PeakActive { get; }
    public long OwnedCapacity { get; }
    public long PeakOwnedCapacity { get; }
}

public interface IGateReprocessingJob
{
    GateReprocessingProgressSnapshot Progress { get; }
    void Cancel();
    Task<GateReprocessingProgressSnapshot> Completion { get; }
}

public readonly struct GateReprocessingAdmissionResult
{
    private readonly IReadOnlyList<string>? _secondaryErrors;
    public GateReprocessingAdmissionResult(GateReprocessingAdmissionStatus status,
        IGateReprocessingJob? job = null, string? errorCode = null,
        IReadOnlyList<string>? secondaryErrors = null)
    {
        if (!Enum.IsDefined(status) || status == GateReprocessingAdmissionStatus.Unknown)
            throw new ArgumentOutOfRangeException(nameof(status));
        bool admitted = status == GateReprocessingAdmissionStatus.Admitted;
        if (admitted != (job is not null))
            throw new ArgumentException("Only an admitted result can carry a job.", nameof(job));
        bool requiresError = status is GateReprocessingAdmissionStatus.CapacityExceeded or
            GateReprocessingAdmissionStatus.Closed or GateReprocessingAdmissionStatus.Rejected or
            GateReprocessingAdmissionStatus.Faulted;
        if (admitted || status == GateReprocessingAdmissionStatus.Cancelled)
        {
            if (errorCode is not null) throw new ArgumentException("This status cannot carry an error code.", nameof(errorCode));
        }
        else if (requiresError && string.IsNullOrWhiteSpace(errorCode))
            throw new ArgumentException("This status requires an error code.", nameof(errorCode));
        string[] errors = secondaryErrors?.ToArray() ?? [];
        if (errors.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException("Secondary errors cannot be empty.", nameof(secondaryErrors));
        Status = status;
        Job = job;
        ErrorCode = errorCode;
        _secondaryErrors = Array.AsReadOnly(errors);
    }
    public GateReprocessingAdmissionStatus Status { get; }
    public IGateReprocessingJob? Job { get; }
    public string? ErrorCode { get; }
    public IReadOnlyList<string> SecondaryErrors => _secondaryErrors ?? Array.Empty<string>();
}

public sealed record GateReprocessingSchedulerOptions
{
    public GateReprocessingSchedulerOptions(int maximumWorkers = 1, int maximumAdmittedJobs = 8,
        long sampleBufferBudgetBytes = 131_072)
    {
        if (maximumWorkers is < 1 or > 16 || maximumAdmittedJobs is < 1 or > 1024 ||
            sampleBufferBudgetBytes < checked((long)maximumWorkers * 131_072))
        { throw new ArgumentOutOfRangeException(nameof(maximumWorkers)); }
        MaximumWorkers = maximumWorkers; MaximumAdmittedJobs = maximumAdmittedJobs;
        SampleBufferBudgetBytes = sampleBufferBudgetBytes;
    }
    public int MaximumWorkers { get; }
    public int MaximumAdmittedJobs { get; }
    public long SampleBufferBudgetBytes { get; }
}

public interface IGateReprocessingScheduler : IAsyncDisposable
{
    ValueTask<GateReprocessingAdmissionResult> AdmitAsync(GateReprocessingJobDescriptor descriptor,
        CancellationToken cancellationToken = default);
    Task StopAsync(CancellationToken cancellationToken = default);
}
