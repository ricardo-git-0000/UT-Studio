using UTStudio.Contracts.Inspection;
using UTStudio.Domain.Acquisition;

namespace UTStudio.Visualization.Core;

public readonly record struct AScanSelectionResult(
    BScanTraceResultKind Kind, AScanSnapshot? Snapshot = null, string? Error = null);

/// <summary>Resolves one authoritative identity into an ownership-free visual snapshot.</summary>
public interface IAuthoritativeAScanSelector
{
    ValueTask<AScanSelectionResult> ResolveAsync(TraceIdentity identity, ulong visualVersion,
        CancellationToken cancellationToken = default);
}

/// <summary>Owns the trace lease only while forming the independent A-Scan snapshot.</summary>
public sealed class UtTraceAScanSelector : IAuthoritativeAScanSelector
{
    private readonly IUtTraceProvider _provider;
    private readonly IAScanProjector _projector;

    public UtTraceAScanSelector(IUtTraceProvider provider, IAScanProjector? projector = null)
    {
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
        _projector = projector ?? new AScanProjector();
    }

    public async ValueTask<AScanSelectionResult> ResolveAsync(TraceIdentity identity, ulong visualVersion,
        CancellationToken cancellationToken = default)
    {
        if (!identity.IsValid) { throw new ArgumentException("A valid trace identity is required.", nameof(identity)); }
        InspectionTraceReadResult result;
        try { result = await _provider.ReadAsync(identity, cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        { return new(BScanTraceResultKind.Cancelled); }
        catch (Exception error) { return Faulted(error.Message); }

        if (result.Status != InspectionReadStatus.Available || result.Lease is null)
        {
            result.Lease?.Dispose();
            return new(result.Status switch
            {
                InspectionReadStatus.NotAvailable => BScanTraceResultKind.NotAvailable,
                InspectionReadStatus.Gap => BScanTraceResultKind.Gap,
                InspectionReadStatus.OutOfRange => BScanTraceResultKind.OutOfRange,
                InspectionReadStatus.Cancelled => BScanTraceResultKind.Cancelled,
                InspectionReadStatus.Closed => BScanTraceResultKind.Closed,
                _ => BScanTraceResultKind.Faulted
            }, Error: result.ErrorCode);
        }

        using (result.Lease)
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                TraceDescriptor descriptor = result.Lease.Descriptor;
                if (descriptor.Identity != identity || descriptor.SampleCount != result.Lease.Samples.Count)
                { return Faulted("The trace provider returned incompatible identity or sample metadata."); }
                var samples = new short[descriptor.SampleCount];
                result.Lease.Samples.CopyTo(samples);
                cancellationToken.ThrowIfCancellationRequested();
                var configuration = new ConventionalAcquisitionConfiguration(
                    identity.Stream.PhysicalChannelId, descriptor.SampleCount, descriptor.SampleRateHz,
                    descriptor.FirstSampleOffsetSeconds, descriptor.SignalMode);
                DateTimeOffset startedAt = descriptor.TimestampUtc is { } timestamp
                    ? timestamp - descriptor.ElapsedSinceRunStart : DateTimeOffset.UnixEpoch;
                var metadata = new ConventionalUtFrameMetadata(identity.Stream.SourceId,
                    identity.Stream.AcquisitionRunId, configuration, startedAt);
                AScanSnapshot snapshot = _projector.Project(metadata, descriptor.Sequence,
                    descriptor.ElapsedSinceRunStart, samples, visualVersion);
                snapshot.SetAuthority(identity, result.Revision);
                cancellationToken.ThrowIfCancellationRequested();
                return new(BScanTraceResultKind.Available, snapshot);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            { return new(BScanTraceResultKind.Cancelled); }
            catch (Exception error) { return Faulted(error.Message); }
        }
    }

    private static AScanSelectionResult Faulted(string? error) =>
        new(BScanTraceResultKind.Faulted, Error: string.IsNullOrWhiteSpace(error)
            ? "Trace selection failed." : error);
}
