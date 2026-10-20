using System.Xml.Linq;
using System.Reflection;
using UTStudio.Contracts.Processing;
using UTStudio.Processing.Core;

namespace UTStudio.Core.Tests.Processing;

[TestClass]
public sealed class GateSignatureAndArchitectureTests
{
    [TestMethod]
    public void StructurallyEqualPlansHaveEqualSignaturesAndRevisionChangesSignature()
    {
        GatePlanCompiler compiler = new();
        string first = compiler.Compile(GateTestPlan.Plan([GateTestPlan.Gate("A")])).CompiledPlan!.Signature.Hex;
        string same = compiler.Compile(GateTestPlan.Plan([GateTestPlan.Gate("A")])).CompiledPlan!.Signature.Hex;
        string revised = compiler.Compile(GateTestPlan.Plan([GateTestPlan.Gate("A")], revision: 2)).CompiledPlan!.Signature.Hex;
        Assert.AreEqual(first, same); Assert.AreNotEqual(first, revised); Assert.AreEqual(64, first.Length);
    }

    [TestMethod]
    public void EverySemanticFieldChangesSignature()
    {
        GatePlanCompiler compiler = new(new(16));
        GatePlan baseline = GateTestPlan.Plan([GateTestPlan.Gate("A")]);
        string expected = compiler.Compile(baseline).CompiledPlan!.Signature.Hex;
        GatePlan[] variants =
        [
            GateTestPlan.Plan([GateTestPlan.Gate("A")], id: "other"),
            GateTestPlan.Plan([GateTestPlan.Gate("A")], revision: 2),
            GateTestPlan.Plan([GateTestPlan.Gate("B")]),
            GateTestPlan.Plan([GateTestPlan.Gate("A", revision: 2)]),
            GateTestPlan.Plan([GateTestPlan.Gate("A", offsetSeconds: 0.0000001)]),
            GateTestPlan.Plan([GateTestPlan.Gate("A", durationSeconds: 0.0000011)]),
            GateTestPlan.Plan([GateTestPlan.Gate("A", domain: GateSignalDomain.Positive)]),
            GateTestPlan.Plan([GateTestPlan.Gate("A", detector: GateDetectorKind.GlobalMinimum)]),
            GateTestPlan.Plan([GateTestPlan.Gate("A", threshold: .5)]),
            GateTestPlan.Plan([GateTestPlan.Gate("A", detector: GateDetectorKind.FirstRisingCrossing,
                hysteresis: new(.2, .8))]),
            GateTestPlan.Plan([GateTestPlan.Gate("A", detector: GateDetectorKind.FirstLocalMaximum,
                local: new(2, .3, 4))]),
            GateTestPlan.Plan([GateTestPlan.Gate("A", measures: [GateMeasureKind.Status])]),
            GateTestPlan.Plan([GateTestPlan.Gate("A", measures: [GateMeasureKind.Status, GateMeasureKind.Amplitude])]),
            GateTestPlan.Plan([GateTestPlan.Gate("A"), GateTestPlan.Gate("B", "A")]),
            GateTestPlan.Plan([GateTestPlan.Gate("B"), GateTestPlan.Gate("A")])
        ];
        foreach (GatePlan variant in variants)
        {
            GatePlanCompilationResult result = compiler.Compile(variant);
            Assert.IsTrue(result.IsCompiled, string.Join(";", result.Errors.Select(x => x.Message)));
            Assert.AreNotEqual(expected, result.CompiledPlan!.Signature.Hex);
        }
    }

    [TestMethod]
    public void DependencyAndSignedZeroAreCanonicalSignatureInputs()
    {
        GatePlanCompiler compiler = new();
        string origin = compiler.Compile(GateTestPlan.Plan([GateTestPlan.Gate("A"), GateTestPlan.Gate("B")])).CompiledPlan!.Signature.Hex;
        string dependency = compiler.Compile(GateTestPlan.Plan([GateTestPlan.Gate("A"), GateTestPlan.Gate("B", "A")])).CompiledPlan!.Signature.Hex;
        Assert.AreNotEqual(origin, dependency);
        string positiveZero = compiler.Compile(GateTestPlan.Plan([GateTestPlan.Gate("A", offsetSeconds: 0d)])).CompiledPlan!.Signature.Hex;
        string negativeZero = compiler.Compile(GateTestPlan.Plan([GateTestPlan.Gate("A", offsetSeconds: -0d)])).CompiledPlan!.Signature.Hex;
        Assert.AreEqual(positiveZero, negativeZero);
    }

