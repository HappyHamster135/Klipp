using Klipp.Core.Editing;

namespace Klipp.Tests.Editing;

public sealed class EditorVideoSelectionMathTests
{
    [Fact]
    public void LandscapeVideoUsesItsExactEdgesWithoutAnArbitraryInset()
    {
        var fit = EditorSourceFitMath.Calculate(1280, 720, 1920, 1080, 0, 0, 0, 0);
        Assert.Equal(new EditorVideoSelectionBounds(0, 0, 1280, 720),
            EditorVideoSelectionMath.GetSourceBounds(fit));
    }

    [Fact]
    public void PortraitVideoExcludesEmptyCanvasOnEitherSide()
    {
        var fit = EditorSourceFitMath.Calculate(1920, 1080, 1080, 1920, 0, 0, 0, 0);
        Assert.Equal(new EditorVideoSelectionBounds(656.25, 0, 607.5, 1080),
            EditorVideoSelectionMath.GetSourceBounds(fit));
    }

    [Fact]
    public void SquareCanvasExcludesLetterboxingAboveAndBelowLandscapeVideo()
    {
        var fit = EditorSourceFitMath.Calculate(1000, 1000, 1920, 1080, 0, 0, 0, 0);
        var bounds = EditorVideoSelectionMath.GetSourceBounds(fit);
        Assert.Equal(0, bounds.Left, 8);
        Assert.Equal(218.75, bounds.Top, 8);
        Assert.Equal(1000, bounds.Width, 8);
        Assert.Equal(562.5, bounds.Height, 8);
    }

    [Fact]
    public void AsymmetricCropFollowsRefittedRetainedImageInsteadOfOriginalSourceEdges()
    {
        var fit = EditorSourceFitMath.Calculate(1920, 1080, 1920, 1080, .25, 0, 0, 0);
        var bounds = EditorVideoSelectionMath.GetSourceBounds(fit);
        Assert.Equal(240, bounds.Left, 8);
        Assert.Equal(0, bounds.Top, 8);
        Assert.Equal(1440, bounds.Width, 8);
        Assert.Equal(1080, bounds.Height, 8);
    }

    [Fact]
    public void CroppedPortraitRefitsToCanvasAndRemainsCentered()
    {
        var fit = EditorSourceFitMath.Calculate(1280, 720, 1080, 1920, .1, .2, .3, .1);
        var bounds = EditorVideoSelectionMath.GetSourceBounds(fit);
        Assert.Equal(1280 / 2d, bounds.Left + bounds.Width / 2, 8);
        Assert.Equal(720 / 2d, bounds.Top + bounds.Height / 2, 8);
        Assert.Equal(0, bounds.Top, 8);
        Assert.Equal(720, bounds.Height, 8);
        Assert.Equal((1080 * .6) / (1920 * .7), bounds.Width / bounds.Height, 8);
    }

    [Fact]
    public void PortraitImageIsPlacedInsideTheCenteredCropInsteadOfAtTheCanvasLeft()
    {
        var fit = EditorSourceFitMath.Calculate(1280, 720, 360, 640, 0, 0, 0, 0);
        var image = EditorVideoSelectionMath.GetSourceImageBounds(fit);
        Assert.Equal(new EditorVideoSelectionBounds(437.5, 0, 405, 720), image);
        Assert.Equal(EditorVideoSelectionMath.GetSourceBounds(fit), image);
    }

    [Fact]
    public void LandscapeImageInSquareCanvasKeepsItsVerticalPlacement()
    {
        var fit = EditorSourceFitMath.Calculate(1000, 1000, 1920, 1080, 0, 0, 0, 0);
        var image = EditorVideoSelectionMath.GetSourceImageBounds(fit);
        Assert.Equal(0, image.Left, 8);
        Assert.Equal(218.75, image.Top, 8);
        Assert.Equal(1000, image.Width, 8);
        Assert.Equal(562.5, image.Height, 8);
    }

    [Theory]
    [InlineData(1920, 1080, 1920, 1080, .25, 0, 0, 0)]
    [InlineData(1280, 720, 360, 640, .1, .2, .3, .1)]
    [InlineData(1000, 1000, 1920, 1080, .2, .15, .1, .05)]
    public void CroppedImagePlacementKeepsTheRetainedSourcePixelsAlignedWithTheClip(
        double cw, double ch, double sw, double sh, double left, double top, double right, double bottom)
    {
        var fit = EditorSourceFitMath.Calculate(cw, ch, sw, sh, left, top, right, bottom);
        var image = EditorVideoSelectionMath.GetSourceImageBounds(fit);
        var crop = EditorVideoSelectionMath.GetSourceBounds(fit);
        Assert.Equal(sw / sh, image.Width / image.Height, 8);
        Assert.Equal(crop.Left, image.Left + left * image.Width, 8);
        Assert.Equal(crop.Top, image.Top + top * image.Height, 8);
        Assert.Equal(crop.Width, image.Width * (1 - left - right), 8);
        Assert.Equal(crop.Height, image.Height * (1 - top - bottom), 8);
    }
}
