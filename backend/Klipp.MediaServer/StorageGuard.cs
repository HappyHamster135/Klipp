namespace Klipp.MediaServer;

public interface IStorageGuard
{
    void Verify();
    void VerifySpace(long requiredBytes);
}

/// <summary>No environment switch can bypass the production mount check.</summary>
public sealed class LinuxStorageGuard(MediaOptions options) : IStorageGuard
{
    public void Verify()
    {
        if (!OperatingSystem.IsLinux()) throw MediaException.StorageUnavailable();
        var mount = Path.TrimEndingDirectorySeparator(Path.GetFullPath(options.MountPoint));
        try
        {
            RejectLinks(mount);
            RejectLinks(options.StorageRoot);
            if (!MatchesMount(File.ReadLines("/proc/self/mountinfo"), mount, options.ExpectedMountSource))
                throw MediaException.StorageUnavailable();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { throw MediaException.StorageUnavailable(); }
    }

    public void VerifySpace(long requiredBytes)
    {
        Verify();
        try
        {
            if (new DriveInfo(options.MountPoint).AvailableFreeSpace < options.MinimumFreeBytes + requiredBytes)
                throw new MediaException(507, "storage_full", "Clip storage is almost full. Try again later.");
        }
        catch (IOException) { throw MediaException.StorageUnavailable(); }
    }

    public static bool MatchesMount(IEnumerable<string> lines, string mountPoint, string expectedSource)
    {
        foreach (var line in lines)
        {
            var pieces = line.Split(' ');
            var separator = Array.IndexOf(pieces, "-");
            if (separator < 6 || separator + 3 >= pieces.Length || pieces.Length < 10) continue;
            if (Unescape(pieces[4]) != mountPoint || Unescape(pieces[separator + 2]) != expectedSource) continue;
            return pieces[separator + 1] == "ext4" && pieces[5].Split(',').Contains("rw")
                && pieces[5].Split(',').Contains("nodev") && pieces[5].Split(',').Contains("nosuid")
                && pieces[5].Split(',').Contains("noexec");
        }
        return false;
    }

    public static void RejectLinks(string path)
    {
        var current = Path.GetFullPath(path);
        while (!string.IsNullOrEmpty(current))
        {
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw MediaException.StorageUnavailable();
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
            current = Path.GetDirectoryName(current);
        }
    }

    private static string Unescape(string value)
    {
        return value.Replace("\\040", " ", StringComparison.Ordinal)
            .Replace("\\011", "\t", StringComparison.Ordinal).Replace("\\012", "\n", StringComparison.Ordinal)
            .Replace("\\134", "\\", StringComparison.Ordinal);
    }
}