    [TestMethod]
    public void CanonicalSignatureMatchesKnownVector()
    {
        string actual = new GatePlanCompiler().Compile(
            GateTestPlan.Plan([GateTestPlan.Gate("A")])).CompiledPlan!.Signature.Hex;
        Assert.AreEqual("ADBA1CC30AEAEB12536FA8974A15B89D8F20C0980AAA452C51485EBC09EE29E4", actual);
    }

    [TestMethod]
    public void DetectorSubfieldsIndependentlyChangeSignature()
    {
        GatePlanCompiler compiler = new();
        string Crossing(double threshold) => compiler.Compile(GateTestPlan.Plan([GateTestPlan.Gate("A",
            detector: GateDetectorKind.FirstRisingCrossing, threshold: threshold)])).CompiledPlan!.Signature.Hex;
        string Hysteresis(double low, double high) => compiler.Compile(GateTestPlan.Plan([GateTestPlan.Gate("A",
            detector: GateDetectorKind.FirstRisingCrossing, hysteresis: new(low, high))])).CompiledPlan!.Signature.Hex;
        string Local(int radius, double prominence, int distance) => compiler.Compile(GateTestPlan.Plan([GateTestPlan.Gate("A",
            detector: GateDetectorKind.FirstLocalMaximum, local: new(radius, prominence, distance))])).CompiledPlan!.Signature.Hex;

        Assert.AreNotEqual(Crossing(.4), Crossing(.5));
        Assert.AreNotEqual(Hysteresis(.2, .8), Hysteresis(.3, .8));
        Assert.AreNotEqual(Hysteresis(.2, .8), Hysteresis(.2, .9));
        Assert.AreNotEqual(Local(1, 10, 2), Local(2, 10, 2));
        Assert.AreNotEqual(Local(1, 10, 2), Local(1, 11, 2));
        Assert.AreNotEqual(Local(1, 10, 2), Local(1, 10, 3));
    }

    [TestMethod]
    public void ProcessingCoreReferencesOnlyNeutralProjectsAndAddsNoPackage()
    {
        string root = FindRepositoryRoot();
        AssertProjectReferences(root, "UTStudio.Domain", []);
        AssertProjectReferences(root, "UTStudio.Contracts", ["UTStudio.Domain"]);
        AssertProjectReferences(root, "UTStudio.Processing.Core", ["UTStudio.Contracts", "UTStudio.Domain"]);
    }

    [TestMethod]
    public void ProcessingAssembliesDoNotReferenceWpfApplicationHardwareOrStore()
    {
        string root = FindRepositoryRoot();
        foreach (string relative in new[]
        {
            "src/UTStudio.Domain/Processing", "src/UTStudio.Contracts/Processing", "src/UTStudio.Processing.Core"
        })
        {
            foreach (string file in Directory.GetFiles(Path.Combine(root, relative), "*.cs", SearchOption.AllDirectories))
            {
                string text = File.ReadAllText(file);
                foreach (string forbidden in new[] { "System.Windows", "UTStudio.Application", "UTStudio.Presentation",
                    "Acquisition.Simulator", "Inspection.Core", "Dispatcher", "BitmapSource" })
                { Assert.IsFalse(text.Contains(forbidden, StringComparison.Ordinal), $"{file} contains {forbidden}."); }
                if (relative.Contains("Contracts", StringComparison.Ordinal))
                    Assert.IsFalse(text.Contains("SemaphoreSlim", StringComparison.Ordinal), $"{file} contains threading state.");
            }
        }
    }

