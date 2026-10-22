using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Klipp.Core.Input;
using Klipp.Core.Models;

namespace Klipp.Desktop.Services;

/// <summary>
/// Appens sparbara inställningar. Serialiseras till JSON.
/// </summary>
public sealed class KlippSettings
{
    /// <summary>Klipplängd i sekunder (15, 30, 60, 120).</summary>
    public int ClipLengthSeconds { get; set; } = 30;

    /// <summary>Hotkey-modifiers (Win32-flaggor: Ctrl=2, Shift=4, Alt=1, Win=8).</summary>
    public uint HotkeyModifiers { get; set; } = 0x0002 | 0x0004; // Ctrl+Shift

    /// <summary>Hotkey virtual key code (0x53 = 'S').</summary>
    public uint HotkeyVirtualKey { get; set; } = 0x53;

    /// <summary>Läsbar representation av hotkeyen, t.ex. "Ctrl + Shift + S".</summary>
    public string HotkeyDisplay { get; set; } = "Ctrl + Shift + S";

    public uint BookmarkHotkeyModifiers { get; set; } = 6;
    public uint BookmarkHotkeyVirtualKey { get; set; } = 0x4D;
    public string BookmarkHotkeyDisplay { get; set; } = "Ctrl + Shift + M";

    /// <summary>Capture-FPS för både vanlig inspelning och klipp-läge.</summary>
    public int CaptureFrameRate { get; set; } = 24;

    /// <summary>Äldre kvalitetspreset. Behålls för bakåtkompatibilitet med gamla settings.json.</summary>
    public string CaptureQualityPreset { get; set; } = "Balanced";

    /// <summary>Output-upplösning: Native, 1080p eller 720p.</summary>
    public string CaptureResolutionMode { get; set; } = "720p";

    /// <summary>Video-bitrate i megabit per sekund.</summary>
    public int CaptureBitrateMbps { get; set; } = 8;
    public string CaptureEncoderMode { get; set; } = "auto";

    /// <summary>Vald grundprofil. Egen betyder att de tre manuella capture-värdena används.</summary>
    public string CaptureProfileId { get; set; } = "custom";

    /// <summary>Välj automatiskt en sparad profil för appen som är aktiv när capture startar.</summary>
    public bool AutoSelectCaptureApplicationProfiles { get; set; } = true;

    /// <summary>Sparade capture-värden per spel eller app, identifierade av processnamn.</summary>
    public List<CaptureApplicationProfile> CaptureApplicationProfiles { get; set; } = [];

    /// <summary>Capture selection: automatic game detection, a display, or a window.</summary>
    public string CaptureSourceMode { get; set; } = "auto";

    /// <summary>Stable Windows display device identity; native handles are never persisted.</summary>
    public string CaptureMonitorId { get; set; } = string.Empty;

    /// <summary>Identity used to find a manually selected window after reopening Klipp.</summary>
    public string CaptureWindowProcessName { get; set; } = string.Empty;
    public string CaptureWindowTitle { get; set; } = string.Empty;

    public bool AutoDetectGames { get; set; } = true;
    public List<string> CaptureGameProcessNames { get; set; } = [];
    public bool CaptureSystemAudio { get; set; } = true;
    public string SystemAudioDeviceId { get; set; } = string.Empty;
    public bool CaptureMicrophone { get; set; }
    public string MicrophoneDeviceId { get; set; } = string.Empty;
    public double SystemAudioGain { get; set; } = 1;
    public double MicrophoneGain { get; set; } = 1;
    public string AudioCaptureMode { get; set; } = "desktop";
    public string GameAudioProcessName { get; set; } = string.Empty;
    public bool CaptureDiscordAudio { get; set; }
    public string DiscordProcessName { get; set; } = "Discord";
    public double DiscordAudioGain { get; set; } = 1;
    public bool CaptureMusicAudio { get; set; }
    public string MusicProcessName { get; set; } = string.Empty;
    public double MusicAudioGain { get; set; } = 1;
    public bool AudioLimiterEnabled { get; set; } = true;
    public bool LowDiskProtectionEnabled { get; set; } = true;
    public bool AutomaticUpdatesEnabled { get; set; } = true;
    /// <summary>Only the version explicitly dismissed with Later; newer updates may still be announced.</summary>
    public string? DeferredUpdateVersion { get; set; }
    public bool CaptureCursor { get; set; } = true;
    public bool EnableExclusiveGameCapture { get; set; }

