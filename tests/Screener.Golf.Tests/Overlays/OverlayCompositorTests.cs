using Microsoft.Extensions.Logging.Abstractions;
using Screener.Golf.Overlays;

namespace Screener.Golf.Tests.Overlays;

public class OverlayCompositorTests
{
    private readonly OverlayCompositor _compositor;

    public OverlayCompositorTests()
    {
        _compositor = new OverlayCompositor(NullLogger<OverlayCompositor>.Instance);
    }

    [Fact]
    public void BuildFfmpegArgs_NoOverlays_ReturnsStreamCopy()
    {
        var args = _compositor.BuildFfmpegArgs(
            "input.mp4", "output.mp4", "John", null, null);

        Assert.Contains("-c copy", args);
        Assert.DoesNotContain("overlay", args);
        Assert.DoesNotContain("drawtext", args);
    }

    [Fact]
    public void BuildFfmpegArgs_LogoOnly_HasOverlayFilter()
    {
        var logoPath = Path.GetTempFileName();
        try
        {
            var logoBug = new LogoBugConfig { LogoPath = logoPath };

            var args = _compositor.BuildFfmpegArgs(
                "input.mp4", "output.mp4", "John", logoBug, null);

            Assert.Contains("overlay=", args);
            Assert.DoesNotContain("drawtext", args);
        }
        finally
        {
            File.Delete(logoPath);
        }
    }

    [Fact]
    public void BuildFfmpegArgs_LowerThirdOnly_HasDrawtextFilter()
    {
        var lowerThird = new LowerThirdConfig { Enabled = true };

        var args = _compositor.BuildFfmpegArgs(
            "input.mp4", "output.mp4", "John Doe", null, lowerThird);

        Assert.Contains("drawtext=", args);
        Assert.DoesNotContain("overlay", args);
    }

    [Fact]
    public void BuildFfmpegArgs_Both_HasChainedFilters()
    {
        var logoPath = Path.GetTempFileName();
        try
        {
            var logoBug = new LogoBugConfig { LogoPath = logoPath };
            var lowerThird = new LowerThirdConfig { Enabled = true };

            var args = _compositor.BuildFfmpegArgs(
                "input.mp4", "output.mp4", "John", logoBug, lowerThird);

            Assert.Contains("[with_logo]", args);
            Assert.Contains("[final]", args);
            Assert.Contains("overlay=", args);
            Assert.Contains("drawtext=", args);
        }
        finally
        {
            File.Delete(logoPath);
        }
    }

    [Fact]
    public void BuildFfmpegArgs_LogoWithScale_HasScaleFilter()
    {
        var logoPath = Path.GetTempFileName();
        try
        {
            var logoBug = new LogoBugConfig { LogoPath = logoPath, Scale = 0.5 };

            var args = _compositor.BuildFfmpegArgs(
                "input.mp4", "output.mp4", null, logoBug, null);

            Assert.Contains("scale=iw*0.50:ih*0.50", args);
            Assert.Contains("[logo_scaled]", args);
        }
        finally
        {
            File.Delete(logoPath);
        }
    }

    [Fact]
    public void BuildFfmpegArgs_LowerThirdDisabled_ReturnsStreamCopy()
    {
        var lowerThird = new LowerThirdConfig { Enabled = false };

        var args = _compositor.BuildFfmpegArgs(
            "input.mp4", "output.mp4", "John", null, lowerThird);

        Assert.Contains("-c copy", args);
    }

    [Fact]
    public void BuildFfmpegArgs_NullGolferNameWithEnabledLowerThird_ReturnsStreamCopy()
    {
        var lowerThird = new LowerThirdConfig { Enabled = true };

        var args = _compositor.BuildFfmpegArgs(
            "input.mp4", "output.mp4", null, null, lowerThird);

        Assert.Contains("-c copy", args);
    }

    [Fact]
    public void BuildFfmpegArgs_AlwaysHasOverwriteFlag()
    {
        var args = _compositor.BuildFfmpegArgs(
            "input.mp4", "output.mp4", null, null, null);

        Assert.Contains("-y", args);
    }

    [Fact]
    public void BuildFfmpegArgs_WithOverlays_HasFaststart()
    {
        var lowerThird = new LowerThirdConfig { Enabled = true };

        var args = _compositor.BuildFfmpegArgs(
            "input.mp4", "output.mp4", "John", null, lowerThird);

        Assert.Contains("+faststart", args);
    }

    [Fact]
    public void BuildFfmpegArgs_EmptyGolferNameWithLowerThird_ReturnsStreamCopy()
    {
        var lowerThird = new LowerThirdConfig { Enabled = true };

        var args = _compositor.BuildFfmpegArgs(
            "input.mp4", "output.mp4", "", null, lowerThird);

        Assert.Contains("-c copy", args);
    }

    [Fact]
    public void BuildFfmpegArgs_LogoPathDoesNotExist_SkipsLogo()
    {
        var logoBug = new LogoBugConfig { LogoPath = @"C:\nonexistent\logo.png" };

        var args = _compositor.BuildFfmpegArgs(
            "input.mp4", "output.mp4", "John", logoBug, null);

        Assert.Contains("-c copy", args);
        Assert.DoesNotContain("overlay", args);
    }

    [Fact]
    public void BuildFfmpegArgs_NullLogoPath_SkipsLogo()
    {
        var logoBug = new LogoBugConfig { LogoPath = null };

        var args = _compositor.BuildFfmpegArgs(
            "input.mp4", "output.mp4", "John", logoBug, null);

        Assert.Contains("-c copy", args);
    }

    [Fact]
    public void BuildFfmpegArgs_LogoNoScale_NoScaleFilter()
    {
        var logoPath = Path.GetTempFileName();
        try
        {
            var logoBug = new LogoBugConfig { LogoPath = logoPath, Scale = 1.0 };

            var args = _compositor.BuildFfmpegArgs(
                "input.mp4", "output.mp4", null, logoBug, null);

            Assert.DoesNotContain("[logo_scaled]", args);
            Assert.DoesNotContain("scale=", args);
        }
        finally
        {
            File.Delete(logoPath);
        }
    }
}
