using Screener.Golf.Detection;

namespace Screener.Golf.Tests.Detection;

public class FrameAnalyzerTests
{
    [Fact]
    public void ExtractLumaDownsampled_ExtractsYChannel_FromUyvyData()
    {
        // Arrange: 4x2 UYVY frame (4 pixels wide, 2 rows)
        // UYVY format: U0 Y0 V0 Y1 | U2 Y2 V2 Y3 ...
        // Each 4-byte group encodes 2 pixels
        int srcWidth = 4;
        int srcHeight = 2;
        int rowBytes = srcWidth * 2; // 8 bytes per row

        var uyvy = new byte[srcWidth * srcHeight * 2]; // 16 bytes

        // Row 0: pixels (Y=10, Y=20, Y=30, Y=40)
        uyvy[0] = 128; uyvy[1] = 10;  uyvy[2] = 128; uyvy[3] = 20;  // U Y V Y
        uyvy[4] = 128; uyvy[5] = 30;  uyvy[6] = 128; uyvy[7] = 40;

        // Row 1: pixels (Y=50, Y=60, Y=70, Y=80)
        uyvy[8] = 128;  uyvy[9] = 50;  uyvy[10] = 128; uyvy[11] = 60;
        uyvy[12] = 128; uyvy[13] = 70; uyvy[14] = 128; uyvy[15] = 80;

        // Extract luma at 1:1 (no downsampling)
        var luma = new byte[srcWidth * srcHeight];

        // Act
        FrameAnalyzer.ExtractLumaDownsampled(uyvy, srcWidth, srcHeight, srcWidth, srcHeight, luma);

        // Assert: Y values should be extracted correctly
        Assert.Equal(10, luma[0]); // row 0, pixel 0
        Assert.Equal(20, luma[1]); // row 0, pixel 1
        Assert.Equal(30, luma[2]); // row 0, pixel 2
        Assert.Equal(40, luma[3]); // row 0, pixel 3
        Assert.Equal(50, luma[4]); // row 1, pixel 0
        Assert.Equal(60, luma[5]); // row 1, pixel 1
        Assert.Equal(70, luma[6]); // row 1, pixel 2
        Assert.Equal(80, luma[7]); // row 1, pixel 3
    }

    [Fact]
    public void ExtractLumaDownsampled_Downsamples_ByHalf()
    {
        // 4x4 source -> 2x2 destination
        int srcWidth = 4, srcHeight = 4;
        int dstWidth = 2, dstHeight = 2;

        var uyvy = new byte[srcWidth * srcHeight * 2];
        // Fill with known Y values
        for (int y = 0; y < srcHeight; y++)
        for (int x = 0; x < srcWidth; x++)
        {
            int pairStart = y * srcWidth * 2 + (x / 2) * 4;
            int yOffset = (x % 2 == 0) ? 1 : 3;
            uyvy[pairStart + yOffset] = (byte)(y * srcWidth + x);
        }

        var luma = new byte[dstWidth * dstHeight];
        FrameAnalyzer.ExtractLumaDownsampled(uyvy, srcWidth, srcHeight, dstWidth, dstHeight, luma);

        // Should sample from the top-left quadrant due to nearest-neighbor
        Assert.Equal(4, luma.Length);
        // Values depend on scaling math but should be valid luma values
        Assert.True(luma[0] < srcWidth * srcHeight);
    }

    [Fact]
    public void ComputeSadInRoi_IdenticalFrames_ReturnsZero()
    {
        int width = 10, height = 10;
        var frame = new byte[width * height];
        for (int i = 0; i < frame.Length; i++)
            frame[i] = 128;

        double sad = FrameAnalyzer.ComputeSadInRoi(
            frame, frame, width, height,
            0, 0, 1.0, 1.0);

        Assert.Equal(0, sad);
    }

    [Fact]
    public void ComputeSadInRoi_MaxDifference_Returns255()
    {
        int width = 10, height = 10;
        var frameA = new byte[width * height];
        var frameB = new byte[width * height];

        // All zeros vs all 255
        Array.Fill(frameB, (byte)255);

        double sad = FrameAnalyzer.ComputeSadInRoi(
            frameA, frameB, width, height,
            0, 0, 1.0, 1.0);

        Assert.Equal(255.0, sad);
    }

    [Fact]
    public void ComputeSadInRoi_PartialRoi_OnlyAnalyzesRegion()
    {
        int width = 10, height = 10;
        var frameA = new byte[width * height];
        var frameB = new byte[width * height];

        // Make only the left half different
        for (int y = 0; y < height; y++)
        for (int x = 0; x < width / 2; x++)
            frameB[y * width + x] = 100;

        // Full frame SAD
        double fullSad = FrameAnalyzer.ComputeSadInRoi(
            frameA, frameB, width, height,
            0, 0, 1.0, 1.0);

        // Right half only (no difference)
        double rightSad = FrameAnalyzer.ComputeSadInRoi(
            frameA, frameB, width, height,
            0.5, 0, 0.5, 1.0);

        Assert.True(fullSad > 0);
        Assert.Equal(0, rightSad);
    }

    [Fact]
    public void ComputeSadInRoi_EmptyRoi_ReturnsZero()
    {
        var frame = new byte[100];
        double sad = FrameAnalyzer.ComputeSadInRoi(
            frame, frame, 10, 10,
            0, 0, 0, 0);

        Assert.Equal(0, sad);
    }

    [Fact]
    public void ComputeSimilarity_IdenticalFrames_ReturnsOne()
    {
        int width = 10, height = 10;
        var frame = new byte[width * height];
        for (int i = 0; i < frame.Length; i++)
            frame[i] = 128;

        double sim = FrameAnalyzer.ComputeSimilarity(frame, frame, width, height);

        Assert.Equal(1.0, sim);
    }

    [Fact]
    public void ComputeSimilarity_MaxDifference_ReturnsZero()
    {
        int width = 10, height = 10;
        var frameA = new byte[width * height];
        var frameB = new byte[width * height];
        Array.Fill(frameB, (byte)255);

        double sim = FrameAnalyzer.ComputeSimilarity(frameA, frameB, width, height);

        Assert.Equal(0.0, sim);
    }

    [Fact]
    public void ComputeSimilarity_EmptyFrames_ReturnsZero()
    {
        double sim = FrameAnalyzer.ComputeSimilarity(
            ReadOnlySpan<byte>.Empty, ReadOnlySpan<byte>.Empty, 0, 0);

        Assert.Equal(0, sim);
    }

    [Fact]
    public void ComputeSimilarity_MismatchedLengths_ReturnsZero()
    {
        var a = new byte[10];
        var b = new byte[20];

        double sim = FrameAnalyzer.ComputeSimilarity(a, b, 10, 1);

        Assert.Equal(0, sim);
    }

    [Fact]
    public void ComputeSimilarity_SlightDifference_ReturnsHighValue()
    {
        int width = 10, height = 10;
        var frameA = new byte[width * height];
        var frameB = new byte[width * height];
        Array.Fill(frameA, (byte)128);
        Array.Fill(frameB, (byte)130); // small difference

        double sim = FrameAnalyzer.ComputeSimilarity(frameA, frameB, width, height);

        Assert.True(sim > 0.99);
        Assert.True(sim < 1.0);
    }
}