    public bool NotifyOnClipSaved { get; set; } = true;
    public bool NotifyOnRecordingStarted { get; set; } = true;
    public bool NotifyOnRecordingStopped { get; set; } = true;
    public bool NotifyOnAutomaticSourceChange { get; set; } = true;
    public bool NotifyOnRecordingBookmark { get; set; } = true;
    public bool NotifyOnAudioDeviceChange { get; set; } = true;
    public int CaptureNotificationDurationSeconds { get; set; } = 4;

    /// <summary>Om klipp-läge ska starta automatiskt vid appstart.</summary>
    public bool AutoStartClipMode { get; set; } = false;

    /// <summary>Försök använda borderless Windows Graphics Capture.</summary>
    public bool HideCaptureBorder { get; set; } = true;

    /// <summary>Mapp där färdiga klipp sparas. Tomt värde betyder standardmappen Videos\Klipp.</summary>
    public string ClipsDirectory { get; set; } = string.Empty;

    /// <summary>Maximal gemensam storlek för proxy-, thumbnail-, waveform- och timeline-rendercache.</summary>
    public int CacheBudgetMb { get; set; } = 4096;

    /// <summary>Rensa återskapningsbar cache automatiskt när budgeten överskrids.</summary>
    public bool AutoCleanCache { get; set; } = true;

    /// <summary>Flytta gamla, oskyddade originalklipp till Windows papperskorg.</summary>
    public bool AutoCleanOldOriginalClips { get; set; }

    /// <summary>Minsta ålder innan ett oskyddat originalklipp kan rensas.</summary>
    public int OriginalClipRetentionDays { get; set; } = OriginalClipRetentionPolicy.DefaultRetentionDays;

    /// <summary>Senaste slutförda automatiska retentionkontroll.</summary>
    public DateTime? LastOriginalClipRetentionRunUtc { get; set; }

    /// <summary>Konfliktfria tangentkommandon som används i projekteditorn.</summary>
    public List<EditorShortcutBinding> EditorShortcuts { get; set; } = EditorShortcutCatalog.CreateDefaults();

    /// <summary>Intern version för engångsmigreringar av prestandasäkra defaults.</summary>
    public int PerformanceDefaultsVersion { get; set; }

    public void NormalizeCapturePreferences()
    {
        CaptureEncoderMode = CaptureEncoderMode?.Trim().ToLowerInvariant() switch
        {
            "cpu" => "cpu", "nvenc" => "nvenc", "qsv" => "qsv", "amf" => "amf", _ => "auto"
        };
        AudioCaptureMode = string.Equals(AudioCaptureMode?.Trim(), "game", StringComparison.OrdinalIgnoreCase) ? "game" : "desktop";
        GameAudioProcessName = NormalizeAudioProcessName(GameAudioProcessName);
        DiscordProcessName = NormalizeAudioProcessName(DiscordProcessName);
        MusicProcessName = NormalizeAudioProcessName(MusicProcessName);
        DiscordAudioGain = double.IsFinite(DiscordAudioGain) ? Math.Clamp(DiscordAudioGain, 0, 2) : 1;
        MusicAudioGain = double.IsFinite(MusicAudioGain) ? Math.Clamp(MusicAudioGain, 0, 2) : 1;
        if ((BookmarkHotkeyModifiers & ~7u) != 0 || BookmarkHotkeyModifiers == 0 ||
            BookmarkHotkeyVirtualKey is < 0x20 or > 0xFE || string.IsNullOrWhiteSpace(BookmarkHotkeyDisplay))
        {
            BookmarkHotkeyModifiers = 6;
            BookmarkHotkeyVirtualKey = 0x4D;
            BookmarkHotkeyDisplay = "Ctrl + Shift + M";
        }
        CaptureSourceMode = CaptureSourceMode?.Trim().ToLowerInvariant() switch
        {
            "desktop" or "monitor" => "desktop",
            "window" => "window",
            _ => "auto"
        };
        CaptureMonitorId = (CaptureMonitorId ?? string.Empty).Trim();
        CaptureWindowProcessName = (CaptureWindowProcessName ?? string.Empty).Trim();
        CaptureWindowTitle = (CaptureWindowTitle ?? string.Empty).Trim();
        SystemAudioDeviceId = (SystemAudioDeviceId ?? string.Empty).Trim();
        MicrophoneDeviceId = (MicrophoneDeviceId ?? string.Empty).Trim();
        SystemAudioGain = double.IsFinite(SystemAudioGain) ? Math.Clamp(SystemAudioGain, 0, 2) : 1;
        MicrophoneGain = double.IsFinite(MicrophoneGain) ? Math.Clamp(MicrophoneGain, 0, 2) : 1;
        CaptureGameProcessNames = (CaptureGameProcessNames ?? [])
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => name.Trim().ToLowerInvariant())
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        CaptureNotificationDurationSeconds = Math.Clamp(CaptureNotificationDurationSeconds, 2, 10);
    }

    private static string NormalizeAudioProcessName(string? value)
    {
        var name = (value ?? string.Empty).Trim();
        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) name = name[..^4];
        return name.Length <= 128 && name.All(character => char.IsLetterOrDigit(character) || character is ' ' or '-' or '_' or '.')
            ? name : string.Empty;
    }
}

