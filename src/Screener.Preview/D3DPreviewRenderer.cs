using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Screener.Abstractions.Capture;
using SharpDX;
using SharpDX.Direct3D;
using SharpDX.Direct3D11;
using SharpDX.DXGI;
using Device = SharpDX.Direct3D11.Device;
using Resource = SharpDX.Direct3D11.Resource;

namespace Screener.Preview;

/// <summary>
/// DirectX 11 renderer for video preview with WPF D3DImage integration.
/// Supports zero-copy rendering when possible.
/// </summary>
public sealed class D3DPreviewRenderer : IDisposable
{
    private readonly ILogger<D3DPreviewRenderer> _logger;
    private readonly object _renderLock = new();

    private Device? _device;
    private DeviceContext? _context;
    private Texture2D? _sharedTexture;
    private Texture2D? _stagingTexture;
    private ShaderResourceView? _shaderResourceView;
    private RenderTargetView? _renderTargetView;

    private int _width;
    private int _height;
    private bool _isInitialized;
    private IntPtr _sharedHandle;

    public int Width => _width;
    public int Height => _height;
    public bool IsInitialized => _isInitialized;
    public IntPtr SharedHandle => _sharedHandle;

    /// <summary>
    /// Fired when a new frame is available for rendering.
    /// </summary>
    public event EventHandler? FrameReady;

