using System.Runtime.InteropServices;
using Klipp.Core.Capture;

namespace Klipp.Tests.Capture;

public sealed class BgraCaptureFrameFitTests
{
    [Fact]
    public void AnUnchangedFramePreservesEveryPixelWithoutSharingTheBuffer()
    {
        byte[] source = [1, 2, 3, 255, 4, 5, 6, 255, 7, 8, 9, 255, 10, 11, 12, 255];
        var result = BgraCaptureFrameFit.Fit(source, 2, 2, 2, 2);
        Assert.Equal(source, result);
        Assert.NotSame(source, result);
    }

    [Fact]
    public void ResizingKeepsTheWholePictureAndOpaqueLetterboxInsteadOfOldPixels()
    {
        uint[] colors = [1, 2, 3, 4, 5, 6, 7, 8];
        var result = BgraCaptureFrameFit.Fit(MemoryMarshal.AsBytes(colors.AsSpan()), 2, 4, 4, 4);
        var actual = MemoryMarshal.Cast<byte, uint>(result.AsSpan()).ToArray();
        Assert.Equal(new uint[] { 0xFF000000, 1, 2, 0xFF000000, 0xFF000000, 3, 4, 0xFF000000,
            0xFF000000, 5, 6, 0xFF000000, 0xFF000000, 7, 8, 0xFF000000 }, actual);
    }

    [Fact]
    public void AResizedSourceStillProducesTheOriginalEncoderByteCount()
    {
        var source = new byte[8 * 6 * 4];
        Array.Fill(source, (byte)85);
        var result = BgraCaptureFrameFit.Fit(source, 8, 6, 16, 9);
        Assert.Equal(16 * 9 * 4, result.Length);
        Assert.All(source, value => Assert.Equal(85, value));
    }

    [Fact]
    public void DownscaleSamplesTheFullSourceInsteadOfCroppingItsFarEdge()
    {
        var source = Enumerable.Range(0, 16).Select(value => (uint)value).ToArray();
        var result = BgraCaptureFrameFit.Fit(MemoryMarshal.AsBytes(source.AsSpan()), 4, 4, 2, 2);
        Assert.Equal(new uint[] { 5, 7, 13, 15 }, MemoryMarshal.Cast<byte, uint>(result.AsSpan()).ToArray());
    }

    [Fact]
    public void InvalidOrIncompleteFramesAreRejected()
    {
        Assert.Throws<ArgumentException>(() => BgraCaptureFrameFit.Fit(new byte[3], 2, 2, 2, 2));
        Assert.Throws<ArgumentOutOfRangeException>(() => BgraCaptureFrameFit.Fit([], 0, 2, 2, 2));
        Assert.Throws<ArgumentOutOfRangeException>(() => BgraCaptureFrameFit.Fit([], 20000, 20000, 2, 2));
    }
}
