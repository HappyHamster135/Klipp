using System;
using Klipp.Core.Subtitles;
using Xunit;

namespace Klipp.Tests.Subtitles;

public sealed class SrtSubtitleServiceTests
{
    [Fact]
    public void Parse_ReadsIndexedMultilineCues()
    {
        const string srt = "1\r\n00:00:01,250 --> 00:00:03,500\r\nFirst line\r\nSecond line\r\n\r\n2\r\n00:00:04,000 --> 00:00:05,000\r\nNext\r\n";

        var cues = SrtSubtitleService.Parse(srt);

        Assert.Equal(2, cues.Count);
        Assert.Equal(TimeSpan.FromMilliseconds(1250), cues[0].Start);
        Assert.Equal("First line" + Environment.NewLine + "Second line", cues[0].Text);
        Assert.Equal("Next", cues[1].Text);
    }

    [Fact]
    public void Parse_AcceptsBomMissingIndexesAndDotMilliseconds()
    {
        const string srt = "\uFEFF00:00:00.500 --> 00:00:01.750\nHello\n";

        var cue = Assert.Single(SrtSubtitleService.Parse(srt));

        Assert.Equal(TimeSpan.FromMilliseconds(500), cue.Start);
        Assert.Equal(TimeSpan.FromMilliseconds(1750), cue.End);
    }

    [Fact]
    public void Parse_SkipsMalformedAndBackwardsCues()
    {
        const string srt = "1\nnot timing\nBad\n\n2\n00:00:04,000 --> 00:00:03,000\nBackwards\n\n3\n00:00:05,000 --> 00:00:06,000\nGood\n";

        var cue = Assert.Single(SrtSubtitleService.Parse(srt));

        Assert.Equal("Good", cue.Text);
    }

    [Fact]
    public void Serialize_RoundTripsValidCuesInTimeOrder()
    {
        var content = SrtSubtitleService.Serialize(
        [
            new SubtitleCue(TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(4), "Second"),
            new SubtitleCue(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2.5), "First")
        ]);

        var cues = SrtSubtitleService.Parse(content);

        Assert.Equal(2, cues.Count);
        Assert.Equal("First", cues[0].Text);
        Assert.Contains("00:00:02,500", content, StringComparison.Ordinal);
    }
}
