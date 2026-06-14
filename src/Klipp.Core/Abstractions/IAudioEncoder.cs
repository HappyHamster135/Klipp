using Klipp.Core.Models;

namespace Klipp.Core.Abstractions;

/// <summary>
/// Kodar raw PCM audio till komprimerade EncodedSample.
/// Implementationer wrappar AAC/Opus encoders via Media Foundation.
/// </summary>
public interface IAudioEncoder : IAsyncDisposable
{
    /// <summary>
    /// Initialiserar encoder:n med givna inställningar.
    /// </summary>
    Task InitializeAsync(RecordingSettings settings, CancellationToken cancellationToken = default);

    /// <summary>
    /// Kodar ett PCM audio-block. Sample-format förväntas vara 16-bit signed
    /// stereo @ 48 kHz (standard för WASAPI loopback).
    /// </summary>
    IAsyncEnumerable<EncodedSample> EncodeAsync(
        ReadOnlyMemory<byte> pcmData,
        long timestamp,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Flushar interna buffrar och returnerar eventuella kvarvarande samples.
    /// </summary>
    IAsyncEnumerable<EncodedSample> FlushAsync(CancellationToken cancellationToken = default);
}
