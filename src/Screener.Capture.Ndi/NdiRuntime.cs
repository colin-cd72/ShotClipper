using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Screener.Capture.Ndi.Interop;

namespace Screener.Capture.Ndi;

/// <summary>
/// Manages NDI runtime detection and initialization.
/// The NDI runtime (Processing.NDI.Lib.x64.dll) is a separate install from https://ndi.video/tools/.
/// This class checks for its presence and initializes the library once for the application lifetime.
/// </summary>
public sealed class NdiRuntime : IDisposable
{
    private readonly ILogger<NdiRuntime> _logger;
    private bool _initialized;

    /// <summary>
    /// Whether the NDI runtime DLL is present on the system.
    /// </summary>
    public bool IsAvailable { get; private set; }

    /// <summary>
    /// Human-readable status message describing the current NDI runtime state.
    /// </summary>
    public string StatusMessage { get; private set; } = "Not checked";

    public NdiRuntime(ILogger<NdiRuntime> logger)
    {
        _logger = logger;
        CheckAvailability();
    }

    private void CheckAvailability()
    {
        const string dllName = "Processing.NDI.Lib.x64.dll";

        // Try loading from PATH first, then search known NDI install locations
        var searchPaths = new[]
        {
            dllName, // System PATH / current directory
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"NDI\NDI 6 Tools\Runtime", dllName),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"NDI\NDI 5 Runtime", dllName),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"NDI\NDI 6 Tools\Router", dllName),
        };

        foreach (var path in searchPaths)
        {
            try
            {
                var handle = NativeLibrary.Load(path);
                if (handle != IntPtr.Zero)
                {
                    NativeLibrary.Free(handle);
                    IsAvailable = true;
                    StatusMessage = $"NDI runtime found: {path}";
                    _logger.LogInformation("NDI runtime detected at {Path}", path);

                    // Register a DLL resolve handler so P/Invoke can find it by name
                    if (path != dllName)
                    {
                        var resolvedPath = path;
                        NativeLibrary.SetDllImportResolver(typeof(NdiInterop).Assembly, (name, assembly, searchPath) =>
                        {
                            if (name == dllName)
                                return NativeLibrary.Load(resolvedPath);
                            return IntPtr.Zero;
                        });
                    }

                    return;
                }
            }
            catch
            {
                // Try next path
            }
        }

        IsAvailable = false;
        StatusMessage = "NDI runtime not installed. Download from ndi.video/tools";
        _logger.LogWarning("NDI runtime not found. Install from https://ndi.video/tools/");
    }

    /// <summary>
    /// Initialize the NDI library. Safe to call multiple times; only initializes once.
    /// </summary>
    /// <returns>True if NDI is initialized and ready for use.</returns>
    public bool Initialize()
    {
        if (!IsAvailable || _initialized) return _initialized;

        try
        {
            _initialized = NdiInterop.NDIlib_initialize();
            if (_initialized)
            {
                StatusMessage = "NDI initialized";
                _logger.LogInformation("NDI library initialized");
            }
            else
            {
                StatusMessage = "NDI initialization failed";
                _logger.LogError("NDIlib_initialize returned false");
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"NDI init error: {ex.Message}";
            _logger.LogError(ex, "Failed to initialize NDI");
        }
        return _initialized;
    }

    public void Dispose()
    {
        if (_initialized)
        {
            try { NdiInterop.NDIlib_destroy(); } catch { }
            _initialized = false;
        }
    }
}
