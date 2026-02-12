using Microsoft.Extensions.Logging;
using Screener.Abstractions.Capture;
using Screener.Abstractions.Encoding;
using Screener.Abstractions.Recording;
using Screener.Abstractions.Timecode;
using Screener.Core.Buffers;

namespace Screener.Recording;

/// <summary>
/// Manages recording sessions, coordinating capture and encoding.
/// Supports multiple simultaneous inputs with separate output files.
/// </summary>
public sealed class RecordingService : IRecordingService, IDisposable
{
    private readonly ILogger<RecordingService> _logger;
    private readonly IDeviceManager _deviceManager;
    private readonly Func<IEncodingPipeline> _pipelineFactory;
    private readonly ITimecodeService _timecodeService;

    // Multi-input support
    private readonly List<ActiveInputRecording> _activeInputs = new();
    private RecordingState _state = RecordingState.Stopped;
    private RecordingSession? _currentSession;
    private CancellationTokenSource? _recordingCts;
    private Task? _recordingTask;
    private readonly object _stateLock = new();

    // For single-input backward compatibility
    private ICaptureDevice? _captureDevice;
    private IEncodingPipeline? _encodingPipeline;

    public RecordingState State => _state;
    public RecordingSession? CurrentSession => _currentSession;

    public event EventHandler<RecordingStateChangedEventArgs>? StateChanged;
    public event EventHandler<RecordingProgressEventArgs>? Progress;

    public RecordingService(
        ILogger<RecordingService> logger,
        IDeviceManager deviceManager,
        Func<IEncodingPipeline> pipelineFactory,
        ITimecodeService timecodeService)
    {
        _logger = logger;
        _deviceManager = deviceManager;
        _pipelineFactory = pipelineFactory;
        _timecodeService = timecodeService;
    }

    public async Task<RecordingSession> StartRecordingAsync(RecordingOptions options, CancellationToken ct = default)
    {
        lock (_stateLock)
        {
            if (_state != RecordingState.Stopped)
                throw new InvalidOperationException($"Cannot start recording in state {_state}");

            SetState(RecordingState.Starting);
        }

        try
        {
            // Ensure output directory exists
            Directory.CreateDirectory(options.OutputDirectory);

            // Determine which inputs to use
            var inputConfigs = options.Inputs?.Where(i => i.Enabled).ToList();
            var isMultiInput = inputConfigs != null && inputConfigs.Count > 0;

            // Get all available devices
            var devices = _deviceManager.AvailableDevices;
            if (devices.Count == 0)
            {
                _deviceManager.RefreshDevices();
                devices = _deviceManager.AvailableDevices;
            }

            // Generate base filename
            var baseFilename = GenerateFilenameBase(options.FilenameTemplate, options.Preset, options.Name);

            // Get current timecode from first device
            var firstDevice = devices.FirstOrDefault();
            var startTimecode = firstDevice != null
                ? await _timecodeService.GetCurrentTimecodeAsync(firstDevice.SupportedVideoModes.First().FrameRate, ct)
                : default;

            // Create session
            _currentSession = new RecordingSession
            {
                FilePath = Path.Combine(options.OutputDirectory, baseFilename + ".mp4"),
                StartTimeUtc = DateTime.UtcNow,
                Preset = options.Preset,
                StartTimecode = startTimecode
            };

            _activeInputs.Clear();

            if (isMultiInput)
            {
                // Multi-input recording: set up each enabled input
                foreach (var inputConfig in inputConfigs!)
                {
                    var device = devices.FirstOrDefault(d => d.DeviceId == inputConfig.DeviceId);
                    if (device == null)
                    {
                        _logger.LogWarning("Device not found for input {Index}: {DeviceId}",
                            inputConfig.InputIndex, inputConfig.DeviceId);
                        continue;
                    }

                    // Device must already be capturing (preview manages capture lifecycle)
                    if (device.Status != DeviceStatus.Capturing)
                    {
                        _logger.LogWarning("Device {DeviceId} is not capturing (status: {Status}), skipping input {Index}",
                            inputConfig.DeviceId, device.Status, inputConfig.InputIndex);
                        continue;
                    }

                    // Use the device's current video mode (matches what's actually being captured)
                    var mode = device.CurrentVideoMode;
                    if (mode == null)
                    {
                        _logger.LogWarning("Device {DeviceId} has no current video mode, skipping input {Index}",
                            inputConfig.DeviceId, inputConfig.InputIndex);
                        continue;
                    }

                    // Generate filename with input suffix
                    var inputFilename = baseFilename + inputConfig.FilenameSuffix + ".mp4";
                    var inputPath = Path.Combine(options.OutputDirectory, inputFilename);

                    // Create encoding pipeline (capture is already running via preview)
                    var pipeline = _pipelineFactory();
                    await pipeline.InitializeAsync(new EncodingConfiguration(
                        inputPath,
                        mode,
                        new AudioFormat(48000, 16, 32),
                        options.Preset,
                        HardwareAcceleration.Auto,
                        UseFragmentedMp4: true
                    ), ct);

                    var activeInput = new ActiveInputRecording
                    {
                        Config = inputConfig,
                        Device = device,
                        Pipeline = pipeline,
                        FilePath = inputPath
                    };

                    _activeInputs.Add(activeInput);

                    // Track in session
                    _currentSession.InputSessions.Add(new InputRecordingSession
                    {
                        Input = inputConfig,
                        FilePath = inputPath
                    });

                    _logger.LogInformation("Started recording input {Index} ({Device}) to {FilePath}",
                        inputConfig.InputIndex + 1, inputConfig.DisplayName, inputPath);
                }

                if (_activeInputs.Count == 0)
                    throw new InvalidOperationException("No valid inputs could be started. Ensure preview is active and devices are capturing.");
            }
            else
            {
                // Single-input recording (backward compatibility)
                _captureDevice = firstDevice;
                if (_captureDevice == null)
                    throw new InvalidOperationException("No capture device available");

                // Device must already be capturing (preview manages capture lifecycle)
                if (_captureDevice.Status != DeviceStatus.Capturing)
                    throw new InvalidOperationException("Capture device is not active. Start preview first.");

                // Use the device's current video mode
                var mode = _captureDevice.CurrentVideoMode
                    ?? _captureDevice.SupportedVideoModes.First();

                _encodingPipeline = _pipelineFactory();
                await _encodingPipeline.InitializeAsync(new EncodingConfiguration(
                    _currentSession.FilePath,
                    mode,
                    new AudioFormat(48000, 16, 32),
                    options.Preset,
                    HardwareAcceleration.Auto,
                    UseFragmentedMp4: true
                ), ct);

                _logger.LogInformation("Recording started: {FilePath}", _currentSession.FilePath);
            }

            // Start recording loop
            _recordingCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            _recordingTask = isMultiInput
                ? RunMultiInputRecordingLoopAsync(_recordingCts.Token)
                : RunRecordingLoopAsync(_recordingCts.Token);

            SetState(RecordingState.Recording);

            return _currentSession;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start recording");
            await CleanupActiveInputsAsync();
            SetState(RecordingState.Stopped);
            throw;
        }
    }

