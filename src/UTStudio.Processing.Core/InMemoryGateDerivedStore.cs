using System.Collections.ObjectModel;
using UTStudio.Contracts.Inspection;
using UTStudio.Contracts.Processing;
using UTStudio.Domain.Acquisition;
using UTStudio.Domain.Inspection;
using UTStudio.Domain.Processing;

namespace UTStudio.Processing.Core;

/// <summary>Bounded, reconstructible, non-authoritative RAM store for UT gate results.</summary>
public sealed class InMemoryGateDerivedStore : IGateDerivedStore, IGateProcessingTerminalCoverageSink
{
    private readonly object _gate = new();
    private readonly SemaphoreSlim _commitSerial = new(1, 1);
    private readonly GateDerivedStoreOptions _options;
    private Dictionary<GateDerivedBatchKey, StoredBatch> _batches = [];
    private Dictionary<DerivedViewId, StoredView> _views = [];
    private HashSet<DerivedViewId> _invalidated = [];
    private Dictionary<(InspectionId, AcquisitionRunId, GatePlanId, GatePlanRevision, string, GateSchemaVersion, GateAlgorithmVersion, string),
        GateProcessingTerminalCoverage> _terminalCoverage = [];
    private HashSet<DerivedViewId> _usedViewIds = [];
    private ulong _revision, _activationOrder;
    private long _nextOperationId;
    private long _reservedBytes;
    private int _reservedBatches;
    private long _logicalBytes, _commits, _duplicates, _conflicts, _capacityExceeded,
        _activations, _invalidations, _reclaims;
    private int _writers, _readers, _leases;
    private long _writersAdmitted, _writersCompleted, _readersAdmitted, _readersCompleted;
    private int _coverageWriters, _activationWriters;
    private long _coverageWritersAdmitted, _coverageWritersCompleted;
    private long _activationWritersAdmitted, _activationWritersCompleted;
    private int _reservedCoverageCount, _reservedActivationCount;
    private long _reservedCoverageBytes, _reservedActivationBytes;
    private string? _lastErrorCode;
    private readonly Dictionary<DerivedViewId, int> _leaseViews = [];
    private readonly Dictionary<DerivedViewId, HashSet<GateDerivedBatchKey>> _leasedMemberships = [];
    private bool _admissionClosed, _closed;
    private Task? _disposeTask;
    private TaskCompletionSource? _writersDrained, _readersDrained, _leasesDrained;
    private readonly DesiredReplacementControlLimits _desiredReplacementLimits;
    private readonly Dictionary<DesiredReplacementKey, InstalledDesiredReplacement> _desiredReplacements = [];
    private readonly Dictionary<DesiredReplacementKey, ulong> _replacementGenerations = [];
    private readonly DesiredReplacementReservationTombstone[] _desiredReplacementTombstones;
    private PreparedDesiredReplacementState? _preparedDesiredReplacement;
    private int _desiredReplacementTombstoneStart, _desiredReplacementTombstoneCount;
    private int _provisionalDesiredReplacementSlots;
    private ulong _committedReplacementToken, _preparedOperationGeneration;
    internal GateDerivedStoreTestHooks? TestHooks { get; set; }

    public InMemoryGateDerivedStore(GateDerivedStoreOptions? options = null)
        : this(options, new DesiredReplacementControlLimits()) { }

    internal InMemoryGateDerivedStore(GateDerivedStoreOptions? options,
        DesiredReplacementControlLimits desiredReplacementLimits)
    {
        _options = options ?? new();
        _desiredReplacementLimits = desiredReplacementLimits ?? throw new ArgumentNullException(nameof(desiredReplacementLimits));
        _desiredReplacementTombstones = new DesiredReplacementReservationTombstone[
            desiredReplacementLimits.TerminalReservationTombstoneCapacity];
        DesiredReplacementControl = new(this);
    }

    internal PreparedDesiredReplacementOwner DesiredReplacementControl { get; }

    public DerivedStoreRevision Revision { get { lock (_gate) { return new(_revision); } } }
    public ActiveDerivedViewDescriptor? ActiveView { get { lock (_gate) { return _active?.Descriptor; } } }
    private StoredView? _active;

    internal DesiredReplacementControlDiagnostics DesiredReplacementDiagnostics
    {
        get
        {
            lock (_gate)
                return new(_preparedDesiredReplacement is not null, _desiredReplacements.Count,
                    _provisionalDesiredReplacementSlots, _desiredReplacementTombstoneCount,
                    _desiredReplacementLimits.MaximumInstalledDesiredReplacements,
                    _desiredReplacementLimits.TerminalReservationTombstoneCapacity,
                    _committedReplacementToken, _preparedOperationGeneration);
        }
    }

    internal PrepareDesiredReplacementResult PrepareDesiredReplacement(PrepareDesiredReplacementRequest request)
    {
        var speculativeReservationId = new GatePreparedReservationId(Guid.NewGuid());
        lock (_gate)
        {
            if (!request.InspectionId.IsValid || !request.RunId.IsValid || !request.RunGeneration.IsValid ||
                !request.GatePlanId.IsValid || !request.GatePlanRevision.IsValid || !request.Owner.IsValid)
                return PrepareDesiredReplacementResult.Rejected(PrepareDesiredReplacementStatus.InvalidIdentity);
            if (_admissionClosed || _closed)
                return PrepareDesiredReplacementResult.Rejected(PrepareDesiredReplacementStatus.Closed);
            if (_preparedDesiredReplacement is not null)
                return PrepareDesiredReplacementResult.Rejected(PrepareDesiredReplacementStatus.AlreadyPrepared);

            DesiredReplacementKey key = new(request.InspectionId, request.GatePlanId);
            bool isNewKey = !_desiredReplacements.ContainsKey(key);
            if (isNewKey && _desiredReplacements.Count >=
                _desiredReplacementLimits.MaximumInstalledDesiredReplacements)
                return PrepareDesiredReplacementResult.Rejected(PrepareDesiredReplacementStatus.CapacityExceeded);

            ulong committedGeneration = _replacementGenerations.GetValueOrDefault(key);
            if (!TryGetNextDesiredReplacementCounter(_committedReplacementToken, out ulong nextToken) ||
                !TryGetNextDesiredReplacementCounter(committedGeneration, out ulong nextReplacementGeneration) ||
                !TryGetNextDesiredReplacementCounter(_preparedOperationGeneration, out ulong nextOperationGeneration))
                return PrepareDesiredReplacementResult.Rejected(PrepareDesiredReplacementStatus.CounterOverflow);

            var token = new GateReplacementToken(nextToken);
            var replacementGeneration = new GateReplacementGeneration(nextReplacementGeneration);
            var operationGeneration = new GatePreparedOperationGeneration(nextOperationGeneration);
            var reservation = new GatePreparedReservation(speculativeReservationId, request.Owner, operationGeneration,
                GatePreparedReservationState.Prepared);
            ActiveDerivedViewDescriptor?[] predecessorCell = new ActiveDerivedViewDescriptor?[1];
            var installation = new InstalledDesiredReplacement(key, request.RunId, request.RunGeneration,
                request.GatePlanRevision, token, replacementGeneration, predecessorCell);
            DesiredReplacementControlSlot? provisionalSlot = isNewKey ? new(true) : null;
            var state = new PreparedDesiredReplacementState(reservation, installation, provisionalSlot,
                predecessorCell);

            _desiredReplacements.EnsureCapacity(_desiredReplacements.Count + (isNewKey ? 1 : 0));
            _replacementGenerations.EnsureCapacity(_replacementGenerations.Count +
                (_replacementGenerations.ContainsKey(key) ? 0 : 1));

            _preparedOperationGeneration = operationGeneration.Value;
            if (provisionalSlot.HasValue) _provisionalDesiredReplacementSlots++;
            _preparedDesiredReplacement = state;
            return PrepareDesiredReplacementResult.Success(new(reservation, key, token, replacementGeneration));
        }
    }

    internal GatePreparedReservationResult CommitDesiredReplacement(GatePreparedReservation reservation,
        GatePreparedReservationOwner owner, GatePreparedOperationGeneration generation)
    {
        lock (_gate)
            return ConsumeDesiredReplacementUnderLock(reservation, owner, generation,
                GatePreparedReservationOperation.Commit);
    }

    internal GatePreparedReservationResult AbortDesiredReplacement(GatePreparedReservation reservation,
        GatePreparedReservationOwner owner, GatePreparedOperationGeneration generation)
    {
        lock (_gate)
            return ConsumeDesiredReplacementUnderLock(reservation, owner, generation,
                GatePreparedReservationOperation.Abort);
    }

