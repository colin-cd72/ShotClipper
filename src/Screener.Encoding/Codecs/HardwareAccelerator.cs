using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Screener.Abstractions.Encoding;

namespace Screener.Encoding.Codecs;

/// <summary>
/// Detects and manages hardware-accelerated video encoders.
/// </summary>
public sealed class HardwareAccelerator
{
    private readonly ILogger<HardwareAccelerator> _logger;
    private readonly List<HardwareAcceleration> _availableEncoders = new();
    private bool _probed;

    public IReadOnlyList<HardwareAcceleration> AvailableEncoders => _availableEncoders;

    public HardwareAccelerator(ILogger<HardwareAccelerator> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Probe for available hardware encoders.
    /// </summary>
    public async Task ProbeEncodersAsync(CancellationToken ct = default)
    {
        if (_probed) return;

        _logger.LogInformation("Probing for hardware encoders...");

        _availableEncoders.Clear();

        // Check for NVENC (NVIDIA)
        if (await ProbeEncoderAsync("h264_nvenc", ct))
        {
            _availableEncoders.Add(HardwareAcceleration.Nvenc);
            _logger.LogInformation("NVENC encoder available");
        }

        // Check for QSV (Intel)
        if (await ProbeEncoderAsync("h264_qsv", ct))
        {
            _availableEncoders.Add(HardwareAcceleration.Qsv);
            _logger.LogInformation("Intel QSV encoder available");
        }

        // Check for AMF (AMD)
        if (await ProbeEncoderAsync("h264_amf", ct))
        {
            _availableEncoders.Add(HardwareAcceleration.Amf);
            _logger.LogInformation("AMD AMF encoder available");
        }

        // Software is always available
        _availableEncoders.Add(HardwareAcceleration.Software);

        _probed = true;

        _logger.LogInformation("Available encoders: {Encoders}",
            string.Join(", ", _availableEncoders));
    }

    private async Task<bool> ProbeEncoderAsync(string encoder, CancellationToken ct)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "ffmpeg",
                Arguments = $"-hide_banner -encoders 2>&1 | findstr {encoder}",
                UseShellExecute = true,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };

            // Alternative: just try to use the encoder
            psi = new ProcessStartInfo
            {
                FileName = "ffmpeg",
                Arguments = $"-f lavfi -i nullsrc=s=1920x1080:d=1 -c:v {encoder} -f null -",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true
            };

            using var process = Process.Start(psi);
            if (process == null) return false;

            var completed = await Task.Run(() => process.WaitForExit(5000), ct);

            if (!completed)
            {
                process.Kill();
                return false;
            }

            return process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Get the best available encoder for a codec.
    /// </summary>
    public string GetEncoderName(VideoCodec codec, HardwareAcceleration preference)
    {
        // If specific preference is available, use it
        if (preference != HardwareAcceleration.Auto && _availableEncoders.Contains(preference))
        {
            return GetEncoderString(codec, preference);
        }

        // Auto-select best available
        foreach (var accel in new[] { HardwareAcceleration.Nvenc, HardwareAcceleration.Qsv, HardwareAcceleration.Amf })
        {
            if (_availableEncoders.Contains(accel))
            {
                return GetEncoderString(codec, accel);
            }
        }

        // Fallback to software
        return GetEncoderString(codec, HardwareAcceleration.Software);
    }

    private static string GetEncoderString(VideoCodec codec, HardwareAcceleration accel)
    {
        return (codec, accel) switch
        {
            (VideoCodec.H264, HardwareAcceleration.Nvenc) => "h264_nvenc",
            (VideoCodec.H264, HardwareAcceleration.Qsv) => "h264_qsv",
            (VideoCodec.H264, HardwareAcceleration.Amf) => "h264_amf",
            (VideoCodec.H264, _) => "libx264",

            (VideoCodec.H265, HardwareAcceleration.Nvenc) => "hevc_nvenc",
            (VideoCodec.H265, HardwareAcceleration.Qsv) => "hevc_qsv",
            (VideoCodec.H265, HardwareAcceleration.Amf) => "hevc_amf",
            (VideoCodec.H265, _) => "libx265",

            (VideoCodec.ProRes, _) => "prores_ks",
            (VideoCodec.DNxHD, _) => "dnxhd",

            _ => "libx264"
        };
    }

    /// <summary>
    /// Get recommended encoding parameters for hardware encoder.
    /// </summary>
    public Dictionary<string, string> GetEncoderParameters(HardwareAcceleration accel, int bitrateMbps, int crf)
    {
        return accel switch
        {
            HardwareAcceleration.Nvenc => new Dictionary<string, string>
            {
                ["preset"] = "p4",
                ["rc"] = "vbr",
                ["b:v"] = $"{bitrateMbps}M",
                ["maxrate"] = $"{bitrateMbps * 1.5}M",
                ["bufsize"] = $"{bitrateMbps * 2}M",
                ["spatial-aq"] = "1",
                ["temporal-aq"] = "1"
            },
            HardwareAcceleration.Qsv => new Dictionary<string, string>
            {
                ["preset"] = "medium",
                ["b:v"] = $"{bitrateMbps}M",
                ["maxrate"] = $"{bitrateMbps * 1.5}M",
                ["bufsize"] = $"{bitrateMbps * 2}M",
                ["look_ahead"] = "1"
            },
            HardwareAcceleration.Amf => new Dictionary<string, string>
            {
                ["quality"] = "balanced",
                ["b:v"] = $"{bitrateMbps}M",
                ["maxrate"] = $"{bitrateMbps * 1.5}M",
                ["bufsize"] = $"{bitrateMbps * 2}M"
            },
            _ => new Dictionary<string, string>
            {
                ["preset"] = "medium",
                ["crf"] = crf.ToString(),
                ["maxrate"] = $"{bitrateMbps}M",
                ["bufsize"] = $"{bitrateMbps * 2}M"
            }
        };
    }
}
