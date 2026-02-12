using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace Screener.Capture.Srt;

/// <summary>
/// Shared helper for launching and managing FFmpeg processes.
/// </summary>
public static class FfmpegProcessHelper
{
    private static readonly Regex ResolutionRegex = new(
        @"Stream\s+#\d+:\d+.*Video:.*?\s(\d{2,5})x(\d{2,5})",
        RegexOptions.Compiled);

    /// <summary>
    /// Finds the FFmpeg executable path.
    /// Checks tools\ffmpeg\ffmpeg.exe relative to the application directory first,
    /// then falls back to ffmpeg on the system PATH.
    /// </summary>
    public static string FindFfmpeg()
    {
        // Check bundled location first
        var bundledPath = Path.Combine(
            AppDomain.CurrentDomain.BaseDirectory,
            "tools", "ffmpeg", "ffmpeg.exe");

        if (File.Exists(bundledPath))
            return bundledPath;

        // Check if ffmpeg is available on PATH
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "ffmpeg",
                Arguments = "-version",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            using var probe = Process.Start(psi);
            if (probe != null)
            {
                probe.WaitForExit(5000);
                if (probe.ExitCode == 0)
                    return "ffmpeg";
            }
        }
        catch
        {
            // Not on PATH
        }

        throw new FileNotFoundException(
            "FFmpeg not found. Place ffmpeg.exe in tools\\ffmpeg\\ relative to the application " +
            "directory, or add it to the system PATH.");
    }

    /// <summary>
    /// Launches an FFmpeg process with the specified arguments.
    /// </summary>
    /// <param name="arguments">FFmpeg command-line arguments.</param>
    /// <param name="redirectStdin">Whether to redirect standard input for piping data in.</param>
    /// <param name="redirectStdout">Whether to redirect standard output for reading data out.</param>
    /// <param name="logger">Optional logger for stderr output.</param>
    /// <returns>The started FFmpeg process.</returns>
    public static async Task<Process> LaunchAsync(
        string arguments,
        bool redirectStdin,
        bool redirectStdout,
        ILogger? logger = null)
    {
        var ffmpegPath = FindFfmpeg();

        logger?.LogInformation("Launching FFmpeg: {Path} {Args}", ffmpegPath, arguments);

        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = ffmpegPath,
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = redirectStdin,
                RedirectStandardOutput = redirectStdout,
                RedirectStandardError = true
            },
            EnableRaisingEvents = true
        };

        if (logger != null)
        {
            process.ErrorDataReceived += (_, e) =>
            {
                if (string.IsNullOrEmpty(e.Data)) return;

                if (e.Data.Contains("error", StringComparison.OrdinalIgnoreCase))
                    logger.LogError("FFmpeg: {Data}", e.Data);
                else
                    logger.LogDebug("FFmpeg: {Data}", e.Data);
            };
        }

        process.Start();
        process.BeginErrorReadLine();

        // Small yield to let the process spin up
        await Task.Delay(50);

        return process;
    }

    /// <summary>
    /// Parses a video resolution from an FFmpeg stderr line containing stream information.
    /// Matches patterns like "Stream #0:0: Video: h264 ... 1920x1080".
    /// </summary>
    /// <param name="line">A line from FFmpeg stderr output.</param>
    /// <returns>The parsed width and height, or null if the line does not contain resolution info.</returns>
    public static (int Width, int Height)? ParseResolutionFromStderr(string line)
    {
        var match = ResolutionRegex.Match(line);
        if (match.Success &&
            int.TryParse(match.Groups[1].Value, out var width) &&
            int.TryParse(match.Groups[2].Value, out var height))
        {
            return (width, height);
        }

        return null;
    }

    /// <summary>
    /// Attempts to stop an FFmpeg process gracefully by closing stdin,
    /// then waiting for the specified timeout before killing.
    /// </summary>
    /// <param name="process">The FFmpeg process to stop.</param>
    /// <param name="timeout">How long to wait for graceful exit before killing.</param>
    public static async Task StopGracefully(Process process, TimeSpan timeout)
    {
        if (process.HasExited)
            return;

        try
        {
            // Close stdin to signal FFmpeg to finalize and exit
            if (process.StartInfo.RedirectStandardInput)
            {
                try
                {
                    process.StandardInput.Close();
                }
                catch
                {
                    // stdin may already be closed
                }
            }

            // Wait for graceful exit
            var exited = await Task.Run(() => process.WaitForExit((int)timeout.TotalMilliseconds));

            if (!exited)
            {
                process.Kill();
            }
        }
        catch
        {
            // Process may have already exited
            if (!process.HasExited)
            {
                try { process.Kill(); }
                catch { }
            }
        }
    }
}
