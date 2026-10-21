using NAudio.CoreAudioApi;

namespace Klipp.Encoding.Audio;

/// <summary>Explicit endpoints reconnect by ID; an empty ID follows Windows default device changes.</summary>
public sealed record CaptureAudioOptions
{
    public bool CaptureSystemAudio { get; init; } = true;
    public string SystemAudioDeviceId { get; init; } = string.Empty;
    public bool CaptureMicrophone { get; init; }
    public string MicrophoneDeviceId { get; init; } = string.Empty;
    public double SystemAudioGain { get; init; } = 1;
    public double MicrophoneGain { get; init; } = 1;
    public string AudioCaptureMode { get; init; } = "desktop";
    public int GameAudioProcessId { get; init; }
    public long GameAudioProcessStartTicks { get; init; }
    public string GameAudioProcessName { get; init; } = string.Empty;
    public bool CaptureDiscordAudio { get; init; }
    public string DiscordProcessName { get; init; } = "Discord";
    public double DiscordAudioGain { get; init; } = 1;
    public bool CaptureMusicAudio { get; init; }
    public string MusicProcessName { get; init; } = string.Empty;
    public double MusicAudioGain { get; init; } = 1;
    public bool AudioLimiterEnabled { get; init; } = true;
    public bool IsEnabled => TrackNames.Count > 0;
    // Two independent stereo pairs travel through one clock/pipe. They are split
    // into named MP4 audio streams by the encoder, never saved as surround sound.
    public int OutputChannels => Math.Max(1, TrackNames.Count) * 2;
    public IReadOnlyList<string> TrackNames
    {
        get
        {
            var tracks = new List<string>(4);
            if (CaptureSystemAudio) tracks.Add(CaptureAudioLayout.SystemName);
            if (CaptureMicrophone) tracks.Add(CaptureAudioLayout.MicrophoneName);
            if (CaptureDiscordAudio && AudioCaptureMode == "game") tracks.Add(CaptureAudioLayout.DiscordName);
            if (CaptureMusicAudio && AudioCaptureMode == "game") tracks.Add(CaptureAudioLayout.MusicName);
            return tracks;
        }
    }
    public string AudioTrackName => CaptureMicrophone && !CaptureSystemAudio ? CaptureAudioLayout.MicrophoneName : CaptureAudioLayout.SystemName;

    internal static float SafeGain(double value) => (float)Math.Clamp(double.IsFinite(value) ? value : 1, 0, 2);

    /// <summary>Returns an owned endpoint. An explicit unavailable choice never falls back to another device.</summary>
    public static MMDevice ResolveDevice(string? deviceId, DataFlow flow)
    {
        var id = deviceId?.Trim() ?? string.Empty;
        var label = flow == DataFlow.Capture ? "microphone" : "system audio device";
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            var device = id.Length == 0
                ? enumerator.GetDefaultAudioEndpoint(flow, flow == DataFlow.Capture ? Role.Communications : Role.Console)
                : enumerator.GetDevice(id);
            if (device.State == DeviceState.Active && device.DataFlow == flow) return device;
            device.Dispose();
            throw new InvalidOperationException($"The selected {label} is unavailable. Choose another device in Settings > Audio.");
        }
        catch (Exception exception) when (exception is not InvalidOperationException)
        {
            throw new InvalidOperationException(id.Length == 0
                ? $"No default {label} is available. Connect a device or turn off this audio source in Settings > Audio."
                : $"The selected {label} is unavailable. Choose another device in Settings > Audio.", exception);
        }
    }
}
