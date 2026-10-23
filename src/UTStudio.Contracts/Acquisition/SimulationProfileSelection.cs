namespace UTStudio.Contracts.Acquisition;

public sealed record SimulationProfileOption(string Id, string DisplayName)
{
    public bool IsValid => !string.IsNullOrWhiteSpace(Id) && !string.IsNullOrWhiteSpace(DisplayName);
}

/// <summary>Neutral pre-run selection port. The selected configuration becomes immutable on first Start.</summary>
public interface ISimulationProfileSelection
{
    IReadOnlyList<SimulationProfileOption> Options { get; }
    string SelectedProfileId { get; }
    bool CanSelectProfile { get; }
    void SelectProfile(string profileId);
}
