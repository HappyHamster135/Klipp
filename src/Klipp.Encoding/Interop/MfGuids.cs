namespace Klipp.Encoding.Interop;

/// <summary>
/// GUIDs som Media Foundation använder för media types, attribut och kategorier.
/// Värdena är dokumenterade i Windows SDK-headers (mfapi.h, mftransform.h).
/// </summary>
internal static class MfGuids
{
    // ---- Major Types ----
    /// <summary>MFMediaType_Video — major type för video.</summary>
    public static readonly Guid MediaTypeVideo = new("73646976-0000-0010-8000-00aa00389b71");

    /// <summary>MFMediaType_Audio — major type för audio.</summary>
    public static readonly Guid MediaTypeAudio = new("73647561-0000-0010-8000-00aa00389b71");

    // ---- Video Format Subtypes ----
    /// <summary>MFVideoFormat_H264 — H.264 / AVC.</summary>
    public static readonly Guid VideoFormatH264 = new("34363248-0000-0010-8000-00aa00389b71");

    /// <summary>MFVideoFormat_NV12 — NV12 raw video (encoder input format).</summary>
    public static readonly Guid VideoFormatNv12 = new("3231564E-0000-0010-8000-00aa00389b71");

    /// <summary>MFVideoFormat_ARGB32 — 32-bit BGRA raw video.</summary>
    public static readonly Guid VideoFormatArgb32 = new("00000015-0000-0010-8000-00aa00389b71");

    // ---- MFT Categories ----
    /// <summary>MFT_CATEGORY_VIDEO_ENCODER — kategori för video-encoders i MFTEnumEx.</summary>
    public static readonly Guid TransformCategoryVideoEncoder =
        new("D6C02D4B-6833-45B4-971A-05A4B04BAB91");

    // ---- Media Type Attributes ----
    /// <summary>MF_MT_MAJOR_TYPE — major type-attribut på en media type.</summary>
    public static readonly Guid MtMajorType = new("48eba18e-f8c9-4687-bf11-0a74c9f96a8f");

    /// <summary>MF_MT_SUBTYPE — subtype-attribut.</summary>
    public static readonly Guid MtSubType = new("f7e34c9a-42e8-4714-b74b-cb29d72c35e5");

    /// <summary>MF_MT_FRAME_SIZE — packed UINT64 med width (hög 32) och height (låg 32).</summary>
    public static readonly Guid MtFrameSize = new("1652c33d-d6b2-4012-b834-72030849a37d");

    /// <summary>MF_MT_FRAME_RATE — packed UINT64 med numerator (hög 32) och denominator (låg 32).</summary>
    public static readonly Guid MtFrameRate = new("c459a2e8-3d2c-4e44-b132-fee5156c7bb0");

    /// <summary>MF_MT_PIXEL_ASPECT_RATIO — packed UINT64.</summary>
    public static readonly Guid MtPixelAspectRatio = new("c6376a1e-8d0a-4027-be45-6d9a0ad39bb6");

    /// <summary>MF_MT_AVG_BITRATE — UINT32, bits per sekund.</summary>
    public static readonly Guid MtAvgBitrate = new("20332624-fb0d-4d9e-bd0d-cbf6786c102e");

    /// <summary>MF_MT_INTERLACE_MODE — UINT32, anger interlace mode (2 = Progressive).</summary>
    public static readonly Guid MtInterlaceMode = new("e2724bb8-e676-4806-b4b2-a8d6efb44ccd");

    // ---- Sample Attributes ----
    /// <summary>MFSampleExtension_CleanPoint — UINT32, 1 om sample är en keyframe.</summary>
    public static readonly Guid SampleCleanPoint = new("9cdf01d8-a0f0-43ba-b077-eaa06cbd728a");
}
