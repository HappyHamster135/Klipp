using Klipp.Core.Editing;

namespace Klipp.Tests.Editing;

public sealed class EditorPreviewCutMathTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(0.25)]
    [InlineData(2)]
    public void AdjacentSplitFootageKeepsItsDecoderRunning(double speed)
    {
        var left = new EditorPreviewCutSegment(3, 10, 14, speed);
        var right = new EditorPreviewCutSegment(left.TimelineEndSeconds, 14, 18, speed);

        Assert.True(EditorPreviewCutMath.CanContinueSource(left, right, sameSource: true));
    }

    [Fact]
    public void AdjacentTimelineClipsWithDifferentSourceInPointsNeedPreparedDecoder()
    {
        var left = new EditorPreviewCutSegment(0, 0, 4, 1);
        var right = new EditorPreviewCutSegment(4, 12, 16, 1);

        Assert.False(EditorPreviewCutMath.CanContinueSource(left, right, sameSource: true));
        Assert.False(EditorPreviewCutMath.CanContinueSource(left, right with { TrimStartSeconds = 4 }, sameSource: false));
    }

    [Theory]
    [InlineData(4.1, 1)]
    [InlineData(3.9, 1)]
    [InlineData(4, 2)]
    public void GapOverlapOrSpeedChangeCannotReuseContiguousClock(double start, double speed)
    {
        Assert.False(EditorPreviewCutMath.CanContinueSource(
            new(0, 0, 4, 1), new(start, 4, 8, speed), sameSource: true));
    }

    [Fact]
    public void PreparedFrameAllowsTimerOvershootAtFastPlaybackButRejectsStaleSeek()
    {
        Assert.True(EditorPreviewCutMath.CanUsePreparedFrame(12, 12.13, 4, 60));
        Assert.False(EditorPreviewCutMath.CanUsePreparedFrame(12, 13, 4, 60));
        Assert.False(EditorPreviewCutMath.CanUsePreparedFrame(12, 14, 1, 30));
    }

    [Fact]
    public void SlowPlaybackPreparedFrameToleranceIsMeasuredInProjectTime()
    {
        // A 120 ms source offset at quarter speed is 480 ms of project time.
        Assert.False(EditorPreviewCutMath.CanUsePreparedFrame(12, 12.12, 0.25, 60));
        Assert.False(EditorPreviewCutMath.CanUsePreparedFrame(12, 12.031, 0.25, 60));
        Assert.True(EditorPreviewCutMath.CanUsePreparedFrame(12, 12.029, 0.25, 60));
    }
}
