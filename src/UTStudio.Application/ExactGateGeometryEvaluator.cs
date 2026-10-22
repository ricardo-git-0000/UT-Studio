using System.Buffers;
using UTStudio.Contracts.Inspection;
using UTStudio.Contracts.Processing;
using UTStudio.Processing.Core;

namespace UTStudio.Application;

public sealed class ExactGateGeometryEvaluatorOptions
{
    public const int DefaultMaximumConcurrentEvaluations = 2;
    public const long DefaultMaximumRetainedScratchElements = 2_000_000;
    public ExactGateGeometryEvaluatorOptions(int maximumConcurrentEvaluations = DefaultMaximumConcurrentEvaluations,
        long maximumRetainedScratchElements = DefaultMaximumRetainedScratchElements)
    {
        if (maximumConcurrentEvaluations < 1) throw new ArgumentOutOfRangeException(nameof(maximumConcurrentEvaluations));
        if (maximumRetainedScratchElements < maximumConcurrentEvaluations * 524_288L)
            throw new ArgumentOutOfRangeException(nameof(maximumRetainedScratchElements));
        MaximumConcurrentEvaluations = maximumConcurrentEvaluations;
        MaximumRetainedScratchElements = maximumRetainedScratchElements;
    }
    public int MaximumConcurrentEvaluations { get; }
    public long MaximumRetainedScratchElements { get; }
}

/// <summary>Copies one exact RF trace, releases its lease, then evaluates an already compiled plan.</summary>
public sealed class ExactGateGeometryEvaluator : IGateGeometryEvaluator
{
    private readonly IUtTraceProvider _traces;
    private readonly IGateGeometrySourceGuard _guard;
    private readonly GateEvaluator _evaluator;
    private readonly SemaphoreSlim _capacity;

    public ExactGateGeometryEvaluator(IUtTraceProvider traces, IGateGeometrySourceGuard guard,
        ExactGateGeometryEvaluatorOptions? options = null)
        : this(traces, guard, options, null) { }

    internal ExactGateGeometryEvaluator(IUtTraceProvider traces, IGateGeometrySourceGuard guard,
        ExactGateGeometryEvaluatorOptions? options, GateEvaluator? evaluator)
    {
        _traces = traces ?? throw new ArgumentNullException(nameof(traces));
        _guard = guard ?? throw new ArgumentNullException(nameof(guard));
        ExactGateGeometryEvaluatorOptions resolved = options ?? new();
        _evaluator = evaluator ?? new(new GateScratchPool(resolved.MaximumConcurrentEvaluations,
            resolved.MaximumRetainedScratchElements));
        int maximum = resolved.MaximumConcurrentEvaluations;
        _capacity = new(maximum, maximum);
    }

