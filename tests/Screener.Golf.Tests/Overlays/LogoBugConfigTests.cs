using Screener.Golf.Overlays;

namespace Screener.Golf.Tests.Overlays;

public class LogoBugConfigTests
{
    [Fact]
    public void Default_Position_IsTopRight()
    {
        var config = new LogoBugConfig();
        Assert.Equal(LogoPosition.TopRight, config.Position);
    }

    [Fact]
    public void Default_Scale_IsOne()
    {
        var config = new LogoBugConfig();
        Assert.Equal(1.0, config.Scale);
    }

    [Theory]
    [InlineData(LogoPosition.TopLeft, 20, "x=20:y=20")]
    [InlineData(LogoPosition.TopRight, 20, "x=W-w-20:y=20")]
    [InlineData(LogoPosition.BottomLeft, 20, "x=20:y=H-h-20")]
    [InlineData(LogoPosition.BottomRight, 20, "x=W-w-20:y=H-h-20")]
    public void GetOverlayPosition_ReturnsCorrectExpression(LogoPosition position, int margin, string expected)
    {
        var config = new LogoBugConfig
        {
            Position = position,
            Margin = margin
        };

        string result = config.GetOverlayPosition();

        Assert.Equal(expected, result);
    }

    [Fact]
    public void GetOverlayPosition_Custom_UsesCustomCoordinates()
    {
        var config = new LogoBugConfig
        {
            Position = LogoPosition.Custom,
            CustomX = 100,
            CustomY = 200
        };

        string result = config.GetOverlayPosition();

        Assert.Equal("x=100:y=200", result);
    }

    [Fact]
    public void GetOverlayPosition_DifferentMargin_ReflectedInOutput()
    {
        var config = new LogoBugConfig
        {
            Position = LogoPosition.TopLeft,
            Margin = 50
        };

        string result = config.GetOverlayPosition();

        Assert.Equal("x=50:y=50", result);
    }
}
