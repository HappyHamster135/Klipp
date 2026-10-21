using Klipp.Core.Editing;

namespace Klipp.Tests.Editing;

public sealed class EditorLinkedTrimMathTests
{
    private static readonly EditorRippleSourceSpan[] Family =
    [
        new("v:game", "v1", "", true, 5, 6, 2, 8, 12, 1),
        new("a:game", "a1", "v:game", false, 5, 6, 2, 8, 12, 1),
        new("a:voice", "a2", "v:game", false, 5, 6, 2, 8, 10, 1)
    ];

    [Theory]
    [InlineData("v:game")]
    [InlineData("a:game")]
    [InlineData("a:voice")]
    public void EitherEndOfAnAlignedSourceFamilyUsesOneNormalTrimDelta(string anchor)
    {
        foreach (var leading in new[] { true, false })
        {
            var plan = EditorLinkedTrimMath.Plan(anchor, -1, leading, Family);
            Assert.True(plan.ClipIds.SetEquals(Family.Select(span => span.Id)));
            Assert.Equal(-1, plan.EdgeDelta);
            foreach (var span in Family)
            {
                var timelineStart = span.TimelineStart + (leading ? plan.EdgeDelta : 0);
                var sourceStart = span.TrimStart + (leading ? plan.EdgeDelta * span.Speed : 0);
                var sourceEnd = span.TrimEnd + (leading ? 0 : plan.EdgeDelta * span.Speed);
                Assert.Equal(leading ? 11 : 10, timelineStart + (sourceEnd - sourceStart) / span.Speed, 8);
                Assert.Equal(3, sourceStart + (6 - timelineStart) * span.Speed, 8);
            }
        }
    }

    [Fact]
    public void ExtensionStopsAtTheShortestMicrophoneSourceWithoutSeparatingVideo()
    {
        var plan = EditorLinkedTrimMath.Plan("v:game", 10, false, Family);
        Assert.Equal(2, plan.EdgeDelta);
        foreach (var span in Family) Assert.True(span.TrimEnd + plan.EdgeDelta * span.Speed <= span.SourceDuration);
    }

    [Fact]
    public void LeadingExtensionStopsAtZeroAndSourceStartTogether()
    {
        var nearZero = Family.Select(span => span with { TimelineStart = .5 }).ToArray();
        Assert.Equal(-.5, EditorLinkedTrimMath.Plan("v:game", -5, true, nearZero).EdgeDelta);
        Assert.Equal(-2, EditorLinkedTrimMath.Plan("a:voice", -5, true, Family).EdgeDelta);
    }

    [Fact]
    public void IndependentlyMovedTrimmedOrRetimedSourceAudioStaysIndependent()
    {
        foreach (var independent in new[]
        {
            Family[2] with { TimelineStart = 6 }, Family[2] with { TrimStart = 3, Duration = 5 },
            Family[2] with { Speed = 2, Duration = 3 }
        })
        {
            var plan = EditorLinkedTrimMath.Plan("v:game", -1, false, [Family[0], Family[1], independent]);
            Assert.False(plan.ClipIds.Contains(independent.Id));
            Assert.Equal(2, plan.ClipIds.Count);
        }
    }

    [Fact]
    public void UnselectedOrLockedPartnersCanBeOmittedWithoutBlockingAnExplicitTrim()
    {
        var plan = EditorLinkedTrimMath.Plan("v:game", 4, false, Family.Take(2));
        Assert.False(plan.ClipIds.Contains("a:voice"));
        Assert.Equal(4, plan.EdgeDelta);
    }

    [Fact]
    public void UnrelatedSelectedAudioIsNotLinkedJustBecauseItsRangeMatches()
    {
        var unlinked = Family[2] with { SourceClipId = "" };
        var another = unlinked with { Id = "a:music" };
        var plan = EditorLinkedTrimMath.Plan(unlinked.Id, -1, false, [unlinked, another]);
        Assert.Equal(new[] { unlinked.Id }, plan.ClipIds);
    }

    [Theory]
    [InlineData(.25)]
    [InlineData(4)]
    public void SourceMappingAndMinimumLengthsHoldAtDifferentPlaybackSpeeds(double speed)
    {
        var clips = Family.Select(span => span with { Speed = speed, Duration = 6 / speed }).ToArray();
        var plan = EditorLinkedTrimMath.Plan("a:voice", -100, false, clips);
        foreach (var clip in clips)
            Assert.Equal(.1, clip.TrimEnd + plan.EdgeDelta * speed - clip.TrimStart, 8);
    }

    [Fact]
    public void ReturningToTheOriginalEdgeUsesTheOriginalSourceRange()
    {
        Assert.Equal(-2, EditorLinkedTrimMath.Plan("v:game", -2, false, Family).EdgeDelta);
        Assert.Equal(0, EditorLinkedTrimMath.Plan("v:game", 0, false, Family).EdgeDelta);
    }
}
