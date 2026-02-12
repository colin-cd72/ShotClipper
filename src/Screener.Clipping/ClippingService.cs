using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Screener.Abstractions.Clipping;
using Screener.Abstractions.Encoding;
using Screener.Abstractions.Recording;
using Screener.Abstractions.Timecode;

namespace Screener.Clipping;

/// <summary>
/// Manages live clip marking and extraction during recording.
/// </summary>
public sealed class ClippingService : IClippingService
{
    private readonly ILogger<ClippingService> _logger;
    private readonly List<ClipMarker> _markers = new();
    private readonly List<ClipDefinition> _pendingClips = new();
    private readonly SemaphoreSlim _extractionSemaphore;
    private ClipMarker? _pendingInPoint;
    private string? _activeRecordingPath;

    public IReadOnlyList<ClipMarker> Markers => _markers.ToList();
    public ClipMarker? PendingInPoint => _pendingInPoint;

    public event EventHandler<ClipMarkerEventArgs>? MarkerAdded;
    public event EventHandler<ClipExtractionProgressEventArgs>? ExtractionProgress;

    public ClippingService(ILogger<ClippingService> logger, int maxConcurrentExtractions = 2)
    {
        _logger = logger;
        _extractionSemaphore = new SemaphoreSlim(maxConcurrentExtractions);
    }

    /// <summary>
    /// Set the active recording file path for clip extraction.
    /// </summary>
    public void SetActiveRecording(string filePath)
    {
        _activeRecordingPath = filePath;
    }

    public ClipMarker SetInPoint(TimeSpan position, Smpte12MTimecode timecode, string? name = null)
    {
        _pendingInPoint = new ClipMarker(MarkerType.In, position, timecode, name);
        _markers.Add(_pendingInPoint);

        _logger.LogInformation("In point set at {Position} ({Timecode})", position, timecode);

        MarkerAdded?.Invoke(this, new ClipMarkerEventArgs { Marker = _pendingInPoint });

        return _pendingInPoint;
    }

    public ClipMarker SetOutPoint(TimeSpan position, Smpte12MTimecode timecode, string? name = null)
    {
        var outMarker = new ClipMarker(MarkerType.Out, position, timecode, name);
        _markers.Add(outMarker);

        _logger.LogInformation("Out point set at {Position} ({Timecode})", position, timecode);

        MarkerAdded?.Invoke(this, new ClipMarkerEventArgs { Marker = outMarker });

        // If we have a pending in point, automatically create clip definition
        if (_pendingInPoint != null && position > _pendingInPoint.Position)
        {
            var clipName = name ?? $"Clip_{_markers.Count / 2}";

            _ = CreateClipAsync(clipName, _pendingInPoint.Position, position);

            _pendingInPoint = null;
        }

        return outMarker;
    }

    public ClipMarker AddChapterMarker(TimeSpan position, Smpte12MTimecode timecode, string name)
    {
        var marker = new ClipMarker(MarkerType.Chapter, position, timecode, name);
        _markers.Add(marker);

        _logger.LogInformation("Chapter marker added: {Name} at {Position}", name, position);

        MarkerAdded?.Invoke(this, new ClipMarkerEventArgs { Marker = marker });

        return marker;
    }

    public async Task<ClipDefinition> CreateClipAsync(string name, TimeSpan inPoint, TimeSpan outPoint, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(_activeRecordingPath))
            throw new InvalidOperationException("No active recording");

        var clip = new ClipDefinition(
            Guid.NewGuid(),
            name,
            inPoint,
            outPoint,
            _activeRecordingPath,
            new Dictionary<string, string>
            {
                ["CreatedAt"] = DateTime.UtcNow.ToString("O")
            });

        _pendingClips.Add(clip);

        _logger.LogInformation("Clip created: {Name}, Duration: {Duration}", name, clip.Duration);

