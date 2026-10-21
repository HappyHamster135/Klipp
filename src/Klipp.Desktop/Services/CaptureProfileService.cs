using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace Klipp.Desktop.Services;

public sealed class CaptureApplicationProfile
{
    public string ProcessName { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string ProfileName { get; set; } = "Custom";
    public string ResolutionMode { get; set; } = "720p";
    public int FrameRate { get; set; } = 24;
    public int BitrateMbps { get; set; } = 8;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed record CaptureProfileDefinition(
    string Id,
    string Name,
    string Description,
    string ResolutionMode,
    int FrameRate,
    int BitrateMbps);

public sealed record CaptureApplicationInfo(
    string ProcessName,
    string DisplayName,
    string WindowTitle);

public sealed record CaptureProfileResolution(
    CaptureOptions Options,
    string ProfileName,
    CaptureApplicationInfo? Application,
    bool IsApplicationSpecific);

public sealed class CaptureProfileService
{
    private delegate bool EnumWindowsProc(nint hwnd, nint parameter);

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint hwnd, out uint processId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsProc callback, nint parameter);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(nint hwnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextLength(nint hwnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(nint hwnd, StringBuilder text, int maximumCount);

    private readonly int _currentProcessId = Environment.ProcessId;
    private CaptureApplicationInfo? _lastExternalApplication;

    public static IReadOnlyList<CaptureProfileDefinition> BuiltInProfiles { get; } =
    [
        new("eco", "Eco", "Lowest background usage", "540p", 15, 5),
        new("balanced", "Balanced", "Consistent quality for long sessions", "720p", 24, 8),
        new("quality", "Quality", "Sharp 1080p recording", "1080p", 30, 16),
        new("smooth", "Smooth motion", "60 fps for fast games", "1080p", 60, 24)
    ];

    public CaptureApplicationInfo? LastExternalApplication => _lastExternalApplication;

    public void RememberApplication(CaptureApplicationInfo application)
    {
        _lastExternalApplication = application;
    }

    public CaptureApplicationInfo? ObserveForegroundApplication()
    {
        var application = TryGetApplication(GetForegroundWindow());
        if (application is not null)
            _lastExternalApplication = application;
        return application ?? _lastExternalApplication;
    }

    public IReadOnlyList<CaptureApplicationInfo> GetRunningApplications()
    {
        var applications = new Dictionary<string, CaptureApplicationInfo>(StringComparer.OrdinalIgnoreCase);
        EnumWindows((window, _) =>
        {
            if (!IsWindowVisible(window) || GetWindowTextLength(window) <= 0)
                return true;

            var application = TryGetApplication(window);
            if (application is not null)
                applications.TryAdd(application.ProcessName, application);
            return true;
        }, 0);

        return applications.Values
            .OrderBy(application => application.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
    }

    public CaptureProfileResolution Resolve(KlippSettings settings, CaptureOptions configuredOptions)
    {
        var application = ObserveForegroundApplication();
        return ResolveForApplication(settings, configuredOptions, application?.ProcessName, application);
    }

    public CaptureProfileResolution ResolveForApplication(
        KlippSettings settings,
        CaptureOptions configuredOptions,
        string? processName,
        CaptureApplicationInfo? application = null)
    {
        if (settings.AutoSelectCaptureApplicationProfiles && !string.IsNullOrWhiteSpace(processName))
        {
            var binding = settings.CaptureApplicationProfiles.FirstOrDefault(candidate =>
                string.Equals(candidate.ProcessName, processName, StringComparison.OrdinalIgnoreCase));
            if (binding is not null)
            {
                return new CaptureProfileResolution(
                    configuredOptions with
                    {
                        ResolutionMode = NormalizeResolution(binding.ResolutionMode),
                        FrameRate = NormalizeFrameRate(binding.FrameRate),
                        BitrateMbps = NormalizeBitrate(binding.BitrateMbps),
                    },
                    string.IsNullOrWhiteSpace(binding.ProfileName) ? "App profile" : binding.ProfileName,
                    application ?? new CaptureApplicationInfo(binding.ProcessName, binding.DisplayName, string.Empty),
                    true);
            }
        }

        return new CaptureProfileResolution(
            configuredOptions,
            GetProfileName(settings.CaptureProfileId),
            application,
            false);
    }

    public static CaptureProfileDefinition? FindBuiltIn(string? profileId) =>
        BuiltInProfiles.FirstOrDefault(profile =>
            string.Equals(profile.Id, profileId, StringComparison.OrdinalIgnoreCase));

    public static void ApplyBuiltIn(KlippSettings settings, CaptureProfileDefinition profile)
    {
        settings.CaptureProfileId = profile.Id;
        settings.CaptureResolutionMode = profile.ResolutionMode;
        settings.CaptureFrameRate = profile.FrameRate;
        settings.CaptureBitrateMbps = profile.BitrateMbps;
    }

    public static CaptureApplicationProfile SaveApplicationProfile(
        KlippSettings settings,
        CaptureApplicationInfo application,
        CaptureOptions options,
        string profileName)
    {
        var existing = settings.CaptureApplicationProfiles.FirstOrDefault(candidate =>
            string.Equals(candidate.ProcessName, application.ProcessName, StringComparison.OrdinalIgnoreCase));
        if (existing is null)
        {
            existing = new CaptureApplicationProfile { ProcessName = application.ProcessName };
            settings.CaptureApplicationProfiles.Add(existing);
        }

        if (!string.IsNullOrWhiteSpace(application.WindowTitle) || string.IsNullOrWhiteSpace(existing.DisplayName))
            existing.DisplayName = application.DisplayName;
        existing.ProfileName = string.IsNullOrWhiteSpace(profileName) ? "Custom" : profileName;
        existing.ResolutionMode = NormalizeResolution(options.ResolutionMode);
        existing.FrameRate = NormalizeFrameRate(options.FrameRate);
        existing.BitrateMbps = NormalizeBitrate(options.BitrateMbps);
        existing.UpdatedAt = DateTimeOffset.UtcNow;
        return existing;
    }

    public static bool RemoveApplicationProfile(KlippSettings settings, string processName) =>
        settings.CaptureApplicationProfiles.RemoveAll(candidate =>
            string.Equals(candidate.ProcessName, processName, StringComparison.OrdinalIgnoreCase)) > 0;

    public static string GetProfileName(string? profileId)
    {
        if (string.Equals(profileId, "recommended", StringComparison.OrdinalIgnoreCase))
            return "Recommended";
        return FindBuiltIn(profileId)?.Name ?? "Custom";
    }

    private CaptureApplicationInfo? TryGetApplication(nint window)
    {
        if (window == 0)
            return null;

        GetWindowThreadProcessId(window, out var processId);
        if (processId == 0 || processId == _currentProcessId)
            return null;

        try
        {
            using var process = Process.GetProcessById((int)processId);
            var processName = process.ProcessName.Trim();
            if (string.IsNullOrWhiteSpace(processName))
                return null;

            var titleLength = GetWindowTextLength(window);
            var titleBuilder = new StringBuilder(Math.Max(1, titleLength + 1));
            _ = GetWindowText(window, titleBuilder, titleBuilder.Capacity);
            var title = titleBuilder.ToString().Trim();
            var displayName = string.IsNullOrWhiteSpace(title) ? processName : title;
            return new CaptureApplicationInfo(processName, displayName, title);
        }
        catch
        {
            return null;
        }
    }

    private static string NormalizeResolution(string? mode) => mode switch
    {
        "540p" => "540p",
        "720p" => "720p",
        "1080p" => "1080p",
        _ => "Native"
    };

    private static int NormalizeFrameRate(int frameRate) => frameRate switch
    {
        15 => 15,
        24 => 24,
        60 => 60,
        _ => 30
    };

    private static int NormalizeBitrate(int bitrateMbps) => bitrateMbps switch
    {
        5 => 5,
        8 => 8,
        24 => 24,
        35 => 35,
        _ => 16
    };
}
