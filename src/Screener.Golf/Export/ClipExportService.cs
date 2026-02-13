using System.IO;
using Microsoft.Extensions.Logging;
using Polly;
using Polly.Retry;
using Screener.Abstractions.Clipping;
using Screener.Golf.Models;
using Screener.Golf.Overlays;
using Screener.Golf.Persistence;
using Screener.Golf.Switching;

namespace Screener.Golf.Export;

/// <summary>
/// Orchestrates the end-to-end clip export pipeline:
/// SequenceRecorder → ClippingService → OverlayCompositor.
/// </summary>
public class ClipExportService
{
    private readonly IClippingService _clippingService;
    private readonly OverlayCompositor _overlayCompositor;
    private readonly OverlayRepository _overlayRepository;
    private readonly SessionRepository _sessionRepository;
    private readonly GolfSession _golfSession;
    private readonly ILogger<ClipExportService> _logger;
    private readonly ResiliencePipeline _retryPipeline;

    /// <summary>Fired when a clip export completes (success or failure).</summary>
    public event EventHandler<ExportCompletedEventArgs>? ExportCompleted;

    /// <summary>Fired when export status changes for progress tracking.</summary>
    public event EventHandler<ExportProgressEventArgs>? ExportProgressChanged;

    public ClipExportService(
        IClippingService clippingService,
        OverlayCompositor overlayCompositor,
        OverlayRepository overlayRepository,
        SessionRepository sessionRepository,
        GolfSession golfSession,
        ILogger<ClipExportService> logger)
    {
        _clippingService = clippingService;
        _overlayCompositor = overlayCompositor;
        _overlayRepository = overlayRepository;
        _sessionRepository = sessionRepository;
        _golfSession = golfSession;
        _logger = logger;

        _retryPipeline = new ResiliencePipelineBuilder()
            .AddRetry(new RetryStrategyOptions
            {
                MaxRetryAttempts = 3,
                BackoffType = DelayBackoffType.Exponential,
                Delay = TimeSpan.FromSeconds(2),
                ShouldHandle = new PredicateBuilder().Handle<InvalidOperationException>(),
                OnRetry = args =>
                {
                    _logger.LogWarning("Retry attempt {Attempt} after {Delay}s",
                        args.AttemptNumber + 1, args.RetryDelay.TotalSeconds);
                    return ValueTask.CompletedTask;
                }
            })
            .Build();
    }

    /// <summary>
    /// Subscribe to a SequenceRecorder's SequenceCompleted event to auto-export clips.
    /// </summary>
    public void WireUp(SequenceRecorder recorder)
    {
        recorder.SequenceCompleted += OnSequenceCompleted;
    }

    private void OnSequenceCompleted(object? sender, SwingSequence sequence)
    {
        // Run export on background thread to avoid blocking the UI
        Task.Run(async () =>
        {
            try
            {
                await ExportSequenceAsync(sequence);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to export swing #{Num}", sequence.SequenceNumber);
            }
        });
    }

