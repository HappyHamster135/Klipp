using System;
using Klipp.Capture.Models;

namespace Klipp.Desktop.Services;

public sealed record class CaptureOptions
{
    public int FrameRate { get; init; } = 24;
    public string ResolutionMode { get; init; } = "720p";
    public int BitrateMbps { get; init; } = 8;
    public bool HideCaptureBorder { get; init; } = true;
    public CaptureTarget? Target { get; init; }
    public bool CaptureSystemAudio { get; init; } = true;
    public string SystemAudioDeviceId { get; init; } = string.Empty;
    public bool CaptureMicrophone { get; init; }
    public string MicrophoneDeviceId { get; init; } = string.Empty;
    public double SystemAudioGain { get; init; } = 1;
    public double MicrophoneGain { get; init; } = 1;
    public string EncoderMode { get; init; } = "auto";
    public string AudioCaptureMode { get; init; } = "desktop";
    public int GameAudioProcessId { get; init; }
    public string GameAudioProcessName { get; init; } = string.Empty;
    public bool CaptureDiscordAudio { get; init; }
    public string DiscordProcessName { get; init; } = "Discord";
    public double DiscordAudioGain { get; init; } = 1;
    public bool CaptureMusicAudio { get; init; }
    public string MusicProcessName { get; init; } = string.Empty;
    public double MusicAudioGain { get; init; } = 1;
    public bool AudioLimiterEnabled { get; init; } = true;
    public bool CaptureCursor { get; init; } = true;
    public bool EnableExclusiveGameCapture { get; init; }

    public int VideoBitrate => BitrateMbps * 1_000_000;

    public Klipp.Encoding.Audio.CaptureAudioOptions CreateAudioOptions(CaptureTarget? activeTarget = null) => new()
    {
        CaptureSystemAudio = CaptureSystemAudio, SystemAudioDeviceId = SystemAudioDeviceId,
        CaptureMicrophone = CaptureMicrophone, MicrophoneDeviceId = MicrophoneDeviceId,
        SystemAudioGain = SystemAudioGain, MicrophoneGain = MicrophoneGain,
        AudioCaptureMode = AudioCaptureMode == "game" ? "game" : "desktop",
        GameAudioProcessId = GameAudioProcessId > 0 ? GameAudioProcessId :
            string.IsNullOrWhiteSpace(GameAudioProcessName) && (activeTarget ?? Target) is { Kind: CaptureTargetKind.Window } window
                ? (int)window.ProcessId : 0,
        GameAudioProcessName = GameAudioProcessName,
        GameAudioProcessStartTicks = GetGameProcessStartTicks(activeTarget ?? Target),
        CaptureDiscordAudio = CaptureDiscordAudio, DiscordProcessName = DiscordProcessName, DiscordAudioGain = DiscordAudioGain,
        CaptureMusicAudio = CaptureMusicAudio, MusicProcessName = MusicProcessName, MusicAudioGain = MusicAudioGain,
        AudioLimiterEnabled = AudioLimiterEnabled
    };

    private long GetGameProcessStartTicks(CaptureTarget? target)
    {
        if (!string.IsNullOrWhiteSpace(GameAudioProcessName)) return 0;
        try
        {
            if (GameAudioProcessId <= 0 && target is { Kind: CaptureTargetKind.Window, ProcessCreated: > 0 })
                return DateTime.FromFileTimeUtc(target.ProcessCreated).Ticks;
            var pid = GameAudioProcessId > 0 ? GameAudioProcessId : (int)(target?.ProcessId ?? 0);
            if (pid <= 0) return 0;
            using var process = System.Diagnostics.Process.GetProcessById(pid);
            return process.StartTime.ToUniversalTime().Ticks;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { return 0; }
    }

    public (int Width, int Height) GetOutputSize(int sourceWidth, int sourceHeight)
    {
        var maxHeight = ResolutionMode switch
        {
            "540p" => 540,
            "720p" => 720,
            "1080p" => 1080,
            _ => sourceHeight
        };

        if (maxHeight >= sourceHeight)
            return (MakeEven(sourceWidth), MakeEven(sourceHeight));

        var scale = maxHeight / (double)sourceHeight;
        var width = MakeEven((int)Math.Round(sourceWidth * scale));
        var height = MakeEven(maxHeight);

        return (Math.Max(2, width), Math.Max(2, height));
    }

    private static int MakeEven(int value) => value % 2 == 0 ? value : value - 1;
}
