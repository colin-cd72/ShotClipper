using System.Text.RegularExpressions;
using Screener.Abstractions.Encoding;
using Screener.Abstractions.Timecode;

namespace Screener.Recording;

/// <summary>
/// Generates filenames from templates with variable substitution.
/// </summary>
public sealed class FilenameGenerator
{
    private static readonly Regex VariablePattern = new(@"\{(\w+)(?::([^}]+))?\}", RegexOptions.Compiled);

    private readonly Dictionary<string, Func<string?, string>> _builtInVariables;
    private int _counter;
    private readonly object _counterLock = new();

    public FilenameGenerator()
    {
        var now = DateTime.Now;

        _builtInVariables = new Dictionary<string, Func<string?, string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["date"] = format => now.ToString(format ?? "yyyy-MM-dd"),
            ["time"] = format => now.ToString(format ?? "HH-mm-ss"),
            ["datetime"] = format => now.ToString(format ?? "yyyyMMdd_HHmmss"),
            ["year"] = _ => now.Year.ToString(),
            ["month"] = _ => now.Month.ToString("D2"),
            ["day"] = _ => now.Day.ToString("D2"),
            ["hour"] = _ => now.Hour.ToString("D2"),
            ["minute"] = _ => now.Minute.ToString("D2"),
            ["second"] = _ => now.Second.ToString("D2"),
            ["counter"] = format => GetNextCounter(format),
            ["guid"] = _ => Guid.NewGuid().ToString("N")[..8],
            ["hostname"] = _ => Environment.MachineName,
            ["username"] = _ => Environment.UserName,
        };
    }

    /// <summary>
    /// Generate a filename from the template.
    /// </summary>
    public string Generate(
        string template,
        EncodingPreset? preset = null,
        Smpte12MTimecode? timecode = null,
        Dictionary<string, string>? customVariables = null)
    {
        // Refresh time for this generation
        var now = DateTime.Now;
        _builtInVariables["date"] = format => now.ToString(format ?? "yyyy-MM-dd");
        _builtInVariables["time"] = format => now.ToString(format ?? "HH-mm-ss");
        _builtInVariables["datetime"] = format => now.ToString(format ?? "yyyyMMdd_HHmmss");
        _builtInVariables["year"] = _ => now.Year.ToString();
        _builtInVariables["month"] = _ => now.Month.ToString("D2");
        _builtInVariables["day"] = _ => now.Day.ToString("D2");
        _builtInVariables["hour"] = _ => now.Hour.ToString("D2");
        _builtInVariables["minute"] = _ => now.Minute.ToString("D2");
        _builtInVariables["second"] = _ => now.Second.ToString("D2");

        var result = VariablePattern.Replace(template, match =>
        {
            var variableName = match.Groups[1].Value;
            var format = match.Groups[2].Success ? match.Groups[2].Value : null;

            // Check custom variables first
            if (customVariables?.TryGetValue(variableName, out var customValue) == true)
            {
                return customValue;
            }

            // Check built-in variables
            if (_builtInVariables.TryGetValue(variableName, out var generator))
            {
                return generator(format);
            }

            // Check preset
            if (preset != null && variableName.Equals("preset", StringComparison.OrdinalIgnoreCase))
            {
                return preset.Name;
            }

            // Check timecode
            if (timecode.HasValue && variableName.Equals("timecode", StringComparison.OrdinalIgnoreCase))
            {
                return timecode.Value.ToString().Replace(":", "-").Replace(";", "-");
            }

            // Unknown variable - keep original
            return match.Value;
        });

        // Sanitize for filesystem
        return SanitizeFilename(result);
    }

    /// <summary>
    /// Generate a unique filename, incrementing counter if file exists.
    /// </summary>
    public string GenerateUnique(
        string template,
        string directory,
        string extension,
        EncodingPreset? preset = null,
        Smpte12MTimecode? timecode = null,
        Dictionary<string, string>? customVariables = null)
    {
        var baseFilename = Generate(template, preset, timecode, customVariables);
        var fullPath = Path.Combine(directory, baseFilename + extension);

        if (!File.Exists(fullPath))
            return baseFilename + extension;

        // Add counter suffix
        for (int i = 1; i < 1000; i++)
        {
            var newFilename = $"{baseFilename}_{i:D3}{extension}";
            fullPath = Path.Combine(directory, newFilename);

            if (!File.Exists(fullPath))
                return newFilename;
        }

        // Fallback with GUID
        return $"{baseFilename}_{Guid.NewGuid():N}{extension}";
    }

    private string GetNextCounter(string? format)
    {
        lock (_counterLock)
        {
            _counter++;

            if (int.TryParse(format, out var padding))
            {
                return _counter.ToString($"D{padding}");
            }

            return _counter.ToString("D3");
        }
    }

    public void ResetCounter()
    {
        lock (_counterLock)
        {
            _counter = 0;
        }
    }

    private static string SanitizeFilename(string filename)
    {
        var invalidChars = Path.GetInvalidFileNameChars();
        var sanitized = filename;

        foreach (var c in invalidChars)
        {
            sanitized = sanitized.Replace(c, '_');
        }

        // Also replace some characters that might cause issues
        sanitized = sanitized.Replace(' ', '_');

        return sanitized;
    }
}
