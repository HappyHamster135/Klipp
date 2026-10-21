using Klipp.Core.Playback;

namespace Klipp.Tests.Playback;

public sealed class PlaybackReviewMathTests
{
    [Fact]
    public void RepeatedReviewKeys_IncreaseSpeedWithoutExceedingEightTimes()
    {
        var rate = 0d;
        foreach (var expected in new[] { 1, 2, 4, 8, 8 })
        {
            rate = PlaybackReviewMath.NextRate(rate, 1);
            Assert.Equal(expected, rate);
        }
        Assert.Equal(-1, PlaybackReviewMath.NextRate(rate, -1));
        Assert.Equal(-2, PlaybackReviewMath.NextRate(-1, -1));
        Assert.Equal(1, PlaybackReviewMath.NextRate(-8, 1));
        Assert.Equal(0, PlaybackReviewMath.NextRate(-8, 0));
    }

    [Theory]
    [InlineData(10, 2, -2, 3, 40, 6)]
    [InlineData(10, 20, -8, 3, 40, 3)]
    [InlineData(10, 20, 8, 3, 40, 40)]
    [InlineData(10, -20, 8, 3, 40, 10)]
    public void ShuttleClock_UsesElapsedTime_AndStopsAtPlaybackRange(double origin, double elapsed, double rate,
        double start, double end, double expected) =>
        Assert.Equal(expected, PlaybackReviewMath.Advance(origin, elapsed, rate, start, end));
}
