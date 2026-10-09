using System.Runtime.CompilerServices;
using UTStudio.Contracts.Inspection;
using UTStudio.Domain.Acquisition;
using UTStudio.Domain.Inspection;

namespace UTStudio.Visualization.Core;

internal static class AScanVisualDeliveryTestFactory
{
    private static readonly ConditionalWeakTable<AScanVisualDelivery, ScriptedProvider> Providers = new();
    private static readonly InspectionId InspectionId =
        new(Guid.Parse("71000000-0000-0000-0000-000000000071"));

    internal static AScanVisualDelivery Create(IAScanProjector? projector = null,
        TimeProvider? timeProvider = null)
    {
        var provider = new ScriptedProvider();
        var delivery = new AScanVisualDelivery(provider, projector, timeProvider);
        Providers.Add(delivery, provider);
        return delivery;
    }

    internal static IInspectionRunWriter CreateRunWriter(AScanVisualDelivery delivery, AcquisitionRunId runId) =>
        new TestRunWriter(Providers.TryGetValue(delivery, out var provider)
            ? provider : throw new InvalidOperationException("Unknown authoritative test delivery."), runId);

    internal static void ConfirmAndNotify(this AScanVisualDelivery delivery,
        ConventionalUtFrameMetadata metadata, ulong sequence, ulong acquisitionIndex,
        TimeSpan elapsedSinceRunStart, ReadOnlySpan<short> samples)
    {
        ScriptedProvider provider = Providers.TryGetValue(delivery, out var found)
            ? found : throw new InvalidOperationException("The delivery was not created by the authoritative test factory.");
        var stream = new UtStreamIdentity(InspectionId, metadata.RunId, metadata.SourceId,
            metadata.Configuration.PhysicalChannelId);
        var identity = new TraceIdentity(stream, acquisitionIndex);
        var descriptor = new TraceDescriptor(identity, sequence, elapsedSinceRunStart,
            metadata.RunStartedAtUtc + elapsedSinceRunStart, null, samples.Length,
            metadata.Configuration.SampleRateHz, metadata.Configuration.FirstSampleOffsetSeconds,
            metadata.Configuration.SignalMode);
        InspectionRevision revision = provider.Confirm(descriptor, samples);
        delivery.NotifyCommitted(identity, revision);
    }

    private sealed class ScriptedProvider : IUtTraceProvider
    {
        private readonly object _gate = new();
        private readonly Dictionary<TraceIdentity, (TraceDescriptor Descriptor, short[] Samples, InspectionRevision Revision)> _traces = [];
        private ulong _revision;
        internal InspectionRevision Confirm(TraceDescriptor descriptor, ReadOnlySpan<short> samples)
        {
            lock (_gate)
            {
                var revision = new InspectionRevision(++_revision);
                _traces[descriptor.Identity] = (descriptor, samples.ToArray(), revision);
                return revision;
            }
        }
        public ValueTask<InspectionTraceReadResult> ReadAsync(TraceIdentity identity,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_gate)
            {
                return _traces.TryGetValue(identity, out var trace)
                    ? ValueTask.FromResult(new InspectionTraceReadResult(InspectionReadStatus.Available,
                        trace.Revision, new Lease(trace.Descriptor, trace.Samples)))
                    : ValueTask.FromResult(new InspectionTraceReadResult(InspectionReadStatus.NotAvailable, new(_revision)));
            }
        }
    }

    private sealed class TestRunWriter(ScriptedProvider provider, AcquisitionRunId runId) : IInspectionRunWriter
    {
        private bool _sealed;
        public AcquisitionRunId RunId { get; } = runId;
        public InspectionRunState State => _sealed ? InspectionRunState.Sealed : InspectionRunState.Open;
        public ValueTask<InspectionAppendResult> CommitAsync(InspectionCommitInput commit,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_sealed) return ValueTask.FromResult(new InspectionAppendResult(InspectionAppendStatus.Closed, default, false));
            InspectionRevision revision = provider.Confirm(commit.Trace.Descriptor, commit.Trace.Samples.Span);
            return ValueTask.FromResult(new InspectionAppendResult(InspectionAppendStatus.Committed, revision, true));
        }
        public ValueTask<InspectionAppendResult> CommitGapAsync(GapRecord gap,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new InspectionAppendResult(InspectionAppendStatus.Committed, new(1), true));
        public ValueTask<InspectionRunSealResult> SealRunAsync(CancellationToken cancellationToken = default)
        { _sealed = true; return ValueTask.FromResult(new InspectionRunSealResult(RunId, new(1), null, null, null, null, 0, 0)); }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class Lease(TraceDescriptor descriptor, short[] samples) : IInspectionTraceLease
    {
        private short[]? _samples = samples;
        public TraceDescriptor Descriptor { get; } = descriptor;
        public IReadOnlyUtSamples Samples { get; } = new SamplesView(samples);
        public void Dispose() => Interlocked.Exchange(ref _samples, null);
        private sealed class SamplesView(short[] values) : IReadOnlyUtSamples
        {
            public int Count => values.Length;
            public short this[int index] => values[index];
            public void CopyTo(Span<short> destination) => values.CopyTo(destination);
        }
    }
}
