namespace Klipp.Core.Enums;

/// <summary>
/// Stödda video-codecs för inspelning och uppspelning.
/// </summary>
public enum VideoCodec
{
    /// <summary>H.264 / AVC — bredast stöd, hårdvaruaccelererad på alla moderna GPU:er.</summary>
    H264,

    /// <summary>H.265 / HEVC — bättre kompression, kräver nyare hårdvara.</summary>
    H265,

    /// <summary>AV1 — modern royalty-fri codec, kräver mycket ny hårdvara (RTX 40-serien, RDNA3, Arc).</summary>
    Av1
}
