namespace Klipp.Core.Capture;

public sealed record CaptureGameEvidence(string ProcessName, string WindowClass = "",
    bool HasEngineFiles = false, bool HasRendererImports = false, bool IsGameInstallLocation = false,
    bool IsFullscreen = false);

public readonly record struct CaptureGameRecognition(bool IsGame, string Reason);

/// <summary>Conservative evidence, independent of a window's title or a browser page's contents.</summary>
public static class CaptureGamePolicy
{
    private static readonly HashSet<string> Excluded = new(StringComparer.OrdinalIgnoreCase)
    {
        "chrome", "msedge", "firefox", "brave", "opera", "opera_gx", "iexplore", "explorer", "dwm",
        "shellexperiencehost", "startmenuexperiencehost", "searchhost", "applicationframehost", "systemsettings",
        "taskmgr", "notepad", "code", "devenv", "windowsterminal", "powershell", "pwsh", "cmd",
        "steam", "steamwebhelper", "epicgameslauncher", "epicwebhelper", "eadesktop", "ealauncher",
        "riotclientservices", "riotclientux", "battle.net", "blizzardbrowser", "ubisoftconnect", "upc", "uplay",
        "galaxyclient", "goggalaxy", "playnite", "discord", "obs64", "obs32", "slobs", "klipp.desktop", "codex",
        "unity", "unityhub", "unrealeditor", "ue4editor", "blender", "photoshop", "wallpaper32", "wallpaper64",
        "wallpaperengine", "vlc", "mpv", "wmplayer", "video.ui", "photos", "mspaint", "winword", "excel"
    };
    private static readonly HashSet<string> KnownGames = new(StringComparer.OrdinalIgnoreCase)
    {
        "cs2", "dota2", "hl2", "tf_win64", "r5apex", "fortniteclient-win64-shipping", "valorant-win64-shipping",
        "rocketleague", "overwatch", "minecraft.windows", "eldenring", "cyberpunk2077", "gta5", "gta5_enhanced",
        "rdr2", "witcher3", "league of legends", "robloxplayerbeta", "wow", "wowclassic", "ffxiv_dx11",
        "destiny2", "deadbydaylight-win64-shipping", "bg3", "bg3_dx11", "helldivers2", "rainbowsix",
        "rainbowsix_vulkan", "bf2042", "bf1", "bfv", "forzahorizon5", "fallguys_client_game", "rustclient",
        "terraria", "stardew valley", "valheim", "factorio", "hades", "hades2", "starfield", "sekiro"
    };

    public static CaptureGameRecognition Classify(CaptureGameEvidence evidence)
    {
        var process = NormalizeProcessName(evidence.ProcessName);
        if (IsExcludedProcess(process)) return new(false, "Application is excluded from automatic game capture");
        if (KnownGames.Contains(process)) return new(true, "Recognized game executable");
        if (evidence.HasEngineFiles && (evidence.IsGameInstallLocation || evidence.IsFullscreen))
            return new(true, "Game engine files and game location or fullscreen window");
        if (evidence.IsFullscreen && evidence.IsGameInstallLocation && evidence.HasRendererImports)
            return new(true, "Fullscreen game installation with a graphics renderer");
        return new(false, "No reliable game evidence");
    }

    public static bool IsExcludedProcess(string? processName)
    {
        var process = NormalizeProcessName(processName);
        return process.Length == 0 || Excluded.Contains(process) || process.Contains("launcher", StringComparison.Ordinal) ||
            process.EndsWith("editor", StringComparison.Ordinal);
    }

    public static string NormalizeProcessName(string? value)
    {
        var name = Path.GetFileName((value ?? string.Empty).Replace('\\', '/')).Trim().ToLowerInvariant();
        return name.EndsWith(".exe", StringComparison.Ordinal) ? name[..^4] : name;
    }
}