    public async ValueTask<GateGeometryEvaluationResult> EvaluateAsync(GateGeometryEvaluationRequest request,
        CompiledGatePlan compiledPlan, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(compiledPlan);
        ValidatePlanIdentity(request.Source, compiledPlan);
        if (cancellationToken.IsCancellationRequested)
            return Result(GateGeometryEvaluationStatus.Cancelled, request);
        if (!_capacity.Wait(0)) return Result(GateGeometryEvaluationStatus.CapacityExceeded, request);
        try
        {
            GateGeometrySourceValidationStatus initial;
            try { initial = _guard.Validate(request.Source); }
            catch (Exception error) { return Fault(request, $"source-validation-{error.GetType().Name}"); }
            if (initial != GateGeometrySourceValidationStatus.Current) return GuardResult(initial, request);
            InspectionTraceReadResult read;
            try { read = await _traces.ReadAsync(request.TraceIdentity, cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            { return Result(GateGeometryEvaluationStatus.Cancelled, request); }
            catch (Exception error) { return Fault(request, $"trace-read-{error.GetType().Name}"); }
            if (read.Status != InspectionReadStatus.Available || read.Lease is null)
                return ReadResult(read.Status, request, read.ErrorCode);

            short[]? samples = null;
            TraceDescriptor descriptor;
            try
            {
                using IInspectionTraceLease lease = read.Lease;
                descriptor = lease.Descriptor;
                if (read.Revision != request.ExpectedInspectionRevision || descriptor.Identity != request.TraceIdentity)
                    return Result(GateGeometryEvaluationStatus.Stale, request);
                if (lease.Samples.Count != descriptor.SampleCount)
                    return Fault(request, "trace-sample-count-mismatch");
                samples = ArrayPool<short>.Shared.Rent(lease.Samples.Count);
                lease.Samples.CopyTo(samples.AsSpan(0, lease.Samples.Count));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            { return Result(GateGeometryEvaluationStatus.Cancelled, request); }
            catch (Exception error)
            {
                if (samples is not null) ArrayPool<short>.Shared.Return(samples);
                return Fault(request, $"trace-copy-{error.GetType().Name}");
            }

            try
            {
                if (cancellationToken.IsCancellationRequested) return Result(GateGeometryEvaluationStatus.Cancelled, request);
                var confirmed = new ConfirmedGateTraceDescriptor(descriptor, request.TraceIdentity,
                    request.ExpectedInspectionRevision, descriptor.TraceQuality);
                GateEvaluationResult evaluated = _evaluator.Evaluate(compiledPlan, confirmed,
                    samples.AsSpan(0, descriptor.SampleCount), cancellationToken);
                if (evaluated.Status == GateEvaluationStatus.Cancelled)
                    return Result(GateGeometryEvaluationStatus.Cancelled, request);
                if (evaluated.Status != GateEvaluationStatus.Completed || evaluated.Batch is null)
                    return evaluated.PrimaryError?.Code == GateEvaluationErrorCode.ScratchUnavailable
                        ? Result(GateGeometryEvaluationStatus.CapacityExceeded, request)
                        : Fault(request, evaluated.PrimaryError?.Code.ToString() ?? "evaluation-failed");
                GateGeometrySourceValidationStatus final;
                try { final = _guard.Validate(request.Source); }
                catch (Exception error) { return Fault(request, $"source-validation-{error.GetType().Name}"); }
                if (final != GateGeometrySourceValidationStatus.Current) return GuardResult(final, request);
                return new(GateGeometryEvaluationStatus.Completed, request,
                    BuildSnapshot(request, compiledPlan, evaluated.Batch));
            }
            finally { ArrayPool<short>.Shared.Return(samples); }
        }
        finally { _capacity.Release(); }
    }

    private static GateGeometrySnapshot BuildSnapshot(GateGeometryEvaluationRequest request,
        CompiledGatePlan plan, GateEvaluationBatch batch)
    {
        GateEvaluationEntry[] entries = batch.Entries.ToArray();
        var items = new GateGeometryItemSnapshot[plan.DeclaredGates.Count];
        for (int index = 0; index < plan.DeclaredGates.Count; index++)
        {
            GateDefinition gate = plan.DeclaredGates[index].Definition;
            GateEvaluationEntry entry = entries[index];
            bool traceOrigin = gate.Window.StartReference.Kind == GateTimeReferenceKind.TraceOrigin;
            GateEvaluationEntry? dependency = traceOrigin ? null : entries[plan.DeclaredGates[index].DependencyIndex];
            double? anchorTime = traceOrigin ? 0d : dependency?.DetectionTimeSeconds;
            double? start = anchorTime.HasValue ? anchorTime.Value + gate.Window.StartOffsetSeconds : null;
            GateAnchorSnapshot? anchor = dependency is null ? null : new(dependency.GateId,
                dependency.GateRevision, gate.Window.StartReference.Kind,
                gate.Window.StartReference.DependencyGateId, dependency.Status, dependency.CausalGateId,
                dependency.CausalStatus, dependency.DetectionTimeSeconds);
            items[index] = new(gate.Id, gate.Revision, gate.SignalDomain, entry.Status, anchor, start,
                start + gate.Window.DurationSeconds, gate.Detector.Threshold?.NormalizedValue);
        }
        return new(request.Source, request.TraceIdentity, request.ExpectedInspectionRevision, items);
    }

    private static void ValidatePlanIdentity(GateGeometrySourceIdentity source, CompiledGatePlan plan)
    {
        if (plan.Source.Id != source.Authority.PlanId || plan.Source.Revision != source.Revision ||
            !plan.Signature.Equals(source.Signature))
            throw new ArgumentException("The compiled plan does not match the geometry source.", nameof(plan));
    }

    private static GateGeometryEvaluationResult ReadResult(InspectionReadStatus status,
        GateGeometryEvaluationRequest request, string? error) => status switch
        {
            InspectionReadStatus.Cancelled => Result(GateGeometryEvaluationStatus.Cancelled, request),
            InspectionReadStatus.Closed => Result(GateGeometryEvaluationStatus.Closed, request),
            InspectionReadStatus.Faulted => Fault(request, error ?? "trace-read-faulted"),
            _ => Result(GateGeometryEvaluationStatus.NotAvailable, request)
        };

    private static GateGeometryEvaluationResult GuardResult(GateGeometrySourceValidationStatus status,
        GateGeometryEvaluationRequest request) => Result(status == GateGeometrySourceValidationStatus.Closed
            ? GateGeometryEvaluationStatus.Closed : GateGeometryEvaluationStatus.Stale, request);
    private static GateGeometryEvaluationResult Result(GateGeometryEvaluationStatus status,
        GateGeometryEvaluationRequest request) => new(status, request);
    private static GateGeometryEvaluationResult Fault(GateGeometryEvaluationRequest request, string code) =>
        new(GateGeometryEvaluationStatus.Faulted, request, errorCode: code);
}