    private async Task CleanupActiveInputsAsync()
    {
        foreach (var input in _activeInputs)
        {
            try
            {
                await input.Pipeline.DisposeAsync();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error cleaning up input {Index}", input.Config.InputIndex);
            }
        }
        _activeInputs.Clear();
    }

    private async Task RunMultiInputRecordingLoopAsync(CancellationToken ct)
    {
        // Set up handlers for each input
        foreach (var input in _activeInputs)
        {
            var localInput = input; // Capture for closure

            input.VideoHandler = async (sender, e) =>
            {
                if (_state == RecordingState.Paused) return;

                try
                {
                    await localInput.Pipeline.WriteVideoFrameAsync(e.FrameData, e.Timestamp, ct);
                    localInput.FramesRecorded++;
                }
                catch (Exception ex)
                {
                    localInput.DroppedFrames++;
                    _logger.LogWarning(ex, "Input {Index} frame {Frame} dropped",
                        localInput.Config.InputIndex + 1, e.FrameNumber);
                }
            };

            input.AudioHandler = async (sender, e) =>
            {
                if (_state == RecordingState.Paused) return;

                try
                {
                    await localInput.Pipeline.WriteAudioSamplesAsync(e.SampleData, e.Timestamp, ct);
                }
                catch { }
            };

            input.Device.VideoFrameReceived += input.VideoHandler;
            input.Device.AudioSamplesReceived += input.AudioHandler;
        }

        try
        {
            // Report progress periodically
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(1000, ct);
                ReportMultiInputProgress();
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            // Remove handlers
            foreach (var input in _activeInputs)
            {
                if (input.VideoHandler != null)
                    input.Device.VideoFrameReceived -= input.VideoHandler;
                if (input.AudioHandler != null)
                    input.Device.AudioSamplesReceived -= input.AudioHandler;
            }
        }
    }

