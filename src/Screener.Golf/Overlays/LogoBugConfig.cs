namespace Screener.Golf.Overlays;

/// <summary>
/// Configuration for the logo bug overlay on exported clips.
/// </summary>
public class LogoBugConfig
{
    /// <summary>Path to the logo image file (PNG with transparency).</summary>
    public string? LogoPath { get; set; }

    /// <summary>Position preset.</summary>
    public LogoPosition Position { get; set; } = LogoPosition.TopRight;

    /// <summary>Custom X offset from edge (pixels). Used when Position is Custom.</summary>
    public double CustomX { get; set; } = 20;

    /// <summary>Custom Y offset from edge (pixels). Used when Position is Custom.</summary>
    public double CustomY { get; set; } = 20;

    /// <summary>Scale factor (0.1 to 2.0). Default 1.0 = original size.</summary>
    public double Scale { get; set; } = 1.0;

    /// <summary>Opacity (0.0 to 1.0). Default 1.0 = fully opaque.</summary>
    public double Opacity { get; set; } = 1.0;

    /// <summary>Margin from edge in pixels.</summary>
    public int Margin { get; set; } = 20;

    /// <summary>
    /// Build FFmpeg overlay position expression.
    /// </summary>
    public string GetOverlayPosition()
    {
        return Position switch
        {
            LogoPosition.TopLeft => $"x={Margin}:y={Margin}",
            LogoPosition.TopRight => $"x=W-w-{Margin}:y={Margin}",
            LogoPosition.BottomLeft => $"x={Margin}:y=H-h-{Margin}",
            LogoPosition.BottomRight => $"x=W-w-{Margin}:y=H-h-{Margin}",
            LogoPosition.Custom => $"x={(int)CustomX}:y={(int)CustomY}",
            _ => $"x=W-w-{Margin}:y={Margin}"
        };
    }
}

public enum LogoPosition
{
    TopLeft,
    TopRight,
    BottomLeft,
    BottomRight,
    Custom
}
