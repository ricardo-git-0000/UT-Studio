using UTStudio.Contracts.Inspection;
using UTStudio.Contracts.Processing;
using UTStudio.Domain.Acquisition;
using UTStudio.Domain.Inspection;
using UTStudio.Domain.Processing;
using UTStudio.Processing.Core;

namespace UTStudio.Core.Tests.Processing;

[TestClass]
public sealed class GateReprocessingPaginationFaultTests
{
    private static readonly InspectionId Inspection = new(Guid.Parse("81818181-8181-8181-8181-818181818181"));
    private static readonly AcquisitionRunId Run = new(Guid.Parse("82828282-8282-8282-8282-828282828282"));
    private static readonly UtStreamIdentity Stream = new(Inspection, Run, new UtSourceId("fault-page"), new PhysicalChannelId(0));

    [TestMethod] public Task F07_FirstPageFailure() => RunAsync(Mode.FirstPageFailure, nameof(InvalidDataException));
    [TestMethod] public Task F08_IntermediatePageFailure() => RunAsync(Mode.IntermediatePageFailure, nameof(InvalidDataException));
    [TestMethod] public Task F09_InvalidCursorFailure() => RunAsync(Mode.InvalidCursor, nameof(ArgumentException));
    [TestMethod] public Task F10A_DuplicateEnumeratedIdentity() => RunAsync(Mode.Duplicate, "coverage-mismatch:duplicate-or-order");
    [TestMethod] public Task F10B_OutOfOrderEnumeratedIdentity() => RunAsync(Mode.OutOfOrder, "coverage-mismatch:duplicate-or-order");
    [TestMethod] public Task F10C_WrongEnumeratedStream() => RunAsync(Mode.WrongStream, "coverage-mismatch:enumerated-identity");
    [TestMethod] public Task F11_ExactReadThrows() => RunAsync(Mode.ExactReadThrows, nameof(InvalidDataException));
    [TestMethod]
    public Task F13A_EnumeratedIdentityBecomesNotAvailableBeforeLeaseAcquisition() =>
        RunAsync(Mode.NotAvailable, "coverage-mismatch:NotAvailable");

    private static async Task RunAsync(Mode mode, string expectedPrimary)
    {
        var provider = new FaultingProvider(mode);
        await using var derived = new InMemoryGateDerivedStore();
        await using var scheduler = new GateReprocessingScheduler(provider, derived);
        CompiledGatePlan plan = new GatePlanCompiler().Compile(
            GateTestPlan.Plan([GateTestPlan.Gate("A", durationSeconds: 0.000001)])).CompiledPlan!;
        var descriptor = new GateReprocessingJobDescriptor(new(Guid.NewGuid()), new(Guid.NewGuid()),
            Inspection, Run, new(1), [new GateReprocessingStreamSelection(Stream, [new(0, 1)])],
            plan, 1, GateReprocessingPublicationPolicy.ActivateOnComplete,
            new DerivedViewId("must-not-publish"), snapshotPageSize: 1);

        GateReprocessingAdmissionResult admission = await scheduler.AdmitAsync(descriptor);
        Assert.AreEqual(GateReprocessingAdmissionStatus.Admitted, admission.Status, admission.ErrorCode);
        GateReprocessingProgressSnapshot terminal = await admission.Job!.Completion;

        Assert.AreEqual(GateReprocessingLifecycle.Faulted, terminal.Lifecycle);
        StringAssert.StartsWith(terminal.PrimaryError, expectedPrimary);
        int expectedCommittedPrefix = mode is Mode.IntermediatePageFailure or Mode.InvalidCursor or
            Mode.Duplicate or Mode.OutOfOrder ? 1 : 0;
        Assert.AreEqual(expectedCommittedPrefix, derived.Diagnostics.BatchCount);
        Assert.AreEqual(expectedCommittedPrefix, terminal.CommitProgress!.Completed);
        Assert.AreEqual(0, derived.Diagnostics.TerminalEvidenceCount);
        Assert.IsNull(derived.ActiveView);
        Assert.AreEqual(0, terminal.ActiveWorkers); Assert.AreEqual(0, terminal.ActiveCallbacks);
        Assert.AreEqual(0, terminal.ActiveLeases); Assert.AreEqual(0, terminal.ActiveBuffers);
        Assert.AreEqual(0, terminal.ActiveScratch); Assert.IsTrue(terminal.CleanupConfirmed);
        Assert.AreEqual(terminal.LeasesAcquired, terminal.LeasesReleased);
        Assert.AreEqual(terminal.BufferRents, terminal.BufferReturns);
        Assert.AreEqual(terminal.ScratchRents, terminal.ScratchReturns);
        Assert.AreEqual(0, terminal.CoverageProgress!.Attempts);
        Assert.AreEqual(0, terminal.ActivationProgress!.Attempts);
        Assert.AreEqual(0, derived.Diagnostics.AdmittedWriters);
        Assert.AreEqual(0, derived.Diagnostics.ReservedBatchCount);
        Assert.AreEqual(0, derived.Diagnostics.ReservedCoverageCount);
        Assert.AreEqual(0, derived.Diagnostics.ReservedActivationCount);
    }

