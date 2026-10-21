namespace Klipp.Core.Capture;

public readonly record struct CaptureDiskReserve(long TemporaryBytes, long OutputBytes);

/// <summary>Leave room for WAV repair and the staged MP4 while originals remain recoverable.</summary>
public static class CaptureDiskProtectionPolicy
{
    public const long MinimumFreeBytes = 2L * 1024 * 1024 * 1024;
    private const long MarginBytes = 256L * 1024 * 1024;

    public static CaptureDiskReserve CalculateReserve(bool manualRecording, long videoBytes, long audioBytes,
        double recordedSeconds, int audioSources, int videoBitrateMbps, int clipLengthSeconds)
    {
        var rate = Math.Clamp(videoBitrateMbps, 1, 120) * 1_000_000d / 8;
        var duration = double.IsFinite(recordedSeconds) ? Math.Max(0, recordedSeconds) : 0;
        var sources = Math.Clamp(audioSources, 0, 4);
        var audioRate = sources == 0 ? 0 : (sources + 1) * 256_000d / 8;
        // Two polling intervals plus encoder/audio tails remain available even
        // when a stop is briefly waiting for an in-flight clip save.
        var headroom = (rate + sources * 48000d * 2 * sizeof(float)) * 15;
        if (!manualRecording)
        {
            var clipSeconds = Math.Clamp(clipLengthSeconds, 15, 120);
            return new(ToBytes(MarginBytes + headroom), ToBytes(MarginBytes + (rate + audioRate) * clipSeconds * 1.1));
        }
        return new(ToBytes((double)Math.Max(0, audioBytes) + MarginBytes + headroom),
            ToBytes(Math.Max(0, videoBytes) * 1.05 + duration * audioRate + MarginBytes));
    }

    public static long RequiredBytes(CaptureDiskReserve reserve, bool sameVolume, bool temporaryVolume) =>
        Math.Max(MinimumFreeBytes, sameVolume
            ? ToBytes((double)reserve.TemporaryBytes + reserve.OutputBytes)
            : temporaryVolume ? reserve.TemporaryBytes : reserve.OutputBytes);

    public static bool ShouldStop(long availableBytes, long requiredBytes) =>
        Math.Max(0, availableBytes) <= Math.Max(MinimumFreeBytes, requiredBytes);

    private static long ToBytes(double value) => value >= long.MaxValue ? long.MaxValue : (long)Math.Ceiling(Math.Max(0, value));
}
