using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using System.Runtime.CompilerServices;
using Klipp.Core.Abstractions;
using Klipp.Core.Models;
using Klipp.Encoding.Interop;
using Klipp.Encoding.Interop.Com;

namespace Klipp.Encoding.Video;

/// <summary>
/// H.264 video-encoder som använder Media Foundation:s MFT (Media Foundation Transform)
/// för hårdvaru-accelererad encoding.
/// </summary>
/// <remarks>
/// Implementationen följer Microsoft:s MFT-flöde:
///   1. Discovery — hitta tillgängliga H.264-encoders
///   2. Selection — välj bästa (hårdvara prioriteras)
///   3. Activation — skapa en IMFTransform-instans
///   4. Media types — konfigurera input (NV12) och output (H.264)
///   5. Encode loop — ProcessInput → ProcessOutput
///
/// För närvarande implementerar denna klass bara steg 1-3 (Fas 1 av byggplanen).
/// </remarks>
public sealed class MediaFoundationH264Encoder : IVideoEncoder
{
    private static readonly StrategyBasedComWrappers _comWrappers = new();

    // MFT_TRANSFORM_CLSID_Attribute — encoderns CLSID
    private static readonly Guid MftTransformClsidAttribute =
        new("6821C42B-65A4-4E82-99BC-9A88205ECD0C");

    // MFT_FRIENDLY_NAME_Attribute
    private static readonly Guid MftFriendlyNameAttribute =
        new("314FFBAE-5B41-4C95-9C19-4E7D586FACE3");

    // MFT_ENUM_HARDWARE_URL_Attribute — finns om encodern är hårdvarubaserad
    private static readonly Guid MftEnumHardwareUrlAttribute =
        new("2FB866AC-B078-4942-AB6C-003D05CDA674");

    private IMFTransform? _transform;
    private RecordingSettings? _settings;
    private string? _selectedEncoderName;
    private bool _isHardware;
    private bool _initialized;
    private bool _disposed;

    /// <summary>Namnet på den encoder som valts (efter Initialize). Null innan dess.</summary>
    public string? SelectedEncoderName => _selectedEncoderName;

    /// <summary>True om vald encoder är hårdvarubaserad. Undefined innan Initialize.</summary>
    public bool IsHardwareAccelerated => _isHardware;

    /// <inheritdoc/>
    public Task InitializeAsync(RecordingSettings settings, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_initialized) throw new InvalidOperationException("Encoder är redan initialiserad.");

        ArgumentNullException.ThrowIfNull(settings);
        settings.Validate();

        MediaFoundationStartup.EnsureInitialized();

        _settings = settings;

        // Fas 1: Discovery + Selection + Activation
        var activate = FindBestH264Encoder()
            ?? throw new InvalidOperationException(
                "Ingen H.264-encoder hittades på systemet. " +
                "Klipp kräver att Media Foundation H.264-encoders är registrerade. " +
                "Detta är standard på Windows 10/11 men kan saknas på vissa OEM-installationer. " +
                "Installera Media Feature Pack från Windows Optional Features.");

        try
        {
            _transform = ActivateTransform(activate);
        }
        finally
        {
            (activate as IDisposable)?.Dispose();
        }

