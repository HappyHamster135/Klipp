using Klipp.Core.Editing;

namespace Klipp.Tests.Editing;

public sealed class EditorTimelineInteractionMathTests
{
    [Theory]
    [InlineData(99, -1)]
    [InlineData(100, 0)]
    [InlineData(235, 1)]
    [InlineData(250, -1)]
    [InlineData(501, -1)]
    public void TransitionDropsRequireAVisibleNearbyBoundary(double pointer, int expected)
    {
        Assert.Equal(expected, EditorTimelineInteractionMath.FindNearestBoundaryIndex(pointer,
            new double[] { 110, 200, 490, 900 }, 100, 400));
    }

    [Fact]
    public void BoundaryTiesAreStable()
    {
        Assert.Equal(0, EditorTimelineInteractionMath.FindNearestBoundaryIndex(150,
            new double[] { 140, 160 }, 100, 400));
    }

    [Fact]
    public void AutoPanHasNoCenterDriftAndScalesWithTime()
    {
        Assert.Equal(0, EditorTimelineInteractionMath.AutoPanDeltaSeconds(300, 100, 400, 20, 0.016));
        var delta = EditorTimelineInteractionMath.AutoPanDeltaSeconds(500, 100, 400, 20, 0.016);
        Assert.Equal(delta * 2, EditorTimelineInteractionMath.AutoPanDeltaSeconds(500, 100, 400, 20, 0.032), 8);
        Assert.Equal(-delta, EditorTimelineInteractionMath.AutoPanDeltaSeconds(100, 100, 400, 20, 0.016), 8);
    }

    [Fact]
    public void AutoPanCapsSuspendedTimerJumps()
    {
        Assert.Equal(0.6, EditorTimelineInteractionMath.AutoPanDeltaSeconds(900, 100, 400, 10, 20), 8);
        Assert.Equal(0, EditorTimelineInteractionMath.AutoPanDeltaSeconds(double.NaN, 100, 400, 10, 1));
    }

    [Theory]
    [InlineData(12, 3)]
    [InlineData(20, 5)]
    [InlineData(36, 9)]
    [InlineData(100, 16)]
    public void TrimTargetsPreserveTheClipBody(double width, double expected)
    {
        var edge = EditorTimelineInteractionMath.TrimEdgeHitWidth(width);
        Assert.Equal(expected, edge);
        Assert.True(width - edge * 2 >= Math.Min(18, width / 2));
    }
}
