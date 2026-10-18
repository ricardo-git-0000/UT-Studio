using UTStudio.Contracts.Processing;
using UTStudio.Contracts.Inspection;
using UTStudio.Domain.Acquisition;
using UTStudio.Domain.Inspection;

namespace UTStudio.Core.Tests.Processing;

[TestClass]
public sealed class GateReprocessingDiagnosticsContractTests
{
    [TestMethod]
    public void AdmissionResultRejectsImpossibleStatesAndCopiesSecondaryErrors()
    {
        GateReprocessingAdmissionResult empty = default;
        Assert.AreEqual(GateReprocessingAdmissionStatus.Unknown, empty.Status);
        Assert.IsNull(empty.Job);
        Assert.IsEmpty(empty.SecondaryErrors);
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new GateReprocessingAdmissionResult(
            GateReprocessingAdmissionStatus.Unknown));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new GateReprocessingAdmissionResult(
            (GateReprocessingAdmissionStatus)999));
        Assert.ThrowsExactly<ArgumentException>(() => new GateReprocessingAdmissionResult(
            GateReprocessingAdmissionStatus.Admitted));
        Assert.ThrowsExactly<ArgumentException>(() => new GateReprocessingAdmissionResult(
            GateReprocessingAdmissionStatus.Closed, new StubJob(), "closed"));
        Assert.ThrowsExactly<ArgumentException>(() => new GateReprocessingAdmissionResult(
            GateReprocessingAdmissionStatus.Faulted));
        Assert.ThrowsExactly<ArgumentException>(() => new GateReprocessingAdmissionResult(
            GateReprocessingAdmissionStatus.Cancelled, errorCode: "not-an-internal-fault"));

        var secondary = new List<string> { "cleanup" };
        var cancelled = new GateReprocessingAdmissionResult(GateReprocessingAdmissionStatus.Cancelled,
            secondaryErrors: secondary);
        secondary[0] = "mutated";
        CollectionAssert.AreEqual(new[] { "cleanup" }, cancelled.SecondaryErrors.ToArray());
    }

    private sealed class StubJob : IGateReprocessingJob
    {
        public GateReprocessingProgressSnapshot Progress => throw new NotSupportedException();
        public Task<GateReprocessingProgressSnapshot> Completion => throw new NotSupportedException();
        public void Cancel() { }
    }

    [TestMethod]
    public void OperationProgressRejectsImpossibleAdmissionCompletionAndOutcomeCombinations()
    {
        Assert.ThrowsExactly<ArgumentException>(() => new GateReprocessingOperationProgress(0, 1, 0, 0, 0, 0, 0));
        Assert.ThrowsExactly<ArgumentException>(() => new GateReprocessingOperationProgress(1, 1, 2, 0, 0, 0, 0));
        Assert.ThrowsExactly<ArgumentException>(() => new GateReprocessingOperationProgress(1, 0, 0, 0, 1, 0, 0));
        Assert.ThrowsExactly<ArgumentException>(() => new GateReprocessingOperationProgress(1, 1, 1, 0, 1, 1, 0));
        var valid = new GateReprocessingOperationProgress(2, 1, 1, 1, 0, 1, 0);
        Assert.AreEqual(0L, valid.Active);
    }

    [TestMethod]
    public void ResourceProgressRejectsImpossibleAttemptsOwnershipAndPeakCombinations()
    {
        Assert.ThrowsExactly<ArgumentException>(() => new GateReprocessingResourceProgress(1, 1, 1, 0, 0, 0, 1, 1));
        Assert.ThrowsExactly<ArgumentException>(() => new GateReprocessingResourceProgress(1, 1, 0, 1, 1, 1, 0, 1));
        Assert.ThrowsExactly<ArgumentException>(() => new GateReprocessingResourceProgress(1, 1, 0, 0, 0, 0, 0, 1));
        Assert.ThrowsExactly<ArgumentException>(() => new GateReprocessingResourceProgress(1, 1, 0, 0, 0, 0, 1, 0));
        var valid = new GateReprocessingResourceProgress(2, 1, 1, 1, 1, 0, 0, 1, 0, 16);
        Assert.AreEqual(0, valid.Active);
    }

    [TestMethod]
    public void StreamProgressRejectsImpossibleOrderingAndLastIdentityCombinations()
    {
        var stream = new UtStreamIdentity(new(Guid.NewGuid()), new(Guid.NewGuid()),
            new UtSourceId("diagnostics"), new PhysicalChannelId(0));
        Assert.ThrowsExactly<ArgumentException>(() => new GateReprocessingStreamProgress(stream, 1, 2, 0, 0, 0, 1));
        Assert.ThrowsExactly<ArgumentException>(() => new GateReprocessingStreamProgress(stream, 1, 1, 2, 0, 0, 1));
        Assert.ThrowsExactly<ArgumentException>(() => new GateReprocessingStreamProgress(stream, 1, 0, 0, 0, 0, 1));
        Assert.ThrowsExactly<ArgumentException>(() => new GateReprocessingStreamProgress(stream, 1, 1, 1, 1, 1, null));
        var valid = new GateReprocessingStreamProgress(stream, 1, 1, 1, 1, 1, ulong.MaxValue);
        Assert.AreEqual(ulong.MaxValue, valid.LastVisitedAcquisitionIndex);
    }
}
