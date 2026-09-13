namespace UTStudio.Visualization.Core;

/// <summary>Time in seconds and bipolar RF amplitude in percent of 32768 digital counts.</summary>
public readonly record struct AScanPoint(double TimeSeconds, double AmplitudePercent);
