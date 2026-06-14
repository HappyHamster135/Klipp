using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

using Klipp.Encoding.Interop.Com;

namespace Klipp.Encoding.Interop;

/// <summary>
/// P/Invoke-deklarationer för "fria" Media Foundation-funktioner från mfplat.dll.
/// Detta är funktioner som inte hänger på något COM-interface — de skapar nya objekt
/// eller utför globala operationer.
/// </summary>
internal static partial class MfApi
{
    private const string MfPlat = "mfplat.dll";

    /// <summary>
    /// MFCreateMediaType — skapar en tom IMFMediaType som vi kan fylla med attribut.
    /// </summary>
    [LibraryImport(MfPlat)]
    [return: MarshalAs(UnmanagedType.I4)]
    public static partial int MFCreateMediaType(
        [MarshalUsing(typeof(UniqueComInterfaceMarshaller<IMFMediaType>))] out IMFMediaType ppMFType);

    /// <summary>
    /// MFCreateSample — skapar en tom IMFSample utan buffers.
    /// </summary>
    [LibraryImport(MfPlat)]
    [return: MarshalAs(UnmanagedType.I4)]
    public static partial int MFCreateSample(
        [MarshalUsing(typeof(UniqueComInterfaceMarshaller<IMFSample>))] out IMFSample ppIMFSample);

    /// <summary>
    /// MFCreateMemoryBuffer — skapar en IMFMediaBuffer med given maximal storlek.
    /// Den faktiska "current length" sätts senare när vi skriver data.
    /// </summary>
    [LibraryImport(MfPlat)]
    [return: MarshalAs(UnmanagedType.I4)]
    public static partial int MFCreateMemoryBuffer(
        uint cbMaxLength,
        [MarshalUsing(typeof(UniqueComInterfaceMarshaller<IMFMediaBuffer>))] out IMFMediaBuffer ppBuffer);

    /// <summary>
    /// MFTEnumEx — listar alla MFTs (Media Foundation Transforms) som matchar givna kriterier.
    /// För vår encoder söker vi efter category=VideoEncoder, input=NV12, output=H264.
    /// </summary>
    /// <param name="guidCategory">Kategori, t.ex. MFT_CATEGORY_VIDEO_ENCODER.</param>
    /// <param name="flags">MFT_ENUM_FLAG-kombination (vi använder Hardware | SortAndFilter).</param>
    /// <param name="pInputType">Input type-filter (in by ref, kan vara null).</param>
    /// <param name="pOutputType">Output type-filter (in by ref, kan vara null).</param>
    /// <param name="pppMFTActivate">Array av IMFActivate-pekare (ut). Måste frigöras med CoTaskMemFree.</param>
    /// <param name="pnumMFTActivate">Antal aktivatörer i arrayen (ut).</param>
    [LibraryImport(MfPlat)]
    [return: MarshalAs(UnmanagedType.I4)]
    public static partial int MFTEnumEx(
        nint guidCategory,
        uint flags,
        nint pInputType,
        nint pOutputType,
        out nint pppMFTActivate,
        out uint pnumMFTActivate);

    /// <summary>
    /// CoTaskMemFree — frigör minne som allokerats av COM (t.ex. arrayen från MFTEnumEx).
    /// </summary>
    [LibraryImport("ole32.dll")]
    public static partial void CoTaskMemFree(nint pv);
}
