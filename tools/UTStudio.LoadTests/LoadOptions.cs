namespace UTStudio.LoadTests;

internal enum LoadProfile { Smoke, Baseline, Soak }

internal sealed record LoadOptions(LoadProfile Profile, int SampleCount, double? Rate, TimeSpan Duration)
{
    internal TimeSpan Warmup { get; init; } = TimeSpan.Zero;
    internal TimeSpan ProgressTimeout { get; init; } = TimeSpan.FromSeconds(10);
    internal TimeSpan CleanupTimeout { get; init; } = TimeSpan.FromSeconds(10);
    internal static readonly TimeSpan SmokeDuration = TimeSpan.FromSeconds(30);
    internal static readonly TimeSpan BaselineDuration = TimeSpan.FromMinutes(5);
    internal static readonly TimeSpan SoakDuration = TimeSpan.FromMinutes(30);

    internal string RateLabel => Rate is null ? "max" : Rate.Value.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);

    internal static bool TryParse(string[] args, out LoadOptions? options, out string? error)
    {
        options = null;
        error = null;
        LoadProfile profile = LoadProfile.Smoke;
        int samples = 2048;
        double? rate = 50;
        TimeSpan? duration = null;
        TimeSpan warmup = TimeSpan.Zero;
        TimeSpan progressTimeout = TimeSpan.FromSeconds(10), cleanupTimeout = TimeSpan.FromSeconds(10);
        for (int i = 0; i < args.Length; i++)
        {
            string value;
            switch (args[i])
            {
                case "--profile":
                    if (!Take(args, ref i, out value) || !Enum.TryParse(value, true, out profile) ||
                        !Enum.IsDefined(profile) || int.TryParse(value, out _))
                    { error = "--profile expects smoke, baseline or soak."; return false; }
                    break;
                case "--samples":
                    if (!Take(args, ref i, out value) || !int.TryParse(value, out samples))
                    { error = "--samples expects an integer."; return false; }
                    break;
                case "--rate":
                    if (!Take(args, ref i, out value)) { error = "--rate expects a number or max."; return false; }
                    if (value.Equals("max", StringComparison.OrdinalIgnoreCase)) { rate = null; }
                    else if (!double.TryParse(value, System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out double parsedRate))
                    { error = "--rate expects a number or max."; return false; }
                    else { rate = parsedRate; }
                    break;
                case "--duration":
                    if (!Take(args, ref i, out value) || !TryDuration(value, out TimeSpan parsedDuration))
                    { error = "--duration expects a positive value such as 10s, 5m or 1h."; return false; }
                    duration = parsedDuration;
                    break;
                case "--warmup":
                    if (!Take(args, ref i, out value) || (value != "0s" && !TryDuration(value, out warmup)))
                    { error = "--warmup expects 0s or a positive duration."; return false; }
                    if (value == "0s") { warmup = TimeSpan.Zero; }
                    break;
                case "--progress-timeout":
                    if (!Take(args, ref i, out value) || !TryDuration(value, out progressTimeout))
                    { error = "--progress-timeout expects a positive duration."; return false; }
                    break;
                case "--cleanup-timeout":
                    if (!Take(args, ref i, out value) || !TryDuration(value, out cleanupTimeout))
                    { error = "--cleanup-timeout expects a positive duration."; return false; }
                    break;
                case "--help":
                case "-h":
                    error = null; return false;
                default:
                    error = $"Unknown option: {args[i]}"; return false;
            }
        }
        if (samples is not (2048 or 65535)) { error = "--samples must be 2048 or 65535."; return false; }
        if (rate is { } finite && (!double.IsFinite(finite) || finite <= 0 || finite > 1_000_000))
        { error = "--rate must be finite, positive and no greater than 1000000, or max."; return false; }
        if (rate is null && samples != 65535) { error = "--rate max is defined only for 65535 samples."; return false; }
        if (rate is { } target && progressTimeout.TotalSeconds <= 1 / target)
        { error = "--progress-timeout must exceed the target frame period."; return false; }
        TimeSpan selected = duration ?? profile switch
        {
            LoadProfile.Smoke => SmokeDuration,
            LoadProfile.Baseline => BaselineDuration,
            _ => SoakDuration
        };
        options = new(profile, samples, rate, selected)
        { Warmup = warmup, ProgressTimeout = progressTimeout, CleanupTimeout = cleanupTimeout };
        return true;
    }

    private static bool Take(string[] args, ref int index, out string value)
    {
        if (++index >= args.Length) { value = string.Empty; return false; }
        value = args[index]; return true;
    }

    private static bool TryDuration(string text, out TimeSpan duration)
    {
        duration = default;
        if (text.Length < 2 || !double.TryParse(text[..^1], System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out double amount) || !double.IsFinite(amount) || amount <= 0)
        { return false; }
        try
        {
            duration = char.ToLowerInvariant(text[^1]) switch
            {
                's' => TimeSpan.FromSeconds(amount),
                'm' => TimeSpan.FromMinutes(amount),
                'h' => TimeSpan.FromHours(amount),
                _ => default
            };
            return duration > TimeSpan.Zero && duration <= TimeSpan.FromHours(24);
        }
        catch (OverflowException) { return false; }
    }
}
