using Klipp.Core.Editing;

namespace Klipp.Tests.Editing;

public sealed class EditorExportPresetMathTests
{
    [Theory]
    [InlineData("720p", 1920, 1080, 1280, 720)]
    [InlineData("720p", 720, 1280, 720, 1280)]
    [InlineData("1080p", 720, 1280, 1080, 1920)]
    [InlineData("1080p", 1080, 1080, 1080, 1080)]
    [InlineData("canvas", 1080, 1350, 1080, 1350)]
    public void PresetResolutionPreservesTheCanvasFraming(string mode, int cw, int ch, int width, int height)
    {
        EditorExportPresetMath.ResolveDimensions(mode, cw, ch).ShouldBe((width, height));
    }

    [Fact]
    public void AllPresetsMatchTheirResolvedSettingsAndManualChangesBecomeCustom()
    {
        foreach (var preset in EditorExportPresetMath.Presets)
        {
            var settings = EditorExportPresetMath.Resolve(preset, 1080, 1920);
            EditorExportPresetMath.Match(settings, 1080, 1920).ShouldBe(preset.Id);
            EditorExportPresetMath.Match(settings with { VideoBitrateMbps = 7 }, 1080, 1920).ShouldBe("custom");
        }
    }

    [Fact]
    public void EstimateIncludesOneMixedAudioStreamAndMp4Overhead()
    {
        EditorExportPresetMath.EstimateBytes(60, 8, 128).ShouldBe(62_179_200);
        EditorExportPresetMath.EstimateBytes(60, 8, 128, includeAudio: false).ShouldBe(61_200_000);
    }

    [Fact]
    public void AShorterEditAndSmallerPresetReduceEstimatedSize()
    {
        var large = EditorExportPresetMath.EstimateBytes(60, 16, 192);
        var small = EditorExportPresetMath.EstimateBytes(30, 4, 128);
        small.ShouldBeLessThan(large / 4);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void InvalidDurationsDoNotProduceNonsensicalFileSizes(double duration)
    {
        EditorExportPresetMath.EstimateBytes(duration, 16, 192).ShouldBe(0);
    }

    [Fact]
    public void HugeDurationsSaturateWithoutOverflow()
    {
        EditorExportPresetMath.EstimateBytes(double.MaxValue, 120, 320).ShouldBe(long.MaxValue);
    }
}
