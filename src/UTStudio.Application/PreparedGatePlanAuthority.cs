using System.Collections.ObjectModel;
using System.Text;
using UTStudio.Contracts.Processing;
using UTStudio.Domain.Acquisition;
using UTStudio.Domain.Inspection;
using UTStudio.Domain.Processing;
using UTStudio.Processing.Core;

namespace UTStudio.Application;

internal enum GatePlanAuthorityPrepareStatus
{
    Unknown, Prepared, NotOperational, Closed, InvalidIdentity, Conflict, RevisionExhausted,
    GenerationExhausted, AlreadyPrepared, ValidationFailed, LedgerSlotCapacityExceeded,
    LedgerByteCapacityExceeded, LedgerEstimateOverflow
}

internal sealed record GatePlanAuthorityPreparedCommitOptions
{
    internal GatePlanAuthorityPreparedCommitOptions(int maximumRetainedLedgersPerInspection = 8,
        long maximumEstimatedLedgerBytesPerInspection = 64 * 1024,
        int terminalReservationTombstoneCapacity = 8, long estimatedLedgerFixedBytes = 128)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumRetainedLedgersPerInspection, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumEstimatedLedgerBytesPerInspection, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(terminalReservationTombstoneCapacity, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(estimatedLedgerFixedBytes, 1);
        MaximumRetainedLedgersPerInspection = maximumRetainedLedgersPerInspection;
        MaximumEstimatedLedgerBytesPerInspection = maximumEstimatedLedgerBytesPerInspection;
        TerminalReservationTombstoneCapacity = terminalReservationTombstoneCapacity;
        EstimatedLedgerFixedBytes = estimatedLedgerFixedBytes;
    }

    internal int MaximumRetainedLedgersPerInspection { get; }
    internal long MaximumEstimatedLedgerBytesPerInspection { get; }
    internal int TerminalReservationTombstoneCapacity { get; }
    internal long EstimatedLedgerFixedBytes { get; }
}

internal sealed record GatePlanAuthorityRetainedLedgerSeed(GateApplyLedgerSnapshot Ledger);

internal sealed record GatePlanAuthorityPrepareRequest(
    GatePlanAuthorityKey Authority,
    GatePlanRevision ExpectedRevision,
    GatePlanDraft Draft,
    AcquisitionRunId RunId,
    GateRunGeneration RunGeneration,
    GatePreparedReservationOwner Owner,
    GatePreparedOperationGeneration OperationGeneration,
    CompiledGatePlan? CompiledPlan = null);

internal readonly record struct GatePlanAuthorityPrepareResult
{
    private GatePlanAuthorityPrepareResult(GatePlanAuthorityPrepareStatus status,
        GatePreparedReservation reservation, GatePlanCapacityResource? capacityResource, long estimatedLedgerBytes,
        GateAuthorityGeneration preparedGeneration)
    {
        Status = status; Reservation = reservation; CapacityResource = capacityResource;
        EstimatedLedgerBytes = estimatedLedgerBytes; PreparedGeneration = preparedGeneration;
    }
    internal GatePlanAuthorityPrepareStatus Status { get; }
    internal GatePreparedReservation Reservation { get; }
    internal GatePlanCapacityResource? CapacityResource { get; }
    internal long EstimatedLedgerBytes { get; }
    internal GateAuthorityGeneration PreparedGeneration { get; }
    internal bool IsPrepared => Status == GatePlanAuthorityPrepareStatus.Prepared;
    internal static GatePlanAuthorityPrepareResult Prepared(GatePreparedReservation reservation, long bytes,
        GateAuthorityGeneration generation) => new(GatePlanAuthorityPrepareStatus.Prepared, reservation, null, bytes, generation);
    internal static GatePlanAuthorityPrepareResult Rejected(GatePlanAuthorityPrepareStatus status,
        GatePlanCapacityResource? resource = null) => new(status, default, resource, 0, default);
}

internal readonly record struct GatePlanAuthorityCommitResult
{
    internal GatePlanAuthorityCommitResult(GatePreparedReservationResult operation,
        GatePlanAuthorityDeferredPublication? publication)
    { Operation = operation; Publication = publication; }
    internal GatePreparedReservationResult Operation { get; }
    internal GatePlanAuthorityDeferredPublication? Publication { get; }
}

internal sealed class GatePlanAuthorityDeferredPublication
{
    private readonly ConfirmedAuthorityPublicationTicket _ticket;
    private int _publishAttempted;
    internal GatePlanAuthorityDeferredPublication(ConfirmedAuthorityPublicationTicket ticket) => _ticket = ticket;
    internal GatePlanAuthorityPublicationMetadata Metadata => _ticket.Metadata;
    internal bool TryGetPendingSnapshot(out GatePlanAuthoritySnapshot? snapshot) =>
        _ticket.TryGetPendingSnapshot(out snapshot);
    internal bool IsPayloadReleased => _ticket.IsPayloadReleased;
    internal bool Publish()
    {
        if (Interlocked.Exchange(ref _publishAttempted, 1) != 0) return false;
        return _ticket.Publish();
    }
}

internal readonly record struct GatePlanAuthorityPublicationMetadata(GatePlanAuthorityKey Authority,
    GatePlanRevision Revision, GateAuthorityGeneration Generation, GateAuthorityPublicationSequence Sequence);

internal enum ConfirmedAuthorityPublicationState
{ Prepared, ConfirmedPending, Claimed, Admitted, Discarded }

internal sealed class ConfirmedAuthorityPublicationTicket
{
    private readonly object _gate = new();
    private InMemoryGatePlanAuthorityOwner.AuthorityReader? _reader;
    private InMemoryGatePlanAuthorityOwner.Entry? _entry;
    private GatePlanAuthoritySnapshot? _snapshot;
    private ConfirmedAuthorityPublicationState _state = ConfirmedAuthorityPublicationState.Prepared;

    internal ConfirmedAuthorityPublicationTicket(InMemoryGatePlanAuthorityOwner.AuthorityReader reader,
        InMemoryGatePlanAuthorityOwner.Entry entry, GatePlanAuthoritySnapshot snapshot)
    {
        _reader = reader; _entry = entry; _snapshot = snapshot;
        Metadata = new(snapshot.Authority, snapshot.Committed.Revision, snapshot.Generation,
            snapshot.PublicationSequence);
    }

