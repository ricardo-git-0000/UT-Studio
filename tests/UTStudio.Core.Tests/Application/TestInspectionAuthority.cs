using UTStudio.Application;
using UTStudio.Contracts.Acquisition;
using UTStudio.Contracts.Inspection;
using UTStudio.Contracts.Presentation;
using UTStudio.Domain.Acquisition;
using UTStudio.Domain.Inspection;

namespace UTStudio.Core.Tests.Application;

internal static class TestInspectionAuthority
{
    private static readonly InspectionId InspectionId = new(Guid.Parse("5d943510-aacf-48db-82af-ebf56f278ca1"));

    internal static ApplicationSession CreateSession(IUtFrameSource source,
        IConventionalFrameSink? visualSink = null) =>
        new(source, InspectionId, runId => new Writer(runId), visualSink);

    private sealed class Writer(AcquisitionRunId runId) : IInspectionRunWriter
    {
        private ulong _revision;
        private InspectionRunState _state = InspectionRunState.Open;

        public AcquisitionRunId RunId { get; } = runId;
        public InspectionRunState State => _state;

        public ValueTask<InspectionAppendResult> CommitAsync(InspectionCommitInput commit,
            CancellationToken cancellationToken = default) => Commit(cancellationToken);

        public ValueTask<InspectionAppendResult> CommitGapAsync(GapRecord gap,
            CancellationToken cancellationToken = default) => Commit(cancellationToken);

        public ValueTask<InspectionRunSealResult> SealRunAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _state = InspectionRunState.Sealed;
            return ValueTask.FromResult(new InspectionRunSealResult(RunId, new(_revision), null, null,
                null, null, 0, 0));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private ValueTask<InspectionAppendResult> Commit(CancellationToken cancellationToken)
        {
            if (cancellationToken.IsCancellationRequested)
            { return ValueTask.FromResult(new InspectionAppendResult(InspectionAppendStatus.Cancelled, new(_revision), true)); }
            if (_state != InspectionRunState.Open)
            { return ValueTask.FromResult(new InspectionAppendResult(InspectionAppendStatus.Closed, new(_revision), false)); }
            return ValueTask.FromResult(new InspectionAppendResult(InspectionAppendStatus.Committed,
                new(++_revision), true));
        }
    }
}
