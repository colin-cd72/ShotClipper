using Microsoft.Extensions.Logging;

namespace Screener.Golf.Detection;

/// <summary>
/// Detects when the Full Swing simulator has reset to idle (ball has landed)
/// by comparing frames against a calibrated idle reference.
/// </summary>
public class ResetDetector
{
    private readonly ILogger _logger;
    private readonly AutoCutConfiguration _config;
    private readonly int _analysisPixelCount;

    // Calibrated idle reference frame (luma only)
    private byte[]? _idleReference;
    private bool _isCalibrated;

    // Current analysis buffer
    private byte[]? _currentLuma;
    private byte[]? _previousLuma;

    // Consecutive idle frame counter
    private int _consecutiveIdleFrames;

    public bool IsCalibrated => _isCalibrated;
    public double LastSimilarity { get; private set; }
    public double LastInterFrameSad { get; private set; }

    /// <summary>Fired when the simulator has reset to idle.</summary>
    public event EventHandler? ResetDetected;

    public ResetDetector(ILogger logger, AutoCutConfiguration config)
    {
        _logger = logger;
        _config = config;
        _analysisPixelCount = config.AnalysisWidth * config.AnalysisHeight;
        _currentLuma = new byte[_analysisPixelCount];
        _previousLuma = new byte[_analysisPixelCount];
    }

    /// <summary>
    /// Calibrate by capturing the current simulator frame as the idle reference.
    /// </summary>
    public void CalibrateIdleReference(ReadOnlySpan<byte> uyvyData, int srcWidth, int srcHeight)
    {
        _idleReference = new byte[_analysisPixelCount];
        FrameAnalyzer.ExtractLumaDownsampled(
            uyvyData, srcWidth, srcHeight,
            _config.AnalysisWidth, _config.AnalysisHeight,
            _idleReference);

        _isCalibrated = true;
        _consecutiveIdleFrames = 0;
        _logger.LogInformation("Idle reference calibrated from simulator frame ({W}x{H})",
            _config.AnalysisWidth, _config.AnalysisHeight);
    }

    /// <summary>
    /// Process a raw UYVY frame from Source 2 (simulator).
    /// </summary>
    /// <returns>True if the simulator has reset to idle.</returns>
    public bool ProcessFrame(ReadOnlySpan<byte> uyvyData, int srcWidth, int srcHeight)
    {
        if (!_isCalibrated || _idleReference == null) return false;

        // Swap buffers
        (_currentLuma, _previousLuma) = (_previousLuma, _currentLuma);

        // Extract current frame luma
        FrameAnalyzer.ExtractLumaDownsampled(
            uyvyData, srcWidth, srcHeight,
            _config.AnalysisWidth, _config.AnalysisHeight,
            _currentLuma!);

        // Compare against idle reference
        double similarity = FrameAnalyzer.ComputeSimilarity(
            _currentLuma!, _idleReference,
            _config.AnalysisWidth, _config.AnalysisHeight);
        LastSimilarity = similarity;

        // Check inter-frame difference (is the scene static?)
        double interFrameSad = 0;
        if (_previousLuma != null)
        {
            interFrameSad = FrameAnalyzer.ComputeSadInRoi(
                _currentLuma!, _previousLuma,
                _config.AnalysisWidth, _config.AnalysisHeight,
                0, 0, 1.0, 1.0);
        }
        LastInterFrameSad = interFrameSad;

        // Check if frame matches idle reference AND scene is static
        bool isIdle = similarity >= _config.IdleSimilarityThreshold
                      && interFrameSad < _config.StaticSceneThreshold;

        if (isIdle)
        {
            _consecutiveIdleFrames++;
            if (_consecutiveIdleFrames >= _config.ConsecutiveIdleFramesRequired)
            {
                _logger.LogInformation(
                    "Simulator reset detected: Similarity={Sim:P1}, InterFrameSAD={Sad:F1}, ConsecutiveIdle={Count}",
                    similarity, interFrameSad, _consecutiveIdleFrames);
                ResetDetected?.Invoke(this, EventArgs.Empty);
                return true;
            }
        }
        else
        {
            _consecutiveIdleFrames = 0;
        }

        return false;
    }

    /// <summary>
    /// Reset the detector counters (but keep calibration).
    /// </summary>
    public void Reset()
    {
        _consecutiveIdleFrames = 0;
        LastSimilarity = 0;
        LastInterFrameSad = 0;
    }

    /// <summary>
    /// Clear calibration entirely.
    /// </summary>
    public void ClearCalibration()
    {
        _idleReference = null;
        _isCalibrated = false;
        Reset();
    }
}
