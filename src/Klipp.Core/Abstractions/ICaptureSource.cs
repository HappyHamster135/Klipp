using Klipp.Core.Models;

namespace Klipp.Core.Abstractions;

/// <summary>
/// Generisk källa som producerar en ström av media-samples av typ <typeparamref name="T"/>.
/// Implementationer: WgcCaptureSource (video från fönster/skärm),
/// WasapiLoopbackSource (system-ljud).
/// </summary>
/// <typeparam name="T">Sample-typ, vanligtvis VideoFrame eller raw audio buffer.</typeparam>
public interface ICaptureSource<T> : IAsyncDisposable
{
    /// <summary>
    /// Sant medan källan aktivt capturear.
    /// </summary>
    bool IsCapturing { get; }

    /// <summary>
    /// Startar capture. Idempotent — andra anropet är no-op om redan startad.
    /// </summary>
    Task StartAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Stoppar capture. Idempotent. Frigör inte resurser — använd DisposeAsync för det.
    /// </summary>
    Task StopAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Asynkron ström av samples. Konsumenten itererar med await foreach.
    /// Strömmen avslutas när StopAsync anropas eller token cancelleras.
    /// </summary>
    IAsyncEnumerable<T> ReadSamplesAsync(CancellationToken cancellationToken = default);
}
