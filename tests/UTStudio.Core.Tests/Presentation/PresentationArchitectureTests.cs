using System.Reflection;
using System.Xml.Linq;
using UTStudio.Contracts.Application;
using UTStudio.Contracts.Inspection;
using UTStudio.Contracts.Processing;
using UTStudio.Domain.Acquisition;
using UTStudio.Presentation;

namespace UTStudio.Core.Tests.Presentation;

[TestClass]
public sealed class PresentationArchitectureTests
{
    [TestMethod]
    public void PresentationDoesNotReferenceAuthoritativeTraceOwnershipTypes()
    {
        Type[] forbidden =
        [
            typeof(IUtTraceProvider),
            typeof(IInspectionTraceLease),
            typeof(IReadOnlyUtSamples)
        ];
        var violations = new List<string>();

        foreach (Type type in typeof(AScanViewModel).Assembly.GetTypes())
        {
            foreach (FieldInfo field in type.GetFields(MemberFlags))
            {
                AddViolation(field, field.FieldType, forbidden, violations);
            }
            foreach (PropertyInfo property in type.GetProperties(MemberFlags))
            {
                AddViolation(property, property.PropertyType, forbidden, violations);
            }
            foreach (MethodInfo method in type.GetMethods(MemberFlags))
            {
                AddViolation(method, method.ReturnType, forbidden, violations);
                foreach (ParameterInfo parameter in method.GetParameters())
                {
                    AddViolation(method, parameter.ParameterType, forbidden, violations);
                }
            }
        }

        Assert.IsEmpty(violations, string.Join(Environment.NewLine, violations));
    }

    private const BindingFlags MemberFlags = BindingFlags.Public | BindingFlags.NonPublic |
        BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

    private static void AddViolation(MemberInfo member, Type candidate, Type[] forbidden,
        List<string> violations)
    {
        if (ContainsAny(candidate, forbidden))
        {
            violations.Add($"{member.DeclaringType?.FullName}.{member.Name} -> {candidate}");
        }
    }

    private static bool ContainsAny(Type candidate, Type[] forbidden)
    {
        if (candidate.IsByRef || candidate.IsPointer || candidate.IsArray)
        {
            return ContainsAny(candidate.GetElementType()!, forbidden);
        }
        return forbidden.Contains(candidate) ||
            candidate.IsGenericType &&
            candidate.GetGenericArguments().Any(argument => ContainsAny(argument, forbidden));
    }

    [TestMethod]
    public void PresentationAssemblyHasNoApplicationOrPlatformDependency()
    {
        var references = typeof(AScanViewModel).Assembly.GetReferencedAssemblies().Select(reference => reference.Name!).ToArray();
        string[] forbidden = ["UTStudio.Application", "UTStudio.Acquisition.Simulator", "UTStudio.Processing.Core",
            "PresentationFramework", "PresentationCore", "WindowsBase", "System.Xaml"];
        Assert.IsFalse(references.Any(reference => forbidden.Contains(reference) || reference.StartsWith("Avalonia", StringComparison.Ordinal)));
        Assert.IsTrue(references.Contains("CommunityToolkit.Mvvm"));
        Assert.AreEqual("UTStudio.Contracts", typeof(IApplicationSession).Assembly.GetName().Name);
        Assert.AreEqual(typeof(IApplicationSession).Assembly, typeof(SessionSnapshot).Assembly);
        Assert.AreEqual(typeof(IApplicationSession).Assembly, typeof(SessionPhase).Assembly);
        Assert.IsFalse(typeof(IAsyncDisposable).IsAssignableFrom(typeof(IApplicationSession)));
    }

