using UTStudio.Contracts.Processing;

namespace UTStudio.Processing.Core;

/// <summary>
/// Productive Phase 3 terminal sink. It acknowledges a batch synchronously and retains
/// neither the batch nor any of its contents. Phase 4 replaces it with the derived store.
/// </summary>
public sealed class TransientGateEvaluationBatchSink : IGateEvaluationBatchSink
{
    public ValueTask<GateBatchDeliveryResult> DeliverAsync(GateEvaluationBatch batch,
        CancellationToken exceptionalCancellation = default)
    {
        ArgumentNullException.ThrowIfNull(batch);
        return ValueTask.FromResult(exceptionalCancellation.IsCancellationRequested
            ? new GateBatchDeliveryResult(GateBatchDeliveryStatus.Cancelled)
            : GateBatchDeliveryResult.Accepted());
    }
}
