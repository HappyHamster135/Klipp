using System.Runtime.CompilerServices;
using Klipp.Core.Abstractions;
using Klipp.Core.Enums;
using Klipp.Core.Models;

namespace Klipp.Encoding.Video;

/// <summary>
/// "Fake" encoder som inte gör riktig komprimering — wrappar bara raw BGRA-pixels
/// som om de vore EncodedSample. Används för att testa arkitekturen end-to-end
/// innan riktig H.264-encoding är på plats.
/// </summary>
public sealed class RawSampleEncoder : IVideoEncoder
{
    private RecordingSettings? _settings;
    private long _frameDuration100Ns;
    private bool _initialized;
    private bool _disposed;

    public Task InitializeAsync(RecordingSettings settings, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_initialized) throw new InvalidOperationException("Encoder är redan initialiserad.");

        ArgumentNullException.ThrowIfNull(settings);
        settings.Validate();

        _settings = settings;
        _frameDuration100Ns = 10_000_000L / settings.FrameRate;
        _initialized = true;

        return Task.CompletedTask;
    }

    public async IAsyncEnumerable<EncodedSample> EncodeAsync(
        VideoFrame frame,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_initialized) throw new InvalidOperationException("Anropa InitializeAsync först.");

        var sample = new EncodedSample
        {
            Type = SampleType.Video,
            Data = frame.PixelData,
            Timestamp = frame.Timestamp,
            Duration = _frameDuration100Ns,
            IsKeyframe = true
        };

        yield return sample;
        await Task.CompletedTask;
    }

    public IAsyncEnumerable<EncodedSample> FlushAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return AsyncEnumerable.Empty<EncodedSample>();
    }

    public ValueTask DisposeAsync()
    {
        _disposed = true;
        return ValueTask.CompletedTask;
    }
}

internal static class AsyncEnumerable
{
    public static IAsyncEnumerable<T> Empty<T>() => EmptyAsyncEnumerable<T>.Instance;

    private sealed class EmptyAsyncEnumerable<T> : IAsyncEnumerable<T>, IAsyncEnumerator<T>
    {
        public static readonly EmptyAsyncEnumerable<T> Instance = new();
        public T Current => default!;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        public ValueTask<bool> MoveNextAsync() => ValueTask.FromResult(false);
        public IAsyncEnumerator<T> GetAsyncEnumerator(CancellationToken cancellationToken = default) => this;
    }
}