    internal GatePlanAuthorityPublicationMetadata Metadata { get; }
    internal ConfirmedAuthorityPublicationState State { get { lock (_gate) return _state; } }
    internal bool IsPayloadReleased { get { lock (_gate) return _reader is null && _entry is null && _snapshot is null; } }

    internal void Confirm()
    {
        lock (_gate)
        {
            if (_state != ConfirmedAuthorityPublicationState.Prepared) throw new InvalidOperationException();
            _state = ConfirmedAuthorityPublicationState.ConfirmedPending;
        }
    }

    internal bool Publish()
    {
        InMemoryGatePlanAuthorityOwner.AuthorityReader? reader;
        lock (_gate)
        {
            if (_state == ConfirmedAuthorityPublicationState.Admitted) return true;
            if (_state != ConfirmedAuthorityPublicationState.ConfirmedPending) return false;
            reader = _reader;
        }
        return reader is not null && reader.PublishPrepared(this);
    }

    internal bool TryClaim(out InMemoryGatePlanAuthorityOwner.Entry? entry,
        out GatePlanAuthoritySnapshot? snapshot)
    {
        lock (_gate)
        {
            if (_state != ConfirmedAuthorityPublicationState.ConfirmedPending)
            { entry = null; snapshot = null; return false; }
            _state = ConfirmedAuthorityPublicationState.Claimed;
            entry = _entry; snapshot = _snapshot;
            return entry is not null && snapshot is not null;
        }
    }

    internal void CompleteAdmission()
    {
        lock (_gate)
        {
            if (_state != ConfirmedAuthorityPublicationState.Claimed) throw new InvalidOperationException();
            _state = ConfirmedAuthorityPublicationState.Admitted;
            ReleasePayload();
        }
    }

    internal void DiscardPrepared()
    {
        lock (_gate)
        {
            if (_state != ConfirmedAuthorityPublicationState.Prepared) return;
            _state = ConfirmedAuthorityPublicationState.Discarded;
            ReleasePayload();
        }
    }

    internal bool TryGetPendingSnapshot(out GatePlanAuthoritySnapshot? snapshot)
    {
        lock (_gate)
        {
            snapshot = _state is ConfirmedAuthorityPublicationState.Prepared or
                ConfirmedAuthorityPublicationState.ConfirmedPending ? _snapshot : null;
            return snapshot is not null;
        }
    }

    private void ReleasePayload()
    {
        _reader = null; _entry = null; _snapshot = null;
    }
}

internal readonly record struct GatePlanGeometryItem(
    GateId GateId,
    GateRevision GateRevision,
    GateTimeReferenceKind StartReferenceKind,
    GateId DependencyGateId,
    double StartOffsetSeconds,
    double DurationSeconds,
    double? ThresholdNormalized);

internal sealed class GatePlanGeometrySnapshot
{
    private readonly ReadOnlyCollection<GatePlanGeometryItem> _gates;
    internal GatePlanGeometrySnapshot(GatePlanId planId, GatePlanRevision revision,
        IEnumerable<GatePlanGeometryItem> gates)
    {
        GatePlanGeometryItem[] captured = gates.ToArray();
        if (!planId.IsValid || !revision.IsValid || captured.Length == 0)
            throw new ArgumentException("Valid nonempty gate-plan geometry is required.");
        PlanId = planId; Revision = revision; _gates = Array.AsReadOnly(captured);
    }
    internal GatePlanId PlanId { get; }
    internal GatePlanRevision Revision { get; }
    internal IReadOnlyList<GatePlanGeometryItem> Gates => _gates;
}

internal sealed record GatePlanAuthorityInstalledState(
    GatePlanAuthoritySnapshot Authority,
    GatePlanGeometrySnapshot Geometry,
    GateApplyLedgerSnapshot Ledger);

internal sealed record GatePlanAuthorityPreparedCommitDiagnostics(
    bool Operational, bool Closed, int PreparedReservations, int ProvisionalLedgerSlots,
    long ProvisionalLedgerBytes, int RetainedLedgerSlots, long RetainedLedgerBytes,
    int ConfirmedPublicationsPending, int TerminalReservationTombstones,
    int TerminalReservationTombstoneCapacity);

internal sealed record GateLedgerLifecycleDiagnostics(
    bool IsStableAfterDrain, int Active, int Disposing, int Disposed,
    int PendingObservers, int PendingLeases, int PendingTombstones);

internal enum GateHistoricalSealResolutionStatus
{ TargetSelected, NoDeferredTarget, Closed, IdentityMismatch }

internal readonly record struct GateHistoricalSealResolution(
    GateHistoricalSealResolutionStatus Status,
    GatePlanAuthorityKey Authority,
    GatePlanRevision PlanRevision)
{
    internal bool HasTarget => Status == GateHistoricalSealResolutionStatus.TargetSelected;
}

