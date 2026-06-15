using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using System.Runtime.CompilerServices;
using Klipp.Core.Abstractions;
using Klipp.Core.Models;
using Klipp.Encoding.Interop;
using Klipp.Encoding.Interop.Com;

namespace Klipp.Encoding.Video;

/// <summary>
/// H.264 video-encoder som använder Media Foundation:s MFT för hårdvaru-accelererad encoding.
/// </summary>
/// <remarks>
/// Byggplan:
///   Fas 1 — Discovery + Activation              ✅ KLAR
///   Fas 2 — Media type configuration            ✅ KLAR (denna session)
///   Fas 3 — BGRA → NV12 color conversion        ⬜
///   Fas 4 — Encode loop (ProcessInput/Output)   ⬜
///   Fas 5 — MP4 muxing                          ⬜
/// </remarks>
public sealed class MediaFoundationH264Encoder : IVideoEncoder
{
    private static readonly StrategyBasedComWrappers _comWrappers = new();

    private static readonly Guid MftTransformClsidAttribute =
        new("6821C42B-65A4-4E82-99BC-9A88205ECD0C");
    private static readonly Guid MftFriendlyNameAttribute =
        new("314FFBAE-5B41-4C95-9C19-4E7D586FACE3");
    private static readonly Guid MftEnumHardwareUrlAttribute =
        new("2FB866AC-B078-4942-AB6C-003D05CDA674");

    private IMFTransform? _transform;
    private RecordingSettings? _settings;
    private string? _selectedEncoderName;
    private bool _isHardware;
    private bool _initialized;
    private bool _disposed;

