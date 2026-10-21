using Klipp.Core.Editing;

namespace Klipp.Tests.Editing;

public sealed class EditorSelectionEditMathTests
{
    [Theory]
    [InlineData(1, -2, -2)]
    [InlineData(2, -2, -1)]
    [InlineData(.5, -5, -4)]
    public void LeadingExtensionStopsAtAvailableSource(double speed, double request, double expected)
    {
        var clip = new EditorSelectionTrimRange(10, 2, 8, 20, speed);
        Assert.Equal(expected, EditorSelectionEditMath.ClampTrimDelta(request, true, [clip]), 8);
    }

    [Fact]
    public void LeadingExtensionCannotMoveTimelineBeforeZero()
    {
        var clip = new EditorSelectionTrimRange(.25, 10, 14, 20, 1);
        Assert.Equal(-.25, EditorSelectionEditMath.ClampTrimDelta(-2, true, [clip]), 8);
    }

    [Fact]
    public void StartTrimPreservesMinimumSourceDurationAtPlaybackSpeed()
    {
        var clip = new EditorSelectionTrimRange(5, 2, 8, 20, 2);
        Assert.Equal(2.95, EditorSelectionEditMath.ClampTrimDelta(100, true, [clip]), 8);
    }

    [Fact]
    public void EndExtensionStopsAtSourceEndAtPlaybackSpeed()
    {
        var clip = new EditorSelectionTrimRange(5, 2, 8, 10, .5);
        Assert.Equal(4, EditorSelectionEditMath.ClampTrimDelta(100, false, [clip]), 8);
    }

    [Fact]
    public void EndTrimPreservesMinimumSourceDurationAtPlaybackSpeed()
    {
        var clip = new EditorSelectionTrimRange(5, 2, 8, 20, 2);
        Assert.Equal(-2.95, EditorSelectionEditMath.ClampTrimDelta(-100, false, [clip]), 8);
    }

    [Fact]
    public void MixedSpeedSelectionUsesOneDeltaBoundedByTheShortestMember()
    {
        EditorSelectionTrimRange[] clips = [new(5, 2, 8, 20, 1), new(8, 4, 6, 10, 2)];
        var delta = EditorSelectionEditMath.ClampTrimDelta(3, true, clips);
        Assert.Equal(.95, delta, 8);
        foreach (var clip in clips)
        {
            var startAfter = clip.TimelineStartSeconds + delta;
            var sourceAfter = clip.TrimStartSeconds + delta * clip.PlaybackSpeed;
            var timelineEndBefore = clip.TimelineStartSeconds + (clip.TrimEndSeconds - clip.TrimStartSeconds) / clip.PlaybackSpeed;
            var timelineEndAfter = startAfter + (clip.TrimEndSeconds - sourceAfter) / clip.PlaybackSpeed;
            Assert.Equal(timelineEndBefore, timelineEndAfter, 8);
            Assert.True(clip.TrimEndSeconds - sourceAfter >= .1 - .000001);
        }
        Assert.Equal(clips[1].TimelineStartSeconds - clips[0].TimelineStartSeconds,
            (clips[1].TimelineStartSeconds + delta) - (clips[0].TimelineStartSeconds + delta), 8);
    }

    [Fact]
    public void MixedSelectionEndExtensionUsesTheMostLimitedSourceHandle()
    {
        EditorSelectionTrimRange[] clips = [new(5, 2, 8, 20, 1), new(8, 4, 9, 10, 2)];
        Assert.Equal(.5, EditorSelectionEditMath.ClampTrimDelta(3, false, clips), 8);
    }

    [Fact]
    public void GroupMoveStopsEveryMemberTogetherAtZero()
    {
        double[] starts = [.2, 3, 8.5];
        var delta = EditorAdvancedEditMath.ClampGroupMove(-5, starts);
        Assert.Equal(-.2, delta, 8);
        Assert.Equal(0, starts[0] + delta, 8);
        Assert.Equal(starts[2] - starts[1], (starts[2] + delta) - (starts[1] + delta), 8);
    }

    [Fact]
    public void ExhaustedHandlePreventsPartialGroupTrim()
    {
        EditorSelectionTrimRange[] clips = [new(5, 2, 8, 20, 1), new(8, 0, 10, 10, 1)];
        Assert.Equal(0, EditorSelectionEditMath.ClampTrimDelta(-1, true, clips));
        Assert.Equal(0, EditorSelectionEditMath.ClampTrimDelta(1, false, clips));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void InvalidPlaybackSpeedCannotPartiallyTrimAGroup(double invalidSpeed)
    {
        EditorSelectionTrimRange[] clips = [new(5, 2, 8, 20, 1), new(8, 0, 10, 10, invalidSpeed)];
        Assert.Equal(0, EditorSelectionEditMath.ClampTrimDelta(1, true, clips));
    }

    [Fact]
    public void EmptyOrNonFiniteRequestDoesNotMakeAnEdit()
    {
        Assert.Equal(0, EditorSelectionEditMath.ClampTrimDelta(1, true, []));
        Assert.Equal(0, EditorSelectionEditMath.ClampTrimDelta(double.NaN, false, [new(5, 2, 8, 20, 1)]));
    }
}
