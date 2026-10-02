using UTStudio.Domain.Acquisition;

namespace UTStudio.Visualization.Core;

internal enum BScanVisualAuditEventKind
{
    BootstrapRequested,
    BootstrapPending,
    BuildAdmitted,
    BuildStarted,
    BuildCompleted,
    CommitAttempted,
    CandidateCommitted,
    CandidateRejectedStale,
    CandidateRejectedClosed,
    RecoveryRequested,
    CallbackQueued,
    CallbackStarted,
    CallbackCompleted,
    ConsumerRemoved
}

internal enum BScanVisualAuditResult { None, Accepted, Committed, Stale, Closed, Faulted }

internal enum BScanVisualAuditRejection { None, IngressState, StoreState, Consumer, Generation, Presentation, Signature, Closed }

internal readonly record struct BScanVisualAuditEntry(
    long Sequence,
    BScanVisualAuditEventKind Kind,
    string ConsumerId,
    AcquisitionRunId? RunId,
    long IngressEpoch,
    long RequestGeneration,
    long PresentationEpoch,
    ulong StoreRevision,
    int Signature,
    BScanVisualAuditResult Result,
    BScanVisualAuditRejection Rejection);

internal readonly record struct BScanVisualAuditSnapshot(
    IReadOnlyList<BScanVisualAuditEntry> Entries,
    long OverflowCount);

internal sealed class BScanVisualAudit
{
    private static long _globalSequence;
    private readonly object _gate = new();
    private readonly BScanVisualAuditEntry[] _entries;
    private int _next;
    private int _count;
    private long _overflow;

    internal BScanVisualAudit(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        _entries = new BScanVisualAuditEntry[capacity];
    }

    internal void Record(BScanVisualAuditEventKind kind, string consumerId,
        BScanIngressVisualState? ingressState, long requestGeneration, long presentationEpoch,
        BScanRenderRequest? request = null, BScanVisualAuditResult result = BScanVisualAuditResult.None,
        BScanVisualAuditRejection rejection = BScanVisualAuditRejection.None)
    {
        long sequence = Interlocked.Increment(ref _globalSequence);
        int signature = request is null ? 0 : HashCode.Combine(request.RunId, request.SpatialViewport,
            request.TimeViewport, request.PixelWidth, request.PixelHeight,
            request.RenderConfigurationVersion, request.FollowLiveWindow, request.EffectiveResampling);
        var entry = new BScanVisualAuditEntry(sequence, kind, consumerId,
            ingressState?.Metadata.RunId ?? request?.RunId, ingressState?.Epoch ?? 0,
            requestGeneration, presentationEpoch, ingressState?.StoreState.StoreRevision ?? 0,
            signature, result, rejection);
        lock (_gate)
        {
            if (_count == _entries.Length) { _overflow++; }
            else { _count++; }
            _entries[_next] = entry;
            _next = (_next + 1) % _entries.Length;
        }
    }

    internal BScanVisualAuditSnapshot Snapshot()
    {
        lock (_gate)
        {
            var copy = new BScanVisualAuditEntry[_count];
            int first = (_next - _count + _entries.Length) % _entries.Length;
            for (int index = 0; index < _count; index++)
            { copy[index] = _entries[(first + index) % _entries.Length]; }
            Array.Sort(copy, static (left, right) => left.Sequence.CompareTo(right.Sequence));
            return new(copy, _overflow);
        }
    }
}
