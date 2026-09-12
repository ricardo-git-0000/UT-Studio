using System.Buffers;
using UTStudio.Domain.Acquisition;

namespace UTStudio.Contracts.Acquisition;

/// <summary>Owns exactly one RF sample lease until disposal or exclusive ownership transfer.</summary>
/// <remarks>
/// Ownership transfers from the caller only after successful construction; failures leave it with the caller.
/// After transfer, the caller must not modify, read or dispose the owner.
/// Do not read concurrently with disposal or use previously obtained memory after disposal.
/// ReadOnlyMemory cannot revoke existing aliases. Only Dispose is safe to call concurrently.
/// </remarks>
public sealed class ConventionalUtFrame : IDisposable
{
    private IMemoryOwner<short>? _sampleOwner;
    private ReadOnlyMemory<short> _samples;

    public ConventionalUtFrame(
        ConventionalUtFrameMetadata metadata,
        ulong sequence,
        TimeSpan elapsedSinceRunStart,
        IMemoryOwner<short> sampleOwner)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        ArgumentNullException.ThrowIfNull(sampleOwner);
        ArgumentOutOfRangeException.ThrowIfLessThan(elapsedSinceRunStart, TimeSpan.Zero);
        var memory = sampleOwner.Memory;
        if (memory.Length < metadata.Configuration.SampleCount)
        {
            throw new ArgumentException("Sample memory is shorter than SampleCount.", nameof(sampleOwner));
        }

        _samples = memory[..metadata.Configuration.SampleCount];
        Metadata = metadata;
        Sequence = sequence;
        ElapsedSinceRunStart = elapsedSinceRunStart;
        _sampleOwner = sampleOwner;
    }

    public ConventionalUtFrameMetadata Metadata { get; }

    /// <summary>Producer sequence, starting at zero per run. Ordering is enforced by the source.</summary>
    public ulong Sequence { get; }

    /// <summary>Nonnegative monotonic time of the trigger relative to the run origin.</summary>
    public TimeSpan ElapsedSinceRunStart { get; }

    /// <summary>Only the valid signed RF samples, in digital counts. Throws after disposal.</summary>
    public ReadOnlyMemory<short> Samples
    {
        get
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _sampleOwner) is null, this);
            return _samples;
        }
    }

    /// <summary>Invokes the owner's Dispose at most once, even if it throws.</summary>
    /// <remarks>A failing owner disposal is propagated; subsequent calls do not retry it.</remarks>
    public void Dispose()
    {
        var owner = Interlocked.Exchange(ref _sampleOwner, null);
        if (owner is null)
        {
            return;
        }

        _samples = default;
        owner.Dispose();
    }
}
