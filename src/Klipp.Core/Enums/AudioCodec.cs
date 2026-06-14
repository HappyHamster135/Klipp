namespace Klipp.Core.Enums;

/// <summary>
/// Stödda audio-codecs för inspelning och uppspelning.
/// </summary>
public enum AudioCodec
{
    /// <summary>AAC-LC — standard för MP4-containrar, bredast stöd.</summary>
    Aac,

    /// <summary>Opus — bättre kvalitet per bitrate, främst för streaming.</summary>
    Opus
}
