using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace Klipp.Encoding.Interop.Com;

/// <summary>
/// IMFAttributes — basinterface för alla MF-objekt som bär nyckel/värde-attribut.
/// Nyckel är en Guid, värde kan vara UINT32, UINT64, double, string, Guid eller blob.
/// </summary>
/// <remarks>
/// Source-genererad COM-interop tillåter inte string- eller array-marshalling utan
/// extra metadata. Eftersom vi inte använder dessa metoder i encoder-koden exponerar
/// vi dem som rå <c>nint</c>-pointers. Det är OK — vi behöver bara att vtable-positionerna
/// stämmer, inte att vi kan anropa varenda metod ergonomiskt.
/// </remarks>
[GeneratedComInterface]
[Guid("2cd2d921-c447-44a7-a13c-4adabfc247e3")]
internal partial interface IMFAttributes
{
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
}
