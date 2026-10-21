using System.Buffers.Binary;

namespace Klipp.Encoding.Muxing;

/// <summary>Float WAV with 64-bit RF64 lengths and a durable format header for crash recovery.</summary>
public sealed class Rf64WaveFileWriter : IDisposable
{
    internal const int HeaderLength = 104;
    private readonly Stream _stream;
    private readonly bool _leaveOpen;
    private readonly int _sampleRate, _channels, _blockAlign;
    private bool _disposed;

    public Rf64WaveFileWriter(string path, int sampleRate, int channels)
        : this(Open(path, sampleRate, channels), sampleRate, channels) { }

    private static FileStream Open(string path, int sampleRate, int channels)
    {
        if (sampleRate is < 8000 or > 384000) throw new ArgumentOutOfRangeException(nameof(sampleRate));
        if (channels is < 1 or > 32) throw new ArgumentOutOfRangeException(nameof(channels));
        return new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 32768, FileOptions.SequentialScan);
    }

    public Rf64WaveFileWriter(Stream stream, int sampleRate, int channels, bool leaveOpen = false)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanWrite || !stream.CanSeek) throw new ArgumentException("RF64 capture requires a writable, seekable stream.", nameof(stream));
        if (sampleRate is < 8000 or > 384000) throw new ArgumentOutOfRangeException(nameof(sampleRate));
        if (channels is < 1 or > 32) throw new ArgumentOutOfRangeException(nameof(channels));
        if (stream.Length != 0) throw new ArgumentException("Choose an empty recording stream.", nameof(stream));
        _stream = stream; _leaveOpen = leaveOpen;
        _sampleRate = sampleRate; _channels = channels; _blockAlign = channels * sizeof(float);
        try { WriteHeader(0); FlushDurable(); }
        catch { if (!leaveOpen) stream.Dispose(); throw; }
    }

    public void Write(byte[] buffer, int offset, int count)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(buffer);
        if (count % _blockAlign != 0) throw new ArgumentException("Write complete interleaved audio frames.", nameof(count));
        _stream.Write(buffer.AsSpan(offset, count));
    }

    private void WriteHeader(long payloadBytes)
    {
        Span<byte> header = stackalloc byte[HeaderLength];
        header.Clear();
        "RF64"u8.CopyTo(header);
        BinaryPrimitives.WriteUInt32LittleEndian(header[4..], uint.MaxValue);
        "WAVEds64"u8.CopyTo(header[8..]);
        BinaryPrimitives.WriteUInt32LittleEndian(header[16..], 28);
        BinaryPrimitives.WriteUInt64LittleEndian(header[20..], checked((ulong)(HeaderLength + payloadBytes - 8)));
        BinaryPrimitives.WriteUInt64LittleEndian(header[28..], checked((ulong)payloadBytes));
        BinaryPrimitives.WriteUInt64LittleEndian(header[36..], checked((ulong)(payloadBytes / _blockAlign)));
        "fmt "u8.CopyTo(header[48..]);
        BinaryPrimitives.WriteUInt32LittleEndian(header[52..], 40);
        BinaryPrimitives.WriteUInt16LittleEndian(header[56..], 0xfffe);
        BinaryPrimitives.WriteUInt16LittleEndian(header[58..], (ushort)_channels);
        BinaryPrimitives.WriteUInt32LittleEndian(header[60..], (uint)_sampleRate);
        BinaryPrimitives.WriteUInt32LittleEndian(header[64..], checked((uint)(_sampleRate * _blockAlign)));
        BinaryPrimitives.WriteUInt16LittleEndian(header[68..], (ushort)_blockAlign);
        BinaryPrimitives.WriteUInt16LittleEndian(header[70..], 32);
        BinaryPrimitives.WriteUInt16LittleEndian(header[72..], 22);
        BinaryPrimitives.WriteUInt16LittleEndian(header[74..], 32);
        // Unspecified channel mask keeps stereo pairs as discrete indices.
        new Guid("00000003-0000-0010-8000-00aa00389b71").TryWriteBytes(header[80..96]);
        "data"u8.CopyTo(header[96..]);
        BinaryPrimitives.WriteUInt32LittleEndian(header[100..], uint.MaxValue);
        _stream.Position = 0;
        _stream.Write(header);
        _stream.Position = HeaderLength + payloadBytes;
    }

    private void FlushDurable()
    {
        if (_stream is FileStream file) file.Flush(flushToDisk: true);
        else _stream.Flush();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            var completePayload = Math.Max(0, _stream.Length - HeaderLength) / _blockAlign * _blockAlign;
            _stream.SetLength(HeaderLength + completePayload);
            WriteHeader(completePayload);
            FlushDurable();
        }
        finally { if (!_leaveOpen) _stream.Dispose(); }
    }
}
