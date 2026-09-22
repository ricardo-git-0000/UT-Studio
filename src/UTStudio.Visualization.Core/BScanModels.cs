using System.Buffers;
using UTStudio.Domain.Acquisition;

namespace UTStudio.Visualization.Core;

public readonly record struct BScanTraceIdentity(
    UtSourceId SourceId,
    AcquisitionRunId RunId,
    PhysicalChannelId PhysicalChannelId,
    ulong Sequence)
{
    public bool IsValid => SourceId.IsValid && RunId.IsValid;
}

public sealed record BScanTraceDescriptor(
    BScanTraceIdentity Identity,
    ulong AcquisitionIndex,
    TimeSpan ElapsedSinceRunStart,
    int SampleCount,
    double SampleRateHz,
    double FirstSampleOffsetSeconds,
    ScanSpatialCoordinate? SpatialCoordinate,
    ulong StoreRevision,
    ulong Generation);

public readonly record struct BScanMinMax(short Minimum, short Maximum);

public enum BScanTraceResultKind
{
    Available,
    NotAvailable,
    Evicted,
    Cancelled,
    Faulted
}

/// <summary>An exact, independently owned trace copy. Dispose returns its pooled storage.</summary>
public sealed class BScanTraceSnapshot : IDisposable
{
    private Ownership? _ownership;
    private ReadOnlyMemory<short> _samples;

    internal BScanTraceSnapshot(BScanTraceDescriptor descriptor, IMemoryOwner<short> owner, Action? release = null)
    {
        Descriptor = descriptor;
        _ownership = new(owner, release);
        _samples = owner.Memory[..descriptor.SampleCount];
    }

    public BScanTraceDescriptor Descriptor { get; }
    public ReadOnlyMemory<short> Samples => Volatile.Read(ref _ownership) is not null
        ? _samples : throw new ObjectDisposedException(nameof(BScanTraceSnapshot));
    public void Dispose()
    {
        Ownership? ownership = Interlocked.Exchange(ref _ownership, null);
        _samples = default;
        if (ownership is null) { return; }
        try { ownership.Owner.Dispose(); }
        finally { ownership.Release?.Invoke(); }
    }

    private sealed record Ownership(IMemoryOwner<short> Owner, Action? Release);
}

public sealed class BScanTraceResult
{
    private BScanTraceResult(BScanTraceResultKind kind, BScanTraceSnapshot? trace, string? error)
    {
        Kind = kind;
        Trace = trace;
        Error = error;
    }

    public BScanTraceResultKind Kind { get; }
    public BScanTraceSnapshot? Trace { get; }
    public string? Error { get; }
    public static BScanTraceResult Available(BScanTraceSnapshot trace) => new(BScanTraceResultKind.Available, trace, null);
    public static BScanTraceResult NotAvailable() => new(BScanTraceResultKind.NotAvailable, null, null);
    public static BScanTraceResult Evicted() => new(BScanTraceResultKind.Evicted, null, null);
    public static BScanTraceResult Cancelled() => new(BScanTraceResultKind.Cancelled, null, null);
    public static BScanTraceResult Faulted(Exception error) =>
        new(BScanTraceResultKind.Faulted, null, string.IsNullOrWhiteSpace(error.Message) ? error.GetType().Name : error.Message);
}

public interface IBScanTraceProvider
{
    ValueTask<BScanTraceResult> GetTraceAsync(BScanTraceIdentity identity, CancellationToken cancellationToken = default);
}
