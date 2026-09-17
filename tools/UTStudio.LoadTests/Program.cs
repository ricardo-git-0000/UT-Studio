using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using UTStudio.LoadTests;

if (!LoadOptions.TryParse(args, out LoadOptions? options, out string? error))
{
    Console.WriteLine(error ?? "Usage: --profile smoke|baseline|soak --samples 2048|65535 --rate <number|max> [--source auto|production|experimental] [--duration 10s|5m|30m] [--warmup 0s|60s] [--telemetry minimal|full] [--progress normal|quiet] [--output <path.json>]");
    return error is null ? 0 : 64;
}

using var shutdown = new CancellationTokenSource();
ConsoleCancelEventHandler handler = (_, eventArgs) => { eventArgs.Cancel = true; shutdown.Cancel(); };
Console.CancelKeyPress += handler;
try
{
    PrintEnvironment(options!);
    LoadResult result = await new LoadRunner().RunAsync(options!, shutdown.Token);
    PrintResult(options!, result);
    IReadOnlyList<string> failures = FunctionalCriteria.Evaluate(result);
    foreach (string failure in failures) { Console.Error.WriteLine($"FAIL: {failure}"); }
    int exitCode = FunctionalCriteria.ExitCode(result);
    if (options!.OutputPath is not null)
    { await ResultWriter.WriteAsync(options.OutputPath, options, result, failures, exitCode); }
    return exitCode;
}
finally { Console.CancelKeyPress -= handler; }

static void PrintEnvironment(LoadOptions options)
{
    Console.WriteLine($"profile={options.Profile} samples={options.SampleCount} rate={options.RateLabel}/s duration={options.Duration} source.requested={options.Source}");
    Console.WriteLine($"warmup.excluded={options.Warmup} telemetry={options.Telemetry} detailedPerFrameInstrumentation={(options.Telemetry == TelemetryMode.Full ? "enabled" : "disabled")} progress={options.Progress} progressTimeout={options.ProgressTimeout} cleanupTimeout={options.CleanupTimeout}");
    Console.WriteLine($"os={RuntimeInformation.OSDescription} arch={RuntimeInformation.ProcessArchitecture} runtime={RuntimeInformation.FrameworkDescription}");
    Console.WriteLine($"machine={Environment.MachineName} cpu.logical={Environment.ProcessorCount} commit={TryGitCommit() ?? "unavailable"}");
    Console.WriteLine($"assembly={Assembly.GetExecutingAssembly().GetName().Version} pid={Environment.ProcessId}");
}

static string? TryGitCommit()
{
    try
    {
        using var process = Process.Start(new ProcessStartInfo("git", "rev-parse --short HEAD")
        { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true });
        if (process is null || !process.WaitForExit(2000) || process.ExitCode != 0) { return null; }
        return process.StandardOutput.ReadToEnd().Trim();
    }
    catch { return null; }
}

