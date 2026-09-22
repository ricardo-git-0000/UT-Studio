using UTStudio.Contracts.Presentation;
using UTStudio.Domain.Acquisition;

namespace UTStudio.Application;

/// <summary>
/// Synchronous two-branch visual fan-out. It invokes A before B and never copies samples.
/// A close may overlap an accept that was already admitted, as allowed by
/// <see cref="IConventionalFrameSink"/>; child sinks must therefore honor that contract.
/// </summary>
public sealed class ConventionalFrameSinkFanOut : IConventionalFrameSink
{
    private readonly Branch _aScan;
    private readonly Branch _bScan;

    public ConventionalFrameSinkFanOut(IConventionalFrameSink aScan, IConventionalFrameSink bScan)
    {
        ArgumentNullException.ThrowIfNull(aScan);
        ArgumentNullException.ThrowIfNull(bScan);
        _aScan = new("AScan", aScan);
        _bScan = new("BScan", bScan);
    }

    public IReadOnlyList<VisualBranchDiagnostics> Diagnostics => [_aScan.GetDiagnostics(), _bScan.GetDiagnostics()];

    public void OpenRun(AcquisitionRunId runId)
    {
        if (!runId.IsValid) { throw new ArgumentException("Invalid run identifier.", nameof(runId)); }
        _aScan.Open(runId);
        _bScan.Open(runId);
    }

    public void Accept(ConventionalUtFrameMetadata metadata, ulong sequence, ulong acquisitionIndex,
        TimeSpan elapsedSinceRunStart, ScanSpatialCoordinate? spatialCoordinate, ReadOnlySpan<short> samples)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        _aScan.Accept(metadata, sequence, acquisitionIndex, elapsedSinceRunStart, spatialCoordinate, samples);
        _bScan.Accept(metadata, sequence, acquisitionIndex, elapsedSinceRunStart, spatialCoordinate, samples);
    }

    public void CloseRun(AcquisitionRunId runId)
    {
        if (!runId.IsValid) { throw new ArgumentException("Invalid run identifier.", nameof(runId)); }
        _aScan.Close(runId);
        _bScan.Close(runId);
    }

    private sealed class Branch(string id, IConventionalFrameSink sink)
    {
        private readonly object _gate = new();
        private AcquisitionRunId? _runId;
        private bool _open, _disabled, _closed;
        private ulong _failureCount;
        private VisualBranchFailure? _firstFailure, _latestFailure;

        internal void Open(AcquisitionRunId runId)
        {
            lock (_gate)
            {
                if (_runId == runId && (_open || _disabled || _closed)) { return; }
                if (_runId is not null) { throw new InvalidOperationException($"Visual branch {id} already has a run."); }
                _open = false;
                _disabled = false;
                _closed = false;
                _runId = runId;
            }

            try
            {
                sink.OpenRun(runId);
                lock (_gate) { if (!_closed) { _open = true; } }
            }
            catch (Exception error)
            {
                lock (_gate) { _disabled = true; }
                Record(VisualBranchOperation.Open, error);
                Close(runId);
            }
        }

        internal void Accept(ConventionalUtFrameMetadata metadata, ulong sequence, ulong acquisitionIndex,
            TimeSpan elapsedSinceRunStart, ScanSpatialCoordinate? spatialCoordinate, ReadOnlySpan<short> samples)
        {
            lock (_gate) { if (!_open || _disabled || _closed || _runId != metadata.RunId) { return; } }
            try { sink.Accept(metadata, sequence, acquisitionIndex, elapsedSinceRunStart, spatialCoordinate, samples); }
            catch (Exception error)
            {
                lock (_gate) { _disabled = true; }
                Record(VisualBranchOperation.Accept, error);
                Close(metadata.RunId);
            }
        }

        internal void Close(AcquisitionRunId runId)
        {
            bool invoke;
            lock (_gate)
            {
                if (_runId != runId || _closed) { return; }
                _closed = true;
                _open = false;
                invoke = true;
            }
            if (!invoke) { return; }
            try { sink.CloseRun(runId); }
            catch (Exception error) { Record(VisualBranchOperation.Close, error); }
            finally { lock (_gate) { if (_runId == runId) { _runId = null; } } }
        }

        private void Record(VisualBranchOperation operation, Exception error)
        {
            var failure = new VisualBranchFailure(id, operation,
                new($"visual.{id.ToLowerInvariant()}.{operation.ToString().ToLowerInvariant()}",
                    string.IsNullOrWhiteSpace(error.Message) ? error.GetType().Name : error.Message));
            lock (_gate)
            {
                _failureCount++;
                _firstFailure ??= failure;
                _latestFailure = failure;
            }
        }

        internal VisualBranchDiagnostics GetDiagnostics()
        {
            lock (_gate)
            {
                return new(id, _open, _disabled, _closed, _failureCount, _firstFailure, _latestFailure);
            }
        }
    }
}
