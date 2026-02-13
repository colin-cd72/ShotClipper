using Microsoft.Extensions.Logging;
using Screener.Golf.Models;

namespace Screener.Golf.Detection;

/// <summary>
/// Orchestrates the auto-cut state machine for golf swing detection and simulator reset detection.
/// Subscribes to frame callbacks from InputPreviewRenderer and fires CutTriggered events.
/// </summary>
public class AutoCutService
{
    private readonly ILogger<AutoCutService> _logger;
    private readonly SwingDetector _swingDetector;
    private readonly ResetDetector _resetDetector;
    private readonly AudioSwingDetector _audioSwingDetector;
    private AutoCutConfiguration _config;

    private AutoCutState _state = AutoCutState.Disabled;
    private DateTimeOffset _stateEnteredAt;
    private long _source1FrameCount;
    private long _source2FrameCount;

    // Fusion spike timestamps
    private DateTimeOffset? _audioSpikeDetectedAt;
    private DateTimeOffset? _videoSpikeDetectedAt;

    public AutoCutState State => _state;
    public AutoCutConfiguration Configuration => _config;
    public SwingDetector SwingDetector => _swingDetector;
    public ResetDetector ResetDetector => _resetDetector;
    public AudioSwingDetector AudioSwingDetector => _audioSwingDetector;

    /// <summary>
    /// Fired when the auto-cut system determines a source switch should happen.
    /// EventArgs contains the target source index (0 = golfer cam, 1 = simulator).
    /// </summary>
    public event EventHandler<AutoCutEventArgs>? CutTriggered;

    /// <summary>Fired when the state machine transitions.</summary>
    public event EventHandler<AutoCutState>? StateChanged;

    public AutoCutService(ILogger<AutoCutService> logger, AutoCutConfiguration? config = null)
    {
        _config = config ?? AutoCutConfiguration.Default;
        _logger = logger;
        _swingDetector = new SwingDetector(logger, _config);
        _resetDetector = new ResetDetector(logger, _config);
        _audioSwingDetector = new AudioSwingDetector(logger, _config);
    }

    /// <summary>
    /// Enable auto-cut detection. Transitions to WaitingForSwing.
    /// </summary>
    public void Enable()
    {
        if (!_resetDetector.IsCalibrated)
        {
            _logger.LogWarning("Cannot enable auto-cut: simulator idle reference not calibrated");
            return;
        }

        _swingDetector.Reset();
        _resetDetector.Reset();
        _audioSwingDetector.Reset();
        _audioSpikeDetectedAt = null;
        _videoSpikeDetectedAt = null;
        TransitionTo(AutoCutState.WaitingForSwing);
        _logger.LogInformation("Auto-cut enabled");
    }

    /// <summary>
    /// Disable auto-cut detection.
    /// </summary>
    public void Disable()
    {
        TransitionTo(AutoCutState.Disabled);
        _logger.LogInformation("Auto-cut disabled");
    }

    /// <summary>
    /// Update the configuration. Takes effect on next frame analysis.
    /// </summary>
    public void UpdateConfiguration(AutoCutConfiguration config)
    {
        _config = config;
        _logger.LogInformation("Auto-cut configuration updated");
    }

    /// <summary>
    /// Calibrate the idle reference from the current simulator frame.
    /// </summary>
    public void CalibrateIdleReference(ReadOnlyMemory<byte> uyvyData, int srcWidth, int srcHeight)
    {
        _resetDetector.CalibrateIdleReference(uyvyData.Span, srcWidth, srcHeight);
    }

    /// <summary>
    /// Process a frame from Source 1 (golfer camera).
    /// Call this from the frame callback hook at the configured skip rate.
    /// </summary>
    public void ProcessSource1Frame(ReadOnlyMemory<byte> uyvyData, int srcWidth, int srcHeight)
    {
        _source1FrameCount++;

        // Only analyze at the configured skip rate
        if (_source1FrameCount % _config.FrameSkip != 0) return;

        if (_state == AutoCutState.WaitingForSwing)
        {
            bool swingDetected = _swingDetector.ProcessFrame(uyvyData.Span, srcWidth, srcHeight);
            if (swingDetected)
            {
                _videoSpikeDetectedAt = DateTimeOffset.UtcNow;

                // Check if audio spike was recent (fusion)
                string reason = "swing_detected";
                if (_audioSpikeDetectedAt.HasValue)
                {
                    var gap = (DateTimeOffset.UtcNow - _audioSpikeDetectedAt.Value).TotalMilliseconds;
                    if (gap <= _config.AudioVideoFusionWindowMs)
                        reason = "video_audio_fusion";
                }

                TransitionTo(AutoCutState.SwingDetected);
                CutTriggered?.Invoke(this, new AutoCutEventArgs(1, reason));
                TransitionTo(AutoCutState.FollowingShot);
            }
        }
    }

