using UTStudio.LoadTests;

namespace UTStudio.Core.Tests.Performance;

[TestClass]
public sealed class LoadOptionsTests
{
    [TestMethod]
    public void DefaultsAreSafeSmokeScenario()
    {
        Assert.IsTrue(LoadOptions.TryParse([], out var options, out var error));
        Assert.IsNull(error);
        Assert.AreEqual(LoadProfile.Smoke, options!.Profile);
        Assert.AreEqual(2048, options.SampleCount);
        Assert.AreEqual(50d, options.Rate);
        Assert.AreEqual(TimeSpan.FromSeconds(30), options.Duration);
    }

    [TestMethod]
    public void ParsesExplicitExperimentalScenario()
    {
        Assert.IsTrue(LoadOptions.TryParse(["--profile", "baseline", "--samples", "65535", "--rate", "max", "--duration", "2.5s"],
            out var options, out var error));
        Assert.IsNull(error);
        Assert.AreEqual(LoadProfile.Baseline, options!.Profile);
        Assert.IsNull(options.Rate);
        Assert.AreEqual(TimeSpan.FromSeconds(2.5), options.Duration);
    }

    [TestMethod]
    [DataRow("--samples", "1")]
    [DataRow("--rate", "0")]
    [DataRow("--duration", "0s")]
    [DataRow("--unknown", "x")]
    public void RejectsInvalidOptions(string name, string value)
    {
        Assert.IsFalse(LoadOptions.TryParse([name, value], out var options, out var error));
        Assert.IsNull(options);
        Assert.IsFalse(string.IsNullOrWhiteSpace(error));
    }

    [TestMethod]
    public void SlowCadenceRequiresCompatibleWatchdog()
    {
        Assert.IsFalse(LoadOptions.TryParse(["--rate", "0.05"], out _, out _));
        Assert.IsTrue(LoadOptions.TryParse(["--rate", "0.05", "--progress-timeout", "30s", "--warmup", "1s"], out var options, out _));
        Assert.AreEqual(TimeSpan.FromSeconds(1), options!.Warmup);
    }
}
