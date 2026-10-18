using UTStudio.Contracts.Processing;
using UTStudio.Contracts.Inspection;
using UTStudio.Domain.Acquisition;
using UTStudio.Domain.Inspection;
using System.Globalization;
using System.Reflection;
using System.Text.Json;
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
        Assert.HasCount(37, rows);
        string[] ids = rows.Select(line => Regex.Match(line, @"^\| (\d{2}) \|").Groups[1].Value).ToArray();
        Assert.HasCount(37, ids.Distinct().ToArray());
        CollectionAssert.AreEqual(Enumerable.Range(1, 37).Select(value => value.ToString("D2")).ToArray(), ids);

        var expectedClassifications = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["Ejecutable"] = 26,
            ["CoveredBeforeSchedulerAdmission"] = 2,
            ["IndivisibleContractOperation"] = 1,
            ["NotRepresentableByContract"] = 8
        };
        Dictionary<string, int> actualClassifications = rows
            .Select(row => row.Split('|')[3].Trim())
            .GroupBy(value => value, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
        CollectionAssert.AreEquivalent(expectedClassifications.Keys.ToArray(), actualClassifications.Keys.ToArray());
        foreach ((string classification, int count) in expectedClassifications)
            Assert.AreEqual(count, actualClassifications[classification], classification);

        string forbidden = string.Join('\n', rows);
        Assert.IsFalse(Regex.IsMatch(forbidden,
            @"\*|\.\.\.|…|placeholders?|\bTBD\b|casos?\s+[A-D](?:\s*[–-]\s*[A-D])?|matriz general|\d{2}\s*[–-]\s*\d{2}",
            RegexOptions.IgnoreCase));

        Assembly testAssembly = typeof(GateReprocessingDiagnosticsContractTests).Assembly;
        int[] parameterizedIds = [11, 21, 24, 25, 26, 27, 28];
        foreach (string row in rows)
        {
            MatchCollection references = Regex.Matches(row,
                @"(?:prueba(?: [A-D])?|caso): `([A-Za-z0-9_]+)\.([A-Za-z0-9_]+)(?:\(([^`]*)\))?`");
            Assert.IsGreaterThanOrEqualTo(1, references.Count, row);
            var citedDataRows = new HashSet<string>(StringComparer.Ordinal);
            foreach (Match reference in references)
            {
                Type testClass = testAssembly.GetTypes().Single(type => type.Name == reference.Groups[1].Value);
                MethodInfo testMethod = testClass.GetMethods(BindingFlags.Public | BindingFlags.NonPublic |
                    BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
                    .Single(method => method.Name == reference.Groups[2].Value);
                Assert.IsTrue(testMethod.GetCustomAttributes(inherit: true).Any(attribute =>
                    attribute is TestMethodAttribute || attribute is DataTestMethodAttribute), reference.Value);

                if (reference.Groups[3].Success)
                {
                    string signature = NormalizeCitationArguments(reference.Groups[3].Value,
                        testMethod.GetParameters());
                    Assert.IsTrue(citedDataRows.Add($"{testClass.FullName}.{testMethod.Name}({signature})"),
                        $"Duplicate cited DataRow: {reference.Value}");
                    Assert.IsTrue(testMethod.GetCustomAttributes<DataRowAttribute>()
                        .Select(attribute => NormalizeDataRow(attribute.Data))
                        .Contains(signature, StringComparer.Ordinal), $"Missing DataRow: {reference.Value}");
                }
            }

            int id = int.Parse(Regex.Match(row, @"^\| (\d{2}) \|").Groups[1].Value,
                CultureInfo.InvariantCulture);
            if (parameterizedIds.Contains(id))
                Assert.IsGreaterThanOrEqualTo(1, citedDataRows.Count, row);
            Match assertions = Regex.Match(row, @"Aserciones:\s*([^|.][^|]*)\|\s*Verde\s*\|");
            Assert.IsTrue(assertions.Success && !string.IsNullOrWhiteSpace(assertions.Groups[1].Value), row);
        }

        CollectionAssert.AreEquivalent(new[] { "A", "B", "C", "D" }, Regex.Matches(rows[12], @"prueba ([A-D]):")
            .Select(match => match.Groups[1].Value).ToArray());
    }

    private static string NormalizeCitationArguments(string arguments, ParameterInfo[] parameters)
    {
        string[] tokens = Regex.Matches(arguments, "(?:\\\"(?:\\\\\\\\.|[^\\\"\\\\\\\\])*\\\"|[^,])+")
            .Select(match => match.Value.Trim()).ToArray();
        Assert.HasCount(parameters.Length, tokens);
        return string.Join(',', tokens.Select((token, index) => NormalizeCitationValue(token,
            parameters[index].ParameterType)));
    }

    private static string NormalizeCitationValue(string token, Type parameterType)
    {
        if (token == "null")
            return "null";
        if (parameterType == typeof(string))
            return $"string:{JsonSerializer.Deserialize<string>(token)}";
        if (parameterType == typeof(bool) && bool.TryParse(token, out bool boolean))
            return $"bool:{boolean.ToString().ToLowerInvariant()}";
        if (parameterType.IsEnum)
        {
            string prefix = $"{parameterType.Name}.";
            Assert.IsTrue(token.StartsWith(prefix, StringComparison.Ordinal), token);
            object value = Enum.Parse(parameterType, token[prefix.Length..], ignoreCase: false);
            return $"enum:{parameterType.FullName}:{value}";
        }

        var numericSyntax = new Dictionary<Type, string>(8)
        {
            [typeof(sbyte)] = @"^-?\d+sbyte$",
            [typeof(byte)] = @"^\d+byte$",
            [typeof(short)] = @"^-?\d+short$",
            [typeof(ushort)] = @"^\d+ushort$",
            [typeof(int)] = @"^-?\d+$",
            [typeof(uint)] = @"^\d+U$",
            [typeof(long)] = @"^-?\d+L$",
            [typeof(ulong)] = @"^\d+UL$"
        };
        Assert.IsTrue(numericSyntax.TryGetValue(parameterType, out string? syntax) &&
            Regex.IsMatch(token, syntax, RegexOptions.CultureInvariant), token);
        string digits = Regex.Replace(token, @"(?:sbyte|byte|short|ushort|U|L|UL)$", string.Empty);
        object number = Convert.ChangeType(digits, parameterType, CultureInfo.InvariantCulture);
        return $"{parameterType.Name}:{Convert.ToString(number, CultureInfo.InvariantCulture)}";
    }

    private static string NormalizeDataRow(object?[] values) => string.Join(',', values.Select(value => value switch
    {
        null => "null",
        string text => $"string:{text}",
        bool boolean => $"bool:{boolean.ToString().ToLowerInvariant()}",
        Enum enumeration => $"enum:{enumeration.GetType().FullName}:{enumeration}",
        sbyte or byte or short or ushort or int or uint or long or ulong =>
            $"{value.GetType().Name}:{Convert.ToString(value, CultureInfo.InvariantCulture)}",
        _ => throw new AssertFailedException($"Unsupported DataRow value type: {value.GetType().FullName}")
    }));

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
