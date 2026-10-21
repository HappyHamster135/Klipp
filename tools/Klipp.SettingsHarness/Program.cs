using System.Text.Json;
using Klipp.Desktop.Services;

var directory = Path.Combine(Path.GetTempPath(), "Klipp-Settings-QA", Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(directory);
var path = Path.Combine(directory, "settings.json");
var checks = 0;
void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
    checks++;
}

void CheckDefaults(KlippSettings value)
{
    Check(value.CaptureSourceMode == "auto" && value.AutoDetectGames, "Automatic game detection is not the default.");
    Check(value.CaptureSystemAudio && value.CaptureCursor, "Existing audio/cursor capture defaults changed.");
    Check(!value.EnableExclusiveGameCapture, "Exclusive game capture must be an explicit opt-in for new and legacy settings.");
    Check(!value.CaptureMicrophone && value.SystemAudioDeviceId == "" && value.MicrophoneDeviceId == "" &&
        value.SystemAudioGain == 1 && value.MicrophoneGain == 1, "Legacy audio devices or microphone defaults changed.");
    Check(value.NotifyOnClipSaved && value.NotifyOnRecordingStarted && value.NotifyOnRecordingStopped &&
        value.NotifyOnAutomaticSourceChange, "Capture notifications should default on.");
    Check(value.CaptureNotificationDurationSeconds == 4, "Default notification lifetime changed.");
    Check(value.CaptureEncoderMode == "auto", "GPU-preferred automatic capture encoding is not the default.");
    Check(value.AudioCaptureMode == "desktop" && value.GameAudioProcessName == "" &&
        value.DiscordProcessName == "Discord" && value.MusicProcessName == "",
        "Legacy audio capture must keep desktop routing without inventing selected game/music apps.");
    Check(!value.CaptureDiscordAudio && !value.CaptureMusicAudio && value.DiscordAudioGain == 1 && value.MusicAudioGain == 1,
        "Optional Discord/music sources or gains changed without user configuration.");
    Check(value.AudioLimiterEnabled && value.LowDiskProtectionEnabled,
        "Audio limiting and recording disk protection should default on.");
    Check(value.CaptureGameProcessNames.Count == 0 && value.CaptureMonitorId == "" &&
        value.CaptureWindowProcessName == "" && value.CaptureWindowTitle == "", "A source identity was invented.");
}

var service = new SettingsService(path);
await service.LoadAsync();
CheckDefaults(service.Current);
Check(!File.Exists(path), "Loading absent settings wrote a file.");

await File.WriteAllTextAsync(path, """
{"ClipLengthSeconds":60,"CaptureResolutionMode":"Native","CaptureFrameRate":60,"CaptureApplicationProfiles":null,"EditorShortcuts":null}
""");
await service.LoadAsync();
CheckDefaults(service.Current);
Check(service.Current.ClipLengthSeconds == 60 && service.Current.CaptureResolutionMode == "Native" &&
    service.Current.CaptureFrameRate == 60, "Loading legacy settings lost saved capture quality.");
Check(service.Current.CaptureApplicationProfiles.Count == 0 && service.Current.EditorShortcuts.Count > 0,
    "Legacy null collections were not normalized.");

service.Current.CaptureSourceMode = " WINDOW ";
service.Current.CaptureMonitorId = " \\.\\DISPLAY2 ";
service.Current.CaptureWindowProcessName = " GameClient ";
service.Current.CaptureWindowTitle = " My game ";
service.Current.CaptureGameProcessNames = [" GameClient ", "gameclient", "", null!, " AnotherGame "];
service.Current.AutoDetectGames = false;
service.Current.CaptureSystemAudio = false;
service.Current.SystemAudioDeviceId = " {render-endpoint} ";
service.Current.CaptureMicrophone = true;
service.Current.MicrophoneDeviceId = " {microphone-endpoint} ";
service.Current.SystemAudioGain = .72;
service.Current.MicrophoneGain = 1.25;
service.Current.CaptureCursor = false;
service.Current.EnableExclusiveGameCapture = true;
service.Current.NotifyOnClipSaved = false;
service.Current.NotifyOnRecordingStarted = false;
service.Current.NotifyOnRecordingStopped = false;
service.Current.NotifyOnAutomaticSourceChange = false;
service.Current.CaptureNotificationDurationSeconds = 9;
service.Current.CaptureEncoderMode = " NVENC ";
service.Current.AudioCaptureMode = " GAME ";
service.Current.GameAudioProcessName = " Game.Client.exe ";
service.Current.CaptureDiscordAudio = true;
service.Current.DiscordProcessName = " DiscordPTB.EXE ";
service.Current.DiscordAudioGain = .62;
service.Current.CaptureMusicAudio = true;
service.Current.MusicProcessName = " Music Player.exe ";
service.Current.MusicAudioGain = 1.18;
service.Current.AudioLimiterEnabled = false;
service.Current.LowDiskProtectionEnabled = false;
await service.SaveAsync();
var reloaded = new SettingsService(path);
await reloaded.LoadAsync();
Check(reloaded.Current.CaptureSourceMode == "window" && !reloaded.Current.AutoDetectGames,
    "Capture source preferences did not round-trip.");
