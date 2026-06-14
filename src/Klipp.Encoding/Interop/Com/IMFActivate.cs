using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace Klipp.Encoding.Interop.Com;

/// <summary>
/// IMFActivate — en factory för att skapa MF-objekt (typiskt en MFT/encoder) på begäran.
/// MFTEnumEx returnerar en array av dessa istället för faktiska encoders, eftersom
/// encoder-skapande är dyrt (allokerar GPU-resurser etc).
/// </summary>
/// <remarks>
/// Ärver från IMFAttributes — alla 32 attribute-metoder upprepas först i vtable.
/// Aktivatörens egna metoder är: ActivateObject, ShutdownObject, DetachObject.
/// </remarks>
[GeneratedComInterface]
[Guid("7FEE9E9A-4A89-47a6-899C-B6A53A70FB67")]
internal partial interface IMFActivate
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

    // ---- IMFActivate:s egna metoder ----

    /// <summary>
    /// Skapar och returnerar ett MF-objekt (oftast en IMFTransform/encoder).
    /// Detta är det dyra anropet — det allokerar faktiskt GPU-resurser.
    /// </summary>
    /// <param name="riid">IID för det interface vi vill ha tillbaka (typiskt IMFTransform).</param>
    /// <param name="ppv">Det skapade objektet (ut).</param>
    [PreserveSig] int ActivateObject(in Guid riid, out nint ppv);

    /// <summary>Stänger av objektet som ActivateObject skapade.</summary>
    [PreserveSig] int ShutdownObject();

    /// <summary>Detachar det aktiverade objektet utan att stänga av det.</summary>
    [PreserveSig] int DetachObject();
}
