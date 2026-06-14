using System.Runtime.InteropServices;

namespace Klipp.Encoding.Interop;

/// <summary>
/// Struct-typer från Media Foundation-headers.
/// Minneslayouten måste matcha exakt — använd [StructLayout(LayoutKind.Sequential)]
/// och undvik managed types som strings eller arrays.
/// </summary>
internal static class MfStructs
{
    /// <summary>
    /// MFT_REGISTER_TYPE_INFO (mfobjects.h) — beskriver en input- eller output-mediatyp
    /// när vi söker efter MFTs med MFTEnumEx.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct MftRegisterTypeInfo
    {
        public Guid GuidMajorType;
        public Guid GuidSubtype;
    }

    /// <summary>
    /// MFT_OUTPUT_STREAM_INFO (mftransform.h) — innehåller info om en MFT:s output stream,
    /// inklusive minsta buffer-storlek encoder behöver för output samples.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct MftOutputStreamInfo
    {
        public uint DwFlags;
        public uint CbSize;
        public uint CbAlignment;
    }

    /// <summary>
    /// MFT_INPUT_STREAM_INFO (mftransform.h) — info om en MFT:s input stream.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct MftInputStreamInfo
    {
        public long HnsMaxLatency;
        public uint DwFlags;
        public uint CbSize;
        public uint CbMaxLookahead;
        public uint CbAlignment;
    }

    /// <summary>
    /// MFT_OUTPUT_DATA_BUFFER (mftransform.h) — wrappar en sample som returneras
    /// från IMFTransform::ProcessOutput.
    /// </summary>
    /// <remarks>
    /// PEvents-fältet är en COM-pointer (IMFCollection) som vi inte använder,
    /// men måste finnas med för rätt minneslayout.
    /// </remarks>
    [StructLayout(LayoutKind.Sequential)]
    public struct MftOutputDataBuffer
    {
        public uint DwStreamID;
        public nint PSample; // IMFSample*
        public uint DwStatus;
        public nint PEvents; // IMFCollection*
    }
}
