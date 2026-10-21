using System.Diagnostics;

namespace Klipp.Encoding.Audio;

/// <summary>Completes a short prebuffered WAV tail without extending the stop time.</summary>
internal static class AudioTailDrain
{
    internal const int SampleRate = 48000;
    internal const int MaximumFrames = SampleRate;

    internal static int PendingFrames(long started, long stopped, long writtenFrames)
    {
        var stoppedFrames = Math.Max(0, (long)Math.Round(
            (stopped - started) / (double)Stopwatch.Frequency * SampleRate));
        return (int)Math.Min(MaximumFrames, Math.Max(0, stoppedFrames - writtenFrames));
    }

    internal static async Task<int> DrainAsync(AudioSampleMixer mixer,
        Func<byte[], int, CancellationToken, Task> sink, long started, long stopped, long writtenFrames)
    {
        var remaining = PendingFrames(started, stopped, writtenFrames);
        var written = 0;
        var samples = new float[960 * mixer.OutputChannels];
        var bytes = new byte[samples.Length * sizeof(float)];
        using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));
        while (remaining > 0)
        {
            timeout.Token.ThrowIfCancellationRequested();
            var frames = Math.Min(960, remaining);
            var count = frames * mixer.OutputChannels;
            mixer.Read(samples, count);
            Buffer.BlockCopy(samples, 0, bytes, 0, count * sizeof(float));
            await sink(bytes, count * sizeof(float), timeout.Token).WaitAsync(timeout.Token).ConfigureAwait(false);
            remaining -= frames;
            written += frames;
        }
        return written;
    }
}
