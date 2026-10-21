using Klipp.Core.Editing;

namespace Klipp.Tests.Editing;

public sealed class EditorEffectRegionFeatherTests
{
    [Theory]
    [InlineData("rectangle", 0)]
    [InlineData("rectangle", .4)]
    [InlineData("rectangle", 1)]
    [InlineData("ellipse", 0)]
    [InlineData("ellipse", .4)]
    [InlineData("ellipse", 1)]
    public void FeatherNeverChangesPixelsOutsideTheRegion(string shape, double feather)
    {
        var region = new EditorEffectRegion(.25, .25, .5, .5);
        Assert.Equal(0, EditorEffectRegionMath.Coverage(region, shape, feather, 200, 100, 49, 50));
        Assert.Equal(0, EditorEffectRegionMath.Coverage(region, shape, feather, 200, 100, 151, 50));
        Assert.Equal(0, EditorEffectRegionMath.Coverage(region, shape, feather, 200, 100, 100, 24));
        Assert.Equal(0, EditorEffectRegionMath.Coverage(region, shape, feather, 200, 100, 100, 76));
    }

    [Fact]
    public void RectangularFeatherUsesTheSmallerPixelDimension()
    {
        var region = new EditorEffectRegion(.25, .25, .5, .5);
        Assert.Equal(.5, EditorEffectRegionMath.Coverage(region, "rectangle", .4, 200, 100, 60, 30), 8);
        Assert.Equal(1, EditorEffectRegionMath.Coverage(region, "rectangle", .4, 200, 100, 60, 35), 8);
        Assert.Equal(0, EditorEffectRegionMath.Coverage(region, "rectangle", .4, 200, 100, 50, 50), 8);
    }

    [Fact]
    public void EllipseHasAnInwardRadialRampAndUnchangedExterior()
    {
        var region = new EditorEffectRegion(.25, .25, .5, .5);
        Assert.Equal(.5, EditorEffectRegionMath.Coverage(region, "ellipse", .4, 200, 100, 140, 50), 8);
        Assert.Equal(1, EditorEffectRegionMath.Coverage(region, "ellipse", .4, 200, 100, 130, 50), 8);
        Assert.Equal(0, EditorEffectRegionMath.Coverage(region, "ellipse", .4, 200, 100, 150, 50), 8);
    }

    [Theory]
    [InlineData("linear", .5)]
    [InlineData("ease-in", .25)]
    [InlineData("ease-out", .75)]
    [InlineData("ease-in-out", .5)]
    [InlineData("hold", 0)]
    public void InterpolationKeepsRegionsBoundedAndUsesTheIncomingEasing(string easing, double amount)
    {
        var from = new EditorEffectRegion(.1, .2, .2, .3);
        var to = new EditorEffectRegion(.4, .3, .6, .7);
        var result = EditorEffectRegionMath.Interpolate(from, to, .5, easing);
        Assert.Equal(.1 + .3 * amount, result.X, 8);
        Assert.Equal(.2 + .1 * amount, result.Y, 8);
        Assert.True(result.X + result.Width <= 1.0000001);
        Assert.True(result.Y + result.Height <= 1.0000001);
    }

    [Fact]
    public void InvalidGeometryAndFeatherCannotProduceInvalidCoverage()
    {
        var region = EditorEffectRegionMath.Normalize(new(double.NaN, double.PositiveInfinity, 0, double.NaN));
        Assert.Equal(new EditorEffectRegion(0, 0, .02, 1), region);
        Assert.Equal(0, EditorEffectRegionMath.Coverage(region, "ellipse", .4, 0, 100, 10, 10));
        Assert.True(double.IsFinite(EditorEffectRegionMath.Coverage(region, "rectangle", double.NaN, 200, 100, 1, 10)));
    }
}
