using Klipp.Core.Editing;

namespace Klipp.Tests.Editing;

public sealed class EditorAudioPreviewTimingStateTests
{
    [Fact]
    public void RepeatedMixUpdatesKeepTheDecoderAtItsCurrentClock()
    {
        var state = new EditorAudioPreviewTimingState();
        Assert.True(state.Update(10, 2, 1));
        // Volume, mute, normalization, fade and ducking edits do not change this map.
        for (var update = 0; update < 120; update++)
            Assert.False(state.Update(10, 2, 1));
    }

    [Theory]
    [InlineData(10.01, 2, 1)]
    [InlineData(10, 2.01, 1)]
    [InlineData(10, 2, 1.5)]
    public void MovingTrimmingOrRetimingRequiresOneSeek(double start, double trim, double speed)
    {
        var state = new EditorAudioPreviewTimingState();
        state.Update(10, 2, 1);
        Assert.True(state.Update(start, trim, speed));
        Assert.False(state.Update(start, trim, speed));
    }

    [Fact]
    public void EditingOneVoiceDoesNotReseekTheOtherStem()
    {
        var game = new EditorAudioPreviewTimingState();
        var microphone = new EditorAudioPreviewTimingState();
        game.Update(0, 0, 1);
        microphone.Update(0, 0, 1);

        Assert.True(microphone.Update(.5, 0, 1));
        Assert.False(game.Update(0, 0, 1));
    }
}
