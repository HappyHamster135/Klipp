using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;

namespace Klipp.Encoding.Audio;

public sealed record ProcessAudioApplication(string ProcessName, string DisplayName, int ProcessId);

public static class ProcessAudioCaptureSupport
{
    public static bool IsSupported => OperatingSystem.IsWindowsVersionAtLeast(10, 0, 20348);
    public static string Description => IsSupported ? "Captures only the selected app and its child processes."
        : "App audio requires Windows 10 build 20348 or Windows 11. Desktop audio remains available.";
}

/// <summary>Persist executable names, never PIDs. A resolved PID is paired with its creation time before activation.</summary>
public static class AudioApplicationCatalog
{
    public static IReadOnlyList<ProcessAudioApplication> GetApplications()
    {
        var apps = new List<ProcessAudioApplication>();
        using var current = Process.GetCurrentProcess();
        var sessionId = current.SessionId;
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    if (process.Id == Environment.ProcessId || process.SessionId != sessionId) continue;
                    var name = process.ProcessName;
                    var title = process.MainWindowTitle;
                    if (string.IsNullOrWhiteSpace(title) && !name.Contains("discord", StringComparison.OrdinalIgnoreCase) &&
                        !name.Contains("spotify", StringComparison.OrdinalIgnoreCase)) continue;
                    apps.Add(new(name, string.IsNullOrWhiteSpace(title) ? name : $"{name} · {title}", process.Id));
                }
                catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException) { }
            }
        }
        return apps.GroupBy(app => app.ProcessName, StringComparer.OrdinalIgnoreCase).Select(group => group.First())
            .OrderBy(app => app.ProcessName, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    internal static string NormalizeName(string? name) => Path.GetFileNameWithoutExtension((name ?? string.Empty).Trim());

    internal static string ConfigureProcess(int pid, string? name, long expectedStartTicks = 0)
    {
        if (!string.IsNullOrWhiteSpace(name)) return "app:" + NormalizeName(name);
        if (pid <= 0) return "app:";
        if (expectedStartTicks > 0) return $"pid:{pid}:{expectedStartTicks}";
        try
        {
            using var process = Process.GetProcessById(pid);
            return $"pid:{pid}:{process.StartTime.ToUniversalTime().Ticks}";
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        { return $"pid:{pid}:0"; }
    }

    internal static bool IsProcessConfiguration(string id) => id.StartsWith("app:", StringComparison.Ordinal) ||
        id.StartsWith("pid:", StringComparison.Ordinal) || id.StartsWith("process:", StringComparison.Ordinal);

    internal static AudioCaptureEndpointInfo ResolveProcess(string configured)
    {
        if (!ProcessAudioCaptureSupport.IsSupported) throw new PlatformNotSupportedException(ProcessAudioCaptureSupport.Description);
        Process? selected = null;
        if (configured.StartsWith("app:", StringComparison.Ordinal))
        {
            var name = NormalizeName(configured[4..]);
            if (name.Length == 0) throw new InvalidOperationException("Choose an app for this audio track.");
            var processes = Process.GetProcessesByName(name);
            try
            {
                // The oldest process of an application is its process-tree root,
                // which includes Electron/browser audio child processes.
                selected = processes.Where(process => !process.HasExited)
                    .OrderBy(process => process.StartTime).FirstOrDefault();
                if (selected is null) throw new InvalidOperationException("The selected audio app is not running.");
                return Identity(selected);
            }
            finally { foreach (var process in processes) process.Dispose(); }
        }
        var parts = configured.Split(':');
        if (parts.Length < 3 || !int.TryParse(parts[1], out var pid) || !long.TryParse(parts[2], out var created))
            throw new InvalidOperationException("The selected app identity is invalid.");
        using (selected = Process.GetProcessById(pid))
        {
            if (created <= 0 || selected.StartTime.ToUniversalTime().Ticks != created)
                throw new InvalidOperationException("The selected app has closed. Select its new window.");
            return Identity(selected);
        }
    }

    private static AudioCaptureEndpointInfo Identity(Process process) => new(
        $"process:{process.Id}:{process.StartTime.ToUniversalTime().Ticks}", process.ProcessName);
}
