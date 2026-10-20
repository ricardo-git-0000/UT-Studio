using UTStudio.Contracts.Inspection;
using UTStudio.Contracts.Processing;
using UTStudio.Domain.Acquisition;
using UTStudio.Domain.Inspection;
using UTStudio.Domain.Processing;
using UTStudio.Processing.Core;

namespace UTStudio.Core.Tests.Processing;

[TestClass]
public sealed class GateEditorContractTests
{
    private static readonly InspectionId Inspection = new(Guid.Parse("71000000-0000-0000-0000-000000000001"));
    private static readonly AcquisitionRunId Run = new(Guid.Parse("72000000-0000-0000-0000-000000000001"));

    [TestMethod]
    public void GenerationTypesAreDistinctSafeValues()
    {
        Type[] types = [typeof(GateAuthorityGeneration), typeof(GateAuthorityPublicationSequence), typeof(GatePreparedOperationGeneration),
            typeof(GateReplacementGeneration), typeof(GateRunGeneration)];
        object[] one = [new GateAuthorityGeneration(1), new GateAuthorityPublicationSequence(1), new GatePreparedOperationGeneration(1),
            new GateReplacementGeneration(1), new GateRunGeneration(1)];
        object[] maximum = [new GateAuthorityGeneration(ulong.MaxValue), new GateAuthorityPublicationSequence(ulong.MaxValue),
            new GatePreparedOperationGeneration(ulong.MaxValue), new GateReplacementGeneration(ulong.MaxValue),
            new GateRunGeneration(ulong.MaxValue)];
        Assert.IsFalse(default(GateAuthorityGeneration).IsValid);
        Assert.IsFalse(default(GateAuthorityPublicationSequence).IsValid);
        Assert.IsFalse(default(GatePreparedOperationGeneration).IsValid);
        Assert.IsFalse(default(GateReplacementGeneration).IsValid);
        Assert.IsFalse(default(GateRunGeneration).IsValid);
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new GateAuthorityGeneration(0));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new GateAuthorityPublicationSequence(0));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new GatePreparedOperationGeneration(0));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new GateReplacementGeneration(0));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new GateRunGeneration(0));
        for (int index = 0; index < types.Length; index++)
        {
            Assert.AreEqual(1UL, (ulong)types[index].GetProperty("Value")!.GetValue(one[index])!);
            Assert.AreEqual(ulong.MaxValue, (ulong)types[index].GetProperty("Value")!.GetValue(maximum[index])!);
            Assert.AreEqual(one[index], Activator.CreateInstance(types[index], 1UL));
            Assert.AreEqual(one[index].GetHashCode(), Activator.CreateInstance(types[index], 1UL)!.GetHashCode());
            System.Reflection.MethodInfo[] implicitOperators = types[index].GetMethods(
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static |
                System.Reflection.BindingFlags.DeclaredOnly)
                .Where(method => method.Name == "op_Implicit").ToArray();
            Assert.HasCount(0, implicitOperators,
                $"{types[index].Name} must not declare implicit conversions to or from generations or primitives.");
        }
        Assert.AreNotEqual(typeof(GatePreparedReservation).GetProperty(nameof(GatePreparedReservation.Generation))!.PropertyType,
            typeof(GateStoreOnlyCandidateDescriptor).GetProperty(nameof(GateStoreOnlyCandidateDescriptor.Generation))!.PropertyType);
        Assert.AreEqual(typeof(GateRunGeneration), typeof(GateCandidateActivationRequest)
            .GetProperty(nameof(GateCandidateActivationRequest.ExpectedRunGeneration))!.PropertyType);
    }

    [TestMethod]
    public void ReadStampAndRecoveryResultsRejectContradictoryPayloads()
    {
        CommittedGatePlanSnapshot committed = Committed(1);
        var snapshot = new GatePlanAuthoritySnapshot(committed, new(1), new(1));
        var stamp = new GatePlanAuthorityReadStamp(committed.AuthorityKey, snapshot, new(1), new(1));
        var acquired = new GatePlanAuthorityReadStampResult(GatePlanAuthorityReadStampStatus.Acquired, snapshot, stamp);
        Assert.AreSame(snapshot, acquired.Stamp.Snapshot);
        Assert.ThrowsExactly<ArgumentException>(() => new GatePlanAuthorityReadStampResult(
            GatePlanAuthorityReadStampStatus.Acquired));
        Assert.ThrowsExactly<ArgumentException>(() => new GatePlanAuthorityReadStamp(
            new(Inspection, new("other")), snapshot, new(1), new(1)));
        Assert.AreEqual(GatePlanRecoveryInstallStatus.Installed,
            new GatePlanRecoveryInstallResult(GatePlanRecoveryInstallStatus.Installed, snapshot).Status);
        Assert.ThrowsExactly<ArgumentException>(() => new GatePlanRecoveryInstallResult(
            GatePlanRecoveryInstallStatus.CapabilityDisposed, snapshot));
    }

    [TestMethod]
    public void DraftHasBaseRevisionAndDefensivelyCopiesGates()
    {
        List<GateDefinition> gates = [GateTestPlan.Gate("A")]; GatePlanDraft draft = Draft(new(1), gates);
        gates.Add(GateTestPlan.Gate("B")); Assert.HasCount(1, draft.Gates);
        Assert.IsNull(typeof(GatePlanDraft).GetProperty("Revision"));
        Assert.AreEqual(new GatePlanRevision(1), draft.BaseRevision);
        Assert.ThrowsExactly<NotSupportedException>(() => ((IList<GateDefinition>)draft.Gates).Add(GateTestPlan.Gate("C")));
    }

    [TestMethod]
    public void PreviewAndCancelFactoriesEnforceEditorialState()
    {
        CommittedGatePlanSnapshot committed = Committed(1); GatePlanDraft draft = Draft(new(1));
        var preview = GatePlanEditResult.Previewed(new(committed, draft, state: GatePlanEditorState.Previewed));
        Assert.AreEqual(GatePlanEditOutcome.Previewed, preview.Outcome); Assert.IsNull(preview.ResultingRevision);
        Assert.ThrowsExactly<ArgumentException>(() => GatePlanEditResult.Previewed(new(committed)));
        var cancelled = GatePlanEditResult.Cancelled(new(committed, state: GatePlanEditorState.Cancelled));
        Assert.AreEqual(GatePlanEditOutcome.Cancelled, cancelled.Outcome); Assert.IsNull(cancelled.Snapshot.Draft);
        Assert.ThrowsExactly<ArgumentException>(() => GatePlanEditResult.Cancelled(new(committed, draft)));
    }

    [TestMethod]
    public void AcceptedFactoryConsumesDraftAndPublishesExactlyNextRevision()
    {
        GatePlanEditorSnapshot previous = new(Committed(1), Draft(new(1)));
        GatePlanEditResult accepted = GatePlanEditResult.Accepted(previous, new(Committed(2)));
        Assert.AreEqual(1u, accepted.ExpectedRevision.Value);
        Assert.AreEqual(new GatePlanRevision(2), accepted.CurrentRevision);
        Assert.AreEqual(new GatePlanRevision(2), accepted.ResultingRevision);
        Assert.IsNull(accepted.Snapshot.Draft); Assert.HasCount(0, accepted.ValidationErrors);
        Assert.ThrowsExactly<ArgumentException>(() => GatePlanEditResult.Accepted(previous, new(Committed(3))));
        GatePlanEditorSnapshot invalidDraft = new(Committed(1), Draft(new(1)),
            [new(GateCompilationErrorCode.InvalidThreshold, "invalid")]);
        Assert.ThrowsExactly<ArgumentException>(() => GatePlanEditResult.Accepted(invalidDraft, new(Committed(2))));
        GatePlanEditorSnapshot maximum = new(Committed(uint.MaxValue), Draft(new(uint.MaxValue)));
        Assert.ThrowsExactly<ArgumentException>(() => GatePlanEditResult.Accepted(maximum, new(Committed(uint.MaxValue))));
    }

    [TestMethod]
    public void RejectionFactoriesPreserveCurrentRevisionAndValidatePayloads()
    {
        var error = new GateCompilationError(GateCompilationErrorCode.InvalidDuration, "invalid");
        GatePlanEditorSnapshot current = new(Committed(1), Draft(new(1)), [error],
            GatePlanEditorState.ValidationFailed);
        GatePlanEditResult validation = GatePlanEditResult.ValidationFailed(current, [error]);
        Assert.IsNull(validation.ResultingRevision); Assert.AreEqual(validation.ExpectedRevision, validation.CurrentRevision);
        Assert.ThrowsExactly<ArgumentException>(() => GatePlanEditResult.ValidationFailed(current, []));
        Assert.ThrowsExactly<ArgumentException>(() => GatePlanEditResult.ValidationFailed(current, [null!]));
        GatePlanEditorSnapshot validCurrent = new(Committed(1), Draft(new(1)));
        var capacity = GatePlanEditResult.CapacityExceeded(validCurrent, GatePlanCapacityResource.LiveEpochs);
        Assert.IsNull(capacity.ResultingRevision); Assert.AreEqual(new GatePlanRevision(1), capacity.CurrentRevision);
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => GatePlanEditResult.CapacityExceeded(validCurrent, default));
        Assert.ThrowsExactly<ArgumentException>(() => GatePlanEditResult.CapacityExceeded(new(Committed(1)),
            GatePlanCapacityResource.LiveEpochs));
        GatePlanEditorSnapshot invalidDraft = new(Committed(1), Draft(new(1)),
            [new(GateCompilationErrorCode.InvalidThreshold, "invalid")]);
        Assert.ThrowsExactly<ArgumentException>(() => GatePlanEditResult.CapacityExceeded(invalidDraft,
            GatePlanCapacityResource.LiveEpochs));
        Assert.AreEqual(GatePlanEditOutcome.CancelledBeforeCommit,
            GatePlanEditResult.CancelledBeforeCommit(validCurrent).Outcome);
        Assert.AreEqual(GatePlanEditOutcome.FaultedBeforeCommit,
            GatePlanEditResult.FaultedBeforeCommit(validCurrent).Outcome);
        Assert.ThrowsExactly<ArgumentException>(() => GatePlanEditResult.CancelledBeforeCommit(new(Committed(1))));
    }

    [TestMethod]
    public void ConflictRequiresStaleExpectedAndCarriesCurrentRevision()
    {
        GatePlanEditorSnapshot stale = new(Committed(2), Draft(new(1)), state: GatePlanEditorState.Stale);
        GatePlanEditResult conflict = GatePlanEditResult.Conflict(new(1), stale);
        Assert.AreEqual(1u, conflict.ExpectedRevision.Value);
        Assert.AreEqual(new GatePlanRevision(2), conflict.CurrentRevision); Assert.IsNull(conflict.ResultingRevision);
        Assert.ThrowsExactly<ArgumentException>(() => GatePlanEditResult.Conflict(new(2), stale));
        Assert.ThrowsExactly<ArgumentException>(() => GatePlanEditResult.Conflict(new(1), new(Committed(2), Draft(new(2)))));
        Assert.ThrowsExactly<ArgumentException>(() => GatePlanEditResult.Conflict(new(1), new(Committed(2))));
    }

    [TestMethod]
    public void RevisionExhaustionIsExplicitAndDoesNotOverflow()
    {
        GatePlanEditorSnapshot maximum = new(Committed(uint.MaxValue), Draft(new(uint.MaxValue)));
        GatePlanEditResult result = GatePlanEditResult.RevisionExhausted(maximum);
        Assert.AreEqual(GatePlanEditOutcome.RevisionExhausted, result.Outcome);
        Assert.AreEqual(uint.MaxValue, result.CurrentRevision.Value); Assert.IsNull(result.ResultingRevision);
        Assert.ThrowsExactly<ArgumentException>(() => GatePlanEditResult.RevisionExhausted(new(Committed(1))));
        Assert.AreEqual(typeof(GatePlanRevision), typeof(GatePlanEditResult).GetProperty(nameof(result.CurrentRevision))!.PropertyType);
        Assert.AreNotEqual(typeof(GateRevision), typeof(GatePlanEditResult).GetProperty(nameof(result.CurrentRevision))!.PropertyType);
    }

    [TestMethod]
    public void ErrorCollectionsRejectNullAndAreDefensivelyCopied()
    {
        List<GateCompilationError> errors = [new(GateCompilationErrorCode.InvalidThreshold, "preview")];
        GatePlanEditorSnapshot snapshot = new(Committed(1), Draft(new(1)), errors); errors.Clear();
        Assert.HasCount(1, snapshot.DraftErrors);
        Assert.ThrowsExactly<ArgumentException>(() => new GatePlanEditorSnapshot(Committed(1), Draft(new(1)), [null!]));
        Assert.ThrowsExactly<NotSupportedException>(() => ((IList<GateCompilationError>)snapshot.DraftErrors)
            .Add(new(GateCompilationErrorCode.InvalidThreshold, "other")));
    }

    [TestMethod]
    public void EditorialStateRejectsHybridSnapshots()
    {
        CommittedGatePlanSnapshot committed = Committed(1);
        GatePlanDraft draft = Draft(new(1));
        var error = new GateCompilationError(GateCompilationErrorCode.InvalidThreshold, "invalid");
        Assert.AreEqual(GatePlanEditorState.Committed, new GatePlanEditorSnapshot(committed).State);
        Assert.AreEqual(GatePlanEditorState.Editing, new GatePlanEditorSnapshot(committed, draft).State);
        Assert.AreEqual(GatePlanEditorState.ValidationFailed,
            new GatePlanEditorSnapshot(committed, draft, [error]).State);
        Assert.ThrowsExactly<ArgumentException>(() => new GatePlanEditorSnapshot(committed, null, null,
            GatePlanEditorState.Previewed));
        Assert.ThrowsExactly<ArgumentException>(() => new GatePlanEditorSnapshot(committed, draft, [error],
            GatePlanEditorState.Previewed));
        Assert.ThrowsExactly<ArgumentException>(() => new GatePlanEditorSnapshot(committed, draft, null,
            GatePlanEditorState.ValidationFailed));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new GatePlanEditorSnapshot(committed, state:
            (GatePlanEditorState)99));
    }

    [TestMethod]
    public void LatePreviewFactoriesCarryTypedTerminalAndSupersededOutcomes()
    {
        CommittedGatePlanSnapshot committed = Committed(1);
        var editing = new GatePlanEditorSnapshot(committed, Draft(new(1)));
        GatePlanEditResult superseded = GatePlanEditResult.Superseded(editing, new(1));
        Assert.AreEqual(GatePlanEditOutcome.Superseded, superseded.Outcome);
        var closed = new GatePlanEditorSnapshot(committed, state: GatePlanEditorState.Closed);
        GatePlanEditResult closedResult = GatePlanEditResult.Closed(closed, new(1));
        Assert.AreEqual(GatePlanEditOutcome.Closed, closedResult.Outcome);
        Assert.ThrowsExactly<ArgumentException>(() => GatePlanEditResult.Superseded(editing, default));
        Assert.ThrowsExactly<ArgumentException>(() => GatePlanEditResult.Closed(new(committed), new(1)));
        Assert.ThrowsExactly<ArgumentException>(() => new GatePlanAuthoritySnapshot(committed, default));
    }

    [TestMethod]
    [DataRow(GatePreparedReservationOperation.Commit, GatePreparedReservationState.Prepared, true, true, GatePreparedReservationResultStatus.Committed)]
    [DataRow(GatePreparedReservationOperation.Commit, GatePreparedReservationState.Committed, true, true, GatePreparedReservationResultStatus.AlreadyCommitted)]
    [DataRow(GatePreparedReservationOperation.Commit, GatePreparedReservationState.Aborted, true, true, GatePreparedReservationResultStatus.AlreadyAborted)]
    [DataRow(GatePreparedReservationOperation.Abort, GatePreparedReservationState.Prepared, true, true, GatePreparedReservationResultStatus.Aborted)]
    [DataRow(GatePreparedReservationOperation.Abort, GatePreparedReservationState.Committed, true, true, GatePreparedReservationResultStatus.AlreadyCommitted)]
    [DataRow(GatePreparedReservationOperation.Abort, GatePreparedReservationState.Aborted, true, true, GatePreparedReservationResultStatus.AlreadyAborted)]
    [DataRow(GatePreparedReservationOperation.Commit, GatePreparedReservationState.Prepared, false, true, GatePreparedReservationResultStatus.OwnerOrGenerationMismatch)]
    [DataRow(GatePreparedReservationOperation.Commit, GatePreparedReservationState.Committed, true, false, GatePreparedReservationResultStatus.OwnerOrGenerationMismatch)]
    [DataRow(GatePreparedReservationOperation.Abort, GatePreparedReservationState.Aborted, false, false, GatePreparedReservationResultStatus.OwnerOrGenerationMismatch)]
    public void PreparedReservationClassificationHasCredentialFirstPrecedence(
        GatePreparedReservationOperation operation, GatePreparedReservationState state,
        bool correctOwner, bool correctGeneration, GatePreparedReservationResultStatus expected)
    {
        GatePreparedReservation reservation = Reservation(state);
        GatePreparedReservationResult result = GatePreparedReservationResult.Classify(reservation,
            correctOwner ? reservation.Owner : new(Guid.Parse("74000000-0000-0000-0000-000000000002")),
            correctGeneration ? reservation.Generation : new(2), operation);
        Assert.AreEqual(expected, result.Status);
        if (expected == GatePreparedReservationResultStatus.Committed)
            Assert.AreEqual(GatePreparedReservationState.Committed, result.Reservation.State);
        if (expected == GatePreparedReservationResultStatus.Aborted)
            Assert.AreEqual(GatePreparedReservationState.Aborted, result.Reservation.State);
    }

    [TestMethod]
    public void PreparedReservationDefaultAndOperationAreValidated()
    {
        GatePreparedReservation valid = Reservation(GatePreparedReservationState.Prepared);
        Assert.AreEqual(GatePreparedReservationResultStatus.InvalidReservation,
            GatePreparedReservationResult.Classify(default, valid.Owner, valid.Generation,
                GatePreparedReservationOperation.Commit).Status);
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => GatePreparedReservationResult.Classify(
            valid, valid.Owner, valid.Generation, GatePreparedReservationOperation.Unknown));
        Assert.AreEqual(GatePreparedReservationResultStatus.InvalidReservation,
            GatePreparedReservationResult.Classify(default, default, default,
                GatePreparedReservationOperation.Unknown).Status);
        Assert.AreEqual(GatePreparedReservationResultStatus.OwnerOrGenerationMismatch,
            GatePreparedReservationResult.Classify(valid, new(Guid.NewGuid()), valid.Generation,
                GatePreparedReservationOperation.Unknown).Status);
    }

    [TestMethod]
    public void PreparedReservationMatrixCoversEveryStateOperationAndCredentialCombination()
    {
        foreach (GatePreparedReservationOperation operation in new[]
                 { GatePreparedReservationOperation.Commit, GatePreparedReservationOperation.Abort })
            foreach (GatePreparedReservationState state in new[]
                     { GatePreparedReservationState.Prepared, GatePreparedReservationState.Committed, GatePreparedReservationState.Aborted })
                foreach (bool ownerMatches in new[] { false, true })
                    foreach (bool generationMatches in new[] { false, true })
                    {
                        GatePreparedReservation reservation = Reservation(state);
                        GatePreparedReservationResult result = GatePreparedReservationResult.Classify(reservation,
                            ownerMatches ? reservation.Owner : new(Guid.Parse("74000000-0000-0000-0000-000000000002")),
                            generationMatches ? reservation.Generation : new(2), operation);
                        if (!ownerMatches || !generationMatches)
                        {
                            Assert.AreEqual(GatePreparedReservationResultStatus.OwnerOrGenerationMismatch, result.Status);
                            Assert.AreEqual(reservation, result.Reservation);
                        }
                    }
    }

    [TestMethod]
    public void CandidateEqualityIncludesEveryManifestDimension()
    {
        GateStoreOnlyCandidateDescriptor baseline = Candidate(Manifest());
        GateDerivedCoverageManifest basis = baseline.Manifest;
        GateDerivedStreamCoverage stream = basis.Streams.Single();
        GateDerivedCoverageManifest[] variants =
        [
            ManifestVariant(inspection: new(Guid.Parse("71000000-0000-0000-0000-000000000002"))),
            ManifestVariant(run: new(Guid.Parse("72000000-0000-0000-0000-000000000002"))),
            ManifestVariant(planId: new("other-plan")), ManifestVariant(planRevision: new(2)),
            ManifestVariant(planSignature: new string('A', 64)), ManifestVariant(capturedRevision: new(3)),
            ManifestVariant(minimum: 0), ManifestVariant(maximum: 2),
            Manifest(sourceRevision: 2), Manifest(schema: 2), Manifest(algorithm: 2), Manifest(scope: "region"),
            Manifest(source: "other"), Manifest(selectedRanges: [new(1, 1)]), Manifest(digestSeed: 2),
            Manifest(eligibleCount: 2)
        ];
        Assert.AreEqual(baseline, Candidate(Manifest())); Assert.AreEqual(baseline.GetHashCode(), Candidate(Manifest()).GetHashCode());
        foreach (GateDerivedCoverageManifest variant in variants) Assert.AreNotEqual(baseline, Candidate(variant));
        Assert.AreEqual(stream, Manifest().Streams.Single());
    }

    [TestMethod]
    public void ActivePredecessorHasStructuralEqualityAndHashing()
    {
        var first = new ActiveDerivedViewDescriptor(new("active"), new(3), Manifest(), 4);
        var same = new ActiveDerivedViewDescriptor(new("active"), new(3), Manifest(), 4);
        Assert.AreEqual(first, same); Assert.AreEqual(first.GetHashCode(), same.GetHashCode());
        Assert.AreNotEqual(first, new ActiveDerivedViewDescriptor(new("active"), new(3), Manifest(scope: "region"), 4));
    }

    [TestMethod]
    public void DeepDescriptorsObeyEqualityLawsAndCanonicalStreamOrder()
    {
        GateDerivedCoverageManifest first = TwoStreamManifest(reverse: false);
        GateDerivedCoverageManifest second = TwoStreamManifest(reverse: true);
        GateDerivedCoverageManifest third = TwoStreamManifest(reverse: false);
        Assert.AreEqual(first, first); Assert.AreEqual(first, second); Assert.AreEqual(second, first);
        Assert.AreEqual(second, third); Assert.AreEqual(first, third);
        Assert.AreEqual(first.GetHashCode(), second.GetHashCode());
        Assert.AreEqual(first.GetHashCode(), first.GetHashCode());
        Assert.AreEqual("a", first.Streams[0].Stream.SourceId.Value);
        GateStoreOnlyCandidateDescriptor candidate1 = Candidate(first);
        GateStoreOnlyCandidateDescriptor candidate2 = Candidate(second);
        GateStoreOnlyCandidateDescriptor candidate3 = Candidate(third);
        Assert.AreEqual(candidate1, candidate1); Assert.AreEqual(candidate2, candidate1);
        Assert.AreEqual(candidate1, candidate2); Assert.AreEqual(candidate2, candidate3);
        Assert.AreEqual(candidate1, candidate3);
        Assert.AreEqual(candidate1.GetHashCode(), candidate2.GetHashCode());
        Assert.AreEqual(candidate1.GetHashCode(), candidate1.GetHashCode());
        var view1 = new ActiveDerivedViewDescriptor(new("active"), new(3), first, 4);
        var view2 = new ActiveDerivedViewDescriptor(new("active"), new(3), second, 4);
        var view3 = new ActiveDerivedViewDescriptor(new("active"), new(3), third, 4);
        Assert.AreEqual(view1, view1); Assert.AreEqual(view2, view1);
        Assert.AreEqual(view1, view2); Assert.AreEqual(view2, view3); Assert.AreEqual(view1, view3);
        Assert.AreEqual(view1.GetHashCode(), view2.GetHashCode());
        Assert.AreEqual(view1.GetHashCode(), view1.GetHashCode());
        Assert.AreNotEqual(candidate1, new GateStoreOnlyCandidateDescriptor(first, new("other"), new(4), new(7),
            GateCandidateOwnershipState.Available));
        Assert.AreNotEqual(view1, new ActiveDerivedViewDescriptor(new("active"), new(4), first, 4));
    }

    [TestMethod]
    public void ManifestRejectsDuplicateStreamIdentity()
    {
        GateDerivedCoverageManifest basis = Manifest(); GateDerivedStreamCoverage stream = basis.Streams.Single();
        Assert.ThrowsExactly<ArgumentException>(() => new GateDerivedCoverageManifest(Inspection, Run,
            [stream, stream], basis.SourceFinalRevision, basis.PlanId, basis.PlanRevision, basis.PlanSignature,
            basis.SchemaVersion, basis.AlgorithmVersion, stream.EligibleTraceCount * 2,
            basis.CapturedStoreRevision, basis.CoverageScope));
    }

    [TestMethod]
    public void CasClassifierIsPartitionedAndAppliesDocumentedPrecedence()
    {
        GateStoreOnlyCandidateDescriptor candidate = Candidate(Manifest());
        var request = new GateCandidateActivationRequest(candidate, null, Run, new(8));
        Assert.AreEqual(GateCandidateActivationStatus.Activated, Classify(request).Status);
        Assert.AreEqual(GateCandidateActivationStatus.Disposed, Classify(request, lifecycle: GateApplyLedgerLifecycleState.Disposed,
            ownership: GateCandidateOwnershipState.Activated).Status);
        Assert.AreEqual(GateCandidateActivationStatus.Disposed,
            Classify(request, lifecycle: GateApplyLedgerLifecycleState.Disposing).Status);
        Assert.AreEqual(GateCandidateActivationStatus.RunSuperseded, Classify(request, runGeneration: 9).Status);
        Assert.AreEqual(GateCandidateActivationStatus.RunSuperseded, Classify(request,
            runId: new(Guid.Parse("20000000-0000-0000-0000-000000000099")),
            ownership: GateCandidateOwnershipState.Reclaimed).Status);
        Assert.AreEqual(GateCandidateActivationStatus.TokenSuperseded, Classify(request, token: 9,
            candidate: Candidate(Manifest(scope: "other"), replacementToken: 9)).Status);
        Assert.AreEqual(GateCandidateActivationStatus.TokenSuperseded,
            Classify(request, replacementGeneration: 9).Status);
        Assert.AreEqual(GateCandidateActivationStatus.TokenSuperseded,
            Classify(request, noDesiredToken: true, ownership: GateCandidateOwnershipState.Reclaimed).Status);
        Assert.AreEqual(GateCandidateActivationStatus.CandidateMismatch,
            Classify(request, candidate: Candidate(Manifest(scope: "other"))).Status);
        Assert.AreEqual(GateCandidateActivationStatus.CandidateNotAvailable,
            Classify(request, ownership: GateCandidateOwnershipState.Reserved).Status);
        Assert.AreEqual(GateCandidateActivationStatus.CandidateAbandoned,
            Classify(request, ownership: GateCandidateOwnershipState.LogicallyAbandoned).Status);
        Assert.AreEqual(GateCandidateActivationStatus.CandidateAbandoned,
            Classify(request, ownership: GateCandidateOwnershipState.Reclaimed).Status);
        var disposedActive = new ActiveDerivedViewDescriptor(candidate.CandidateViewId, new(3), candidate.Manifest, 3);
        Assert.AreEqual(GateCandidateActivationStatus.Disposed,
            Classify(request, lifecycle: GateApplyLedgerLifecycleState.Disposed,
                ownership: GateCandidateOwnershipState.Activated, active: disposedActive).Status);
        Assert.AreEqual(GateCandidateActivationStatus.CandidateNotAvailable,
            Classify(request, capacity: false).Status);
    }

    [TestMethod]
    public void ObservedCasStateRejectsEveryContradictoryCoordinateAndOwnership()
    {
        GateStoreOnlyCandidateDescriptor candidate = Candidate(Manifest());
        AssertObservedRejected(candidate, desiredToken: new(5));
        AssertObservedRejected(candidate, desiredGeneration: new(8));
        AssertObservedRejected(Candidate(ManifestVariant(inspection: new(Guid.NewGuid()))));
        AssertObservedRejected(Candidate(ManifestVariant(run: new(Guid.NewGuid()))));
        AssertObservedRejected(Candidate(ManifestVariant(planId: new("other-plan"))));
        AssertObservedRejected(Candidate(ManifestVariant(planRevision: new(2))));
        AssertObservedRejected(candidate, ownership: null);
        AssertObservedRejected(candidate, ownership: GateCandidateOwnershipState.Reclaimed);
        AssertObservedRejected(null, ownership: GateCandidateOwnershipState.Available);
        AssertObservedRejected(null, ownership: GateCandidateOwnershipState.Reserved);
    }

    [TestMethod]
    public void CasPredecessorMismatchPreservesExplicitObservedEvidence()
    {
        GateStoreOnlyCandidateDescriptor candidate = Candidate(Manifest());
        var expected = new ActiveDerivedViewDescriptor(new("old"), new(1), Manifest(), 1);
        var other = new ActiveDerivedViewDescriptor(new("other"), new(2), Manifest(), 2);
        var request = new GateCandidateActivationRequest(candidate, expected, Run, new(8));
        GateCandidateActivationResult missing = Classify(request, predecessorKind: GateObservedPredecessorKind.None);
        Assert.AreEqual(GateCandidateActivationStatus.PredecessorMismatch, missing.Status);
        Assert.AreEqual(expected, missing.ExpectedPredecessor);
        Assert.AreEqual(GateObservedPredecessorKind.None, missing.ObservedPredecessorKind); Assert.IsNull(missing.ObservedPredecessor);
        GateCandidateActivationResult different = Classify(request, predecessorKind: GateObservedPredecessorKind.Present,
            predecessor: other);
        Assert.AreEqual(GateCandidateActivationStatus.PredecessorMismatch, different.Status);
        Assert.AreEqual(expected, different.ExpectedPredecessor);
        Assert.AreEqual(other, different.ObservedPredecessor);
        var noneExpected = new GateCandidateActivationRequest(candidate, null, Run, new(8));
        Assert.AreEqual(GateCandidateActivationStatus.PredecessorMismatch,
            Classify(noneExpected, predecessorKind: GateObservedPredecessorKind.Present, predecessor: other).Status);
    }

    [TestMethod]
    public void AlreadyActivatedIsIdempotentSuccessBeforePredecessorComparison()
    {
        GateStoreOnlyCandidateDescriptor candidate = Candidate(Manifest());
        var request = new GateCandidateActivationRequest(candidate,
            new(new("old"), new(1), Manifest(), 1), Run, new(8));
        var active = new ActiveDerivedViewDescriptor(candidate.CandidateViewId, new(3), candidate.Manifest, 3);
        GateCandidateActivationResult result = Classify(request, ownership: GateCandidateOwnershipState.Activated,
            predecessorKind: GateObservedPredecessorKind.Present, predecessor: active, active: active);
        Assert.AreEqual(GateCandidateActivationStatus.AlreadyActivated, result.Status);
        Assert.IsTrue(result.IsSuccess); Assert.AreEqual(active, result.ActiveView);
    }

    [TestMethod]
    public void AbandonAndReclaimResultsEnforceOwnershipPayloads()
    {
        GateCandidateAbandonResult abandoned = GateCandidateAbandonResult.Create(
            GateCandidateAbandonStatus.LogicallyAbandoned, Candidate(Manifest(), GateCandidateOwnershipState.LogicallyAbandoned));
        Assert.AreEqual(GateCandidateOwnershipState.LogicallyAbandoned, abandoned.Candidate.OwnershipState);
        GateCandidateReclaimResult busy = GateCandidateReclaimResult.Create(GateCandidateReclaimStatus.Busy,
            Candidate(Manifest(), GateCandidateOwnershipState.LogicallyAbandoned));
        Assert.AreEqual(GateCandidateReclaimStatus.Busy, busy.Status);
        Assert.AreEqual(GateCandidateAbandonStatus.AlreadyAbandoned, GateCandidateAbandonResult.Create(
            GateCandidateAbandonStatus.AlreadyAbandoned,
            Candidate(Manifest(), GateCandidateOwnershipState.LogicallyAbandoned)).Status);
        Assert.AreEqual(GateCandidateAbandonStatus.AlreadyAbandoned, GateCandidateAbandonResult.Create(
            GateCandidateAbandonStatus.AlreadyAbandoned,
            Candidate(Manifest(), GateCandidateOwnershipState.Reclaimed)).Status);
        Assert.AreEqual(GateCandidateAbandonStatus.AlreadyActivated, GateCandidateAbandonResult.Create(
            GateCandidateAbandonStatus.AlreadyActivated, Candidate(Manifest(), GateCandidateOwnershipState.Activated)).Status);
        Assert.AreEqual(GateCandidateAbandonStatus.CandidateNotAvailable, GateCandidateAbandonResult.Create(
            GateCandidateAbandonStatus.CandidateNotAvailable, Candidate(Manifest(), GateCandidateOwnershipState.Reserved)).Status);
        Assert.AreEqual(GateCandidateAbandonStatus.Disposed, GateCandidateAbandonResult.Create(
            GateCandidateAbandonStatus.Disposed, Candidate(Manifest(), GateCandidateOwnershipState.Activated),
            GateApplyLedgerLifecycleState.Disposed).Status);
        Assert.AreEqual(GateCandidateReclaimStatus.Reclaimed, GateCandidateReclaimResult.Create(
            GateCandidateReclaimStatus.Reclaimed, Candidate(Manifest(), GateCandidateOwnershipState.Reclaimed)).Status);
        Assert.AreEqual(GateCandidateReclaimStatus.AlreadyReclaimed, GateCandidateReclaimResult.Create(
            GateCandidateReclaimStatus.AlreadyReclaimed, Candidate(Manifest(), GateCandidateOwnershipState.Reclaimed)).Status);
        Assert.AreEqual(GateCandidateReclaimStatus.AlreadyActivated, GateCandidateReclaimResult.Create(
            GateCandidateReclaimStatus.AlreadyActivated, Candidate(Manifest(), GateCandidateOwnershipState.Activated)).Status);
        Assert.AreEqual(GateCandidateReclaimStatus.CandidateNotAvailable, GateCandidateReclaimResult.Create(
            GateCandidateReclaimStatus.CandidateNotAvailable,
            Candidate(Manifest(), GateCandidateOwnershipState.Materializing)).Status);
        Assert.AreEqual(GateCandidateReclaimStatus.Disposed, GateCandidateReclaimResult.Create(
            GateCandidateReclaimStatus.Disposed, Candidate(Manifest(), GateCandidateOwnershipState.Reclaimed),
            GateApplyLedgerLifecycleState.Disposed).Status);
        Assert.ThrowsExactly<ArgumentException>(() => GateCandidateReclaimResult.Create(GateCandidateReclaimStatus.Busy,
            Candidate(Manifest(), GateCandidateOwnershipState.Abandoning)));
        Assert.ThrowsExactly<ArgumentException>(() => GateCandidateReclaimResult.Create(GateCandidateReclaimStatus.Reclaimed,
            Candidate(Manifest(), GateCandidateOwnershipState.Activated)));
        Assert.ThrowsExactly<ArgumentException>(() => GateCandidateAbandonResult.Create(GateCandidateAbandonStatus.Disposed,
            Candidate(Manifest(), GateCandidateOwnershipState.Activated)));
        Assert.ThrowsExactly<ArgumentException>(() => GateCandidateReclaimResult.Create(GateCandidateReclaimStatus.Disposed,
            Candidate(Manifest(), GateCandidateOwnershipState.Reclaimed)));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => GateCandidateAbandonResult.Create((GateCandidateAbandonStatus)99,
            Candidate(Manifest(), GateCandidateOwnershipState.Available)));
    }

    [TestMethod]
    public void LedgerAcceptsCoherentPipelineAndHasStructuralCauseSemantics()
    {
        GateApplyLedgerSnapshot first = Ledger(execution: GateHistoricalExecutionState.Completed,
            coverage: GateCandidateCoverageState.Complete, activation: GateActivationState.Activated,
            candidateRetirement: GateCandidateRetirementState.Activated,
            previousRetirement: GatePreviousViewRetirementState.Required);
        GateApplyLedgerSnapshot same = Ledger(execution: GateHistoricalExecutionState.Completed,
            coverage: GateCandidateCoverageState.Complete, activation: GateActivationState.Activated,
            candidateRetirement: GateCandidateRetirementState.Activated,
            previousRetirement: GatePreviousViewRetirementState.Required);
        Assert.AreEqual(first, same); Assert.AreEqual(first.GetHashCode(), same.GetHashCode());
        var fault = new GateLedgerCause(GateLedgerStage.Activation, GateLedgerCauseKind.RunSuperseded);
        GateApplyLedgerSnapshot withCause = Ledger(activation: GateActivationState.RunSuperseded,
            candidateRetirement: GateCandidateRetirementState.Dormant, primaryCause: fault);
        Assert.AreEqual(fault, withCause.PrimaryCause);
        Assert.ThrowsExactly<ArgumentException>(() => Ledger(activation: GateActivationState.RunSuperseded));
    }

    [TestMethod]
    public void LedgerCausesAreTypedOrderedAndDefensivelyCopied()
    {
        var primary = new GateLedgerCause(GateLedgerStage.Activation, GateLedgerCauseKind.CandidateAbandoned);
        List<GateLedgerCause> secondary =
        [
            new(GateLedgerStage.CandidateCoverage, GateLedgerCauseKind.Superseded),
            new(GateLedgerStage.CandidateRetirement, GateLedgerCauseKind.ReclaimBusy)
        ];
        GateApplyLedgerSnapshot snapshot = GateApplyLedgerSnapshot.Create(Inspection, Run, new(1), new("plan"), new(1),
            GateApplyCommitState.Accepted, GateLiveEpochState.Active, GateHistoricalAdmissionState.Admitted,
            GateHistoricalExecutionState.Completed, GateCandidateCoverageState.Superseded,
            GateActivationState.Abandoned, GateCandidateRetirementState.LogicallyAbandoned,
            GatePreviousViewRetirementState.NotApplicable, GateCandidateOwnershipState.LogicallyAbandoned,
            primary, secondary);
        secondary.Clear(); Assert.HasCount(2, snapshot.SecondaryCauses);
        Assert.AreEqual(GateLedgerStage.CandidateCoverage, snapshot.SecondaryCauses[0].Stage);
        Assert.ThrowsExactly<NotSupportedException>(() => ((IList<GateLedgerCause>)snapshot.SecondaryCauses)
            .Add(new(GateLedgerStage.Commit, GateLedgerCauseKind.Faulted)));
        Assert.ThrowsExactly<ArgumentException>(() => new GateLedgerCause(GateLedgerStage.Commit,
            GateLedgerCauseKind.CapacityExceeded));
        Assert.ThrowsExactly<ArgumentException>(() => new GateLedgerCause(GateLedgerStage.Commit,
            GateLedgerCauseKind.TokenSuperseded));
    }

    [TestMethod]
    public void LedgerRejectsImpossibleCrossMachineStates()
    {
        Assert.ThrowsExactly<ArgumentException>(() => Ledger(activation: GateActivationState.Activated));
        Assert.ThrowsExactly<ArgumentException>(() => Ledger(execution: GateHistoricalExecutionState.Running,
            admission: GateHistoricalAdmissionState.Dormant));
        Assert.ThrowsExactly<ArgumentException>(() => Ledger(coverage: GateCandidateCoverageState.Complete,
            execution: GateHistoricalExecutionState.Running));
        Assert.ThrowsExactly<ArgumentException>(() => Ledger(activation: GateActivationState.Activated,
            execution: GateHistoricalExecutionState.Completed, coverage: GateCandidateCoverageState.Complete,
            candidateRetirement: GateCandidateRetirementState.Reclaimed));
        Assert.ThrowsExactly<ArgumentException>(() => Ledger(previousRetirement: GatePreviousViewRetirementState.Required));
        Assert.ThrowsExactly<ArgumentException>(() => GateApplyLedgerSnapshot.Create(Inspection, Run, new(1), new("plan"), new(1),
            GateApplyCommitState.Conflict, GateLiveEpochState.Active, GateHistoricalAdmissionState.Dormant,
            GateHistoricalExecutionState.Dormant, GateCandidateCoverageState.Dormant, GateActivationState.Dormant,
            GateCandidateRetirementState.Dormant, GatePreviousViewRetirementState.Dormant,
            primaryCause: new(GateLedgerStage.Commit, GateLedgerCauseKind.RevisionConflict)));
    }

    [TestMethod]
    public void LedgerRequiresOwnershipDerivedFromAllEightSubstates()
    {
        Assert.ThrowsExactly<ArgumentException>(() => Snapshot(candidateOwnership: GateCandidateOwnershipState.Activated));
        Assert.ThrowsExactly<ArgumentException>(() => Snapshot(admission: GateHistoricalAdmissionState.Admitted,
            candidateOwnership: GateCandidateOwnershipState.Abandoning));
        Assert.AreEqual(GateCandidateOwnershipState.Reserved,
            Snapshot(admission: GateHistoricalAdmissionState.Admitted,
                candidateOwnership: GateCandidateOwnershipState.Reserved).CandidateOwnership);
        Assert.AreEqual(GateCandidateOwnershipState.Materializing,
            Snapshot(admission: GateHistoricalAdmissionState.Admitted,
                execution: GateHistoricalExecutionState.Running, coverage: GateCandidateCoverageState.Building,
                candidateOwnership: GateCandidateOwnershipState.Materializing).CandidateOwnership);
        Assert.AreEqual(GateCandidateOwnershipState.Available,
            Snapshot(admission: GateHistoricalAdmissionState.Admitted,
                execution: GateHistoricalExecutionState.Completed, coverage: GateCandidateCoverageState.Complete,
                candidateOwnership: GateCandidateOwnershipState.Available).CandidateOwnership);
        Assert.AreEqual(GateCandidateOwnershipState.Activating,
            Snapshot(admission: GateHistoricalAdmissionState.Admitted,
                execution: GateHistoricalExecutionState.Completed, coverage: GateCandidateCoverageState.Complete,
                activation: GateActivationState.Activating,
                candidateOwnership: GateCandidateOwnershipState.Activating).CandidateOwnership);
        Assert.AreEqual(GateCandidateOwnershipState.Activated,
            Snapshot(admission: GateHistoricalAdmissionState.Admitted,
                execution: GateHistoricalExecutionState.Completed, coverage: GateCandidateCoverageState.Complete,
                activation: GateActivationState.Activated, retirement: GateCandidateRetirementState.Activated,
                candidateOwnership: GateCandidateOwnershipState.Activated).CandidateOwnership);
    }

    [TestMethod]
    public void LedgerTransitionValidatorRejectsJumpsReopeningAndIdentityChanges()
    {
        GateApplyLedgerSnapshot dormant = Snapshot();
        GateApplyLedgerSnapshot deferred = Snapshot(admission: GateHistoricalAdmissionState.Deferred);
        Assert.AreEqual(GateApplyLedgerTransitionStatus.Valid,
            GateApplyLedgerTransitionResult.Validate(dormant, deferred).Status);
        GateApplyLedgerSnapshot admitting = Snapshot(admission: GateHistoricalAdmissionState.Admitting);
        Assert.AreEqual(GateApplyLedgerTransitionStatus.Valid,
            GateApplyLedgerTransitionResult.Validate(deferred, admitting).Status);
        GateApplyLedgerSnapshot admitted = Snapshot(admission: GateHistoricalAdmissionState.Admitted,
            candidateOwnership: GateCandidateOwnershipState.Reserved);
        Assert.AreEqual(GateApplyLedgerTransitionStatus.Valid,
            GateApplyLedgerTransitionResult.Validate(admitting, admitted).Status);
        Assert.AreEqual(GateApplyLedgerTransitionStatus.IllegalStageTransition,
            GateApplyLedgerTransitionResult.Validate(dormant, admitted).Status);
        GateApplyLedgerSnapshot notApplicable = Snapshot(admission: GateHistoricalAdmissionState.NotApplicable);
        Assert.AreEqual(GateApplyLedgerTransitionStatus.TerminalReopened,
            GateApplyLedgerTransitionResult.Validate(notApplicable, deferred).Status);
        GateApplyLedgerSnapshot otherIdentity = Snapshot(inspectionId: new(Guid.Parse("10000000-0000-0000-0000-000000000099")));
        Assert.AreEqual(GateApplyLedgerTransitionStatus.IdentityMismatch,
            GateApplyLedgerTransitionResult.Validate(dormant, otherIdentity).Status);
        GateApplyLedgerSnapshot otherRevision = Snapshot(planRevision: new(2));
        Assert.AreEqual(GateApplyLedgerTransitionStatus.RevisionMismatch,
            GateApplyLedgerTransitionResult.Validate(dormant, otherRevision).Status);
    }

    [TestMethod]
    public void LedgerTransitionValidatorAcceptsNormativeMaterializeActivateAndRetirePath()
    {
        GateApplyLedgerSnapshot[] path =
        [
            Snapshot(),
            Snapshot(admission: GateHistoricalAdmissionState.Deferred),
            Snapshot(admission: GateHistoricalAdmissionState.Admitting),
            Snapshot(admission: GateHistoricalAdmissionState.Admitted,
                candidateOwnership: GateCandidateOwnershipState.Reserved),
            Snapshot(admission: GateHistoricalAdmissionState.Admitted,
                execution: GateHistoricalExecutionState.Queued,
                candidateOwnership: GateCandidateOwnershipState.Reserved),
            Snapshot(admission: GateHistoricalAdmissionState.Admitted,
                execution: GateHistoricalExecutionState.Running, coverage: GateCandidateCoverageState.Building,
                candidateOwnership: GateCandidateOwnershipState.Materializing),
            Snapshot(admission: GateHistoricalAdmissionState.Admitted,
                execution: GateHistoricalExecutionState.Completed, coverage: GateCandidateCoverageState.Complete,
                candidateOwnership: GateCandidateOwnershipState.Available),
            Snapshot(admission: GateHistoricalAdmissionState.Admitted,
                execution: GateHistoricalExecutionState.Completed, coverage: GateCandidateCoverageState.Complete,
                activation: GateActivationState.Eligible, candidateOwnership: GateCandidateOwnershipState.Available),
            Snapshot(admission: GateHistoricalAdmissionState.Admitted,
                execution: GateHistoricalExecutionState.Completed, coverage: GateCandidateCoverageState.Complete,
                activation: GateActivationState.Activating, candidateOwnership: GateCandidateOwnershipState.Activating),
            Snapshot(admission: GateHistoricalAdmissionState.Admitted,
                execution: GateHistoricalExecutionState.Completed, coverage: GateCandidateCoverageState.Complete,
                activation: GateActivationState.Activated, retirement: GateCandidateRetirementState.Activated,
                previousRetirement: GatePreviousViewRetirementState.Required,
                candidateOwnership: GateCandidateOwnershipState.Activated),
            Snapshot(admission: GateHistoricalAdmissionState.Admitted,
                execution: GateHistoricalExecutionState.Completed, coverage: GateCandidateCoverageState.Complete,
                activation: GateActivationState.Activated, retirement: GateCandidateRetirementState.Activated,
                previousRetirement: GatePreviousViewRetirementState.Retiring,
                candidateOwnership: GateCandidateOwnershipState.Activated),
            Snapshot(admission: GateHistoricalAdmissionState.Admitted,
                execution: GateHistoricalExecutionState.Completed, coverage: GateCandidateCoverageState.Complete,
                activation: GateActivationState.Activated, retirement: GateCandidateRetirementState.Activated,
                previousRetirement: GatePreviousViewRetirementState.Retired,
                candidateOwnership: GateCandidateOwnershipState.Activated)
        ];
        for (int index = 1; index < path.Length; index++)
            Assert.AreEqual(GateApplyLedgerTransitionStatus.Valid,
                GateApplyLedgerTransitionResult.Validate(path[index - 1], path[index]).Status, $"step {index}");
    }

    [TestMethod]
    public void RejectedCasCanProgressThroughAbandonmentAndReclaim()
    {
        GateApplyLedgerSnapshot rejected = RejectedSnapshot(GateCandidateRetirementState.Dormant,
            GateCandidateOwnershipState.Available);
        GateApplyLedgerSnapshot required = RejectedSnapshot(GateCandidateRetirementState.Required,
            GateCandidateOwnershipState.Available);
        GateApplyLedgerSnapshot abandoning = RejectedSnapshot(GateCandidateRetirementState.Abandoning,
            GateCandidateOwnershipState.Abandoning);
        GateApplyLedgerSnapshot abandoned = RejectedSnapshot(GateCandidateRetirementState.LogicallyAbandoned,
            GateCandidateOwnershipState.LogicallyAbandoned);
        GateApplyLedgerSnapshot reclaimed = RejectedSnapshot(GateCandidateRetirementState.Reclaimed,
            GateCandidateOwnershipState.Reclaimed);
        GateApplyLedgerSnapshot[] path = [rejected, required, abandoning, abandoned, reclaimed];
        for (int index = 1; index < path.Length; index++)
            Assert.AreEqual(GateApplyLedgerTransitionStatus.Valid,
                GateApplyLedgerTransitionResult.Validate(path[index - 1], path[index]).Status, $"cleanup {index}");
    }

    [TestMethod]
    public void LifecycleIsOrthogonalPreservesMaterialTerminalAndDrainsPendingResources()
    {
        GateApplyLedgerSnapshot active = Snapshot();
        GateApplyLedgerSnapshot disposing = Snapshot(lifecycle: GateApplyLedgerLifecycleState.Disposing,
            pendingResources: GateApplyLedgerPendingResource.Observer | GateApplyLedgerPendingResource.Lease);
        GateApplyLedgerSnapshot drained = Snapshot(lifecycle: GateApplyLedgerLifecycleState.Disposing);
        GateApplyLedgerSnapshot disposed = Snapshot(lifecycle: GateApplyLedgerLifecycleState.Disposed);
        Assert.AreEqual(GateApplyLedgerTransitionStatus.Valid,
            GateApplyLedgerTransitionResult.Validate(active, disposing).Status);
        Assert.AreEqual(GateApplyLedgerTransitionStatus.Valid,
            GateApplyLedgerTransitionResult.Validate(disposing, drained).Status);
        Assert.AreEqual(GateApplyLedgerTransitionStatus.Valid,
            GateApplyLedgerTransitionResult.Validate(drained, disposed).Status);
        Assert.IsNull(disposed.CandidateOwnership);
        Assert.ThrowsExactly<ArgumentException>(() => Snapshot(lifecycle: GateApplyLedgerLifecycleState.Disposed,
            pendingResources: GateApplyLedgerPendingResource.Observer));
        Assert.AreEqual(GateApplyLedgerTransitionStatus.IllegalStageTransition,
            GateApplyLedgerTransitionResult.Validate(active, disposed).Status);
        Assert.AreEqual(GateApplyLedgerTransitionStatus.TerminalReopened,
            GateApplyLedgerTransitionResult.Validate(disposed, active).Status);
    }

    [TestMethod]
    public void DisposeCompletesAvailableCleanupAndPreservesActivatedOrReclaimedOwnership()
    {
        GateApplyLedgerSnapshot available = RejectedSnapshot(GateCandidateRetirementState.Dormant,
            GateCandidateOwnershipState.Available);
        GateApplyLedgerSnapshot disposingAvailable = RejectedSnapshot(GateCandidateRetirementState.Dormant,
            GateCandidateOwnershipState.Available, GateApplyLedgerLifecycleState.Disposing,
            GateApplyLedgerPendingResource.Tombstone);
        GateApplyLedgerSnapshot required = RejectedSnapshot(GateCandidateRetirementState.Required,
            GateCandidateOwnershipState.Available, GateApplyLedgerLifecycleState.Disposing,
            GateApplyLedgerPendingResource.Tombstone);
        GateApplyLedgerSnapshot abandoning = RejectedSnapshot(GateCandidateRetirementState.Abandoning,
            GateCandidateOwnershipState.Abandoning, GateApplyLedgerLifecycleState.Disposing,
            GateApplyLedgerPendingResource.Tombstone);
        GateApplyLedgerSnapshot abandoned = RejectedSnapshot(GateCandidateRetirementState.LogicallyAbandoned,
            GateCandidateOwnershipState.LogicallyAbandoned, GateApplyLedgerLifecycleState.Disposing,
            GateApplyLedgerPendingResource.Tombstone);
        GateApplyLedgerSnapshot reclaimed = RejectedSnapshot(GateCandidateRetirementState.Reclaimed,
            GateCandidateOwnershipState.Reclaimed, GateApplyLedgerLifecycleState.Disposing);
        GateApplyLedgerSnapshot disposedReclaimed = RejectedSnapshot(GateCandidateRetirementState.Reclaimed,
            GateCandidateOwnershipState.Reclaimed, GateApplyLedgerLifecycleState.Disposed);
        GateApplyLedgerSnapshot[] path =
            [available, disposingAvailable, required, abandoning, abandoned, reclaimed, disposedReclaimed];
        for (int index = 1; index < path.Length; index++)
            Assert.AreEqual(GateApplyLedgerTransitionStatus.Valid,
                GateApplyLedgerTransitionResult.Validate(path[index - 1], path[index]).Status, $"dispose cleanup {index}");

        GateApplyLedgerSnapshot activated = ActivatedSnapshot(GateApplyLedgerLifecycleState.Active);
        GateApplyLedgerSnapshot disposingActivated = ActivatedSnapshot(GateApplyLedgerLifecycleState.Disposing);
        GateApplyLedgerSnapshot disposedActivated = ActivatedSnapshot(GateApplyLedgerLifecycleState.Disposed);
        Assert.AreEqual(GateApplyLedgerTransitionStatus.Valid,
            GateApplyLedgerTransitionResult.Validate(activated, disposingActivated).Status);
        Assert.AreEqual(GateApplyLedgerTransitionStatus.Valid,
            GateApplyLedgerTransitionResult.Validate(disposingActivated, disposedActivated).Status);
        Assert.AreEqual(GateCandidateOwnershipState.Activated, disposedActivated.CandidateOwnership);
        Assert.AreEqual(GateCandidateOwnershipState.Reclaimed, disposedReclaimed.CandidateOwnership);
    }

    [TestMethod]
    public void EveryMaterialOwnershipCanEnterDisposingButCannotHidePendingCleanup()
    {
        GateApplyLedgerSnapshot[] states =
        [
            Ledger(),
            Ledger(execution: GateHistoricalExecutionState.Running, coverage: GateCandidateCoverageState.Building),
            RejectedSnapshot(GateCandidateRetirementState.Dormant, GateCandidateOwnershipState.Available),
            Ledger(execution: GateHistoricalExecutionState.Completed, coverage: GateCandidateCoverageState.Complete,
                activation: GateActivationState.Activating),
            ActivatedSnapshot(GateApplyLedgerLifecycleState.Active),
            RejectedSnapshot(GateCandidateRetirementState.Abandoning, GateCandidateOwnershipState.Abandoning),
            RejectedSnapshot(GateCandidateRetirementState.LogicallyAbandoned,
                GateCandidateOwnershipState.LogicallyAbandoned),
            RejectedSnapshot(GateCandidateRetirementState.Reclaimed, GateCandidateOwnershipState.Reclaimed)
        ];
        foreach (GateApplyLedgerSnapshot state in states)
        {
            GateApplyLedgerSnapshot disposing = WithLifecycle(state, GateApplyLedgerLifecycleState.Disposing,
                GateApplyLedgerPendingResource.Observer | GateApplyLedgerPendingResource.Lease |
                GateApplyLedgerPendingResource.Tombstone);
            Assert.AreEqual(GateApplyLedgerTransitionStatus.Valid,
                GateApplyLedgerTransitionResult.Validate(state, disposing).Status, state.CandidateOwnership?.ToString());
            Assert.ThrowsExactly<ArgumentException>(() => WithLifecycle(state, GateApplyLedgerLifecycleState.Disposed,
                GateApplyLedgerPendingResource.Observer));
        }
    }

    [TestMethod]
    public void DisposeRoutesFromNoCandidateReservedAndMaterializingReachTerminalCleanup()
    {
        GateApplyLedgerSnapshot noCandidate = Snapshot();
        GateApplyLedgerSnapshot noCandidateDisposing = Snapshot(lifecycle: GateApplyLedgerLifecycleState.Disposing);
        GateApplyLedgerSnapshot noCandidateDisposed = Snapshot(lifecycle: GateApplyLedgerLifecycleState.Disposed);
        AssertValidDisposePath("none", [noCandidate, noCandidateDisposing, noCandidateDisposed], null);

        GateApplyLedgerSnapshot reserved = Ledger();
        GateApplyLedgerSnapshot reservedDisposing = Cleanup(GateHistoricalExecutionState.Dormant,
            GateCandidateCoverageState.Dormant, GateActivationState.Dormant,
            GateCandidateRetirementState.Dormant, GateCandidateOwnershipState.Reserved,
            pendingResources: GateApplyLedgerPendingResource.Tombstone, live: GateLiveEpochState.Active);
        GateApplyLedgerSnapshot reservedRequired = Cleanup(GateHistoricalExecutionState.Dormant,
            GateCandidateCoverageState.Dormant, GateActivationState.Abandoned,
            GateCandidateRetirementState.Required, GateCandidateOwnershipState.Reserved,
            pendingResources: GateApplyLedgerPendingResource.Tombstone);
        GateApplyLedgerSnapshot reservedAbandoning = Cleanup(GateHistoricalExecutionState.Dormant,
            GateCandidateCoverageState.Dormant, GateActivationState.Abandoned,
            GateCandidateRetirementState.Abandoning, GateCandidateOwnershipState.Abandoning,
            pendingResources: GateApplyLedgerPendingResource.Tombstone);
        GateApplyLedgerSnapshot reservedLogical = Cleanup(GateHistoricalExecutionState.Dormant,
            GateCandidateCoverageState.Dormant, GateActivationState.Abandoned,
            GateCandidateRetirementState.LogicallyAbandoned, GateCandidateOwnershipState.LogicallyAbandoned,
            pendingResources: GateApplyLedgerPendingResource.Tombstone);
        GateApplyLedgerSnapshot reservedReclaimed = Cleanup(GateHistoricalExecutionState.Dormant,
            GateCandidateCoverageState.Dormant, GateActivationState.Abandoned,
            GateCandidateRetirementState.Reclaimed, GateCandidateOwnershipState.Reclaimed);
        GateApplyLedgerSnapshot reservedDisposed = Cleanup(GateHistoricalExecutionState.Dormant,
            GateCandidateCoverageState.Dormant, GateActivationState.Abandoned,
            GateCandidateRetirementState.Reclaimed, GateCandidateOwnershipState.Reclaimed,
            lifecycle: GateApplyLedgerLifecycleState.Disposed);
        AssertValidDisposePath("reserved", [reserved, reservedDisposing, reservedRequired, reservedAbandoning,
            reservedLogical, reservedReclaimed, reservedDisposed], GateCandidateOwnershipState.Reclaimed);

        GateApplyLedgerSnapshot materializing = Ledger(execution: GateHistoricalExecutionState.Running,
            coverage: GateCandidateCoverageState.Building);
        GateApplyLedgerSnapshot materializingDisposing = Cleanup(GateHistoricalExecutionState.Running,
            GateCandidateCoverageState.Building, GateActivationState.Dormant,
            GateCandidateRetirementState.Dormant, GateCandidateOwnershipState.Materializing,
            pendingResources: GateApplyLedgerPendingResource.Tombstone, live: GateLiveEpochState.Active);
        GateApplyLedgerSnapshot materializingRequired = Cleanup(GateHistoricalExecutionState.Running,
            GateCandidateCoverageState.Building, GateActivationState.Abandoned,
            GateCandidateRetirementState.Required, GateCandidateOwnershipState.Materializing,
            pendingResources: GateApplyLedgerPendingResource.Tombstone, live: GateLiveEpochState.Active);
        GateApplyLedgerSnapshot materializingAbandoning = Cleanup(GateHistoricalExecutionState.Cancelled,
            GateCandidateCoverageState.Cancelled, GateActivationState.Abandoned,
            GateCandidateRetirementState.Abandoning, GateCandidateOwnershipState.Abandoning,
            pendingResources: GateApplyLedgerPendingResource.Tombstone);
        GateApplyLedgerSnapshot materializingLogical = Cleanup(GateHistoricalExecutionState.Cancelled,
            GateCandidateCoverageState.Cancelled, GateActivationState.Abandoned,
            GateCandidateRetirementState.LogicallyAbandoned, GateCandidateOwnershipState.LogicallyAbandoned,
            pendingResources: GateApplyLedgerPendingResource.Tombstone);
        GateApplyLedgerSnapshot materializingReclaimed = Cleanup(GateHistoricalExecutionState.Cancelled,
            GateCandidateCoverageState.Cancelled, GateActivationState.Abandoned,
            GateCandidateRetirementState.Reclaimed, GateCandidateOwnershipState.Reclaimed);
        GateApplyLedgerSnapshot materializingDisposed = Cleanup(GateHistoricalExecutionState.Cancelled,
            GateCandidateCoverageState.Cancelled, GateActivationState.Abandoned,
            GateCandidateRetirementState.Reclaimed, GateCandidateOwnershipState.Reclaimed,
            lifecycle: GateApplyLedgerLifecycleState.Disposed);
        AssertValidDisposePath("materializing", [materializing, materializingDisposing, materializingRequired,
            materializingAbandoning, materializingLogical, materializingReclaimed, materializingDisposed],
            GateCandidateOwnershipState.Reclaimed);
    }

    [TestMethod]
    public void DisposeRoutesFromActivatingCoverActivationAndAbandonmentRaces()
    {
        GateApplyLedgerSnapshot activating = Ledger(execution: GateHistoricalExecutionState.Completed,
            coverage: GateCandidateCoverageState.Complete, activation: GateActivationState.Activating);
        GateApplyLedgerSnapshot activatingDisposing = Cleanup(GateHistoricalExecutionState.Completed,
            GateCandidateCoverageState.Complete, GateActivationState.Activating,
            GateCandidateRetirementState.Dormant, GateCandidateOwnershipState.Activating,
            live: GateLiveEpochState.Active);
        GateApplyLedgerSnapshot activated = Cleanup(GateHistoricalExecutionState.Completed,
            GateCandidateCoverageState.Complete, GateActivationState.Activated,
            GateCandidateRetirementState.Activated, GateCandidateOwnershipState.Activated);
        GateApplyLedgerSnapshot activatedDisposed = Cleanup(GateHistoricalExecutionState.Completed,
            GateCandidateCoverageState.Complete, GateActivationState.Activated,
            GateCandidateRetirementState.Activated, GateCandidateOwnershipState.Activated,
            lifecycle: GateApplyLedgerLifecycleState.Disposed);
        AssertValidDisposePath("activating-wins", [activating, activatingDisposing, activated, activatedDisposed],
            GateCandidateOwnershipState.Activated);

        GateApplyLedgerSnapshot activatingDisposingAbandon = Cleanup(GateHistoricalExecutionState.Completed,
            GateCandidateCoverageState.Complete, GateActivationState.Activating,
            GateCandidateRetirementState.Dormant, GateCandidateOwnershipState.Activating,
            pendingResources: GateApplyLedgerPendingResource.Tombstone, live: GateLiveEpochState.Active);
        GateApplyLedgerSnapshot required = Cleanup(GateHistoricalExecutionState.Completed,
            GateCandidateCoverageState.Complete, GateActivationState.Abandoned,
            GateCandidateRetirementState.Required, GateCandidateOwnershipState.Available,
            pendingResources: GateApplyLedgerPendingResource.Tombstone);
        GateApplyLedgerSnapshot abandoning = Cleanup(GateHistoricalExecutionState.Completed,
            GateCandidateCoverageState.Complete, GateActivationState.Abandoned,
            GateCandidateRetirementState.Abandoning, GateCandidateOwnershipState.Abandoning,
            pendingResources: GateApplyLedgerPendingResource.Tombstone);
        GateApplyLedgerSnapshot logical = Cleanup(GateHistoricalExecutionState.Completed,
            GateCandidateCoverageState.Complete, GateActivationState.Abandoned,
            GateCandidateRetirementState.LogicallyAbandoned, GateCandidateOwnershipState.LogicallyAbandoned,
            pendingResources: GateApplyLedgerPendingResource.Tombstone);
        GateApplyLedgerSnapshot reclaimed = Cleanup(GateHistoricalExecutionState.Completed,
            GateCandidateCoverageState.Complete, GateActivationState.Abandoned,
            GateCandidateRetirementState.Reclaimed, GateCandidateOwnershipState.Reclaimed);
        GateApplyLedgerSnapshot disposed = Cleanup(GateHistoricalExecutionState.Completed,
            GateCandidateCoverageState.Complete, GateActivationState.Abandoned,
            GateCandidateRetirementState.Reclaimed, GateCandidateOwnershipState.Reclaimed,
            lifecycle: GateApplyLedgerLifecycleState.Disposed);
        AssertValidDisposePath("activating-abandonment", [activating, activatingDisposingAbandon, required, abandoning,
            logical, reclaimed, disposed], GateCandidateOwnershipState.Reclaimed);
    }

    [TestMethod]
    public void DisposeRoutesFromEveryRetirementStateCompleteWithoutReopening()
    {
        foreach (GateCandidateRetirementState start in new[] { GateCandidateRetirementState.Abandoning,
                     GateCandidateRetirementState.LogicallyAbandoned, GateCandidateRetirementState.Reclaimed })
        {
            GateCandidateOwnershipState ownership = start switch
            {
                GateCandidateRetirementState.Abandoning => GateCandidateOwnershipState.Abandoning,
                GateCandidateRetirementState.LogicallyAbandoned => GateCandidateOwnershipState.LogicallyAbandoned,
                _ => GateCandidateOwnershipState.Reclaimed
            };
            GateApplyLedgerSnapshot active = RejectedSnapshot(start, ownership);
            List<GateApplyLedgerSnapshot> path = [active, RejectedSnapshot(start, ownership,
                GateApplyLedgerLifecycleState.Disposing,
                start == GateCandidateRetirementState.Reclaimed ? GateApplyLedgerPendingResource.None :
                    GateApplyLedgerPendingResource.Tombstone)];
            if (start == GateCandidateRetirementState.Abandoning)
                path.Add(RejectedSnapshot(GateCandidateRetirementState.LogicallyAbandoned,
                    GateCandidateOwnershipState.LogicallyAbandoned, GateApplyLedgerLifecycleState.Disposing,
                    GateApplyLedgerPendingResource.Tombstone));
            if (start != GateCandidateRetirementState.Reclaimed)
                path.Add(RejectedSnapshot(GateCandidateRetirementState.Reclaimed,
                    GateCandidateOwnershipState.Reclaimed, GateApplyLedgerLifecycleState.Disposing));
            path.Add(RejectedSnapshot(GateCandidateRetirementState.Reclaimed,
                GateCandidateOwnershipState.Reclaimed, GateApplyLedgerLifecycleState.Disposed));
            AssertValidDisposePath(start.ToString(), path, GateCandidateOwnershipState.Reclaimed);
        }
    }

    [TestMethod]
    public void DisposingRejectsNewWorkAndAllowsOnlyAdmittedCleanup()
    {
        GateApplyLedgerSnapshot empty = Snapshot(lifecycle: GateApplyLedgerLifecycleState.Disposing);
        GateApplyLedgerSnapshot newAdmission = Snapshot(admission: GateHistoricalAdmissionState.Deferred,
            lifecycle: GateApplyLedgerLifecycleState.Disposing);
        Assert.AreEqual(GateApplyLedgerTransitionStatus.IllegalStageTransition,
            GateApplyLedgerTransitionResult.Validate(empty, newAdmission).Status);

        GateApplyLedgerSnapshot reserved = Cleanup(GateHistoricalExecutionState.Dormant,
            GateCandidateCoverageState.Dormant, GateActivationState.Dormant,
            GateCandidateRetirementState.Dormant, GateCandidateOwnershipState.Reserved,
            live: GateLiveEpochState.Active);
        GateApplyLedgerSnapshot queued = Cleanup(GateHistoricalExecutionState.Queued,
            GateCandidateCoverageState.Dormant, GateActivationState.Dormant,
            GateCandidateRetirementState.Dormant, GateCandidateOwnershipState.Reserved,
            live: GateLiveEpochState.Active);
        Assert.AreEqual(GateApplyLedgerTransitionStatus.IllegalStageTransition,
            GateApplyLedgerTransitionResult.Validate(reserved, queued).Status);

        GateApplyLedgerSnapshot eligible = Cleanup(GateHistoricalExecutionState.Completed,
            GateCandidateCoverageState.Complete, GateActivationState.Eligible,
            GateCandidateRetirementState.Dormant, GateCandidateOwnershipState.Available,
            live: GateLiveEpochState.Active);
        GateApplyLedgerSnapshot activating = Cleanup(GateHistoricalExecutionState.Completed,
            GateCandidateCoverageState.Complete, GateActivationState.Activating,
            GateCandidateRetirementState.Dormant, GateCandidateOwnershipState.Activating,
            live: GateLiveEpochState.Active);
        Assert.AreEqual(GateApplyLedgerTransitionStatus.IllegalStageTransition,
            GateApplyLedgerTransitionResult.Validate(eligible, activating).Status);

        GateApplyLedgerSnapshot withObserver = Snapshot(lifecycle: GateApplyLedgerLifecycleState.Disposing,
            pendingResources: GateApplyLedgerPendingResource.Observer);
        Assert.AreEqual(GateApplyLedgerTransitionStatus.IllegalStageTransition,
            GateApplyLedgerTransitionResult.Validate(empty, withObserver).Status);

        GateStoreOnlyCandidateDescriptor candidate = Candidate(Manifest());
        var request = new GateCandidateActivationRequest(candidate, null, Run, new(8));
        Assert.AreEqual(GateCandidateActivationStatus.Disposed,
            Classify(request, lifecycle: GateApplyLedgerLifecycleState.Disposing).Status);
    }

    [TestMethod]
    public void DisposingRejectsStartingCandidateMaterializationWhileActiveAllowsIt()
    {
        GateApplyLedgerSnapshot activeBefore = Ledger(execution: GateHistoricalExecutionState.Running);
        GateApplyLedgerSnapshot activeBuilding = Ledger(execution: GateHistoricalExecutionState.Running,
            coverage: GateCandidateCoverageState.Building);
        Assert.AreEqual(GateApplyLedgerLifecycleState.Active, activeBefore.Lifecycle);
        Assert.AreEqual(GateHistoricalAdmissionState.Admitted, activeBefore.HistoricalAdmission);
        Assert.AreEqual(GateHistoricalExecutionState.Running, activeBefore.HistoricalExecution);
        Assert.AreEqual(GateCandidateCoverageState.Dormant, activeBefore.CandidateCoverage);
        Assert.AreEqual(GateCandidateOwnershipState.Reserved, activeBefore.CandidateOwnership);
        Assert.AreEqual(GateCandidateCoverageState.Building, activeBuilding.CandidateCoverage);
        Assert.AreEqual(GateCandidateOwnershipState.Materializing, activeBuilding.CandidateOwnership);
        Assert.AreEqual(GateApplyLedgerTransitionStatus.Valid,
            GateApplyLedgerTransitionResult.Validate(activeBefore, activeBuilding).Status);

        GateApplyLedgerSnapshot disposingBefore = Cleanup(GateHistoricalExecutionState.Running,
            GateCandidateCoverageState.Dormant, GateActivationState.Dormant,
            GateCandidateRetirementState.Dormant, GateCandidateOwnershipState.Reserved,
            live: GateLiveEpochState.Active);
        GateApplyLedgerSnapshot disposingBuilding = Cleanup(GateHistoricalExecutionState.Running,
            GateCandidateCoverageState.Building, GateActivationState.Dormant,
            GateCandidateRetirementState.Dormant, GateCandidateOwnershipState.Materializing,
            live: GateLiveEpochState.Active);
        Assert.AreEqual(GateApplyLedgerLifecycleState.Disposing, disposingBefore.Lifecycle);
        Assert.AreEqual(activeBefore.HistoricalAdmission, disposingBefore.HistoricalAdmission);
        Assert.AreEqual(activeBefore.HistoricalExecution, disposingBefore.HistoricalExecution);
        Assert.AreEqual(activeBefore.CandidateCoverage, disposingBefore.CandidateCoverage);
        Assert.AreEqual(activeBefore.CandidateOwnership, disposingBefore.CandidateOwnership);
        Assert.AreEqual(activeBuilding.HistoricalAdmission, disposingBuilding.HistoricalAdmission);
        Assert.AreEqual(activeBuilding.HistoricalExecution, disposingBuilding.HistoricalExecution);
        Assert.AreEqual(activeBuilding.CandidateCoverage, disposingBuilding.CandidateCoverage);
        Assert.AreEqual(activeBuilding.CandidateOwnership, disposingBuilding.CandidateOwnership);
        GateApplyLedgerTransitionResult rejected =
            GateApplyLedgerTransitionResult.Validate(disposingBefore, disposingBuilding);
        Assert.AreEqual(GateApplyLedgerTransitionStatus.IllegalStageTransition, rejected.Status);
        Assert.AreEqual(GateLedgerStage.Lifecycle, rejected.Stage);
    }

    [TestMethod]
    public void AbandonAndReclaimRespectActiveDisposingAndDisposedLifecycle()
    {
        GateStoreOnlyCandidateDescriptor logical = Candidate(Manifest(),
            GateCandidateOwnershipState.LogicallyAbandoned);
        GateStoreOnlyCandidateDescriptor reclaimed = Candidate(Manifest(), GateCandidateOwnershipState.Reclaimed);
        Assert.AreEqual(GateCandidateAbandonStatus.LogicallyAbandoned,
            GateCandidateAbandonResult.Create(GateCandidateAbandonStatus.LogicallyAbandoned, logical).Status);
        Assert.AreEqual(GateCandidateAbandonStatus.LogicallyAbandoned,
            GateCandidateAbandonResult.Create(GateCandidateAbandonStatus.LogicallyAbandoned, logical,
                GateApplyLedgerLifecycleState.Disposing).Status);
        Assert.AreEqual(GateCandidateReclaimStatus.Busy,
            GateCandidateReclaimResult.Create(GateCandidateReclaimStatus.Busy, logical,
                GateApplyLedgerLifecycleState.Disposing).Status);
        Assert.ThrowsExactly<ArgumentException>(() => GateCandidateReclaimResult.Create(GateCandidateReclaimStatus.Busy,
            Candidate(Manifest(), GateCandidateOwnershipState.Abandoning), GateApplyLedgerLifecycleState.Disposing));
        Assert.AreEqual(GateCandidateReclaimStatus.Reclaimed,
            GateCandidateReclaimResult.Create(GateCandidateReclaimStatus.Reclaimed, reclaimed,
                GateApplyLedgerLifecycleState.Disposing).Status);
        Assert.AreEqual(GateCandidateReclaimStatus.Disposed,
            GateCandidateReclaimResult.Create(GateCandidateReclaimStatus.Disposed, reclaimed,
                GateApplyLedgerLifecycleState.Disposed).Status);
        Assert.ThrowsExactly<ArgumentException>(() => GateCandidateReclaimResult.Create(
            GateCandidateReclaimStatus.Reclaimed, reclaimed, GateApplyLedgerLifecycleState.Disposed));

        GateApplyLedgerSnapshot busy = RejectedSnapshot(GateCandidateRetirementState.LogicallyAbandoned,
            GateCandidateOwnershipState.LogicallyAbandoned, GateApplyLedgerLifecycleState.Disposing,
            GateApplyLedgerPendingResource.Tombstone);
        Assert.ThrowsExactly<ArgumentException>(() => RejectedSnapshot(
            GateCandidateRetirementState.LogicallyAbandoned, GateCandidateOwnershipState.LogicallyAbandoned,
            GateApplyLedgerLifecycleState.Disposed, GateApplyLedgerPendingResource.Tombstone));
        GateApplyLedgerSnapshot physicallyReclaimed = RejectedSnapshot(GateCandidateRetirementState.Reclaimed,
            GateCandidateOwnershipState.Reclaimed, GateApplyLedgerLifecycleState.Disposing);
        GateApplyLedgerSnapshot disposed = RejectedSnapshot(GateCandidateRetirementState.Reclaimed,
            GateCandidateOwnershipState.Reclaimed, GateApplyLedgerLifecycleState.Disposed);
        AssertValidDisposePath("busy-reclaim", [busy, physicallyReclaimed, disposed],
            GateCandidateOwnershipState.Reclaimed, assertLifecycleSkip: false);
    }

    private static void AssertValidDisposePath(string name, IReadOnlyList<GateApplyLedgerSnapshot> path,
        GateCandidateOwnershipState? terminalOwnership, bool assertLifecycleSkip = true)
    {
        Assert.IsGreaterThanOrEqualTo(2, path.Count);
        for (int index = 1; index < path.Count; index++)
        {
            GateApplyLedgerTransitionResult result = GateApplyLedgerTransitionResult.Validate(path[index - 1], path[index]);
            Assert.AreEqual(GateApplyLedgerTransitionStatus.Valid, result.Status, $"{name} edge {index}");
            Assert.AreEqual(path[0].InspectionId, path[index].InspectionId, $"{name} inspection {index}");
            Assert.AreEqual(path[0].RunId, path[index].RunId, $"{name} run {index}");
            Assert.AreEqual(path[0].PlanId, path[index].PlanId, $"{name} plan {index}");
            Assert.AreEqual(path[0].PlanRevision, path[index].PlanRevision, $"{name} revision {index}");
            IReadOnlyList<GateLedgerCause> before = AllCauses(path[index - 1]);
            IReadOnlyList<GateLedgerCause> after = AllCauses(path[index]);
            Assert.IsTrue(before.All(after.Contains), $"{name} causes must be append-only at edge {index}.");
        }
        GateApplyLedgerSnapshot final = path[^1];
        Assert.AreEqual(GateApplyLedgerLifecycleState.Disposed, final.Lifecycle, name);
        Assert.AreEqual(GateApplyLedgerPendingResource.None, final.PendingResources, name);
        Assert.AreEqual(terminalOwnership, final.CandidateOwnership, name);
        Assert.AreEqual(GateApplyLedgerTransitionStatus.TerminalReopened,
            GateApplyLedgerTransitionResult.Validate(final, path[0]).Status, $"{name} terminal reopen");
        if (assertLifecycleSkip && path.Count > 2)
            Assert.AreEqual(GateApplyLedgerTransitionStatus.IllegalStageTransition,
                GateApplyLedgerTransitionResult.Validate(path[0], final).Status, $"{name} lifecycle skip");
    }

    private static IReadOnlyList<GateLedgerCause> AllCauses(GateApplyLedgerSnapshot snapshot) =>
        snapshot.PrimaryCause is { } primary ? [primary, .. snapshot.SecondaryCauses] : snapshot.SecondaryCauses;

    private static GateApplyLedgerSnapshot Cleanup(GateHistoricalExecutionState execution,
        GateCandidateCoverageState coverage, GateActivationState activation,
        GateCandidateRetirementState retirement, GateCandidateOwnershipState ownership,
        GateApplyLedgerLifecycleState lifecycle = GateApplyLedgerLifecycleState.Disposing,
        GateApplyLedgerPendingResource pendingResources = GateApplyLedgerPendingResource.None,
        GateLiveEpochState live = GateLiveEpochState.Drained)
    {
        List<GateLedgerCause> secondary = [];
        if (execution == GateHistoricalExecutionState.Cancelled)
            secondary.Add(new(GateLedgerStage.HistoricalExecution, GateLedgerCauseKind.Cancelled));
        if (coverage == GateCandidateCoverageState.Cancelled)
            secondary.Add(new(GateLedgerStage.CandidateCoverage, GateLedgerCauseKind.Cancelled));
        if (activation == GateActivationState.Abandoned)
            secondary.Add(new(GateLedgerStage.Activation, GateLedgerCauseKind.CandidateAbandoned));
        return GateApplyLedgerSnapshot.Create(Inspection, Run, new(1), new("plan"), new(1),
            GateApplyCommitState.Accepted, live, GateHistoricalAdmissionState.Admitted, execution, coverage,
            activation, retirement,
            activation == GateActivationState.Activated ? GatePreviousViewRetirementState.NotApplicable :
                GatePreviousViewRetirementState.Dormant,
            ownership, new(GateLedgerStage.Lifecycle, GateLedgerCauseKind.Disposed), secondary,
            lifecycle, pendingResources);
    }

    private static CommittedGatePlanSnapshot Committed(uint revision) => new(Inspection,
        new GatePlanCompiler().Compile(GateTestPlan.Plan([GateTestPlan.Gate("A")], revision: revision)).CompiledPlan!);
    private static GatePlanDraft Draft(GatePlanRevision revision, IEnumerable<GateDefinition>? gates = null) => new(
        new(Inspection, new("plan")), revision, new(1), new(1), gates ?? [GateTestPlan.Gate("A")]);
    private static GateStoreOnlyCandidateDescriptor Candidate(GateDerivedCoverageManifest manifest,
        GateCandidateOwnershipState state = GateCandidateOwnershipState.Available, ulong replacementToken = 4) =>
        new(manifest, new("candidate"), new(replacementToken), new(7), state);

    private static GatePreparedReservation Reservation(GatePreparedReservationState state) => new(
        new(Guid.Parse("73000000-0000-0000-0000-000000000001")),
        new(Guid.Parse("74000000-0000-0000-0000-000000000001")), new(1), state);

    private static GateCandidateActivationResult Classify(GateCandidateActivationRequest request,
        GateApplyLedgerLifecycleState lifecycle = GateApplyLedgerLifecycleState.Active,
        AcquisitionRunId? runId = null, ulong runGeneration = 8, ulong token = 4,
        ulong replacementGeneration = 7, bool noDesiredToken = false,
        GateStoreOnlyCandidateDescriptor? candidate = null,
        GateCandidateOwnershipState? ownership = GateCandidateOwnershipState.Available,
        GateObservedPredecessorKind predecessorKind = GateObservedPredecessorKind.None,
        ActiveDerivedViewDescriptor? predecessor = null, ActiveDerivedViewDescriptor? active = null,
        bool capacity = true)
    {
        GateStoreOnlyCandidateDescriptor? observed = ownership == GateCandidateOwnershipState.Reclaimed ? null :
            candidate ?? new(request.Candidate.Manifest, request.Candidate.CandidateViewId,
                new GateReplacementToken(token), new GateReplacementGeneration(replacementGeneration), ownership!.Value);
        var state = new GateReplacementCasObservedState(lifecycle, request.Candidate.Manifest.InspectionId,
            runId ?? Run, request.Candidate.Manifest.PlanId, request.Candidate.Manifest.PlanRevision, new(runGeneration),
            noDesiredToken ? null : new GateReplacementToken(token),
            noDesiredToken ? null : new GateReplacementGeneration(replacementGeneration),
            predecessorKind, predecessor, observed, ownership, capacity, active);
        return GateCandidateActivationResult.Classify(request, state);
    }

    private static GateDerivedCoverageManifest Manifest(ulong sourceRevision = 1, ushort schema = 1,
        ushort algorithm = 1, string scope = "full", string source = "source",
        IReadOnlyList<AcquisitionIndexRange>? selectedRanges = null, byte digestSeed = 1, int eligibleCount = 1)
    {
        var stream = new UtStreamIdentity(Inspection, Run, new UtSourceId(source), new PhysicalChannelId(0));
        byte[] digest = Enumerable.Repeat(digestSeed, 32).ToArray();
        ulong maximum = (ulong)eligibleCount;
        var coverage = new GateDerivedStreamCoverage(stream, 1, maximum, eligibleCount, digest, selectedRanges);
        CompiledGatePlan compiled = Committed(1).CompiledPlan;
        return new(Inspection, Run, [coverage], new(sourceRevision), compiled.Source.Id, compiled.Source.Revision,
            compiled.Signature.Hex, new(schema), new(algorithm), eligibleCount, new(2), scope);
    }

    private static GateDerivedCoverageManifest ManifestVariant(InspectionId? inspection = null,
        AcquisitionRunId? run = null, GatePlanId? planId = null, GatePlanRevision? planRevision = null,
        string? planSignature = null, DerivedStoreRevision? capturedRevision = null, ulong minimum = 1,
        ulong maximum = 1)
    {
        InspectionId actualInspection = inspection ?? Inspection;
        AcquisitionRunId actualRun = run ?? Run;
        var stream = new UtStreamIdentity(actualInspection, actualRun, new("source"), new(0));
        var coverage = new GateDerivedStreamCoverage(stream, minimum, maximum, 1,
            Enumerable.Repeat((byte)1, 32).ToArray());
        CompiledGatePlan compiled = Committed(1).CompiledPlan;
        return new(actualInspection, actualRun, [coverage], new(1), planId ?? compiled.Source.Id,
            planRevision ?? compiled.Source.Revision, planSignature ?? compiled.Signature.Hex, new(1), new(1), 1,
            capturedRevision ?? new(2));
    }

    private static void AssertObservedRejected(GateStoreOnlyCandidateDescriptor? candidate,
        GateReplacementToken? desiredToken = null, GateReplacementGeneration? desiredGeneration = null,
        GateCandidateOwnershipState? ownership = GateCandidateOwnershipState.Available) =>
        Assert.ThrowsExactly<ArgumentException>(() => new GateReplacementCasObservedState(
            GateApplyLedgerLifecycleState.Active, Inspection, Run, new("plan"), new(1), new(8),
            desiredToken ?? new(4), desiredGeneration ?? new(7), GateObservedPredecessorKind.None, null,
            candidate, ownership, true, null));

    private static GateDerivedCoverageManifest TwoStreamManifest(bool reverse)
    {
        GateDerivedCoverageManifest basis = Manifest(); CompiledGatePlan compiled = Committed(1).CompiledPlan;
        GateDerivedStreamCoverage Coverage(string source, int channel, byte seed) => new(
            new(Inspection, Run, new(source), new(channel)), 1, 1, 1,
            Enumerable.Repeat(seed, 32).ToArray());
        GateDerivedStreamCoverage[] streams = [Coverage("a", 0, 1), Coverage("b", 1, 2)];
        if (reverse) Array.Reverse(streams);
        return new(Inspection, Run, streams, new(1), compiled.Source.Id, compiled.Source.Revision,
            compiled.Signature.Hex, new(1), new(1), 2, new(2));
    }

    private static GateApplyLedgerSnapshot Ledger(
        GateHistoricalAdmissionState admission = GateHistoricalAdmissionState.Admitted,
        GateHistoricalExecutionState execution = GateHistoricalExecutionState.Dormant,
        GateCandidateCoverageState coverage = GateCandidateCoverageState.Dormant,
        GateActivationState activation = GateActivationState.Dormant,
        GateCandidateRetirementState candidateRetirement = GateCandidateRetirementState.Dormant,
        GatePreviousViewRetirementState previousRetirement = GatePreviousViewRetirementState.Dormant,
        GateLedgerCause? primaryCause = null)
    {
        GateCandidateOwnershipState? ownership = activation switch
        {
            GateActivationState.Eligible => GateCandidateOwnershipState.Available,
            GateActivationState.Activating => GateCandidateOwnershipState.Activating,
            GateActivationState.Activated => GateCandidateOwnershipState.Activated,
            _ => candidateRetirement switch
            {
                GateCandidateRetirementState.LogicallyAbandoned => GateCandidateOwnershipState.LogicallyAbandoned,
                GateCandidateRetirementState.Reclaimed => GateCandidateOwnershipState.Reclaimed,
                GateCandidateRetirementState.Activated => GateCandidateOwnershipState.Activated,
                GateCandidateRetirementState.Abandoning => GateCandidateOwnershipState.Abandoning,
                GateCandidateRetirementState.Required => GateCandidateOwnershipState.Available,
                _ => coverage switch
                {
                    GateCandidateCoverageState.Building => GateCandidateOwnershipState.Materializing,
                    GateCandidateCoverageState.Complete => GateCandidateOwnershipState.Available,
                    _ => admission == GateHistoricalAdmissionState.Admitted ? GateCandidateOwnershipState.Reserved : null
                }
            }
        };
        return GateApplyLedgerSnapshot.Create(Inspection, Run, new(1), new("plan"), new(1),
            GateApplyCommitState.Accepted, GateLiveEpochState.Active, admission, execution, coverage, activation,
            candidateRetirement, previousRetirement, ownership, primaryCause);
    }

    private static GateApplyLedgerSnapshot Snapshot(InspectionId? inspectionId = null,
        GatePlanRevision? planRevision = null, GateHistoricalAdmissionState admission = GateHistoricalAdmissionState.Dormant,
        GateHistoricalExecutionState execution = GateHistoricalExecutionState.Dormant,
        GateCandidateCoverageState coverage = GateCandidateCoverageState.Dormant,
        GateActivationState activation = GateActivationState.Dormant,
        GateCandidateRetirementState retirement = GateCandidateRetirementState.Dormant,
        GatePreviousViewRetirementState previousRetirement = GatePreviousViewRetirementState.Dormant,
        GateCandidateOwnershipState? candidateOwnership = null,
        GateApplyLedgerLifecycleState lifecycle = GateApplyLedgerLifecycleState.Active,
        GateApplyLedgerPendingResource pendingResources = GateApplyLedgerPendingResource.None)
    {
        GateLedgerCause? lifecycleCause = lifecycle == GateApplyLedgerLifecycleState.Active ? null :
            new(GateLedgerStage.Lifecycle, GateLedgerCauseKind.Disposed);
        return GateApplyLedgerSnapshot.Create(
            inspectionId ?? Inspection, Run, new(1), new("plan"), planRevision ?? new(1),
            GateApplyCommitState.Accepted, GateLiveEpochState.Dormant, admission, execution, coverage,
            activation, retirement, previousRetirement, candidateOwnership, lifecycleCause,
            lifecycle: lifecycle, pendingResources: pendingResources);
    }

    private static GateApplyLedgerSnapshot RejectedSnapshot(GateCandidateRetirementState retirement,
        GateCandidateOwnershipState ownership,
        GateApplyLedgerLifecycleState lifecycle = GateApplyLedgerLifecycleState.Active,
        GateApplyLedgerPendingResource pendingResources = GateApplyLedgerPendingResource.None) =>
        GateApplyLedgerSnapshot.Create(Inspection, Run, new(1), new("plan"), new(1),
            GateApplyCommitState.Accepted,
            lifecycle == GateApplyLedgerLifecycleState.Active ? GateLiveEpochState.Active : GateLiveEpochState.Drained,
            GateHistoricalAdmissionState.Admitted,
            GateHistoricalExecutionState.Completed, GateCandidateCoverageState.Complete, GateActivationState.CasRejected,
            retirement, GatePreviousViewRetirementState.NotApplicable, ownership,
            new(GateLedgerStage.Activation, GateLedgerCauseKind.CandidateNotAvailable),
            lifecycle == GateApplyLedgerLifecycleState.Active ? null :
                [new(GateLedgerStage.Lifecycle, GateLedgerCauseKind.Disposed)], lifecycle, pendingResources);

    private static GateApplyLedgerSnapshot ActivatedSnapshot(GateApplyLedgerLifecycleState lifecycle) =>
        GateApplyLedgerSnapshot.Create(Inspection, Run, new(1), new("plan"), new(1),
            GateApplyCommitState.Accepted, GateLiveEpochState.Drained, GateHistoricalAdmissionState.Admitted,
            GateHistoricalExecutionState.Completed, GateCandidateCoverageState.Complete, GateActivationState.Activated,
            GateCandidateRetirementState.Activated, GatePreviousViewRetirementState.Retired,
            GateCandidateOwnershipState.Activated,
            lifecycle == GateApplyLedgerLifecycleState.Active ? null :
                new(GateLedgerStage.Lifecycle, GateLedgerCauseKind.Disposed),
            lifecycle: lifecycle);

    private static GateApplyLedgerSnapshot WithLifecycle(GateApplyLedgerSnapshot source,
        GateApplyLedgerLifecycleState lifecycle, GateApplyLedgerPendingResource pendingResources)
    {
        var lifecycleCause = new GateLedgerCause(GateLedgerStage.Lifecycle, GateLedgerCauseKind.Disposed);
        GateLedgerCause? primary = source.PrimaryCause ?? lifecycleCause;
        IEnumerable<GateLedgerCause>? secondary = source.PrimaryCause is null ? source.SecondaryCauses :
            source.SecondaryCauses.Concat([lifecycleCause]);
        return GateApplyLedgerSnapshot.Create(source.InspectionId, source.RunId, source.RunGeneration, source.PlanId,
            source.PlanRevision, source.Commit, source.LiveEpoch, source.HistoricalAdmission,
            source.HistoricalExecution, source.CandidateCoverage, source.Activation, source.CandidateRetirement,
            source.PreviousViewRetirement, source.CandidateOwnership, primary, secondary, lifecycle, pendingResources);
    }
}
