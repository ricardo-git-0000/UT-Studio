using System.Text.Json;

namespace UTStudio.LoadTests;

internal static class ResultWriter
{
    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = true };

    internal static async Task WriteAsync(string path, LoadOptions options, LoadResult result,
        IReadOnlyList<string> failures, int exitCode)
    {
        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory)) { Directory.CreateDirectory(directory); }
        var report = new
        {
            schemaVersion = 4,
            referenceMachine = "VM-R7700X-4vCPU-8GiB-W11-26200-dotnet10.0.11",
            interpretation = "same-VM regression reference; not an absolute product-capacity measurement",
            options = new
            {
                profile = options.Profile.ToString(),
                options.SampleCount,
                rate = options.Rate,
                duration = options.Duration,
                warmup = options.Warmup,
                source = options.Source.ToString(),
                requestedPacing = options.PacingSpecified ? options.Pacing.Label() : null,
                maxCatchUp = options.MaxCatchUp,
                telemetry = options.Telemetry.ToString(),
                detailedPerFrameInstrumentation = options.Telemetry == TelemetryMode.Full ? "enabled" : "disabled",
                progress = options.Progress.ToString()
            },
            result = new
            {
                outcome = result.Outcome.ToString(),
                primaryOutcome = result.PrimaryOutcome.ToString(),
                result.CleanupSucceeded,
                result.SourceDescription,
                effectivePacing = result.EffectivePacing.Label(),
                result.Produced,
                result.Consumed,
                result.Released,
                result.OutstandingBuffers,
                result.MaximumOutstandingBuffers,
                result.Counters,
                result.ActiveWindow,
                rates = new
                {
                    target = result.TargetRate,
                    offered = result.ActiveWindow.OfferedPerSecond,
                    accepted = result.ActiveWindow.AcceptedPerSecond,
                    consumed = result.ActiveWindow.ConsumedPerSecond
                },
                result.DrainedDuringStop,
                result.ConsumedAfterActiveBeforeStop,
                result.Demand,
                result.TargetRate,
                result.GridRate,
                result.DiagnosticTargetDeficitFrames,
                result.ThroughputFramesPerSecond,
                result.VisualReceived,
                result.VisualPublished,
                result.VisualReplaced,
                result.VisualDropped,
                result.VisualObserverErrors,
                result.VisualDeliveryLatencyMicroseconds,
                result.ProjectionMicroseconds,
                result.ManagedBytes,
                result.ManagedBytesAtActiveStart,
                result.ManagedBytesAtActiveEnd,
                result.WorkingSetBytesAtActiveStart,
                result.WorkingSetBytesAtActiveEnd,
                result.WorkingSetBytes,
                result.MaximumWorkingSetBytes,
                result.Gen0,
                result.Gen1,
                result.Gen2,
                result.CpuPercent,
                result.MaximumCpuPercent,
                result.StartupCpuSeconds,
                result.CleanupCpuSeconds,
                result.StopDuration,
                result.FramesReleasedBarrier,
                result.CorrelationMisses,
                result.CorrelationOverwrites,
                resourceSamples = result.ResourceSamples,
                result.TotalResourceSamples,
                errors = result.Errors.Select(error => new { error.Code, error.Message })
            },
            failures,
            exitCode
        };
        await using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None,
            bufferSize: 16 * 1024, useAsync: true);
        await JsonSerializer.SerializeAsync(stream, report, SerializerOptions).ConfigureAwait(false);
    }
}
