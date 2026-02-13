using Screener.Golf.Overlays;

namespace Screener.Golf.Tests.Overlays;

public class LowerThirdConfigTests
{
    [Fact]
    public void Default_IsEnabled()
    {
        var config = new LowerThirdConfig();
        Assert.True(config.Enabled);
    }

    [Fact]
    public void Default_ShowsBox()
    {
        var config = new LowerThirdConfig();
        Assert.True(config.ShowBox);
    }

    [Fact]
    public void BuildDrawtextFilter_ContainsGolferName()
    {
        var config = new LowerThirdConfig();
        string filter = config.BuildDrawtextFilter("Tiger Woods");

        Assert.Contains("text='Tiger Woods'", filter);
    }

    [Fact]
    public void BuildDrawtextFilter_ContainsFontSize()
    {
        var config = new LowerThirdConfig { FontSize = 36 };
        string filter = config.BuildDrawtextFilter("Test");

        Assert.Contains("fontsize=36", filter);
    }

    [Fact]
    public void BuildDrawtextFilter_ContainsFontColor()
    {
        var config = new LowerThirdConfig { FontColor = "yellow" };
        string filter = config.BuildDrawtextFilter("Test");

        Assert.Contains("fontcolor=yellow", filter);
    }

    [Fact]
    public void BuildDrawtextFilter_ContainsPosition()
    {
        var config = new LowerThirdConfig { X = 50, YFromBottom = 100 };
        string filter = config.BuildDrawtextFilter("Test");

        Assert.Contains("x=50", filter);
        Assert.Contains("y=H-100", filter);
    }

    [Fact]
    public void BuildDrawtextFilter_WithBox_ContainsBoxParams()
    {
        var config = new LowerThirdConfig
        {
            ShowBox = true,
            BoxColor = "black@0.6",
            BoxPadding = 15
        };
        string filter = config.BuildDrawtextFilter("Test");

        Assert.Contains("box=1", filter);
        Assert.Contains("boxcolor=black@0.6", filter);
        Assert.Contains("boxborderw=15", filter);
    }

    [Fact]
    public void BuildDrawtextFilter_WithoutBox_NoBoxParams()
    {
        var config = new LowerThirdConfig { ShowBox = false };
        string filter = config.BuildDrawtextFilter("Test");

        Assert.DoesNotContain("box=1", filter);
        Assert.DoesNotContain("boxcolor", filter);
    }

    [Fact]
    public void BuildDrawtextFilter_WithFontFamily_ContainsFontFamily()
    {
        var config = new LowerThirdConfig { FontFamily = "Helvetica" };
        string filter = config.BuildDrawtextFilter("Test");

        Assert.Contains("fontfamily='Helvetica'", filter);
    }

    [Fact]
    public void BuildDrawtextFilter_EmptyFontFamily_OmitsFontFamily()
    {
        var config = new LowerThirdConfig { FontFamily = "" };
        string filter = config.BuildDrawtextFilter("Test");

        Assert.DoesNotContain("fontfamily", filter);
    }

    [Fact]
    public void BuildDrawtextFilter_EscapesSingleQuotes()
    {
        var config = new LowerThirdConfig();
        string filter = config.BuildDrawtextFilter("O'Brien");

        // Single quotes should be escaped for FFmpeg
        Assert.Contains("O'\\''Brien", filter);
    }

    [Fact]
    public void BuildDrawtextFilter_EscapesColons()
    {
        var config = new LowerThirdConfig();
        string filter = config.BuildDrawtextFilter("Score: 72");

        Assert.Contains("Score\\: 72", filter);
    }

    [Fact]
    public void BuildDrawtextFilter_StartsWithDrawtext()
    {
        var config = new LowerThirdConfig();
        string filter = config.BuildDrawtextFilter("Test");

        Assert.StartsWith("drawtext=", filter);
    }
}
