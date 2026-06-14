using Klipp.Core.Models;

namespace Klipp.Core.Abstractions;

/// <summary>
/// Kodar raw VideoFrame-data till komprimerade EncodedSample.
/// Implementationer wrappar hårdvaru-encoders (NVENC, AMF, QuickSync) via Media Foundation.
/// </summary>
public interface IVideoEncoder : IAsyncDisposable
{
    /// <summary>
    /// Initialiserar encoder:n med givna inställningar.
    /// </summary>
    Task InitializeAsync(RecordingSettings settings, CancellationToken cancellationToken = default);

    /// <summary>
    /// Skickar in en frame för kodning. Returnerar noll, ett eller flera samples
    /// beroende på encoder:ns interna buffring (B-frames kan fördröja output).
    /// </summary>
    IAsyncEnumerable<EncodedSample> EncodeAsync(VideoFrame frame, CancellationToken cancellationToken = default);

    /// <summary>
    /// Flushar interna buffrar och returnerar eventuella kvarvarande samples.
    /// </summary>
    IAsyncEnumerable<EncodedSample> FlushAsync(CancellationToken cancellationToken = default);
}