internal sealed class InMemoryGatePlanPreparedCommitOwner
{
    private readonly InMemoryGatePlanAuthorityOwner.AuthorityReader _reader;
    internal InMemoryGatePlanPreparedCommitOwner(InMemoryGatePlanAuthorityOwner.AuthorityReader reader) =>
        _reader = reader;
    internal GatePlanAuthorityPrepareResult Prepare(GatePlanAuthorityPrepareRequest request) =>
        _reader.PrepareAuthorityCommit(request);
    internal GatePlanAuthorityCommitResult Commit(GatePreparedReservation reservation,
        GatePreparedReservationOwner owner, GatePreparedOperationGeneration generation) =>
        _reader.CommitAuthority(reservation, owner, generation);
    internal GatePreparedReservationResult Abort(GatePreparedReservation reservation,
        GatePreparedReservationOwner owner, GatePreparedOperationGeneration generation) =>
        _reader.AbortAuthority(reservation, owner, generation);
    internal GatePlanAuthorityPreparedCommitDiagnostics Diagnostics => _reader.PreparedCommitDiagnostics;
    internal GatePlanAuthorityInstalledState? ReadInstalled(GatePlanAuthorityKey authority) =>
        _reader.ReadInstalled(authority);
    internal IReadOnlyList<GateApplyLedgerSnapshot> ReadRetainedLedgers(InspectionId inspectionId) =>
        _reader.ReadRetainedLedgers(inspectionId);
    internal long EstimateLedgerBytes(GateApplyLedgerSnapshot ledger) => _reader.EstimateLedgerBytesForDiagnostics(ledger);
    internal bool TryAdvanceLedger(GatePlanAuthorityKey authority, GatePlanRevision revision,
        Func<GateApplyLedgerSnapshot, GateApplyLedgerSnapshot> transition) =>
        _reader.TryAdvanceLedger(authority, revision, transition);
    internal GateHistoricalSealResolution ResolveHistoricalAtSeal(InspectionId inspectionId,
        AcquisitionRunId runId, GateRunGeneration runGeneration) =>
        _reader.ResolveHistoricalAtSeal(inspectionId, runId, runGeneration);
    internal GateLedgerLifecycleDiagnostics ReadLifecycleDiagnostics(bool stableAfterDrain = false) =>
        _reader.ReadLedgerLifecycleDiagnostics(stableAfterDrain);
    internal void BeginLedgerDisposal() => _reader.BeginLedgerDisposal();
    internal void CompleteLedgerDisposal() => _reader.CompleteLedgerDisposal();
    internal bool CompleteHistoricalWithoutData(GatePlanAuthorityKey authority, GatePlanRevision revision) =>
        _reader.TryAdvanceLedger(authority, revision, current => GateApplyLedgerSnapshot.Create(
            current.InspectionId, current.RunId, current.RunGeneration, current.PlanId, current.PlanRevision,
            current.Commit, current.LiveEpoch, GateHistoricalAdmissionState.NotApplicable,
            GateHistoricalExecutionState.NotApplicable, GateCandidateCoverageState.NotApplicable,
            GateActivationState.NotApplicable, GateCandidateRetirementState.NotApplicable,
            GatePreviousViewRetirementState.NotApplicable, current.CandidateOwnership, current.PrimaryCause,
            current.SecondaryCauses, current.Lifecycle, GateApplyLedgerPendingResource.None));
}

public sealed partial class InMemoryGatePlanAuthorityOwner
{
    internal sealed partial class AuthorityReader
    {
        private GatePlanAuthorityPreparedCommitOptions _preparedOptions = null!;
        private readonly GatePlanCompiler _preparedCompiler = new();
        private readonly Dictionary<InspectionId, InspectionLedgerBudget> _ledgerBudgets = [];
        private PreparedAuthorityCommit? _preparedAuthority;
        private readonly Queue<ConfirmedAuthorityPublicationTicket> _confirmedPublications = [];
        private ulong _lastAdmittedPublicationGeneration;
        private PreparedAuthorityTombstone[] _preparedTombstones = [];
        private int _preparedTombstoneCount;
        private bool _preparedOperational;

        internal void InitializePreparedCommits(GatePlanAuthorityPreparedCommitOptions options,
            IEnumerable<GatePlanAuthorityRetainedLedgerSeed>? retainedLedgers)
        {
            _preparedOptions = options ?? throw new ArgumentNullException(nameof(options));
            _preparedTombstones = new PreparedAuthorityTombstone[options.TerminalReservationTombstoneCapacity];
            foreach (GatePlanAuthorityRetainedLedgerSeed seed in retainedLedgers ?? [])
            {
                ArgumentNullException.ThrowIfNull(seed);
                GateApplyLedgerSnapshot ledger = seed.Ledger ?? throw new ArgumentException("A seed ledger is required.");
                long bytes = EstimateLedgerBytes(ledger);
                InspectionLedgerBudget budget = Budget(ledger.InspectionId);
                if (budget.Retained.Count >= options.MaximumRetainedLedgersPerInspection ||
                    checked(budget.RetainedBytes + bytes) > options.MaximumEstimatedLedgerBytesPerInspection)
                    throw new ArgumentException("Seed ledgers exceed the configured budget.", nameof(retainedLedgers));
                budget.Retained.Add(new(ledger, bytes));
                budget.RetainedBytes += bytes;
            }
        }

        internal void EnablePreparedCommits()
        { lock (_gate) _preparedOperational = true; }

