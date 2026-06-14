using System.Buffers.Binary;

namespace Klipp.Encoding.Muxing;

/// <summary>
/// Metadata från en .raw-fils header. Räcker för att veta längd, upplösning
/// och format utan att läsa själva frame-datan.
/// </summary>
public sealed record RawFileMetadata(
    int Width,
    int Height,
    int FrameRate,
    int FrameCount,
    uint Format)
{
    /// <summary>Total varaktighet i sekunder.</summary>
    public double DurationSeconds => FrameRate > 0 ? (double)FrameCount / FrameRate : 0;

    /// <summary>Formaterad som "M:SS" (t.ex. "0:30").</summary>
    public string DurationText
    {
        get
        {
            var totalSeconds = (int)DurationSeconds;
            var minutes = totalSeconds / 60;
            var seconds = totalSeconds % 60;
            return $"{minutes}:{seconds:D2}";
        }
    }
}

/// <summary>
/// Läser metadata och frames från .raw-filer skrivna av <see cref="RawFileWriter"/>.
/// </summary>
public static class RawFileReader
{
    private const uint Magic = 0x57415243; // "KRAW"
    private const int HeaderSize = 32;

    /// <summary>
    /// Läser metadata från en .raw-fils header. Snabb operation — läser bara 32 bytes.
    /// </summary>
    /// <exception cref="InvalidDataException">Om filen inte är en giltig .raw-fil.</exception>
    public static RawFileMetadata ReadMetadata(string filePath)
    {
        using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);

        Span<byte> header = stackalloc byte[HeaderSize];
        var bytesRead = stream.Read(header);

        if (bytesRead < HeaderSize)
            throw new InvalidDataException($"Filen är för kort för att vara en .raw-fil: {filePath}");

        var magic = BinaryPrimitives.ReadUInt32LittleEndian(header.Slice(0, 4));
        if (magic != Magic)
            throw new InvalidDataException($"Filen har inte rätt KRAW-magic: {filePath}");

        return new RawFileMetadata(
            Width: BinaryPrimitives.ReadInt32LittleEndian(header.Slice(8, 4)),
            Height: BinaryPrimitives.ReadInt32LittleEndian(header.Slice(12, 4)),
            FrameRate: BinaryPrimitives.ReadInt32LittleEndian(header.Slice(16, 4)),
            FrameCount: BinaryPrimitives.ReadInt32LittleEndian(header.Slice(20, 4)),
            Format: BinaryPrimitives.ReadUInt32LittleEndian(header.Slice(24, 4))
        );
    }
}
