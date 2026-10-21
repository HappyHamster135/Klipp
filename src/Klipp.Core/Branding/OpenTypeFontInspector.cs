using System.Buffers.Binary;
using System.Text;

namespace Klipp.Core.Branding;

public sealed record OpenTypeFontInfo(string FamilyName, string FullName);

public static class OpenTypeFontInspector
{
    private const uint TrueTypeScaler = 0x00010000;
    private const uint AppleTrueTypeScaler = 0x74727565;
    private const uint OpenTypeCffScaler = 0x4F54544F;
    private const uint NameTableTag = 0x6E616D65;

    public static async Task<OpenTypeFontInfo> InspectAsync(
        string filePath,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
            throw new FileNotFoundException("The selected font could not be found.", filePath);
        var bytes = await File.ReadAllBytesAsync(filePath, cancellationToken).ConfigureAwait(false);
        return Inspect(bytes);
    }

    public static OpenTypeFontInfo Inspect(ReadOnlySpan<byte> data)
    {
        if (data.Length < 12)
            throw new InvalidDataException("The font header is incomplete.");
        var scaler = BinaryPrimitives.ReadUInt32BigEndian(data);
        if (scaler is not TrueTypeScaler and not AppleTrueTypeScaler and not OpenTypeCffScaler)
            throw new InvalidDataException("Only individual TrueType and OpenType fonts are supported.");

        var tableCount = BinaryPrimitives.ReadUInt16BigEndian(data[4..]);
        var directoryLength = checked(12 + tableCount * 16);
        if (directoryLength > data.Length)
            throw new InvalidDataException("The font table directory is incomplete.");

        ReadOnlySpan<byte> nameTable = default;
        for (var index = 0; index < tableCount; index++)
        {
            var record = data.Slice(12 + index * 16, 16);
            if (BinaryPrimitives.ReadUInt32BigEndian(record) != NameTableTag)
                continue;
            var offset = BinaryPrimitives.ReadUInt32BigEndian(record[8..]);
            var length = BinaryPrimitives.ReadUInt32BigEndian(record[12..]);
            if (offset > int.MaxValue || length > int.MaxValue ||
                offset + length > (uint)data.Length)
            {
                throw new InvalidDataException("The font name table points outside the file.");
            }
            nameTable = data.Slice((int)offset, (int)length);
            break;
        }

        if (nameTable.IsEmpty || nameTable.Length < 6)
            throw new InvalidDataException("The font does not contain a readable name table.");
        var nameCount = BinaryPrimitives.ReadUInt16BigEndian(nameTable[2..]);
        var stringsOffset = BinaryPrimitives.ReadUInt16BigEndian(nameTable[4..]);
        if (6 + nameCount * 12 > nameTable.Length || stringsOffset > nameTable.Length)
            throw new InvalidDataException("The font name table is incomplete.");

        var family = ReadBestName(nameTable, nameCount, stringsOffset, [16, 1]);
        var fullName = ReadBestName(nameTable, nameCount, stringsOffset, [4]) ?? family;
        if (string.IsNullOrWhiteSpace(family))
            throw new InvalidDataException("The font family name could not be read.");
        return new OpenTypeFontInfo(family, fullName ?? family);
    }

    private static string? ReadBestName(
        ReadOnlySpan<byte> nameTable,
        int nameCount,
        int stringsOffset,
        IReadOnlyList<int> preferredNameIds)
    {
        string? best = null;
        var bestScore = int.MinValue;
        for (var index = 0; index < nameCount; index++)
        {
            var record = nameTable.Slice(6 + index * 12, 12);
            var platformId = BinaryPrimitives.ReadUInt16BigEndian(record);
            var languageId = BinaryPrimitives.ReadUInt16BigEndian(record[4..]);
            var nameId = BinaryPrimitives.ReadUInt16BigEndian(record[6..]);
            var preferredIndex = IndexOf(preferredNameIds, nameId);
            if (preferredIndex < 0)
                continue;
            var length = BinaryPrimitives.ReadUInt16BigEndian(record[8..]);
            var offset = BinaryPrimitives.ReadUInt16BigEndian(record[10..]);
            if (stringsOffset + offset + length > nameTable.Length)
                continue;

            var value = DecodeName(nameTable.Slice(stringsOffset + offset, length), platformId);
            value = NormalizeName(value);
            if (string.IsNullOrWhiteSpace(value))
                continue;
            var score = (preferredNameIds.Count - preferredIndex) * 100;
            score += platformId switch { 3 => 30, 0 => 20, 1 => 10, _ => 0 };
            if (languageId is 0 or 0x0409)
                score += 5;
            if (score <= bestScore)
                continue;
            best = value;
            bestScore = score;
        }
        return best;
    }

    private static int IndexOf(IReadOnlyList<int> values, int value)
    {
        for (var index = 0; index < values.Count; index++)
        {
            if (values[index] == value)
                return index;
        }
        return -1;
    }

    private static string DecodeName(ReadOnlySpan<byte> bytes, int platformId)
    {
        if (platformId is 0 or 3)
        {
            if (bytes.Length % 2 != 0)
                return string.Empty;
            var characters = new char[bytes.Length / 2];
            for (var index = 0; index < characters.Length; index++)
                characters[index] = (char)BinaryPrimitives.ReadUInt16BigEndian(bytes[(index * 2)..]);
            return new string(characters);
        }
        return Encoding.Latin1.GetString(bytes);
    }

    private static string NormalizeName(string value)
    {
        var cleaned = new string(value
            .Where(character => !char.IsControl(character) || char.IsWhiteSpace(character))
            .ToArray());
        cleaned = string.Join(' ', cleaned.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return cleaned.Length > 80 ? cleaned[..80] : cleaned;
    }
}
