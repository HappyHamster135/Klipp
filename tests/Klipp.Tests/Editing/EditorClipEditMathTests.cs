using Klipp.Core.Editing;

namespace Klipp.Tests.Editing;

public sealed class EditorClipEditMathTests
{
    [Theory]
    [InlineData(3, 4, 9, 8)]
    [InlineData(3, 4, 1, 0)]
    [InlineData(3, 4, 20, 19)]
    public void FreeMoveKeepsTheGrabOffsetAndAllowsGaps(double original, double grabbed, double pointer, double expected)
    {
        Assert.Equal(expected, EditorClipEditMath.MoveTimelineStart(original, grabbed, pointer, 60));
    }

    [Fact]
    public void FreeMoveKeepsFramePrecision()
    {
        Assert.Equal(181 / 60d, EditorClipEditMath.MoveTimelineStart(3, 4, 4.02, 60), 8);
    }

    [Theory]
    [InlineData(10, 4, 1, 6)]
    [InlineData(10, 4, 2, 8)]
    [InlineData(10, 4, 0.5, 2)]
    [InlineData(1, 4, 1, 0)]
    public void LeadingTrimCanRevealAvailableFootageWithoutCrossingZero(double start, double sourceTrim, double speed, double expected)
    {
        Assert.Equal(expected, EditorClipEditMath.EarliestTrimStart(start, sourceTrim, speed));
    }

    [Theory]
    [InlineData(1, 14)]
    [InlineData(2, 18)]
    [InlineData(0.5, 12)]
    public void CutsMapProjectTimeIntoTrimmedSource(double speed, double expectedSource)
    {
        Assert.True(EditorClipEditMath.TrySplitSource(24, 20, 10, 30, speed, out var source));
        Assert.Equal(expectedSource, source);
    }

    [Theory]
    [InlineData(19)]
    [InlineData(20)]
    [InlineData(20.01)]
    [InlineData(29.99)]
    [InlineData(30)]
    [InlineData(40)]
    public void CutRejectsEdgesAndPlayheadsOutsideTheClip(double playhead)
    {
        Assert.False(EditorClipEditMath.TrySplitSource(playhead, 20, 10, 30, 2, out _));
    }

    [Fact]
    public void ExactlyMinimumLengthSegmentsAreAllowed()
    {
        Assert.True(EditorClipEditMath.TrySplitSource(0.05, 0, 10, 10.2, 2, out var source));
        Assert.Equal(10.1, source, 8);
    }

    [Fact]
    public void AdjacentClipsHaveUnambiguousBoundaryOwnership()
    {
        Assert.False(EditorClipEditMath.Contains(5, 0, 0, 10, 2));
        Assert.True(EditorClipEditMath.Contains(5, 5, 10, 20, 2));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void InvalidPlaybackSpeedCannotMakeAnEdit(double speed)
    {
        Assert.False(EditorClipEditMath.TrySplitSource(1, 0, 0, 10, speed, out _));
        Assert.False(EditorClipEditMath.Contains(1, 0, 0, 10, speed));
    }
}
