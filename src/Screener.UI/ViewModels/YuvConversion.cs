namespace Screener.UI.ViewModels;

/// <summary>
/// Pre-computed YUV-to-RGB lookup tables and VANC/HANC geometry detection.
/// Shared by all preview renderers (InputPreviewRenderer).
/// </summary>
internal static class YuvConversion
{
    // Pre-computed lookup tables for YUV->RGB conversion
    internal static readonly int[] YtoC = new int[256];   // 298 * (Y - 16), clamped
    internal static readonly int[] UtoG = new int[256];   // -100 * (U - 128)
    internal static readonly int[] UtoB = new int[256];   // 516 * (U - 128)
    internal static readonly int[] VtoR = new int[256];   // 409 * (V - 128)
    internal static readonly int[] VtoG = new int[256];   // -208 * (V - 128)
    internal const int ClampOffset = 512;
    internal static readonly byte[] ClampTable = new byte[ClampOffset + 768]; // Clamp table for -512..767 range

    static YuvConversion()
    {
        // Initialize YUV->RGB lookup tables
        for (int i = 0; i < 256; i++)
        {
            int y = i - 16;
            YtoC[i] = y < 0 ? 0 : 298 * y;
            UtoG[i] = -100 * (i - 128);
            UtoB[i] = 516 * (i - 128);
            VtoR[i] = 409 * (i - 128);
            VtoG[i] = -208 * (i - 128);
        }
        // Initialize clamp table (offset by ClampOffset to handle negative values)
        for (int i = 0; i < ClampTable.Length; i++)
        {
            int v = i - ClampOffset;
            ClampTable[i] = (byte)(v < 0 ? 0 : v > 255 ? 255 : v);
        }
    }

    // Auto-detection of VANC rows (vertical) and HANC offset (horizontal).
    // Detected once on the first valid frame, reset on format change.
    private static int _detectedVancRows = -1; // -1 = not yet detected
    private static int _detectedHancBytes = 0;  // horizontal byte offset per row
    private static readonly object _vancDetectionLock = new();

    internal static int DetectedVancRows => _detectedVancRows;
    internal static int DetectedHancBytes => _detectedHancBytes;

    /// <summary>
    /// Ensures VANC geometry has been detected. Called by InputPreviewRenderer on first frame.
    /// </summary>
    internal static void EnsureVancDetected(byte[] frameData, int srcRowBytes, int height)
    {
        if (_detectedVancRows >= 0) return;
        lock (_vancDetectionLock)
        {
            if (_detectedVancRows >= 0) return;
            int maxScan = Math.Min(120, height);
            int firstVideoRow = 0;
            for (int row = 0; row < maxScan; row++)
            {
                int rowStart = row * srcRowBytes;
                if (rowStart + 4 >= frameData.Length) break;
                byte b0 = frameData[rowStart];
                byte b1 = frameData[rowStart + 1];
                byte b2 = frameData[rowStart + 2];
                byte b3 = frameData[rowStart + 3];
                bool isAnc = (b0 == 0x00 && b1 == 0x02 && b2 == 0x01 && b3 == 0x20);
                bool isZero = (b0 == 0x00 && b1 == 0x00 && b2 == 0x00 && b3 == 0x00);
                if (!isAnc && !isZero) { firstVideoRow = row; break; }
                firstVideoRow = row + 1;
            }
            _detectedVancRows = firstVideoRow;
        }
    }

    internal static void ResetVancDetection()
    {
        lock (_vancDetectionLock)
        {
            _detectedVancRows = -1;
            _detectedHancBytes = 0;
        }
    }
}
