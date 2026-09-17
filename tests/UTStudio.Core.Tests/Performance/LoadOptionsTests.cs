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
        Assert.AreEqual(LoadSourceMode.Auto, options.Source);
        Assert.AreEqual(TelemetryMode.Full, options.Telemetry);
        Assert.AreEqual(ProgressMode.Normal, options.Progress);
        Assert.AreEqual(PacingMode.SkipMissed, options.Pacing);
        Assert.AreEqual(32, options.MaxCatchUp);
        Assert.IsNull(options.OutputPath);
    }

    [TestMethod]
    public void ParsesCatchUpAndResolvesAutoToExperimental()
    {
        Assert.IsTrue(LoadOptions.TryParse(["--rate", "1000", "--pacing", "catch-up-bounded", "--max-catch-up", "16"], out var options, out _));
        Assert.AreEqual(PacingMode.CatchUpBounded, options!.Pacing);
        Assert.AreEqual(16, options.MaxCatchUp);
        Assert.AreEqual(LoadSourceMode.Experimental, options.Source);
    }

    [TestMethod]
    public void RejectsIncompatiblePacingArguments()
    {
        Assert.IsFalse(LoadOptions.TryParse(["--samples", "65535", "--rate", "max", "--pacing", "skip-missed"], out _, out _));
        Assert.IsFalse(LoadOptions.TryParse(["--samples", "65535", "--rate", "max", "--pacing", "catch-up-bounded"], out _, out _));
        Assert.IsFalse(LoadOptions.TryParse(["--source", "production", "--pacing", "catch-up-bounded"], out _, out _));
        Assert.IsFalse(LoadOptions.TryParse(["--max-catch-up", "0", "--pacing", "catch-up-bounded"], out _, out _));
        Assert.IsFalse(LoadOptions.TryParse(["--max-catch-up", "33", "--pacing", "catch-up-bounded"], out _, out _));
        Assert.IsFalse(LoadOptions.TryParse(["--max-catch-up", "4"], out _, out _));
    }

    [TestMethod]
    public void ParsesExecutionAndOutputControls()
    {
        Assert.IsTrue(LoadOptions.TryParse([
            "--source", "experimental", "--warmup", "1.5s", "--telemetry", "minimal",
            "--progress", "quiet", "--output", "results/run.json"
        ], out var options, out var error));

        Assert.IsNull(error);
        Assert.AreEqual(LoadSourceMode.Experimental, options!.Source);
        Assert.AreEqual(TimeSpan.FromSeconds(1.5), options.Warmup);
        Assert.AreEqual(TelemetryMode.Minimal, options.Telemetry);
        Assert.AreEqual(ProgressMode.Quiet, options.Progress);
        Assert.AreEqual(Path.GetFullPath("results/run.json"), options.OutputPath);
    }

    [TestMethod]
    [DataRow("--source", "invalid")]
    [DataRow("--telemetry", "verbose")]
    [DataRow("--progress", "silent")]
    [DataRow("--output", "result.csv")]
    public void RejectsInvalidExecutionControls(string name, string value)
    {
        Assert.IsFalse(LoadOptions.TryParse([name, value], out _, out var error));
        Assert.IsFalse(string.IsNullOrWhiteSpace(error));
    }

    [TestMethod]
    public void ProductionSourceRejectsUnsupportedDemand()
    {
        Assert.IsFalse(LoadOptions.TryParse(["--source", "production", "--rate", "1000"], out _, out _));
        Assert.IsFalse(LoadOptions.TryParse(["--source", "production", "--samples", "65535", "--rate", "max"], out _, out _));
        Assert.IsTrue(LoadOptions.TryParse(["--source", "production", "--rate", "100"], out var options, out _));
        Assert.AreEqual(LoadSourceMode.Production, options!.Source);
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
