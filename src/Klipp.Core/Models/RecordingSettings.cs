using Klipp.Core.Enums;

namespace Klipp.Core.Models;

/// <summary>
/// Konfiguration för en inspelningssession. Immutable — för att ändra inställningar
/// skapas en ny instans via <c>with</c>-expression.
/// </summary>
public sealed record class RecordingSettings
{
    /// <summary>Output-bredd i pixlar. Måste vara jämnt delbart med 2.</summary>
    public required int Width { get; init; }

    /// <summary>Output-höjd i pixlar. Måste vara jämnt delbart med 2.</summary>
    public required int Height { get; init; }

    /// <summary>Frames per sekund. Vanliga värden: 30, 60, 120.</summary>
    public required int FrameRate { get; init; }

    /// <summary>Video-bitrate i bits per sekund. 8 Mbps = 8_000_000.</summary>
    public required int VideoBitrate { get; init; }

    /// <summary>Audio-bitrate i bits per sekund. 128 kbps = 128_000.</summary>
    public int AudioBitrate { get; init; } = 128_000;

    /// <summary>Video-codec att använda.</summary>
    public VideoCodec VideoCodec { get; init; } = VideoCodec.H264;

    /// <summary>Audio-codec att använda.</summary>
    public AudioCodec AudioCodec { get; init; } = AudioCodec.Aac;

    /// <summary>
    /// Keyframe-intervall i sekunder. Kortare = snabbare seeking och bättre ring buffer-granularitet,
    /// men sämre kompression. 2 sekunder är en bra default.
    /// </summary>
    public int KeyframeIntervalSeconds { get; init; } = 2;

    /// <summary>
    /// Hur många sekunder av historik som ring buffern ska hålla i RAM.
    /// 60 sekunder vid 8 Mbps ≈ 60 MB. Justera efter spelat innehåll.
    /// </summary>
    public int RingBufferSeconds { get; init; } = 60;

    /// <summary>
    /// Default-inställningar för 1080p60 med 8 Mbps — fungerar bra för de flesta spel.
    /// </summary>
    public static RecordingSettings Default1080p60 => new()
    {
        Width = 1920,
        Height = 1080,
        FrameRate = 60,
        VideoBitrate = 8_000_000
    };

    /// <summary>
    /// Validerar att inställningarna är fysiskt rimliga. Kastar
    /// <see cref="ArgumentException"/> om inte.
    /// </summary>
    public void Validate()
    {
        if (Width <= 0 || Width % 2 != 0)
            throw new ArgumentException("Width must be positive and even.", nameof(Width));
        if (Height <= 0 || Height % 2 != 0)
            throw new ArgumentException("Height must be positive and even.", nameof(Height));
        if (FrameRate is < 1 or > 240)
            throw new ArgumentException("FrameRate must be between 1 and 240.", nameof(FrameRate));
        if (VideoBitrate < 100_000)
            throw new ArgumentException("VideoBitrate must be at least 100 kbps.", nameof(VideoBitrate));
        if (KeyframeIntervalSeconds < 1)
            throw new ArgumentException("KeyframeIntervalSeconds must be at least 1.", nameof(KeyframeIntervalSeconds));
        if (RingBufferSeconds < 5)
            throw new ArgumentException("RingBufferSeconds must be at least 5.", nameof(RingBufferSeconds));
    }
}
