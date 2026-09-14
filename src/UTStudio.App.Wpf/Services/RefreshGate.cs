namespace UTStudio.App.Wpf.Services;

/// <summary>Gate measured from actual admission; never catches up missed ticks.</summary>
internal sealed class RefreshGate(TimeProvider clock, TimeSpan interval)
{
    private long? _last;
    internal bool TryEnter()
    {
        long now = clock.GetTimestamp();
        if (_last is { } last && clock.GetElapsedTime(last, now) < interval) { return false; }
        _last = now;
        return true;
    }
}