    internal InstalledDesiredReplacement? FindInstalledDesiredReplacement(DesiredReplacementKey key)
    {
        lock (_gate) return _desiredReplacements.GetValueOrDefault(key);
    }

    private GatePreparedReservationResult ConsumeDesiredReplacementUnderLock(GatePreparedReservation reservation,
        GatePreparedReservationOwner owner, GatePreparedOperationGeneration generation,
        GatePreparedReservationOperation operation)
    {
        if (!reservation.IsValid)
            return GatePreparedReservationResult.Classify(default, owner, generation, operation);

        PreparedDesiredReplacementState? prepared = _preparedDesiredReplacement;
        if (prepared is null || prepared.Reservation.Id != reservation.Id)
        {
            GatePreparedReservation terminal = FindDesiredReplacementTombstoneUnderLock(reservation.Id);
            if (!terminal.IsValid)
                return GatePreparedReservationResult.Classify(default, owner, generation, operation);
            GatePreparedReservationResult replay = GatePreparedReservationResult.Classify(
                terminal, owner, generation, operation);
            if (replay.Status == GatePreparedReservationResultStatus.OwnerOrGenerationMismatch)
                return replay;
            if (reservation.Owner != terminal.Owner || reservation.Generation != terminal.Generation)
                return GatePreparedReservationResult.Classify(
                    terminal, reservation.Owner, reservation.Generation, operation);
            return replay;
        }

        GatePreparedReservation current = prepared.Reservation;
        GatePreparedReservationResult classified = GatePreparedReservationResult.Classify(
            current, owner, generation, operation);
        if (classified.Status == GatePreparedReservationResultStatus.OwnerOrGenerationMismatch)
            return classified;
        if (reservation.Owner != current.Owner || reservation.Generation != current.Generation)
            return GatePreparedReservationResult.Classify(current,
                reservation.Owner, reservation.Generation, operation);

        if (classified.Status == GatePreparedReservationResultStatus.Committed)
        {
            InstalledDesiredReplacement installation = prepared.Installation;
            prepared.PredecessorCell[0] = _active?.Descriptor;
            _desiredReplacements[installation.Key] = installation;
            _replacementGenerations[installation.Key] = installation.Generation.Value;
            _committedReplacementToken = installation.Token.Value;
            if (prepared.ProvisionalSlot.HasValue) _provisionalDesiredReplacementSlots--;
            StoreDesiredReplacementTombstoneUnderLock(current, GatePreparedReservationState.Committed);
            _preparedDesiredReplacement = null;
        }
        else if (classified.Status == GatePreparedReservationResultStatus.Aborted)
        {
            if (prepared.ProvisionalSlot.HasValue) _provisionalDesiredReplacementSlots--;
            StoreDesiredReplacementTombstoneUnderLock(current, GatePreparedReservationState.Aborted);
            _preparedDesiredReplacement = null;
        }
        return classified;
    }

    private void StoreDesiredReplacementTombstoneUnderLock(GatePreparedReservation reservation,
        GatePreparedReservationState state)
    {
        int capacity = _desiredReplacementTombstones.Length;
        int index;
        if (_desiredReplacementTombstoneCount < capacity)
        {
            index = (_desiredReplacementTombstoneStart + _desiredReplacementTombstoneCount) % capacity;
            _desiredReplacementTombstoneCount++;
        }
        else
        {
            index = _desiredReplacementTombstoneStart;
            _desiredReplacementTombstoneStart = (_desiredReplacementTombstoneStart + 1) % capacity;
        }
        _desiredReplacementTombstones[index] = new(reservation.Id, reservation.Owner,
            reservation.Generation, state);
    }

    private GatePreparedReservation FindDesiredReplacementTombstoneUnderLock(GatePreparedReservationId id)
    {
        for (int offset = 0; offset < _desiredReplacementTombstoneCount; offset++)
        {
            int index = (_desiredReplacementTombstoneStart + offset) % _desiredReplacementTombstones.Length;
            if (_desiredReplacementTombstones[index].ReservationId == id)
                return _desiredReplacementTombstones[index].Snapshot();
        }
        return default;
    }

    private void AbortPreparedDesiredReplacementUnderLock()
    {
        PreparedDesiredReplacementState? prepared = _preparedDesiredReplacement;
        if (prepared is null) return;
        if (prepared.ProvisionalSlot.HasValue) _provisionalDesiredReplacementSlots--;
        StoreDesiredReplacementTombstoneUnderLock(prepared.Reservation, GatePreparedReservationState.Aborted);
        _preparedDesiredReplacement = null;
    }

    internal static bool TryGetNextDesiredReplacementCounter(ulong committed, out ulong next)
    {
        if (committed == ulong.MaxValue) { next = 0; return false; }
        next = committed + 1;
        return true;
    }

    public GateDerivedStoreDiagnostics Diagnostics
    {
        get
        {
            lock (_gate)
            {
                HashSet<GateDerivedBatchKey> invalid = _views.Where(x => _invalidated.Contains(x.Key))
                    .SelectMany(x => x.Value.Membership).ToHashSet();
                HashSet<GateDerivedBatchKey> retained = _views.Where(x => !_invalidated.Contains(x.Key))
                    .SelectMany(x => x.Value.Membership).ToHashSet();
                HashSet<GateDerivedBatchKey> reclaimable = invalid.Where(x => !retained.Contains(x)).ToHashSet();
                long invalidatedViewBytes = _views.Where(x => _invalidated.Contains(x.Key))
                    .Sum(x => x.Value.LogicalBytes - x.Value.TombstoneBytes);
                long invalidatedBatchBytes = invalid.Sum(x => _batches.GetValueOrDefault(x)?.LogicalBytes ?? 0);
                long reclaimableBatchBytes = reclaimable.Sum(x => _batches.GetValueOrDefault(x)?.LogicalBytes ?? 0);
                return new(new(_revision), _batches.Count, _logicalBytes, _reservedBatches, _reservedBytes,
            _leases, _writers,
            _commits, _duplicates, _conflicts, _capacityExceeded, _activations, _invalidations,
            _reclaims, _admissionClosed, _closed, _writersAdmitted, _writersCompleted, _readers,
            _readersAdmitted, _readersCompleted, invalid.Count, reclaimable.Count, _views.Count,
            _terminalCoverage.Count, _active is null ? 0 : 1, _views.Count - (_active is null ? 0 : 1) - _invalidated.Count,
            _invalidated.Count, _usedViewIds.Count - _views.Count, _batches.Count,
            _views.Values.Sum(x => x.Membership.Count), invalidatedBatchBytes + invalidatedViewBytes,
            reclaimableBatchBytes + invalidatedViewBytes, _lastErrorCode,
            _coverageWriters, _coverageWritersAdmitted, _coverageWritersCompleted,
            _activationWriters, _activationWritersAdmitted, _activationWritersCompleted,
            _reservedCoverageCount, _reservedCoverageBytes,
            _reservedActivationCount, _reservedActivationBytes);
            }
        }
    }

    public async ValueTask<GateBatchDeliveryResult> DeliverAsync(GateEvaluationBatch batch,
        CancellationToken exceptionalCancellation = default)
    {
        GateDerivedMutationResult result = await CommitAsync(batch, exceptionalCancellation).ConfigureAwait(false);
        return new(result.Status switch
        {
            GateDerivedMutationStatus.Committed => GateBatchDeliveryStatus.Accepted,
            GateDerivedMutationStatus.Duplicate => GateBatchDeliveryStatus.Duplicate,
            GateDerivedMutationStatus.Conflict => GateBatchDeliveryStatus.Conflict,
            GateDerivedMutationStatus.CapacityExceeded => GateBatchDeliveryStatus.CapacityExceeded,
            GateDerivedMutationStatus.Cancelled => GateBatchDeliveryStatus.Cancelled,
            GateDerivedMutationStatus.Closed => GateBatchDeliveryStatus.Closed,
            _ => GateBatchDeliveryStatus.Failed
        }, result.ErrorCode);
    }

    public ValueTask<GateDerivedMutationResult> CommitAsync(GateEvaluationBatch batch,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(batch);
        return CommitSerializedAsync(batch, cancellationToken);
    }

