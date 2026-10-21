using System.Runtime.InteropServices;

namespace Klipp.Core.Capture;

/// <summary>Keeps an encoder's canvas stable when a captured window changes size.</summary>
public static class BgraCaptureFrameFit
{
    public static byte[] Fit(ReadOnlySpan<byte> source, int sourceWidth, int sourceHeight, int targetWidth, int targetHeight)
    {
        var sourceBytes = ByteCount(sourceWidth, sourceHeight);
        var targetBytes = ByteCount(targetWidth, targetHeight);
        if (source.Length < sourceBytes) throw new ArgumentException("The source frame is incomplete.", nameof(source));
        var output = new byte[targetBytes];
        var pixels = MemoryMarshal.Cast<byte, uint>(output.AsSpan());
        pixels.Fill(0xFF000000);
        var input = MemoryMarshal.Cast<byte, uint>(source[..sourceBytes]);
        var scale = Math.Min(targetWidth / (double)sourceWidth, targetHeight / (double)sourceHeight);
        var width = Math.Clamp((int)Math.Round(sourceWidth * scale), 1, targetWidth);
        var height = Math.Clamp((int)Math.Round(sourceHeight * scale), 1, targetHeight);
        var left = (targetWidth - width) / 2;
        var top = (targetHeight - height) / 2;
        var columns = new int[width];
        for (var x = 0; x < width; x++) columns[x] = Math.Min(sourceWidth - 1, (int)((x + .5) * sourceWidth / width));
        for (var y = 0; y < height; y++)
        {
            var from = Math.Min(sourceHeight - 1, (int)((y + .5) * sourceHeight / height)) * sourceWidth;
            var to = (top + y) * targetWidth + left;
            if (width == sourceWidth) input.Slice(from, width).CopyTo(pixels.Slice(to, width));
            else for (var x = 0; x < width; x++) pixels[to + x] = input[from + columns[x]];
        }
        return output;
    }

    private static int ByteCount(int width, int height)
    {
        if (width <= 0 || height <= 0 || width > 16384 || height > 16384 || (long)width * height * 4 > 512L * 1024 * 1024)
            throw new ArgumentOutOfRangeException(nameof(width), "Invalid capture frame dimensions.");
        return checked(width * height * 4);
    }
}
