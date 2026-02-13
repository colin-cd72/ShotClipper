namespace Screener.Golf.Detection;

/// <summary>
/// Configuration for auto-cut detection thresholds, ROI, and timing.
/// </summary>
public class AutoCutConfiguration
{
    // --- Swing Detection (Source 1) ---

    /// <summary>Downsampled analysis width. Default 120 for ~8KB per luma frame.</summary>
    public int AnalysisWidth { get; set; } = 120;

    /// <summary>Downsampled analysis height. Default 68.</summary>
    public int AnalysisHeight { get; set; } = 68;

    /// <summary>Analyze every Nth frame from the source. Default 4 (15fps from 60fps).</summary>
    public int FrameSkip { get; set; } = 4;

    /// <summary>EMA smoothing factor (0-1). Lower = smoother baseline. Default 0.05.</summary>
    public double EmaAlpha { get; set; } = 0.05;

    /// <summary>Spike must exceed EMA by this multiplier to trigger swing detection. Default 4.0.</summary>
    public double SwingSpikeMultiplier { get; set; } = 4.0;

    /// <summary>Minimum SAD value to consider as a real spike (filters noise). Default 500.</summary>
    public double MinimumSpikeThreshold { get; set; } = 500;

    /// <summary>Compare current frame against the frame from N analysis cycles ago. Default 2.</summary>
    public int FrameCompareGap { get; set; } = 2;

    // --- Swing ROI (normalized 0.0-1.0 coordinates) ---

    /// <summary>ROI left edge (0.0 = left). Default 0.2.</summary>
    public double RoiLeft { get; set; } = 0.2;

    /// <summary>ROI top edge (0.0 = top). Default 0.1.</summary>
    public double RoiTop { get; set; } = 0.1;

    /// <summary>ROI width (fraction of frame). Default 0.6.</summary>
    public double RoiWidth { get; set; } = 0.6;

    /// <summary>ROI height (fraction of frame). Default 0.8.</summary>
    public double RoiHeight { get; set; } = 0.8;

    // --- Landing/Reset Detection (Source 2) ---

    /// <summary>Similarity threshold (0-1) for matching the idle reference. Default 0.95.</summary>
    public double IdleSimilarityThreshold { get; set; } = 0.95;

    /// <summary>Number of consecutive idle frames required to confirm reset. Default 3.</summary>
    public int ConsecutiveIdleFramesRequired { get; set; } = 3;

    /// <summary>SAD threshold for inter-frame "static" detection. Default 200.</summary>
    public double StaticSceneThreshold { get; set; } = 200;

    // --- Timing ---

    /// <summary>Maximum time on Source 2 before forcing a cut back (seconds). Default 30.</summary>
    public double MaxSimulatorDurationSeconds { get; set; } = 30;

    /// <summary>Practice swing timeout: if simulator stays idle within this many seconds of cut, treat as practice. Default 3.</summary>
    public double PracticeSwingTimeoutSeconds { get; set; } = 3;

    /// <summary>Post-landing delay before cutting back to Source 1 (seconds). Default 1.5.</summary>
    public double PostLandingDelaySeconds { get; set; } = 1.5;

    /// <summary>Cooldown period after cutting back to Source 1 (seconds). Default 2.</summary>
    public double CooldownDurationSeconds { get; set; } = 2;

    // --- Sensitivity presets ---

    public static AutoCutConfiguration Default => new();

    public static AutoCutConfiguration HighSensitivity => new()
    {
        SwingSpikeMultiplier = 3.0,
        MinimumSpikeThreshold = 300,
        IdleSimilarityThreshold = 0.90,
        ConsecutiveIdleFramesRequired = 2
    };

    public static AutoCutConfiguration LowSensitivity => new()
    {
        SwingSpikeMultiplier = 6.0,
        MinimumSpikeThreshold = 800,
        IdleSimilarityThreshold = 0.97,
        ConsecutiveIdleFramesRequired = 4
    };
}
