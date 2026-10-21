using Klipp.Core.Editing;

namespace Klipp.Tests.Editing;

public sealed class EditorTimelineMathTests
{
    [Theory]
    [InlineData(60)]
    [InlineData(30)]
    [InlineData(24)]
    public void FrameSnappingRemainsAccurateOnLongRecordings(double frameRate)
    {
        var frame = (long)(3600 * frameRate) + 1;
        var seconds = frame / frameRate;

        Assert.Equal(seconds, EditorTimelineMath.SnapToFrame(seconds + 0.1 / frameRate, frameRate), 8);
    }

    [Fact]
    public void PointerTimePreservesSubSecondPrecisionAcrossLongViewport()
    {
        var time = EditorTimelineMath.PointerTime(211.5, 88, 1000, 10, 60);

        Assert.Equal(17.41, time, 6);
    }

    [Theory]
    [InlineData(-50, 10)]
    [InlineData(2000, 70)]
    public void PointerTimeClampsToVisibleRail(double pointerX, double expected)
    {
        Assert.Equal(expected, EditorTimelineMath.PointerTime(pointerX, 88, 1000, 10, 60));
    }

    [Fact]
    public void ZoomKeepsTheTimeUnderPointerAtTheSamePosition()
    {
        var start = EditorTimelineMath.ZoomViewportStart(45, 0.75, 20, 100);

        Assert.Equal(30, start);
        Assert.Equal(45, start + 0.75 * 20);
    }

    [Theory]
    [InlineData(0, 0.5, 0)]
    [InlineData(100, 0.5, 80)]
    public void ZoomViewportClampsAtProjectEdges(double anchor, double ratio, double expected)
    {
        Assert.Equal(expected, EditorTimelineMath.ZoomViewportStart(anchor, ratio, 20, 100));
    }

    [Theory]
    [InlineData(2, 1200)]
    [InlineData(3600, 1000)]
    [InlineData(86400, 800)]
    [InlineData(3600, 150)]
    public void RulerDensityIsBoundedByViewportWidth(double duration, double width)
    {
        var interval = EditorTimelineMath.MajorTickSeconds(duration, width);

        Assert.True(interval > 0);
        Assert.True(duration / interval <= Math.Max(1, width / 76));
    }

    [Fact]
    public void NarrowClipsRetainADraggableMiddleRegion()
    {
        Assert.Equal(6, EditorTimelineMath.EdgeHitWidth(18));
        Assert.Equal(12, EditorTimelineMath.EdgeHitWidth(120));
    }
}
