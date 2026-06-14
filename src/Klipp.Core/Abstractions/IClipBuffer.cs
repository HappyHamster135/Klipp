using Klipp.Core.Models;

namespace Klipp.Core.Abstractions;

/// <summary>
/// Buffrar kodade samples i RAM. Killer-featuren: FlushLastSecondsAsync
/// låter användaren spara senaste N sekunder retroaktivt (Medal-style instant replay).
/// </summary>
/// <remarks>
/// Implementationer maaste vara traad-saekra — capture-loopen lägger till samples från
/// en tråd medan UI:t kan trigga flush från en annan.
/// </remarks>
public interface IClipBuffer : IAsyncDisposable
{
    /// <summary>
    /// Hur många sekunder av historik som faktiskt finns i bufferten just nu.
    /// </summary>
    double BufferedSeconds { get; }

    /// <summary>
    /// Lägger till ett sample i bufferten. Tråd-säker. Äldre samples evikteras
    /// automatiskt när buffer-limit nås — alltid GOP-aligned (start på keyframe).
    /// </summary>
    ValueTask AppendAsync(EncodedSample sample, CancellationToken cancellationToken = default);

    /// <summary>
    /// Skriver senaste seconds sekunder från bufferten till en MP4-fil.
    /// Startpunkten justeras bakåt till närmaste keyframe så filen är spelbar.
    /// </summary>
    /// <param name="seconds">Antal sekunder att spara, normalt 15-60.</param>
    /// <param name="outputPath">Sökväg till output-filen.</param>
    /// <returns>Faktisk längd på den sparade filen i sekunder.</returns>
    Task<double> FlushLastSecondsAsync(
        int seconds,
        string outputPath,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Rensar bufferten. Anropas när inspelning stoppas eller settings ändras.
    /// </summary>
    void Clear();
}