static void PrintResult(LoadOptions options, LoadResult result)
{
    static string Metric<T>(T? value, string? format = null, string unit = "") where T : struct, IFormattable => value is { } number ? number.ToString(format, System.Globalization.CultureInfo.InvariantCulture) + unit : "unavailable";
    static string Percentiles(PercentileSnapshot? snapshot) => snapshot is not { } value ? "unavailable" :
        $"n={value.Population} window={value.WindowPopulation} mean={value.Mean:F3}us p50={value.P50:F3}us p95={value.P95:F3}us p99={value.P99:F3}us";
    Console.WriteLine($"result produced={result.Produced} consumed={result.Consumed} released={result.Released} outstanding={result.OutstandingBuffers} outstanding.max={result.MaximumOutstandingBuffers}");
    Console.WriteLine($"campaign={result.Outcome} campaign.primary={result.PrimaryOutcome} cleanup.succeeded={result.CleanupSucceeded} source={result.SourceDescription}");
    Console.WriteLine($"active.seconds={result.ActiveWindow.Seconds:F6} target.rate={result.TargetRate?.ToString("F2") ?? "unpaced"}/s grid.rate={result.GridRate?.ToString("F2") ?? "none"}/s offered.rate={result.ActiveWindow.OfferedPerSecond:F2}/s accepted.rate={result.ActiveWindow.AcceptedPerSecond:F2}/s consumed.rate={result.ActiveWindow.ConsumedPerSecond:F2}/s drained.duringStop={result.DrainedDuringStop}");
    var counters = result.Counters;
    Console.WriteLine($"consumed.afterActive.beforeStop={result.ConsumedAfterActiveBeforeStop} (-1 means stop cut unavailable)");
    Console.WriteLine($"lifetime offered={counters.Offered} generated={counters.Generated} accepted={counters.Accepted} consumed={counters.Consumed} released={counters.Released} untransferred={counters.Untransferred} attempts.beforeFrame={counters.Offered - counters.Generated} rents={counters.Rented} returns={counters.Returned} inTransit={counters.InTransit}");
    Console.WriteLine($"demand.untilActiveEnd scheduled={result.Demand.Scheduled} fulfilled.attempts={result.Demand.Offered - result.Demand.EarlyOrDuplicate} omitted={result.Demand.Missed} earlyOrDuplicate={result.Demand.EarlyOrDuplicate} pacing.delay.mean={Metric(result.Demand.MeanDelayMicroseconds, "F3", "us")} pacing.delay.max={Metric(result.Demand.MaximumDelayMicroseconds, "F3", "us")}");
    if (result.TargetRate is { } target && (result.ActiveWindow.OfferedPerSecond < target || result.ActiveWindow.ConsumedPerSecond < target))
    { Console.WriteLine("TARGET_NOT_REACHED: requested rate was not fully offered and/or consumed; this is not a capacity baseline."); }
    Console.WriteLine($"visual received={result.VisualReceived} published={result.VisualPublished} replaced={result.VisualReplaced} dropped={result.VisualDropped} observerErrors={result.VisualObserverErrors}");
    Console.WriteLine($"throughput.active={result.ThroughputFramesPerSecond:F2} frames/s visual.accept-to-callback {Percentiles(result.VisualDeliveryLatencyMicroseconds)}");
    Console.WriteLine($"projection {Percentiles(result.ProjectionMicroseconds)}");
    Console.WriteLine($"memory.postCleanup.managedEstimate={result.ManagedBytes} workingSet.postCleanup={result.WorkingSetBytes} workingSet.active.sampledMax={Metric(result.MaximumWorkingSetBytes)} gc.active={result.Gen0}/{result.Gen1}/{result.Gen2}");
    Console.WriteLine($"cpu.active.average={result.CpuPercent:F2}% cpu.active.sampledMax={Metric(result.MaximumCpuPercent, "F2", "%")} cpu.startup.seconds={result.StartupCpuSeconds:F6} cpu.cleanup.seconds={result.CleanupCpuSeconds:F6} neutral.cleanup={result.StopDuration.TotalMilliseconds:F3}ms barrier={result.FramesReleasedBarrier} errors={result.Errors.Count} cancelled={result.Cancelled}");
    Console.WriteLine($"correlation.misses={Metric(result.CorrelationMisses)} correlation.slotOverwrites={Metric(result.CorrelationOverwrites)} histogram={(options.Telemetry == TelemetryMode.Full ? "last-8192-active-observations" : "unavailable")} resourceSeries.retained={Metric(result.ResourceSamples?.Length)} resourceSeries.total={Metric(result.TotalResourceSamples)}");
    if (options.Telemetry == TelemetryMode.Full)
    {
        foreach (var sample in result.ResourceSamples ?? [])
        { Console.WriteLine($"resource elapsed={sample.ElapsedSeconds:F6}s interval={sample.IntervalSeconds:F6}s cpu={sample.CpuPercent:F3}% managedEstimate={sample.ManagedEstimateBytes} workingSet={sample.WorkingSetBytes} accepted={sample.Accepted} consumed={sample.Consumed}"); }
    }
    foreach (var error in result.Errors) { Console.Error.WriteLine($"error {error}"); }
}
