using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace Klipp.Encoding.Interop.Com;

/// <summary>
/// IMFMediaBuffer — ett block råa bytes som encodern kan läsa från eller skriva till.
/// Buffern måste låsas med Lock() innan vi kan komma åt minnet, och låsas upp med Unlock()
/// efteråt. Detta tillåter MF att flytta minnet (t.ex. mellan GPU och CPU) när det är olåst.
/// </summary>
[GeneratedComInterface]
[Guid("045FA593-8799-42b8-BC8D-8968C6453507")]
internal partial interface IMFMediaBuffer
{
    /// <summary>
    /// Låser buffern och ger oss en pekare till minnet.
    /// </summary>
    /// <param name="ppbBuffer">Pekare till buffer-minnet (ut).</param>
    /// <param name="pcbMaxLength">Maximal kapacitet i bytes (ut).</param>
    /// <param name="pcbCurrentLength">Hur mycket data som faktiskt finns just nu i bytes (ut).</param>
    [PreserveSig] int Lock(out nint ppbBuffer, out uint pcbMaxLength, out uint pcbCurrentLength);

    /// <summary>Låser upp buffern. Måste anropas efter Lock(), helst i ett finally-block.</summary>
    [PreserveSig] int Unlock();

    /// <summary>Hämtar hur mycket data som finns i buffern just nu.</summary>
    [PreserveSig] int GetCurrentLength(out uint pcbCurrentLength);

    /// <summary>Sätter hur mycket data som finns i buffern (efter att vi skrivit till den).</summary>
    [PreserveSig] int SetCurrentLength(uint cbCurrentLength);

    /// <summary>Hämtar maximal kapacitet i bytes.</summary>
    [PreserveSig] int GetMaxLength(out uint pcbMaxLength);
}
