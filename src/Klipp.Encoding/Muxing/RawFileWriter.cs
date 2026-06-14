using System.Buffers.Binary;
using Klipp.Core.Abstractions;
using Klipp.Core.Enums;
using Klipp.Core.Models;

namespace Klipp.Encoding.Muxing;

/// <summary>
/// "Fake" MP4-writer som skriver EncodedSample-bytes till en .raw-fil med header.
/// Används istället för en riktig MP4-muxer tills vi har en encoder som producerar
/// riktig H.264.
/// </summary>
/// <remarks>
/// Fil-format (little-endian):
///   Header (32 bytes):
///     [0..3]   Magic: "KRAW"
///     [4..7]   Version: 1
///     [8..11]  Width
///     [12..15] Height
///     [16..19] Frame rate
///     [20..23] Frame count (skrivs vid Finalize)
///     [24..27] Format: 1=BGRA8
///     [28..31] Reserved
///   Sedan: rå sample-data, en frame efter en annan, ingen padding.
///
/// Inspelningar kan inspekteras med Klipp.RawInspector eller läsas tillbaka
/// programmatiskt.
/// </remarks>
public sealed class RawFileWriter : IMp4Writer
{
    private const uint Magic = 0x57415243; // "KRAW" little-endian
    private const uint FormatBgra8 = 1;
    private const int HeaderSize = 32;

    private FileStream? _file;
    private RecordingSettings? _settings;
    private int _frameCount;
    private bool _initialized;
    private bool _finalized;
    private bool _disposed;

    /// <inheritdoc/>
    public async Task InitializeAsync(string outputPath, RecordingSettings settings, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_initialized) throw new InvalidOperationException("Writer är redan initialiserad.");

        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        ArgumentNullException.ThrowIfNull(settings);

        _settings = settings;
        _file = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.Read);

        // Skriv placeholder-header — vi uppdaterar frame count vid Finalize
        var header = new byte[HeaderSize];
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(0, 4), Magic);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4, 4), 1u); // version
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(8, 4), settings.Width);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(12, 4), settings.Height);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(16, 4), settings.FrameRate);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(20, 4), 0); // frame count (uppdateras)
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(24, 4), FormatBgra8);

        await _file.WriteAsync(header, cancellationToken).ConfigureAwait(false);

        _initialized = true;
    }

    /// <inheritdoc/>
    public async Task WriteSampleAsync(EncodedSample sample, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_initialized) throw new InvalidOperationException("Anropa InitializeAsync först.");
        if (_finalized) throw new InvalidOperationException("Writer är redan finalized.");

        // Vi sparar bara video för nu (audio ignoreras)
        if (sample.Type != SampleType.Video) return;

        await _file!.WriteAsync(sample.Data, cancellationToken).ConfigureAwait(false);
        _frameCount++;
    }

    /// <inheritdoc/>
    public async Task FinalizeAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_initialized) throw new InvalidOperationException("Anropa InitializeAsync först.");
        if (_finalized) return;

        // Gå tillbaka och uppdatera frame count i headern
        _file!.Seek(20, SeekOrigin.Begin);
        var frameCountBytes = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(frameCountBytes, _frameCount);
        await _file.WriteAsync(frameCountBytes, cancellationToken).ConfigureAwait(false);

        await _file.FlushAsync(cancellationToken).ConfigureAwait(false);
        _finalized = true;
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        if (_file is not null)
        {
            try
            {
                if (_initialized && !_finalized)
                    await FinalizeAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch { /* ignorera fel vid dispose */ }

            await _file.DisposeAsync().ConfigureAwait(false);
            _file = null;
        }
    }
}
