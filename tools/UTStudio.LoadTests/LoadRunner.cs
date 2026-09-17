using System.Diagnostics;
using UTStudio.Acquisition.Simulator;
using UTStudio.Application;
using UTStudio.Contracts.Acquisition;
using UTStudio.Contracts.Application;
using UTStudio.Domain.Acquisition;
using UTStudio.Visualization.Core;

namespace UTStudio.LoadTests;

internal sealed class LoadRunner
{
    internal async Task<LoadResult> RunAsync(LoadOptions options, CancellationToken cancellationToken,
        TimeSpan? telemetryInterval = null, Action? runStarted = null,
        Func<LoadTelemetry, IUtFrameSource>? sourceFactory = null, Action<ResourceSample>? sampleObserved = null)
    {
        var telemetry = new LoadTelemetry(options.Rate);
        bool experimental = options.Source switch
        {
            LoadSourceMode.Experimental => true,
            LoadSourceMode.Production => false,
            _ => options.Rate is null or > 100
        };
        IUtFrameSource source = sourceFactory?.Invoke(telemetry) ?? (experimental
            ? new ExperimentalFrameSource(options, telemetry)
            : new SimulatorUtFrameSource(new UtSourceId("load-test-simulator"),
                new SimulatorOptions(seed: 1, maxAScansPerSecond: options.Rate!.Value))
            { Metrics = telemetry.Counters });
        var visual = new AScanVisualDelivery(new InstrumentedProjector(new AScanProjector(1024), telemetry));
        IDisposable subscription = visual.Subscribe(new SnapshotObserver(telemetry));
        var observedSource = new RunCaptureSource(source);
        var session = new ApplicationSession(observedSource, new InstrumentedFrameSink(visual, telemetry));
        var configuration = new ConventionalAcquisitionConfiguration(new PhysicalChannelId(0), options.SampleCount, 50_000_000);
        using var process = Process.GetCurrentProcess();
        TimeSpan cpuOrigin = process.TotalProcessorTime, cpuActiveStart = cpuOrigin, cpuActiveEnd = cpuOrigin;
        long cpuActiveStartAt = 0, cpuActiveEndAt = 0;
        int gen0 = 0, gen1 = 0, gen2 = 0, activeGen0 = 0, activeGen1 = 0, activeGen2 = 0;
        CounterSnapshot start = default, end = default;
        var stopBoundary = new TaskCompletionSource<CounterSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        CampaignOutcome outcome = CampaignOutcome.Completed;
        CampaignOutcome primaryOutcome = CampaignOutcome.Completed;
        Task<CleanupReport>? pendingCleanup = null;
        List<UtSourceError> errors = [];
        var series = new ResourceSeries();
        long maximumWorkingSet = 0;
        double maximumCpu = 0;
        bool active = false, cleanupSucceeded = false, barrier = false;
        int outstanding = -1, maximumOutstanding = -1;
        SessionSnapshot terminal = session.Snapshot;
        TimeSpan stopDuration;
        DemandSnapshot demand = default;
        long lastSampleAt = 0;
        TimeSpan lastCpu = cpuOrigin;
        try
        {
            if (options.Rate is { } target && options.ProgressTimeout.TotalSeconds <= 1 / target)
            { throw new ArgumentException("Progress timeout must exceed target frame period."); }
            telemetry.Demand.Start(Stopwatch.GetTimestamp());
            await session.StartAsync(configuration, new AcquisitionRunId(Guid.NewGuid()), cancellationToken).ConfigureAwait(false);
            runStarted?.Invoke();
            if (options.Warmup > TimeSpan.Zero) { await Task.Delay(options.Warmup, cancellationToken).ConfigureAwait(false); }
            cancellationToken.ThrowIfCancellationRequested();
            process.Refresh();
            cpuActiveStart = process.TotalProcessorTime;
            cpuActiveStartAt = Stopwatch.GetTimestamp();
            gen0 = GC.CollectionCount(0); gen1 = GC.CollectionCount(1); gen2 = GC.CollectionCount(2);
            start = telemetry.BeginWindow();
            active = true;
            var watchdog = new ProgressWatchdog(start.Timestamp, start.Consumed,
                (long)(options.ProgressTimeout.TotalSeconds * Stopwatch.Frequency));
            long deadline = start.Timestamp + (long)(options.Duration.TotalSeconds * Stopwatch.Frequency);
            TimeSpan interval = telemetryInterval ?? TimeSpan.FromSeconds(1);
            if (interval <= TimeSpan.Zero) { throw new ArgumentOutOfRangeException(nameof(telemetryInterval)); }
            // Progress polling does not require printing or allocating a histogram snapshot.
            TimeSpan poll = TimeSpan.FromMilliseconds(Math.Max(1, Math.Min(200, options.ProgressTimeout.TotalMilliseconds / 2)));
            lastSampleAt = cpuActiveStartAt;
            lastCpu = cpuActiveStart;
            CounterSnapshot previous = start;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var current = telemetry.Counters.Snapshot();
                if (watchdog.Expired(current.Timestamp, current.Consumed)) { outcome = CampaignOutcome.NoProgress; break; }
                if (current.Timestamp >= deadline) { break; }
                if (Stopwatch.GetElapsedTime(lastSampleAt, current.Timestamp) >= interval)
                {
                    Sample(current, options.Telemetry == TelemetryMode.Full);
                    var rates = new RateWindow(previous, current);
                    if (options.Progress == ProgressMode.Normal)
                    { Console.WriteLine($"progress offered={current.Offered} generated={current.Generated} accepted={current.Accepted} consumed={current.Consumed} released={current.Released} inTransit={current.InTransit} offered.rate={rates.OfferedPerSecond:F2}/s accepted.rate={rates.AcceptedPerSecond:F2}/s consumed.rate={rates.ConsumedPerSecond:F2}/s"); }
                    previous = current;
                }
                TimeSpan remaining = Stopwatch.GetElapsedTime(current.Timestamp, deadline);
                await Task.Delay(remaining < poll ? remaining : poll, cancellationToken).ConfigureAwait(false);
            }
            void Sample(CounterSnapshot current, bool retain)
            {
                process.Refresh();
                TimeSpan cpu = process.TotalProcessorTime;
                long cpuAt = Stopwatch.GetTimestamp();
                TimeSpan wall = Stopwatch.GetElapsedTime(lastSampleAt, cpuAt);
                double percent = CpuMeasurement.Percent(cpu - lastCpu, wall, Environment.ProcessorCount);
                var sample = new ResourceSample(Stopwatch.GetElapsedTime(start.Timestamp, current.Timestamp).TotalSeconds,
                    wall.TotalSeconds, percent, GC.GetTotalMemory(false), process.WorkingSet64, current.Accepted, current.Consumed);
                if (retain) { series.Add(sample); }
                sampleObserved?.Invoke(sample);
                maximumCpu = Math.Max(maximumCpu, percent);
                maximumWorkingSet = Math.Max(maximumWorkingSet, sample.WorkingSetBytes);
                lastSampleAt = cpuAt; lastCpu = cpu;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { outcome = CampaignOutcome.Cancelled; }
        catch (Exception error) { errors.Add(Describe("load.run", error)); outcome = CampaignOutcome.FunctionalFailure; }
        finally
        {
            // Cut counts and wall time together BEFORE Stop; draining is reported separately.
            end = telemetry.EndWindow(out demand);
            if (outcome == CampaignOutcome.Completed && (!active || end.Consumed == start.Consumed))
            { outcome = CampaignOutcome.NoProgress; }
            primaryOutcome = outcome;
            if (!active) { start = end; }
            process.Refresh(); cpuActiveEnd = process.TotalProcessorTime;
            cpuActiveEndAt = Stopwatch.GetTimestamp();
            if (!active) { cpuActiveStart = cpuActiveEnd; }
            if (active)
            {
                activeGen0 = GC.CollectionCount(0) - gen0; activeGen1 = GC.CollectionCount(1) - gen1; activeGen2 = GC.CollectionCount(2) - gen2;
                TimeSpan residual = Stopwatch.GetElapsedTime(lastSampleAt, cpuActiveEndAt);
                double percent = CpuMeasurement.Percent(cpuActiveEnd - lastCpu, residual, Environment.ProcessorCount);
                var sample = new ResourceSample(Stopwatch.GetElapsedTime(start.Timestamp, end.Timestamp).TotalSeconds,
                    residual.TotalSeconds, percent, GC.GetTotalMemory(false), process.WorkingSet64, end.Accepted, end.Consumed);
                if (options.Telemetry == TelemetryMode.Full) { series.Add(sample); }
                maximumCpu = Math.Max(maximumCpu, percent);
                maximumWorkingSet = Math.Max(maximumWorkingSet, sample.WorkingSetBytes);
            }
            long stopping = Stopwatch.GetTimestamp();
            Task<CleanupReport> cleanup = Task.Run(CleanupAsync);
            try
            {
                var report = await cleanup.WaitAsync(options.CleanupTimeout).ConfigureAwait(false);
                errors.AddRange(report.Errors);
                barrier = report.Barrier;
                outstanding = report.Outstanding;
                maximumOutstanding = report.MaximumOutstanding;
                cleanupSucceeded = report.Errors.Count == 0 && barrier;
            }
            catch (TimeoutException)
            {
                outcome = CampaignOutcome.CleanupTimeout;
                errors.Add(new("load.cleanup_timeout", "Cleanup still pending; no buffer was forcibly returned. Late task remains observed."));
                pendingCleanup = cleanup;
                _ = cleanup.ContinueWith(t =>
                {
                    if (t.IsFaulted) { _ = t.Exception; }
                    else if (t.IsCompletedSuccessfully)
                    { foreach (var late in t.Result.Errors) { Console.Error.WriteLine($"late.cleanup.error={late}"); } }
                }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }
            stopDuration = Stopwatch.GetElapsedTime(stopping);
        }
        terminal = session.Snapshot;
        if (terminal.PrimaryError is not null) { errors.Add(terminal.PrimaryError); }
        errors.AddRange(terminal.CleanupErrors);
        if (terminal.VisualError is not null) { errors.Add(terminal.VisualError); }
        var statistics = visual.Statistics;
        if (statistics.LastError is not null) { errors.Add(statistics.LastError); }
        if (terminal.PrimaryError is not null || terminal.VisualError is not null || statistics.LastError is not null)
        { primaryOutcome = CampaignOutcome.FunctionalFailure; }
        if (errors.Count > 0 && outcome != CampaignOutcome.CleanupTimeout) { outcome = CampaignOutcome.FunctionalFailure; }
        var counters = telemetry.Counters.Snapshot();
        var stopCut = stopBoundary.Task.IsCompletedSuccessfully ? stopBoundary.Task.Result : default;
        var window = new RateWindow(start, end);
        if (outcome == CampaignOutcome.Completed && end.Consumed == start.Consumed) { outcome = CampaignOutcome.NoProgress; }
        process.Refresh();
        long managed = GC.GetTotalMemory(false), workingSet = process.WorkingSet64;
        return new(counters.Accepted, counters.Consumed, counters.Released, outstanding, maximumOutstanding,
            statistics.Received, statistics.Published, statistics.Replaced, statistics.Dropped, statistics.ObserverErrors,
            window.ConsumedPerSecond, telemetry.AcceptToCallbackMicroseconds.Snapshot(), telemetry.ProjectionMicroseconds.Snapshot(),
            managed, workingSet, maximumWorkingSet, activeGen0, activeGen1, activeGen2,
            active ? CpuMeasurement.Percent(cpuActiveEnd - cpuActiveStart, Stopwatch.GetElapsedTime(cpuActiveStartAt, cpuActiveEndAt), Environment.ProcessorCount) : 0,
            maximumCpu, barrier, stopDuration, errors, outcome == CampaignOutcome.Cancelled)
        {
            Outcome = outcome,
            PrimaryOutcome = primaryOutcome,
            PendingCleanup = pendingCleanup,
            CleanupSucceeded = cleanupSucceeded,
            Counters = counters,
            ActiveWindow = window,
            DrainedDuringStop = stopCut.Timestamp == 0 ? -1 : counters.Consumed - stopCut.Consumed,
            ConsumedAfterActiveBeforeStop = stopCut.Timestamp == 0 ? -1 : stopCut.Consumed - end.Consumed,
            Demand = demand,
            TargetRate = options.Rate,
            GridRate = telemetry.Demand.GridRate,
            SourceDescription = sourceFactory is not null ? "injected-test-source" : experimental
                ? "experimental; generator=LCG; pool=experimental.SamplePool; pacing=max means unpaced" : "productive; generator=SyntheticRfGenerator; pool=BoundedSampleBufferPool",
            CorrelationMisses = telemetry.CorrelationMisses,
            CorrelationOverwrites = telemetry.CorrelationOverwrites,
            ResourceSamples = series.Snapshot(),
            TotalResourceSamples = series.TotalSamples,
            StartupCpuSeconds = (cpuActiveStart - cpuOrigin).TotalSeconds,
            CleanupCpuSeconds = (process.TotalProcessorTime - cpuActiveEnd).TotalSeconds
        };

        async Task<CleanupReport> CleanupAsync()
        {
            List<UtSourceError> failures = [];
            stopBoundary.TrySetResult(telemetry.Counters.Snapshot());
            try { await session.StopAsync(CancellationToken.None).ConfigureAwait(false); }
            catch (Exception error) { failures.Add(Describe("load.stop", error)); }
            var cleanupTerminal = session.Snapshot;
            var (remaining, maximum) = source switch
            {
                ExperimentalFrameSource value => (value.OutstandingBuffers, value.MaximumOutstandingBuffers),
                SimulatorUtFrameSource value => (value.OutstandingBuffers, value.MaximumOutstandingBuffers),
                _ => (0, 0)
            };
            // Session Stop awaits ProducerCompletion, sole reader and AllFramesReleased.
            var run = observedSource.Run;
            bool released = run is null ? cleanupTerminal.Phase == SessionPhase.Idle :
                run.AllFramesReleased.IsCompletedSuccessfully && run.ProducerCompletion.IsCompleted &&
                run.Frames.Completion.IsCompleted && remaining == 0;
            try { await session.DisposeAsync().ConfigureAwait(false); }
            catch (Exception error) { failures.Add(Describe("load.session_cleanup", error)); }
            try { await source.DisposeAsync().ConfigureAwait(false); }
            catch (Exception error) { failures.Add(Describe("load.source_cleanup", error)); }
            try { subscription.Dispose(); await visual.DisposeAsync().ConfigureAwait(false); }
            catch (Exception error) { failures.Add(Describe("load.visual_cleanup", error)); }
            failures.AddRange(session.Snapshot.CleanupErrors);
            return new(released, remaining, maximum, failures);
        }
    }
    private static UtSourceError Describe(string code, Exception error) => new(code, error.Message);
    private sealed class SnapshotObserver(LoadTelemetry telemetry) : IObserver<AScanSnapshot>
    {
        public void OnNext(AScanSnapshot value) => telemetry.ObserveVisual(value.Sequence);
        public void OnError(Exception error) { }
        public void OnCompleted() { }
    }
}
