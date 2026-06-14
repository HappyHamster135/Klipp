using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace Klipp.Encoding.Interop.Com;

/// <summary>
/// IMFSample — ett media-sample (typiskt en video-frame eller ett audio-block).
/// Bär en eller flera buffers, en tidsstämpel, en längd, och attribut (via IMFAttributes).
/// </summary>
/// <remarks>
/// Ärver från IMFAttributes — alla 32 IMFAttributes-metoder måste upprepas först
/// i exakt samma ordning så vtable-positionerna stämmer.
/// </remarks>
[GeneratedComInterface]
[Guid("c40a00f2-b93a-4d80-ae8c-5a1c634f58e4")]
internal partial interface IMFSample
{
    // ---- Ärvt från IMFAttributes (32 metoder) ----
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

    // ---- IMFSample:s egna metoder ----

    /// <summary>Hämtar flaggor om samplet (oftast 0).</summary>
    [PreserveSig] int GetSampleFlags(out uint pdwSampleFlags);

    /// <summary>Sätter sample-flaggor.</summary>
    [PreserveSig] int SetSampleFlags(uint dwSampleFlags);

    /// <summary>Hämtar tidsstämpel i 100-ns enheter.</summary>
    [PreserveSig] int GetSampleTime(out long phnsSampleTime);

    /// <summary>Sätter tidsstämpel i 100-ns enheter.</summary>
    [PreserveSig] int SetSampleTime(long hnsSampleTime);

    /// <summary>Hämtar längd i 100-ns enheter.</summary>
    [PreserveSig] int GetSampleDuration(out long phnsSampleDuration);

    /// <summary>Sätter längd i 100-ns enheter.</summary>
    [PreserveSig] int SetSampleDuration(long hnsSampleDuration);

    /// <summary>Hämtar antal buffers i samplet (oftast 1).</summary>
    [PreserveSig] int GetBufferCount(out uint pdwBufferCount);

    /// <summary>Hämtar en specifik buffer.</summary>
    [PreserveSig] int GetBufferByIndex(uint dwIndex, out IMFMediaBuffer ppBuffer);

    /// <summary>
    /// Hämtar alla buffers konkatenerade som en enda buffer.
    /// Användbart när encodern returnerar flera buffers per output sample.
    /// </summary>
    [PreserveSig] int ConvertToContiguousBuffer(out IMFMediaBuffer ppBuffer);

    /// <summary>Lägger till en buffer i samplet.</summary>
    [PreserveSig] int AddBuffer([MarshalAs(UnmanagedType.Interface)] IMFMediaBuffer pBuffer);

    /// <summary>Tar bort en buffer från samplet.</summary>
    [PreserveSig] int RemoveBufferByIndex(uint dwIndex);

    /// <summary>Tar bort alla buffers från samplet.</summary>
    [PreserveSig] int RemoveAllBuffers();

    /// <summary>Hämtar total längd av all data i alla buffers tillsammans.</summary>
    [PreserveSig] int GetTotalLength(out uint pcbTotalLength);

    /// <summary>Kopierar all data till en byte-buffer (sällan använt — vi använder GetBufferByIndex).</summary>
    [PreserveSig] int CopyToBuffer([MarshalAs(UnmanagedType.Interface)] IMFMediaBuffer pBuffer);
}
