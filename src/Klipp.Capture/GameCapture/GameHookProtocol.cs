using System.Buffers.Binary;
using System.Text;

namespace Klipp.Capture.GameCapture;

internal static class GameHookProtocol
{
    public const uint Magic = 0x50494C4B;
    public const uint Version = 1;
    public const uint Hello = 1, Frame = 2, Failure = 3, Stopped = 4, Heartbeat = 10, Popup = 11, Hide = 12, Disconnect = 13, Ready = 14;
    public const int MaximumMessage = 4 * 1024 * 1024;

    public static async Task<(uint Type, byte[] Payload)> ReadAsync(Stream stream, CancellationToken cancellationToken)
    {
        var header = new byte[8];
        await stream.ReadExactlyAsync(header, cancellationToken).ConfigureAwait(false);
        var type = BinaryPrimitives.ReadUInt32LittleEndian(header);
        var size = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(4));
        if (size > MaximumMessage) throw new InvalidDataException("The game bridge sent an invalid message.");
        var payload = new byte[size];
        await stream.ReadExactlyAsync(payload, cancellationToken).ConfigureAwait(false);
        return (type, payload);
    }

    public static async Task WriteAsync(Stream stream, uint type, byte[] payload, CancellationToken cancellationToken)
    {
        var header = new byte[8];
        BinaryPrimitives.WriteUInt32LittleEndian(header, type);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4), (uint)payload.Length);
        await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        if (payload.Length != 0) await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
    }

    public static byte[] UInt(uint value)
    {
        var output = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(output, value);
        return output;
    }

    public static byte[] StartArguments(string pipe, string nonce, nint window, int frameRate)
    {
        if (pipe.Length > 126 || nonce.Length != 64) throw new ArgumentException("Invalid game bridge identity.");
        using var stream = new MemoryStream(344);
        using var writer = new BinaryWriter(stream, Encoding.Unicode, leaveOpen: true);
        writer.Write(Magic); writer.Write(Version); writer.Write((uint)Environment.ProcessId); writer.Write((uint)frameRate);
        writer.Write((ulong)window.ToInt64());
        var pipeBytes = new byte[256]; Encoding.Unicode.GetBytes(pipe, pipeBytes); writer.Write(pipeBytes);
        writer.Write(Encoding.ASCII.GetBytes(nonce));
        return stream.ToArray();
    }

    internal sealed record FrameInfo(uint Generation, int Width, int Height, uint Format, uint AdapterLow, int AdapterHigh,
        ulong[] Handles, ulong[] Sequence, long[] Timestamp)
    {
        public static FrameInfo Parse(byte[] payload)
        {
            if (payload.Length != 96) throw new InvalidDataException("The game bridge frame description is invalid.");
            using var reader = new BinaryReader(new MemoryStream(payload));
            var generation = reader.ReadUInt32(); var width = reader.ReadInt32(); var height = reader.ReadInt32();
            var format = reader.ReadUInt32(); var low = reader.ReadUInt32(); var high = reader.ReadInt32();
            if (generation == 0 || width <= 0 || height <= 0 || width > 8192 || height > 8192 ||
                (long)width * height * 4 > 128L * 1024 * 1024 || (format != 87 && format != 28))
                throw new InvalidDataException("The game sent an unsupported capture texture.");
            var handles = new ulong[3]; var sequence = new ulong[3]; var timestamps = new long[3];
            for (var i = 0; i < 3; i++) handles[i] = reader.ReadUInt64();
            for (var i = 0; i < 3; i++) sequence[i] = reader.ReadUInt64();
            for (var i = 0; i < 3; i++) timestamps[i] = reader.ReadInt64();
            return new(generation, width, height, format, low, high, handles, sequence, timestamps);
        }
    }
}
