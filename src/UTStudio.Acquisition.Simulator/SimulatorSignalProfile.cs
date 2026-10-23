namespace UTStudio.Acquisition.Simulator;

public enum SimulatorEchoMotion { MovingWrap, Stationary }

public sealed record SimulatorSignalProfile(
    string Id, uint Version, string DisplayName, SimulatorEchoMotion Motion,
    double FirstEchoSeconds, double SecondEchoSeconds,
    double FirstAmplitude, double SecondAmplitude,
    double CarrierHz, double SigmaSeconds)
{
    public bool IsValid => !string.IsNullOrWhiteSpace(Id) && Version > 0 &&
        !string.IsNullOrWhiteSpace(DisplayName) && Enum.IsDefined(Motion) &&
        double.IsFinite(FirstEchoSeconds) && double.IsFinite(SecondEchoSeconds) &&
        FirstEchoSeconds < SecondEchoSeconds && FirstAmplitude > 0 && SecondAmplitude > 0 &&
        CarrierHz > 0 && SigmaSeconds > 0;
}

public static class SimulatorSignalProfiles
{
    public static SimulatorSignalProfile MovingV1 { get; } = new(
        "moving-echoes", 1, "Ecos móviles", SimulatorEchoMotion.MovingWrap,
        10e-6, 25e-6, .6, .3, 5_000_000, .35e-6);
    public static SimulatorSignalProfile StationaryV1 { get; } = new(
        "stationary-echoes", 1, "Ecos estacionarios A/B/C", SimulatorEchoMotion.Stationary,
        10e-6, 25e-6, .6, .3, 5_000_000, .35e-6);
    public static IReadOnlyList<SimulatorSignalProfile> All { get; } = [MovingV1, StationaryV1];
}
