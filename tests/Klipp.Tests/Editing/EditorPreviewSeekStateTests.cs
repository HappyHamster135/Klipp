using Klipp.Core.Editing;

namespace Klipp.Tests.Editing;

public sealed class EditorPreviewSeekStateTests
{
    [Fact]
    public void OldDecoderClockCannotReleaseASeekToANewerTarget()
    {
        var seek = new EditorPreviewSeekState();
        Assert.True(seek.Request(20, 2, 0, true));
        Assert.True(seek.IsPending(2, 0.1));
        Assert.True(seek.Request(30, 2, 0.1, true));
        Assert.True(seek.IsPending(20, 0.2, seekCompleted: true));
        Assert.False(seek.IsPending(30, 0.3, seekCompleted: true));
    }

    [Fact]
    public void RepeatedDriftCorrectionDoesNotFloodAnInFlightSeek()
    {
        var seek = new EditorPreviewSeekState();
        Assert.True(seek.Request(20, 2, 0, false));
        Assert.False(seek.Request(20.03, 2, 0.03, false));
        Assert.False(seek.Request(20, 2, 0.3, true));
        Assert.True(seek.Request(20.4, 2, 0.4, false));
    }

    [Fact]
    public void SamePositionNeedsNoSeekAndSourceResetDiscardsOldPendingTime()
    {
        var seek = new EditorPreviewSeekState();
        Assert.False(seek.Request(2.005, 2, 0, true));
        Assert.True(seek.Request(20, 2, 0, true));
        seek.Reset();
        Assert.False(seek.IsPending(0, 0.1));
    }

    [Fact]
    public void DecoderPositionRequiresConfirmationBeforeItDrivesTheClock()
    {
        var seek = new EditorPreviewSeekState();
        Assert.True(seek.Request(20, 2, 0, true));
        Assert.True(seek.IsPending(20, 0.01));
        Assert.False(seek.IsPending(20, 0.06));
    }

    [Fact]
    public void PlaybackClockNeverMovesBackToTheOldPositionAfterASeek()
    {
        Assert.Equal(20, EditorPreviewClockMath.Advance(20, 0.03,
            new EditorPreviewSourceTime(2, 0, 0, 1)));
    }

    [Fact]
    public void FailedDecoderSeekReportsTimeoutInsteadOfBlockingForever()
    {
        var seek = new EditorPreviewSeekState();
        Assert.True(seek.Request(20, 2, 0, true));
        Assert.True(seek.IsPending(2, 1.9));
        Assert.False(seek.IsPending(2, 2.1));
        Assert.True(seek.TakeTimeout());
        Assert.False(seek.TakeTimeout());
        Assert.True(seek.Request(10, 2, 2.2, true));
    }

    [Fact]
    public void ReversingToTheOldPositionStillCancelsThePendingForwardSeek()
    {
        var seek = new EditorPreviewSeekState();
        Assert.True(seek.Request(20, 2, 0, true));
        Assert.True(seek.Request(2, 2, 0.05, true));
        Assert.True(seek.IsPending(20, 0.1, seekCompleted: true));
        Assert.False(seek.IsPending(2, 0.15, seekCompleted: true));
    }
}