        _initialized = true;
        return Task.CompletedTask;
    }

    /// <summary>
    /// Hittar bästa tillgängliga H.264-encoder. Prioriterar hårdvara över software.
    /// Returnerar IMFActivate (caller måste Release).
    /// </summary>
    private IMFActivate? FindBestH264Encoder()
    {
        var flags = (uint)MfConstants.MftEnumFlag.SortAndFilter;

        var categoryGuid = MfGuids.TransformCategoryVideoEncoder;
        var categoryPtr = Marshal.AllocHGlobal(Marshal.SizeOf<Guid>());
        Marshal.StructureToPtr(categoryGuid, categoryPtr, false);

        var outputInfo = new MfStructs.MftRegisterTypeInfo
        {
            GuidMajorType = MfGuids.MediaTypeVideo,
            GuidSubtype = MfGuids.VideoFormatH264
        };
        var outputInfoPtr = Marshal.AllocHGlobal(Marshal.SizeOf<MfStructs.MftRegisterTypeInfo>());
        Marshal.StructureToPtr(outputInfo, outputInfoPtr, false);

        int hr;
        nint activatesArrayPtr;
        uint count;
        try
        {
            hr = MfApi.MFTEnumEx(
                categoryPtr,
                flags,
                nint.Zero,
                outputInfoPtr,
                out activatesArrayPtr,
                out count);
        }
        finally
        {
            Marshal.FreeHGlobal(categoryPtr);
            Marshal.FreeHGlobal(outputInfoPtr);
        }

        if (hr < 0 || count == 0 || activatesArrayPtr == nint.Zero)
            return null;

        IMFActivate? bestEncoder = null;
        bool bestIsHardware = false;
        string? bestName = null;

        try
        {
            for (int i = 0; i < count; i++)
            {
                var activatePtr = Marshal.ReadIntPtr(activatesArrayPtr, i * nint.Size);
                if (activatePtr == nint.Zero) continue;

                bool keepThis = false;
                try
                {
                    var obj = _comWrappers.GetOrCreateObjectForComInstance(activatePtr, CreateObjectFlags.None);
                    if (obj is not IMFActivate activate) continue;

                    var isHardware = TryReadHasAttribute(activate, MftEnumHardwareUrlAttribute);
                    var name = TryReadString(activate, MftFriendlyNameAttribute) ?? "(unknown)";

                    // Föredra hårdvara över software. Om båda är hårdvara, behåll första.
                    // Föredra hårdvara över software. Om båda är hårdvara, behåll första.
                    if (bestEncoder is null || (isHardware && !bestIsHardware))
                    {
                        // Frigör tidigare valda om vi byter
                        (bestEncoder as IDisposable)?.Dispose();

                        bestEncoder = activate;
                        bestIsHardware = isHardware;
                        bestName = name;
                        keepThis = true;
                    }
                }
                finally
                {
                    if (!keepThis)
                        Marshal.Release(activatePtr);
                }
            }
        }
        finally
        {
            MfApi.CoTaskMemFree(activatesArrayPtr);
        }

        _selectedEncoderName = bestName;
        _isHardware = bestIsHardware;
        return bestEncoder;
    }

    /// <summary>
    /// Aktiverar en MFT-aktivator och returnerar IMFTransform-instansen.
    /// </summary>
    private static IMFTransform ActivateTransform(IMFActivate activate)
    {
        // IID för IMFTransform
        var iidTransform = new Guid("BF94C121-5B05-4E6F-8000-BA598961414D");

        var hr = activate.ActivateObject(iidTransform, out var transformPtr);
        if (hr < 0 || transformPtr == nint.Zero)
            throw new InvalidOperationException($"ActivateObject misslyckades: 0x{hr:X8}");

        try
        {
            var obj = _comWrappers.GetOrCreateObjectForComInstance(transformPtr, CreateObjectFlags.None);
            if (obj is not IMFTransform transform)
                throw new InvalidOperationException("Aktiverat objekt är inte IMFTransform.");

            return transform;
        }
        finally
        {
            Marshal.Release(transformPtr);
        }
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

    /// <inheritdoc/>
    public IAsyncEnumerable<EncodedSample> EncodeAsync(
        VideoFrame frame,
        CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException("Fas 4 — kommer i nästa session.");
    }

    /// <inheritdoc/>
    public IAsyncEnumerable<EncodedSample> FlushAsync(CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException("Fas 4 — kommer i nästa session.");
    }

    /// <inheritdoc/>
    public ValueTask DisposeAsync()
    {
        if (_disposed) return ValueTask.CompletedTask;
        _disposed = true;

        if (_transform is not null)
        {
            (_transform as IDisposable)?.Dispose();
            _transform = null;
        }

        return ValueTask.CompletedTask;
    }
}
