using Klipp.Core.Editing;

namespace Klipp.Tests.Editing;

public sealed class EditorSourceFitMathTests
{
    [Theory]
    [InlineData(1920, 1080, 1920, 1080)]
    [InlineData(1920, 1080, 1080, 1920)]
    [InlineData(1080, 1920, 3840, 2160)]
    [InlineData(947, 509, 1237, 853)]
    public void NoCropPreservesTheOriginalUniformFitExactly(double cw, double ch, double sw, double sh)
    {
        var fit = EditorSourceFitMath.Calculate(cw, ch, sw, sh, 0, 0, 0, 0);
        Assert.Equal(1, fit.RefitScale);
        Assert.Equal(0, fit.TranslateX);
        Assert.Equal(0, fit.TranslateY);
        Assert.Equal(sw / sh, fit.SourceWidth / fit.SourceHeight, 8);
        Assert.Equal(cw / 2, fit.SourceLeft + fit.SourceWidth / 2, 8);
        Assert.Equal(ch / 2, fit.SourceTop + fit.SourceHeight / 2, 8);
        Assert.Equal(fit.SourceLeft, fit.CropLeft);
        Assert.Equal(fit.SourceTop, fit.CropTop);
        Assert.Equal(fit.SourceWidth, fit.CropWidth);
        Assert.Equal(fit.SourceHeight, fit.CropHeight);
    }

    [Fact]
    public void PortraitSourceHasTheCorrectOriginalLetterboxedRectangle()
    {
        var fit = EditorSourceFitMath.Calculate(1920, 1080, 1080, 1920, 0, 0, 0, 0);
        Assert.Equal(656.25, fit.SourceLeft);
        Assert.Equal(0, fit.SourceTop);
        Assert.Equal(607.5, fit.SourceWidth);
        Assert.Equal(1080, fit.SourceHeight);
    }

    [Theory]
    [InlineData(1920, 1080, 3840, 2160, .1, .2, .3, .1)]
    [InlineData(1920, 1080, 1080, 1920, .2, .1, .1, .2)]
    [InlineData(1080, 1920, 3840, 2160, .05, .3, .4, .1)]
    [InlineData(500, 500, 1920, 1080, .25, 0, .25, 0)]
    public void RefitCentersTheCropAndKeepsItsAspectWithoutClipping(double cw, double ch, double sw, double sh,
        double left, double top, double right, double bottom)
    {
        var fit = EditorSourceFitMath.Calculate(cw, ch, sw, sh, left, top, right, bottom);
        var x1 = fit.CropLeft * fit.RefitScale + fit.TranslateX;
        var y1 = fit.CropTop * fit.RefitScale + fit.TranslateY;
        var x2 = (fit.CropLeft + fit.CropWidth) * fit.RefitScale + fit.TranslateX;
        var y2 = (fit.CropTop + fit.CropHeight) * fit.RefitScale + fit.TranslateY;
        Assert.Equal(cw / 2, (x1 + x2) / 2, 8);
        Assert.Equal(ch / 2, (y1 + y2) / 2, 8);
        Assert.InRange(x1, -1e-8, cw);
        Assert.InRange(y1, -1e-8, ch);
        Assert.InRange(x2, 0, cw + 1e-8);
        Assert.InRange(y2, 0, ch + 1e-8);
        Assert.True(Math.Abs(x1) < 1e-8 || Math.Abs(y1) < 1e-8);
        Assert.Equal(sw * (1 - left - right) / (sh * (1 - top - bottom)), (x2 - x1) / (y2 - y1), 8);
    }

    [Fact]
    public void NormalizedSourcePolygonCoordinatesMapThroughTheSameCropRefit()
    {
        var fit = EditorSourceFitMath.Calculate(1920, 1080, 1080, 1920, .2, .1, .1, .2);
        double X(double normalized) => (fit.SourceLeft + normalized * fit.SourceWidth) * fit.RefitScale + fit.TranslateX;
        double Y(double normalized) => (fit.SourceTop + normalized * fit.SourceHeight) * fit.RefitScale + fit.TranslateY;
        Assert.Equal(960, X(.55), 8);
        Assert.Equal(540, Y(.45), 8);
        Assert.Equal(fit.CropLeft * fit.RefitScale + fit.TranslateX, X(.2), 8);
        Assert.Equal(fit.CropTop * fit.RefitScale + fit.TranslateY, Y(.1), 8);
        Assert.Equal((fit.CropLeft + fit.CropWidth) * fit.RefitScale + fit.TranslateX, X(.9), 8);
        Assert.Equal((fit.CropTop + fit.CropHeight) * fit.RefitScale + fit.TranslateY, Y(.8), 8);
    }

    [Theory]
    [InlineData(0, 1080)]
    [InlineData(1920, -1)]
    [InlineData(double.NaN, 1080)]
    [InlineData(1920, double.PositiveInfinity)]
    public void UnknownNaturalDimensionsFallBackToCanvas(double sw, double sh)
    {
        var fit = EditorSourceFitMath.Calculate(1280, 720, sw, sh, 0, 0, 0, 0);
        Assert.Equal(0, fit.SourceLeft);
        Assert.Equal(0, fit.SourceTop);
        Assert.Equal(1280, fit.SourceWidth);
        Assert.Equal(720, fit.SourceHeight);
        Assert.Equal(1, fit.RefitScale);
    }

    [Fact]
    public void MalformedCropInputsKeepFinitePositiveGeometry()
    {
        var fit = EditorSourceFitMath.Calculate(1280, 720, 1920, 1080, 1, double.NaN, .5, double.PositiveInfinity);
        Assert.True(fit.CropWidth > 0);
        Assert.True(fit.CropHeight > 0);
        Assert.True(double.IsFinite(fit.RefitScale));
        Assert.True(double.IsFinite(fit.TranslateX));
        Assert.True(double.IsFinite(fit.TranslateY));
    }
}