    private void ReportMultiInputProgress()
    {
        if (_currentSession == null || _activeInputs.Count == 0)
            return;

        // Aggregate stats from all inputs
        long totalFrames = 0;
        int totalDropped = 0;
        long totalSize = 0;

        foreach (var input in _activeInputs)
        {
            totalFrames += input.FramesRecorded;
            totalDropped += input.DroppedFrames;

            if (File.Exists(input.FilePath))
                totalSize += new FileInfo(input.FilePath).Length;

            // Update per-input session
            var inputSession = _currentSession.InputSessions
                .FirstOrDefault(s => s.Input.InputIndex == input.Config.InputIndex);
            if (inputSession != null)
            {
                inputSession.FramesRecorded = input.FramesRecorded;
                inputSession.DroppedFrames = input.DroppedFrames;
                inputSession.FileSizeBytes = File.Exists(input.FilePath)
                    ? new FileInfo(input.FilePath).Length : 0;
            }
        }

        var avgFrameRate = _activeInputs.First().Device.CurrentVideoMode?.FrameRate.Value ?? 30;
        var avgFramesPerInput = totalFrames / _activeInputs.Count;
        var duration = TimeSpan.FromSeconds(avgFramesPerInput / avgFrameRate);
        var bitrate = duration.TotalSeconds > 0 ? totalSize * 8.0 / duration.TotalSeconds / 1_000_000 : 0;

        Progress?.Invoke(this, new RecordingProgressEventArgs
        {
            Session = _currentSession,
            Duration = duration,
            FileSizeBytes = totalSize,
            FramesRecorded = totalFrames,
            DroppedFrames = totalDropped,
            CurrentBitrateMbps = bitrate
        });
    }

    public async Task StopRecordingAsync()
    {
        lock (_stateLock)
        {
            if (_state is not (RecordingState.Recording or RecordingState.Paused))
                return;

            SetState(RecordingState.Stopping);
        }

        try
        {
            _recordingCts?.Cancel();

            if (_recordingTask != null)
            {
                await _recordingTask;
            }

            // Handle multi-input cleanup (don't stop capture - preview manages device lifecycle)
            if (_activeInputs.Count > 0)
            {
                foreach (var input in _activeInputs)
                {
                    try
                    {
                        await input.Pipeline.FinalizeAsync();
                        await input.Pipeline.DisposeAsync();

                        _logger.LogInformation("Stopped input {Index}: {Frames} frames, {Dropped} dropped",
                            input.Config.InputIndex + 1, input.FramesRecorded, input.DroppedFrames);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error stopping input {Index}", input.Config.InputIndex);
                    }
                }
                _activeInputs.Clear();
            }
            else
            {
                // Single-input cleanup (don't stop capture - preview manages device lifecycle)
                if (_encodingPipeline != null)
                {
                    await _encodingPipeline.FinalizeAsync();
                    await _encodingPipeline.DisposeAsync();
                }
            }

            if (_currentSession != null)
            {
                _currentSession.EndTimeUtc = DateTime.UtcNow;

                // Calculate total file size
                long totalSize = 0;
                if (_currentSession.InputSessions.Count > 0)
                {
                    foreach (var inputSession in _currentSession.InputSessions)
                    {
                        if (File.Exists(inputSession.FilePath))
                            totalSize += new FileInfo(inputSession.FilePath).Length;
                    }
                }
                else if (File.Exists(_currentSession.FilePath))
                {
                    totalSize = new FileInfo(_currentSession.FilePath).Length;
                }
                _currentSession.FileSizeBytes = totalSize;
            }

            _logger.LogInformation("Recording stopped: {Duration}, {Size} bytes, {InputCount} input(s)",
                _currentSession?.Duration, _currentSession?.FileSizeBytes,
                _currentSession?.InputSessions.Count ?? 1);
        }
        finally
        {
            // Fire state change before clearing session so handlers can access it
            SetState(RecordingState.Stopped);

            _currentSession = null;
            _encodingPipeline = null;
            _recordingCts?.Dispose();
            _recordingCts = null;
        }
    }

    public Task PauseRecordingAsync()
    {
        lock (_stateLock)
        {
            if (_state != RecordingState.Recording)
                return Task.CompletedTask;

            SetState(RecordingState.Paused);
            _logger.LogInformation("Recording paused");
        }

        return Task.CompletedTask;
    }

    public Task ResumeRecordingAsync()
    {
        lock (_stateLock)
        {
            if (_state != RecordingState.Paused)
                return Task.CompletedTask;

            SetState(RecordingState.Recording);
            _logger.LogInformation("Recording resumed");
        }

        return Task.CompletedTask;
    }