    [TestMethod]
    public void ContractsClrReferencesAndPublicSurfaceRemainNeutral()
    {
        Assembly contracts = typeof(GatePlanEditIntent).Assembly;
        string[] allowedAssemblies = ["System.Collections", "System.Linq", "System.Memory", "System.Runtime",
            "System.Security.Cryptography", "System.Threading", "System.Threading.Channels", "UTStudio.Domain"];
        string[] actualAssemblies = contracts.GetReferencedAssemblies().Select(x => x.Name!)
            .Order(StringComparer.Ordinal).ToArray();
        CollectionAssert.AreEqual(allowedAssemblies, actualAssemblies,
            "Every Contracts CLR dependency requires explicit architectural review.");
        string[] forbiddenAssemblies = ["UTStudio.Application", "UTStudio.Presentation", "UTStudio.Processing.Core",
            "PresentationFramework", "PresentationCore", "WindowsBase", "System.Xaml"];
        Assert.IsFalse(contracts.GetReferencedAssemblies().Any(x => forbiddenAssemblies.Contains(x.Name, StringComparer.Ordinal)));
        HashSet<Type> stepOneContracts = StepOneContracts().ToHashSet();
        foreach (Type owner in contracts.GetExportedTypes().Where(x => x.Namespace == "UTStudio.Contracts.Processing"))
        {
            foreach (Type used in PublicSignatureTypes(owner))
            {
                string assembly = used.Assembly.GetName().Name ?? string.Empty;
                string name = used.FullName ?? used.Name;
                bool forbidden = forbiddenAssemblies.Contains(assembly, StringComparer.Ordinal) ||
                    name.StartsWith("System.Windows.", StringComparison.Ordinal) ||
                    name is "System.Threading.SemaphoreSlim" or "System.Threading.Tasks.TaskCompletionSource" ||
                    name.StartsWith("System.Threading.Tasks.TaskCompletionSource`", StringComparison.Ordinal);
                Assert.IsFalse(forbidden, $"{owner.FullName} exposes forbidden type {name} from {assembly}.");
                if (stepOneContracts.Contains(owner))
                {
                    Assert.IsFalse(IsOperationalType(used),
                        $"{owner.FullName} exposes operational type {name}.");
                }
            }
            if (stepOneContracts.Contains(owner)) AssertStepOneContractShape(owner);
        }
        string root = FindRepositoryRoot();
        string[] files = ["GateEditingContracts.cs", "GatePreparedOperationContracts.cs",
            "GateReplacementContracts.cs", "GateApplyLedgerContracts.cs"];
        HashSet<string> declared = files.SelectMany(file => File.ReadLines(Path.Combine(root, "src",
                "UTStudio.Contracts", "Processing", file)))
            .Select(line => System.Text.RegularExpressions.Regex.Match(line,
                @"^public\s+(?:(?:sealed|static)\s+class|readonly\s+record\s+struct|enum|interface)\s+(\w+)"))
            .Where(match => match.Success).Select(match => match.Groups[1].Value).ToHashSet(StringComparer.Ordinal);
        CollectionAssert.AreEquivalent(declared.Order(StringComparer.Ordinal).ToArray(),
            stepOneContracts.Select(type => type.Name).Order(StringComparer.Ordinal).ToArray(),
            "Every public Step 1 declaration requires deliberate architectural classification.");
    }