/// <summary>
/// Läser och skriver appinställningar som JSON i %LocalAppData%\Klipp\settings.json.
/// </summary>
public sealed class SettingsService
{
    private readonly string _settingsPath;
    private readonly SemaphoreSlim _saveGate = new(1, 1);
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public KlippSettings Current { get; private set; } = new();

    public SettingsService() : this(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Klipp", "settings.json"))
    {
    }

    public SettingsService(string settingsPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(settingsPath);
        _settingsPath = Path.GetFullPath(settingsPath);
        Directory.CreateDirectory(Path.GetDirectoryName(_settingsPath)!);
    }

    /// <summary>Läser inställningar från disk. Använder defaults om filen saknas eller är korrupt.</summary>
    public async Task LoadAsync()
    {
        try
        {
            if (!File.Exists(_settingsPath))
            {
                Current = new KlippSettings();
                return;
            }

            var json = await File.ReadAllTextAsync(_settingsPath).ConfigureAwait(false);
            Current = JsonSerializer.Deserialize<KlippSettings>(json) ?? new KlippSettings();
            Current.NormalizeCapturePreferences();
            Current.CaptureApplicationProfiles ??= [];
            Current.EditorShortcuts = EditorShortcutCatalog.Normalize(Current.EditorShortcuts);
            Current.OriginalClipRetentionDays = OriginalClipRetentionPolicy.NormalizeRetentionDays(
                Current.OriginalClipRetentionDays);
        }
        catch
        {
            // Korrupt eller oläsbar fil — falla tillbaka på defaults hellre än att krascha.
            Current = new KlippSettings();
        }
    }

    /// <summary>Sparar nuvarande inställningar till disk.</summary>
    public async Task SaveForUpdateAsync(CancellationToken cancellationToken = default)
    {
        await _saveGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Current.NormalizeCapturePreferences();
            var json = JsonSerializer.Serialize(Current, JsonOptions);
            var temporaryPath = _settingsPath + ".tmp";
            await File.WriteAllTextAsync(temporaryPath, json, cancellationToken).ConfigureAwait(false);
            File.Move(temporaryPath, _settingsPath, overwrite: true);
        }
        finally { _saveGate.Release(); }
    }

    /// <summary>Saves ordinary preferences without interrupting capture on an I/O error.</summary>
    public async Task SaveAsync()
    {
        var temporaryPath = _settingsPath + ".tmp";
        await _saveGate.WaitAsync().ConfigureAwait(false);
        try
        {
            Current.NormalizeCapturePreferences();
            var json = JsonSerializer.Serialize(Current, JsonOptions);
            await File.WriteAllTextAsync(temporaryPath, json).ConfigureAwait(false);
            File.Move(temporaryPath, _settingsPath, overwrite: true);
        }
        catch
        {
            // Om vi inte kan skriva (t.ex. disk full) ignorerar vi hellre än kraschar.
            try { File.Delete(temporaryPath); } catch { }
        }
        finally
        {
            _saveGate.Release();
        }
    }
}