    private async Task RunRecordingLoopAsync(CancellationToken ct)
    {
        if (_captureDevice == null || _encodingPipeline == null || _currentSession == null)
            return;

        long framesRecorded = 0;
        int droppedFrames = 0;

        // Subscribe to frame events
        var frameHandler = new EventHandler<VideoFrameEventArgs>(async (sender, e) =>
        {
            if (_state == RecordingState.Paused) return;

            try
            {
                await _encodingPipeline.WriteVideoFrameAsync(e.FrameData, e.Timestamp, ct);
                framesRecorded++;

                // Update progress every second
                if (framesRecorded % 30 == 0)
                {
                    ReportProgress(framesRecorded, droppedFrames);
                }
            }
            catch (Exception ex)
            {
                droppedFrames++;
                _logger.LogWarning(ex, "Frame {Frame} dropped", e.FrameNumber);
            }
        });

        var audioHandler = new EventHandler<AudioSamplesEventArgs>(async (sender, e) =>
        {
            if (_state == RecordingState.Paused) return;

            try
            {
                await _encodingPipeline.WriteAudioSamplesAsync(e.SampleData, e.Timestamp, ct);
            }
            catch
            {
            }
        });

        _captureDevice.VideoFrameReceived += frameHandler;
        _captureDevice.AudioSamplesReceived += audioHandler;

        try
        {
            // Wait until cancelled
            await Task.Delay(Timeout.Infinite, ct);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            _captureDevice.VideoFrameReceived -= frameHandler;
            _captureDevice.AudioSamplesReceived -= audioHandler;
        }
    }

    private void ReportProgress(long framesRecorded, int droppedFrames)
    {
        if (_currentSession == null || _captureDevice?.CurrentVideoMode == null)
            return;

        var duration = TimeSpan.FromSeconds(framesRecorded / _captureDevice.CurrentVideoMode.FrameRate.Value);
        var fileSize = File.Exists(_currentSession.FilePath)
            ? new FileInfo(_currentSession.FilePath).Length
            : 0;

        var bitrate = duration.TotalSeconds > 0 ? fileSize * 8.0 / duration.TotalSeconds / 1_000_000 : 0;

        Progress?.Invoke(this, new RecordingProgressEventArgs
        {
            Session = _currentSession,
            Duration = duration,
            FileSizeBytes = fileSize,
            FramesRecorded = framesRecorded,
            DroppedFrames = droppedFrames,
            CurrentBitrateMbps = bitrate
        });
    }

    private string GenerateFilenameBase(string template, EncodingPreset preset, string? name = null)
    {
        var now = DateTime.Now;

        var result = template
            .Replace("{date}", now.ToString("yyyy-MM-dd"))
            .Replace("{date:yyyy-MM-dd}", now.ToString("yyyy-MM-dd"))
            .Replace("{time}", now.ToString("HH-mm-ss"))
            .Replace("{time:HH-mm-ss}", now.ToString("HH-mm-ss"))
            .Replace("{preset}", preset.Name)
            .Replace("{datetime}", now.ToString("yyyyMMdd_HHmmss"));

        // Replace {name} with the recording name, or remove it if not provided
        if (!string.IsNullOrWhiteSpace(name))
        {
            // Sanitize name for filesystem
            var sanitizedName = SanitizeFilename(name);
            result = result.Replace("{name}", sanitizedName);
        }
        else
        {
            // Remove {name}_ or _{name} or {name} patterns
            result = result.Replace("{name}_", "")
                          .Replace("_{name}", "")
                          .Replace("{name}", "");
        }

        return result;
    }

    private string GenerateFilename(string template, EncodingPreset preset, string? name = null)
    {
        return GenerateFilenameBase(template, preset, name) + ".mp4";
    }

    private static string SanitizeFilename(string filename)
    {
        var invalidChars = Path.GetInvalidFileNameChars();
        var sanitized = filename;
        foreach (var c in invalidChars)
        {
            sanitized = sanitized.Replace(c, '_');
        }
        return sanitized.Replace(' ', '_');
    }

    private void SetState(RecordingState newState)
    {
        var oldState = _state;
        _state = newState;

        if (oldState != newState)
        {
            StateChanged?.Invoke(this, new RecordingStateChangedEventArgs
            {
                OldState = oldState,
                NewState = newState,
                Session = _currentSession
            });
        }
    }

    public void Dispose()
    {
        _recordingCts?.Cancel();
        _recordingCts?.Dispose();
        // Don't dispose capture devices - they're managed by the DeviceManager
        _activeInputs.Clear();
    }
}

/// <summary>
/// Tracks an active recording for a single input.
/// </summary>
internal class ActiveInputRecording
{
    public required InputConfiguration Config { get; init; }
    public required ICaptureDevice Device { get; init; }
    public required IEncodingPipeline Pipeline { get; init; }
    public required string FilePath { get; init; }
    public long FramesRecorded { get; set; }
    public int DroppedFrames { get; set; }
    public EventHandler<VideoFrameEventArgs>? VideoHandler { get; set; }
    public EventHandler<AudioSamplesEventArgs>? AudioHandler { get; set; }
}