        return clip;
    }

    public async Task<string> ExtractClipAsync(ClipDefinition clip, ClipExtractionOptions options, CancellationToken ct = default)
    {
        await _extractionSemaphore.WaitAsync(ct);

        try
        {
            // Generate output filename
            var filename = GenerateClipFilename(options.FilenameTemplate, clip);
            var outputPath = Path.Combine(options.OutputDirectory, filename);

            Directory.CreateDirectory(options.OutputDirectory);

            ReportProgress(clip, 0, ClipExtractionStatus.Extracting);

            if (options.TranscodePreset == null)
            {
                // Stream copy (fast)
                await ExtractWithStreamCopyAsync(clip, outputPath, ct);
            }
            else
            {
                // Transcode
                await ExtractWithTranscodeAsync(clip, outputPath, options.TranscodePreset, ct);
            }

            ReportProgress(clip, 100, ClipExtractionStatus.Completed, outputPath);

            _logger.LogInformation("Clip extracted: {OutputPath}", outputPath);

            return outputPath;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to extract clip {ClipName}", clip.Name);
            ReportProgress(clip, 0, ClipExtractionStatus.Failed, errorMessage: ex.Message);
            throw;
        }
        finally
        {
            _extractionSemaphore.Release();
        }
    }

    private async Task ExtractWithStreamCopyAsync(ClipDefinition clip, string outputPath, CancellationToken ct)
    {
        var args = $"-y -ss {clip.InPoint.TotalSeconds:F3} -i \"{clip.SourceFilePath}\" " +
                   $"-t {clip.Duration.TotalSeconds:F3} -c copy -avoid_negative_ts make_zero \"{outputPath}\"";

        await RunFfmpegAsync(args, ct);
    }

    private async Task ExtractWithTranscodeAsync(ClipDefinition clip, string outputPath, EncodingPreset preset, CancellationToken ct)
    {
        var encoder = preset.VideoCodec == VideoCodec.H264 ? "libx264" : "libx265";
        var args = $"-y -ss {clip.InPoint.TotalSeconds:F3} -i \"{clip.SourceFilePath}\" " +
                   $"-t {clip.Duration.TotalSeconds:F3} " +
                   $"-c:v {encoder} -crf {preset.CrfValue} -preset medium " +
                   $"-c:a aac -b:a {preset.AudioBitrateKbps}k " +
                   $"-movflags +faststart \"{outputPath}\"";

        await RunFfmpegAsync(args, ct);
    }

    private async Task RunFfmpegAsync(string arguments, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "ffmpeg",
            Arguments = arguments,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true
        };

        using var process = Process.Start(psi);
        if (process == null)
            throw new InvalidOperationException("Failed to start FFmpeg");

        var stderr = await process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"FFmpeg failed: {stderr}");
        }
    }

    private string GenerateClipFilename(string template, ClipDefinition clip)
    {
        var now = DateTime.Now;

        var filename = template
            .Replace("{name}", clip.Name)
            .Replace("{date}", now.ToString("yyyy-MM-dd"))
            .Replace("{time}", now.ToString("HH-mm-ss"))
            .Replace("{duration}", clip.Duration.ToString(@"hh\-mm\-ss"))
            .Replace("{in}", clip.InPoint.ToString(@"hh\-mm\-ss"))
            .Replace("{out}", clip.OutPoint.ToString(@"hh\-mm\-ss"));

        // Sanitize
        foreach (var c in Path.GetInvalidFileNameChars())
        {
            filename = filename.Replace(c, '_');
        }

        if (!filename.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase))
        {
            filename += ".mp4";
        }

        return filename;
    }

    private void ReportProgress(ClipDefinition clip, double progress, ClipExtractionStatus status,
        string? outputPath = null, string? errorMessage = null)
    {
        ExtractionProgress?.Invoke(this, new ClipExtractionProgressEventArgs
        {
            Clip = clip,
            Progress = progress,
            Status = status,
            OutputPath = outputPath,
            ErrorMessage = errorMessage
        });
    }

    public void ClearMarkers()
    {
        _markers.Clear();
        _pendingInPoint = null;
        _pendingClips.Clear();
    }
}
