using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace Klipp.Encoding.Interop.Com;

/// <summary>
/// IMFMediaType — beskriver formatet av en mediaström (codec, upplösning, bitrate, etc).
/// Ärver från IMFAttributes — alla formatdetaljer sätts som attribut på objektet.
/// </summary>
/// <remarks>
/// VIKTIGT: I COM ärver inte interfaces på samma sätt som i C#. När IMFMediaType "ärver"
/// från IMFAttributes betyder det att alla IMFAttributes-metoder finns FÖRST i vtable,
/// följt av IMFMediaType:s egna metoder. Vi måste därför upprepa alla IMFAttributes-metoder
/// i samma ordning, OCH lägga till de nya — annars blir vtable-offsets fel.
///
/// Detta är konstigt, men det är så Windows COM fungerar.
/// </remarks>
[GeneratedComInterface]
[Guid("44ae0fa8-ea31-4109-8d2e-4cae4997c555")]
internal partial interface IMFMediaType
{
    // ---- Ärvt från IMFAttributes (måste komma först i samma ordning) ----
    [PreserveSig] int GetItem(in Guid guidKey, nint pValue);
    [PreserveSig] int GetItemType(in Guid guidKey, out uint pType);
    [PreserveSig] int CompareItem(in Guid guidKey, nint value, out int pbResult);
    [PreserveSig] int Compare(nint pTheirs, uint matchType, out int pbResult);

    [PreserveSig] int GetUINT32(in Guid guidKey, out uint punValue);
    [PreserveSig] int GetUINT64(in Guid guidKey, out ulong punValue);
    [PreserveSig] int GetDouble(in Guid guidKey, out double pfValue);
    [PreserveSig] int GetGUID(in Guid guidKey, out Guid pguidValue);

    [PreserveSig] int GetStringLength(in Guid guidKey, out uint pcchLength);
    [PreserveSig] int GetString(in Guid guidKey, nint pwszValue, uint cchBufSize, out uint pcchLength);
    [PreserveSig] int GetAllocatedString(in Guid guidKey, out nint ppwszValue, out uint pcchLength);

    [PreserveSig] int GetBlobSize(in Guid guidKey, out uint pcbBlobSize);
    [PreserveSig] int GetBlob(in Guid guidKey, nint pBuf, uint cbBufSize, out uint pcbBlobSize);
    [PreserveSig] int GetAllocatedBlob(in Guid guidKey, out nint ppBuf, out uint pcbSize);

    [PreserveSig] int GetUnknown(in Guid guidKey, in Guid riid, out nint ppv);

    [PreserveSig] int SetItem(in Guid guidKey, nint value);
    [PreserveSig] int DeleteItem(in Guid guidKey);
    [PreserveSig] int DeleteAllItems();

    [PreserveSig] int SetUINT32(in Guid guidKey, uint unValue);
    [PreserveSig] int SetUINT64(in Guid guidKey, ulong unValue);
    [PreserveSig] int SetDouble(in Guid guidKey, double fValue);
    [PreserveSig] int SetGUID(in Guid guidKey, in Guid guidValue);

    [PreserveSig] int SetString(in Guid guidKey, nint wszValue);
    [PreserveSig] int SetBlob(in Guid guidKey, nint pBuf, uint cbBufSize);
    [PreserveSig] int SetUnknown(in Guid guidKey, nint pUnknown);

    [PreserveSig] int LockStore();
    [PreserveSig] int UnlockStore();

    [PreserveSig] int GetCount(out uint pcItems);
    [PreserveSig] int GetItemByIndex(uint unIndex, out Guid pguidKey, nint pValue);
    [PreserveSig] int CopyAllItems(nint pDest);

    // ---- IMFMediaType:s egna metoder ----

    /// <summary>Hämtar major type-attributet (video/audio/etc).</summary>
    [PreserveSig] int GetMajorType(out Guid pguidMajorType);

    /// <summary>Returnerar S_OK om media type är "compressed" (t.ex. H.264), S_FALSE om uncompressed.</summary>
    [PreserveSig] int IsCompressedFormat(out int pfCompressed);

    /// <summary>Jämför detta media type med ett annat. flags styr vad som jämförs.</summary>
    [PreserveSig] int IsEqual(nint pIMediaType, out uint pdwFlags);

    /// <summary>Hämtar en standardrepresentation av media type:n (sällan använt).</summary>
    [PreserveSig] int GetRepresentation(in Guid guidRepresentation, out nint ppvRepresentation);

    /// <summary>Frigör en representation som returnerats från GetRepresentation.</summary>
    [PreserveSig] int FreeRepresentation(in Guid guidRepresentation, nint pvRepresentation);
}
