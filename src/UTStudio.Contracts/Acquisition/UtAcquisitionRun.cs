using System.Threading.Channels;
using UTStudio.Domain.Acquisition;

namespace UTStudio.Contracts.Acquisition;

/// <summary>A passive execution descriptor. It neither produces frames nor completes the supplied tasks.</summary>
public sealed class UtAcquisitionRun
{
    public UtAcquisitionRun(
        ConventionalUtFrameMetadata metadata,
        ChannelReader<ConventionalUtFrame> frames,
        Task producerCompletion,
        Task allFramesReleased)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        ArgumentNullException.ThrowIfNull(frames);
        ArgumentNullException.ThrowIfNull(producerCompletion);
        ArgumentNullException.ThrowIfNull(allFramesReleased);
        Metadata = metadata;
        Frames = frames;
        ProducerCompletion = producerCompletion;
        AllFramesReleased = allFramesReleased;
    }

    public ConventionalUtFrameMetadata Metadata { get; }

    /// <summary>
    /// A new bounded channel per execution, exclusively read by Application.
    /// Every accepted frame must be disposed or transferred, including while draining after failure.
    /// All frames must match Metadata. A reader is not a broadcast mechanism.
    /// </summary>
    public ChannelReader<ConventionalUtFrame> Frames { get; }

    /// <summary>
    /// Completes when the producer has exited, released its untransferred frame and completed the writer.
    /// This is distinct from Frames.Completion, which also depends on draining queued frames.
    /// Producer failures remain observable here and through channel completion.
    /// </summary>
    public Task ProducerCompletion { get; }

    /// <summary>
    /// Supplied by the source: succeeds only after new reservations are sealed and all owners returned.
    /// Includes frames already read or in transformation. Independent of UI and producer completion.
    /// Must not succeed merely because there are no leases before the first frame.
    /// A failed release must not be reported as success. This descriptor cannot enforce these guarantees.
    /// </summary>
    public Task AllFramesReleased { get; }
}