        internal GatePlanAuthorityPrepareResult PrepareAuthorityCommit(GatePlanAuthorityPrepareRequest request)
        {
            if (request is null || !request.Authority.IsValid || !request.ExpectedRevision.IsValid ||
                request.Draft is null || !request.RunId.IsValid || !request.RunGeneration.IsValid ||
                !request.Owner.IsValid || !request.OperationGeneration.IsValid ||
                request.Draft.Authority != request.Authority || request.Draft.BaseRevision != request.ExpectedRevision)
                return GatePlanAuthorityPrepareResult.Rejected(GatePlanAuthorityPrepareStatus.InvalidIdentity);
            lock (_gate)
            {
                if (_closing) return GatePlanAuthorityPrepareResult.Rejected(GatePlanAuthorityPrepareStatus.Closed);
                if (!_preparedOperational)
                    return GatePlanAuthorityPrepareResult.Rejected(GatePlanAuthorityPrepareStatus.NotOperational);
                if (_preparedAuthority is not null)
                    return GatePlanAuthorityPrepareResult.Rejected(GatePlanAuthorityPrepareStatus.AlreadyPrepared);
                if (!_entries.TryGetValue(request.Authority, out Entry? entry))
                    return GatePlanAuthorityPrepareResult.Rejected(GatePlanAuthorityPrepareStatus.InvalidIdentity);
                GatePlanAuthoritySnapshot current = entry.Current;
                if (current.Committed.InspectionId != request.Authority.InspectionId ||
                    current.Committed.Plan.Id != request.Authority.PlanId)
                    return GatePlanAuthorityPrepareResult.Rejected(GatePlanAuthorityPrepareStatus.InvalidIdentity);
                if (current.Committed.Revision != request.ExpectedRevision)
                    return GatePlanAuthorityPrepareResult.Rejected(GatePlanAuthorityPrepareStatus.Conflict);
                if (request.ExpectedRevision.Value == uint.MaxValue)
                    return GatePlanAuthorityPrepareResult.Rejected(GatePlanAuthorityPrepareStatus.RevisionExhausted);
                if (_generation == ulong.MaxValue)
                    return GatePlanAuthorityPrepareResult.Rejected(GatePlanAuthorityPrepareStatus.GenerationExhausted);

                var nextRevision = new GatePlanRevision(request.ExpectedRevision.Value + 1);
                CompiledGatePlan compiled;
                if (request.CompiledPlan is { } supplied)
                {
                    if (supplied.Source.Id != request.Authority.PlanId || supplied.Source.Revision != nextRevision ||
                        supplied.Source.SchemaVersion != request.Draft.SchemaVersion ||
                        supplied.Source.AlgorithmVersion != request.Draft.AlgorithmVersion ||
                        !supplied.Source.Gates.SequenceEqual(request.Draft.Gates))
                        return GatePlanAuthorityPrepareResult.Rejected(GatePlanAuthorityPrepareStatus.InvalidIdentity);
                    compiled = supplied;
                }
                else
                {
                    var source = new GatePlan(request.Authority.PlanId, nextRevision, request.Draft.SchemaVersion,
                        request.Draft.AlgorithmVersion, request.Draft.Gates);
                    GatePlanCompilationResult compilation = _preparedCompiler.Compile(source);
                    if (!compilation.IsCompiled)
                        return GatePlanAuthorityPrepareResult.Rejected(GatePlanAuthorityPrepareStatus.ValidationFailed);
                    compiled = compilation.CompiledPlan!;
                }
                var committed = new CommittedGatePlanSnapshot(request.Authority.InspectionId, compiled);
                // GateAuthorityGeneration is global to this owner. The single global Prepared reservation mirrors
                // the future ApplicationCommitCoordinator, which will execute one aggregate Apply per turn.
                var nextGeneration = new GateAuthorityGeneration(_generation + 1);
                var authoritySnapshot = new GatePlanAuthoritySnapshot(committed, nextGeneration,
                    new(nextGeneration.Value));
                GatePlanGeometrySnapshot geometry = BuildGeometry(compiled);
                GateApplyLedgerSnapshot ledger = GateApplyLedgerSnapshot.Create(request.Authority.InspectionId,
                    request.RunId, request.RunGeneration, request.Authority.PlanId, nextRevision,
                    GateApplyCommitState.Accepted, GateLiveEpochState.Active,
                    GateHistoricalAdmissionState.Deferred, GateHistoricalExecutionState.Dormant,
                    GateCandidateCoverageState.Dormant, GateActivationState.Dormant,
                    GateCandidateRetirementState.Dormant, GatePreviousViewRetirementState.Dormant);
                long bytes;
                try { bytes = EstimateLedgerBytes(ledger); }
                catch (OverflowException)
                { return GatePlanAuthorityPrepareResult.Rejected(GatePlanAuthorityPrepareStatus.LedgerEstimateOverflow); }
                bool addBudget = !_ledgerBudgets.TryGetValue(request.Authority.InspectionId,
                    out InspectionLedgerBudget? budget);
                budget ??= new();
                RetainedLedger[] evictions = PlanEvictions(budget, bytes, out GatePlanAuthorityPrepareStatus rejection);
                if (rejection != GatePlanAuthorityPrepareStatus.Prepared)
                {
                    return GatePlanAuthorityPrepareResult.Rejected(rejection,
                        rejection == GatePlanAuthorityPrepareStatus.LedgerSlotCapacityExceeded ?
                            GatePlanCapacityResource.LedgerRevisions : GatePlanCapacityResource.LedgerBytes);
                }
                var reservation = new GatePreparedReservation(new(Guid.NewGuid()), request.Owner,
                    request.OperationGeneration, GatePreparedReservationState.Prepared);
                var ticket = new ConfirmedAuthorityPublicationTicket(this, entry, authoritySnapshot);
                var publication = new GatePlanAuthorityDeferredPublication(ticket);
                var retained = new RetainedLedger(ledger, bytes);
                var prepared = new PreparedAuthorityCommit(reservation, request.Authority, entry,
                    authoritySnapshot, geometry, ledger, retained, budget, evictions, publication,
                    ticket);

                // Everything that may allocate is completed before the first semantic reservation mutation.
                budget.Retained.EnsureCapacity(budget.Retained.Count + 1);
                _confirmedPublications.EnsureCapacity(_confirmedPublications.Count + 1);
                if (addBudget) _ledgerBudgets.EnsureCapacity(_ledgerBudgets.Count + 1);

                if (addBudget) _ledgerBudgets.Add(request.Authority.InspectionId, budget);
                foreach (RetainedLedger eviction in evictions) eviction.EvictionReserved = true;
                budget.ProvisionalSlots = 1;
                budget.ProvisionalBytes = bytes;
                _preparedAuthority = prepared;
                return GatePlanAuthorityPrepareResult.Prepared(reservation, bytes, nextGeneration);
            }
        }

        internal GatePlanAuthorityCommitResult CommitAuthority(GatePreparedReservation reservation,
            GatePreparedReservationOwner owner, GatePreparedOperationGeneration generation)
        {
            lock (_gate)
            {
                GatePreparedReservation current = CurrentPreparedOrTombstone(reservation.Id);
                GatePreparedReservationResult classified = ClassifyEnvelope(
                    reservation, current, owner, generation, GatePreparedReservationOperation.Commit);
                if (classified.Status != GatePreparedReservationResultStatus.Committed)
                    return new(classified, null);
                PreparedAuthorityCommit prepared = _preparedAuthority!;
                foreach (RetainedLedger eviction in prepared.Evictions)
                {
                    prepared.Budget.Retained.Remove(eviction);
                    prepared.Budget.RetainedBytes -= eviction.EstimatedBytes;
                }
                prepared.Budget.ProvisionalSlots = 0; prepared.Budget.ProvisionalBytes = 0;
                prepared.Budget.Retained.Add(prepared.RetainedLedger);
                prepared.Budget.RetainedBytes += prepared.RetainedLedger.EstimatedBytes;
                prepared.Entry.InstallPrepared(prepared.AuthoritySnapshot, prepared.Geometry, prepared.Ledger);
                _generation = prepared.AuthoritySnapshot.Generation.Value;
                _preparedAuthority = null;
                prepared.Ticket.Confirm();
                _confirmedPublications.Enqueue(prepared.Ticket);
                StorePreparedTombstone(prepared.Reservation, GatePreparedReservationState.Committed);
                return new(classified, prepared.Publication);
            }
        }

        internal GatePreparedReservationResult AbortAuthority(GatePreparedReservation reservation,
            GatePreparedReservationOwner owner, GatePreparedOperationGeneration generation)
        {
            lock (_gate)
            {
                GatePreparedReservation current = CurrentPreparedOrTombstone(reservation.Id);
                GatePreparedReservationResult classified = ClassifyEnvelope(
                    reservation, current, owner, generation, GatePreparedReservationOperation.Abort);
                if (classified.Status == GatePreparedReservationResultStatus.Aborted)
                    AbortPreparedAuthorityUnderGate(reservation.Id);
                return classified;
            }
        }

