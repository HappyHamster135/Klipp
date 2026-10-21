using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace Klipp.Encoding.Audio;

/// <summary>Windows process-tree loopback, following Microsoft's ApplicationLoopback WASAPI sample.</summary>
internal static class ProcessLoopbackActivation
{
    internal static AudioClient Activate(int processId)
    {
        if (!ProcessAudioCaptureSupport.IsSupported) throw new PlatformNotSupportedException(ProcessAudioCaptureSupport.Description);
        var initialized = CoInitializeEx(0, 0); // Native async activation requires an initialized MTA.
        Marshal.ThrowExceptionForHR(initialized);
        // AUDIOCLIENT_ACTIVATION_PARAMS: ActivationType, TargetProcessId,
        // PROCESS_LOOPBACK_MODE_INCLUDE_TARGET_PROCESS_TREE (zero).
        var activation = Marshal.AllocHGlobal(12);
        var variant = Marshal.AllocHGlobal(24);
        IActivateOperation? operation = null;
        var completion = new Completion(activation, variant);
        try
        {
            for (var index = 0; index < 24; index++) Marshal.WriteByte(variant, index, 0);
            Marshal.WriteInt32(activation, 0, 1);
            Marshal.WriteInt32(activation, 4, processId);
            Marshal.WriteInt32(activation, 8, 0);
            Marshal.WriteInt16(variant, 0, 65); // VT_BLOB
            Marshal.WriteInt32(variant, 8, 12);
            Marshal.WriteIntPtr(variant, 16, activation);
            var iid = typeof(IAudioClient).GUID;
            Marshal.ThrowExceptionForHR(ActivateAudioInterfaceAsync("VAD\\Process_Loopback", ref iid, variant, completion, out operation));
            // Activation never depends on a UI message pump. Stop waits for this
            // bounded open; a late callback owns/releases any result itself.
            if (!completion.Finished.Wait(TimeSpan.FromSeconds(5)))
            {
                completion.Abandon();
                throw new TimeoutException("The app audio interface did not become ready. Retrying automatically.");
            }
            Marshal.ThrowExceptionForHR(completion.Result);
            return new AudioClient((IAudioClient)(completion.Client ?? throw new InvalidOperationException("App audio activation returned no client.")));
        }
        finally
        {
            if (operation is not null && Marshal.IsComObject(operation)) Marshal.ReleaseComObject(operation);
            // The OS may complete after a bounded timeout. Keep its parameter
            // memory alive until that callback, rather than freeing in-flight input.
            if (operation is null || completion.Finished.IsSet) completion.ReleaseParameters();
            GC.KeepAlive(completion);
            CoUninitialize();
        }
    }

    [DllImport("Mmdevapi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int ActivateAudioInterfaceAsync(string deviceInterfacePath, ref Guid iid, nint activationParams,
        ICompletionHandler completionHandler, out IActivateOperation operation);
    [DllImport("ole32.dll")] private static extern int CoInitializeEx(nint reserved, uint flags);
    [DllImport("ole32.dll")] private static extern void CoUninitialize();

    [ComImport, Guid("72A22D78-CDE4-431D-B8CC-843A71199B6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IActivateOperation
    {
        [PreserveSig] int GetActivateResult(out int result, [MarshalAs(UnmanagedType.IUnknown)] out object client);
    }

    [ComImport, Guid("41D949AB-9862-444A-80F6-C261334DA5EB"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface ICompletionHandler { [PreserveSig] int ActivateCompleted(IActivateOperation operation); }

    [ComImport, Guid("94EA2B94-E9CC-49E0-C0FF-EE64CA8F5B90"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IAgileObject { }

    [ComVisible(true), ClassInterface(ClassInterfaceType.None)]
    private sealed class Completion(nint activation, nint variant) : ICompletionHandler, IAgileObject
    {
        private readonly object _gate = new();
        private bool _abandoned;
        internal ManualResetEventSlim Finished { get; } = new();
        internal int Result;
        internal object? Client;
        private nint _activation = activation;
        private nint _variant = variant;
        internal void ReleaseParameters()
        {
            var parameters = Interlocked.Exchange(ref _activation, 0);
            var value = Interlocked.Exchange(ref _variant, 0);
            if (parameters != 0) Marshal.FreeHGlobal(parameters);
            if (value != 0) Marshal.FreeHGlobal(value);
        }
        public int ActivateCompleted(IActivateOperation operation)
        {
            lock (_gate)
            {
                try
                {
                    Result = operation.GetActivateResult(out var result, out var client);
                    if (Result >= 0) Result = result;
                    if (_abandoned || Result < 0) { if (client is not null && Marshal.IsComObject(client)) Marshal.ReleaseComObject(client); }
                    else Client = client;
                }
                catch (Exception exception) { Result = exception.HResult; }
                finally { ReleaseParameters(); Finished.Set(); }
            }
            return 0;
        }
        internal void Abandon()
        {
            lock (_gate)
            {
                _abandoned = true;
                if (Client is not null && Marshal.IsComObject(Client)) Marshal.ReleaseComObject(Client);
                Client = null;
            }
        }
    }
}
