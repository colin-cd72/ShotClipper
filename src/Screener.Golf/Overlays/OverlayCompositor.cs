using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Screener.Golf.Overlays;

/// <summary>
/// Builds FFmpeg filter_complex arguments for compositing logo bug and lower third
/// onto exported clip files.
/// </summary>
public class OverlayCompositor
{
    private readonly ILogger<OverlayCompositor> _logger;

    public OverlayCompositor(ILogger<OverlayCompositor> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Export a clip with overlays baked in.
    /// </summary>
    /// <param name="inputPath">Source clip file path.</param>
    /// <param name="outputPath">Output file path.</param>
    /// <param name="golferName">Golfer display name for lower third.</param>
    /// <param name="logoBug">Logo bug configuration (null to skip).</param>
    /// <param name="lowerThird">Lower third configuration (null to skip).</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task ExportWithOverlaysAsync(
        string inputPath,
        string outputPath,
        string? golferName,
        LogoBugConfig? logoBug,
        LowerThirdConfig? lowerThird,
        CancellationToken ct = default)
    {
        var args = BuildFfmpegArgs(inputPath, outputPath, golferName, logoBug, lowerThird);

        _logger.LogInformation("Exporting clip with overlays: {Output}", outputPath);
        _logger.LogDebug("FFmpeg args: {Args}", args);

        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);

        var psi = new ProcessStartInfo
        {
            FileName = "ffmpeg",
            Arguments = args,
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
            _logger.LogError("FFmpeg overlay export failed: {Stderr}", stderr);
            throw new InvalidOperationException($"FFmpeg overlay export failed: {stderr}");
        }

        _logger.LogInformation("Overlay export complete: {Output}", outputPath);
    }

    /// <summary>
    /// Build the FFmpeg arguments string for overlay compositing.
    /// </summary>
    internal string BuildFfmpegArgs(
        string inputPath,
        string outputPath,
        string? golferName,
        LogoBugConfig? logoBug,
        LowerThirdConfig? lowerThird)
    {
        bool hasLogo = logoBug?.LogoPath != null && File.Exists(logoBug.LogoPath);
        bool hasLowerThird = lowerThird?.Enabled == true && !string.IsNullOrEmpty(golferName);

        // No overlays - just stream copy
        if (!hasLogo && !hasLowerThird)
        {
            return $"-y -i \"{inputPath}\" -c copy \"{outputPath}\"";
        }

        var filters = new List<string>();
        string currentStream = "[0:v]";
        int inputIndex = 1;

        var inputArgs = $"-y -i \"{inputPath}\"";

        // Logo bug
        if (hasLogo)
        {
            inputArgs += $" -i \"{logoBug!.LogoPath}\"";

            var scaleFilter = logoBug.Scale != 1.0
                ? $"[{inputIndex}:v]scale=iw*{logoBug.Scale:F2}:ih*{logoBug.Scale:F2}[logo_scaled];{currentStream}[logo_scaled]"
                : $"{currentStream}[{inputIndex}:v]";

            var overlayPos = logoBug.GetOverlayPosition();

            if (logoBug.Scale != 1.0)
            {
                filters.Add($"[{inputIndex}:v]scale=iw*{logoBug.Scale:F2}:ih*{logoBug.Scale:F2}[logo_scaled]");
                filters.Add($"{currentStream}[logo_scaled]overlay={overlayPos}:format=auto[with_logo]");
            }
            else
            {
                filters.Add($"{currentStream}[{inputIndex}:v]overlay={overlayPos}:format=auto[with_logo]");
            }

            currentStream = "[with_logo]";
            inputIndex++;
        }

        // Lower third (drawtext)
        if (hasLowerThird)
        {
            var drawtextFilter = lowerThird!.BuildDrawtextFilter(golferName!);
            var outputLabel = "[final]";
            filters.Add($"{currentStream}{drawtextFilter}{outputLabel}");
            currentStream = outputLabel;
        }

        var filterComplex = string.Join(";", filters);

        return $"{inputArgs} -filter_complex \"{filterComplex}\" -map \"{currentStream}\" -map 0:a? " +
               $"-c:v libx264 -crf 18 -preset medium -c:a aac -movflags +faststart \"{outputPath}\"";
    }
}