        internal GatePlanAuthorityPreparedCommitDiagnostics PreparedCommitDiagnostics
        {
            get
            {
                lock (_gate)
                {
                    return new(_preparedOperational, _closing, _preparedAuthority is null ? 0 : 1,
                        _ledgerBudgets.Values.Sum(x => x.ProvisionalSlots),
                        _ledgerBudgets.Values.Sum(x => x.ProvisionalBytes),
                        _ledgerBudgets.Values.Sum(x => x.Retained.Count),
                        _ledgerBudgets.Values.Sum(x => x.RetainedBytes),
                        _confirmedPublications.Count, _preparedTombstoneCount, _preparedTombstones.Length);
                }
            }
        }

        internal GatePlanAuthorityInstalledState? ReadInstalled(GatePlanAuthorityKey authority)
        {
            lock (_gate)
            {
                if (!_entries.TryGetValue(authority, out Entry? entry) ||
                    entry.Geometry is null || entry.Ledger is null) return null;
                return new(entry.Current, entry.Geometry, entry.Ledger);
            }
        }

        internal IReadOnlyList<GateApplyLedgerSnapshot> ReadRetainedLedgers(InspectionId inspectionId)
        {
            lock (_gate)
                return _ledgerBudgets.TryGetValue(inspectionId, out InspectionLedgerBudget? budget) ?
                    budget.Retained.Select(item => item.Ledger).ToArray() : [];
        }

        internal long EstimateLedgerBytesForDiagnostics(GateApplyLedgerSnapshot ledger)
        {
            ArgumentNullException.ThrowIfNull(ledger);
            lock (_gate) return EstimateLedgerBytes(ledger);
        }

        internal GateLedgerLifecycleDiagnostics ReadLedgerLifecycleDiagnostics(bool stableAfterDrain)
        {
            lock (_gate)
            {
                GateApplyLedgerSnapshot[] ledgers = _ledgerBudgets.Values
                    .SelectMany(budget => budget.Retained.Select(item => item.Ledger)).ToArray();
                return new(stableAfterDrain,
                    ledgers.Count(x => x.Lifecycle == GateApplyLedgerLifecycleState.Active),
                    ledgers.Count(x => x.Lifecycle == GateApplyLedgerLifecycleState.Disposing),
                    ledgers.Count(x => x.Lifecycle == GateApplyLedgerLifecycleState.Disposed),
                    ledgers.Count(x => x.PendingResources.HasFlag(GateApplyLedgerPendingResource.Observer)),
                    ledgers.Count(x => x.PendingResources.HasFlag(GateApplyLedgerPendingResource.Lease)),
                    ledgers.Count(x => x.PendingResources.HasFlag(GateApplyLedgerPendingResource.Tombstone)));
            }
        }

        internal void BeginLedgerDisposal()
        {
            lock (_gate)
            {
                _preparedOperational = false;
                if (_preparedAuthority is { } prepared) AbortPreparedAuthorityUnderGate(prepared.Reservation.Id);
                foreach (InspectionLedgerBudget budget in _ledgerBudgets.Values)
                    foreach (RetainedLedger retained in budget.Retained)
                    {
                        if (retained.Ledger.Lifecycle != GateApplyLedgerLifecycleState.Active) continue;
                        GateApplyLedgerSnapshot current = retained.Ledger;
                        GateApplyLedgerSnapshot next = CopyLifecycle(current, GateApplyLedgerLifecycleState.Disposing);
                        EnsureLifecycleTransition(current, next);
                        retained.Ledger = next;
                        ReplaceInstalledLedgerUnderGate(next);
                    }
            }
        }

        internal void CompleteLedgerDisposal()
        {
            lock (_gate)
            {
                foreach (InspectionLedgerBudget budget in _ledgerBudgets.Values)
                    foreach (RetainedLedger retained in budget.Retained)
                    {
                        if (retained.Ledger.Lifecycle == GateApplyLedgerLifecycleState.Disposed) continue;
                        if (retained.Ledger.Lifecycle != GateApplyLedgerLifecycleState.Disposing ||
                            retained.Ledger.PendingResources != GateApplyLedgerPendingResource.None ||
                            !IsLedgerEvictionEligible(retained.Ledger))
                            throw new ApplicationCommitInvariantException(
                                $"A gate ledger still owns material resources at disposal completion: " +
                                $"plan={retained.Ledger.PlanRevision.Value}, pending={retained.Ledger.PendingResources}, " +
                                $"live={retained.Ledger.LiveEpoch}, admission={retained.Ledger.HistoricalAdmission}, " +
                                $"execution={retained.Ledger.HistoricalExecution}, coverage={retained.Ledger.CandidateCoverage}, " +
                                $"activation={retained.Ledger.Activation}, retirement={retained.Ledger.CandidateRetirement}, " +
                                $"previous={retained.Ledger.PreviousViewRetirement}.");
                        GateApplyLedgerSnapshot current = retained.Ledger;
                        GateApplyLedgerSnapshot next = CopyLifecycle(current, GateApplyLedgerLifecycleState.Disposed);
                        EnsureLifecycleTransition(current, next);
                        retained.Ledger = next;
                        ReplaceInstalledLedgerUnderGate(next);
                    }
            }
        }

        private static GateApplyLedgerSnapshot CopyLifecycle(GateApplyLedgerSnapshot source,
            GateApplyLedgerLifecycleState lifecycle)
        {
            GateLedgerCause disposal = new(GateLedgerStage.Lifecycle, GateLedgerCauseKind.Disposed);
            bool hasLifecycle = source.PrimaryCause?.Stage == GateLedgerStage.Lifecycle ||
                source.SecondaryCauses.Any(x => x.Stage == GateLedgerStage.Lifecycle);
            GateLedgerCause? primary = source.PrimaryCause ?? disposal;
            IEnumerable<GateLedgerCause> secondary = source.PrimaryCause is null || hasLifecycle ?
                source.SecondaryCauses : [.. source.SecondaryCauses, disposal];
            return GateApplyLedgerSnapshot.Create(
                source.InspectionId, source.RunId, source.RunGeneration, source.PlanId, source.PlanRevision,
                source.Commit, source.LiveEpoch, source.HistoricalAdmission, source.HistoricalExecution,
                source.CandidateCoverage, source.Activation, source.CandidateRetirement,
                source.PreviousViewRetirement, source.CandidateOwnership, primary,
                secondary, lifecycle, source.PendingResources);
        }

