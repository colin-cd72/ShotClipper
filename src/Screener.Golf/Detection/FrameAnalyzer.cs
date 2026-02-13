namespace Screener.Golf.Detection;

/// <summary>
/// Static methods for lightweight frame analysis on raw UYVY byte arrays.
/// All operations work on the Y (luma) channel only — zero color-space conversion needed.
/// </summary>
public static class FrameAnalyzer
{
    /// <summary>
    /// Extract the Y (luma) channel from a UYVY frame, downsampled to the target resolution.
    /// In UYVY, Y is at byte offsets 1, 3, 5, 7... within each 4-byte group.
    /// </summary>
    /// <param name="uyvyData">Raw UYVY frame data.</param>
    /// <param name="srcWidth">Source frame width in pixels.</param>
    /// <param name="srcHeight">Source frame height in pixels.</param>
    /// <param name="dstWidth">Target downsampled width.</param>
    /// <param name="dstHeight">Target downsampled height.</param>
    /// <param name="lumaBuffer">Pre-allocated buffer of size dstWidth * dstHeight.</param>
    public static void ExtractLumaDownsampled(
        ReadOnlySpan<byte> uyvyData,
        int srcWidth, int srcHeight,
        int dstWidth, int dstHeight,
        Span<byte> lumaBuffer)
    {
        int srcRowBytes = srcWidth * 2; // UYVY = 2 bytes per pixel
        double xScale = (double)srcWidth / dstWidth;
        double yScale = (double)srcHeight / dstHeight;

        for (int dy = 0; dy < dstHeight; dy++)
        {
            int srcRow = (int)(dy * yScale);
            if (srcRow >= srcHeight) srcRow = srcHeight - 1;
            int srcRowOffset = srcRow * srcRowBytes;

            for (int dx = 0; dx < dstWidth; dx++)
            {
                int srcCol = (int)(dx * xScale);
                if (srcCol >= srcWidth) srcCol = srcWidth - 1;

                // In UYVY: U0 Y0 V0 Y1 U2 Y2 V2 Y3 ...
                // Y is at offset (pixel * 2 + 1) within the 4-byte groups
                // More precisely: byte index = pixel_pair_start + (pixel_within_pair == 0 ? 1 : 3)
                int pairStart = (srcCol / 2) * 4;
                int yOffset = (srcCol % 2 == 0) ? 1 : 3;
                int byteIndex = srcRowOffset + pairStart + yOffset;

                if (byteIndex < uyvyData.Length)
                    lumaBuffer[dy * dstWidth + dx] = uyvyData[byteIndex];
            }
        }
    }

    /// <summary>
    /// Compute Sum of Absolute Differences between two luma buffers within a region of interest.
    /// </summary>
    /// <param name="frameA">First luma buffer.</param>
    /// <param name="frameB">Second luma buffer.</param>
    /// <param name="width">Buffer width.</param>
    /// <param name="height">Buffer height.</param>
    /// <param name="roiLeft">ROI left (0.0-1.0).</param>
    /// <param name="roiTop">ROI top (0.0-1.0).</param>
    /// <param name="roiWidth">ROI width (0.0-1.0).</param>
    /// <param name="roiHeight">ROI height (0.0-1.0).</param>
    /// <returns>Normalized SAD (average per pixel in the ROI).</returns>
    public static double ComputeSadInRoi(
        ReadOnlySpan<byte> frameA,
        ReadOnlySpan<byte> frameB,
        int width, int height,
        double roiLeft, double roiTop,
        double roiWidth, double roiHeight)
    {
        int x0 = (int)(roiLeft * width);
        int y0 = (int)(roiTop * height);
        int x1 = Math.Min(width, (int)((roiLeft + roiWidth) * width));
        int y1 = Math.Min(height, (int)((roiTop + roiHeight) * height));

        if (x1 <= x0 || y1 <= y0) return 0;

        long totalDiff = 0;
        int pixelCount = 0;

        for (int y = y0; y < y1; y++)
        {
            int rowOffset = y * width;
            for (int x = x0; x < x1; x++)
            {
                int idx = rowOffset + x;
                totalDiff += Math.Abs(frameA[idx] - frameB[idx]);
                pixelCount++;
            }
        }

        return pixelCount > 0 ? (double)totalDiff / pixelCount : 0;
    }

    /// <summary>
    /// Compute similarity between two luma buffers (1.0 = identical, 0.0 = maximum difference).
    /// </summary>
    public static double ComputeSimilarity(
        ReadOnlySpan<byte> frameA,
        ReadOnlySpan<byte> frameB,
        int width, int height)
    {
        if (frameA.Length != frameB.Length || frameA.Length == 0) return 0;

        long totalDiff = 0;
        int count = width * height;

        for (int i = 0; i < count; i++)
        {
            totalDiff += Math.Abs(frameA[i] - frameB[i]);
        }

        double avgDiff = (double)totalDiff / count;
        // Max possible avg diff is 255; normalize to 0-1 similarity
        return 1.0 - (avgDiff / 255.0);
    }
}