    private enum Mode
    {
        FirstPageFailure, IntermediatePageFailure, InvalidCursor, Duplicate,
        OutOfOrder, WrongStream, ExactReadThrows, NotAvailable
    }

    private sealed class FaultingProvider(Mode mode) : IInspectionSealedRunSnapshotProvider
    {
        public ValueTask<InspectionSealedRunSnapshotOpenResult> OpenSealedRunSnapshotAsync(
            InspectionSealedRunSnapshotRequest request, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(InspectionSealedRunSnapshotOpenResult.Available(new Snapshot(mode)));

        private sealed class Snapshot(Mode mode) : IInspectionSealedRunSnapshot
        {
            private int _page;
            public ushort ContractVersion => IInspectionSealedRunSnapshot.CurrentContractVersion;
            public InspectionId InspectionId => Inspection;
            public AcquisitionRunId RunId => Run;
            public InspectionRevision SourceFinalRevision => new(1);
            public InspectionRunState RunState => InspectionRunState.Sealed;
            public int MaximumPageSize => 1;
            public IReadOnlyList<InspectionSealedStreamSnapshot> Streams { get; } =
                [new(Stream, 0, 1, 2, new string('0', 64))];
            public InspectionSealedRunEnumerationDiagnostics EnumerationDiagnostics => default;
            public long CountIdentities(InspectionSealedRunRangeQuery query) => 2;
            public InspectionSealedRunIdentityPage ReadIdentities(UtStreamIdentity stream,
                InspectionSealedRunCursor? cursor = null) => ReadIdentities(
                    new InspectionSealedRunRangeQuery(stream, [new(0, 1)], 1), cursor);
            public InspectionSealedRunIdentityPage ReadIdentities(InspectionSealedRunRangeQuery query,
                InspectionSealedRunCursor? cursor = null)
            {
                int page = _page++;
                if (mode == Mode.FirstPageFailure && page == 0) throw new InvalidDataException("first-page");
                if (mode == Mode.IntermediatePageFailure && page == 1) throw new InvalidDataException("middle-page");
                if (mode == Mode.InvalidCursor && page == 1) throw new ArgumentException("cursor");
                if (page > 1) return new([], null);
                UtStreamIdentity stream = mode == Mode.WrongStream
                    ? new(Inspection, Run, new UtSourceId("wrong"), new PhysicalChannelId(9)) : Stream;
                ulong index = (mode, page) switch
                {
                    (Mode.Duplicate, 1) => 0,
                    (Mode.OutOfOrder, 0) => 1,
                    (Mode.OutOfOrder, 1) => 0,
                    _ => checked((ulong)page)
                };
                return new([new TraceIdentity(stream, index)], page == 0 ? new("next") : null);
            }
            public ValueTask<InspectionTraceReadResult> ReadAsync(TraceIdentity identity,
                CancellationToken cancellationToken = default)
            {
                if (mode == Mode.ExactReadThrows) throw new InvalidDataException("exact-read");
                if (mode == Mode.NotAvailable) return ValueTask.FromResult(
                    InspectionTraceReadResult.Unavailable(InspectionReadStatus.NotAvailable, new(1)));
                var descriptor = new TraceDescriptor(identity, identity.AcquisitionIndex, TimeSpan.Zero,
                    null, null, 4, 10_000_000, 0, UtSignalMode.Rf, traceQuality: UtTraceQuality.Good);
                return ValueTask.FromResult(InspectionTraceReadResult.Available(new(1), new Lease(descriptor)));
            }
            public void Dispose() { }
        }
    }

    private sealed class Lease(TraceDescriptor descriptor) : IInspectionTraceLease
    {
        public TraceDescriptor Descriptor => descriptor;
        public IReadOnlyUtSamples Samples { get; } = new SampleSource();
        public void Dispose() { }
        private sealed class SampleSource : IReadOnlyUtSamples
        {
            public int Count => 4;
            public short this[int index] => new short[] { 1, 4, 2, 0 }[index];
            public void CopyTo(Span<short> destination) => new short[] { 1, 4, 2, 0 }.CopyTo(destination);
        }
    }
}
