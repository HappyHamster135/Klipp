using Klipp.Core.Editing;

namespace Klipp.Tests.Editing;

public sealed class EditorTimecodeMathTests
{
    [Theory]
    [InlineData("00:01:02:15", 30, 62.5)]
    [InlineData("01:02:03:30", 60, 3723.5)]
    [InlineData("1:25", 30, 85)]
    [InlineData("1:25.5", 30, 85.5)]
    [InlineData("01:02:03.5", 60, 3723.5)]
    [InlineData("85.5", 30, 85.5)]
    [InlineData(" 85.5 ", 60, 85.5)]
    public void ParsesSupportedAbsoluteFormats(string input, int frameRate, double expected)
    {
        Assert.True(EditorTimecodeMath.TryParsePosition(input, 0, 7200, frameRate, out var actual, out var error));
        Assert.Equal(expected, actual, 8);
        Assert.Equal(string.Empty, error);
    }

    [Theory]
    [InlineData("+1:05", 75)]
    [InlineData("-00:00:00:15", 9.5)]
    [InlineData("-1.5", 8.5)]
    [InlineData("+0.1", 10.1)]
    public void RelativeEntryUsesThePositionAtDialogOpen(string input, double expected)
    {
        Assert.True(EditorTimecodeMath.TryParsePosition(input, 10, 120, 30, out var actual, out _));
        Assert.Equal(expected, actual, 8);
    }

    [Theory]
    [InlineData("")]
    [InlineData("hello")]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    [InlineData("1e3")]
    [InlineData("1::2")]
    [InlineData("1:60")]
    [InlineData("1:60:00")]
    [InlineData("00:00:60:00")]
    [InlineData("00:00:00:30")]
    [InlineData("00:00:00:1.5")]
    [InlineData("-1")]
    [InlineData("121")]
    [InlineData("1:2:3:4:5")]
    [InlineData("999999999999999999999999")]
    public void InvalidOrOutOfRangeInputDoesNotProduceASeek(string input)
    {
        Assert.False(EditorTimecodeMath.TryParsePosition(input, 0, 120, 30, out var actual, out var error));
        Assert.Equal(0, actual);
        Assert.NotEmpty(error);
    }

    [Fact]
    public void EntrySnapsToNearestProjectFrameWithoutPassingDuration()
    {
        Assert.True(EditorTimecodeMath.TryParsePosition("1.02", 0, 10, 30, out var actual, out _));
        Assert.Equal(31d / 30, actual, 8);
        Assert.True(EditorTimecodeMath.TryParsePosition("10.02", 0, 10.02, 30, out actual, out _));
        Assert.Equal(10, actual);
    }

    [Theory]
    [InlineData(62.5, 30, "00:01:02:15")]
    [InlineData(3723.5, 60, "01:02:03:30")]
    [InlineData(3599.999, 30, "01:00:00:00")]
    [InlineData(360000, 60, "100:00:00:00")]
    public void FormattingCarriesFramesAndPreservesLongHours(double seconds, int frameRate, string expected)
    {
        Assert.Equal(expected, EditorTimecodeMath.Format(seconds, frameRate));
        Assert.True(EditorTimecodeMath.TryParsePosition(expected, 0, 400000, frameRate, out var parsed, out _));
        Assert.Equal(EditorTimelineMath.SnapToFrame(seconds, frameRate), parsed, 8);
    }
}
