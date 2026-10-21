using Klipp.Core.Editing;

namespace Klipp.Tests.Editing;

public sealed class EditorAudioPreviewPlannerTests
{
    [Fact]
    public void LargeCutSequence_PreparesOnlyTheCurrentAndUpcomingClips()
    {
        var clips = Enumerable.Range(0, 2000)
            .Select(index => new EditorAudioPreviewSegment($"clip-{index}", index * 2, 2));

        var plan = EditorAudioPreviewPlanner.Plan(clips, 101);

        Assert.Equal(new[] { "clip-50", "clip-51" }, plan.RequiredIds.Order().ToArray());
        Assert.Equal(plan.RequiredIds.Order(), plan.RetainedIds.Order());
    }

    [Fact]
    public void RecentlyFinishedDecoder_IsRetainedButIsNotOpenedFromScratch()
    {
        var plan = EditorAudioPreviewPlanner.Plan([
            new("old", 0, 1), new("just-finished", 1, 1), new("playing", 2, 1), new("next", 3, 1)], 2.1);

        Assert.DoesNotContain("old", plan.RetainedIds);
        Assert.Contains("just-finished", plan.RetainedIds);
        Assert.DoesNotContain("just-finished", plan.RequiredIds);
        Assert.Contains("playing", plan.RequiredIds);
        Assert.Contains("next", plan.RequiredIds);
    }

    [Fact]
    public void ExactCut_StopsRequiringOutgoingAndRequiresIncoming()
    {
        var plan = EditorAudioPreviewPlanner.Plan([new("left", 0, 2), new("right", 2, 2)], 2);

        Assert.DoesNotContain("left", plan.RequiredIds);
        Assert.Contains("left", plan.RetainedIds);
        Assert.Contains("right", plan.RequiredIds);
    }

    [Fact]
    public void BackwardSeek_ReplacesDistantWindowImmediately()
    {
        EditorAudioPreviewSegment[] clips = [new("early", 0, 5), new("late", 100, 5)];

        var late = EditorAudioPreviewPlanner.Plan(clips, 102);
        var early = EditorAudioPreviewPlanner.Plan(clips, 1);

        Assert.Equal(new[] { "late" }, late.RequiredIds);
        Assert.Equal(new[] { "early" }, early.RequiredIds);
        Assert.DoesNotContain("late", early.RetainedIds);
    }

    [Fact]
    public void OverlappingAudio_AllActiveVoicesRemainRequired()
    {
        var plan = EditorAudioPreviewPlanner.Plan([
            new("dialogue", 0, 20), new("music", 2, 30), new("effect", 8, 2),
            new("muted-clip", 0, 20, false), new("muted-or-unsoloed-row", 0, 20, false)], 8.5);

        Assert.Equal(new[] { "dialogue", "effect", "music" }, plan.RequiredIds.Order().ToArray());
    }

    [Fact]
    public void MutingOrSoloingRetainsNearbyVoicesWithoutAllocatingMutedDecoders()
    {
        var plan = EditorAudioPreviewPlanner.Plan([
            new("game", 0, 20), new("microphone", 0, 20, false), new("far-muted", 100, 20, false)], 8.5);

        Assert.Equal(new[] { "game" }, plan.RequiredIds);
        Assert.Contains("microphone", plan.RetainedIds);
        Assert.DoesNotContain("far-muted", plan.RetainedIds);
        var unmuted = EditorAudioPreviewPlanner.Plan([new("microphone", 0, 20)], 8.5);
        Assert.Contains("microphone", unmuted.RequiredIds);
    }

    [Fact]
    public void MutedVoicesAreReleasedAfterLeavingTheirPlaybackWindow()
    {
        var plan = EditorAudioPreviewPlanner.Plan([
            new("past", 0, 2, false), new("future", 5, 2, false)], 3);

        Assert.Empty(plan.RequiredIds);
        Assert.Empty(plan.RetainedIds);
    }

    [Fact]
    public void GapPreparesNextClipWithoutReopeningHistory()
    {
        var plan = EditorAudioPreviewPlanner.Plan([new("before-gap", 0, 3), new("after-gap", 5, 2)], 3.5);

        Assert.Equal(new[] { "after-gap" }, plan.RequiredIds);
        Assert.DoesNotContain("before-gap", plan.RetainedIds);
    }

    [Theory]
    [InlineData(4.5, true)]
    [InlineData(4.5001, false)]
    public void PreparationWindow_HasAnExactInclusiveStartBoundary(double clipStart, bool included)
    {
        var plan = EditorAudioPreviewPlanner.Plan([new("clip", clipStart, 1)], 3);
        Assert.Equal(included, plan.RequiredIds.Contains("clip"));
    }

    [Fact]
    public void InvalidClipsAndWindow_DoNotRequestDecoders()
    {
        var plan = EditorAudioPreviewPlanner.Plan([
            new("nan", double.NaN, 1), new("zero", 1, 0), new("negative", -1, 5),
            new("overflow", double.MaxValue, double.MaxValue), new("", 0, 1)], 0);
        Assert.Empty(plan.RequiredIds);
        Assert.Empty(plan.RetainedIds);
        Assert.Empty(EditorAudioPreviewPlanner.Plan([new("clip", 0, 1)], double.NaN).RequiredIds);
    }
}
