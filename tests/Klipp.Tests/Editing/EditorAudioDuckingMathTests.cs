using Klipp.Core.Editing;

namespace Klipp.Tests.Editing;

public sealed class EditorAudioDuckingMathTests
{
    [Theory]
    [InlineData("microphone", true, true, true)]
    [InlineData("system", true, true, false)]
    [InlineData("", true, true, false)]
    [InlineData("", false, true, true)]
    [InlineData("system", false, true, false)]
    [InlineData("discord", false, true, false)]
    [InlineData("music", false, true, false)]
    [InlineData("discord", true, true, false)]
    [InlineData("music", true, true, false)]
    [InlineData("microphone", false, true, true)]
    [InlineData("microphone", true, false, false)]
    public void KnownNonVoiceStemsNeverControlDuckingAndUnknownLegacyAudioRetainsItsFallback(string role, bool microphone, bool source, bool expected)
        => Assert.Equal(expected, EditorAudioDuckingMath.IsControlSource(role, microphone, source));

    [Theory]
    [InlineData(0, .65)]
    [InlineData(.01, .65)]
    [InlineData(.02, .65)]
    [InlineData(.5, 0)]
    [InlineData(double.NaN, 1)]
    public void SilenceQuietInputAndDisabledDuckingLeaveGainUnchanged(double level, double strength)
        => Assert.Equal(1, EditorAudioDuckingMath.Gain(level, strength));

    [Fact]
    public void CompressionRespondsToSignalAndStrengthInsteadOfApplyingAConstantCut()
    {
        Assert.True(EditorAudioDuckingMath.Gain(.2, .8) < EditorAudioDuckingMath.Gain(.05, .8));
        Assert.True(EditorAudioDuckingMath.Gain(.2, .8) < EditorAudioDuckingMath.Gain(.2, .2));
    }

    [Fact]
    public void EnvelopeAttacksQuicklyAndReleasesSmoothlyAfterSpeech()
    {
        var envelope = new EditorAudioDuckingEnvelope();
        var attack = envelope.Update(.2, .02);
        Assert.InRange(attack, .12, .14);
        var release = envelope.Update(0, .02);
        Assert.InRange(release, .11, .14);
        Assert.True(release < attack);
        Assert.True(envelope.Update(0, 2) < .001);
    }

    [Fact]
    public void RepeatedMeterReadDoesNotAdvanceEnvelopeAndASeekResetsIt()
    {
        var envelope = new EditorAudioDuckingEnvelope();
        var before = envelope.Update(.2, .1);
        Assert.Equal(before, envelope.Update(0, 0));
        Assert.Equal(0, envelope.Update(0, 0, reset: true));
        Assert.Equal(1, EditorAudioDuckingMath.Gain(envelope.Level, .65));
    }
}
