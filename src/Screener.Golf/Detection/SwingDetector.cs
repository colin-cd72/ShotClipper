using Microsoft.Extensions.Logging;

namespace Screener.Golf.Detection;

/// <summary>
/// Detects golf swings on Source 1 (golfer camera) using frame-to-frame SAD spikes
/// against an exponential moving average baseline.
/// </summary>
public class SwingDetector
{
    private readonly ILogger _logger;
    private readonly AutoCutConfiguration _config;

    // Circular buffer of recent luma frames for comparison
    private readonly byte[]?[] _frameHistory;
    private int _frameHistoryIndex;
    private int _framesStored;

    // EMA baseline
    private double _emaBaseline;
    private bool _emaInitialized;

    // Analysis buffer
    private readonly int _analysisPixelCount;

    public double CurrentEma => _emaBaseline;
    public double LastSad { get; private set; }

    /// <summary>Fired when a swing spike is detected.</summary>
    public event EventHandler? SwingDetected;

    public SwingDetector(ILogger logger, AutoCutConfiguration config)
    {
        _logger = logger;
        _config = config;
        _analysisPixelCount = config.AnalysisWidth * config.AnalysisHeight;

        // Keep enough frames for the compare gap
        int historySize = config.FrameCompareGap + 1;
        _frameHistory = new byte[historySize][];
        for (int i = 0; i < historySize; i++)
            _frameHistory[i] = new byte[_analysisPixelCount];
    }

    /// <summary>
    /// Process a raw UYVY frame from Source 1. Call every Nth frame (per FrameSkip config).
    /// </summary>
    /// <param name="uyvyData">Raw UYVY frame bytes.</param>
    /// <param name="srcWidth">Source width.</param>
    /// <param name="srcHeight">Source height.</param>
    /// <returns>True if a swing was detected on this frame.</returns>
    public bool ProcessFrame(ReadOnlySpan<byte> uyvyData, int srcWidth, int srcHeight)
    {
        // Extract luma into the current history slot
        var currentBuffer = _frameHistory[_frameHistoryIndex]!;
        FrameAnalyzer.ExtractLumaDownsampled(
            uyvyData, srcWidth, srcHeight,
            _config.AnalysisWidth, _config.AnalysisHeight,
            currentBuffer);

        _framesStored = Math.Min(_framesStored + 1, _frameHistory.Length);

        // Need at least FrameCompareGap+1 frames before we can compare
        if (_framesStored <= _config.FrameCompareGap)
        {
            _frameHistoryIndex = (_frameHistoryIndex + 1) % _frameHistory.Length;
            return false;
        }

        // Get the frame from FrameCompareGap cycles ago
        int compareIndex = (_frameHistoryIndex - _config.FrameCompareGap + _frameHistory.Length) % _frameHistory.Length;
        var compareBuffer = _frameHistory[compareIndex]!;

        // Compute SAD within the ROI
        double sad = FrameAnalyzer.ComputeSadInRoi(
            currentBuffer, compareBuffer,
            _config.AnalysisWidth, _config.AnalysisHeight,
            _config.RoiLeft, _config.RoiTop,
            _config.RoiWidth, _config.RoiHeight);

        LastSad = sad;

        // Update EMA
        if (!_emaInitialized)
        {
            _emaBaseline = sad;
            _emaInitialized = true;
        }
        else
        {
            _emaBaseline = (_config.EmaAlpha * sad) + ((1.0 - _config.EmaAlpha) * _emaBaseline);
        }

        // Advance history index for next frame
        _frameHistoryIndex = (_frameHistoryIndex + 1) % _frameHistory.Length;

        // Check for spike
        double threshold = Math.Max(_emaBaseline * _config.SwingSpikeMultiplier, _config.MinimumSpikeThreshold);
        if (sad > threshold)
        {
            _logger.LogInformation("Swing detected: SAD={Sad:F1}, EMA={Ema:F1}, Threshold={Threshold:F1}",
                sad, _emaBaseline, threshold);
            SwingDetected?.Invoke(this, EventArgs.Empty);
            return true;
        }

        return false;
    }

    /// <summary>
    /// Reset the detector state (e.g., when starting a new session).
    /// </summary>
    public void Reset()
    {
        _framesStored = 0;
        _frameHistoryIndex = 0;
        _emaInitialized = false;
        _emaBaseline = 0;
        LastSad = 0;
    }
}