    private async Task ExportSequenceAsync(SwingSequence sequence)
    {
        var session = _golfSession.CurrentSession;
        if (session == null)
        {
            _logger.LogWarning("No active session for export of swing #{Num}", sequence.SequenceNumber);
            return;
        }

        if (string.IsNullOrEmpty(session.Source2RecordingPath))
        {
            _logger.LogWarning("No Source2 recording path set for swing #{Num}", sequence.SequenceNumber);
            return;
        }

        if (!sequence.OutPointTicks.HasValue)
        {
            _logger.LogWarning("Swing #{Num} has no out point, skipping export", sequence.SequenceNumber);
            return;
        }

        var swingNum = sequence.SequenceNumber;

        try
        {
            // Update status: extracting
            RaiseProgress(swingNum, "Extracting");
            await _sessionRepository.UpdateExportStatusAsync(sequence.Id, "extracting");

            // Convert UTC ticks to recording-relative TimeSpan offsets
            var inOffset = TimeSpan.FromTicks(sequence.InPointTicks - session.StartedAt.UtcTicks);
            var outOffset = TimeSpan.FromTicks(sequence.OutPointTicks.Value - session.StartedAt.UtcTicks);

            // Clamp to non-negative
            if (inOffset < TimeSpan.Zero) inOffset = TimeSpan.Zero;
            if (outOffset < TimeSpan.Zero) outOffset = TimeSpan.Zero;

            // Set active recording to the simulator source
            _clippingService.SetActiveRecording(session.Source2RecordingPath!);

            // Create clip definition
            var clipName = $"Swing_{swingNum:D3}_{session.GolferDisplayName ?? "Unknown"}";
            var clip = await _clippingService.CreateClipAsync(clipName, inOffset, outOffset);

            // Extract base clip with retry
            var outputDir = Path.Combine(
                Path.GetDirectoryName(session.Source2RecordingPath)!,
                "Swings");
            var options = new ClipExtractionOptions(outputDir, "{name}");
            int extractAttempt = 0;
            var basePath = await _retryPipeline.ExecuteAsync(async ct =>
            {
                extractAttempt++;
                if (extractAttempt > 1)
                    RaiseProgress(swingNum, $"Retrying extraction (attempt {extractAttempt})");
                return await _clippingService.ExtractClipAsync(clip, options);
            });

            _logger.LogInformation("Base clip extracted for swing #{Num}: {Path}", swingNum, basePath);

            // Update status: applying overlays
            RaiseProgress(swingNum, "Applying Overlays");

            // Load overlay configs
            var logoBugRecord = await _overlayRepository.GetDefaultAsync("logo_bug");
            var lowerThirdRecord = await _overlayRepository.GetDefaultAsync("lower_third");

            var logoBug = logoBugRecord?.DeserializeConfig<LogoBugConfig>();
            var lowerThird = lowerThirdRecord?.DeserializeConfig<LowerThirdConfig>();

            // Determine final output path
            var finalPath = Path.Combine(outputDir, $"{clipName}_final.mp4");

            bool hasOverlays = logoBug?.LogoPath != null || lowerThird?.Enabled == true;

            if (hasOverlays)
            {
                int overlayAttempt = 0;
                await _retryPipeline.ExecuteAsync(async ct =>
                {
                    overlayAttempt++;
                    if (overlayAttempt > 1)
                        RaiseProgress(swingNum, $"Retrying overlays (attempt {overlayAttempt})");
                    await _overlayCompositor.ExportWithOverlaysAsync(
                        basePath,
                        finalPath,
                        session.GolferDisplayName,
                        logoBug,
                        lowerThird,
                        ct);
                });
            }
            else
            {
                // No overlays configured — the base clip is the final clip
                finalPath = basePath;
            }

            // Update DB status
            await _sessionRepository.UpdateExportStatusAsync(sequence.Id, "completed", finalPath);

            _logger.LogInformation("Export complete for swing #{Num}: {Path}", swingNum, finalPath);

            RaiseProgress(swingNum, "Complete");
            ExportCompleted?.Invoke(this, new ExportCompletedEventArgs
            {
                SwingNumber = swingNum,
                OutputPath = finalPath,
                Duration = sequence.Duration,
                Success = true
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Export failed for swing #{Num}", swingNum);
            await _sessionRepository.UpdateExportStatusAsync(sequence.Id, "failed");

            RaiseProgress(swingNum, "Failed");
            ExportCompleted?.Invoke(this, new ExportCompletedEventArgs
            {
                SwingNumber = swingNum,
                Success = false,
                ErrorMessage = ex.Message
            });
        }
    }

    private void RaiseProgress(int swingNumber, string status)
    {
        ExportProgressChanged?.Invoke(this, new ExportProgressEventArgs
        {
            SwingNumber = swingNumber,
            Status = status
        });
    }
}

public class ExportCompletedEventArgs : EventArgs
{
    public int SwingNumber { get; init; }
    public string? OutputPath { get; init; }
    public TimeSpan? Duration { get; init; }
    public bool Success { get; init; }
    public string? ErrorMessage { get; init; }
}

public class ExportProgressEventArgs : EventArgs
{
    public int SwingNumber { get; init; }
    public string Status { get; init; } = string.Empty;
}
