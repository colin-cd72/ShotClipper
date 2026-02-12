namespace Screener.Capture.Srt;

/// <summary>
/// Configuration for a single SRT input source.
/// </summary>
public record SrtInputConfig(string Name, int Port, int LatencyMs = 120);
