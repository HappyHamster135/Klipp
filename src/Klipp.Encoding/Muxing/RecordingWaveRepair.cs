using System.Buffers.Binary;

namespace Klipp.Encoding.Muxing;

/// <summary>Repairs an owned WAV copy, preserving complete sample frames and trailing metadata.</summary>
public static class RecordingWaveRepair
{
    internal readonly record struct RepairPlan(bool IsRf64, int Channels, int BlockAlign,
        long DataSizeOffset, long DataOffset, long PayloadLength, long CopyLength, long Ds64Offset)
    {
        public bool NeedsRf64Promotion => !IsRf64 && CopyLength - 8 > uint.MaxValue;
    }

    public static bool TryCreateCopy(string sourcePath, string destinationPath)
        => TryCreateCopy(sourcePath, destinationPath, out _);

    public static bool TryCreateCopy(string sourcePath, string destinationPath, out int channels)
    {
        channels = 0;
        if (!File.Exists(sourcePath)) return false;
        using var input = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (!TryGetRepairPlan(input, out var plan)) { channels = plan.Channels; return false; }
        channels = plan.Channels;
        using var output = new FileStream(destinationPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
        input.Position = 0;
        if (plan.NeedsRf64Promotion)
        {
            Span<byte> header = stackalloc byte[48];
            header.Clear();
            "RF64"u8.CopyTo(header);
            BinaryPrimitives.WriteUInt32LittleEndian(header[4..], uint.MaxValue);
            "WAVEds64"u8.CopyTo(header[8..]);
            BinaryPrimitives.WriteUInt32LittleEndian(header[16..], 28);
            output.Write(header);
            input.Position = 12;
            CopyBytes(input, output, plan.CopyLength - 12);
        }
        else CopyBytes(input, output, plan.CopyLength);
        WriteRepairedLengths(output, plan);
        output.Flush(flushToDisk: true);
        return true;
    }

    internal static bool TryGetRepairPlan(Stream input, out RepairPlan plan)
    {
        plan = default;
        if (!input.CanRead || !input.CanSeek || input.Length < 44) return false;
        input.Position = 0;
        Span<byte> header = stackalloc byte[12];
        input.ReadExactly(header);
        var rf64 = header[..4].SequenceEqual("RF64"u8);
        if ((!rf64 && !header[..4].SequenceEqual("RIFF"u8)) || !header[8..].SequenceEqual("WAVE"u8)) return false;
        var blockAlign = 0;
        var channels = 0;
        long ds64Offset = 0;
        ulong rf64DataSize = 0;
        Span<byte> chunk = stackalloc byte[8];
        Span<byte> format = stackalloc byte[16];
        Span<byte> ds64 = stackalloc byte[28];
        while (input.Position <= input.Length - 8)
        {
            input.ReadExactly(chunk);
            var size = BinaryPrimitives.ReadUInt32LittleEndian(chunk[4..]);
            if (chunk[..4].SequenceEqual("ds64"u8))
            {
                if (!rf64 || size < 28 || size > input.Length - input.Position) return false;
                ds64Offset = input.Position;
                input.ReadExactly(ds64);
                rf64DataSize = BinaryPrimitives.ReadUInt64LittleEndian(ds64[8..]);
                input.Position += size - 28 + (size & 1);
            }
            else if (chunk[..4].SequenceEqual("fmt "u8))
            {
                if (size < 16 || size > input.Length - input.Position) return false;
                input.ReadExactly(format);
                var tag = BinaryPrimitives.ReadUInt16LittleEndian(format);
                if (tag is not (1 or 3 or 0xfffe)) return false;
                channels = BinaryPrimitives.ReadUInt16LittleEndian(format[2..]);
                blockAlign = BinaryPrimitives.ReadUInt16LittleEndian(format[12..]);
                var bits = BinaryPrimitives.ReadUInt16LittleEndian(format[14..]);
                if (channels is < 1 or > 32 || bits is not (8 or 16 or 24 or 32 or 64) ||
                    blockAlign != channels * bits / 8) return false;
                input.Position += size - 16 + (size & 1);
            }
            else if (chunk[..4].SequenceEqual("data"u8))
            {
                if (blockAlign == 0 || rf64 && ds64Offset == 0) return false;
                var dataOffset = input.Position;
                var available = input.Length - dataOffset;
                var declared = rf64 && size == uint.MaxValue ? rf64DataSize : size;
                var payload = available / blockAlign * blockAlign;
                var copyLength = dataOffset + payload;
                // A finalized file can contain metadata after data. If a stale
                // checkpoint points into raw samples, consume complete frames
                // through EOF instead of losing all audio since that checkpoint.
                if (declared > 0 && declared <= (ulong)available && declared % (uint)blockAlign == 0 &&
                    HasCompleteTrailingChunks(input, dataOffset + (long)declared + (long)(declared & 1)))
                {
                    payload = (long)declared;
                    copyLength = input.Length;
                }
                plan = new(rf64, channels, blockAlign, dataOffset - 4, dataOffset, payload, copyLength, ds64Offset);
                return payload > 0;
            }
            else
            {
                if (size > input.Length - input.Position) return false;
                input.Position += (long)size + (size & 1);
            }
        }
        return false;
    }

    internal static void WriteRepairedLengths(Stream output, RepairPlan plan)
    {
        var shift = plan.NeedsRf64Promotion ? 36 : 0;
        var rf64 = plan.IsRf64 || plan.NeedsRf64Promotion;
        var ds64Offset = plan.NeedsRf64Promotion ? 20 : plan.Ds64Offset;
        Span<byte> size = stackalloc byte[8];
        output.Position = 4;
        BinaryPrimitives.WriteUInt32LittleEndian(size, rf64 ? uint.MaxValue : checked((uint)(output.Length - 8)));
        output.Write(size[..4]);
        output.Position = plan.DataSizeOffset + shift;
        BinaryPrimitives.WriteUInt32LittleEndian(size, rf64 ? uint.MaxValue : checked((uint)plan.PayloadLength));
        output.Write(size[..4]);
        if (!rf64) return;
        output.Position = ds64Offset;
        BinaryPrimitives.WriteUInt64LittleEndian(size, checked((ulong)(output.Length - 8)));
        output.Write(size);
        BinaryPrimitives.WriteUInt64LittleEndian(size, checked((ulong)plan.PayloadLength));
        output.Write(size);
        BinaryPrimitives.WriteUInt64LittleEndian(size, checked((ulong)(plan.PayloadLength / plan.BlockAlign)));
        output.Write(size);
    }

    private static bool HasCompleteTrailingChunks(Stream input, long position)
    {
        Span<byte> chunk = stackalloc byte[8];
        while (position < input.Length)
        {
            if (position > input.Length - 8) return false;
            input.Position = position;
            input.ReadExactly(chunk);
            for (var index = 0; index < 4; index++) if (chunk[index] is < 0x20 or > 0x7e) return false;
            var size = BinaryPrimitives.ReadUInt32LittleEndian(chunk[4..]);
            var advance = 8L + size + (size & 1);
            if (advance > input.Length - position) return false;
            position += advance;
        }
        return position == input.Length;
    }

    private static void CopyBytes(Stream input, Stream output, long bytes)
    {
        var buffer = new byte[65536];
        while (bytes > 0)
        {
            var count = (int)Math.Min(buffer.Length, bytes);
            input.ReadExactly(buffer.AsSpan(0, count));
            output.Write(buffer, 0, count);
            bytes -= count;
        }
    }
}
