using Klipp.Core.Editing;

namespace Klipp.Tests.Editing;

public sealed class EditorWaveformMathTests
{
    [Theory]
    [InlineData(0, 10, 0)]
    [InlineData(2, 10, -200)]
    [InlineData(0, 6, 0)]
    [InlineData(2, 6, -200)]
    public void TrimmingCropsTheOriginalSourceScaleInsteadOfStretchingIt(double trimStart, double trimEnd, double left)
    {
        Assert.True(EditorWaveformMath.TryPlace(0, trimStart, trimEnd, 1, 0, 10, 0, 10, 1000, out var image));
        Assert.Equal(left, image.Left, 8);
        Assert.Equal(1000, image.Width, 8);
    }

    [Fact]
    public void APartlyVisibleClipKeepsItsSignalAtTheCorrectTimelineTime()
    {
        Assert.True(EditorWaveformMath.TryPlace(0, 0, 10, 1, 0, 10, 4, 5, 1000, out var image));
        Assert.Equal(-800, image.Left, 8);
        Assert.Equal(2000, image.Width, 8);
        // The cached five-second peak remains one second into this viewport.
        Assert.Equal(200, image.Left + image.Width * .5, 8);
    }

    [Theory]
    [InlineData(2, -100, 500)]
    [InlineData(.5, -400, 2000)]
    public void PlaybackSpeedMapsCachedSourceSecondsToProjectSeconds(double speed, double left, double width)
    {
        Assert.True(EditorWaveformMath.TryPlace(5, 2, 10, speed, 0, 10, 0, 10, 1000, out var image));
        Assert.Equal(left, image.Left, 8);
        Assert.Equal(width, image.Width, 8);
    }

    [Fact]
    public void ExtendingBeyondTheCachedIntervalLeavesAnHonestBlankRegion()
    {
        Assert.True(EditorWaveformMath.TryPlace(0, 2, 10, 1, 4, 8, 0, 10, 1000, out var image));
        Assert.Equal(200, image.Left, 8);
        Assert.Equal(400, image.Width, 8);
    }

    [Fact]
    public void MovingAClipDoesNotChangeItsInternalWaveformScale()
    {
        Assert.True(EditorWaveformMath.TryPlace(12, 4, 8, 1, 4, 8, 10, 10, 1000, out var image));
        Assert.Equal(0, image.Left, 8);
        Assert.Equal(400, image.Width, 8);
    }

    [Theory]
    [InlineData(10, 12, 0, 10)] // Source cache lies outside the clip.
    [InlineData(0, 2, 4, 2)] // The source cache lies outside the viewport.
    [InlineData(0, 10, 12, 2)] // The entire clip is outside the viewport.
    public void UncoveredIntervalsDoNotShowUnrelatedSignal(double cacheStart, double cacheEnd, double viewportStart, double viewportDuration)
    {
        Assert.False(EditorWaveformMath.TryPlace(0, 0, 10, 1, cacheStart, cacheEnd,
            viewportStart, viewportDuration, 1000, out _));
    }

    [Theory]
    [InlineData(0, 0, 10)]
    [InlineData(-1, 0, 10)]
    [InlineData(1, 10, 10)]
    [InlineData(double.NaN, 0, 10)]
    public void InvalidScaleOrCacheCannotReachTheNativeLayout(double speed, double cacheStart, double cacheEnd)
    {
        Assert.False(EditorWaveformMath.TryPlace(0, 0, 10, speed, cacheStart, cacheEnd, 0, 10, 1000, out _));
    }
}
