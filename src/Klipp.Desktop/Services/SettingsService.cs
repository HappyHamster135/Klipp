using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;

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
}

/// <summary>
/// Läser och skriver appinställningar som JSON i %LocalAppData%\Klipp\settings.json.
/// </summary>
public sealed class SettingsService
{
    private readonly string _settingsPath;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public KlippSettings Current { get; private set; } = new();

    public SettingsService()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var dir = Path.Combine(localAppData, "Klipp");
        Directory.CreateDirectory(dir);
        _settingsPath = Path.Combine(dir, "settings.json");
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
        }
        catch
        {
            // Korrupt eller oläsbar fil — falla tillbaka på defaults hellre än att krascha.
            Current = new KlippSettings();
        }
    }

    /// <summary>Sparar nuvarande inställningar till disk.</summary>
    public async Task SaveAsync()
    {
        try
        {
            var json = JsonSerializer.Serialize(Current, JsonOptions);
            await File.WriteAllTextAsync(_settingsPath, json).ConfigureAwait(false);
        }
        catch
        {
            // Om vi inte kan skriva (t.ex. disk full) ignorerar vi hellre än kraschar.
        }
    }
}
