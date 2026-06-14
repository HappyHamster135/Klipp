using Klipp.Core.Models;

namespace Klipp.Core.Abstractions;

/// <summary>
/// Skriver kodade samples till en MP4-container på disk.
/// Implementationer wrappar Media Foundation Sink Writer.
/// En writer används en gång per fil — skapa en ny instans för varje export.
/// </summary>
public interface IMp4Writer : IAsyncDisposable
{
    /// <summary>
    /// Initialiserar writern med output-sökväg och inställningar.
    /// </summary>
    Task InitializeAsync(string outputPath, RecordingSettings settings, CancellationToken cancellationToken = default);

    /// <summary>
    /// Skriver ett sample (video eller audio) till containern.
    /// Samples måste vara i tidsordning per typ; mux:en hanterar interleaving.
    /// </summary>
    Task WriteSampleAsync(EncodedSample sample, CancellationToken cancellationToken = default);

    /// <summary>
    /// Finalize:ar containern (skriver MOOV-box och stänger filen).
    /// Filen är inte spelbar förrän detta körs.
    /// </summary>
    Task FinalizeAsync(CancellationToken cancellationToken = default);
}
