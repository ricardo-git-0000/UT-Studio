using System.Xml.Linq;
using UTStudio.Contracts.Application;
using UTStudio.Domain.Acquisition;
using UTStudio.Presentation;

namespace UTStudio.Core.Tests.Presentation;

[TestClass]
public sealed class PresentationArchitectureTests
{
    [TestMethod]
    public void PresentationAssemblyHasNoApplicationOrPlatformDependency()
    {
        var references = typeof(AScanViewModel).Assembly.GetReferencedAssemblies().Select(reference => reference.Name!).ToArray();
        string[] forbidden = ["UTStudio.Application", "UTStudio.Acquisition.Simulator", "PresentationFramework", "PresentationCore", "WindowsBase", "System.Xaml"];
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
