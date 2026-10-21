using Klipp.Core.Editing;

namespace Klipp.Tests.Editing;

public sealed class EditorAdvancedEditMathTests
{
    [Theory]
    [InlineData(4, 4, .25, .25, .5)]
    [InlineData(0, 4, 0, .5, .5)]
    [InlineData(4, 0, .5, 0, .5)]
    [InlineData(.1, .2, .1, .2, .3)]
    [InlineData(0, 0, 0, 0, 0)]
    public void CrossfadeUsesBalancedSourceHandlesAndFallsBackToTheAvailableSide(double leftAvailable, double rightAvailable,
        double expectedLeft, double expectedRight, double expectedOverlap)
    {
        var plan = EditorAdvancedEditMath.PlanAudioCrossfadeHandles(.5, leftAvailable, rightAvailable);
        Assert.Equal(expectedLeft, plan.LeftExtension, 8);
        Assert.Equal(expectedRight, plan.RightExtension, 8);
        Assert.Equal(expectedOverlap, plan.Overlap, 8);
    }

    [Fact]
    public void CrossfadeExtendsTouchingClipsWithoutMovingExistingSourceContent()
    {
        var plan = EditorAdvancedEditMath.PlanAudioCrossfadeHandles(.5, 4, 4);
        var leftEnd = 4 + plan.LeftExtension;
        var rightStart = 4 - plan.RightExtension;
        var rightTrimStart = 4 - plan.RightExtension;
        Assert.Equal(4.25, leftEnd);
        Assert.Equal(3.75, rightStart);
        Assert.Equal(.5, leftEnd - rightStart);
        Assert.Equal(8, rightStart + 8 - rightTrimStart);
        Assert.Equal(6, rightTrimStart + (6 - rightStart));
    }

    [Theory]
    [InlineData(.25)]
    [InlineData(1)]
    [InlineData(4)]
    public void CrossfadeLeadingExtensionKeepsSourceMappingAtEveryPlaybackSpeed(double speed)
    {
        var plan = EditorAdvancedEditMath.PlanAudioCrossfadeHandles(.5, 1, 1);
        var newTimelineStart = 4 - plan.RightExtension;
        var newTrimStart = 2 - plan.RightExtension * speed;
        const double originalTrimEnd = 10;
        Assert.Equal(4 + (originalTrimEnd - 2) / speed,
            newTimelineStart + (originalTrimEnd - newTrimStart) / speed, 8);
        foreach (var time in new[] { 4d, 4.5, 5 })
            Assert.Equal(2 + (time - 4) * speed, newTrimStart + (time - newTimelineStart) * speed, 8);
    }

    [Fact]
    public void CrossfadeReusesExistingOverlapBeforeTakingMoreSourceHandles()
    {
        var plan = EditorAdvancedEditMath.PlanAudioCrossfadeHandles(.5, 1, 1, existingOverlap: .2);
        Assert.Equal(.15, plan.LeftExtension, 8);
        Assert.Equal(.15, plan.RightExtension, 8);
        Assert.Equal(.5, plan.Overlap, 8);
        var existingOnly = EditorAdvancedEditMath.PlanAudioCrossfadeHandles(.5, 0, 0, existingOverlap: .2);
        Assert.Equal(0, existingOnly.LeftExtension);
        Assert.Equal(0, existingOnly.RightExtension);
        Assert.Equal(.2, existingOnly.Overlap);
    }

    [Fact]
    public void FadeBakingCollapsesAConstantSectionToItsEndpoints()
    {
        var samples = Enumerable.Range(0, 1201).Select(index => (Time: index / 120d, Value: 1d)).ToList();
        Assert.Equal(new[] { 0, 1200 }, EditorAdvancedEditMath.SimplifyLinearSamples(samples));
    }

    [Fact]
    public void FadeBakingRetainsTheRampCornerAndUserKeyTimes()
    {
        var samples = Enumerable.Range(0, 601).Select(index => (Time: index / 120d, Value: Math.Min(1, index / 60d))).ToList();
        var kept = EditorAdvancedEditMath.SimplifyLinearSamples(samples, new HashSet<int> { 300 });
        Assert.Equal(new[] { 0, 60, 300, 600 }, kept);
    }