        private static void EnsureLifecycleTransition(GateApplyLedgerSnapshot current,
            GateApplyLedgerSnapshot next)
        {
            GateApplyLedgerTransitionResult validation = GateApplyLedgerTransitionResult.Validate(current, next);
            if (validation.Status != GateApplyLedgerTransitionStatus.Valid)
                throw new ApplicationCommitInvariantException(
                    $"Invalid lifecycle transition {validation.Status} at {validation.Stage}.");
        }

        private void ReplaceInstalledLedgerUnderGate(GateApplyLedgerSnapshot ledger)
        {
            if (_entries.TryGetValue(new(ledger.InspectionId, ledger.PlanId), out Entry? entry) &&
                entry.Ledger?.PlanRevision == ledger.PlanRevision)
                entry.ReplaceLedger(ledger);
        }

        internal bool TryAdvanceLedger(GatePlanAuthorityKey authority, GatePlanRevision revision,
            Func<GateApplyLedgerSnapshot, GateApplyLedgerSnapshot> transition)
        {
            ArgumentNullException.ThrowIfNull(transition);
            lock (_gate)
            {
                if (_closing || !_entries.TryGetValue(authority, out Entry? entry) ||
                    !_ledgerBudgets.TryGetValue(authority.InspectionId, out InspectionLedgerBudget? budget))
                    return false;
                RetainedLedger? retained = budget.Retained.SingleOrDefault(item =>
                    item.Ledger.PlanId == authority.PlanId && item.Ledger.PlanRevision == revision);
                if (retained is null) return false;
                GateApplyLedgerSnapshot current = retained.Ledger;
                GateApplyLedgerSnapshot next = transition(current) ??
                    throw new ApplicationCommitInvariantException("A ledger transition returned null.");
                GateApplyLedgerTransitionResult validation = GateApplyLedgerTransitionResult.Validate(current, next);
                if (validation.Status != GateApplyLedgerTransitionStatus.Valid)
                    throw new ApplicationCommitInvariantException(
                        $"Invalid ledger transition {validation.Status} at {validation.Stage}.");
                long bytes = EstimateLedgerBytes(next);
                long adjusted = checked(budget.RetainedBytes - retained.EstimatedBytes + bytes);
                if (adjusted > _preparedOptions.MaximumEstimatedLedgerBytesPerInspection)
                    throw new ApplicationCommitInvariantException("A retained ledger transition exceeded its byte budget.");
                retained.Ledger = next; retained.EstimatedBytes = bytes;
                budget.RetainedBytes = adjusted;
                if (entry.Ledger is { } installed && installed.PlanId == authority.PlanId &&
                    installed.PlanRevision == revision)
                    entry.ReplaceLedger(next);
                return true;
            }
        }

        internal GateHistoricalSealResolution ResolveHistoricalAtSeal(InspectionId inspectionId,
            AcquisitionRunId runId, GateRunGeneration runGeneration)
        {
            if (!inspectionId.IsValid || !runId.IsValid || !runGeneration.IsValid)
                return new(GateHistoricalSealResolutionStatus.IdentityMismatch, default, default);
            lock (_gate)
            {
                if (_closing) return new(GateHistoricalSealResolutionStatus.Closed, default, default);
                if (!_ledgerBudgets.TryGetValue(inspectionId, out InspectionLedgerBudget? budget))
                    return new(GateHistoricalSealResolutionStatus.NoDeferredTarget, default, default);
                RetainedLedger[] deferred = budget.Retained.Where(item =>
                    item.Ledger.RunId == runId && item.Ledger.RunGeneration == runGeneration &&
                    item.Ledger.Commit == GateApplyCommitState.Accepted &&
                    item.Ledger.HistoricalAdmission == GateHistoricalAdmissionState.Deferred).ToArray();
                if (deferred.Length == 0)
                    return new(GateHistoricalSealResolutionStatus.NoDeferredTarget, default, default);
                RetainedLedger target = deferred[^1];
                var staged = new (RetainedLedger Retained, GateApplyLedgerSnapshot Next)[deferred.Length];
                for (int index = 0; index < deferred.Length; index++)
                {
                    GateApplyLedgerSnapshot current = deferred[index].Ledger;
                    bool selected = ReferenceEquals(deferred[index], target);
                    GateApplyLedgerSnapshot next = selected ? CopySealLedger(current,
                        GateLiveEpochState.Drained) : CopySealLedger(current, GateLiveEpochState.Drained,
                        GateHistoricalAdmissionState.Superseded, GateHistoricalExecutionState.NotApplicable,
                        GateCandidateCoverageState.NotApplicable, GateActivationState.NotApplicable,
                        GateCandidateRetirementState.NotApplicable, GatePreviousViewRetirementState.NotApplicable,
                        new(GateLedgerStage.HistoricalAdmission, GateLedgerCauseKind.Superseded));
                    GateApplyLedgerTransitionResult validation = GateApplyLedgerTransitionResult.Validate(current, next);
                    if (validation.Status != GateApplyLedgerTransitionStatus.Valid)
                        throw new ApplicationCommitInvariantException(
                            $"Invalid seal ledger transition {validation.Status} at {validation.Stage}.");
                    staged[index] = (deferred[index], next);
                }
                GateApplyLedgerSnapshot chosen = target.Ledger;
                var authority = new GatePlanAuthorityKey(inspectionId, chosen.PlanId);
                if (!_entries.TryGetValue(authority, out Entry? installed) ||
                    installed.Current.Committed.Revision != chosen.PlanRevision)
                    throw new ApplicationCommitInvariantException(
                        "The deferred historical target is not the installed authority revision.");
                foreach ((RetainedLedger retained, GateApplyLedgerSnapshot next) in staged)
                {
                    retained.Ledger = next;
                    if (_entries.TryGetValue(new(inspectionId, next.PlanId), out Entry? entry) &&
                        entry.Ledger?.PlanRevision == next.PlanRevision)
                        entry.ReplaceLedger(next);
                }
                return new(GateHistoricalSealResolutionStatus.TargetSelected, authority, chosen.PlanRevision);
            }
        }

