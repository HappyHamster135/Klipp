using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

using Klipp.Encoding.Interop;
using Klipp.Encoding.Interop.Com;

namespace Klipp.Encoding.Video;

public sealed record EncoderInfo(string Name, bool IsHardware);

public static class EncoderDiscovery
{
    private static readonly StrategyBasedComWrappers _comWrappers = new();

    // MFT_FRIENDLY_NAME_Attribute
    private static readonly Guid MftFriendlyNameAttribute =
        new("314FFBAE-5B41-4C95-9C19-4E7D586FACE3");

    // MFT_ENUM_HARDWARE_URL_Attribute
    private static readonly Guid MftEnumHardwareUrlAttribute =
        new("2FB866AC-B078-4942-AB6C-003D05CDA674");

    // MFT_TRANSFORM_CLSID_Attribute — den CLSID som ActivateObject skapar
    private static readonly Guid MftTransformClsidAttribute =
        new("6821C42B-65A4-4E82-99BC-9A88205ECD0C");

    public static IReadOnlyList<EncoderInfo> ListH264Encoders()
    {
        MediaFoundationStartup.EnsureInitialized();

        var flags = (uint)MfConstants.MftEnumFlag.SortAndFilter;

        var categoryGuid = MfGuids.TransformCategoryVideoEncoder;
        var categoryPtr = Marshal.AllocHGlobal(Marshal.SizeOf<Guid>());
        Marshal.StructureToPtr(categoryGuid, categoryPtr, false);

        int hr;
        nint activatesArrayPtr;
        uint count;
        try
        {
            hr = MfApi.MFTEnumEx(
                categoryPtr,
                flags,
                nint.Zero,
                nint.Zero,    // INGET output-filter — vi listar alla
                out activatesArrayPtr,
                out count);
        }
        finally
        {
            Marshal.FreeHGlobal(categoryPtr);
        }

        Console.WriteLine($"[debug] MFTEnumEx hr=0x{hr:X8} count={count}");

        if (hr < 0)
            throw new InvalidOperationException($"MFTEnumEx misslyckades: 0x{hr:X8}");

        if (count == 0 || activatesArrayPtr == nint.Zero)
            return Array.Empty<EncoderInfo>();

        try
        {
            return ReadActivatesArray(activatesArrayPtr, count);
        }
        finally
        {
            MfApi.CoTaskMemFree(activatesArrayPtr);
        }
    }

    private static List<EncoderInfo> ReadActivatesArray(nint arrayPtr, uint count)
    {
        var result = new List<EncoderInfo>((int)count);

        for (int i = 0; i < count; i++)
        {
            var activatePtr = Marshal.ReadIntPtr(arrayPtr, i * nint.Size);
            if (activatePtr == nint.Zero) continue;

            try
            {
                var obj = _comWrappers.GetOrCreateObjectForComInstance(activatePtr, CreateObjectFlags.None);
                if (obj is not IMFActivate activate) continue;

                var name = TryReadString(activate, MftFriendlyNameAttribute) ?? "(unknown)";
                var isHardware = TryReadHasAttribute(activate, MftEnumHardwareUrlAttribute);

                // DEBUG: skriv ut all info om denna MFT så vi ser vad som finns
                Console.WriteLine($"  [debug] {name}  hardware={isHardware}");

                result.Add(new EncoderInfo(name, isHardware));
            }
            finally
            {
                Marshal.Release(activatePtr);
            }
        }

        return result;
    }

    private static string? TryReadString(IMFActivate activate, Guid attributeKey)
    {
        var hr = activate.GetStringLength(attributeKey, out var length);
        if (hr < 0 || length == 0) return null;

        var bufferChars = length + 1;
        var buffer = Marshal.AllocHGlobal((int)bufferChars * sizeof(char));
        try
        {
            hr = activate.GetString(attributeKey, buffer, bufferChars, out _);
            if (hr < 0) return null;
            return Marshal.PtrToStringUni(buffer);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static bool TryReadHasAttribute(IMFActivate activate, Guid attributeKey)
    {
        var hr = activate.GetItemType(attributeKey, out _);
        return hr >= 0;
    }
}