    private async ValueTask<GateDerivedMutationResult> CommitSerializedAsync(GateEvaluationBatch batch,
        CancellationToken cancellationToken)
    {
        try { await _commitSerial.WaitAsync(cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) { return new(GateDerivedMutationStatus.Cancelled, Revision); }
        try { return await CommitCoreAsync(batch, cancellationToken).ConfigureAwait(false); }
        finally { _commitSerial.Release(); }
    }

    private ValueTask<GateDerivedMutationResult> CommitCoreAsync(GateEvaluationBatch batch,
        CancellationToken cancellationToken)
    {
        if (batch.Entries.Count == 0) { throw new ArgumentException("A derived batch must contain entries.", nameof(batch)); }
        if (System.Text.Encoding.UTF8.GetByteCount(batch.Trace.ConfirmedIdentity.Stream.SourceId.Value) > 128)
        { throw new ArgumentException("SourceId exceeds the derived-store UTF-8 limit.", nameof(batch)); }
        WriterAdmission admission;
        try { admission = TryAdmitWriter(cancellationToken); }
        catch (Exception error)
        {
            return ValueTask.FromResult(new GateDerivedMutationResult(
                GateDerivedMutationStatus.Faulted, Revision, error.GetType().Name));
        }
        if (admission == WriterAdmission.Cancelled)
            return ValueTask.FromResult(new GateDerivedMutationResult(
                GateDerivedMutationStatus.Cancelled, Revision));
        if (admission == WriterAdmission.Closed)
            return ValueTask.FromResult(new GateDerivedMutationResult(
                GateDerivedMutationStatus.Closed, Revision));
        bool reserved = false;
        long size = 0;
        try
        {
            GateDerivedBatchKey key = Key(batch);
            try { size = GateDerivedLogicalSize.Batch(batch); }
            catch (OverflowException) { return ValueTask.FromResult(WriterResult(GateDerivedMutationStatus.CapacityExceeded, "logical-size-overflow")); }
            lock (_gate)
            {
                if (_revision == ulong.MaxValue)
                { return ValueTask.FromResult(WriterResultUnderLock(GateDerivedMutationStatus.Faulted, "revision-overflow")); }
                if (_batches.TryGetValue(key, out StoredBatch? existing))
                {
                    if (BatchEquivalent(existing.Batch, batch)) { _duplicates++; return ValueTask.FromResult(WriterResultUnderLock(GateDerivedMutationStatus.Duplicate)); }
                    _conflicts++; return ValueTask.FromResult(WriterResultUnderLock(GateDerivedMutationStatus.Conflict));
                }
                if (_batches.Count + _reservedBatches >= _options.MaximumBatchCount ||
                    size > _options.LogicalPayloadBudgetBytes - _logicalBytes - _reservedBytes -
                        _reservedCoverageBytes - _reservedActivationBytes)
                { _capacityExceeded++; return ValueTask.FromResult(WriterResultUnderLock(GateDerivedMutationStatus.CapacityExceeded)); }
                _reservedBatches++; _reservedBytes = checked(_reservedBytes + size); reserved = true;
            }
            try { TestHooks?.AfterReservation?.Invoke(); }
            catch (Exception error) { return ValueTask.FromResult(WriterResult(GateDerivedMutationStatus.Faulted, error.GetType().Name)); }
            GateEvaluationBatch owned;
            try { owned = Clone(batch); }
            catch (Exception error) { return ValueTask.FromResult(WriterResult(GateDerivedMutationStatus.Faulted, error.GetType().Name)); }
            try { TestHooks?.AfterCopy?.Invoke(); }
            catch (Exception error) { return ValueTask.FromResult(WriterResult(GateDerivedMutationStatus.Faulted, error.GetType().Name)); }
            lock (_gate)
            {
                if (_batches.TryGetValue(key, out StoredBatch? raced))
                {
                    ReleaseReservationUnderLock(size); reserved = false;
                    if (BatchEquivalent(raced.Batch, owned)) { _duplicates++; return ValueTask.FromResult(WriterResultUnderLock(GateDerivedMutationStatus.Duplicate)); }
                    _conflicts++; return ValueTask.FromResult(WriterResultUnderLock(GateDerivedMutationStatus.Conflict));
                }
                try { TestHooks?.BeforePublish?.Invoke(); }
                catch (Exception error) { return ValueTask.FromResult(WriterResultUnderLock(GateDerivedMutationStatus.Faulted, error.GetType().Name)); }
                long nextBytes = checked(_logicalBytes + size); ulong nextRevision = checked(_revision + 1);
                _batches.Add(key, new(owned, size, new(nextRevision))); _logicalBytes = nextBytes;
                ReleaseReservationUnderLock(size); reserved = false;
                _revision = nextRevision; _commits++;
                return ValueTask.FromResult(WriterResultUnderLock(GateDerivedMutationStatus.Committed));
            }
        }
        finally
        {
            if (reserved) { lock (_gate) { ReleaseReservationUnderLock(size); } }
            ReleaseWriter();
        }
    }

    public GateDerivedMutationResult Activate(GateDerivedCoverageManifest manifest, DerivedViewId viewId) =>
        ActivateAsync(manifest, viewId, CancellationToken.None).AsTask().GetAwaiter().GetResult();

    private GateDerivedMutationResult ActivateLegacyUnderLock(GateDerivedCoverageManifest manifest, DerivedViewId viewId)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        if (!viewId.IsValid) { throw new ArgumentException("A valid view identifier is required.", nameof(viewId)); }
        lock (_gate)
        {
            if (_admissionClosed) { return ResultUnderLock(GateDerivedMutationStatus.Closed); }
            if (_revision == ulong.MaxValue || _activationOrder == ulong.MaxValue)
            { return ResultUnderLock(GateDerivedMutationStatus.Faulted, "revision-overflow"); }
            var terminalKey = (manifest.InspectionId, manifest.RunId, manifest.PlanId,
                manifest.PlanRevision, manifest.PlanSignature, manifest.SchemaVersion, manifest.AlgorithmVersion,
                manifest.CoverageScope);
            if (!_terminalCoverage.TryGetValue(terminalKey, out GateProcessingTerminalCoverage? terminal) ||
                terminal.FinalRevision != manifest.SourceFinalRevision || !TerminalMatchesManifest(terminal, manifest))
            { return ResultUnderLock(GateDerivedMutationStatus.CoverageMismatch, "source-final-revision"); }
            if (_views.TryGetValue(viewId, out StoredView? existingView))
            {
                return ResultUnderLock(ManifestEquivalent(existingView.Descriptor.Manifest, manifest)
                    ? GateDerivedMutationStatus.Duplicate : GateDerivedMutationStatus.Conflict);
            }
            if (_usedViewIds.Contains(viewId)) { return ResultUnderLock(GateDerivedMutationStatus.Conflict); }
            if (manifest.CapturedStoreRevision.Value > _revision) { return ResultUnderLock(GateDerivedMutationStatus.CoverageMismatch, "future-manifest"); }
            GateDerivedBatchKey[] membership = MatchingKeysUnderLock(manifest);
            if (!CoverageMatches(manifest, membership)) { return ResultUnderLock(GateDerivedMutationStatus.CoverageMismatch); }
            try { TestHooks?.BeforeActivation?.Invoke(); }
            catch (Exception error) { return ResultUnderLock(GateDerivedMutationStatus.Faulted, error.GetType().Name); }
            long manifestSize, viewSize, membershipSize;
            try
            {
                manifestSize = GateDerivedLogicalSize.Manifest(manifest);
                viewSize = checked(64 + System.Text.Encoding.UTF8.GetByteCount(viewId.Value));
                membershipSize = GateDerivedLogicalSize.Membership(membership.Length);
            }
            catch (OverflowException) { return ResultUnderLock(GateDerivedMutationStatus.CapacityExceeded, "logical-size-overflow"); }
            if (checked(manifestSize + viewSize + membershipSize) > _options.LogicalPayloadBudgetBytes - _logicalBytes)
            { _capacityExceeded++; return ResultUnderLock(GateDerivedMutationStatus.CapacityExceeded); }
            ulong next = checked(_revision + 1); ulong order = checked(_activationOrder + 1);
            var descriptor = new ActiveDerivedViewDescriptor(viewId, new(next), manifest, order);
            var stored = new StoredView(descriptor, checked(manifestSize + viewSize + membershipSize), viewSize,
                new HashSet<GateDerivedBatchKey>(membership));
            var stagedViews = new Dictionary<DerivedViewId, StoredView>(_views) { [viewId] = stored };
            var stagedIds = new HashSet<DerivedViewId>(_usedViewIds) { viewId };
            try { TestHooks?.AfterActivationStaging?.Invoke(); }
            catch (Exception error) { return ResultUnderLock(GateDerivedMutationStatus.Faulted, error.GetType().Name); }
            _views = stagedViews; _usedViewIds = stagedIds; _active = stored;
            _logicalBytes = checked(_logicalBytes + stored.LogicalBytes);
            _revision = next; _activationOrder = order; _activations++;
            return ResultUnderLock(GateDerivedMutationStatus.Activated);
        }
    }

