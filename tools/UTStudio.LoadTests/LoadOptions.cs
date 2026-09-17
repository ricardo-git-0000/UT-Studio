namespace UTStudio.LoadTests;

internal enum LoadProfile { Smoke, Baseline, Soak }
internal enum LoadSourceMode { Auto, Production, Experimental }
internal enum TelemetryMode { Minimal, Full }
internal enum ProgressMode { Normal, Quiet }
internal enum PacingMode { SkipMissed, CatchUpBounded }

internal sealed record LoadOptions(LoadProfile Profile, int SampleCount, double? Rate, TimeSpan Duration)
{
    internal TimeSpan Warmup { get; init; } = TimeSpan.Zero;
    internal TimeSpan ProgressTimeout { get; init; } = TimeSpan.FromSeconds(10);
    internal TimeSpan CleanupTimeout { get; init; } = TimeSpan.FromSeconds(10);
    internal LoadSourceMode Source { get; init; } = LoadSourceMode.Auto;
    internal TelemetryMode Telemetry { get; init; } = TelemetryMode.Full;
    internal ProgressMode Progress { get; init; } = ProgressMode.Normal;
    internal string? OutputPath { get; init; }
    internal PacingMode Pacing { get; init; } = PacingMode.SkipMissed;
    internal int MaxCatchUp { get; init; } = 32;
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
        LoadSourceMode source = LoadSourceMode.Auto;
        TelemetryMode telemetry = TelemetryMode.Full;
        ProgressMode progress = ProgressMode.Normal;
        string? outputPath = null;
        PacingMode pacing = PacingMode.SkipMissed;
        int maxCatchUp = 32;
        bool pacingSpecified = false, maxCatchUpSpecified = false;
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
                case "--source":
                    if (!Take(args, ref i, out value) || !TryEnum(value, out source))
                    { error = "--source expects auto, production or experimental."; return false; }
                    break;
                case "--telemetry":
                    if (!Take(args, ref i, out value) || !TryEnum(value, out telemetry))
                    { error = "--telemetry expects minimal or full."; return false; }
                    break;
                case "--progress":
                    if (!Take(args, ref i, out value) || !TryEnum(value, out progress))
                    { error = "--progress expects normal or quiet."; return false; }
                    break;
                case "--pacing":
                    if (!Take(args, ref i, out value) || !TryPacing(value, out pacing))
                    { error = "--pacing expects skip-missed or catch-up-bounded."; return false; }
                    pacingSpecified = true;
                    break;
                case "--max-catch-up":
                    if (!Take(args, ref i, out value) || !int.TryParse(value, out maxCatchUp) || maxCatchUp is < 1 or > 32)
                    { error = "--max-catch-up expects an integer from 1 through 32."; return false; }
                    maxCatchUpSpecified = true;
                    break;
                case "--output":
                    if (!Take(args, ref i, out value) || string.IsNullOrWhiteSpace(value) ||
                        !Path.GetExtension(value).Equals(".json", StringComparison.OrdinalIgnoreCase))
                    { error = "--output expects a .json path."; return false; }
                    try { outputPath = Path.GetFullPath(value); }
                    catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
                    { error = "--output expects a valid .json path."; return false; }
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
        if (rate is null && pacingSpecified)
        { error = "--rate max is unpaced and incompatible with --pacing."; return false; }
        if (maxCatchUpSpecified && pacing != PacingMode.CatchUpBounded)
        { error = "--max-catch-up requires --pacing catch-up-bounded."; return false; }
        if (source == LoadSourceMode.Production && pacing == PacingMode.CatchUpBounded)
        { error = "--source production is incompatible with --pacing catch-up-bounded."; return false; }
        if (pacing == PacingMode.CatchUpBounded && source == LoadSourceMode.Auto)
        { source = LoadSourceMode.Experimental; }
        if (source == LoadSourceMode.Production && (rate is null or > 100))
        { error = "--source production supports a numeric --rate no greater than 100/s."; return false; }
        if (rate is { } target && progressTimeout.TotalSeconds <= 1 / target)
        { error = "--progress-timeout must exceed the target frame period."; return false; }
        TimeSpan selected = duration ?? profile switch
        {
            LoadProfile.Smoke => SmokeDuration,
            LoadProfile.Baseline => BaselineDuration,
            _ => SoakDuration
        };
        options = new(profile, samples, rate, selected)
        {
            Warmup = warmup,
            ProgressTimeout = progressTimeout,
            CleanupTimeout = cleanupTimeout,
            Source = source,
            Telemetry = telemetry,
            Progress = progress,
            OutputPath = outputPath,
            Pacing = pacing,
            MaxCatchUp = maxCatchUp
        };
        return true;
    }

    private static bool Take(string[] args, ref int index, out string value)
    {
        if (++index >= args.Length) { value = string.Empty; return false; }
        value = args[index]; return true;
    }

    private static bool TryEnum<T>(string value, out T parsed) where T : struct, Enum =>
        Enum.TryParse(value, true, out parsed) && Enum.IsDefined(parsed) && !int.TryParse(value, out _);

    private static bool TryPacing(string value, out PacingMode pacing)
    {
        pacing = value.ToLowerInvariant() switch
        {
            "skip-missed" => PacingMode.SkipMissed,
            "catch-up-bounded" => PacingMode.CatchUpBounded,
            _ => (PacingMode)(-1)
        };
        return Enum.IsDefined(pacing);
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
