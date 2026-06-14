using Klipp.Core.Enums;
using Klipp.Core.Models;

namespace Klipp.Tests.Fakes;

/// <summary>
/// Hjälpare för att skapa syntetiska <see cref="EncodedSample"/>-objekt i tester
/// utan att behöva specificera varje fält.
/// </summary>
internal static class SampleBuilder
{
    /// <summary>1 frame @ 60 FPS i 100-ns enheter (16.6 ms).</summary>
    public const long FrameDuration60Fps = 166_667;

    /// <summary>1 audio-block @ ~21 ms (1024 samples @ 48 kHz) i 100-ns enheter.</summary>
    public const long AudioBlockDuration = 213_333;

    /// <summary>Skapar en video-keyframe vid given tidsstämpel.</summary>
    public static EncodedSample VideoKeyframe(long timestamp, int sizeBytes = 50_000) => new()
    {
        Type = SampleType.Video,
        Data = new byte[sizeBytes],
        Timestamp = timestamp,
        Duration = FrameDuration60Fps,
        IsKeyframe = true
    };

    /// <summary>Skapar en video P/B-frame vid given tidsstämpel.</summary>
    public static EncodedSample VideoFrame(long timestamp, int sizeBytes = 5_000) => new()
    {
        Type = SampleType.Video,
        Data = new byte[sizeBytes],
        Timestamp = timestamp,
        Duration = FrameDuration60Fps,
        IsKeyframe = false
    };

    /// <summary>Skapar ett audio-block vid given tidsstämpel.</summary>
    public static EncodedSample AudioBlock(long timestamp, int sizeBytes = 500) => new()
    {
        Type = SampleType.Audio,
        Data = new byte[sizeBytes],
        Timestamp = timestamp,
        Duration = AudioBlockDuration,
        IsKeyframe = true
    };

    /// <summary>
    /// Skapar en GOP: 1 keyframe + (frameCount-1) P-frames.
    /// </summary>
    public static IEnumerable<EncodedSample> Gop(long startTimestamp, int frameCount = 120)
    {
        yield return VideoKeyframe(startTimestamp);
        for (int i = 1; i < frameCount; i++)
        {
            yield return VideoFrame(startTimestamp + i * FrameDuration60Fps);
        }
    }
}