    public async ValueTask<GateDerivedMutationResult> ActivateAsync(GateDerivedCoverageManifest manifest,
        DerivedViewId viewId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        if (!viewId.IsValid) throw new ArgumentException("A valid view identifier is required.", nameof(viewId));
        lock (_gate)
        {
            if (cancellationToken.IsCancellationRequested)
                return ResultUnderLock(GateDerivedMutationStatus.Cancelled);
            if (_admissionClosed)
                return ResultUnderLock(GateDerivedMutationStatus.Closed);
            _writers++; _writersAdmitted++; _activationWriters++; _activationWritersAdmitted++;
            _ = ++_nextOperationId;
        }
        await _commitSerial.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        long reserved = 0;
        bool reservationHeld = false;
        try
        {
            Dictionary<GateDerivedBatchKey, StoredBatch> batches;
            Dictionary<DerivedViewId, StoredView> views;
            HashSet<DerivedViewId> usedIds;
            Dictionary<(InspectionId, AcquisitionRunId, GatePlanId, GatePlanRevision, string,
                GateSchemaVersion, GateAlgorithmVersion, string), GateProcessingTerminalCoverage> terminalRoot;
            ulong revision, activationOrder;
            lock (_gate)
            {
                batches = _batches; views = _views; usedIds = _usedViewIds;
                terminalRoot = _terminalCoverage; revision = _revision; activationOrder = _activationOrder;
            }
            if (revision == ulong.MaxValue || activationOrder == ulong.MaxValue)
                return WriterResult(GateDerivedMutationStatus.Faulted, "revision-overflow");
            var terminalKey = (manifest.InspectionId, manifest.RunId, manifest.PlanId,
                manifest.PlanRevision, manifest.PlanSignature, manifest.SchemaVersion,
                manifest.AlgorithmVersion, manifest.CoverageScope);
            if (!terminalRoot.TryGetValue(terminalKey, out GateProcessingTerminalCoverage? terminal) ||
                terminal.FinalRevision != manifest.SourceFinalRevision || !TerminalMatchesManifest(terminal, manifest))
                return WriterResult(GateDerivedMutationStatus.CoverageMismatch, "source-final-revision");
            if (views.TryGetValue(viewId, out StoredView? existingView))
                return WriterResult(ManifestEquivalent(existingView.Descriptor.Manifest, manifest)
                    ? GateDerivedMutationStatus.Duplicate : GateDerivedMutationStatus.Conflict);
            if (usedIds.Contains(viewId)) return WriterResult(GateDerivedMutationStatus.Conflict);
            if (manifest.CapturedStoreRevision.Value > revision)
                return WriterResult(GateDerivedMutationStatus.CoverageMismatch, "future-manifest");
            var membershipBuilder = new List<GateDerivedBatchKey>();
            foreach (KeyValuePair<GateDerivedBatchKey, StoredBatch> candidate in batches)
            {
                if (candidate.Value.Revision.Value <= manifest.CapturedStoreRevision.Value &&
                    BelongsToManifest(candidate.Key, manifest))
                    membershipBuilder.Add(candidate.Key);
            }
            GateDerivedBatchKey[] membership = membershipBuilder.ToArray();
            if (!CoverageMatches(manifest, membership))
                return WriterResult(GateDerivedMutationStatus.CoverageMismatch);
            long manifestSize = GateDerivedLogicalSize.Manifest(manifest);
            long viewSize = checked(64 + System.Text.Encoding.UTF8.GetByteCount(viewId.Value));
            long membershipSize = GateDerivedLogicalSize.Membership(membership.Length);
            reserved = checked(manifestSize + viewSize + membershipSize);
            lock (_gate)
            {
                if (reserved > _options.LogicalPayloadBudgetBytes - _logicalBytes - _reservedBytes -
                    _reservedCoverageBytes - _reservedActivationBytes)
                { _capacityExceeded++; return WriterResultUnderLock(GateDerivedMutationStatus.CapacityExceeded); }
                _reservedActivationCount++; _reservedActivationBytes += reserved; reservationHeld = true;
            }
            ulong next = checked(revision + 1); ulong order = checked(activationOrder + 1);
            var descriptor = new ActiveDerivedViewDescriptor(viewId, new(next), manifest, order);
            var stored = new StoredView(descriptor, reserved, viewSize, new HashSet<GateDerivedBatchKey>(membership));
            var stagedViews = new Dictionary<DerivedViewId, StoredView>(views) { [viewId] = stored };
            var stagedIds = new HashSet<DerivedViewId>(usedIds) { viewId };
            lock (_gate)
            {
                if (!ReferenceEquals(_batches, batches) || !ReferenceEquals(_views, views) ||
                    !ReferenceEquals(_usedViewIds, usedIds) || !ReferenceEquals(_terminalCoverage, terminalRoot) ||
                    _revision != revision || _activationOrder != activationOrder)
                    return WriterResultUnderLock(GateDerivedMutationStatus.Conflict, "stale-activation");
                ReleaseActivationReservationUnderLock(reserved); reservationHeld = false;
                _views = stagedViews; _usedViewIds = stagedIds; _active = stored;
                _logicalBytes = checked(_logicalBytes + reserved); _revision = next;
                _activationOrder = order; _activations++;
                return new GateDerivedMutationResult(GateDerivedMutationStatus.Activated,
                    new(_revision), wasAccepted: true);
            }
        }
        catch (OverflowException) { return WriterResult(GateDerivedMutationStatus.CapacityExceeded, "logical-size-overflow"); }
        catch (Exception error) { return WriterResult(GateDerivedMutationStatus.Faulted, error.GetType().Name); }
        finally
        {
            if (reservationHeld) lock (_gate) { ReleaseActivationReservationUnderLock(reserved); }
            _commitSerial.Release();
            ReleaseActivationWriter();
        }
    }

    public GateDerivedMutationResult Invalidate(DerivedViewId viewId)
    {
        if (!viewId.IsValid) { throw new ArgumentException("A valid view identifier is required.", nameof(viewId)); }
        lock (_gate)
        {
            if (_admissionClosed) { return ResultUnderLock(GateDerivedMutationStatus.Closed); }
            if (_revision == ulong.MaxValue) { return ResultUnderLock(GateDerivedMutationStatus.Faulted, "revision-overflow"); }
            if (!_views.ContainsKey(viewId) || _invalidated.Contains(viewId))
            { return ResultUnderLock(GateDerivedMutationStatus.NotFound); }
            try { TestHooks?.BeforeInvalidation?.Invoke(); }
            catch (Exception error) { return ResultUnderLock(GateDerivedMutationStatus.Faulted, error.GetType().Name); }
            ulong next = checked(_revision + 1);
            var stagedInvalidated = new HashSet<DerivedViewId>(_invalidated) { viewId };
            try { TestHooks?.AfterInvalidationStaging?.Invoke(); }
            catch (Exception error) { return ResultUnderLock(GateDerivedMutationStatus.Faulted, error.GetType().Name); }
            _invalidated = stagedInvalidated; if (_active?.Descriptor.ViewId == viewId) { _active = null; }
            _revision = next; _invalidations++;
            return ResultUnderLock(GateDerivedMutationStatus.Invalidated);
        }
    }