    [Fact]
    public void FadeBakingDoesNotFlattenANonlinearEnvelope()
    {
        var samples = Enumerable.Range(0, 121).Select(index => (Time: index / 120d, Value: Math.Pow(index / 120d, 2))).ToList();
        Assert.Equal(samples.Count, EditorAdvancedEditMath.SimplifyLinearSamples(samples).Count);
    }

    [Fact]
    public void GroupMoveStopsAtZeroWithoutChangingRelativeOffsets()
    {
        var delta = EditorAdvancedEditMath.ClampGroupMove(-8, [3, 6, 10]);
        Assert.Equal(-3, delta);
        Assert.Equal(3, (6 + delta) - (3 + delta));
    }

    [Theory]
    [InlineData(-9, -1)]
    [InlineData(9, 1)]
    [InlineData(0, 0)]
    public void GroupTrackMoveKeepsEveryMemberInBoundsAndPreservesTrackOffsets(int requested, int expected)
    {
        var indices = new[] { 1, 3 };
        var delta = EditorAdvancedEditMath.ClampGroupTrackMove(requested, indices, [false, false, false, false, false]);
        Assert.Equal(expected, delta);
        Assert.Equal(2, (indices[1] + delta) - (indices[0] + delta));
    }

    [Fact]
    public void GroupTrackMoveCannotEnterALockedTargetOrMoveALockedMember()
    {
        Assert.Equal(0, EditorAdvancedEditMath.ClampGroupTrackMove(1, [0, 1], [false, false, true, false]));
        Assert.Equal(0, EditorAdvancedEditMath.ClampGroupTrackMove(-1, [1], [false, true, false]));
    }

    [Fact]
    public void GroupTrackMoveUsesVisualIndicesAfterTrackReordering()
    {
        var before = new[] { "v3", "v2", "v1" };
        var after = new[] { "v2", "v1", "v3" };
        var originalIndex = Array.IndexOf(before, "v2");
        var reorderedIndex = Array.IndexOf(after, "v2");
        var delta = EditorAdvancedEditMath.ClampGroupTrackMove(1, [reorderedIndex], [false, false, false]);
        Assert.Equal("v1", after[reorderedIndex + delta]);
        Assert.NotEqual(originalIndex, reorderedIndex);
    }

    [Theory]
    [InlineData(3, -2, 3)]
    [InlineData(8, -2, 6)]
    [InlineData(12, 3, 15)]
    public void RippleTrimMovesOnlyFollowingClipsByTheTimelineDurationChange(double start, double durationChange, double expected) =>
        Assert.Equal(expected, EditorAdvancedEditMath.RippleTrimFollowerStart(start, 8, durationChange));

    [Theory]
    [InlineData(.5, 2, 3, 7)]
    [InlineData(2, 2, 0, 4)]
    [InlineData(2, -10, 8, 12)]
    public void SlipMapsPointerDeltaThroughSpeedAndPreservesSourceRange(double speed, double pointerDelta, double expectedStart, double expectedEnd)
    {
        var slipped = EditorAdvancedEditMath.SlipSourceRange(4, 8, 12, speed, pointerDelta);
        Assert.Equal(expectedStart, slipped.Start);
        Assert.Equal(expectedEnd, slipped.End);
        Assert.Equal(4 / speed, (slipped.End - slipped.Start) / speed);
    }

    [Fact]
    public void RollingTrimPreservesCombinedTimelineLengthAcrossSpeedExtremes()
    {
        foreach (var leftSpeed in new[] { .25, 1, 4 })
        foreach (var rightSpeed in new[] { .25, 1, 4 })
        foreach (var requested in new[] { -100d, -.5, 0, .5, 100 })
        {
            var delta = EditorAdvancedEditMath.ClampRollingDelta(requested, 2, 6, 10, leftSpeed, 1, 8, rightSpeed);
            var leftEnd = 6 + delta * leftSpeed;
            var rightStart = 1 + delta * rightSpeed;
            Assert.InRange(leftEnd, 2.1 - 1e-9, 10 + 1e-9);
            Assert.InRange(rightStart, -1e-9, 7.9 + 1e-9);
            Assert.Equal(4 / leftSpeed + 7 / rightSpeed,
                (leftEnd - 2) / leftSpeed + (8 - rightStart) / rightSpeed, 8);
        }
    }