        private static GateApplyLedgerSnapshot CopySealLedger(GateApplyLedgerSnapshot source,
            GateLiveEpochState live, GateHistoricalAdmissionState? admission = null,
            GateHistoricalExecutionState? execution = null, GateCandidateCoverageState? coverage = null,
            GateActivationState? activation = null, GateCandidateRetirementState? retirement = null,
            GatePreviousViewRetirementState? previous = null, GateLedgerCause? primary = null) =>
            GateApplyLedgerSnapshot.Create(source.InspectionId, source.RunId, source.RunGeneration,
                source.PlanId, source.PlanRevision, source.Commit, live,
                admission ?? source.HistoricalAdmission, execution ?? source.HistoricalExecution,
                coverage ?? source.CandidateCoverage, activation ?? source.Activation,
                retirement ?? source.CandidateRetirement, previous ?? source.PreviousViewRetirement,
                source.CandidateOwnership, primary ?? source.PrimaryCause, source.SecondaryCauses,
                source.Lifecycle, source.PendingResources);

        internal bool PublishPrepared(ConfirmedAuthorityPublicationTicket requested)
        {
            List<Entry.Subscription> drains;
            lock (_gate)
            {
                if (requested.Metadata.Generation.Value <= _lastAdmittedPublicationGeneration) return true;
                bool found = false;
                drains = [];
                while (_confirmedPublications.TryPeek(out ConfirmedAuthorityPublicationTicket? pending) &&
                    pending.Metadata.Generation.Value <= requested.Metadata.Generation.Value)
                {
                    _confirmedPublications.Dequeue();
                    if (!pending.TryClaim(out Entry? entry, out GatePlanAuthoritySnapshot? snapshot)) continue;
                    drains.AddRange(entry!.EnqueueDeferred(snapshot!));
                    pending.CompleteAdmission();
                    _lastAdmittedPublicationGeneration = pending.Metadata.Generation.Value;
                    if (ReferenceEquals(pending, requested)) found = true;
                }
                if (!found) return false;
            }
            foreach (Entry.Subscription drain in drains) drain.Schedule();
            return true;
        }

        private GatePreparedReservationResult ClassifyEnvelope(GatePreparedReservation supplied,
            GatePreparedReservation current, GatePreparedReservationOwner owner,
            GatePreparedOperationGeneration generation, GatePreparedReservationOperation operation)
        {
            GatePreparedReservationResult result = GatePreparedReservationResult.Classify(current, owner, generation, operation);
            if (result.Status == GatePreparedReservationResultStatus.OwnerOrGenerationMismatch || !current.IsValid)
                return result;
            return supplied.Owner != current.Owner || supplied.Generation != current.Generation ?
                GatePreparedReservationResult.Classify(current, supplied.Owner, supplied.Generation, operation) : result;
        }

        private GatePreparedReservation CurrentPreparedOrTombstone(GatePreparedReservationId id)
        {
            if (!id.IsValid) return default;
            if (_preparedAuthority is { } prepared && prepared.Reservation.Id == id) return prepared.Reservation;
            for (int index = 0; index < _preparedTombstoneCount; index++)
                if (_preparedTombstones[index].Id == id) return _preparedTombstones[index].Snapshot();
            return default;
        }

        private void AbortPreparedAuthorityUnderGate(GatePreparedReservationId id)
        {
            PreparedAuthorityCommit? prepared = _preparedAuthority;
            if (prepared is null || prepared.Reservation.Id != id) return;
            prepared.Ticket.DiscardPrepared();
            foreach (RetainedLedger eviction in prepared.Evictions) eviction.EvictionReserved = false;
            prepared.Budget.ProvisionalSlots = 0; prepared.Budget.ProvisionalBytes = 0;
            RemoveEmptyBudget(prepared.Authority.InspectionId, prepared.Budget);
            _preparedAuthority = null;
            StorePreparedTombstone(prepared.Reservation, GatePreparedReservationState.Aborted);
        }

        private void ClosePreparedCommitsUnderGate()
        {
            if (_preparedAuthority is { } prepared) AbortPreparedAuthorityUnderGate(prepared.Reservation.Id);
            _preparedOperational = false;
        }

        private List<Entry.Subscription> AdmitConfirmedPublicationsUnderGate()
        {
            var drains = new List<Entry.Subscription>();
            while (_confirmedPublications.TryDequeue(out ConfirmedAuthorityPublicationTicket? pending))
            {
                if (!pending.TryClaim(out Entry? entry, out GatePlanAuthoritySnapshot? snapshot)) continue;
                drains.AddRange(entry!.EnqueueDeferred(snapshot!));
                pending.CompleteAdmission();
                _lastAdmittedPublicationGeneration = pending.Metadata.Generation.Value;
            }
            return drains;
        }

        private void ReleaseRetainedLedgersAfterCloseUnderGate() => _ledgerBudgets.Clear();

        private void StorePreparedTombstone(GatePreparedReservation reservation, GatePreparedReservationState state)
        {
            if (_preparedTombstoneCount == _preparedTombstones.Length)
            {
                Array.Copy(_preparedTombstones, 1, _preparedTombstones, 0, _preparedTombstones.Length - 1);
                _preparedTombstoneCount--;
            }
            _preparedTombstones[_preparedTombstoneCount++] = new(
                reservation.Id, reservation.Owner, reservation.Generation, state);
        }

        private InspectionLedgerBudget Budget(InspectionId inspectionId)
        {
            if (_ledgerBudgets.TryGetValue(inspectionId, out InspectionLedgerBudget? budget)) return budget;
            budget = new(); _ledgerBudgets.Add(inspectionId, budget); return budget;
        }

        private void RemoveEmptyBudget(InspectionId inspectionId, InspectionLedgerBudget budget)
        {
            if (budget.Retained.Count == 0 && budget.ProvisionalSlots == 0) _ledgerBudgets.Remove(inspectionId);
        }