    public ValueTask<GateDerivedMutationResult> ReclaimInvalidatedAsync(CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested)
        { return ValueTask.FromResult(new GateDerivedMutationResult(GateDerivedMutationStatus.Cancelled, Revision)); }
        lock (_gate)
        {
            if (_admissionClosed) { return ValueTask.FromResult(ResultUnderLock(GateDerivedMutationStatus.Closed)); }
            if (_revision == ulong.MaxValue) { return ValueTask.FromResult(ResultUnderLock(GateDerivedMutationStatus.Faulted, "revision-overflow")); }
            if (_invalidated.Count == 0) { return ValueTask.FromResult(ResultUnderLock(GateDerivedMutationStatus.NotFound)); }
            try { TestHooks?.BeforeReclaim?.Invoke(); }
            catch (Exception error) { return ValueTask.FromResult(ResultUnderLock(GateDerivedMutationStatus.Faulted, error.GetType().Name)); }
            ulong nextRevision = checked(_revision + 1);
            HashSet<GateDerivedBatchKey> remove = [];
            foreach (DerivedViewId id in _invalidated)
            {
                StoredView view = _views[id];
                foreach (GateDerivedBatchKey key in view.Membership)
                    if (!_views.Values.Any(other => !_invalidated.Contains(other.Descriptor.ViewId) && other.Membership.Contains(key))) remove.Add(key);
            }
            if (_leaseViews.Any(x => x.Value != 0 && _leasedMemberships[x.Key].Overlaps(remove)))
            { return ValueTask.FromResult(ResultUnderLock(GateDerivedMutationStatus.Busy)); }
            var stagedBatches = new Dictionary<GateDerivedBatchKey, StoredBatch>(_batches);
            var stagedViews = new Dictionary<DerivedViewId, StoredView>(_views);
            long nextBytes = _logicalBytes;
            foreach (GateDerivedBatchKey key in remove) { nextBytes = checked(nextBytes - stagedBatches[key].LogicalBytes); stagedBatches.Remove(key); }
            foreach (DerivedViewId id in _invalidated)
            { nextBytes = checked(nextBytes - (stagedViews[id].LogicalBytes - stagedViews[id].TombstoneBytes)); stagedViews.Remove(id); }
            try { TestHooks?.AfterReclaimStaging?.Invoke(); }
            catch (Exception error) { return ValueTask.FromResult(ResultUnderLock(GateDerivedMutationStatus.Faulted, error.GetType().Name)); }
            _batches = stagedBatches; _views = stagedViews; _invalidated = []; _logicalBytes = nextBytes;
            _revision = nextRevision; _reclaims++;
            return ValueTask.FromResult(ResultUnderLock(GateDerivedMutationStatus.Reclaimed));
        }
    }

    public IGateDerivedReadLease OpenActiveView()
    {
        lock (_gate)
        {
            if (_admissionClosed || _active is null) { throw new InvalidOperationException("No active derived view is available."); }
            _readers++; _readersAdmitted++;
            try
            {
                var lease = new ReadLease(this, _active);
                _leases++; _leaseViews[_active.Descriptor.ViewId] = _leaseViews.GetValueOrDefault(_active.Descriptor.ViewId) + 1;
                _leasedMemberships[_active.Descriptor.ViewId] = _active.Membership; return lease;
            }
            finally { _readers--; _readersCompleted++; if (_readers == 0) { _readersDrained?.TrySetResult(); } }
        }
    }

    public GateProcessingTerminalCoverageStatus ConfirmTerminalCoverage(
        IReadOnlyList<GateProcessingTerminalCoverage> coverage)
    {
        GateDerivedMutationResult result = ConfirmTerminalCoverageAsync(coverage, CancellationToken.None)
            .AsTask().GetAwaiter().GetResult();
        return result.Status switch
        {
            GateDerivedMutationStatus.Committed => GateProcessingTerminalCoverageStatus.Accepted,
            GateDerivedMutationStatus.Duplicate => GateProcessingTerminalCoverageStatus.Duplicate,
            GateDerivedMutationStatus.Conflict => GateProcessingTerminalCoverageStatus.Conflict,
            GateDerivedMutationStatus.CapacityExceeded => GateProcessingTerminalCoverageStatus.CapacityExceeded,
            GateDerivedMutationStatus.Closed => GateProcessingTerminalCoverageStatus.Closed,
            _ => GateProcessingTerminalCoverageStatus.Faulted
        };
    }

    private GateProcessingTerminalCoverageStatus ConfirmTerminalCoverageLegacyUnderLock(
        IReadOnlyList<GateProcessingTerminalCoverage> coverage)
    {
        ArgumentNullException.ThrowIfNull(coverage);
        if (coverage.Count == 0) { throw new ArgumentException("Terminal coverage cannot be empty.", nameof(coverage)); }
        lock (_gate)
        {
            GateProcessingTerminalCoverageStatus Finish(GateProcessingTerminalCoverageStatus status, string? error = null)
            {
                if (status == GateProcessingTerminalCoverageStatus.Duplicate) { _duplicates++; }
                else if (status == GateProcessingTerminalCoverageStatus.Conflict) { _conflicts++; }
                else if (status == GateProcessingTerminalCoverageStatus.CapacityExceeded) { _capacityExceeded++; }
                if (error is not null) { _lastErrorCode = error; }
                return status;
            }
            if (_admissionClosed) { return Finish(GateProcessingTerminalCoverageStatus.Closed, "terminal-coverage-closed"); }
            try
            {
                var pending = new List<((InspectionId, AcquisitionRunId, GatePlanId, GatePlanRevision, string, GateSchemaVersion, GateAlgorithmVersion, string),
                    GateProcessingTerminalCoverage, long)>();
                var inputKeys = new HashSet<(InspectionId, AcquisitionRunId, GatePlanId, GatePlanRevision, string, GateSchemaVersion, GateAlgorithmVersion, string)>();
                GateProcessingTerminalCoverage basis = coverage[0];
                int duplicateCount = 0; long totalSize = 0;
                foreach (GateProcessingTerminalCoverage item in coverage)
                {
                    if (item.InspectionId != basis.InspectionId || item.RunId != basis.RunId ||
                        item.FinalRevision != basis.FinalRevision)
                    { return Finish(GateProcessingTerminalCoverageStatus.Conflict, "terminal-coverage-conflict"); }
                    var key = (item.InspectionId, item.RunId, item.PlanId, item.PlanRevision, item.PlanSignature,
                        item.SchemaVersion, item.AlgorithmVersion, item.CoverageScope);
                    if (!inputKeys.Add(key)) { return Finish(GateProcessingTerminalCoverageStatus.Conflict, "terminal-coverage-conflict"); }
                    if (_terminalCoverage.TryGetValue(key, out GateProcessingTerminalCoverage? existing))
                    {
                        if (existing.FinalRevision != item.FinalRevision || !TerminalEquivalent(existing, item))
                        { return Finish(GateProcessingTerminalCoverageStatus.Conflict, "terminal-coverage-conflict"); }
                        duplicateCount++; continue;
                    }
                    long size = GateDerivedLogicalSize.TerminalCoverage(item);
                    totalSize = checked(totalSize + size); pending.Add((key, item, size));
                }
                if (duplicateCount != 0 && pending.Count != 0) { return Finish(GateProcessingTerminalCoverageStatus.Conflict, "terminal-coverage-conflict"); }
                if (duplicateCount == coverage.Count) { return Finish(GateProcessingTerminalCoverageStatus.Duplicate); }
                if (totalSize > _options.LogicalPayloadBudgetBytes - _logicalBytes)
                { return Finish(GateProcessingTerminalCoverageStatus.CapacityExceeded, "terminal-coverage-capacity"); }
                long nextBytes = checked(_logicalBytes + totalSize);
                var staged = new Dictionary<(InspectionId, AcquisitionRunId, GatePlanId, GatePlanRevision, string,
                    GateSchemaVersion, GateAlgorithmVersion, string), GateProcessingTerminalCoverage>(_terminalCoverage);
                foreach (var item in pending) { staged.Add(item.Item1, item.Item2); }
                _terminalCoverage = staged; _logicalBytes = nextBytes;
            }
            catch (OverflowException) { return Finish(GateProcessingTerminalCoverageStatus.CapacityExceeded, "terminal-coverage-overflow"); }
            catch { return Finish(GateProcessingTerminalCoverageStatus.Faulted, "terminal-coverage-faulted"); }
            return GateProcessingTerminalCoverageStatus.Accepted;
        }
    }

    public async ValueTask<GateDerivedMutationResult> ConfirmTerminalCoverageAsync(
        IReadOnlyList<GateProcessingTerminalCoverage> coverage,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(coverage);
        if (coverage.Count == 0) throw new ArgumentException("Terminal coverage cannot be empty.", nameof(coverage));
        lock (_gate)
        {
            if (cancellationToken.IsCancellationRequested)
                return ResultUnderLock(GateDerivedMutationStatus.Cancelled);
            if (_admissionClosed)
                return ResultUnderLock(GateDerivedMutationStatus.Closed);
            _writers++; _writersAdmitted++; _coverageWriters++; _coverageWritersAdmitted++;
            _ = ++_nextOperationId;
        }
        await _commitSerial.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        long reservationSize = 0;
        bool reservationHeld = false;
        try
        {
            Dictionary<(InspectionId, AcquisitionRunId, GatePlanId, GatePlanRevision, string,
                GateSchemaVersion, GateAlgorithmVersion, string), GateProcessingTerminalCoverage> root;
            long logicalBytes;
            lock (_gate) { root = _terminalCoverage; logicalBytes = _logicalBytes; }
            var staged = new Dictionary<(InspectionId, AcquisitionRunId, GatePlanId, GatePlanRevision, string,
                GateSchemaVersion, GateAlgorithmVersion, string), GateProcessingTerminalCoverage>(root);
            var inputKeys = new HashSet<(InspectionId, AcquisitionRunId, GatePlanId, GatePlanRevision, string,
                GateSchemaVersion, GateAlgorithmVersion, string)>();
            GateProcessingTerminalCoverage basis = coverage[0];
            int duplicates = 0; long totalSize = 0;
            foreach (GateProcessingTerminalCoverage item in coverage)
                try
                {
                    if (item.InspectionId != basis.InspectionId || item.RunId != basis.RunId ||
                        item.FinalRevision != basis.FinalRevision)
                        return WriterResult(GateDerivedMutationStatus.Conflict, "terminal-coverage-conflict");
                    var key = (item.InspectionId, item.RunId, item.PlanId, item.PlanRevision,
                        item.PlanSignature, item.SchemaVersion, item.AlgorithmVersion, item.CoverageScope);
                    if (!inputKeys.Add(key))
                        return WriterResult(GateDerivedMutationStatus.Conflict, "terminal-coverage-conflict");
                    if (root.TryGetValue(key, out GateProcessingTerminalCoverage? existing))
                    {
                        if (existing.FinalRevision != item.FinalRevision || !TerminalEquivalent(existing, item))
                            return WriterResult(GateDerivedMutationStatus.Conflict, "terminal-coverage-conflict");
                        duplicates++; continue;
                    }
                    totalSize = checked(totalSize + GateDerivedLogicalSize.TerminalCoverage(item));
                    staged.Add(key, item);
                }
                catch (Exception error) when (error is not OverflowException)
                { return WriterResult(GateDerivedMutationStatus.Faulted, error.GetType().Name); }
            if (duplicates != 0 && duplicates != coverage.Count)
                return WriterResult(GateDerivedMutationStatus.Conflict, "terminal-coverage-conflict");
            if (duplicates == coverage.Count) return WriterResult(GateDerivedMutationStatus.Duplicate);
            reservationSize = totalSize;
            lock (_gate)
            {
                if (reservationSize > _options.LogicalPayloadBudgetBytes - _logicalBytes - _reservedBytes -
                    _reservedCoverageBytes - _reservedActivationBytes)
                { _capacityExceeded++; return WriterResultUnderLock(GateDerivedMutationStatus.CapacityExceeded); }
                _reservedCoverageCount++; _reservedCoverageBytes += reservationSize; reservationHeld = true;
            }
            lock (_gate)
            {
                if (!ReferenceEquals(_terminalCoverage, root) || _logicalBytes != logicalBytes)
                    return WriterResultUnderLock(GateDerivedMutationStatus.Conflict, "stale-terminal-coverage");
                ReleaseCoverageReservationUnderLock(reservationSize); reservationHeld = false;
                _terminalCoverage = staged; _logicalBytes = checked(_logicalBytes + totalSize);
                return new GateDerivedMutationResult(GateDerivedMutationStatus.Committed,
                    new(_revision), wasAccepted: true);
            }
        }
        catch (OverflowException) { return WriterResult(GateDerivedMutationStatus.CapacityExceeded, "terminal-coverage-overflow"); }
        catch (Exception error) { return WriterResult(GateDerivedMutationStatus.Faulted, error.GetType().Name); }
        finally
        {
            if (reservationHeld) lock (_gate) { ReleaseCoverageReservationUnderLock(reservationSize); }
            _commitSerial.Release();
            ReleaseCoverageWriter();
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (_disposeTask is not null) { return new(_disposeTask); }
            _admissionClosed = true;
            AbortPreparedDesiredReplacementUnderLock();
            _desiredReplacements.Clear();
            _replacementGenerations.Clear();
            _provisionalDesiredReplacementSlots = 0;
            _committedReplacementToken = 0;
            Task writers = WaitUnderLock(_writers, ref _writersDrained);
            Task readers = WaitUnderLock(_readers, ref _readersDrained);
            Task leases = WaitUnderLock(_leases, ref _leasesDrained);
            _disposeTask = CloseAsync(writers, readers, leases); return new(_disposeTask);
        }
    }

    private async Task CloseAsync(Task writers, Task readers, Task leases)
    {
        await Task.WhenAll(writers, readers, leases).ConfigureAwait(false);
        lock (_gate)
        {
            _active = null; _batches.Clear(); _views.Clear(); _invalidated.Clear(); _terminalCoverage.Clear();
            _usedViewIds.Clear(); _leaseViews.Clear(); _leasedMemberships.Clear();
            _logicalBytes = 0; _closed = true;
        }
    }

    private GateDerivedBatchKey[] MatchingKeysUnderLock(GateDerivedCoverageManifest manifest) =>
        _batches.Where(x => x.Value.Revision.Value <= manifest.CapturedStoreRevision.Value && BelongsToManifest(x.Key, manifest))
            .Select(x => x.Key).ToArray();

    private static bool CoverageMatches(GateDerivedCoverageManifest manifest, GateDerivedBatchKey[] matching)
    {
        if (matching.GroupBy(x => x.TraceIdentity).Any(x => x.Count() != 1)) { return false; }
        var byStream = matching.GroupBy(x => x.TraceIdentity.Stream)
            .ToDictionary(x => x.Key, x => x.Select(k => k.TraceIdentity).ToArray());
        foreach (GateDerivedStreamCoverage stream in manifest.Streams)
        {
            TraceIdentity[] identities = byStream.GetValueOrDefault(stream.Stream) ?? [];
            if (identities.Length != stream.EligibleTraceCount ||
                GateDerivedStreamCoverage.ComputeIdentityDigest(identities) != stream.IdentityDigest) return false;
            if (identities.Length != 0 && (identities.Min(x => x.AcquisitionIndex) != stream.MinimumAcquisitionIndex ||
                identities.Max(x => x.AcquisitionIndex) != stream.MaximumAcquisitionIndex)) return false;
        }
        return byStream.Keys.All(x => manifest.Streams.Any(s => s.Stream == x)) &&
            byStream.Values.Sum(x => x.Length) == manifest.TotalEligibleTraceCount;
    }

    private static bool BelongsToManifest(GateDerivedBatchKey key, GateDerivedCoverageManifest manifest)
    {
        GateDerivedStreamCoverage? stream = manifest.Streams.SingleOrDefault(x => x.Stream == key.TraceIdentity.Stream);
        return key.TraceIdentity.Stream.InspectionId == manifest.InspectionId &&
            key.TraceIdentity.Stream.AcquisitionRunId == manifest.RunId && stream is not null &&
            (stream.SelectedRanges is null || stream.SelectedRanges.Any(range => range.Contains(key.TraceIdentity.AcquisitionIndex))) &&
            key.SourceInspectionRevision.Value <= manifest.SourceFinalRevision.Value && key.PlanId == manifest.PlanId &&
            key.PlanRevision == manifest.PlanRevision && key.PlanSignature == manifest.PlanSignature &&
            key.SchemaVersion == manifest.SchemaVersion && key.AlgorithmVersion == manifest.AlgorithmVersion;
    }

    private WriterAdmission TryAdmitWriter(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (cancellationToken.IsCancellationRequested) return WriterAdmission.Cancelled;
            if (_admissionClosed) return WriterAdmission.Closed;
            _writers++; _writersAdmitted++;
            return WriterAdmission.Admitted;
        }
    }
    private void ReleaseWriter()
    { lock (_gate) { _writers--; _writersCompleted++; if (_writers == 0) _writersDrained?.TrySetResult(); } }
    private void ReleaseCoverageWriter()
    {
        lock (_gate)
        {
            _coverageWriters--; _coverageWritersCompleted++;
            _writers--; _writersCompleted++;
            if (_writers == 0) _writersDrained?.TrySetResult();
        }
    }
    private void ReleaseActivationWriter()
    {
        lock (_gate)
        {
            _activationWriters--; _activationWritersCompleted++;
            _writers--; _writersCompleted++;
            if (_writers == 0) _writersDrained?.TrySetResult();
        }
    }
    private void ReleaseLease(DerivedViewId viewId)
    { lock (_gate) { _leases--; if (--_leaseViews[viewId] == 0) { _leaseViews.Remove(viewId); _leasedMemberships.Remove(viewId); } if (_leases == 0) _leasesDrained?.TrySetResult(); } }
    private static Task WaitUnderLock(int count, ref TaskCompletionSource? source)
    { if (count == 0) return Task.CompletedTask; source ??= new(TaskCreationOptions.RunContinuationsAsynchronously); return source.Task; }
    private GateDerivedMutationResult Result(GateDerivedMutationStatus status, string? error = null)
    { lock (_gate) return ResultUnderLock(status, error); }
    private GateDerivedMutationResult ResultUnderLock(GateDerivedMutationStatus status, string? error = null)
    { if (error is not null) _lastErrorCode = error.Length <= 128 ? error : error[..128]; return new(status, new(_revision), error); }
    private GateDerivedMutationResult WriterResult(GateDerivedMutationStatus status, string? error = null)
    { lock (_gate) return WriterResultUnderLock(status, error); }
    private GateDerivedMutationResult WriterResultUnderLock(GateDerivedMutationStatus status, string? error = null)
    { if (error is not null) _lastErrorCode = error.Length <= 128 ? error : error[..128]; return new(status, new(_revision), error, wasAccepted: true); }
    private void ReleaseReservationUnderLock(long size) { _reservedBatches--; _reservedBytes -= size; }
    private void ReleaseCoverageReservationUnderLock(long size)
    { _reservedCoverageCount--; _reservedCoverageBytes -= size; }
    private void ReleaseActivationReservationUnderLock(long size)
    { _reservedActivationCount--; _reservedActivationBytes -= size; }

    private enum WriterAdmission { Admitted, Cancelled, Closed }

    private static bool ManifestEquivalent(GateDerivedCoverageManifest left, GateDerivedCoverageManifest right) =>
        left.InspectionId == right.InspectionId && left.RunId == right.RunId &&
        left.SourceFinalRevision == right.SourceFinalRevision && left.PlanId == right.PlanId &&
        left.PlanRevision == right.PlanRevision && left.PlanSignature == right.PlanSignature &&
        left.SchemaVersion == right.SchemaVersion && left.AlgorithmVersion == right.AlgorithmVersion &&
        left.TotalEligibleTraceCount == right.TotalEligibleTraceCount &&
        left.CapturedStoreRevision == right.CapturedStoreRevision && left.CoverageScope == right.CoverageScope &&
        CoveragesEquivalent(left.Streams, right.Streams);

    private static bool TerminalMatchesManifest(GateProcessingTerminalCoverage terminal,
        GateDerivedCoverageManifest manifest) => terminal.SchemaVersion == manifest.SchemaVersion &&
        terminal.AlgorithmVersion == manifest.AlgorithmVersion && terminal.CoverageScope == manifest.CoverageScope &&
        CoveragesEquivalent(terminal.Streams, manifest.Streams);

    private static bool TerminalEquivalent(GateProcessingTerminalCoverage left,
        GateProcessingTerminalCoverage right) => left.PlanId == right.PlanId &&
        left.PlanRevision == right.PlanRevision && left.PlanSignature == right.PlanSignature &&
        left.SchemaVersion == right.SchemaVersion && left.AlgorithmVersion == right.AlgorithmVersion &&
        left.CoverageScope == right.CoverageScope &&
        CoveragesEquivalent(left.Streams, right.Streams);

    private static bool CoveragesEquivalent(IReadOnlyList<GateDerivedStreamCoverage> left,
        IReadOnlyList<GateDerivedStreamCoverage> right) => left.Count == right.Count &&
        left.All(item => right.SingleOrDefault(candidate => candidate.Stream == item.Stream) is { } match &&
            CoverageEquivalent(item, match));

    private static bool CoverageEquivalent(GateDerivedStreamCoverage left, GateDerivedStreamCoverage right) =>
        left.Stream == right.Stream && left.MinimumAcquisitionIndex == right.MinimumAcquisitionIndex &&
        left.MaximumAcquisitionIndex == right.MaximumAcquisitionIndex &&
        left.EligibleTraceCount == right.EligibleTraceCount && left.IdentityDigest == right.IdentityDigest &&
        ((left.SelectedRanges is null && right.SelectedRanges is null) ||
        (left.SelectedRanges is not null && right.SelectedRanges is not null &&
        left.SelectedRanges.SequenceEqual(right.SelectedRanges)));

    private static GateDerivedBatchKey Key(GateEvaluationBatch batch)
    {
        GateEvaluationEntry first = batch.Entries[0];
        if (batch.Entries.Any(x => x.TraceIdentity != first.TraceIdentity || x.SourceInspectionRevision != first.SourceInspectionRevision ||
            x.PlanId != first.PlanId || x.PlanRevision != first.PlanRevision || !x.PlanSignature.Equals(first.PlanSignature) ||
            x.SchemaVersion != first.SchemaVersion || x.AlgorithmVersion != first.AlgorithmVersion))
        { throw new ArgumentException("All batch entries must share the batch key.", nameof(batch)); }
        return new(first.TraceIdentity, first.SourceInspectionRevision, first.PlanId, first.PlanRevision,
            first.PlanSignature.Hex, first.SchemaVersion, first.AlgorithmVersion);
    }

    private static bool BatchEquivalent(GateEvaluationBatch left, GateEvaluationBatch right) =>
        left.Trace.Descriptor == right.Trace.Descriptor &&
        left.Trace.ConfirmedIdentity == right.Trace.ConfirmedIdentity &&
        left.Trace.SourceInspectionRevision == right.Trace.SourceInspectionRevision &&
        left.Trace.EffectiveQuality == right.Trace.EffectiveQuality && left.Equals(right);

    private static GateEvaluationBatch Clone(GateEvaluationBatch batch)
    {
        TraceDescriptor d = batch.Trace.Descriptor;
        var descriptor = new TraceDescriptor(d.Identity, d.Sequence, d.ElapsedSinceRunStart, d.TimestampUtc,
            d.SpatialCoordinate, d.SampleCount, d.SampleRateHz, d.FirstSampleOffsetSeconds, d.SignalMode,
            d.ClockQuality, d.SampleEncoding, d.Provenance, d.TraceQuality);
        var trace = new ConfirmedGateTraceDescriptor(descriptor, batch.Trace.ConfirmedIdentity,
            batch.Trace.SourceInspectionRevision, batch.Trace.EffectiveQuality);
        var signature = new GateEvaluationSignature(batch.ReproducibilitySignature.Bytes.Span);
        GateEvaluationEntry[] entries = batch.Entries.Select(x => new GateEvaluationEntry(x.TraceIdentity,
            x.SourceInspectionRevision, x.PlanId, x.PlanRevision, new GatePlanSignature(x.PlanSignature.Bytes.Span),
            x.SchemaVersion, x.AlgorithmVersion, x.GateId, x.GateRevision, x.RequestedMeasures, x.Status, x.EventKind,
            x.SampleIndex, x.FractionalSamplePosition, x.DetectionTimeSeconds, x.AmplitudeCounts,
            x.SignedRfAmplitudeCounts, x.TimeOfFlightSeconds, x.OffsetWithinWindowSeconds, x.Quality,
            x.SignalDomain, x.Provenance, x.CausalGateId, x.CausalStatus, x.DiagnosticCode,
            new GateDetectorMetrics(x.Metrics.ProminenceCounts, x.Metrics.ThresholdMarginCounts))).ToArray();
        return new(trace, signature, entries);
    }

    private sealed record StoredBatch(GateEvaluationBatch Batch, long LogicalBytes, DerivedStoreRevision Revision);
    private sealed record StoredView(ActiveDerivedViewDescriptor Descriptor, long LogicalBytes, long TombstoneBytes,
        HashSet<GateDerivedBatchKey> Membership);

    /// <summary>
    /// Immutable once published in the desired-replacement root. RunId and RunGeneration are structurally
    /// validated payload only: before an Apply path can use this type, the store must add run authority, a prepared
    /// run transition, outgoing-state invalidation, and serialization with CAS under the store gate. The containing
    /// store alone assigns the predecessor immediately before publication while holding that gate.
    /// </summary>
    internal sealed class InstalledDesiredReplacement
    {
        internal InstalledDesiredReplacement(DesiredReplacementKey key, AcquisitionRunId runId,
            GateRunGeneration runGeneration, GatePlanRevision planRevision, GateReplacementToken token,
            GateReplacementGeneration generation, ActiveDerivedViewDescriptor?[] predecessorCell)
        {
            Key = key;
            RunId = runId;
            RunGeneration = runGeneration;
            PlanRevision = planRevision;
            Token = token;
            Generation = generation;
            _predecessorCell = predecessorCell;
        }

        internal DesiredReplacementKey Key { get; }
        internal AcquisitionRunId RunId { get; }
        internal GateRunGeneration RunGeneration { get; }
        internal GatePlanRevision PlanRevision { get; }
        internal GateReplacementToken Token { get; }
        internal GateReplacementGeneration Generation { get; }
        private readonly ActiveDerivedViewDescriptor?[] _predecessorCell;
        internal ActiveDerivedViewDescriptor? Predecessor => _predecessorCell[0];
    }

    private sealed class ReadLease : IGateDerivedReadLease
    {
        private readonly object _gate = new();
        private InMemoryGateDerivedStore? _owner;
        private readonly Dictionary<string, CursorState> _cursors = [];
        private readonly StoredView _storedView;
        internal ReadLease(InMemoryGateDerivedStore owner, StoredView view)
        { _owner = owner; _storedView = view; Revision = view.Descriptor.ActivationRevision; View = view.Descriptor; }
        public DerivedStoreRevision Revision { get; }
        public ActiveDerivedViewDescriptor View { get; }
        public GateEvaluationBatch? ReadExact(GateDerivedBatchKey key)
        {
            if (!key.IsValid) { throw new ArgumentException("A valid derived batch key is required.", nameof(key)); }
            lock (_gate)
            {
                EnsureAlive(); lock (_owner!._gate)
                {
                    return _storedView.Membership.Contains(key) && _owner._batches.TryGetValue(key, out StoredBatch? stored)
                        ? stored.Batch : null;
                }
            }
        }
        public ValueTask<GateDerivedExactReadResult> ReadExactAsync(GateDerivedBatchKey key,
            CancellationToken cancellationToken = default)
        {
            if (!key.IsValid) { throw new ArgumentException("A valid derived batch key is required.", nameof(key)); }
            if (cancellationToken.IsCancellationRequested)
                return ValueTask.FromResult(new GateDerivedExactReadResult(GateDerivedExactReadStatus.Cancelled, null));
            try
            {
                GateEvaluationBatch? batch = ReadExact(key);
                return ValueTask.FromResult(new GateDerivedExactReadResult(batch is null
                    ? GateDerivedExactReadStatus.NotAvailable : GateDerivedExactReadStatus.Available, batch));
            }
            catch (ObjectDisposedException)
            { return ValueTask.FromResult(new GateDerivedExactReadResult(GateDerivedExactReadStatus.Closed, null)); }
            catch (Exception error)
            { return ValueTask.FromResult(new GateDerivedExactReadResult(GateDerivedExactReadStatus.Faulted, null, error.GetType().Name)); }
        }
        public GateDerivedRangePage ReadRange(GateDerivedRangeQuery query)
        {
            ArgumentNullException.ThrowIfNull(query); lock (_gate)
            {
                EnsureAlive(); Validate(query);
                ulong nextIndex = query.FirstAcquisitionIndex;
                if (query.Cursor is not null)
                {
                    if (!_cursors.Remove(query.Cursor, out CursorState? cursor) || !cursor.Matches(query))
                    { throw new ArgumentException("The cursor is unknown or incompatible.", nameof(query)); }
                    nextIndex = cursor.NextIndex;
                }
                var selected = new SortedDictionary<ulong, GateEvaluationBatch>();
                bool hasMore = false;
                lock (_owner!._gate)
                {
                    foreach ((GateDerivedBatchKey key, StoredBatch stored) in _owner._batches)
                    {
                        ulong index = key.TraceIdentity.AcquisitionIndex;
                        if (!_storedView.Membership.Contains(key) ||
                            key.TraceIdentity.Stream != query.Stream || index < nextIndex || index > query.LastAcquisitionIndex)
                        { continue; }
                        GateEvaluationEntry? entry = stored.Batch.Entries.FirstOrDefault(e => e.GateId == query.GateId);
                        if (entry is null && query.NoDataMode != GateDerivedNoDataMode.Include) { continue; }
                        selected[index] = stored.Batch;
                        if (selected.Count > query.PageSize) { selected.Remove(selected.Last().Key); hasMore = true; }
                    }
                }
                GateDerivedRangeItem[] page = selected.Values.Select(batch =>
                {
                    GateEvaluationEntry? entry = batch.Entries.FirstOrDefault(e => e.GateId == query.GateId);
                    return new GateDerivedRangeItem(batch.Trace.ConfirmedIdentity, entry?.Status,
                        entry?.Quality, entry?.Provenance, entry?.DiagnosticCode, entry?.CausalGateId,
                        entry?.CausalStatus,
                        entry is null ? GateDerivedNoDataReason.MissingResult : null,
                        entry is null ? [] : ProjectMeasures(batch, entry, query.Measures));
                }).ToArray();
                string? next = null;
                if (hasMore && page.Length != 0)
                { next = Guid.NewGuid().ToString("N"); _cursors.Add(next, new(query, checked(page[^1].TraceIdentity.AcquisitionIndex + 1))); }
                return new(Array.AsReadOnly(page), next);
            }
        }
        public void Dispose()
        {
            InMemoryGateDerivedStore? owner;
            lock (_gate) { owner = _owner; _owner = null; _cursors.Clear(); }
            owner?.ReleaseLease(View.ViewId);
        }
        private void EnsureAlive() { if (_owner is null) throw new ObjectDisposedException(nameof(ReadLease)); }
        private void Validate(GateDerivedRangeQuery query)
        {
            if (query.ViewId != View.ViewId || query.Stream.InspectionId != View.Manifest.InspectionId ||
                query.Stream.AcquisitionRunId != View.Manifest.RunId || query.FirstAcquisitionIndex > query.LastAcquisitionIndex ||
                !query.GateId.IsValid || query.PageSize is < 1 or > 256 || !Enum.IsDefined(query.NoDataMode) ||
                query.NoDataMode == GateDerivedNoDataMode.Unknown || query.Measures is null || query.Measures.Count == 0 ||
                query.Measures.Any(x => !Enum.IsDefined(x) || x == GateMeasureKind.Unknown))
            { throw new ArgumentException("The derived range query is invalid.", nameof(query)); }
        }
        private static IReadOnlyList<GateDerivedMeasureResult> ProjectMeasures(GateEvaluationBatch batch,
            GateEvaluationEntry entry, IReadOnlyList<GateMeasureKind> requested)
        {
            GateDerivedBatchKey batchKey = Key(batch);
            return Array.AsReadOnly(requested.Select(measure =>
            {
                bool declared = entry.RequestedMeasures.Contains(measure);
                double? value = measure switch
                {
                    GateMeasureKind.Amplitude => entry.AmplitudeCounts,
                    GateMeasureKind.DetectionTime => entry.DetectionTimeSeconds,
                    GateMeasureKind.TimeOfFlight => entry.TimeOfFlightSeconds,
                    GateMeasureKind.Status => null,
                    GateMeasureKind.Quality => null,
                    _ => null
                };
                bool typedValue = declared && measure is GateMeasureKind.Status or GateMeasureKind.Quality;
                if (!declared) { value = null; }
                GateDerivedNoDataReason? noData = declared && (value.HasValue || typedValue)
                    ? null : GateDerivedNoDataReason.MeasureUnavailable;
                GateDerivedUnit unit = measure switch
                {
                    GateMeasureKind.Amplitude => GateDerivedUnit.DigitalCounts,
                    GateMeasureKind.DetectionTime or GateMeasureKind.TimeOfFlight => GateDerivedUnit.Seconds,
                    GateMeasureKind.Status => GateDerivedUnit.StatusCode,
                    GateMeasureKind.Quality => GateDerivedUnit.QualityCode,
                    _ => GateDerivedUnit.Unknown
                };
                var layer = new GateDerivedLayerKey(batchKey, entry.GateId, entry.GateRevision, measure,
                    unit, entry.SignalDomain);
                return new GateDerivedMeasureResult(layer, value, entry.Status, entry.Quality, entry.Provenance,
                    noData, entry.CausalGateId, entry.CausalStatus);
            }).ToArray());
        }
        private sealed class CursorState
        {
            private readonly DerivedViewId _viewId;
            private readonly UtStreamIdentity _stream;
            private readonly ulong _first, _last;
            private readonly GateId _gateId;
            private readonly GateMeasureKind[] _measures;
            private readonly GateDerivedNoDataMode _mode;
            internal CursorState(GateDerivedRangeQuery query, ulong nextIndex)
            {
                _viewId = query.ViewId; _stream = query.Stream; _first = query.FirstAcquisitionIndex;
                _last = query.LastAcquisitionIndex; _gateId = query.GateId; _measures = query.Measures.ToArray();
                _mode = query.NoDataMode; NextIndex = nextIndex;
            }
            internal ulong NextIndex { get; }
            internal bool Matches(GateDerivedRangeQuery query) => query.ViewId == _viewId && query.Stream == _stream &&
                query.FirstAcquisitionIndex == _first && query.LastAcquisitionIndex == _last &&
                query.GateId == _gateId && query.NoDataMode == _mode && query.Measures.SequenceEqual(_measures);
        }
    }
}

internal sealed class GateDerivedStoreTestHooks
{
    internal Action? AfterReservation { get; init; }
    internal Action? AfterCopy { get; init; }
    internal Action? BeforePublish { get; init; }
    internal Action? BeforeActivation { get; init; }
    internal Action? BeforeInvalidation { get; init; }
    internal Action? BeforeReclaim { get; init; }
    internal Action? AfterActivationStaging { get; init; }
    internal Action? AfterInvalidationStaging { get; init; }
    internal Action? AfterReclaimStaging { get; init; }
}
