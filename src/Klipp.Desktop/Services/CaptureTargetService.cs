using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using Klipp.Capture.Models;
using Klipp.Capture.Video;
using Klipp.Core.Capture;

namespace Klipp.Desktop.Services;

public sealed record CaptureTargetInfo(CaptureTarget Target, string Id, string DisplayName,
    string ProcessName, string WindowTitle, int Width, int Height, bool IsPrimary = false,
    bool IsForeground = false, bool IsMinimized = false, bool IsGame = false, string GameReason = "", string DeviceId = "")
{
    public override string ToString() => DisplayName;
}

/// <summary>Enumerates capturable sources. File/renderer evidence is cached; call enumeration off the UI thread.</summary>
public sealed class CaptureTargetService
{
    private sealed record ProcessInfo(string Path, string Name, long Created);
    private sealed record GameFiles(bool Engine, bool Renderer, bool GameLocation, DateTime CheckedUtc);
    private readonly ConcurrentDictionary<uint, ProcessInfo> _processes = new();
    private readonly ConcurrentDictionary<string, GameFiles> _gameFiles = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<CaptureTargetInfo> GetMonitors()
    {
        var result = new List<CaptureTargetInfo>();
        Native.EnumDisplayMonitors(0, 0, (nint monitor, nint _, ref Native.Rect bounds, nint state) =>
        {
            var info = new Native.MonitorInfo { Size = (uint)Marshal.SizeOf<Native.MonitorInfo>() };
            if (!Native.GetMonitorInfo(monitor, ref info)) return true;
            var width = info.Monitor.Right - info.Monitor.Left;
            var height = info.Monitor.Bottom - info.Monitor.Top;
            if (width <= 0 || height <= 0) return true;
            var primary = (info.Flags & 1) != 0;
            var physical = new Native.DisplayDevice { Size = (uint)Marshal.SizeOf<Native.DisplayDevice>() };
            var stableId = Native.EnumDisplayDevices(info.Device, 0, ref physical, 1) && !string.IsNullOrEmpty(physical.DeviceId)
                ? physical.DeviceId : info.Device ?? $"monitor:{monitor:X}";
            var deviceName = info.Device ?? string.Empty;
            var displayName = deviceName.Contains("DISPLAY", StringComparison.OrdinalIgnoreCase)
                ? "Display " + deviceName[(deviceName.LastIndexOf("DISPLAY", StringComparison.OrdinalIgnoreCase) + 7)..] : "Display";
            var display = $"{displayName}{(primary ? " (primary)" : string.Empty)} · {width} × {height}";
            var target = new CaptureTarget { Kind = CaptureTargetKind.Monitor, Handle = monitor,
                DisplayName = display, DeviceId = stableId };
            result.Add(new(target, stableId, display, string.Empty, string.Empty,
                width, height, primary, DeviceId: stableId));
            return true;
        }, 0);
        return result.OrderByDescending(item => item.IsPrimary).ThenBy(item => item.Id, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public CaptureTargetInfo GetPrimaryMonitor() => GetMonitors().FirstOrDefault(item => item.IsPrimary) ??
        GetMonitors().FirstOrDefault() ?? throw new InvalidOperationException("No connected display is available for capture.");

    public IReadOnlyList<CaptureTargetInfo> GetWindows()
    {
        var windows = new List<CaptureTargetInfo>();
        var foreground = Native.GetForegroundWindow();
        Native.EnumWindows((window, _) => { if (TryGetWindow(window, foreground, out var info)) windows.Add(info!); return true; }, 0);
        return windows.OrderByDescending(item => item.IsForeground).ThenByDescending(item => item.IsGame)
            .ThenBy(item => item.DisplayName, StringComparer.CurrentCultureIgnoreCase).ToArray();
    }

    public Task<IReadOnlyList<CaptureTargetInfo>> GetWindowsAsync() => Task.Run(GetWindows);
    public Task<IReadOnlyList<CaptureTargetInfo>> GetMonitorsAsync() => Task.Run(GetMonitors);
    public CaptureTargetInfo? GetForegroundWindow() => TryGetWindow(Native.GetForegroundWindow(), Native.GetForegroundWindow(), out var info) ? info : null;

    public CaptureTargetInfo? SelectAutomaticTarget(IReadOnlyList<CaptureTargetInfo> windows, string? currentId,
        bool autoDetectGames = true)
    {
        var id = AutomaticCapturePolicy.Select(windows.Select(item => new CaptureSelectionCandidate(item.Id, true,
            item.IsGame, item.IsForeground, item.IsMinimized)).ToArray(), currentId, autoDetectGames: autoDetectGames);
        return windows.FirstOrDefault(item => item.Id == id);
    }

    public bool IsEligible(CaptureTarget target) => TryGetTarget(target, out _);

    public bool TryGetTarget(CaptureTarget target, out CaptureTargetInfo? current)
    {
        current = null;
        if (target.Kind == CaptureTargetKind.Monitor)
        {
            current = GetMonitors().FirstOrDefault(item => item.Target.Handle == target.Handle &&
                (string.IsNullOrEmpty(target.DeviceId) || string.Equals(item.DeviceId, target.DeviceId, StringComparison.OrdinalIgnoreCase)));
            return current is not null;
        }
        if (target.Kind != CaptureTargetKind.Window || !TryGetWindow(target.Handle, Native.GetForegroundWindow(), out current)) return false;
        return (target.ProcessId == 0 || target.ProcessId == current!.Target.ProcessId) &&
            (target.ProcessCreated == 0 || target.ProcessCreated == current!.Target.ProcessCreated);
    }

    public (int Width, int Height) GetCaptureSize(CaptureTarget target)
    {
        if (!TryGetTarget(target, out var current)) throw new InvalidOperationException("The selected capture source is no longer available. Choose another source.");
        if (current!.IsMinimized) throw new InvalidOperationException("Restore the selected window before starting capture.");
        var item = target.Kind == CaptureTargetKind.Monitor
            ? GraphicsCaptureItemFactory.CreateForMonitor(target.Handle) : GraphicsCaptureItemFactory.CreateForWindow(target.Handle);
        if (item.Size.Width <= 1 || item.Size.Height <= 1) throw new InvalidOperationException("The selected capture source has no visible picture.");
        return (item.Size.Width, item.Size.Height);
    }

    private bool TryGetWindow(nint window, nint foreground, out CaptureTargetInfo? result)
    {
        result = null;
        if (window == 0 || !Native.IsWindow(window) || !Native.IsWindowVisible(window) || Native.GetWindowTextLength(window) == 0) return false;
        Native.GetWindowThreadProcessId(window, out var pid);
        if (pid == 0 || pid == Environment.ProcessId) return false;
        var style = Native.GetWindowLongPtr(window, -20).ToInt64();
        if ((style & 0x80) != 0 || Native.GetWindow(window, 4) != 0 && (style & 0x40000) == 0) return false;
        if (Native.DwmGetWindowAttribute(window, 14, out int cloaked, sizeof(int)) == 0 && cloaked != 0) return false;
        if (Native.GetWindowDisplayAffinity(window, out var affinity) && affinity != 0) return false;
        if (!Native.GetWindowRect(window, out var bounds) || !Native.GetClientRect(window, out var client) || client.Right <= 1 || client.Bottom <= 1) return false;
        var title = new StringBuilder(Math.Min(2048, Native.GetWindowTextLength(window) + 1));
        Native.GetWindowText(window, title, title.Capacity);
        var className = new StringBuilder(256);
        Native.GetClassName(window, className, className.Capacity);
        var process = ReadProcess(pid);
        if (process is null) return false;
        var minimized = Native.IsIconic(window);
        var monitor = Native.MonitorFromWindow(window, 2);
        var monitorInfo = new Native.MonitorInfo { Size = (uint)Marshal.SizeOf<Native.MonitorInfo>() };
        var fullscreen = !minimized && Native.GetMonitorInfo(monitor, ref monitorInfo) &&
            Math.Abs(bounds.Left - monitorInfo.Monitor.Left) <= 12 && Math.Abs(bounds.Top - monitorInfo.Monitor.Top) <= 12 &&
            Math.Abs(bounds.Right - monitorInfo.Monitor.Right) <= 12 && Math.Abs(bounds.Bottom - monitorInfo.Monitor.Bottom) <= 12;
        var game = CaptureGamePolicy.Classify(new(process.Name));
        if (!game.IsGame && !CaptureGamePolicy.IsExcludedProcess(process.Name))
        {
            var files = ReadGameFiles(process.Path);
            game = CaptureGamePolicy.Classify(new(process.Name, className.ToString(), files.Engine, files.Renderer, files.GameLocation, fullscreen));
        }
        var display = title.ToString();
        var target = new CaptureTarget { Kind = CaptureTargetKind.Window, Handle = window, DisplayName = display,
            ProcessId = pid, ProcessCreated = process.Created };
        result = new(target, $"window:{pid}:{process.Created}:{window:X}", display, process.Name, display,
            bounds.Right - bounds.Left, bounds.Bottom - bounds.Top, IsForeground: window == foreground,
            IsMinimized: minimized, IsGame: game.IsGame, GameReason: game.Reason);
        return true;
    }

    private ProcessInfo? ReadProcess(uint pid)
    {
        var handle = Native.OpenProcess(0x1000, false, pid);
        if (handle == 0) return null;
        try
        {
            if (!Native.GetProcessTimes(handle, out var created, out _, out _, out _)) return null;
            if (_processes.TryGetValue(pid, out var existing) && existing.Created == created) return existing;
            var path = new StringBuilder(32768);
            var length = path.Capacity;
            if (!Native.QueryFullProcessImageName(handle, 0, path, ref length)) return null;
            var fullPath = path.ToString();
            var info = new ProcessInfo(fullPath, Path.GetFileNameWithoutExtension(fullPath), created);
            if (_processes.Count > 512) _processes.Clear();
            _processes[pid] = info;
            return info;
        }
        finally { Native.CloseHandle(handle); }
    }

    private GameFiles ReadGameFiles(string path)
    {
        if (_gameFiles.TryGetValue(path, out var cached) && DateTime.UtcNow - cached.CheckedUtc < TimeSpan.FromMinutes(2)) return cached;
        var directory = Path.GetDirectoryName(path) ?? string.Empty;
        var stem = Path.GetFileNameWithoutExtension(path);
        var normalized = path.Replace('/', '\\');
        var gameLocation = normalized.Contains("\\steamapps\\common\\", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("\\Epic Games\\", StringComparison.OrdinalIgnoreCase) || normalized.Contains("\\EpicGames\\", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("\\XboxGames\\", StringComparison.OrdinalIgnoreCase) || normalized.Contains("\\GOG Games\\", StringComparison.OrdinalIgnoreCase);
        var engine = File.Exists(Path.Combine(directory, "UnityPlayer.dll")) &&
            File.Exists(Path.Combine(directory, stem + "_Data", "globalgamemanagers"));
        var parent = directory;
        for (var depth = 0; !engine && depth < 4 && parent.Length > 0; depth++)
        {
            engine = Directory.Exists(Path.Combine(parent, "Content", "Paks")) &&
                (stem.EndsWith("-Win64-Shipping", StringComparison.OrdinalIgnoreCase) || Directory.Exists(Path.Combine(parent, "Engine")));
            parent = Path.GetDirectoryName(parent) ?? string.Empty;
        }
        var evidence = new GameFiles(engine, !engine && gameLocation && HasRendererImports(path), gameLocation, DateTime.UtcNow);
        if (_gameFiles.Count > 512) _gameFiles.Clear();
        _gameFiles[path] = evidence;
        return evidence;
    }

    private static bool HasRendererImports(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new PEReader(stream);
            var imports = reader.PEHeaders.PEHeader?.ImportTableDirectory;
            if (imports is null || imports.Value.RelativeVirtualAddress == 0) return false;
            var block = reader.GetSectionData(imports.Value.RelativeVirtualAddress);
            var bytes = block.GetContent(0, Math.Min(block.Length, Math.Min(imports.Value.Size, 20 * 256)));
            for (var offset = 0; offset + 20 <= bytes.Length; offset += 20)
            {
                var rva = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(offset + 12, 4));
                if (rva == 0) break;
                var nameBlock = reader.GetSectionData(rva);
                var nameBytes = nameBlock.GetContent(0, Math.Min(256, nameBlock.Length));
                var length = nameBytes.IndexOf((byte)0);
                var name = System.Text.Encoding.ASCII.GetString(nameBytes.AsSpan(0, length < 0 ? nameBytes.Length : length)).ToLowerInvariant();
                if (name is "d3d9.dll" or "d3d11.dll" or "d3d12.dll" or "dxgi.dll" or "vulkan-1.dll" or "opengl32.dll") return true;
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or BadImageFormatException or ArgumentOutOfRangeException) { }
        return false;
    }

    private static class Native
    {
        [StructLayout(LayoutKind.Sequential)] public struct Rect { public int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] public struct MonitorInfo
        {
            public uint Size; public Rect Monitor, Work; public uint Flags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Device;
        }
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] public struct DisplayDevice
        {
            public uint Size;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
            public uint StateFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceId;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
        }
        public delegate bool MonitorCallback(nint monitor, nint dc, ref Rect rectangle, nint state);
        public delegate bool WindowCallback(nint window, nint state);
        [DllImport("user32.dll")] public static extern bool EnumDisplayMonitors(nint dc, nint clip, MonitorCallback callback, nint data);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern bool EnumDisplayDevices(string? device, uint index, ref DisplayDevice info, uint flags);
        [DllImport("user32.dll")] public static extern bool EnumWindows(WindowCallback callback, nint data);
        [DllImport("user32.dll")] public static extern nint GetForegroundWindow();
        [DllImport("user32.dll")] public static extern bool IsWindow(nint window);
        [DllImport("user32.dll")] public static extern bool IsWindowVisible(nint window);
        [DllImport("user32.dll")] public static extern bool IsIconic(nint window);
        [DllImport("user32.dll")] public static extern nint GetWindow(nint window, uint command);
        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] public static extern nint GetWindowLongPtr(nint window, int index);
        [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(nint window, out uint pid);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowTextLength(nint window);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowText(nint window, StringBuilder value, int capacity);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassName(nint window, StringBuilder value, int capacity);
        [DllImport("user32.dll")] public static extern bool GetWindowRect(nint window, out Rect rectangle);
        [DllImport("user32.dll")] public static extern bool GetClientRect(nint window, out Rect rectangle);
        [DllImport("user32.dll")] public static extern nint MonitorFromWindow(nint window, uint flags);
        [DllImport("user32.dll")] public static extern bool GetWindowDisplayAffinity(nint window, out uint affinity);
        [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(nint window, uint attribute, out int value, int size);
        [DllImport("kernel32.dll")] public static extern nint OpenProcess(uint access, bool inherit, uint pid);
        [DllImport("kernel32.dll")] public static extern bool CloseHandle(nint handle);
        [DllImport("kernel32.dll")] public static extern bool GetProcessTimes(nint process, out long created, out long exited, out long kernel, out long user);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] public static extern bool QueryFullProcessImageName(nint process, uint flags, StringBuilder name, ref int length);
    }
}
