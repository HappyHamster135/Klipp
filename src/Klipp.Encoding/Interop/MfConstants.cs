namespace Klipp.Encoding.Interop;

/// <summary>
/// Numeriska konstanter från Media Foundation-headers.
/// Värdena är dokumenterade i mfapi.h, mftransform.h och mferror.h.
/// </summary>
internal static class MfConstants
{
    // ---- MFStartup flags (mfapi.h) ----
    public const int MF_VERSION = 0x00020070;
    public const int MFSTARTUP_FULL = 0;
    public const int MFSTARTUP_LITE = 1;

    // ---- MFT_ENUM_FLAG (mfapi.h) — används med MFTEnumEx ----
    [Flags]
    public enum MftEnumFlag : uint
    {
        None = 0x00000000,
        SyncMft = 0x00000001,
        AsyncMft = 0x00000002,
        Hardware = 0x00000004,
        FieldOfUse = 0x00000008,
        LocalMft = 0x00000010,
        TranscodeOnly = 0x00000020,
        SortAndFilter = 0x00000040,
        All = 0x0000003F
    }

    // ---- MFT_MESSAGE_TYPE (mftransform.h) — anrop till IMFTransform::ProcessMessage ----
    public enum MftMessageType : uint
    {
        CommandFlush = 0x00000000,
        CommandDrain = 0x00000001,
        SetD3DManager = 0x00000002,
        NotifyBeginStreaming = 0x10000000,
        NotifyEndStreaming = 0x10000001,
        NotifyEndOfStream = 0x10000002,
        NotifyStartOfStream = 0x10000003
    }

    // ---- Video Interlace Mode (mfapi.h) — värde för MF_MT_INTERLACE_MODE ----
    public enum VideoInterlaceMode : uint
    {
        Unknown = 0,
        Progressive = 2,
        FieldInterleavedUpperFirst = 3,
        FieldInterleavedLowerFirst = 4
    }

    // ---- MF Error Codes (mferror.h) ----
    /// <summary>MF_E_TRANSFORM_NEED_MORE_INPUT — encodern behöver fler input frames innan output kan produceras.</summary>
    public const int MF_E_TRANSFORM_NEED_MORE_INPUT = unchecked((int)0xC00D6D72);

    /// <summary>MF_E_TRANSFORM_STREAM_CHANGE — output stream type har ändrats, måste konfigureras om.</summary>
    public const int MF_E_TRANSFORM_STREAM_CHANGE = unchecked((int)0xC00D6D61);

    // ---- HRESULTs ----
    public const int S_OK = 0;
}