    [TestMethod]
    public void ProcessingCoreDoesNotExposeInternalsToBenchmarks()
    {
        string assemblyInfo = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "src",
            "UTStudio.Processing.Core", "Properties", "AssemblyInfo.cs"));
        Assert.IsFalse(assemblyInfo.Contains("InternalsVisibleTo(\"UTStudio.Benchmarks\")", StringComparison.Ordinal));
        Assert.Contains("InternalsVisibleTo(\"UTStudio.Core.Tests\")", assemblyInfo);
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current is not null && !File.Exists(Path.Combine(current.FullName, "UTStudio.sln"))) { current = current.Parent; }
        return current?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }

    private static void AssertProjectReferences(string root, string projectName, string[] expected)
    {
        XDocument project = XDocument.Load(Path.Combine(root, "src", projectName, $"{projectName}.csproj"));
        string[] references = project.Descendants("ProjectReference")
            .Select(x => Path.GetFileNameWithoutExtension((string)x.Attribute("Include")!))
            .Order(StringComparer.Ordinal).ToArray();
        CollectionAssert.AreEqual(expected, references);
        Assert.AreEqual(0, project.Descendants("PackageReference").Count(), $"{projectName} must not add packages.");
    }

    private static IReadOnlyCollection<Type> PublicSignatureTypes(Type owner)
    {
        HashSet<Type> found = []; void Visit(Type? type)
        {
            if (type is null) return;
            while (type.HasElementType) type = type.GetElementType()!;
            if (!found.Add(type)) return;
            if (type.IsGenericType) { Visit(type.GetGenericTypeDefinition()); foreach (Type argument in type.GetGenericArguments()) Visit(argument); }
            if (type.IsGenericParameter) foreach (Type constraint in type.GetGenericParameterConstraints()) Visit(constraint);
        }
        void VisitAttribute(CustomAttributeData attribute)
        {
            Visit(attribute.AttributeType); Visit(attribute.Constructor.DeclaringType);
            foreach (ParameterInfo parameter in attribute.Constructor.GetParameters()) Visit(parameter.ParameterType);
            foreach (CustomAttributeTypedArgument argument in attribute.ConstructorArguments) VisitAttributeArgument(argument);
            foreach (CustomAttributeNamedArgument argument in attribute.NamedArguments)
            { Visit(argument.MemberInfo.DeclaringType); VisitAttributeArgument(argument.TypedValue); }
        }
        void VisitAttributeArgument(CustomAttributeTypedArgument argument)
        {
            Visit(argument.ArgumentType);
            if (argument.Value is IReadOnlyCollection<CustomAttributeTypedArgument> items)
                foreach (CustomAttributeTypedArgument item in items) VisitAttributeArgument(item);
        }
        void VisitMember(MemberInfo member) { foreach (CustomAttributeData attribute in member.CustomAttributes) VisitAttribute(attribute); }
        VisitMember(owner);
        foreach (Type argument in owner.GetGenericArguments())
            if (argument.IsGenericParameter) foreach (Type constraint in argument.GetGenericParameterConstraints()) Visit(constraint);
        Visit(owner.BaseType); foreach (Type item in owner.GetInterfaces()) Visit(item);
        const BindingFlags flags = BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
        foreach (ConstructorInfo constructor in owner.GetConstructors(flags))
        { VisitMember(constructor); foreach (ParameterInfo parameter in constructor.GetParameters()) { Visit(parameter.ParameterType); foreach (CustomAttributeData attribute in parameter.CustomAttributes) VisitAttribute(attribute); } }
        foreach (MethodInfo method in owner.GetMethods(flags))
        {
            VisitMember(method); Visit(method.ReturnType); foreach (CustomAttributeData attribute in method.ReturnParameter.CustomAttributes) VisitAttribute(attribute);
            foreach (Type argument in method.GetGenericArguments())
                if (argument.IsGenericParameter) foreach (Type constraint in argument.GetGenericParameterConstraints()) Visit(constraint);
            foreach (ParameterInfo parameter in method.GetParameters()) { Visit(parameter.ParameterType); foreach (CustomAttributeData attribute in parameter.CustomAttributes) VisitAttribute(attribute); }
        }
        foreach (PropertyInfo property in owner.GetProperties(flags))
        { VisitMember(property); Visit(property.PropertyType); foreach (ParameterInfo parameter in property.GetIndexParameters()) Visit(parameter.ParameterType); }
        foreach (FieldInfo field in owner.GetFields(flags)) { VisitMember(field); Visit(field.FieldType); }
        foreach (EventInfo item in owner.GetEvents(flags)) { VisitMember(item); Visit(item.EventHandlerType); }
        return found;
    }

    private static Type[] StepOneContracts() =>
    [
        typeof(GatePlanEditIntentKind), typeof(GatePlanEditOutcome), typeof(GatePlanCapacityResource),
        typeof(GatePlanEditorState),
        typeof(GatePlanAuthorityKey), typeof(CommittedGatePlanSnapshot), typeof(GatePlanAuthoritySnapshot),
        typeof(GateAuthorityPublicationSequence), typeof(GatePlanAuthorityReadStamp),
        typeof(GatePlanAuthorityReadStampStatus), typeof(GatePlanAuthorityReadStampResult),
        typeof(GatePlanRecoveryInstallStatus), typeof(GatePlanRecoveryInstallResult), typeof(GatePlanDraft),
        typeof(GatePlanEditIntent), typeof(GatePlanEditorSnapshot), typeof(GatePlanEditResult),
        typeof(IGatePlanAuthorityReader), typeof(IGatePlanPreviewCompiler),
        typeof(GatePreparedReservationState), typeof(GatePreparedReservationOperation),
        typeof(GatePreparedReservationResultStatus), typeof(GatePreparedReservationId),
        typeof(GateAuthorityGeneration), typeof(GatePreparedOperationGeneration),
        typeof(GateReplacementGeneration), typeof(GateRunGeneration), typeof(GatePreparedReservationOwner),
        typeof(GatePreparedReservation), typeof(GatePreparedReservationResult),
        typeof(GateCandidateOwnershipState), typeof(GateCandidateActivationStatus),
        typeof(GateCandidateAbandonStatus), typeof(GateCandidateReclaimStatus), typeof(GateReplacementToken),
        typeof(GateStoreOnlyCandidateDescriptor), typeof(GateCandidateActivationRequest),
        typeof(GateObservedPredecessorKind), typeof(GateReplacementCasObservedState),
        typeof(GateCandidateActivationResult), typeof(GateCandidateAbandonResult), typeof(GateCandidateReclaimResult),
        typeof(GateApplyCommitState), typeof(GateLiveEpochState), typeof(GateHistoricalAdmissionState),
        typeof(GateHistoricalExecutionState), typeof(GateCandidateCoverageState), typeof(GateActivationState),
        typeof(GateCandidateRetirementState), typeof(GatePreviousViewRetirementState), typeof(GateLedgerStage),
        typeof(GateLedgerCauseKind), typeof(GateApplyLedgerTransitionStatus),
        typeof(GateApplyLedgerLifecycleState), typeof(GateApplyLedgerPendingResource), typeof(GateLedgerCause),
        typeof(GateApplyLedgerTransitionResult), typeof(GateApplyLedgerSnapshot)
    ];

    private static bool IsOperationalType(Type type)
    {
        Type definition = type.IsGenericType ? type.GetGenericTypeDefinition() : type;
        string fullName = definition.FullName ?? definition.Name;
        return typeof(Delegate).IsAssignableFrom(type) || typeof(IDisposable).IsAssignableFrom(type) ||
            typeof(IAsyncDisposable).IsAssignableFrom(type) || typeof(System.IO.Stream).IsAssignableFrom(type) ||
            typeof(System.IO.TextReader).IsAssignableFrom(type) || typeof(System.IO.TextWriter).IsAssignableFrom(type) ||
            typeof(System.Runtime.InteropServices.SafeHandle).IsAssignableFrom(type) ||
            typeof(System.Threading.WaitHandle).IsAssignableFrom(type) ||
            definition == typeof(Task) || definition == typeof(Task<>) || definition == typeof(ValueTask) ||
            definition == typeof(ValueTask<>) || fullName.StartsWith("System.Reflection.", StringComparison.Ordinal) ||
            fullName.StartsWith("System.Threading.Channels.", StringComparison.Ordinal) ||
            fullName.StartsWith("System.Collections.Concurrent.", StringComparison.Ordinal) ||
            fullName is "System.Threading.Thread" or "System.Threading.Timer" or "System.Timers.Timer" or
                "System.Threading.SemaphoreSlim" or "System.Threading.Mutex" or "System.Threading.Monitor" ||
            definition == typeof(List<>) || definition == typeof(Dictionary<,>) || definition == typeof(HashSet<>) ||
            definition == typeof(Queue<>) || definition == typeof(Stack<>);
    }

    private static void AssertStepOneContractShape(Type type)
    {
        if (type.IsEnum) return;
        if (type.IsValueType)
        {
            Assert.IsTrue(type.IsDefined(typeof(System.Runtime.CompilerServices.IsReadOnlyAttribute), inherit: false),
                $"{type.FullName} must be a readonly value object.");
            return;
        }
        if (type.IsClass && !type.IsAbstract) Assert.IsTrue(type.IsSealed, $"{type.FullName} must be sealed and immutable.");
        Assert.IsFalse(typeof(IDisposable).IsAssignableFrom(type) || typeof(IAsyncDisposable).IsAssignableFrom(type),
            $"{type.FullName} must not own operational lifetime.");
        Assert.IsFalse(new[] { "Store", "Scheduler", "Coordinator", "Worker", "Service" }
            .Any(suffix => type.Name.EndsWith(suffix, StringComparison.Ordinal)),
            $"{type.FullName} looks like a concrete operational component.");
        const BindingFlags flags = BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly;
        Assert.IsFalse(type.GetFields(flags).Any(x => !x.IsInitOnly && !x.IsLiteral), $"{type.FullName} has mutable public fields.");
        Assert.IsFalse(type.GetProperties(flags).Any(x => x.SetMethod?.IsPublic == true), $"{type.FullName} has public setters.");
        Assert.IsFalse(type.GetMethods(flags).Any(x => x.ReturnType == typeof(Task) ||
            x.ReturnType == typeof(ValueTask) || x.ReturnType.IsGenericType &&
            (x.ReturnType.GetGenericTypeDefinition() == typeof(Task<>) || x.ReturnType.GetGenericTypeDefinition() == typeof(ValueTask<>))),
            $"{type.FullName} exposes operational asynchronous behavior.");
    }
}
