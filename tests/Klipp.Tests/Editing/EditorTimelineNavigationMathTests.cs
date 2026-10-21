using Klipp.Core.Editing;

namespace Klipp.Tests.Editing;

public sealed class EditorTimelineNavigationMathTests
{
    [Fact]
    public void EditNavigationVisitsBothSidesOfAGap()
    {
        double[] edges = [0, 2, 5, 7];
        Assert.Equal(2, EditorTimelineNavigationMath.AdjacentBoundary(edges, 1, 8, 30, next: true));
        Assert.Equal(5, EditorTimelineNavigationMath.AdjacentBoundary(edges, 2, 8, 30, next: true));
        Assert.Equal(2, EditorTimelineNavigationMath.AdjacentBoundary(edges, 5, 8, 30, next: false));
    }

    [Fact]
    public void FrameRoundingCannotLeaveNavigationStuckAtTheSameEdge()
    {
        double[] edges = [2.0000001, 3];
        Assert.Equal(3, EditorTimelineNavigationMath.AdjacentBoundary(edges, 2, 4, 60, next: true));
        Assert.Equal(0, EditorTimelineNavigationMath.AdjacentBoundary(edges, 2, 4, 60, next: false));
    }

    [Fact]
    public void BoundariesIncludeProjectStartAndEndEvenOnAnEmptyTrack()
    {
        Assert.Equal(8, EditorTimelineNavigationMath.AdjacentBoundary([], 2, 8, 30, next: true));
        Assert.Equal(0, EditorTimelineNavigationMath.AdjacentBoundary([], 2, 8, 30, next: false));
        Assert.Equal(0, EditorTimelineNavigationMath.AdjacentBoundary([], 0, 8, 30, next: false));
        Assert.Equal(8, EditorTimelineNavigationMath.AdjacentBoundary([], 8, 8, 30, next: true));
    }

    [Fact]
    public void OutOfBoundsAndInvalidEdgesCannotSeekOutsideTheProject()
    {
        double[] edges = [-1, double.NaN, double.PositiveInfinity, 20];
        Assert.Equal(8, EditorTimelineNavigationMath.AdjacentBoundary(edges, 1, 8, 30, next: true));
        Assert.Equal(0, EditorTimelineNavigationMath.AdjacentBoundary(edges, 1, 8, 30, next: false));
    }
}