    [TestMethod]
    public void ProjectReferencesAndSingleApprovedPackageStayNeutral()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "UTStudio.sln"))) { directory = directory.Parent; }
        Assert.IsNotNull(directory);
        var project = XDocument.Load(Path.Combine(directory.FullName, "src", "UTStudio.Presentation", "UTStudio.Presentation.csproj"));
        var references = project.Descendants("ProjectReference").Select(item => (string)item.Attribute("Include")!).ToArray();
        Assert.IsTrue(references.All(reference => reference.Contains("UTStudio.Domain", StringComparison.Ordinal) ||
            reference.Contains("UTStudio.Contracts", StringComparison.Ordinal) || reference.Contains("UTStudio.Visualization.Core", StringComparison.Ordinal)));
        var package = project.Descendants("PackageReference").Single();
        Assert.AreEqual("CommunityToolkit.Mvvm", (string?)package.Attribute("Include"));
        Assert.AreEqual("8.4.2", (string?)package.Attribute("Version"));
        Assert.AreEqual("net10.0", project.Descendants("TargetFramework").Single().Value);
        Assert.IsFalse(project.Descendants("UseWPF").Any());
    }

    [TestMethod]
    public void UiAsyncCommandDoesNotScheduleExecutionWithTaskRun()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "UTStudio.sln"))) { directory = directory.Parent; }
        Assert.IsNotNull(directory);
        string source = File.ReadAllText(Path.Combine(directory.FullName, "src", "UTStudio.Presentation", "UiAsyncCommand.cs"));
        Assert.DoesNotContain("Task.Run(", source, StringComparison.Ordinal);
    }

    [TestMethod]
    public void GateEditorRemainsHeadlessAndApplicationAddsNoWpfDependency()
    {
        string[] presentationReferences = typeof(GatePlanEditorSession).Assembly.GetReferencedAssemblies()
            .Select(reference => reference.Name!).ToArray();
        Assert.DoesNotContain("UTStudio.Processing.Core", presentationReferences);
        Assert.IsFalse(presentationReferences.Any(reference => reference is "PresentationFramework" or
            "PresentationCore" or "WindowsBase" or "System.Xaml"));

        string[] applicationReferences = typeof(UTStudio.Application.InMemoryGatePlanAuthorityOwner).Assembly
            .GetReferencedAssemblies().Select(reference => reference.Name!).ToArray();
        Assert.IsFalse(applicationReferences.Any(reference => reference is "PresentationFramework" or
            "PresentationCore" or "WindowsBase" or "System.Xaml"));

        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "UTStudio.sln")))
            directory = directory.Parent;
        Assert.IsNotNull(directory);
        string editor = File.ReadAllText(Path.Combine(directory.FullName, "src", "UTStudio.Presentation",
            "GatePlanEditorSession.cs"));
        string preview = File.ReadAllText(Path.Combine(directory.FullName, "src", "UTStudio.Application",
            "GatePlanPreviewCompiler.cs"));
        foreach (string forbidden in new[] { "GateEvaluator", "IGateDerivedStore", "IGateReprocessingScheduler",
                     "CommitAsync", "CommitGapAsync", "System.Windows", "SemaphoreSlim", "TaskCompletionSource" })
        {
            Assert.DoesNotContain(forbidden, editor, StringComparison.Ordinal);
            Assert.DoesNotContain(forbidden, preview, StringComparison.Ordinal);
        }
        Assert.AreEqual(typeof(Task<GateReprocessingProgressSnapshot>), typeof(IGateReprocessingJob)
            .GetProperty(nameof(IGateReprocessingJob.Completion))!.PropertyType);
    }

    [TestMethod]
    public void SessionSnapshotValidatesBoundaryAndDefensivelyCopiesErrors()
    {
        var errors = new List<UtSourceError> { new("first", "original") };
        var snapshot = new SessionSnapshot(1, SessionPhase.Faulted, ManualApplicationSession.Source, null,
            10, 10, null, null, null, errors, 0, canStart: true);
        errors.Clear();
        Assert.HasCount(1, snapshot.CleanupErrors);
        Assert.Throws<NotSupportedException>(() => ((IList<UtSourceError>)snapshot.CleanupErrors).Clear());
        Assert.IsTrue(snapshot.CanStart);
        Assert.Throws<ArgumentException>(() => new SessionSnapshot(0, SessionPhase.Idle, default, null, 0, 0, null, null, null, [], 0));
        Assert.Throws<ArgumentException>(() => new SessionSnapshot(0, SessionPhase.Idle, ManualApplicationSession.Source, default(AcquisitionRunId), 0, 0, null, null, null, [], 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new SessionSnapshot(0, (SessionPhase)99, ManualApplicationSession.Source, null, 0, 0, null, null, null, [], 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new SessionSnapshot(0, SessionPhase.Idle, ManualApplicationSession.Source, null, -1, 0, null, null, null, [], 0));
        Assert.Throws<ArgumentException>(() => new SessionSnapshot(0, SessionPhase.Idle, ManualApplicationSession.Source, null, 0, 0, null, null, null, [null!], 0));
    }
}
