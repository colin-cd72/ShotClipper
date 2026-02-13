namespace Screener.Golf.Overlays;

/// <summary>
/// Configuration for the lower-third golfer name overlay on exported clips.
/// </summary>
public class LowerThirdConfig
{
    /// <summary>Whether to show the lower third.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Font size in pixels.</summary>
    public int FontSize { get; set; } = 48;

    /// <summary>Font color (hex, e.g., "white" or "0xFFFFFF").</summary>
    public string FontColor { get; set; } = "white";

    /// <summary>Background box color with opacity (e.g., "black@0.6").</summary>
    public string BoxColor { get; set; } = "black@0.6";

    /// <summary>Whether to show the background box.</summary>
    public bool ShowBox { get; set; } = true;

    /// <summary>Box padding in pixels.</summary>
    public int BoxPadding { get; set; } = 10;

    /// <summary>X position from left edge.</summary>
    public int X { get; set; } = 40;

    /// <summary>Y position from bottom edge (pixels from bottom).</summary>
    public int YFromBottom { get; set; } = 80;

    /// <summary>Font family name (must be available on system).</summary>
    public string FontFamily { get; set; } = "Arial";

    /// <summary>
    /// Build FFmpeg drawtext filter string for a given golfer name.
    /// </summary>
    public string BuildDrawtextFilter(string golferName)
    {
        var escaped = golferName.Replace("'", "'\\''").Replace(":", "\\:");
        var filter = $"drawtext=text='{escaped}':fontsize={FontSize}:fontcolor={FontColor}" +
                     $":x={X}:y=H-{YFromBottom}";

        if (!string.IsNullOrEmpty(FontFamily))
            filter += $":fontfamily='{FontFamily}'";

        if (ShowBox)
            filter += $":box=1:boxcolor={BoxColor}:boxborderw={BoxPadding}";

        return filter;
    }
}
