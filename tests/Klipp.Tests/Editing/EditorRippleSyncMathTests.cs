using Klipp.Core.Editing;

namespace Klipp.Tests.Editing;

public sealed class EditorRippleSyncMathTests
{
    private static readonly EditorRippleTrackState[] Tracks =
    [new("video", true, false), new("voice", true, false), new("music", false, false), new("locked", true, true)];

    [Fact]
    public void RippleIncludesEnabledTracksAndTheEditedTrackButNeverAnEditingLockedTrack()
    {
        var participants = EditorRippleSyncMath.ParticipatingTracks(Tracks, ["music", "locked"]);
        Assert.True(participants.SetEquals(["video", "voice", "music"]));
        Assert.False(EditorRippleSyncMath.ParticipatingTracks(Tracks, ["video"]).Contains("music"));
    }

    [Fact]
    public void SharedRemovalMovesVideoAndAudioByTheSameAmountAndLeavesOptedOutTracksAlone()
    {
        var plan = EditorRippleSyncMath.PlanRemoval(Tracks,
            [new("video", 2, 4), new("voice", 2, 4)],
            [new("video", 4, 8), new("voice", 4.5, 8.5), new("music", 0, 12), new("locked", 0, 12)], 60);
        Assert.Equal(2, plan.RemovedSeconds);
        Assert.Equal(2, EditorAdvancedEditMath.RippleStart(4, plan.Intervals));
        Assert.Equal(2.5, EditorAdvancedEditMath.RippleStart(4.5, plan.Intervals));
        Assert.False(plan.TrackIds.Contains("music"));
        Assert.False(plan.TrackIds.Contains("locked"));
    }

    [Fact]
    public void OverlappingSelectionAcrossTracksDoesNotDoubleCountTheRemovedTime()
    {
        var plan = EditorRippleSyncMath.PlanRemoval(Tracks,
            [new("video", 2, 5), new("voice", 4, 7), new("video", 9, 10)], [], 60);
        Assert.Equal(6, plan.RemovedSeconds);
        Assert.Equal(6, EditorAdvancedEditMath.RippleStart(12, plan.Intervals));
    }

    [Fact]
    public void CrossingMediaPreventsAnImplicitCutDuringGapClosure()
    {
        var plan = EditorRippleSyncMath.PlanRemoval(Tracks, [new("video", 2, 5)], [new("voice", 0, 10)], 60);
        Assert.Empty(plan.Intervals);
        Assert.Equal(10, EditorAdvancedEditMath.RippleStart(10, plan.Intervals));
    }

    [Fact]
    public void OnlyCommonEmptySectionsAreRemovedWithoutOverlappingRemainingMedia()
    {
        var plan = EditorRippleSyncMath.PlanRemoval(Tracks, [new("video", 2, 8)],
            [new("voice", 4, 6), new("video", 8, 10), new("voice", 8, 10)], 60);
        Assert.Equal(new[] { (2d, 4d), (6d, 8d) }, plan.Intervals);
        var occupiedStart = EditorAdvancedEditMath.RippleStart(4, plan.Intervals);
        var followerStart = EditorAdvancedEditMath.RippleStart(8, plan.Intervals);
        Assert.Equal(occupiedStart + 2, followerStart);
    }

    [Fact]
    public void FractionalGapEdgesRoundInwardToWholeFrames()
    {
        var plan = EditorRippleSyncMath.PlanRemoval(Tracks, [new("video", 2.015, 3.014)], [], 30);
        var interval = Assert.Single(plan.Intervals);
        Assert.True(interval.Start >= 2.015);
        Assert.True(interval.End <= 3.014);
        Assert.Equal(Math.Round(plan.RemovedSeconds * 30), plan.RemovedSeconds * 30, 8);
    }

    [Theory]
    [InlineData(-3)]
    [InlineData(3)]
    public void RippleTrimStopsAtAClipSpanningTheEditPoint(double delta) =>
        Assert.Equal(0, EditorRippleSyncMath.ClampTrimDelta(Tracks, "video", 4, delta, [new("voice", 2, 8)], 60));

    [Fact]
    public void ShorteningCannotMoveFollowersOverStationaryMediaOnAnotherTrack()
    {
        var delta = EditorRippleSyncMath.ClampTrimDelta(Tracks, "video", 4, -2,
            [new("voice", 0, 3), new("voice", 4, 6), new("video", 4, 8)], 60);
        Assert.Equal(-1, delta);
        Assert.Equal(3, EditorAdvancedEditMath.RippleTrimFollowerStart(4, 4, delta));
    }

    [Fact]
    public void OptedOutAndEditingLockedTracksDoNotConstrainRippleTrim()
    {
        Assert.Equal(2, EditorRippleSyncMath.ClampTrimDelta(Tracks, "video", 4, 2,
            [new("music", 0, 12), new("locked", 0, 12)], 60));
    }

