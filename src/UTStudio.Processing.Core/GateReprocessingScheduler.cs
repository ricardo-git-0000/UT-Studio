using System.Threading.Channels;
using System.Security.Cryptography;
using System.Text;
using UTStudio.Contracts.Inspection;
using UTStudio.Contracts.Processing;
using UTStudio.Domain.Processing;

namespace UTStudio.Processing.Core;

/// <summary>Bounded historical scheduler. It owns workers and scratch that are not shared with live processing.</summary>
public sealed class GateReprocessingScheduler : IGateReprocessingScheduler, IOwnedGateReprocessingScheduler
{
    private readonly object _gate = new();
    private readonly IInspectionSealedRunSnapshotProvider _snapshots;
    private readonly IGateDerivedStore _store;
    private readonly GateScratchPool _scratchPool;
    private readonly GateReprocessingSchedulerOptions _options;
    private readonly GateReprocessingSampleBufferPool _sampleBuffers;
    private readonly TimeProvider _timeProvider;
    private readonly Channel<Job> _ready;
    private readonly CancellationTokenSource _stop = new();
    private readonly Task[] _workers;
    private readonly HashSet<Job> _jobs = [];
    private readonly HashSet<GateReprocessingAttemptId> _attempts = [];
    private int _admissions;
    private TaskCompletionSource? _admissionsDrained;
    private bool _accepting = true;
    private Task? _stopTask;
    private Task? _disposeTask;

    public GateReprocessingScheduler(IInspectionSealedRunSnapshotProvider snapshots, IGateDerivedStore store,
        GateReprocessingSchedulerOptions? options = null, GateScratchPool? reprocessingScratchPool = null,
        TimeProvider? timeProvider = null)
    {
        _snapshots = snapshots ?? throw new ArgumentNullException(nameof(snapshots));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _options = options ?? new();
        _sampleBuffers = new(_options.SampleBufferBudgetBytes);
        _scratchPool = reprocessingScratchPool ?? new(_options.MaximumWorkers,
            checked((long)_options.MaximumWorkers * GateScratchBuffer.ElementCapacityFor(65_536)));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _ready = Channel.CreateBounded<Job>(new BoundedChannelOptions(_options.MaximumAdmittedJobs)
        { FullMode = BoundedChannelFullMode.Wait, SingleReader = _options.MaximumWorkers == 1, SingleWriter = false });
        _workers = Enumerable.Range(0, _options.MaximumWorkers)
            .Select(WorkerAsync).ToArray();
    }

    public async ValueTask<GateReprocessingAdmissionResult> AdmitAsync(GateReprocessingJobDescriptor descriptor,
        CancellationToken cancellationToken = default) =>
        await AdmitCoreAsync(descriptor, null, cancellationToken).ConfigureAwait(false);

    ValueTask<GateReprocessingAdmissionResult> IOwnedGateReprocessingScheduler.AdmitOwnedAsync(
        GateReprocessingJobDescriptor descriptor, IGateReprocessingAdmissionOwner owner,
        CancellationToken cancellationToken) => AdmitCoreAsync(descriptor,
            owner ?? throw new ArgumentNullException(nameof(owner)), cancellationToken);

