using System.Xml.Linq;

namespace UTStudio.Core.Tests.Inspection;

[TestClass]
public sealed class InspectionArchitectureTests
{
    [TestMethod]
    public void InspectionCoreReferencesOnlyDomainAndContracts()
    {
        string root = FindRepositoryRoot();
        XDocument project = XDocument.Load(Path.Combine(root, "src", "UTStudio.Inspection.Core", "UTStudio.Inspection.Core.csproj"));
        string[] references = project.Descendants("ProjectReference")
            .Select(x => Path.GetFileNameWithoutExtension((string)x.Attribute("Include")!))
            .Order(StringComparer.Ordinal).ToArray();
        CollectionAssert.AreEqual(new[] { "UTStudio.Contracts", "UTStudio.Domain" }, references);
        Assert.AreEqual(0, project.Descendants("PackageReference").Count());
    }

    [TestMethod]
    public void ContractsDoesNotReferenceInspectionCore()
    {
        string root = FindRepositoryRoot();
        string text = File.ReadAllText(Path.Combine(root, "src", "UTStudio.Contracts", "UTStudio.Contracts.csproj"));
        Assert.IsFalse(text.Contains("Inspection.Core", StringComparison.Ordinal));
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current is not null && !File.Exists(Path.Combine(current.FullName, "UTStudio.sln"))) { current = current.Parent; }
        return current?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }
}