    /// <summary>
    /// Process a frame from Source 2 (simulator output).
    /// Call this from the frame callback hook at the configured skip rate.
    /// </summary>
    public void ProcessSource2Frame(ReadOnlyMemory<byte> uyvyData, int srcWidth, int srcHeight)
    {
        _source2FrameCount++;

        // Only analyze at the configured skip rate
        if (_source2FrameCount % _config.FrameSkip != 0) return;

        if (_state == AutoCutState.FollowingShot)
        {
            var elapsed = DateTimeOffset.UtcNow - _stateEnteredAt;

            // Practice swing detection: if sim stayed idle within timeout, cut back
            if (elapsed.TotalSeconds < _config.PracticeSwingTimeoutSeconds)
            {
                bool isIdle = _resetDetector.ProcessFrame(uyvyData.Span, srcWidth, srcHeight);
                if (isIdle)
                {
                    _logger.LogInformation("Practice swing detected (sim idle within {Timeout}s), cutting back",
                        _config.PracticeSwingTimeoutSeconds);
                    CutTriggered?.Invoke(this, new AutoCutEventArgs(0, "practice_swing"));
                    TransitionTo(AutoCutState.Cooldown);
                    return;
                }
            }
            else
            {
                // Normal reset detection
                bool resetDetected = _resetDetector.ProcessFrame(uyvyData.Span, srcWidth, srcHeight);
                if (resetDetected)
                {
                    TransitionTo(AutoCutState.ResetDetected);
                    return;
                }
            }

            // Timeout: force cut back after max duration
            if (elapsed.TotalSeconds >= _config.MaxSimulatorDurationSeconds)
            {
                _logger.LogWarning("Simulator timeout ({Max}s), forcing cut back to Source 1",
                    _config.MaxSimulatorDurationSeconds);
                CutTriggered?.Invoke(this, new AutoCutEventArgs(0, "timeout"));
                TransitionTo(AutoCutState.Cooldown);
            }
        }
        else if (_state == AutoCutState.ResetDetected)
        {
            // Post-landing delay
            var elapsed = DateTimeOffset.UtcNow - _stateEnteredAt;
            if (elapsed.TotalSeconds >= _config.PostLandingDelaySeconds)
            {
                CutTriggered?.Invoke(this, new AutoCutEventArgs(0, "ball_landed"));
                TransitionTo(AutoCutState.Cooldown);
            }
        }
    }

    /// <summary>
    /// Process audio samples from the golfer camera input.
    /// Detects club impact sounds and optionally fuses with video spike detection.
    /// </summary>
    public void ProcessAudioFrame(ReadOnlyMemory<byte> sampleData, int sampleRate, int channels, int bitsPerSample)
    {
        if (_state != AutoCutState.WaitingForSwing)
            return;

        bool spike = _audioSwingDetector.ProcessAudio(sampleData.Span, sampleRate, channels, bitsPerSample);
        if (!spike)
            return;

        if (_config.AudioOnlyMode)
        {
            // Audio alone triggers the cut
            TransitionTo(AutoCutState.SwingDetected);
            CutTriggered?.Invoke(this, new AutoCutEventArgs(1, "audio_swing"));
            TransitionTo(AutoCutState.FollowingShot);
            return;
        }

        // Check if video spike was recent (fusion)
        if (_videoSpikeDetectedAt.HasValue)
        {
            var gap = (DateTimeOffset.UtcNow - _videoSpikeDetectedAt.Value).TotalMilliseconds;
            if (gap <= _config.AudioVideoFusionWindowMs)
            {
                TransitionTo(AutoCutState.SwingDetected);
                CutTriggered?.Invoke(this, new AutoCutEventArgs(1, "audio_video_fusion"));
                TransitionTo(AutoCutState.FollowingShot);
                return;
            }
        }

        // Record audio spike timestamp and wait for video confirmation
        _audioSpikeDetectedAt = DateTimeOffset.UtcNow;
    }

    /// <summary>
    /// Called periodically (e.g., by a timer) to handle cooldown transitions.
    /// </summary>
    public void Tick()
    {
        if (_state == AutoCutState.Cooldown)
        {
            var elapsed = DateTimeOffset.UtcNow - _stateEnteredAt;
            if (elapsed.TotalSeconds >= _config.CooldownDurationSeconds)
            {
                TransitionTo(AutoCutState.WaitingForSwing);
            }
        }
    }

    private void TransitionTo(AutoCutState newState)
    {
        if (_state == newState) return;

        var oldState = _state;
        _state = newState;
        _stateEnteredAt = DateTimeOffset.UtcNow;

        _logger.LogDebug("Auto-cut state: {OldState} -> {NewState}", oldState, newState);
        StateChanged?.Invoke(this, newState);
    }
}

/// <summary>
/// Event args for auto-cut triggers.
/// </summary>
public class AutoCutEventArgs : EventArgs
{
    /// <summary>Target source index (0 = golfer camera, 1 = simulator).</summary>
    public int TargetSourceIndex { get; }

    /// <summary>Reason for the cut.</summary>
    public string Reason { get; }

    public AutoCutEventArgs(int targetSourceIndex, string reason)
    {
        TargetSourceIndex = targetSourceIndex;
        Reason = reason;
    }
}
