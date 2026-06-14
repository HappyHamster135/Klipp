using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace Klipp.Encoding.Interop.Com;

/// <summary>
/// IMFTransform — ett dataflöde med input/output streams. För en H.264-encoder
/// betyder det: input = raw video (NV12 frames), output = komprimerade samples (H.264).
/// </summary>
/// <remarks>
/// Konfigurations-pattern:
///   1. SetOutputType (encoder behöver veta target först)
///   2. SetInputType
///   3. ProcessMessage(NotifyBeginStreaming)
///   4. Loop: ProcessInput → ProcessOutput
///   5. ProcessMessage(CommandDrain) + ProcessOutput tills tom
///   6. ProcessMessage(NotifyEndStreaming)
///
/// IMFTransform ärver INTE från IMFAttributes — den är ett "rent" interface som börjar
/// efter IUnknown:s tre metoder.
/// </remarks>
[GeneratedComInterface]
[Guid("BF94C121-5B05-4E6F-8000-BA598961414D")]
internal partial interface IMFTransform
{
    /// <summary>Hämtar tillåtna intervall för stream-IDs (sällan använt för encoders).</summary>
    [PreserveSig] int GetStreamLimits(out uint pdwInputMinimum, out uint pdwInputMaximum,
                                       out uint pdwOutputMinimum, out uint pdwOutputMaximum);

    /// <summary>Hämtar aktuellt antal input/output streams (typiskt 1/1 för encoders).</summary>
    [PreserveSig] int GetStreamCount(out uint pcInputStreams, out uint pcOutputStreams);

    /// <summary>Hämtar stream-IDs (sällan använt — använd index 0 direkt).</summary>
    [PreserveSig] int GetStreamIDs(uint dwInputIDArraySize, nint pdwInputIDs,
                                    uint dwOutputIDArraySize, nint pdwOutputIDs);

    /// <summary>Hämtar info om en input stream (buffer-storlek, latency, etc).</summary>
    [PreserveSig] int GetInputStreamInfo(uint dwInputStreamID, out MfStructs.MftInputStreamInfo pStreamInfo);

    /// <summary>Hämtar info om en output stream (viktigt — vi använder CbSize för output buffer-allokering).</summary>
    [PreserveSig] int GetOutputStreamInfo(uint dwOutputStreamID, out MfStructs.MftOutputStreamInfo pStreamInfo);

    /// <summary>Hämtar globala attribut för transformer:n.</summary>
    [PreserveSig] int GetAttributes(out nint pAttributes);

    /// <summary>Hämtar attribut för en specifik input stream.</summary>
    [PreserveSig] int GetInputStreamAttributes(uint dwInputStreamID, out nint pAttributes);

    /// <summary>Hämtar attribut för en specifik output stream.</summary>
    [PreserveSig] int GetOutputStreamAttributes(uint dwOutputStreamID, out nint pAttributes);

    /// <summary>Tar bort en input stream (sällan använt).</summary>
    [PreserveSig] int DeleteInputStream(uint dwStreamID);

    /// <summary>Lägger till input streams (sällan använt).</summary>
    [PreserveSig] int AddInputStreams(uint cStreams, nint adwStreamIDs);

    /// <summary>Listar möjliga input types för en stream (för auto-konfiguration).</summary>
    [PreserveSig] int GetInputAvailableType(uint dwInputStreamID, uint dwTypeIndex, out nint ppType);

    /// <summary>Listar möjliga output types för en stream.</summary>
    [PreserveSig] int GetOutputAvailableType(uint dwOutputStreamID, uint dwTypeIndex, out nint ppType);

    /// <summary>Sätter input media type (NV12 för vår encoder).</summary>
    [PreserveSig] int SetInputType(uint dwInputStreamID,
                                    [MarshalAs(UnmanagedType.Interface)] IMFMediaType pType,
                                    uint dwFlags);

    /// <summary>Sätter output media type (H.264 med bitrate för vår encoder).</summary>
    [PreserveSig] int SetOutputType(uint dwOutputStreamID,
                                     [MarshalAs(UnmanagedType.Interface)] IMFMediaType pType,
                                     uint dwFlags);

    /// <summary>Hämtar nuvarande input media type.</summary>
    [PreserveSig] int GetInputCurrentType(uint dwInputStreamID, out nint ppType);

    /// <summary>Hämtar nuvarande output media type.</summary>
    [PreserveSig] int GetOutputCurrentType(uint dwOutputStreamID, out nint ppType);

    /// <summary>Returnerar status för input stream (är den redo att ta emot mer data?).</summary>
    [PreserveSig] int GetInputStatus(uint dwInputStreamID, out uint pdwFlags);

    /// <summary>Returnerar status för output stream.</summary>
    [PreserveSig] int GetOutputStatus(out uint pdwFlags);

    /// <summary>Sätter tidsgränser för output (sällan använt).</summary>
    [PreserveSig] int SetOutputBounds(long hnsLowerBound, long hnsUpperBound);

    /// <summary>Processar ett MFT-event (sällan använt).</summary>
    [PreserveSig] int ProcessEvent(uint dwInputStreamID, nint pEvent);

    /// <summary>
    /// Skickar ett meddelande till transformer:n.
    /// Använd MftMessageType-enum från MfConstants för värdet på eMessage.
    /// </summary>
    [PreserveSig] int ProcessMessage(MfConstants.MftMessageType eMessage, nint ulParam);

    /// <summary>Matar in ett sample (en frame) i transformer:n.</summary>
    [PreserveSig] int ProcessInput(uint dwInputStreamID,
                                    [MarshalAs(UnmanagedType.Interface)] IMFSample pSample,
                                    uint dwFlags);

    /// <summary>
    /// Försöker hämta ut komprimerade samples från transformer:n.
    /// Returnerar MF_E_TRANSFORM_NEED_MORE_INPUT om vi måste mata in fler frames först.
    /// </summary>
    [PreserveSig] int ProcessOutput(uint dwFlags, uint cOutputBufferCount,
                                     ref MfStructs.MftOutputDataBuffer pOutputSamples,
                                     out uint pdwStatus);
}
