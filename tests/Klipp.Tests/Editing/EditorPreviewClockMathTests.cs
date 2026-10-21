using Klipp.Core.Editing;

namespace Klipp.Tests.Editing;

public sealed class EditorPreviewClockMathTests
{
    [Theory]
    [InlineData(7, 20, 5, 2, 21)]
    [InlineData(7, 20, 5, 0.5, 24)]
    [InlineData(5.25, 20, 5, 0.25, 21)]
    public void NativeClockUsesAbsolutePlacementAndSourceTrim(double source, double start,
        double trim, double speed, double expected)
    {
        var nativeTime = new EditorPreviewSourceTime(source, start, trim, speed);

        Assert.Equal(expected, EditorPreviewClockMath.Advance(0, 30, nativeTime), 8);
    }

    [Fact]
    public void BufferedDecoderHoldsProjectTimeAndIgnoresStaleSourceClock()
    {
        var staleSource = new EditorPreviewSourceTime(12, 20, 2, 1);

        Assert.Equal(23.5, EditorPreviewClockMath.Advance(23.5, 10, staleSource, decoderBlocked: true));
    }

    [Fact]
    public void OpeningDecoderHoldsTimeInAnInitialBlackGap()
    {
        Assert.Equal(5.2, EditorPreviewClockMath.Advance(5.2, 0.5, decoderBlocked: true));
    }

    [Fact]
    public void ClipEndContinuesIntoGapWithoutSkippingToLaterMedia()
    {
        var position = EditorPreviewClockMath.ContinueAfterClipEnd(8.99, 5, 4);

        Assert.Equal(9, position);
        Assert.Equal(9.02, EditorPreviewClockMath.Advance(position, 0.02), 8);
    }

    [Fact]
    public void ClipEndDoesNotMoveAnAdvancedClockBackwards()
    {
        Assert.Equal(9.03, EditorPreviewClockMath.ContinueAfterClipEnd(9.03, 5, 4));
    }

    [Fact]
    public void EmptySpaceAdvancesAtProjectSpeedWithoutAVideoClock()
    {
        Assert.Equal(12.5, EditorPreviewClockMath.Advance(12, 0.5));
    }

    [Fact]
    public void ASourceBeforeItsTrimStartCannotMoveBeforeTheClipsAbsoluteStart()
    {
        var source = new EditorPreviewSourceTime(1, 20, 5, 2);

        Assert.Equal(20, EditorPreviewClockMath.Advance(20, 0.1, source));
    }

    [Fact]
    public void NegativeElapsedTimeCannotMoveAnEmptySpaceClockBackwards()
    {
        Assert.Equal(12, EditorPreviewClockMath.Advance(12, -0.1));
    }
}
