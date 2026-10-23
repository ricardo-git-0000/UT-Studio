using UTStudio.Domain.Acquisition;

namespace UTStudio.Application;

public sealed record SimulationInspectionConfiguration(
    string SignalProfileId, uint SignalProfileVersion,
    string GateBootstrapProfileId, uint GateBootstrapProfileVersion,
    ulong Seed, PhysicalChannelId PhysicalChannelId, int SampleCount,
    double SampleRateHz, double FirstSampleOffsetSeconds, UtSignalMode SignalMode)
{
    public bool IsValid => !string.IsNullOrWhiteSpace(SignalProfileId) && SignalProfileVersion > 0 &&
        !string.IsNullOrWhiteSpace(GateBootstrapProfileId) && GateBootstrapProfileVersion > 0 &&
        SampleCount > 0 && double.IsFinite(SampleRateHz) && SampleRateHz > 0 &&
        double.IsFinite(FirstSampleOffsetSeconds) && Enum.IsDefined(SignalMode);
}
