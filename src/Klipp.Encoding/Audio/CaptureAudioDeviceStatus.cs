namespace Klipp.Encoding.Audio;

public enum CaptureAudioSource { System, Microphone, Discord, Music }
public enum CaptureAudioConnectionState { Connected, Reconnecting }

/// <summary>Immutable endpoint state; a missing source is recorded as silence until it reconnects.</summary>
public sealed record CaptureAudioDeviceStatus(
    CaptureAudioSource Source,
    CaptureAudioConnectionState State,
    string DeviceId,
    string DeviceName,
    string Message);

public sealed record CaptureAudioLevel(CaptureAudioSource Source, double Peak, bool IsClipping, bool LimiterActive);

internal sealed record AudioCaptureEndpointInfo(string Id, string Name);

internal interface IAudioCaptureEndpointFactory : IDisposable
{
    event Action? DevicesChanged;
    AudioCaptureEndpointInfo Resolve(string configuredId, CaptureAudioSource source);
    IAudioCaptureEndpoint Open(AudioCaptureEndpointInfo device, CaptureAudioSource source);
}

internal interface IAudioCaptureEndpoint : NAudio.Wave.ISampleProvider, IDisposable
{
    string DeviceId { get; }
    string DeviceName { get; }
    event Action? DataReceived;
    event Action<Exception?>? Stopped;
    void Start();
    void Stop();
    void FinishCapture();
}
