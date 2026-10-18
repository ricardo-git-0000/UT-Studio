using UTStudio.Contracts.Processing;
using UTStudio.Contracts.Inspection;
using UTStudio.Domain.Acquisition;
using UTStudio.Domain.Inspection;
using System.Text.RegularExpressions;

namespace UTStudio.Core.Tests.Processing;

[TestClass]
public sealed class GateReprocessingDiagnosticsContractTests
{
    [TestMethod]
    public void Phase5TraceabilityHasThirtySevenConcreteResolvableTestReferences()
    {
        string root = FindRepositoryRoot();
        string matrix = File.ReadAllText(Path.Combine(root, "docs", "testing",
            "gate-reprocessing-phase5-traceability.md"));
        string[] rows = matrix.Split('\n').Where(line => Regex.IsMatch(line, @"^\| \d{2} \|")).ToArray();
        CollectionAssert.AreEqual(Enumerable.Range(1, 37).Select(value => value.ToString("D2")).ToArray(),
            rows.Select(line => Regex.Match(line, @"^\| (\d{2}) \|").Groups[1].Value).ToArray());
        string forbidden = string.Join('\n', rows);
        Assert.IsFalse(Regex.IsMatch(forbidden, @"\*|casos?\s+[A-D](?:\s*[–-]\s*[A-D])?|matriz general|\d{2}\s*[–-]\s*\d{2}",
            RegexOptions.IgnoreCase));

        string[] testFiles = Directory.EnumerateFiles(Path.Combine(root, "tests"), "*.cs",
            SearchOption.AllDirectories).ToArray();
        foreach (string row in rows)
        {
            MatchCollection references = Regex.Matches(row,
                @"prueba(?: [A-D])?: `([A-Za-z0-9_]+)\.([A-Za-z0-9_]+)`");
            Assert.IsGreaterThanOrEqualTo(1, references.Count, row);
            foreach (Match reference in references)
            {
                string className = Regex.Escape(reference.Groups[1].Value);
                string methodName = Regex.Escape(reference.Groups[2].Value);
                bool resolved = testFiles.Select(File.ReadAllText).Any(source =>
                    Regex.IsMatch(source, $@"class\s+{className}\b") &&
                    Regex.IsMatch(source,
                        $@"\[(?:TestMethod|DataTestMethod)\][\s\S]{{0,1000}}?\b{methodName}\s*\("));
                Assert.IsTrue(resolved, reference.Value);
            }
            Assert.IsTrue(row.Contains("Aserciones:", StringComparison.Ordinal), row);
        }
    }

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

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current is not null && !File.Exists(Path.Combine(current.FullName, "UTStudio.sln")))
            current = current.Parent;
        return current?.FullName ?? throw new InvalidOperationException("Repository root was not found.");
    }
}
