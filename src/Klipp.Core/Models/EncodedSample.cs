using Klipp.Core.Enums;

namespace Klipp.Core.Models;

/// <summary>
/// Ett kodat media-sample (video eller audio) redo att skrivas till en MP4-container
/// eller läggas i ring buffern. Ägaren av byte-arrayen är den som skapar samplet —
/// konsumenter ska inte modifiera <see cref="Data"/>.
/// </summary>
public sealed record class EncodedSample
{
    /// <summary>Sample-typ: video eller audio.</summary>
    public required SampleType Type { get; init; }

    /// <summary>Kodad data (H.264 NAL-units eller AAC-frames).</summary>
    public required ReadOnlyMemory<byte> Data { get; init; }

    /// <summary>Tidsstämpel i 100-nanosekundsenheter sedan inspelning startade.</summary>
    public required long Timestamp { get; init; }

    /// <summary>Längd i 100-nanosekundsenheter (t.ex. 1 frame vid 60 FPS = 166666).</summary>
    public required long Duration { get; init; }

    /// <summary>
    /// Sant om detta är en keyframe (I-frame för video, alltid sant för audio).
    /// Ring buffern måste börja "save last N seconds" från närmaste keyframe.
    /// </summary>
    public required bool IsKeyframe { get; init; }
}