    [Fact]
    public void AStationaryClipTouchingTheEditPointPreventsNegativeRippleOverlap()
    {
        Assert.Equal(0, EditorRippleSyncMath.ClampTrimDelta(Tracks, "video", 4, -1,
            [new("voice", 0, 4), new("voice", 4, 8)], 60));
    }

    [Fact]
    public void ClampedTrimRoundsTowardZeroWithoutExceedingAvailableGap()
    {
        var delta = EditorRippleSyncMath.ClampTrimDelta(Tracks, "video", 4, -3,
            [new("voice", 0, 3.015), new("voice", 4, 8)], 30);
        Assert.Equal(-29d / 30, delta, 8);
        Assert.True(4 + delta >= 3.015);
    }

    [Fact]
    public void ReversingTheDragUsesOriginalFollowerPositions()
    {
        var first = EditorRippleSyncMath.ClampTrimDelta(Tracks, "video", 4, 2, [], 60);
        var reversed = EditorRippleSyncMath.ClampTrimDelta(Tracks, "video", 4, -1, [], 60);
        Assert.Equal(8, EditorAdvancedEditMath.RippleTrimFollowerStart(6, 4, first));
        Assert.Equal(5, EditorAdvancedEditMath.RippleTrimFollowerStart(6, 4, reversed));
    }

    private static EditorRippleSourceSpan Video(string id = "video-clip") =>
        new(id, "video", "", true, 0, 4, 2, 6, 10, 1);

    private static EditorRippleSourceSpan Stem(string id, string track = "voice", string source = "video-clip") =>
        new(id, track, source, false, 0, 4, 2, 6, 10, 1);

    [Fact]
    public void VideoRippleTrimIncludesAlignedGameAndMicrophoneStemsInsteadOfBlockingOnThem()
    {
        var plan = EditorRippleSyncMath.PlanSourceTrim(Tracks, "video-clip", -1, false,
            [Video(), Stem("game"), Stem("microphone")], 60);
        Assert.Equal(-1, plan.Delta);
        Assert.True(plan.TrimmedIds.SetEquals(["video-clip", "game", "microphone"]));
    }

    [Fact]
    public void SourceStemRippleTrimIncludesTheAlignedVideoAndOtherStems()
    {
        var plan = EditorRippleSyncMath.PlanSourceTrim(Tracks, "microphone", -1, false,
            [Video(), Stem("game"), Stem("microphone")], 60);
        Assert.Equal(-1, plan.Delta);
        Assert.Equal(3, plan.TrimmedIds.Count);
    }

    [Fact]
    public void IndependentStemEditsArePreservedAndConstrainTheRipple()
    {
        var moved = Stem("microphone") with { TimelineStart = .5 };
        var plan = EditorRippleSyncMath.PlanSourceTrim(Tracks, "video-clip", -1, false,
            [Video(), Stem("game"), moved], 60);
        Assert.Equal(0, plan.Delta);
        Assert.False(plan.TrimmedIds.Contains("microphone"));
    }

    [Theory]
    [InlineData("music")]
    [InlineData("locked")]
    public void OptedOutAndEditingLockedStemsAreNotRetimed(string track)
    {
        var plan = EditorRippleSyncMath.PlanSourceTrim(Tracks, "video-clip", -1, false,
            [Video(), Stem("game"), Stem("microphone", track)], 60);
        Assert.Equal(-1, plan.Delta);
        Assert.False(plan.TrimmedIds.Contains("microphone"));
    }

    [Fact]
    public void EndExtensionRespectsTheShortestAlignedStemSourceHandle()
    {
        var shortStem = Stem("microphone") with { SourceDuration = 6.2 };
        var plan = EditorRippleSyncMath.PlanSourceTrim(Tracks, "video-clip", 2, false,
            [Video(), Stem("game"), shortStem], 60);
        Assert.Equal(.2, plan.Delta, 8);
    }

    [Fact]
    public void LeadingRippleExtensionUsesTheSharedSourceHeadHandle()
    {
        var plan = EditorRippleSyncMath.PlanSourceTrim(Tracks, "video-clip", 5, true,
            [Video(), Stem("game"), Stem("microphone")], 60);
        Assert.Equal(2, plan.Delta);
    }

    [Fact]
    public void UnrelatedAudioWithTheSameBoundsDoesNotBecomeASourceCounterpart()
    {
        var unrelated = Stem("unrelated", source: "another-video");
        var plan = EditorRippleSyncMath.PlanSourceTrim(Tracks, "video-clip", 1, false,
            [Video(), unrelated], 60);
        Assert.Equal(1, plan.Delta);
        Assert.False(plan.TrimmedIds.Contains("unrelated"));
    }
}