    public string? SelectedEncoderName => _selectedEncoderName;
    public bool IsHardwareAccelerated => _isHardware;

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
                "Ingen H.264-encoder hittades på systemet. Klipp kräver att Media Foundation " +
                "H.264-encoders är registrerade. Installera Media Feature Pack från Windows " +
                "Optional Features om de saknas.");

        try
        {
            _transform = ActivateTransform(activate);
        }
        finally
        {
            (activate as IDisposable)?.Dispose();
        }

        // Fas 2: Media type configuration
        // VIKTIGT: Output type måste sättas FÖRE input type. Encodern måste veta
        // vad den ska producera innan den kan validera vad den kan ta emot.
        ConfigureOutputType();
        ConfigureInputType();

        _initialized = true;
        return Task.CompletedTask;
    }

    /// <summary>
    /// Skapar och sätter output media type: H.264 med vår upplösning, bitrate, framerate.
    /// </summary>
    private void ConfigureOutputType()
    {
        var settings = _settings!;
        var outputType = CreateMediaType();

        try
        {
            // Major type: Video
            ThrowIfFailed(outputType.SetGUID(MfGuids.MtMajorType, MfGuids.MediaTypeVideo),
                "SetGUID(MAJOR_TYPE)");

            // Subtype: H.264
            ThrowIfFailed(outputType.SetGUID(MfGuids.MtSubType, MfGuids.VideoFormatH264),
                "SetGUID(SUBTYPE = H264)");

            // Bitrate (bits per sekund)
            ThrowIfFailed(outputType.SetUINT32(MfGuids.MtAvgBitrate, (uint)settings.VideoBitrate),
                "SetUINT32(AVG_BITRATE)");

            // Frame size: packed UINT64 med width (hög 32) + height (låg 32)
            var frameSize = PackUInt64((uint)settings.Width, (uint)settings.Height);
            ThrowIfFailed(outputType.SetUINT64(MfGuids.MtFrameSize, frameSize),
                "SetUINT64(FRAME_SIZE)");

            // Frame rate: packed UINT64 med numerator + denominator
            var frameRate = PackUInt64((uint)settings.FrameRate, 1);
            ThrowIfFailed(outputType.SetUINT64(MfGuids.MtFrameRate, frameRate),
                "SetUINT64(FRAME_RATE)");

            // Pixel aspect ratio 1:1 (kvadratiska pixlar)
            var pixelAspect = PackUInt64(1, 1);
            ThrowIfFailed(outputType.SetUINT64(MfGuids.MtPixelAspectRatio, pixelAspect),
                "SetUINT64(PIXEL_ASPECT_RATIO)");

            // Interlace mode: Progressive (= 2)
            ThrowIfFailed(outputType.SetUINT32(MfGuids.MtInterlaceMode,
                (uint)MfConstants.VideoInterlaceMode.Progressive),
                "SetUINT32(INTERLACE_MODE)");

            // Sätt på encodern
            ThrowIfFailed(_transform!.SetOutputType(0, outputType, 0),
                "SetOutputType(0, outputType, 0)");
        }
        finally
        {
            (outputType as IDisposable)?.Dispose();
        }
    }

    /// <summary>
    /// Skapar och sätter input media type: NV12 med samma dimensions och framerate.
    /// </summary>
    /// <remarks>
    /// NV12 är encoderns föredragna input-format på Windows. Det är en YUV-variant
    /// där Y-planet ligger först, följt av interleaved UV-plan i halv upplösning.
    /// Vi måste konvertera BGRA → NV12 i Fas 3.
    /// </remarks>
    private void ConfigureInputType()
    {
        var settings = _settings!;
        var inputType = CreateMediaType();

        try
        {
            ThrowIfFailed(inputType.SetGUID(MfGuids.MtMajorType, MfGuids.MediaTypeVideo),
                "input SetGUID(MAJOR_TYPE)");

            ThrowIfFailed(inputType.SetGUID(MfGuids.MtSubType, MfGuids.VideoFormatNv12),
                "input SetGUID(SUBTYPE = NV12)");

            var frameSize = PackUInt64((uint)settings.Width, (uint)settings.Height);
            ThrowIfFailed(inputType.SetUINT64(MfGuids.MtFrameSize, frameSize),
                "input SetUINT64(FRAME_SIZE)");

            var frameRate = PackUInt64((uint)settings.FrameRate, 1);
            ThrowIfFailed(inputType.SetUINT64(MfGuids.MtFrameRate, frameRate),
                "input SetUINT64(FRAME_RATE)");

            var pixelAspect = PackUInt64(1, 1);
            ThrowIfFailed(inputType.SetUINT64(MfGuids.MtPixelAspectRatio, pixelAspect),
                "input SetUINT64(PIXEL_ASPECT_RATIO)");

            ThrowIfFailed(inputType.SetUINT32(MfGuids.MtInterlaceMode,
                (uint)MfConstants.VideoInterlaceMode.Progressive),
                "input SetUINT32(INTERLACE_MODE)");

            ThrowIfFailed(_transform!.SetInputType(0, inputType, 0),
                "SetInputType(0, inputType, 0)");
        }
        finally
        {
            (inputType as IDisposable)?.Dispose();
        }
    }

    /// <summary>
    /// Skapar en tom IMFMediaType via MFCreateMediaType P/Invoke.
    /// Källans UniqueComInterfaceMarshaller hanterar COM-marshalling automatiskt.
    /// </summary>
    private static IMFMediaType CreateMediaType()
    {
        var hr = MfApi.MFCreateMediaType(out var mediaType);
        if (hr < 0 || mediaType is null)
            throw new InvalidOperationException($"MFCreateMediaType misslyckades: 0x{hr:X8}");

        return mediaType;
    }

    /// <summary>
    /// Packar två UINT32-värden till en UINT64. Används för MF_MT_FRAME_SIZE och MF_MT_FRAME_RATE
    /// där MF förväntar sig packed-format.
    /// </summary>
    private static ulong PackUInt64(uint high, uint low) => ((ulong)high << 32) | low;

    /// <summary>
    /// Hjälpare som kastar en exception om HRESULT är ett fel-värde.
    /// Inkluderar operationens namn i felmeddelandet för enklare debugging.
    /// </summary>
    private static void ThrowIfFailed(int hr, string operation)
    {
        if (hr < 0)
            throw new InvalidOperationException($"{operation} misslyckades: 0x{hr:X8}");
    }

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
            hr = MfApi.MFTEnumEx(categoryPtr, flags, nint.Zero, outputInfoPtr,
                                 out activatesArrayPtr, out count);
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

                    if (bestEncoder is null || (isHardware && !bestIsHardware))
                    {
                        (bestEncoder as IDisposable)?.Dispose();
                        bestEncoder = activate;
                        bestIsHardware = isHardware;
                        bestName = name;
                        keepThis = true;
                    }
                }
                finally
                {
                    if (!keepThis) Marshal.Release(activatePtr);
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

    private static IMFTransform ActivateTransform(IMFActivate activate)
    {
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

    public IAsyncEnumerable<EncodedSample> EncodeAsync(VideoFrame frame, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException("Fas 4 — kommer i nästa session.");
    }

    public IAsyncEnumerable<EncodedSample> FlushAsync(CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException("Fas 4 — kommer i nästa session.");
    }

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
