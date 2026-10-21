using System;
using System.Runtime.InteropServices;

namespace Klipp.Desktop.Services;

public sealed record CaptureRecommendation(
    string ResolutionMode,
    int FrameRate,
    int BitrateMbps,
    string Summary,
    string Reason);

public sealed class CaptureRecommendationService
{
    [DllImport("user32.dll")]
    private static extern nint MonitorFromPoint(POINT pt, uint dwFlags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetMonitorInfo(nint hMonitor, ref MONITORINFO lpmi);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

    private const uint MonitorDefaultToPrimary = 0x00000001;

    public CaptureRecommendation GetRecommendation()
    {
        var processorCount = Math.Max(1, Environment.ProcessorCount);
        var memoryGb = GetInstalledMemoryGb();
        var (screenWidth, screenHeight) = GetPrimaryMonitorSize();
        var highResolutionScreen = screenHeight >= 1200 || screenWidth >= 2560;

        if (processorCount <= 4 || memoryGb <= 8)
        {
            return new CaptureRecommendation(
                ResolutionMode: "540p",
                FrameRate: 15,
                BitrateMbps: 5,
                Summary: "540p max - 15 fps - 5 Mbps",
                Reason: $"Eco profile for {processorCount} logical CPU cores and {memoryGb} GB RAM. Keeps background recording light on system resources.");
        }

        if (processorCount <= 8 || memoryGb <= 16 || highResolutionScreen)
        {
            return new CaptureRecommendation(
                ResolutionMode: "720p",
                FrameRate: 24,
                BitrateMbps: 8,
                Summary: "720p max - 24 fps - 8 Mbps",
                Reason: $"Stable profile for {processorCount} logical CPU cores and a {screenWidth}x{screenHeight} display. A lower frame rate reduces capture overhead.");
        }

        if (processorCount <= 12)
        {
            return new CaptureRecommendation(
                ResolutionMode: "1080p",
                FrameRate: 30,
                BitrateMbps: 16,
                Summary: "1080p max - 30 fps - 16 Mbps",
                Reason: $"Balanced profile for {processorCount} logical CPU cores. Sharp recordings with room for your other apps.");
        }

        return new CaptureRecommendation(
            ResolutionMode: "1080p",
            FrameRate: 30,
            BitrateMbps: 16,
            Summary: "1080p max - 30 fps - 16 Mbps",
            Reason: $"Fast profile for {processorCount} logical CPU cores. Choose Native resolution or 60 fps manually for smoother motion.");
    }

    private static (int Width, int Height) GetPrimaryMonitorSize()
    {
        var monitor = MonitorFromPoint(new POINT(), MonitorDefaultToPrimary);
        var info = new MONITORINFO { cbSize = (uint)Marshal.SizeOf<MONITORINFO>() };

        if (monitor == 0 || !GetMonitorInfo(monitor, ref info))
            return (1920, 1080);

        return (
            Math.Max(1, info.rcMonitor.Right - info.rcMonitor.Left),
            Math.Max(1, info.rcMonitor.Bottom - info.rcMonitor.Top));
    }

    private static int GetInstalledMemoryGb()
    {
        var status = new MEMORYSTATUSEX
        {
            dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>()
        };

        if (!GlobalMemoryStatusEx(ref status))
            return 16;

        return (int)Math.Max(1, Math.Round(status.ullTotalPhys / 1024d / 1024d / 1024d));
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int x;
        public int y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public uint cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }
}
