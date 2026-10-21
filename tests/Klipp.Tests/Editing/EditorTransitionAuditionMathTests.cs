using Klipp.Core.Editing;

namespace Klipp.Tests.Editing;

public sealed class EditorTransitionAuditionMathTests
{
    [Theory]
    [InlineData("wipeleft", 0.75, 0, 0.25, 1)]
    [InlineData("wiperight", 0, 0, 0.25, 1)]
    [InlineData("wipeup", 0, 0.75, 1, 0.25)]
    [InlineData("wipedown", 0, 0, 1, 0.25)]
    public void WipesRevealIncomingFromTheNamedDirection(string style, double x, double y, double width, double height)
    {
        var frame = EditorTransitionAuditionMath.Evaluate(style, 0.25);
        Assert.Equal(x, frame.RevealX);
        Assert.Equal(y, frame.RevealY);
        Assert.Equal(width, frame.RevealWidth);
        Assert.Equal(height, frame.RevealHeight);
    }

    [Theory]
    [InlineData("slideleft", -0.25, 0, 0.75, 0)]
    [InlineData("slideright", 0.25, 0, -0.75, 0)]
    [InlineData("slideup", 0, -0.25, 0, 0.75)]
    [InlineData("slidedown", 0, 0.25, 0, -0.75)]
    public void SlidesKeepTheTwoFramesTouchingWithoutAGap(string style, double ax, double ay, double bx, double by)
    {
        var frame = EditorTransitionAuditionMath.Evaluate(style, 0.25);
        Assert.Equal(ax, frame.OutgoingX);
        Assert.Equal(ay, frame.OutgoingY);
        Assert.Equal(bx, frame.IncomingX);
        Assert.Equal(by, frame.IncomingY);
        Assert.Equal(1, frame.IncomingOpacity);
    }

    [Fact]
    public void DipsPassThroughSolidBlackOrWhiteAtTheMidpoint()
    {
        var black = EditorTransitionAuditionMath.Evaluate("fadeblack", 0.5);
        Assert.Equal(0, black.OutgoingOpacity);
        Assert.Equal(0, black.IncomingOpacity);
        var white = EditorTransitionAuditionMath.Evaluate("fadewhite", 0.5);
        Assert.Equal(1, white.WhiteOpacity);
        Assert.Equal(0, EditorTransitionAuditionMath.Evaluate("fadewhite", 1).WhiteOpacity);
    }

    [Fact]
    public void DissolveBlendsButCutChangesOnceAtTheMidpoint()
    {
        Assert.Equal(0.25, EditorTransitionAuditionMath.Evaluate("dissolve", 0.25).IncomingOpacity);
        Assert.Equal(0, EditorTransitionAuditionMath.Evaluate("cut", 0.499).IncomingOpacity);
        Assert.Equal(1, EditorTransitionAuditionMath.Evaluate("cut", 0.5).IncomingOpacity);
    }

    [Theory]
    [InlineData(0.25)]
    [InlineData(0.5)]
    [InlineData(1)]
    public void PreviewHoldsBothEndpointsWithoutChangingTheActualTransitionDuration(double duration)
    {
        Assert.Equal(0, EditorTransitionAuditionMath.ProgressAt(0, duration));
        Assert.Equal(0, EditorTransitionAuditionMath.ProgressAt(EditorTransitionAuditionMath.HoldSeconds, duration));
        Assert.Equal(0.5, EditorTransitionAuditionMath.ProgressAt(EditorTransitionAuditionMath.HoldSeconds + duration / 2, duration), 10);
        Assert.Equal(1, EditorTransitionAuditionMath.ProgressAt(EditorTransitionAuditionMath.CycleSeconds(duration), duration));
    }

    [Theory]
    [InlineData("circleopen")]
    [InlineData("rectcrop")]
    [InlineData("horzopen")]
    [InlineData("vertopen")]
    [InlineData("diagtl")]
    public void ShapeTransitionsRevealOnlyIncomingByTheEndAndNoneAtTheStart(string style)
    {
        var start = EditorTransitionAuditionMath.Evaluate(style, 0);
        var end = EditorTransitionAuditionMath.Evaluate(style, 1);
        for (var y = 0.5; y < 90; y += 7)
        for (var x = 0.5; x < 160; x += 7)
        {
            Assert.False(EditorTransitionAuditionMath.IsIncomingVisible(start, x, y, 160, 90));
            Assert.True(EditorTransitionAuditionMath.IsIncomingVisible(end, x, y, 160, 90));
        }
    }

    [Fact]
    public void IrisUsesACircleInPicturePixelsRatherThanStretchingWithAspectRatio()
    {
        var frame = EditorTransitionAuditionMath.Evaluate("circleopen", 0.25);
        Assert.True(EditorTransitionAuditionMath.IsIncomingVisible(frame, 180, 90, 320, 180));
        Assert.True(EditorTransitionAuditionMath.IsIncomingVisible(frame, 160, 110, 320, 180));
        Assert.False(EditorTransitionAuditionMath.IsIncomingVisible(frame, 230, 90, 320, 180));
        Assert.False(EditorTransitionAuditionMath.IsIncomingVisible(frame, 160, 160, 320, 180));
    }

    [Fact]
    public void NewFamiliesHaveDistinctCoverageAtTheSameProgress()
    {
        var box = EditorTransitionAuditionMath.Evaluate("rectcrop", 0.5);
        var horizontal = EditorTransitionAuditionMath.Evaluate("horzopen", 0.5);
        var vertical = EditorTransitionAuditionMath.Evaluate("vertopen", 0.5);
        var diagonal = EditorTransitionAuditionMath.Evaluate("diagtl", 0.5);
        Assert.False(EditorTransitionAuditionMath.IsIncomingVisible(box, 80, 5, 160, 90));
        Assert.True(EditorTransitionAuditionMath.IsIncomingVisible(horizontal, 80, 5, 160, 90));
        Assert.True(EditorTransitionAuditionMath.IsIncomingVisible(vertical, 5, 45, 160, 90));
        Assert.False(EditorTransitionAuditionMath.IsIncomingVisible(box, 5, 45, 160, 90));
        Assert.True(EditorTransitionAuditionMath.IsIncomingVisible(diagonal, 10, 10, 160, 90));
        Assert.False(EditorTransitionAuditionMath.IsIncomingVisible(diagonal, 150, 80, 160, 90));
    }

    [Fact]
    public void DiagonalPolygonCrossesThePictureWithoutLeavingItsBounds()
    {
        Assert.Equal(new[] { (0d, 0d), (0.5, 0d), (0d, 0.5) }, EditorTransitionAuditionMath.GetRevealPolygon(0.25));
        Assert.Equal(new[] { (0d, 0d), (1d, 0d), (1d, 0.5), (0.5, 1d), (0d, 1d) }, EditorTransitionAuditionMath.GetRevealPolygon(0.75));
    }
}