        private RetainedLedger[] PlanEvictions(InspectionLedgerBudget budget, long newBytes,
            out GatePlanAuthorityPrepareStatus status)
        {
            int slots = budget.Retained.Count + budget.ProvisionalSlots + 1;
            long bytes;
            try { bytes = checked(budget.RetainedBytes + budget.ProvisionalBytes + newBytes); }
            catch (OverflowException)
            { status = GatePlanAuthorityPrepareStatus.LedgerEstimateOverflow; return []; }
            var evictions = new List<RetainedLedger>();
            foreach (RetainedLedger candidate in budget.Retained)
            {
                if (slots <= _preparedOptions.MaximumRetainedLedgersPerInspection &&
                    bytes <= _preparedOptions.MaximumEstimatedLedgerBytesPerInspection) break;
                if (!candidate.Eligible || candidate.EvictionReserved) continue;
                evictions.Add(candidate); slots--; bytes -= candidate.EstimatedBytes;
            }
            if (slots > _preparedOptions.MaximumRetainedLedgersPerInspection)
            { status = GatePlanAuthorityPrepareStatus.LedgerSlotCapacityExceeded; return []; }
            if (bytes > _preparedOptions.MaximumEstimatedLedgerBytesPerInspection)
            { status = GatePlanAuthorityPrepareStatus.LedgerByteCapacityExceeded; return []; }
            status = GatePlanAuthorityPrepareStatus.Prepared;
            return evictions.ToArray();
        }

        /// <summary>
        /// Stable lifetime estimate: fixed bytes + UTF-8 PlanId bytes + space for every canonical stage cause.
        /// Terminal reconciliation may add causes after admission, so retained bytes cannot depend on the current count.
        /// </summary>
        private long EstimateLedgerBytes(GateApplyLedgerSnapshot ledger)
        {
            return checked(_preparedOptions.EstimatedLedgerFixedBytes +
                Encoding.UTF8.GetByteCount(ledger.PlanId.Value) + (8L * 24L));
        }

        private static GatePlanGeometrySnapshot BuildGeometry(CompiledGatePlan plan) =>
            new(plan.Source.Id, plan.Source.Revision, plan.Source.Gates.Select(gate => new GatePlanGeometryItem(
                gate.Id, gate.Revision, gate.Window.StartReference.Kind,
                gate.Window.StartReference.DependencyGateId, gate.Window.StartOffsetSeconds,
                gate.Window.DurationSeconds, gate.Detector.Threshold?.NormalizedValue)));

        private static bool IsLedgerEvictionEligible(GateApplyLedgerSnapshot ledger) =>
            ledger.PendingResources == GateApplyLedgerPendingResource.None &&
            (ledger.Commit is GateApplyCommitState.Accepted or GateApplyCommitState.ValidationFailed or
                GateApplyCommitState.Conflict or GateApplyCommitState.CapacityExceeded or
                GateApplyCommitState.CancelledBeforeCommit or GateApplyCommitState.FaultedBeforeCommit) &&
            IsTerminal(ledger.LiveEpoch) && IsTerminal(ledger.HistoricalAdmission) &&
            IsTerminal(ledger.HistoricalExecution) && IsTerminal(ledger.CandidateCoverage) &&
            IsTerminal(ledger.Activation) && IsTerminal(ledger.CandidateRetirement) &&
            IsTerminal(ledger.PreviousViewRetirement);

        private static bool IsTerminal(GateLiveEpochState value) => value is GateLiveEpochState.Drained or
            GateLiveEpochState.NotApplicable or GateLiveEpochState.Superseded or GateLiveEpochState.Cancelled or
            GateLiveEpochState.Faulted;
        private static bool IsTerminal(GateHistoricalAdmissionState value) => value is GateHistoricalAdmissionState.Admitted or
            GateHistoricalAdmissionState.NotApplicable or GateHistoricalAdmissionState.Superseded or
            GateHistoricalAdmissionState.Cancelled or GateHistoricalAdmissionState.Rejected or GateHistoricalAdmissionState.Faulted;
        private static bool IsTerminal(GateHistoricalExecutionState value) => value is GateHistoricalExecutionState.Completed or
            GateHistoricalExecutionState.NotApplicable or GateHistoricalExecutionState.Superseded or
            GateHistoricalExecutionState.Cancelled or GateHistoricalExecutionState.Faulted;
        private static bool IsTerminal(GateCandidateCoverageState value) => value is GateCandidateCoverageState.Complete or
            GateCandidateCoverageState.NotApplicable or GateCandidateCoverageState.Superseded or
            GateCandidateCoverageState.Cancelled or GateCandidateCoverageState.Faulted;
        private static bool IsTerminal(GateActivationState value) => value is GateActivationState.Activated or
            GateActivationState.NotApplicable or GateActivationState.Superseded or GateActivationState.CasRejected or
            GateActivationState.RunSuperseded or GateActivationState.Abandoned or GateActivationState.Faulted;
        private static bool IsTerminal(GateCandidateRetirementState value) => value is GateCandidateRetirementState.Reclaimed or
            GateCandidateRetirementState.NotApplicable or GateCandidateRetirementState.Activated;
        private static bool IsTerminal(GatePreviousViewRetirementState value) => value is GatePreviousViewRetirementState.Retired or
            GatePreviousViewRetirementState.NotApplicable or GatePreviousViewRetirementState.Faulted;

        private sealed class InspectionLedgerBudget
        {
            internal List<RetainedLedger> Retained { get; } = [];
            internal long RetainedBytes, ProvisionalBytes;
            internal int ProvisionalSlots;
        }
        private sealed class RetainedLedger(GateApplyLedgerSnapshot ledger, long estimatedBytes)
        {
            internal GateApplyLedgerSnapshot Ledger { get; set; } = ledger;
            internal long EstimatedBytes { get; set; } = estimatedBytes;
            internal bool Eligible => IsLedgerEvictionEligible(Ledger);
            internal bool EvictionReserved { get; set; }
        }
        private sealed record PreparedAuthorityCommit(GatePreparedReservation Reservation,
            GatePlanAuthorityKey Authority, Entry Entry, GatePlanAuthoritySnapshot AuthoritySnapshot,
            GatePlanGeometrySnapshot Geometry, GateApplyLedgerSnapshot Ledger, RetainedLedger RetainedLedger,
            InspectionLedgerBudget Budget, RetainedLedger[] Evictions,
            GatePlanAuthorityDeferredPublication Publication, ConfirmedAuthorityPublicationTicket Ticket);
        private readonly record struct PreparedAuthorityTombstone(GatePreparedReservationId Id,
            GatePreparedReservationOwner Owner, GatePreparedOperationGeneration Generation,
            GatePreparedReservationState State)
        {
            internal GatePreparedReservation Snapshot() => new(Id, Owner, Generation, State);
        }
    }
}
