using UTStudio.Domain.Acquisition;

namespace UTStudio.Contracts.Presentation;

/// <summary>Optional, borrowed visual branch; never a lossless processing or persistence port.</summary>
/// <remarks>
/// One Application reader calls Accept synchronously. Samples are borrowed only until it returns;
/// do not retain aliases or the frame owner. Copy/project before returning. Open/Close are bounded,
/// nonblocking operations without external callbacks. Close may overlap Accept and must invalidate
/// its pending result. Exceptions disable only this visual branch; Application still releases the frame.
/// The source and sink are disposed by their external owners after the session closes its run.
/// </remarks>
public interface IConventionalFrameSink
{
    void OpenRun(AcquisitionRunId runId);
    void Accept(ConventionalUtFrameMetadata metadata, ulong sequence, TimeSpan elapsedSinceRunStart,
        ReadOnlySpan<short> samples);
    void CloseRun(AcquisitionRunId runId);
}