    public D3DPreviewRenderer(ILogger<D3DPreviewRenderer> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Initialize the D3D11 device and create shared resources.
    /// </summary>
    public void Initialize(int width, int height)
    {
        lock (_renderLock)
        {
            if (_isInitialized && _width == width && _height == height)
                return;

            Cleanup();

            _width = width;
            _height = height;

            try
            {
                // Create D3D11 device with BGRA support for WPF interop
                var creationFlags = DeviceCreationFlags.BgraSupport;
#if DEBUG
                // Enable debug layer in debug builds
                creationFlags |= DeviceCreationFlags.Debug;
#endif

                _device = new Device(DriverType.Hardware, creationFlags);
                _context = _device.ImmediateContext;

                // Create shared texture that WPF can access
                var textureDesc = new Texture2DDescription
                {
                    Width = width,
                    Height = height,
                    MipLevels = 1,
                    ArraySize = 1,
                    Format = Format.B8G8R8A8_UNorm,
                    SampleDescription = new SampleDescription(1, 0),
                    Usage = ResourceUsage.Default,
                    BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource,
                    CpuAccessFlags = CpuAccessFlags.None,
                    OptionFlags = ResourceOptionFlags.Shared
                };

                _sharedTexture = new Texture2D(_device, textureDesc);

                // Get the shared handle for WPF D3DImage
                using var resource = _sharedTexture.QueryInterface<SharpDX.DXGI.Resource>();
                _sharedHandle = resource.SharedHandle;

                // Create staging texture for CPU-to-GPU copies
                var stagingDesc = new Texture2DDescription
                {
                    Width = width,
                    Height = height,
                    MipLevels = 1,
                    ArraySize = 1,
                    Format = Format.B8G8R8A8_UNorm,
                    SampleDescription = new SampleDescription(1, 0),
                    Usage = ResourceUsage.Dynamic,
                    BindFlags = BindFlags.ShaderResource,
                    CpuAccessFlags = CpuAccessFlags.Write,
                    OptionFlags = ResourceOptionFlags.None
                };

                _stagingTexture = new Texture2D(_device, stagingDesc);

                // Create views
                _renderTargetView = new RenderTargetView(_device, _sharedTexture);
                _shaderResourceView = new ShaderResourceView(_device, _stagingTexture);

                _isInitialized = true;

                _logger.LogInformation("D3D preview renderer initialized: {Width}x{Height}", width, height);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to initialize D3D preview renderer");
                Cleanup();
                throw;
            }
        }
    }

    /// <summary>
    /// Render a video frame to the shared texture.
    /// </summary>
    public void RenderFrame(VideoFrame frame)
    {
        if (!_isInitialized || _device == null || _context == null || _stagingTexture == null || _sharedTexture == null)
            return;

        lock (_renderLock)
        {
            try
            {
                // Check if frame size matches
                if (frame.Width != _width || frame.Height != _height)
                {
                    Initialize(frame.Width, frame.Height);
                }

                // Map the staging texture for CPU write
                var dataBox = _context.MapSubresource(
                    _stagingTexture,
                    0,
                    MapMode.WriteDiscard,
                    SharpDX.Direct3D11.MapFlags.None);

                try
                {
                    // Copy frame data to staging texture
                    // Handle different pixel formats
                    CopyFrameData(frame, dataBox);
                }
                finally
                {
                    _context.UnmapSubresource(_stagingTexture, 0);
                }

                // Copy staging to shared texture
                _context.CopyResource(_stagingTexture, _sharedTexture);

                // Notify that frame is ready
                FrameReady?.Invoke(this, EventArgs.Empty);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to render frame");
            }
        }
    }

    /// <summary>
    /// Copy frame data with format conversion if needed.
    /// </summary>
    private unsafe void CopyFrameData(VideoFrame frame, DataBox dataBox)
    {
        var destPitch = dataBox.RowPitch;
        var srcPitch = frame.RowBytes;
        var height = frame.Height;

        fixed (byte* srcPtr = frame.Data.Span)
        {
            var dest = (byte*)dataBox.DataPointer;

            switch (frame.PixelFormat)
            {
                case PixelFormat.BGRA:
                    // Direct copy - format matches
                    for (int y = 0; y < height; y++)
                    {
                        System.Buffer.MemoryCopy(
                            srcPtr + y * srcPitch,
                            dest + y * destPitch,
                            destPitch,
                            Math.Min(srcPitch, destPitch));
                    }
                    break;

                case PixelFormat.UYVY:
                    ConvertUYVYToBGRA(srcPtr, dest, frame.Width, height, srcPitch, destPitch);
                    break;

                case PixelFormat.YUV422_10bit:
                    ConvertYUV422_10bitToBGRA(srcPtr, dest, frame.Width, height, srcPitch, destPitch);
                    break;

                default:
                    // Fallback: fill with black
                    for (int y = 0; y < height; y++)
                    {
                        for (int x = 0; x < frame.Width; x++)
                        {
                            var offset = y * destPitch + x * 4;
                            dest[offset] = 0;     // B
                            dest[offset + 1] = 0; // G
                            dest[offset + 2] = 0; // R
                            dest[offset + 3] = 255; // A
                        }
                    }
                    break;
            }
        }
    }

    /// <summary>
    /// Convert UYVY (YUV 4:2:2) to BGRA.
    /// </summary>
    private unsafe void ConvertUYVYToBGRA(byte* src, byte* dest, int width, int height, int srcPitch, int destPitch)
    {
        // Calculate HANC offset: DeckLink may include horizontal blanking at the start of each row
        // Active video is width * 2 bytes; any extra bytes in srcPitch are HANC at the start
        int activeVideoBytes = width * 2;
        int hancOffset = srcPitch > activeVideoBytes ? srcPitch - activeVideoBytes : 0;

        for (int y = 0; y < height; y++)
        {
            // Start at HANC offset to skip horizontal blanking data at row start
            var srcRow = src + y * srcPitch + hancOffset;
            var destRow = dest + y * destPitch;

            for (int x = 0; x < width; x += 2)
            {
                // UYVY: U0 Y0 V0 Y1 -> two pixels
                var u = srcRow[0] - 128;
                var y0 = srcRow[1];
                var v = srcRow[2] - 128;
                var y1 = srcRow[3];

                // First pixel
                var r0 = Clamp((int)(y0 + 1.402 * v));
                var g0 = Clamp((int)(y0 - 0.344 * u - 0.714 * v));
                var b0 = Clamp((int)(y0 + 1.772 * u));

                destRow[0] = (byte)b0;
                destRow[1] = (byte)g0;
                destRow[2] = (byte)r0;
                destRow[3] = 255;

                // Second pixel
                var r1 = Clamp((int)(y1 + 1.402 * v));
                var g1 = Clamp((int)(y1 - 0.344 * u - 0.714 * v));
                var b1 = Clamp((int)(y1 + 1.772 * u));

                destRow[4] = (byte)b1;
                destRow[5] = (byte)g1;
                destRow[6] = (byte)r1;
                destRow[7] = 255;

                srcRow += 4;
                destRow += 8;
            }
        }
    }

    /// <summary>
    /// Convert 10-bit YUV 4:2:2 to BGRA.
    /// </summary>
    private unsafe void ConvertYUV422_10bitToBGRA(byte* src, byte* dest, int width, int height, int srcPitch, int destPitch)
    {
        // v210 format: 3 10-bit samples packed into 4 bytes
        // Calculate HANC offset: DeckLink may include horizontal blanking at the start of each row
        // Active video for v210 is approximately (width * 16 + 5) / 6 bytes
        int activeVideoBytes = (width * 16 + 5) / 6;
        int hancOffset = srcPitch > activeVideoBytes ? srcPitch - activeVideoBytes : 0;

        for (int y = 0; y < height; y++)
        {
            // Start at HANC offset to skip horizontal blanking data at row start
            var srcRow = (uint*)(src + y * srcPitch + hancOffset);
            var destRow = dest + y * destPitch;
            var destX = 0;

            for (int x = 0; x < width / 6; x++)
            {
                // Unpack 6 pixels from 4 32-bit words
                var w0 = srcRow[0];
                var w1 = srcRow[1];
                var w2 = srcRow[2];
                var w3 = srcRow[3];

                // Extract 10-bit values (simplified - actual v210 unpacking is more complex)
                var u0 = (int)((w0 >> 0) & 0x3FF) - 512;
                var y0 = (int)((w0 >> 10) & 0x3FF);
                var v0 = (int)((w0 >> 20) & 0x3FF) - 512;

                // Scale 10-bit to 8-bit
                y0 = y0 >> 2;
                u0 = u0 >> 2;
                v0 = v0 >> 2;

                // Convert to RGB
                var r = Clamp(y0 + (int)(1.402 * v0));
                var g = Clamp(y0 - (int)(0.344 * u0) - (int)(0.714 * v0));
                var b = Clamp(y0 + (int)(1.772 * u0));

                // Write 6 pixels (simplified - repeating first pixel)
                for (int i = 0; i < 6 && destX < width; i++, destX++)
                {
                    destRow[destX * 4 + 0] = (byte)b;
                    destRow[destX * 4 + 1] = (byte)g;
                    destRow[destX * 4 + 2] = (byte)r;
                    destRow[destX * 4 + 3] = 255;
                }

                srcRow += 4;
            }
        }
    }

    private static int Clamp(int value) => Math.Max(0, Math.Min(255, value));

    /// <summary>
    /// Clear the render target to black.
    /// </summary>
    public void Clear()
    {
        if (!_isInitialized || _context == null || _renderTargetView == null)
            return;

        lock (_renderLock)
        {
            _context.ClearRenderTargetView(_renderTargetView, new SharpDX.Mathematics.Interop.RawColor4(0, 0, 0, 1));
            FrameReady?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>
    /// Get the D3D11 device for external use.
    /// </summary>
    public Device? GetDevice() => _device;

    /// <summary>
    /// Get the shared texture for external use.
    /// </summary>
    public Texture2D? GetSharedTexture() => _sharedTexture;

    private void Cleanup()
    {
        _isInitialized = false;
        _sharedHandle = IntPtr.Zero;

        _shaderResourceView?.Dispose();
        _shaderResourceView = null;

        _renderTargetView?.Dispose();
        _renderTargetView = null;

        _stagingTexture?.Dispose();
        _stagingTexture = null;

        _sharedTexture?.Dispose();
        _sharedTexture = null;

        _context?.Dispose();
        _context = null;

        _device?.Dispose();
        _device = null;
    }

    public void Dispose()
    {
        lock (_renderLock)
        {
            Cleanup();
        }
    }
}