Check(reloaded.Current.CaptureMonitorId == "\\.\\DISPLAY2" &&
    reloaded.Current.CaptureWindowProcessName == "GameClient" && reloaded.Current.CaptureWindowTitle == "My game",
    "Stable source identity did not round-trip.");
Check(reloaded.Current.CaptureGameProcessNames.SequenceEqual(["gameclient", "anothergame"]),
    "Remembered game process names were not trimmed, case-normalized and deduplicated.");
Check(!reloaded.Current.CaptureSystemAudio && !reloaded.Current.CaptureCursor,
    "Audio/cursor switches did not round-trip.");
Check(reloaded.Current.EnableExclusiveGameCapture, "Exclusive game capture opt-in did not round-trip.");
Check(reloaded.Current.SystemAudioDeviceId == "{render-endpoint}" && reloaded.Current.CaptureMicrophone &&
    reloaded.Current.MicrophoneDeviceId == "{microphone-endpoint}" && reloaded.Current.SystemAudioGain == .72 &&
    reloaded.Current.MicrophoneGain == 1.25, "Selected audio devices, microphone and gains did not round-trip.");
Check(!reloaded.Current.NotifyOnClipSaved && !reloaded.Current.NotifyOnRecordingStarted &&
    !reloaded.Current.NotifyOnRecordingStopped && !reloaded.Current.NotifyOnAutomaticSourceChange &&
    reloaded.Current.CaptureNotificationDurationSeconds == 9, "Popup preferences did not round-trip.");
Check(reloaded.Current.CaptureEncoderMode == "nvenc" && reloaded.Current.AudioCaptureMode == "game",
    "GPU encoder preference and game-only capture mode did not normalize and round-trip.");
Check(reloaded.Current.GameAudioProcessName == "Game.Client" && reloaded.Current.DiscordProcessName == "DiscordPTB" &&
    reloaded.Current.MusicProcessName == "Music Player", "Process audio app identities did not normalize and round-trip.");
Check(reloaded.Current.CaptureDiscordAudio && reloaded.Current.CaptureMusicAudio && reloaded.Current.DiscordAudioGain == .62 &&
    reloaded.Current.MusicAudioGain == 1.18, "Optional app-source toggles and independent gains did not round-trip.");
Check(!reloaded.Current.AudioLimiterEnabled && !reloaded.Current.LowDiskProtectionEnabled,
    "Limiter/disk protection opt-outs did not round-trip.");

await File.WriteAllTextAsync(path, """
{"CaptureSourceMode":"unknown","CaptureMonitorId":null,"CaptureWindowProcessName":null,"CaptureWindowTitle":null,"CaptureGameProcessNames":null,"CaptureNotificationDurationSeconds":999}
""");
await reloaded.LoadAsync();
Check(reloaded.Current.CaptureSourceMode == "auto", "Unknown source mode did not fall back to automatic.");
Check(reloaded.Current.CaptureMonitorId == "" && reloaded.Current.CaptureWindowProcessName == "" &&
    reloaded.Current.CaptureWindowTitle == "" && reloaded.Current.CaptureGameProcessNames.Count == 0,
    "Malformed source identities were not made safe.");
Check(reloaded.Current.CaptureNotificationDurationSeconds == 10, "Long notification duration was not bounded.");
reloaded.Current.CaptureNotificationDurationSeconds = -10;
reloaded.Current.CaptureSourceMode = "monitor";
reloaded.Current.SystemAudioDeviceId = null!;
reloaded.Current.MicrophoneDeviceId = null!;
reloaded.Current.SystemAudioGain = -4;
reloaded.Current.MicrophoneGain = 10;
await reloaded.SaveAsync();
Check(reloaded.Current.CaptureNotificationDurationSeconds == 2 && reloaded.Current.CaptureSourceMode == "desktop",
    "Save did not normalize duration and the legacy monitor alias.");
Check(reloaded.Current.SystemAudioDeviceId == "" && reloaded.Current.MicrophoneDeviceId == "" &&
    reloaded.Current.SystemAudioGain == 0 && reloaded.Current.MicrophoneGain == 2, "Invalid audio endpoint IDs/gains were not normalized.");
