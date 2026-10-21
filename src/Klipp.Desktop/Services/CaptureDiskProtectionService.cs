using System;
using System.IO;
using Klipp.Core.Capture;

namespace Klipp.Desktop.Services;

public sealed record CaptureDiskProtectionSnapshot(bool ShouldStop, string Detail, long AvailableBytes, long RequiredBytes);

/// <summary>Reads only volume counters, never scans media or frees user files.</summary>
public sealed class CaptureDiskProtectionService(Func<string, long>? availableBytes = null)
{
    private readonly Func<string, long> _availableBytes = availableBytes ?? (root => new DriveInfo(root).AvailableFreeSpace);

    public CaptureDiskProtectionSnapshot Inspect(string temporaryPath, string outputPath, CaptureDiskReserve reserve)
    {
        try
        {
            var temporaryRoot = Path.GetPathRoot(Path.GetFullPath(temporaryPath))!;
            var outputRoot = Path.GetPathRoot(Path.GetFullPath(outputPath))!;
            var sameVolume = string.Equals(temporaryRoot, outputRoot, StringComparison.OrdinalIgnoreCase);
            var temporaryAvailable = _availableBytes(temporaryRoot);
            var temporaryRequired = CaptureDiskProtectionPolicy.RequiredBytes(reserve, sameVolume, true);
            if (CaptureDiskProtectionPolicy.ShouldStop(temporaryAvailable, temporaryRequired))
                return new(true, $"Space is needed on {temporaryRoot} to finish saving this capture.", temporaryAvailable, temporaryRequired);
            if (sameVolume) return new(false, string.Empty, temporaryAvailable, temporaryRequired);
            var outputAvailable = _availableBytes(outputRoot);
            var outputRequired = CaptureDiskProtectionPolicy.RequiredBytes(reserve, false, false);
            return new(CaptureDiskProtectionPolicy.ShouldStop(outputAvailable, outputRequired),
                $"Space is needed on {outputRoot} to finish saving this capture.", outputAvailable, outputRequired);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return new(true, $"The capture drive became unavailable. {exception.Message}", 0, 0);
        }
    }
}