    [Theory]
    [InlineData(8, 1.95)]
    [InlineData(-8, -1)]
    [InlineData(.5, .5)]
    public void RollingTrimCapsBothClipsAtTheirSourceLimits(double requested, double expected)
    {
        var delta = EditorAdvancedEditMath.ClampRollingDelta(requested,
            leftStart: 2, leftEnd: 6, leftSourceDuration: 10, leftSpeed: 2,
            rightStart: 1, rightEnd: 3.05, rightSpeed: 1);
        Assert.Equal(expected, delta, 8);
    }

    [Fact]
    public void RollingTrimKeepsCombinedDurationAtDifferentSpeeds()
    {
        var delta = EditorAdvancedEditMath.ClampRollingDelta(.75, 1, 5, 12, 2, 2, 7, .5);
        var originalDuration = (5 - 1) / 2d + (7 - 2) / .5;
        var editedDuration = (5 + delta * 2 - 1) / 2d + (7 - (2 + delta * .5)) / .5;
        Assert.Equal(originalDuration, editedDuration, 8);
    }

    [Theory]
    [InlineData(-3, 0)]
    [InlineData(9, 6)]
    [InlineData(4, 4)]
    public void SlipKeepsTheSourceRangeInsideAvailableFootage(double requested, double expected) =>
        Assert.Equal(expected, EditorAdvancedEditMath.ClampSlipSourceStart(requested, 4, 10));

    [Fact]
    public void RippleDeleteDoesNotDoubleCountOverlappingSelection()
    {
        var removed = new[] { (Start: 2d, End: 5d), (Start: 4d, End: 7d), (Start: 9d, End: 10d) };
        Assert.Equal(3, EditorAdvancedEditMath.RippleStart(8, removed));
        Assert.Equal(6, EditorAdvancedEditMath.RippleStart(12, removed));
    }

    [Fact]
    public void RippleDeleteDoesNotMoveClipsBeforeOrInsideTheDeletedRange()
    {
        var removed = new[] { (Start: 4d, End: 8d) };
        Assert.Equal(2, EditorAdvancedEditMath.RippleStart(2, removed));
        Assert.Equal(6, EditorAdvancedEditMath.RippleStart(6, removed));
        Assert.Equal(4, EditorAdvancedEditMath.RippleStart(8, removed));
    }

    [Fact]
    public void RippleDeletePreservesAnUnselectedClipOverlappingTheDeletedClip()
    {
        var gaps = EditorAdvancedEditMath.SubtractCoveredIntervals([(0d, 8d)], [(6d, 12d), (12d, 20d)]);
        Assert.Equal(new[] { (0d, 6d) }, gaps);
        var overlappingStart = EditorAdvancedEditMath.RippleStart(6, gaps);
        var followingStart = EditorAdvancedEditMath.RippleStart(12, gaps);
        Assert.Equal(0, overlappingStart);
        Assert.Equal(overlappingStart + 6, followingStart);
    }

    [Fact]
    public void RippleDeleteClosesOnlyEmptySectionsAroundRemainingClips()
    {
        var gaps = EditorAdvancedEditMath.SubtractCoveredIntervals([(2d, 12d)], [(0d, 3d), (4d, 6d), (8d, 10d)]);
        Assert.Equal(new[] { (3d, 4d), (6d, 8d), (10d, 12d) }, gaps);
        Assert.Equal(3, EditorAdvancedEditMath.RippleStart(4, gaps));
        Assert.Equal(5, EditorAdvancedEditMath.RippleStart(8, gaps));
        Assert.Equal(7, EditorAdvancedEditMath.RippleStart(12, gaps));
    }

    [Fact]
    public void RippleDeleteMergesSelectedAndOccupiedRangesBeforeSubtracting()
    {
        var gaps = EditorAdvancedEditMath.SubtractCoveredIntervals([(2d, 6d), (5d, 9d)], [(1d, 3d), (4d, 7d), (8d, 15d)]);
        Assert.Equal(new[] { (3d, 4d), (7d, 8d) }, gaps);
    }

    [Fact]
    public void RippleDeleteLeavesNoGapToCloseWhenUnselectedFootageCoversTheSelection() =>
        Assert.Empty(EditorAdvancedEditMath.SubtractCoveredIntervals([(3d, 7d)], [(0d, 10d)]));
}