reloaded.Current.SystemAudioGain = double.NaN;
reloaded.Current.MicrophoneGain = double.PositiveInfinity;
await reloaded.SaveAsync();
Check(reloaded.Current.SystemAudioGain == 1 && reloaded.Current.MicrophoneGain == 1,
    "Non-finite audio gains were not made safe before serialization.");
reloaded.Current.CaptureEncoderMode = "invalid encoder";
reloaded.Current.AudioCaptureMode = "invalid mode";
reloaded.Current.GameAudioProcessName = @"C:\Games\Unintended.exe";
reloaded.Current.DiscordProcessName = null!;
reloaded.Current.MusicProcessName = new string('a', 129);
reloaded.Current.DiscordAudioGain = double.NaN;
reloaded.Current.MusicAudioGain = double.NegativeInfinity;
await reloaded.SaveAsync();
Check(reloaded.Current.CaptureEncoderMode == "auto" && reloaded.Current.AudioCaptureMode == "desktop",
    "Unknown encoder/audio modes were not normalized to safe compatible defaults.");
Check(reloaded.Current.GameAudioProcessName == "" && reloaded.Current.DiscordProcessName == "" && reloaded.Current.MusicProcessName == "",
    "Paths, null and excessive process audio identities were not rejected safely.");
Check(reloaded.Current.DiscordAudioGain == 1 && reloaded.Current.MusicAudioGain == 1,
    "Non-finite app-source gains were not made safe before JSON serialization.");
reloaded.Current.DiscordAudioGain = -2;
reloaded.Current.MusicAudioGain = 20;
await reloaded.SaveAsync();
Check(reloaded.Current.DiscordAudioGain == 0 && reloaded.Current.MusicAudioGain == 2,
    "App-source gains did not retain the existing zero-to-two capture range.");
foreach (var mode in new[] { "cpu", "qsv", "amf", "auto" })
{
    reloaded.Current.CaptureEncoderMode = mode;
    await reloaded.SaveAsync();
    var saved = new SettingsService(path);
    await saved.LoadAsync();
    Check(saved.Current.CaptureEncoderMode == mode, "A supported capture encoder preference did not survive reopening settings.");
}
await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => reloaded.SaveAsync()));
using (var json = JsonDocument.Parse(await File.ReadAllTextAsync(path)))
    Check(json.RootElement.GetProperty("CaptureSourceMode").GetString() == "desktop", "Concurrent saves left invalid JSON.");
Check(!File.Exists(path + ".tmp"), "Saving left an unfinished temporary file.");

await File.WriteAllTextAsync(path, "{ corrupt");
await reloaded.LoadAsync();
CheckDefaults(reloaded.Current);
var profileSettings = new KlippSettings();
var game = new CaptureApplicationInfo("profile-audio-test", "Audio profile test", "Disposable fixture");
CaptureProfileService.SaveApplicationProfile(profileSettings, game,
    new CaptureOptions { ResolutionMode = "1080p", FrameRate = 60, BitrateMbps = 24 }, "Smooth");
var configuredAudio = new CaptureOptions
{
    CaptureSystemAudio = false, SystemAudioDeviceId = "explicit-render-endpoint", CaptureMicrophone = true,
    MicrophoneDeviceId = "explicit-microphone-endpoint", SystemAudioGain = .42, MicrophoneGain = 1.37,
    EnableExclusiveGameCapture = true
};
var appResolved = new CaptureProfileService().ResolveForApplication(profileSettings, configuredAudio,
    "PROFILE-AUDIO-TEST", game);
Check(appResolved.IsApplicationSpecific && appResolved.Options.ResolutionMode == "1080p" &&
    appResolved.Options.FrameRate == 60 && appResolved.Options.BitrateMbps == 24 &&
    appResolved.Options.CaptureSystemAudio == configuredAudio.CaptureSystemAudio &&
    appResolved.Options.SystemAudioDeviceId == configuredAudio.SystemAudioDeviceId &&
    appResolved.Options.CaptureMicrophone == configuredAudio.CaptureMicrophone &&
    appResolved.Options.MicrophoneDeviceId == configuredAudio.MicrophoneDeviceId &&
    appResolved.Options.SystemAudioGain == configuredAudio.SystemAudioGain &&
    appResolved.Options.MicrophoneGain == configuredAudio.MicrophoneGain &&
    appResolved.Options.EnableExclusiveGameCapture == configuredAudio.EnableExclusiveGameCapture,
    "Resolving a saved quality profile changed audio routing, gains or the exclusive game capture opt-in.");
Console.WriteLine($"PASS {checks} isolated settings checks. Fixture: {directory}");
