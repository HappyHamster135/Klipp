using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Klipp.Desktop.Services;

public enum CaptureHealthSeverity
{
    Healthy,
    Warning,
    Critical
}

public sealed record CaptureRuntimeHealth(
    bool Active,
    bool CaptureRunning,
    bool EncoderRunning,
    bool AudioConfigured,
    bool AudioSignal,
    long CapturedFrames,
    long DeliveredFrames,
    long DroppedFrames,
    long InvalidFrames,
    long EncodedFrames,
    double ActiveSeconds,
    DateTime? LastFrameUtc,
    int FrameWidth,
    int FrameHeight,
    int TargetFrameRate,
    string EncoderName,
    string? FailureMessage)
{
    public double MeasuredFrameRate => ActiveSeconds <= 0.5
        ? 0
        : DeliveredFrames / ActiveSeconds;

    public double DropRate => CapturedFrames + InvalidFrames <= 0
        ? 0
        : Math.Clamp((DroppedFrames + InvalidFrames) / (double)(CapturedFrames + InvalidFrames), 0, 1);

    public bool CaptureStalled => Active && ActiveSeconds > 3 &&
        (!LastFrameUtc.HasValue || DateTime.UtcNow - LastFrameUtc.Value > TimeSpan.FromSeconds(3));

    public CaptureHealthSeverity Severity
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(FailureMessage) || CaptureStalled ||
                (Active && ActiveSeconds > 3 && (!CaptureRunning || !EncoderRunning)))
            {
                return CaptureHealthSeverity.Critical;
            }

            if (Active && ActiveSeconds > 5 && (DropRate >= 0.05 || (AudioConfigured && !AudioSignal)))
                return CaptureHealthSeverity.Warning;

            return CaptureHealthSeverity.Healthy;
        }
    }
}

public sealed record CaptureStorageHealth(
    string DriveName,
    long AvailableBytes,
    long TotalBytes,
    double EstimatedSecondsRemaining,
    CaptureHealthSeverity Severity,
    string? FailureMessage);

public sealed class CaptureHealthService
{
    private const long WarningBytes = 10L * 1024 * 1024 * 1024;
    private const long CriticalBytes = 2L * 1024 * 1024 * 1024;

    public CaptureStorageHealth GetStorageHealth(
        IEnumerable<string> paths,
        int videoBitrateMbps)
    {
        try
        {
            var drives = paths
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Select(Path.GetFullPath)
                .Select(Path.GetPathRoot)
                .Where(root => !string.IsNullOrWhiteSpace(root))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(root => new DriveInfo(root!))
                .Where(drive => drive.IsReady)
                .ToList();
            if (drives.Count == 0)
            {
                return new CaptureStorageHealth(
                    string.Empty,
                    0,
                    0,
                    0,
                    CaptureHealthSeverity.Critical,
                    "No writable capture drive was found.");
            }

            var limitingDrive = drives.OrderBy(drive => drive.AvailableFreeSpace).First();
            return EvaluateStorage(
                limitingDrive.Name,
                limitingDrive.AvailableFreeSpace,
                limitingDrive.TotalSize,
                videoBitrateMbps);
        }
        catch (Exception ex)
        {
            return new CaptureStorageHealth(
                string.Empty,
                0,
                0,
                0,
                CaptureHealthSeverity.Critical,
                ex.Message);
        }
    }

    public static CaptureStorageHealth EvaluateStorage(
        string driveName,
        long availableBytes,
        long totalBytes,
        int videoBitrateMbps)
    {
        availableBytes = Math.Max(0, availableBytes);
        totalBytes = Math.Max(0, totalBytes);
        var totalBitsPerSecond = Math.Max(1, videoBitrateMbps) * 1_000_000d + 128_000d;
        var estimatedSeconds = availableBytes * 8d / totalBitsPerSecond;
        var severity = availableBytes <= CriticalBytes || estimatedSeconds <= TimeSpan.FromMinutes(10).TotalSeconds
            ? CaptureHealthSeverity.Critical
            : availableBytes <= WarningBytes || estimatedSeconds <= TimeSpan.FromHours(1).TotalSeconds
                ? CaptureHealthSeverity.Warning
                : CaptureHealthSeverity.Healthy;
        return new CaptureStorageHealth(
            driveName,
            availableBytes,
            totalBytes,
            estimatedSeconds,
            severity,
            null);
    }
}