    private async ValueTask<GateReprocessingAdmissionResult> AdmitCoreAsync(GateReprocessingJobDescriptor descriptor,
        IGateReprocessingAdmissionOwner? admissionOwner, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        lock (_gate)
        {
            if (!_accepting) return new(GateReprocessingAdmissionStatus.Closed, errorCode: "scheduler-stopped");
            if (_jobs.Count + _admissions >= _options.MaximumAdmittedJobs)
                return new(GateReprocessingAdmissionStatus.CapacityExceeded, errorCode: "job-capacity");
            if (_attempts.Contains(descriptor.AttemptId))
                return new(GateReprocessingAdmissionStatus.Rejected, errorCode: "duplicate-attempt");
            if (_attempts.Count >= 65_536)
                return new(GateReprocessingAdmissionStatus.CapacityExceeded, errorCode: "attempt-ledger-capacity");
            _attempts.Add(descriptor.AttemptId); _admissions++;
        }
        IInspectionSealedRunSnapshot? snapshot = null;
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stop.Token);
            var request = new InspectionSealedRunSnapshotRequest(descriptor.InspectionId, descriptor.RunId,
                descriptor.SourceFinalRevision, descriptor.Selection.Select(x => x.Stream), descriptor.SnapshotPageSize);
            InspectionSealedRunSnapshotOpenResult opened = await _snapshots.OpenSealedRunSnapshotAsync(request,
                linked.Token).ConfigureAwait(false);
            if (opened.Status != InspectionSealedRunSnapshotOpenStatus.Available || opened.Snapshot is null)
            {
                GateReprocessingAdmissionStatus status = MapOpen(opened.Status);
                return new(status, errorCode: status == GateReprocessingAdmissionStatus.Cancelled
                    ? null : opened.ErrorCode ?? opened.Status.ToString());
            }
            snapshot = opened.Snapshot;
            ValidateSnapshotHeader(snapshot, request);
            int[] selectedByStream = CountSelected(snapshot, descriptor.Selection, linked.Token);
            long selected = selectedByStream.Aggregate(0L, static (sum, value) => checked(sum + value));
            var job = new Job(this, descriptor, snapshot, selected, selectedByStream,
                _timeProvider.GetUtcNow(), admissionOwner);
            lock (_gate)
            {
                if (!_accepting) return ClosedAndDispose(job);
                admissionOwner?.Bind(job);
                _jobs.Add(job); job.SetLifecycle(GateReprocessingLifecycle.Admitted);
            }
            snapshot = null;
            // Once inserted in _jobs the work is admitted and caller cancellation only cancels waiting APIs.
            await _ready.Writer.WriteAsync(job, CancellationToken.None).ConfigureAwait(false);
            return new(GateReprocessingAdmissionStatus.Admitted, job);
        }
        catch (OperationCanceledException)
        {
            IReadOnlyList<string> cleanup = DisposeAdmissionSnapshot(snapshot);
            bool stopped = _stop.IsCancellationRequested;
            return new(stopped ? GateReprocessingAdmissionStatus.Closed : GateReprocessingAdmissionStatus.Cancelled,
                errorCode: stopped ? "scheduler-stopped" : null,
                secondaryErrors: cleanup);
        }
        catch (GateReprocessingProviderException error)
        {
            IReadOnlyList<string> cleanup = DisposeAdmissionSnapshot(snapshot);
            return new(GateReprocessingAdmissionStatus.Faulted, errorCode: error.Code,
                secondaryErrors: cleanup);
        }
        catch (Exception error)
        {
            IReadOnlyList<string> cleanup = DisposeAdmissionSnapshot(snapshot);
            return new(GateReprocessingAdmissionStatus.Faulted, errorCode: error.GetType().Name,
                secondaryErrors: cleanup);
        }
        finally { ReleaseAdmission(); }
    }

    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        Task stop;
        lock (_gate) { stop = _stopTask ??= StopCoreAsync(); }
        return cancellationToken.CanBeCanceled ? stop.WaitAsync(cancellationToken) : stop;
    }

    public ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            Task stop = _stopTask ??= StopCoreAsync();
            return new(_disposeTask ??= DisposeCoreAsync(stop));
        }
    }

    private async Task DisposeCoreAsync(Task stop)
    {
        await stop.ConfigureAwait(false);
        try { _sampleBuffers.Dispose(); }
        finally { _stop.Dispose(); }
    }

    private async Task StopCoreAsync()
    {
        Task admissions;
        lock (_gate)
        {
            _accepting = false;
            admissions = _admissions == 0 ? Task.CompletedTask : (_admissionsDrained ??=
                new(TaskCreationOptions.RunContinuationsAsynchronously)).Task;
        }
        _stop.Cancel();
        await admissions.ConfigureAwait(false);
        Job[] jobs;
        lock (_gate) { jobs = _jobs.ToArray(); }
        foreach (Job job in jobs) job.Cancel();
        _ready.Writer.TryComplete();
        await Task.WhenAll(_workers).ConfigureAwait(false);
        await Task.WhenAll(jobs.Select(x => x.Completion)).ConfigureAwait(false);
    }

    private void ReleaseAdmission()
    {
        TaskCompletionSource? signal = null;
        lock (_gate) { _admissions--; if (_admissions == 0) signal = _admissionsDrained; }
        signal?.TrySetResult();
    }

    private async Task WorkerAsync(int workerIndex)
    {
        try
        {
            await foreach (Job job in _ready.Reader.ReadAllAsync().ConfigureAwait(false))
            {
                if (job.IsTerminal) continue;
                bool more = await ProcessOneBlockAsync(job).ConfigureAwait(false);
                if (more && !job.IsTerminal)
                {
                    await Task.Yield();
                    try { await _ready.Writer.WriteAsync(job).ConfigureAwait(false); }
                    catch (ChannelClosedException)
                    {
                        job.Cancel();
                        await ProcessOneBlockAsync(job).ConfigureAwait(false);
                    }
                }
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
    }

    private async Task<bool> ProcessOneBlockAsync(Job job)
    {
        job.WorkerActive(1);
        try
        {
            if (job.Cancellation.IsCancellationRequested)
            { FinishCancelled(job); return false; }
            job.SetLifecycle(GateReprocessingLifecycle.Running);
            int processed = 0;
            try
            {
                while (processed < job.Descriptor.BlockSize)
                {
                    if (job.Cancellation.IsCancellationRequested)
                    { FinishCancelled(job); return false; }
                    TraceIdentity? next = job.Reader.Next();
                    if (next is null)
                    {
                        if (processed != 0) job.RecordBlockCompleted();
                        await CompleteAsync(job).ConfigureAwait(false);
                        return false;
                    }
                    job.RecordEnumerated(next.Value);
                    processed++;
                    IdentityOutcome outcome = await ProcessIdentityAsync(job, next.Value).ConfigureAwait(false);
                    job.RecordIdentityTerminal(next.Value);
                    if (outcome.Cancelled) { FinishCancelled(job); return false; }
                    if (outcome.Error is not null) { Fault(job, outcome.Error); return false; }
                }
                job.RecordBlockCompleted();
                return true;
            }
            catch (OperationCanceledException) when (job.Cancellation.IsCancellationRequested)
            { FinishCancelled(job); return false; }
            catch (GateReprocessingProviderException error) { Fault(job, error.Code); return false; }
            catch (Exception error) { Fault(job, error.GetType().Name); return false; }
        }
        finally { job.WorkerActive(-1); }
    }

    private async Task<IdentityOutcome> ProcessIdentityAsync(Job job, TraceIdentity identity)
    {
        InspectionTraceReadResult read = await job.Snapshot.ReadAsync(identity,
            job.Cancellation.Token).ConfigureAwait(false);
        if (read.Status == InspectionReadStatus.Cancelled)
            return job.Cancellation.IsCancellationRequested ? IdentityOutcome.CancelledResult :
                new(false, "provider-fault:spurious-cancelled");
        if (read.Status is InspectionReadStatus.NotAvailable or InspectionReadStatus.Gap or InspectionReadStatus.OutOfRange)
            return new(false, $"coverage-mismatch:{read.Status}");
        if (read.Status != InspectionReadStatus.Available || read.Lease is null)
            return new(false, $"provider-fault:{read.Status}:{read.ErrorCode}");
        job.LeaseAcquired();
        GateEvaluationBatch? batch = null;
        IdentityOutcome evaluationOutcome = IdentityOutcome.Success;
        try
        {
            IInspectionTraceLease lease = read.Lease;
            TraceDescriptor descriptor = lease.Descriptor;
            if (descriptor.Identity != identity || descriptor.Identity.Stream.InspectionId != job.Descriptor.InspectionId ||
                descriptor.Identity.Stream.AcquisitionRunId != job.Descriptor.RunId ||
                read.Revision == InspectionRevision.Initial || read.Revision.Value > job.Descriptor.SourceFinalRevision.Value ||
                descriptor.SampleEncoding != UtSampleEncoding.SignedInt16 ||
                descriptor.TraceQuality is not (UtTraceQuality.Good or UtTraceQuality.Degraded or UtTraceQuality.Invalid))
                evaluationOutcome = new(false, "coverage-mismatch:descriptor-or-revision");
            else if (descriptor.SampleCount > GateReprocessingSampleBufferPool.BucketSamples)
                evaluationOutcome = new(false, "sample-buffer-capacity");
            else
            {
                ReadOnlySpan<short> samples;
                JobReprocessingBufferLease? sampleLease = null;
                try
                {
                    if (descriptor.TraceQuality == UtTraceQuality.Invalid)
                        samples = ReadOnlySpan<short>.Empty;
                    else
                    {
                        sampleLease = job.BufferPool.Rent();
                        if (lease.Samples.Count != descriptor.SampleCount)
                        { evaluationOutcome = new(false, "coverage-mismatch:sample-count"); samples = []; }
                        else
                        {
                            lease.Samples.CopyTo(sampleLease.Buffer);
                            if (job.Cancellation.IsCancellationRequested)
                            { evaluationOutcome = IdentityOutcome.CancelledResult; samples = []; }
                            else samples = sampleLease.Buffer.AsSpan(0, descriptor.SampleCount);
                        }
                    }
                    if (!evaluationOutcome.Cancelled && evaluationOutcome.Error is null)
                    {
                        if (job.Cancellation.IsCancellationRequested)
                            evaluationOutcome = IdentityOutcome.CancelledResult;
                        else
                        {
                            job.RecordRead(identity);
                            var confirmed = new ConfirmedGateTraceDescriptor(descriptor, identity,
                                read.Revision, descriptor.TraceQuality);
                            if (job.Cancellation.IsCancellationRequested)
                                evaluationOutcome = IdentityOutcome.CancelledResult;
                            else
                            {
                                if (job.Cancellation.IsCancellationRequested)
                                    evaluationOutcome = IdentityOutcome.CancelledResult;
                                else
                                {
                                    job.CallbackActive(1);
                                    GateEvaluationResult? evaluated = null;
                                    try
                                    {
                                        if (job.Cancellation.IsCancellationRequested)
                                            evaluationOutcome = IdentityOutcome.CancelledResult;
                                        else
                                            evaluated = job.Evaluator.Evaluate(job.Descriptor.Plan, confirmed,
                                                samples, job.Cancellation.Token);
                                    }
                                    finally { job.CallbackActive(-1); }
                                    if (!evaluationOutcome.Cancelled)
                                    {
                                        GateEvaluationResult completed = evaluated!;
                                        job.RecordEvaluated(identity);
                                        if (job.Cancellation.IsCancellationRequested ||
                                            completed.Status == GateEvaluationStatus.Cancelled)
                                        { job.RecordEvaluatedNotPublished(); evaluationOutcome = IdentityOutcome.CancelledResult; }
                                        else if (!completed.IsCompleted || completed.Batch is null)
                                            evaluationOutcome = new(false,
                                                completed.PrimaryError?.Code.ToString() ?? "evaluation-failed");
                                        else if (job.Cancellation.IsCancellationRequested)
                                        { job.RecordEvaluatedNotPublished(); evaluationOutcome = IdentityOutcome.CancelledResult; }
                                        else batch = completed.Batch;
                                    }
                                }
                            }
                        }
                    }
                }
                catch (Exception error)
                { evaluationOutcome = new(false, error.GetType().Name); }
                finally
                {
                    if (sampleLease is not null)
                    {
                        try { sampleLease.Dispose(); }
                        catch (Exception error)
                        {
                            string cleanup = $"buffer-cleanup-{error.GetType().Name}";
                            if (evaluationOutcome.Error is not null) job.RecordCleanup(cleanup);
                            else evaluationOutcome = new(false, cleanup);
                        }
                    }
                }
            }
        }
        catch (Exception error)
        { evaluationOutcome = new(false, error.GetType().Name); }
        finally
        {
            bool released = false;
            try { read.Lease.Dispose(); released = true; }
            catch (Exception error)
            {
                string cleanup = $"lease-cleanup-{error.GetType().Name}";
                if (evaluationOutcome.Error is not null)
                    job.RecordCleanup(cleanup);
                else if (evaluationOutcome.Cancelled)
                {
                    job.RecordCleanup("cancelled-before-lease-cleanup");
                    evaluationOutcome = new(false, cleanup);
                }
                else
                    evaluationOutcome = new(false, cleanup);
            }
            job.LeaseRelease(released);
        }

        if (evaluationOutcome.Cancelled || evaluationOutcome.Error is not null) return evaluationOutcome;
        // The store decides cancellation and writer admission atomically. The RF lease is already released.
        job.CallbackActive(1);
        job.CommitAttempt();
        GateDerivedMutationResult committed;
        try { committed = await _store.CommitAsync(batch!, job.Cancellation.Token).ConfigureAwait(false); }
        catch { job.CommitException(); throw; }
        finally { job.CallbackActive(-1); }
        job.CommitResult(committed);
        if (!committed.WasAccepted && committed.CancelledBeforeAdmission)
        {
            job.RecordCancelledBeforeCommit();
            return IdentityOutcome.CancelledResult;
        }
        if (committed.Status == GateDerivedMutationStatus.Committed) job.RecordCommitted();
        else if (committed.Status == GateDerivedMutationStatus.Duplicate) job.RecordDuplicate();
        else return new(false, $"derived-{committed.Status}:{committed.ErrorCode}");
        return IdentityOutcome.Success;
    }

    private async Task CompleteAsync(Job job)
    {
        if (!job.TryBeginCompleting()) { FinishCancelled(job); return; }
        job.Cancellation.Token.ThrowIfCancellationRequested();
        GateDerivedStreamCoverage[] streams = job.BuildCoverage(job.Cancellation.Token);
        string coverageScope = CoverageScope(job.Descriptor.Selection, job.Cancellation.Token);
        job.Cancellation.Token.ThrowIfCancellationRequested();
        if (job.DisposeSnapshotCapture() is { } cleanup) { Fault(job, cleanup); return; }
        var terminal = new GateProcessingTerminalCoverage(job.Descriptor.InspectionId, job.Descriptor.RunId,
            job.Descriptor.SourceFinalRevision, job.Descriptor.Plan.Source.Id, job.Descriptor.Plan.Source.Revision,
            job.Descriptor.Plan.Signature.Hex, job.Descriptor.Plan.Source.SchemaVersion,
            job.Descriptor.Plan.Source.AlgorithmVersion, streams, coverageScope);
        if (_store is not IGateProcessingTerminalCoverageSink terminalSink)
        { Fault(job, "terminal-coverage-unsupported"); return; }
        job.Cancellation.Token.ThrowIfCancellationRequested();
        job.CallbackActive(1);
        job.CoverageAttempt();
        GateDerivedMutationResult terminalResult;
        try
        {
            terminalResult = await terminalSink.ConfirmTerminalCoverageAsync(
            [terminal], job.Cancellation.Token).ConfigureAwait(false);
        }
        catch { job.CoverageException(); throw; }
        finally { job.CallbackActive(-1); }
        job.CoverageResult(terminalResult);
        if (!terminalResult.WasAccepted && terminalResult.CancelledBeforeAdmission)
        { FinishCancelled(job); return; }
        if (terminalResult.Status is not (GateDerivedMutationStatus.Committed or GateDerivedMutationStatus.Duplicate))
        { Fault(job, $"terminal-coverage-{terminalResult.Status}:{terminalResult.ErrorCode}"); return; }
        var manifest = new GateDerivedCoverageManifest(job.Descriptor.InspectionId, job.Descriptor.RunId,
            streams, job.Descriptor.SourceFinalRevision, job.Descriptor.Plan.Source.Id,
            job.Descriptor.Plan.Source.Revision, job.Descriptor.Plan.Signature.Hex,
            job.Descriptor.Plan.Source.SchemaVersion, job.Descriptor.Plan.Source.AlgorithmVersion,
            checked((int)job.Selected), _store.Revision, coverageScope);
        if (job.Descriptor.PublicationPolicy == GateReprocessingPublicationPolicy.StoreOnly &&
            job.AdmissionOwner is { } owner)
        {
            GateStoreOnlyCandidateDescriptor candidate = owner.MaterializeCandidate(manifest);
            job.SetStoreOnlyCandidate(candidate);
        }
        else if (job.Descriptor.PublicationPolicy == GateReprocessingPublicationPolicy.ActivateOnComplete)
        {
            job.Cancellation.Token.ThrowIfCancellationRequested();
            job.CallbackActive(1);
            job.ActivationAttempt();
            GateDerivedMutationResult activated;
            try
            {
                activated = await _store.ActivateAsync(manifest,
                job.Descriptor.DerivedViewId!.Value, job.Cancellation.Token).ConfigureAwait(false);
            }
            catch { job.ActivationException(); throw; }
            finally { job.CallbackActive(-1); }
            job.ActivationResult(activated);
            if (!activated.WasAccepted && activated.CancelledBeforeAdmission)
            { FinishCancelled(job); return; }
            if (activated.Status is not (GateDerivedMutationStatus.Activated or GateDerivedMutationStatus.Duplicate))
            { Fault(job, $"activation-{activated.Status}:{activated.ErrorCode}"); return; }
        }
        job.Complete(GateReprocessingLifecycle.Completed);
        Remove(job);
        await Task.CompletedTask;
    }


    private static string CoverageScope(IReadOnlyList<GateReprocessingStreamSelection> selection,
        CancellationToken cancellationToken)
    {
        using var payload = new MemoryStream();
        using (var writer = new BinaryWriter(payload, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write((ushort)1); writer.Write(selection.Count);
            foreach (GateReprocessingStreamSelection stream in selection)
            {
                cancellationToken.ThrowIfCancellationRequested();
                writer.Write(stream.Stream.InspectionId.Value.ToByteArray());
                writer.Write(stream.Stream.AcquisitionRunId.Value.ToByteArray());
                writer.Write(stream.Stream.SourceId.Value); writer.Write(stream.Stream.PhysicalChannelId.Value);
                writer.Write(stream.Stream.BeamId?.Value ?? -1); writer.Write(stream.Stream.FocalLawId?.Value ?? -1);
                writer.Write(stream.Ranges.Count);
                foreach (AcquisitionIndexRange range in stream.Ranges)
                { cancellationToken.ThrowIfCancellationRequested(); writer.Write(range.FirstInclusive); writer.Write(range.LastInclusive); }
            }
        }
        return Convert.ToHexString(SHA256.HashData(payload.GetBuffer().AsSpan(0, checked((int)payload.Length))));
    }

    private static int[] CountSelected(IInspectionSealedRunSnapshot snapshot,
        IReadOnlyList<GateReprocessingStreamSelection> selections, CancellationToken token)
    {
        var counts = new int[selections.Count];
        for (int i = 0; i < selections.Count; i++)
        {
            token.ThrowIfCancellationRequested();
            GateReprocessingStreamSelection selection = selections[i];
            var query = new InspectionSealedRunRangeQuery(selection.Stream,
                selection.Ranges.Select(range => new InspectionSealedRunIndexRange(
                    range.FirstInclusive, range.LastInclusive)), snapshot.MaximumPageSize);
            counts[i] = checked((int)snapshot.CountIdentities(query));
        }
        return counts;
    }

    private static void ValidateSnapshotHeader(IInspectionSealedRunSnapshot snapshot,
        InspectionSealedRunSnapshotRequest request)
    {
        if (snapshot.ContractVersion != IInspectionSealedRunSnapshot.CurrentContractVersion ||
            snapshot.InspectionId != request.InspectionId || snapshot.RunId != request.RunId ||
            snapshot.SourceFinalRevision != request.SourceFinalRevision ||
            snapshot.RunState != InspectionRunState.Sealed || snapshot.MaximumPageSize < request.MaximumPageSize)
            throw new GateReprocessingProviderException("snapshot-header-mismatch");
        UtStreamIdentity[] actual = snapshot.Streams.Select(x => x.Stream)
            .OrderBy(InspectionSealedRunSnapshotRequest.StreamSortKey, StringComparer.Ordinal).ToArray();
        if (!actual.SequenceEqual(request.Streams))
            throw new GateReprocessingProviderException("snapshot-header-mismatch");
    }

    private void FinishCancelled(Job job)
    {
        job.SetLifecycle(GateReprocessingLifecycle.Cancelling);
        if (job.DisposeSnapshotCapture() is { } cleanup) { Fault(job, cleanup); return; }
        job.Complete(GateReprocessingLifecycle.Cancelled); Remove(job);
    }

    private void Fault(Job job, string error)
    {
        job.RecordPrimary(error);
        if (job.DisposeSnapshotCapture() is { } cleanup) job.RecordCleanup(cleanup);
        job.Complete(GateReprocessingLifecycle.Faulted); Remove(job);
    }

    private void Remove(Job job)
    {
        lock (_gate) { _jobs.Remove(job); }
    }

    private static GateReprocessingAdmissionStatus MapOpen(InspectionSealedRunSnapshotOpenStatus status) => status switch
    {
        InspectionSealedRunSnapshotOpenStatus.Cancelled => GateReprocessingAdmissionStatus.Cancelled,
        InspectionSealedRunSnapshotOpenStatus.Closed => GateReprocessingAdmissionStatus.Closed,
        InspectionSealedRunSnapshotOpenStatus.Faulted => GateReprocessingAdmissionStatus.Faulted,
        _ => GateReprocessingAdmissionStatus.Rejected
    };

    private GateReprocessingAdmissionResult ClosedAndDispose(Job job)
    {
        string? cleanup = job.DisposeSnapshotCapture();
        return new(GateReprocessingAdmissionStatus.Closed, errorCode: "scheduler-stopped",
            secondaryErrors: cleanup is null ? null : [cleanup]);
    }

    private static IReadOnlyList<string> DisposeAdmissionSnapshot(IInspectionSealedRunSnapshot? snapshot)
    {
        if (snapshot is null) return Array.Empty<string>();
        try { snapshot.Dispose(); return Array.Empty<string>(); }
        catch (Exception error) { return [$"snapshot-cleanup-{error.GetType().Name}"]; }
    }

    private sealed class SelectedReader
    {
        private readonly IInspectionSealedRunSnapshot _snapshot;
        private readonly IReadOnlyList<GateReprocessingStreamSelection> _selection;
        private readonly InspectionSealedRunRangeQuery[] _queries;
        private int _streamIndex;
        private readonly ulong?[] _lastIndices;
        private InspectionSealedRunCursor? _cursor;
        private IReadOnlyList<TraceIdentity> _page = [];
        private int _pageIndex;
        internal SelectedReader(IInspectionSealedRunSnapshot snapshot,
            IReadOnlyList<GateReprocessingStreamSelection> selection)
        {
            _snapshot = snapshot; _selection = selection;
            _lastIndices = new ulong?[selection.Count];
            _queries = selection.Select(item => new InspectionSealedRunRangeQuery(item.Stream,
                item.Ranges.Select(range => new InspectionSealedRunIndexRange(
                    range.FirstInclusive, range.LastInclusive)), snapshot.MaximumPageSize)).ToArray();
        }
        internal TraceIdentity? Next()
        {
            while (_streamIndex < _selection.Count)
            {
                while (_pageIndex < _page.Count)
                {
                    TraceIdentity identity = _page[_pageIndex++];
                    GateReprocessingStreamSelection expected = _selection[_streamIndex];
                    if (identity.Stream != expected.Stream)
                        throw new GateReprocessingProviderException("coverage-mismatch:enumerated-identity");
                    if (_lastIndices[_streamIndex] is ulong last && identity.AcquisitionIndex <= last)
                        throw new GateReprocessingProviderException("coverage-mismatch:duplicate-or-order");
                    _lastIndices[_streamIndex] = identity.AcquisitionIndex;
                    return identity;
                }
                if (_page.Count != 0 && _cursor is null)
                { _streamIndex++; _page = []; _pageIndex = 0; continue; }
                InspectionSealedRunIdentityPage next = _snapshot.ReadIdentities(_queries[_streamIndex], _cursor);
                _page = next.Identities; _pageIndex = 0; _cursor = next.NextCursor;
                if (_page.Count == 0 && _cursor is null)
                { _streamIndex++; _page = []; continue; }
            }
            return null;
        }

    }

    private readonly record struct IdentityOutcome(bool Cancelled, string? Error)
    {
        internal static IdentityOutcome Success => new(false, null);
        internal static IdentityOutcome CancelledResult => new(true, null);
    }

    private sealed class GateReprocessingProviderException(string code) : Exception(code)
    { internal string Code { get; } = code; }

    private sealed class Job : IGateReprocessingJob
    {
        private readonly object _gate = new();
        private readonly GateReprocessingScheduler _owner;
        private readonly TaskCompletionSource<GateReprocessingProgressSnapshot> _completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private GateReprocessingLifecycle _lifecycle = GateReprocessingLifecycle.Created;
        private long _read, _evaluated, _committed, _duplicates, _notPublished, _cancelledBeforeCommit;
        private int _blocksCompleted, _activeLeases, _peakLeases;
        private int _activeWorkers, _peakWorkers, _activeCallbacks, _peakCallbacks;
        private long _leasesAcquired, _leasesReleased;
        private long _leaseReleaseFailed;
        private long _snapshotReleaseFailed;
        private InspectionSealedRunEnumerationDiagnostics _enumerationDiagnostics;
        private readonly CoverageAccumulator[] _coverage;
        private readonly StreamProgressCounter[] _streamProgress;
        private readonly Dictionary<UtStreamIdentity, int> _coverageOrdinals;
        private string? _primary;
        private readonly List<string> _secondary = [];
        private DateTimeOffset _updated;
        private int _snapshotDisposed;
        private int _coverageDisposed;
        private readonly OperationCounter _commitProgress = new();
        private readonly OperationCounter _coverageProgress = new();
        private readonly OperationCounter _activationProgress = new();
        private GateStoreOnlyCandidateDescriptor? _storeOnlyCandidate;
        internal Job(GateReprocessingScheduler owner, GateReprocessingJobDescriptor descriptor,
            IInspectionSealedRunSnapshot snapshot, long selected, int[] selectedByStream, DateTimeOffset created,
            IGateReprocessingAdmissionOwner? admissionOwner)
        {
            _owner = owner; Descriptor = descriptor; Snapshot = snapshot; Selected = selected;
            Reader = new(snapshot, descriptor.Selection); Created = created; _updated = created;
            _coverage = descriptor.Selection.Select((selection, index) =>
                new CoverageAccumulator(selection, selectedByStream[index])).ToArray();
            _streamProgress = descriptor.Selection.Select((selection, index) =>
                new StreamProgressCounter(selection.Stream, selectedByStream[index])).ToArray();
            _coverageOrdinals = descriptor.Selection.Select((selection, index) => (selection.Stream, index))
                .ToDictionary(static item => item.Stream, static item => item.index);
            ScratchPool = new JobGateScratchPool(owner._scratchPool);
            Evaluator = new GateEvaluator(ScratchPool);
            BufferPool = new JobReprocessingBufferPool(owner._sampleBuffers);
            AdmissionOwner = admissionOwner;
        }
        internal GateReprocessingJobDescriptor Descriptor { get; }
        internal IInspectionSealedRunSnapshot Snapshot { get; }
        internal SelectedReader Reader { get; }
        internal long Selected { get; }
        internal DateTimeOffset Created { get; }
        internal CancellationTokenSource Cancellation { get; } = new();
        internal JobGateScratchPool ScratchPool { get; }
        internal GateEvaluator Evaluator { get; }
        internal JobReprocessingBufferPool BufferPool { get; }
        internal IGateReprocessingAdmissionOwner? AdmissionOwner { get; }
        internal bool IsTerminal { get { lock (_gate) return _lifecycle is GateReprocessingLifecycle.Completed or GateReprocessingLifecycle.Cancelled or GateReprocessingLifecycle.Faulted; } }
        public GateReprocessingProgressSnapshot Progress { get { lock (_gate) return SnapshotProgress(); } }
        public Task<GateReprocessingProgressSnapshot> Completion => _completion.Task;
        public void Cancel()
        {
            bool cancel = false;
            lock (_gate)
            {
                if (IsTerminal) return;
                _lifecycle = GateReprocessingLifecycle.Cancelling; Touch(); cancel = true;
            }
            if (cancel) Cancellation.Cancel();
        }
        internal bool TryBeginCompleting()
        {
            lock (_gate)
            {
                if (IsTerminal || _lifecycle == GateReprocessingLifecycle.Cancelling || Cancellation.IsCancellationRequested)
                    return false;
                _lifecycle = GateReprocessingLifecycle.Completing; Touch(); return true;
            }
        }
        internal void SetLifecycle(GateReprocessingLifecycle value)
        {
            lock (_gate)
            {
                if (IsTerminal || _lifecycle == GateReprocessingLifecycle.Cancelling) return;
                if ((int)value < (int)_lifecycle) return;
                _lifecycle = value; Touch();
            }
        }
        internal void RecordRead(TraceIdentity identity)
        {
            lock (_gate)
            {
                _read++;
                _streamProgress[_coverageOrdinals[identity.Stream]].Read++; Touch();
            }
        }
        internal void RecordEvaluated(TraceIdentity identity)
        {
            lock (_gate)
            {
                _evaluated++;
                _streamProgress[_coverageOrdinals[identity.Stream]].Evaluated++; Touch();
            }
        }
        internal void RecordCommitted() { lock (_gate) { _committed++; Touch(); } }
        internal void RecordDuplicate() { lock (_gate) { _duplicates++; Touch(); } }
        internal void RecordEvaluatedNotPublished() { lock (_gate) { _notPublished++; Touch(); } }
        internal void RecordCancelledBeforeCommit() { lock (_gate) { _cancelledBeforeCommit++; Touch(); } }
        internal void RecordBlockCompleted() { lock (_gate) { _blocksCompleted++; Touch(); } }
        internal void RecordEnumerated(TraceIdentity identity)
        {
            if (!_coverageOrdinals.TryGetValue(identity.Stream, out int ordinal))
                throw new GateReprocessingProviderException("coverage-mismatch:enumerated-stream");
            _coverage[ordinal].Append(identity);
            lock (_gate)
            {
                _streamProgress[ordinal].Visited++;
                _streamProgress[ordinal].LastVisited = identity.AcquisitionIndex;
            }
        }
        internal void RecordIdentityTerminal(TraceIdentity identity)
        { lock (_gate) { _streamProgress[_coverageOrdinals[identity.Stream]].Terminal++; Touch(); } }
        internal void CommitAttempt() { lock (_gate) _commitProgress.Attempt(); }
        internal void CommitResult(GateDerivedMutationResult result) { lock (_gate) _commitProgress.Result(result); }
        internal void CommitException() { lock (_gate) _commitProgress.Exception(); }
        internal void CoverageAttempt() { lock (_gate) _coverageProgress.Attempt(); }
        internal void CoverageResult(GateDerivedMutationResult result) { lock (_gate) _coverageProgress.Result(result); }
        internal void CoverageException() { lock (_gate) _coverageProgress.Exception(); }
        internal void ActivationAttempt() { lock (_gate) _activationProgress.Attempt(); }
        internal void ActivationResult(GateDerivedMutationResult result) { lock (_gate) _activationProgress.Result(result); }
        internal void ActivationException() { lock (_gate) _activationProgress.Exception(); }
        internal void SetStoreOnlyCandidate(GateStoreOnlyCandidateDescriptor candidate)
        {
            ArgumentNullException.ThrowIfNull(candidate);
            lock (_gate)
            {
                if (_lifecycle != GateReprocessingLifecycle.Completing || _storeOnlyCandidate is not null)
                    throw new InvalidOperationException("The StoreOnly candidate cannot be installed in this state.");
                _storeOnlyCandidate = candidate;
            }
        }
        internal GateDerivedStreamCoverage[] BuildCoverage(CancellationToken cancellationToken)
        {
            var result = new GateDerivedStreamCoverage[_coverage.Length];
            for (int i = 0; i < result.Length; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                result[i] = _coverage[i].Complete();
            }
            return result;
        }
        internal void LeaseAcquired()
        { lock (_gate) { _activeLeases++; _leasesAcquired++; _peakLeases = Math.Max(_peakLeases, _activeLeases); Touch(); } }
        internal void LeaseRelease(bool succeeded)
        { lock (_gate) { if (succeeded) { _activeLeases--; _leasesReleased++; } else _leaseReleaseFailed++; Touch(); } }
        internal void WorkerActive(int delta)
        {
            lock (_gate)
            {
                _activeWorkers += delta;
                if (_activeWorkers < 0) throw new InvalidOperationException("Worker accounting underflow.");
                _peakWorkers = Math.Max(_peakWorkers, _activeWorkers); Touch();
                if (_activeWorkers == 0 && IsTerminal) CompleteUnderLock();
            }
        }
        internal void CallbackActive(int delta)
        {
            lock (_gate)
            {
                _activeCallbacks += delta;
                _peakCallbacks = Math.Max(_peakCallbacks, _activeCallbacks); Touch();
            }
        }
        internal void RecordCleanup(string error) { lock (_gate) { _secondary.Add(error); Touch(); } }
        internal void RecordPrimary(string error) { lock (_gate) { _primary ??= error; Touch(); } }
        internal void Complete(GateReprocessingLifecycle lifecycle)
        {
            lock (_gate)
            {
                _lifecycle = lifecycle; Touch(); if (_activeWorkers == 0) CompleteUnderLock();
            }
        }
        internal void DisposeSnapshot() { if (Interlocked.Exchange(ref _snapshotDisposed, 1) == 0) Snapshot.Dispose(); }
        internal string? DisposeSnapshotCapture()
        {
            string? failure = null;
            if (Volatile.Read(ref _snapshotDisposed) == 0)
            {
                try { _enumerationDiagnostics = Snapshot.EnumerationDiagnostics; }
                catch (Exception error) { failure = $"snapshot-diagnostics-{error.GetType().Name}"; }
                try { DisposeSnapshot(); }
                catch (Exception error)
                {
                    _snapshotReleaseFailed++;
                    string detail = $"snapshot-cleanup-{error.GetType().Name}";
                    if (failure is null) failure = detail;
                    else RecordCleanup(detail);
                }
            }
            if (Interlocked.Exchange(ref _coverageDisposed, 1) == 0)
            {
                foreach (CoverageAccumulator accumulator in _coverage)
                {
                    try { accumulator.Dispose(); }
                    catch (Exception error)
                    {
                        string detail = $"coverage-cleanup-{error.GetType().Name}";
                        if (failure is null) failure = detail;
                        else RecordCleanup(detail);
                    }
                }
            }
            return failure;
        }
        private void CompleteUnderLock() { _completion.TrySetResult(SnapshotProgress()); }
        private void Touch() => _updated = _owner._timeProvider.GetUtcNow();
        private GateReprocessingProgressSnapshot SnapshotProgress()
        {
            GateReprocessingSampleBufferDiagnostics buffers = _owner._sampleBuffers.Diagnostics;
            JobGateScratchDiagnostics scratch = ScratchPool.JobSnapshot;
            JobReprocessingBufferDiagnostics jobBuffers = BufferPool.Diagnostics;
            GateReprocessingStreamProgress[] streams = _streamProgress.Select(x => x.Snapshot()).ToArray();
            InspectionSealedRunEnumerationDiagnostics enumeration = _snapshotDisposed == 0
                ? Snapshot.EnumerationDiagnostics : _enumerationDiagnostics;
            return new(Descriptor.JobId, Descriptor.AttemptId,
                _lifecycle, Descriptor.SourceFinalRevision, Descriptor.Plan.Source.Revision, Descriptor.PublicationPolicy,
                Selected, _read, _evaluated, _committed, _duplicates, _notPublished,
                checked((int)((Selected + Descriptor.BlockSize - 1) / Descriptor.BlockSize)), _blocksCompleted,
                _activeLeases, scratch.ActiveRentals, _primary, _secondary, Created, _updated,
                Descriptor.Selection, enumeration.PagesRead, enumeration.ContinuationSearches,
                streams.Sum(x => x.Visited), _cancelledBeforeCommit, _peakLeases,
                jobBuffers.ActiveBuffers, jobBuffers.PeakActiveBuffers, jobBuffers.ReservedBytes,
                jobBuffers.OwnedBytes, jobBuffers.PeakOwnedBytes, 0,
                jobBuffers.RentSucceeded, jobBuffers.ReturnSucceeded,
                scratch.RentSucceeded, scratch.ReturnSucceeded,
                scratch.PeakActiveRentals,
                _activeWorkers, _peakWorkers, _activeCallbacks, _peakCallbacks,
                _leasesAcquired, _leasesReleased, enumeration.EntriesReturned,
                enumeration.ContinuationSearchComparisons, enumeration.NormalizedRangeCount,
                enumeration.LowerBoundSearches, enumeration.UpperBoundSearches,
                enumeration.CursorResumes, enumeration.PrefixRescans, 0,
                _leaseReleaseFailed == 0 && jobBuffers.ReturnFailed == 0 && scratch.ReturnFailed == 0 &&
                    _snapshotReleaseFailed == 0,
                _leaseReleaseFailed, jobBuffers.ReturnFailed, scratch.ReturnFailed, _snapshotReleaseFailed,
                new(Descriptor.Plan.Source.Id, Descriptor.Plan.Source.Revision, Descriptor.Plan.Signature,
                    Descriptor.Plan.Source.SchemaVersion, Descriptor.Plan.Source.AlgorithmVersion),
                streams, _commitProgress.Snapshot(), _coverageProgress.Snapshot(), _activationProgress.Snapshot(),
                new(jobBuffers.RentAttempted, jobBuffers.RentSucceeded, jobBuffers.RentRejected,
                    jobBuffers.ReturnAttempted, jobBuffers.ReturnSucceeded, jobBuffers.ReturnFailed,
                    jobBuffers.ActiveBuffers, jobBuffers.PeakActiveBuffers, jobBuffers.OwnedBytes, jobBuffers.PeakOwnedBytes),
                new(scratch.RentAttempted, scratch.RentSucceeded, scratch.RentRejected,
                    scratch.ReturnAttempted, scratch.ReturnSucceeded, scratch.ReturnFailed,
                    scratch.ActiveRentals, scratch.PeakActiveRentals, scratch.OwnedElements, scratch.PeakOwnedElements),
                new(_leasesAcquired, _leasesAcquired, 0, _leasesReleased + _leaseReleaseFailed,
                    _leasesReleased, _leaseReleaseFailed, _activeLeases, _peakLeases),
                _storeOnlyCandidate);
        }
    }

    private sealed class StreamProgressCounter(UtStreamIdentity stream, long selected)
    {
        internal UtStreamIdentity Stream { get; } = stream;
        internal long Selected { get; } = selected;
        internal long Visited { get; set; }
        internal long Read { get; set; }
        internal long Evaluated { get; set; }
        internal long Terminal { get; set; }
        internal ulong? LastVisited { get; set; }
        internal GateReprocessingStreamProgress Snapshot() =>
            new(Stream, Selected, Visited, Read, Evaluated, Terminal, LastVisited);
    }

    private sealed class OperationCounter
    {
        private long _attempts, _admitted, _completed, _rejected, _succeeded, _duplicates, _failed;
        internal void Attempt() => _attempts++;
        internal void Result(GateDerivedMutationResult result)
        {
            if (!result.WasAccepted) { _rejected++; return; }
            _admitted++; _completed++;
            if (result.Status is GateDerivedMutationStatus.Committed or GateDerivedMutationStatus.Activated)
                _succeeded++;
            else if (result.Status == GateDerivedMutationStatus.Duplicate) _duplicates++;
            else _failed++;
        }
        internal void Exception() { _admitted++; _completed++; _failed++; }
        internal GateReprocessingOperationProgress Snapshot() =>
            new(_attempts, _admitted, _completed, _rejected, _succeeded, _duplicates, _failed);
    }

    private sealed class CoverageAccumulator : IDisposable
    {
        private readonly GateReprocessingStreamSelection _selection;
        private readonly int _expected;
        private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        private readonly byte[] _identityPrefix;
        private int _observed;
        private ulong? _minimum, _maximum;

        internal CoverageAccumulator(GateReprocessingStreamSelection selection, int expected)
        {
            _selection = selection; _expected = expected;
            Span<byte> header = stackalloc byte[6];
            System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(header,
                GateDerivedStreamCoverage.DigestCanonicalVersion);
            System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(header[2..], expected);
            _hash.AppendData(header);
            byte[] source = Encoding.UTF8.GetBytes(selection.Stream.SourceId.Value);
            _identityPrefix = new byte[48 + source.Length];
            selection.Stream.InspectionId.Value.TryWriteBytes(_identityPrefix.AsSpan(0, 16));
            selection.Stream.AcquisitionRunId.Value.TryWriteBytes(_identityPrefix.AsSpan(16, 16));
            System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(_identityPrefix.AsSpan(32, 4), source.Length);
            source.CopyTo(_identityPrefix.AsSpan(36));
            int offset = 36 + source.Length;
            System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(_identityPrefix.AsSpan(offset, 4), selection.Stream.PhysicalChannelId.Value);
            System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(_identityPrefix.AsSpan(offset + 4, 4), selection.Stream.BeamId?.Value ?? -1);
            System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(_identityPrefix.AsSpan(offset + 8, 4), selection.Stream.FocalLawId?.Value ?? -1);
        }

        internal void Append(TraceIdentity identity)
        {
            if (identity.Stream != _selection.Stream ||
                (_maximum is ulong previous && identity.AcquisitionIndex <= previous))
                throw new GateReprocessingProviderException("coverage-mismatch:accumulator-order");
            Span<byte> acquisitionIndex = stackalloc byte[8];
            System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(acquisitionIndex, identity.AcquisitionIndex);
            _hash.AppendData(_identityPrefix); _hash.AppendData(acquisitionIndex); _observed = checked(_observed + 1);
            _minimum ??= identity.AcquisitionIndex; _maximum = identity.AcquisitionIndex;
        }

        internal GateDerivedStreamCoverage Complete()
        {
            if (_observed != _expected)
                throw new GateReprocessingProviderException("coverage-mismatch:observed-count");
            return new(_selection.Stream, _minimum, _maximum, _observed,
                new GateSha256Digest(_hash.GetHashAndReset()), _selection.Ranges);
        }
        public void Dispose() => _hash.Dispose();
    }
}

internal readonly record struct GateReprocessingSampleBufferDiagnostics(
    long CapacityBytes, long ReservedBytes, long OwnedBytes, long PeakOwnedBytes,
    int ActiveBuffers, int PeakBuffers, long Rents, long Returns, long Rejections,
    long AllocatedBytes);

internal readonly record struct JobReprocessingBufferDiagnostics(
    long RentAttempted, long RentSucceeded, long RentRejected,
    long ReturnAttempted, long ReturnSucceeded, long ReturnFailed,
    int ActiveBuffers, int PeakActiveBuffers, long ReservedBytes,
    long OwnedBytes, long PeakOwnedBytes);

internal sealed class JobReprocessingBufferPool
{
    private readonly object _gate = new();
    private readonly GateReprocessingSampleBufferPool _shared;
    private readonly HashSet<JobReprocessingBufferLease> _active = new(ReferenceEqualityComparer.Instance);
    private long _rentAttempted, _rentSucceeded, _rentRejected;
    private long _returnAttempted, _returnSucceeded, _returnFailed, _ownedBytes, _peakOwnedBytes;
    private int _peak;
    internal JobReprocessingBufferPool(GateReprocessingSampleBufferPool shared) => _shared = shared;
    internal JobReprocessingBufferLease Rent()
    {
        lock (_gate) _rentAttempted++;
        GateReprocessingSampleBufferLease physical;
        try { physical = _shared.Rent(); }
        catch { lock (_gate) _rentRejected++; throw; }
        var lease = new JobReprocessingBufferLease(this, physical);
        lock (_gate)
        {
            _active.Add(lease); _rentSucceeded++; _ownedBytes += GateReprocessingSampleBufferPool.BucketBytes;
            _peakOwnedBytes = Math.Max(_peakOwnedBytes, _ownedBytes); _peak = Math.Max(_peak, _active.Count);
        }
        return lease;
    }
    internal void Return(JobReprocessingBufferLease lease)
    {
        lock (_gate)
        {
            _returnAttempted++;
            if (!_active.Contains(lease))
            { _returnFailed++; throw new InvalidOperationException("The job buffer lease is foreign or already returned."); }
        }
        try { lease.Physical.Dispose(); }
        catch { lock (_gate) _returnFailed++; throw; }
        lock (_gate)
        {
            _active.Remove(lease); _returnSucceeded++; _ownedBytes -= GateReprocessingSampleBufferPool.BucketBytes;
        }
    }
    internal JobReprocessingBufferDiagnostics Diagnostics
    {
        get
        {
            lock (_gate) return new(_rentAttempted, _rentSucceeded, _rentRejected,
            _returnAttempted, _returnSucceeded, _returnFailed, _active.Count, _peak, 0,
            _ownedBytes, _peakOwnedBytes);
        }
    }
}

internal sealed class JobReprocessingBufferLease : IDisposable
{
    private JobReprocessingBufferPool? _owner;
    internal JobReprocessingBufferLease(JobReprocessingBufferPool owner, GateReprocessingSampleBufferLease physical)
    { _owner = owner; Physical = physical; }
    internal GateReprocessingSampleBufferLease Physical { get; }
    internal short[] Buffer => Physical.Buffer;
    public void Dispose()
    {
        JobReprocessingBufferPool owner = Interlocked.Exchange(ref _owner, null) ??
            throw new InvalidOperationException("The job buffer lease was already returned.");
        owner.Return(this);
    }
}

internal sealed class GateReprocessingSampleBufferPool : IDisposable
{
    internal const int BucketSamples = 65_536;
    internal const int BucketBytes = BucketSamples * sizeof(short);
    private readonly object _gate = new();
    private readonly long _capacityBytes;
    private readonly Stack<short[]> _available = [];
    private readonly HashSet<short[]> _active = new(ReferenceEqualityComparer.Instance);
    private long _allocatedBytes;
    private long _reservedBytes, _ownedBytes, _peakOwnedBytes, _rents, _returns, _rejections;
    private int _activeBuffers, _peakBuffers;

    internal GateReprocessingSampleBufferPool(long capacityBytes)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacityBytes, BucketBytes);
        _capacityBytes = capacityBytes;
    }

    internal GateReprocessingSampleBufferDiagnostics Diagnostics
    {
        get
        {
            lock (_gate) return new(_capacityBytes, _reservedBytes, _ownedBytes, _peakOwnedBytes,
            _activeBuffers, _peakBuffers, _rents, _returns, _rejections, _allocatedBytes);
        }
    }

    internal GateReprocessingSampleBufferLease Rent()
    {
        lock (_gate)
        {
            if (_available.TryPop(out short[]? reused))
            {
                _ownedBytes += BucketBytes; _rents++; _activeBuffers++;
                _active.Add(reused); _peakBuffers = Math.Max(_peakBuffers, _activeBuffers);
                _peakOwnedBytes = Math.Max(_peakOwnedBytes, _ownedBytes);
                return new(this, reused);
            }
            if (BucketBytes > _capacityBytes - _allocatedBytes)
            { _rejections++; throw new InvalidOperationException("The historical sample-buffer budget is exhausted."); }
            _reservedBytes += BucketBytes; _allocatedBytes += BucketBytes;
        }
        short[] buffer;
        try { buffer = new short[BucketSamples]; }
        catch
        {
            lock (_gate) { _reservedBytes -= BucketBytes; _allocatedBytes -= BucketBytes; }
            throw;
        }
        lock (_gate)
        {
            _reservedBytes -= BucketBytes; _ownedBytes += BucketBytes; _rents++;
            _activeBuffers++; _peakBuffers = Math.Max(_peakBuffers, _activeBuffers);
            _peakOwnedBytes = Math.Max(_peakOwnedBytes, _ownedBytes);
            _active.Add(buffer);
        }
        return new(this, buffer);
    }

    internal void Return(short[] buffer)
    {
        lock (_gate)
        {
            if (!_active.Remove(buffer) || _activeBuffers <= 0 || _ownedBytes < BucketBytes)
                throw new InvalidOperationException("The sample buffer was returned more than once.");
            _activeBuffers--; _ownedBytes -= BucketBytes; _returns++; _available.Push(buffer);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_activeBuffers != 0) throw new InvalidOperationException("Active sample buffers remain.");
            _available.Clear(); _allocatedBytes = 0;
        }
    }
}

internal sealed class GateReprocessingSampleBufferLease : IDisposable
{
    private GateReprocessingSampleBufferPool? _owner;
    internal GateReprocessingSampleBufferLease(GateReprocessingSampleBufferPool owner, short[] buffer)
    { _owner = owner; Buffer = buffer; }
    internal short[] Buffer { get; }
    public void Dispose()
    {
        GateReprocessingSampleBufferPool owner = Interlocked.Exchange(ref _owner, null) ??
            throw new InvalidOperationException("The sample buffer lease was already returned.");
        owner.Return(Buffer);
    }
}
