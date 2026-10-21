using Klipp.Core.Editing;

namespace Klipp.Tests.Editing;

public sealed class EditorEffectRegionMathTests
{
    [Theory]
    [InlineData(1920, 1080, 1920, 1080, 0, 0, 0, 0)]
    [InlineData(1920, 1080, 1080, 1920, 0, 0, 0, 0)]
    [InlineData(1080, 1920, 3840, 2160, .2, .1, .15, .35)]
    [InlineData(947, 509, 1237, 853, .1, .3, .25, .2)]
    public void SourceAndHostRoundTripWithDifferentAspectRatiosAndCrops(double cw, double ch, double sw, double sh,
        double left, double top, double right, double bottom)
    {
        var fit = EditorSourceFitMath.Calculate(cw, ch, sw, sh, left, top, right, bottom);
        foreach (var point in new[] { new EditorEffectPoint(0, 0), new(.23, .67), new(1, 1), new(-.4, 1.6) })
        {
            var roundTrip = EditorEffectRegionMath.HostToSource(fit, EditorEffectRegionMath.SourceToHost(fit, point));
            Assert.Equal(point.X, roundTrip.X, 9);
            Assert.Equal(point.Y, roundTrip.Y, 9);
        }
    }

    [Fact]
    public void PortraitSourceUsesTheVisiblePictureRatherThanTheCanvasBars()
    {
        var fit = EditorSourceFitMath.Calculate(1920, 1080, 1080, 1920, 0, 0, 0, 0);
        var point = EditorEffectRegionMath.SourceToHost(fit, new(0, .5));
        Assert.Equal(656.25, point.X, 8);
        Assert.Equal(540, point.Y, 8);
        Assert.True(EditorEffectRegionMath.HostToSource(fit, new(0, 540)).X < 0);
    }

    [Fact]
    public void MovingFromAnOffCenterGrabPreservesSizeAndRelativePointerOffset()
    {
        var region = new EditorEffectRegion(.2, .3, .4, .25);
        var grab = new EditorEffectPoint(.24, .47);
        var pointer = new EditorEffectPoint(.34, .42);
        var moved = EditorEffectRegionMath.MoveWithinSource(region, new(pointer.X - grab.X, pointer.Y - grab.Y));
        Assert.Equal(.3, moved.X, 9);
        Assert.Equal(.25, moved.Y, 9);
        Assert.Equal(region.Width, moved.Width);
        Assert.Equal(region.Height, moved.Height);
        Assert.Equal(grab.X - region.X, pointer.X - moved.X, 9);
        Assert.Equal(grab.Y - region.Y, pointer.Y - moved.Y, 9);
    }

    [Fact]
    public void MovingClampsAtSourceEdgesWithoutShrinkingTheEffect()
    {
        var region = new EditorEffectRegion(.2, .3, .4, .25);
        Assert.Equal(new(0, .75, .4, .25), EditorEffectRegionMath.MoveWithinSource(region, new(-5, 5)));
        Assert.Equal(new(.6, 0, .4, .25), EditorEffectRegionMath.MoveWithinSource(region, new(5, -5)));
    }

    [Theory]
    [InlineData("nw", .1, .15, .1, .15, .5, .6)]
    [InlineData("ne", .9, .15, .2, .15, .7, .6)]
    [InlineData("sw", .1, .9, .1, .3, .5, .6)]
    [InlineData("se", .9, .9, .2, .3, .7, .6)]
    public void CornerResizePreservesTheOppositeCorner(string corner, double px, double py,
        double x, double y, double width, double height)
    {
        var result = EditorEffectRegionMath.ResizeCorner(new(.2, .3, .4, .45), new(px, py), corner);
        Assert.Equal(x, result.X, 9);
        Assert.Equal(y, result.Y, 9);
        Assert.Equal(width, result.Width, 9);
        Assert.Equal(height, result.Height, 9);
    }

    [Theory]
    [InlineData("nw", 5, 5)]
    [InlineData("ne", -5, 5)]
    [InlineData("sw", 5, -5)]
    [InlineData("se", -5, -5)]
    public void DraggingThroughTheOppositeCornerKeepsMinimumSizeWithoutFlipping(string corner, double x, double y)
    {
        var region = new EditorEffectRegion(.2, .3, .4, .45);
        var result = EditorEffectRegionMath.ResizeCorner(region, new(x, y), corner);
        Assert.Equal(.02, result.Width, 9);
        Assert.Equal(.02, result.Height, 9);
        if (corner.EndsWith('w')) Assert.Equal(region.Right, result.Right, 9);
        else Assert.Equal(region.X, result.X, 9);
        if (corner.StartsWith('n')) Assert.Equal(region.Bottom, result.Bottom, 9);
        else Assert.Equal(region.Y, result.Y, 9);
    }

    [Theory]
    [InlineData("nw", -5, -5)]
    [InlineData("ne", 5, -5)]
    [InlineData("sw", -5, 5)]
    [InlineData("se", 5, 5)]
    public void ExpandingEveryCornerStaysInsideTheSource(string corner, double x, double y)
    {
        var result = EditorEffectRegionMath.ResizeCorner(new(.2, .3, .4, .45), new(x, y), corner);
        Assert.InRange(result.X, 0, 1);
        Assert.InRange(result.Y, 0, 1);
        Assert.InRange(result.Right, 0, 1);
        Assert.InRange(result.Bottom, 0, 1);
    }

    [Fact]
    public void UnchangedOffCenterCornerGrabDoesNotResize()
    {
        var region = new EditorEffectRegion(.2, .3, .4, .45);
        var grab = new EditorEffectPoint(.615, .765);
        var grabOffset = new EditorEffectPoint(grab.X - region.Right, grab.Y - region.Bottom);
        var adjusted = new EditorEffectPoint(grab.X - grabOffset.X, grab.Y - grabOffset.Y);
        var result = EditorEffectRegionMath.ResizeCorner(region, adjusted, "se");
        Assert.Equal(region.X, result.X, 9);
        Assert.Equal(region.Y, result.Y, 9);
        Assert.Equal(region.Width, result.Width, 9);
        Assert.Equal(region.Height, result.Height, 9);
    }

    [Fact]
    public void MalformedRegionAndPointerInputsRemainFiniteAndBounded()
    {
        var result = EditorEffectRegionMath.ResizeCorner(new(double.NaN, double.PositiveInfinity, -.1, double.NaN),
            new(double.NaN, double.NegativeInfinity), "nw");
        Assert.True(double.IsFinite(result.X));
        Assert.True(double.IsFinite(result.Y));
        Assert.InRange(result.Width, .02, 1);
        Assert.InRange(result.Height, .02, 1);
        Assert.InRange(result.Right, 0, 1);
        Assert.InRange(result.Bottom, 0, 1);
    }
}
