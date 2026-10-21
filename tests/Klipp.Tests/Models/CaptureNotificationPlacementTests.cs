using Klipp.Core.Models;

namespace Klipp.Tests.Models;

public class CaptureNotificationPlacementTests
{
    [Theory]
    [InlineData(1920, 1080, 1, 16, 56, 384, 120)]
    [InlineData(2560, 1600, 1.5, 24, 84, 576, 180)]
    [InlineData(3840, 2160, 2, 32, 112, 768, 240)]
    public void PlacesTheCardBelowTheUpperLeftCorner(int width, int height, double scale,
        int x, int y, int cardWidth, int cardHeight)
    {
        CaptureNotificationPlacement.UpperLeftEdge(0, 0, width, height, scale)
            .ShouldBe(new CaptureNotificationBounds(x, y, cardWidth, cardHeight));
    }

    [Fact]
    public void UsesTheSelectedMonitorsOrigin()
    {
        CaptureNotificationPlacement.UpperLeftEdge(-1920, -1080, 1920, 1080, 1)
            .ShouldBe(new CaptureNotificationBounds(-1904, -1024, 384, 120));
    }

    [Theory]
    [InlineData(320, 160, 4)]
    [InlineData(1, 1, 1)]
    [InlineData(40, 12, 2)]
    [InlineData(1920, 170, 1)]
    [InlineData(12, 1000, 1.5)]
    [InlineData(200, 1, 8)]
    [InlineData(1, 200, .5)]
    public void KeepsTheWholeCardWithinSmallDisplays(int width, int height, double scale)
    {
        var card = CaptureNotificationPlacement.UpperLeftEdge(-500, 200, width, height, scale);
        card.Left.ShouldBeGreaterThanOrEqualTo(-500);
        card.Top.ShouldBeGreaterThanOrEqualTo(200);
        (card.Left + card.Width).ShouldBeLessThanOrEqualTo(-500 + width);
        (card.Top + card.Height).ShouldBeLessThanOrEqualTo(200 + height);
        card.Width.ShouldBeGreaterThan(0);
        card.Height.ShouldBeGreaterThan(0);
    }

    [Fact]
    public void ReducesTheTopOffsetBeforeClippingTheCardOnShortDisplays()
    {
        CaptureNotificationPlacement.UpperLeftEdge(100, -200, 1920, 170, 1)
            .ShouldBe(new CaptureNotificationBounds(116, -166, 384, 120));
    }

    [Fact]
    public void InvalidDpiFallsBackToNormalScale()
    {
        var normal = CaptureNotificationPlacement.UpperLeftEdge(0, 0, 1920, 1080, 1);
        CaptureNotificationPlacement.UpperLeftEdge(0, 0, 1920, 1080, double.NaN).ShouldBe(normal);
        CaptureNotificationPlacement.UpperLeftEdge(0, 0, 1920, 1080, double.PositiveInfinity).ShouldBe(normal);
        CaptureNotificationPlacement.UpperLeftEdge(0, 0, 1920, 1080, 0).ShouldBe(normal);
    }
}
