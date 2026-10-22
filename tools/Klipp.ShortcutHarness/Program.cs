using System.ComponentModel;
using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Klipp.Desktop.Services;
using Microsoft.Win32.SafeHandles;

internal static class Program
{
    private const string AppId = "HappyHamster135.Klipp";
    private static int _checks;
    private static string _root = null!;
    private static string _iconSource = null!;

    [STAThread]
    private static int Main(string[] args)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Native shell-link checks require Windows.");
        if (args.Contains("--verify-release-icons")) return ReleaseIconChecks.Run(args);
        if (args.Length == 2 && args[0] == "--inspect-link")
        {
            var path = Path.GetFullPath(args[1]);
            using var link = new NativeShellLink(path);
            Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new
            {
                Path = path, link.Target, link.WorkingDirectory, link.IconPath, link.IconIndex, link.AppUserModelId,
                CanonicalTarget = CanonicalPath.TryResolveFile(link.Target),
                DirectProperty = IndependentShell.ReadDirectAppUserModelId(link),
                ParsedProperty = IndependentShell.ReadAppUserModelId(path)
            }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
            return 0;
        }
        _root = Path.Combine(Path.GetTempPath(), "Klipp-Shortcut-QA-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _iconSource = FindIcon();

        RepairBrokenShortcut();
        RepairVectorIdentityShortcut();
        PreserveRemovedAndUnrelatedShortcuts();
        RecoverAfterCorruptLink();
        RepairRenamedPinnedLink();
        SkipInvalidAndPortableInstallations();
        ResolveRedirectedInstallation();
        HandleLongUnicodePaths();
        RetainIconAfterVersionReplacement();

        Console.WriteLine($"PASS: {_checks} native shortcut checks. Fixtures: {_root}");
        return 0;
    }

    private static void RepairBrokenShortcut()
    {
        var install = NewInstallation("valid install");
        var linkPath = LinkPath("Desktop", "Klipp.lnk");
        using (var link = CreateLink(linkPath, install))
        {
            link.IconPath = Path.Combine(_root, "missing logical AppData", "Klipp.Desktop.exe");
            link.IconIndex = 17;
            link.WorkingDirectory = _root;
            link.Arguments = "--example \"a folder with spaces\"";
            link.Description = "A custom replay shortcut";
            link.ShowCommand = 3;
            link.HotKey = 0x0243;
            link.Save();
        }
        var notifications = new List<string>();
        Require(ShortcutIconService.RepairInstallation(install, [linkPath], notifications.Add) == 1,
            "An owned shortcut with a missing icon path was not repaired.");
        Require(notifications.Count == 1 && SamePath(notifications[0], linkPath), "The repaired shell entry was not refreshed exactly once.");
        using (var link = new NativeShellLink(linkPath))
        {
            AssertIconMetadata(link, install);
            Require(link.Arguments == "--example \"a folder with spaces\"", "Shortcut arguments changed.");
            Require(link.Description == "A custom replay shortcut", "Shortcut description changed.");
            Require(link.ShowCommand == 3 && link.HotKey == 0x0243, "Shortcut window mode or hotkey changed.");
            Require(NativeIcon.ReadPixels(link.IconPath).SequenceEqual(NativeIcon.ReadPixels(_iconSource)),
                "Windows loaded different pixels from the repaired icon resource.");
        }
        AssertIndependentMetadata(linkPath, install);

        var hash = Hash(linkPath);
        var timestamp = File.GetLastWriteTimeUtc(linkPath);
        notifications.Clear();
        Require(ShortcutIconService.RepairInstallation(install, [linkPath], notifications.Add) == 0,
            "A correct shortcut was rewritten on a repeated startup.");
        Require(Hash(linkPath) == hash && File.GetLastWriteTimeUtc(linkPath) == timestamp,
            "Repeated repair changed a correct shortcut file.");
        Require(notifications.Count == 0, "Repeated repair unnecessarily refreshed the shell.");
    }

    private static void PreserveRemovedAndUnrelatedShortcuts()
    {
        var install = NewInstallation("owned install");
        var removed = LinkPath("Removed", "Klipp.lnk");
        Require(ShortcutIconService.RepairInstallation(install, [removed]) == 0 && !File.Exists(removed),
            "A shortcut removed by its user was recreated.");

        var sibling = NewInstallation("owned install extra");
        var foreign = LinkPath("Other application", "Klipp.lnk");
        using (var link = CreateLink(foreign, sibling)) link.Save();
        var foreignHash = Hash(foreign);
        Require(ShortcutIconService.RepairInstallation(install, [foreign]) == 0 && Hash(foreign) == foreignHash,
            "A similarly named foreign installation was changed.");

        var wrongIdentity = LinkPath("Wrong identity", "Klipp.lnk");
        using (var link = CreateLink(wrongIdentity, install))
        {
            link.AppUserModelId = "AnotherCompany.AnotherApp";
            link.Save();
        }
        var wrongIdentityHash = Hash(wrongIdentity);
        Require(IndependentShell.ReadAppUserModelId(wrongIdentity) == "AnotherCompany.AnotherApp",
            "The foreign-identity fixture was not stored with its independent shell identity.");
        Require(ShortcutIconService.RepairInstallation(install, [wrongIdentity]) == 0 && Hash(wrongIdentity) == wrongIdentityHash,
            "A shortcut belonging to another application identity was changed.");
        Require(IndependentShell.ReadAppUserModelId(wrongIdentity) == "AnotherCompany.AnotherApp",
            "The independent shell property reader observed a changed foreign identity.");

        var missingForeign = LinkPath("Missing foreign target", "Klipp.lnk");
        using (var link = CreateLink(missingForeign, install))
        {
            link.Target = Path.Combine(_root, "uninstalled other copy", "current", "Klipp.Desktop.exe");
            link.Save();
        }
        var missingForeignHash = Hash(missingForeign);
        Require(ShortcutIconService.RepairInstallation(install, [missingForeign]) == 0 && Hash(missingForeign) == missingForeignHash,
            "An unresolvable target from another installation was claimed by filename or AUMID alone.");

        var missingLocal = LinkPath("Missing nonstandard local target", "Klipp.lnk");
        using (var link = CreateLink(missingLocal, install))
        {
            link.Target = Path.Combine(install, "old-current", "Klipp.Desktop.exe");
            link.Save();
        }
        var missingLocalHash = Hash(missingLocal);
        Require(ShortcutIconService.RepairInstallation(install, [missingLocal]) == 0 && Hash(missingLocal) == missingLocalHash,
            "An unresolvable nonstandard target was guessed to belong to the current executable.");

        var emptyIdentity = LinkPath("Missing identity", "Klipp.lnk");
        using (var link = NativeShellLink.Create(emptyIdentity))
        {
            link.Target = Exe(install);
            link.IconPath = Path.Combine(_root, "broken.ico");
            link.Save();
        }
        var emptyIdentityHash = Hash(emptyIdentity);
        Require(ShortcutIconService.RepairInstallation(install, [emptyIdentity]) == 0 && Hash(emptyIdentity) == emptyIdentityHash,
            "A link without the Klipp application identity was claimed.");
    }

    private static void RepairVectorIdentityShortcut()
    {
        var install = NewInstallation("native vector identity");
        var linkPath = LinkPath("Velopack vector identity", "Klipp.lnk");
        using (var link = CreateLink(linkPath, install))
        {
            link.Save();
        }
        IndependentShell.WriteVectorPropertyBlock(linkPath, AppId);
        using (var rawLink = new NativeShellLink(linkPath))
        {
            var raw = System.Text.Json.JsonSerializer.SerializeToElement(IndependentShell.ReadDirectAppUserModelId(rawLink));
            Require(raw.GetProperty("VariantType").GetInt32() == 0x101F && raw.GetProperty("Count").GetInt32() == 1,
                "The native-installer fixture did not preserve its one-element string-vector representation.");
        }
        Require(IndependentShell.ReadAppUserModelId(linkPath) == AppId,
            "Windows' shell property reader did not normalize the native vector application identity.");
        Require(ShortcutIconService.RepairInstallation(install, [linkPath]) == 1,
            "An actual native-installer string-vector application identity prevented shortcut repair.");
        using (var repaired = new NativeShellLink(linkPath)) AssertIconMetadata(repaired, install);
        AssertIndependentMetadata(linkPath, install);

        var ambiguous = LinkPath("Ambiguous vector identity", "Klipp.lnk");
        using (var link = CreateLink(ambiguous, install))
        {
            link.Save();
        }
        IndependentShell.WriteVectorPropertyBlock(ambiguous, AppId, "AnotherCompany.App");
        using (var rawLink = new NativeShellLink(ambiguous))
        {
            var raw = System.Text.Json.JsonSerializer.SerializeToElement(IndependentShell.ReadDirectAppUserModelId(rawLink));
            Require(raw.GetProperty("VariantType").GetInt32() == 0x101F && raw.GetProperty("Count").GetInt32() == 2,
                "The ambiguous fixture did not preserve its two-element string-vector representation.");
        }
        var original = Hash(ambiguous);
        Require(ShortcutIconService.RepairInstallation(install, [ambiguous]) == 0 && Hash(ambiguous) == original,
            "A multi-value application identity was incorrectly claimed.");
    }

    private static void RecoverAfterCorruptLink()
    {
        var install = NewInstallation("corrupt link install");
        var corrupt = LinkPath("Damaged shortcuts", "broken.lnk");
        File.WriteAllBytes(corrupt, [1, 2, 3, 4, 5]);
        var good = LinkPath("Damaged shortcuts", "good.lnk");
        using (var link = CreateLink(good, install)) link.Save();
        var corruptHash = Hash(corrupt);
        Require(ShortcutIconService.RepairInstallation(install, [corrupt, good]) == 1,
            "One corrupt shell link prevented repairing a later valid link.");
        Require(Hash(corrupt) == corruptHash, "The corrupt shortcut was overwritten.");
        using var repaired = new NativeShellLink(good);
        AssertIconMetadata(repaired, install);
    }

    private static void RepairRenamedPinnedLink()
    {
        var install = NewInstallation("pin install");
        var pinned = LinkPath("User Pinned TaskBar fixture", "My replay recorder.lnk");
        using (var link = CreateLink(pinned, install))
        {
            link.Target = Path.Combine(install, "Klipp.exe");
            link.Save();
        }
        Require(ShortcutIconService.RepairInstallation(install, [pinned]) == 1,
            "An owned renamed pinned shortcut targeting the stable launcher was not repaired.");
        using var repaired = new NativeShellLink(pinned);
        AssertIconMetadata(repaired, install, launcherTarget: true);
        AssertIndependentMetadata(pinned, install, launcherTarget: true);
    }

    private static void SkipInvalidAndPortableInstallations()
    {
        var cases = new (string Name, Action<string> Change, bool Portable)[]
        {
            ("portable", _ => { }, true),
            ("missing updater", install => File.Delete(Path.Combine(install, "Update.exe")), false),
            ("missing manifest", install => File.Delete(Path.Combine(install, "current", "sq.version")), false),
            ("wrong application", install => WriteManifest(install, "AnotherCompany.App", "Klipp.Desktop.exe"), false),
            ("wrong executable", install => WriteManifest(install, AppId, "Other.exe"), false),
            ("missing icon", install => File.Delete(Icon(install)), false),
            ("missing app", install => File.Delete(Exe(install)), false),
            ("invalid manifest", install => File.WriteAllText(Path.Combine(install, "current", "sq.version"), "not xml"), false)
        };
        foreach (var item in cases)
        {
            var install = NewInstallation(item.Name);
            var linkPath = LinkPath(item.Name + " links", "Klipp.lnk");
            using (var link = CreateLink(linkPath, install)) link.Save();
            item.Change(install);
            var hash = Hash(linkPath);
            var notifications = new List<string>();
            Require(ShortcutIconService.RepairInstallation(install, [linkPath], notifications.Add, item.Portable) == 0,
                $"The {item.Name} installation modified a shortcut.");
            Require(Hash(linkPath) == hash && notifications.Count == 0,
                $"The {item.Name} installation changed or refreshed an unrelated shell entry.");
        }
    }

    private static void ResolveRedirectedInstallation()
    {
        var physicalInstall = NewInstallation("physical redirected install");
        var alias = Path.Combine(_root, "logical AppData alias");
        Junction.Create(alias, physicalInstall);
        var linkPath = LinkPath("redirected Desktop", "Klipp.lnk");
        using (var link = CreateLink(linkPath, alias)) link.Save();
        Require(ShortcutIconService.RepairInstallation(alias, [linkPath]) == 1,
            "A shortcut created through a redirected install path was not repaired.");
        using var repaired = new NativeShellLink(linkPath);
        AssertIconMetadata(repaired, physicalInstall);
        Require(!repaired.IconPath.Contains("logical AppData alias", StringComparison.OrdinalIgnoreCase),
            "The repaired icon still depends on a redirected alias path.");
        Require(!repaired.Target.Contains("logical AppData alias", StringComparison.OrdinalIgnoreCase),
            "The repaired target still depends on a redirected alias path.");
    }

    private static void HandleLongUnicodePaths()
    {
        var install = NewInstallation(Path.Combine("Åsa 日本語 captured games", "Klipp"));
        var linkPath = LinkPath("Unicode shortcuts Åsa 日本語", "My game clips.lnk");
        using (var link = CreateLink(linkPath, install)) link.Save();
        Require(ShortcutIconService.RepairInstallation(install, [linkPath]) == 1,
            "The Unicode installation path was not repaired.");
        using var repaired = new NativeShellLink(linkPath);
        AssertIconMetadata(repaired, install);
        AssertIndependentMetadata(linkPath, install);

        var longName = Path.Combine("Long file path Åsa 日本語", new string('x', 110), new string('y', 110),
            new string('z', 110), new string('w', 110), "Klipp");
        var longInstall = NewInstallation(longName);
        Require(Exe(longInstall).Length > 512, "The long file-path fixture did not exercise native buffer growth.");
        var canonicalLongFile = CanonicalPath.TryResolveFile(Exe(longInstall));
        Require(canonicalLongFile is not null && canonicalLongFile.Length > 512 &&
            File.ReadAllText(canonicalLongFile) == "disposable desktop executable fixture",
            "Canonical file path resolution failed or truncated its resized buffer.");

        // Windows Shell's SetPath can reject targets longer than MAX_PATH even
        // when the filesystem accepts them. Check the actual platform boundary;
        // do not pretend a .lnk can represent every native file path.
        var longLinkPath = LinkPath("Native shell path limit", "Klipp.lnk");
        var nativePathSupported = false;
        using (var longLink = NativeShellLink.Create(longLinkPath))
        {
            try
            {
                longLink.Target = Exe(longInstall);
                longLink.AppUserModelId = AppId;
                longLink.IconPath = Path.Combine(_root, "missing icon.ico");
                longLink.Save();
                nativePathSupported = true;
            }
            catch (COMException)
            {
                Require(!File.Exists(longLinkPath), "Rejected native target unexpectedly produced a shortcut.");
            }
        }
        if (nativePathSupported)
        {
            var originalHash = Hash(longLinkPath);
            var changed = ShortcutIconService.RepairInstallation(longInstall, [longLinkPath]);
            if (changed == 1)
            {
                using var longLink = new NativeShellLink(longLinkPath);
                AssertIconMetadata(longLink, longInstall);
            }
            else Require(Hash(longLinkPath) == originalHash, "Unsupported long target was partially rewritten.");
        }
        else
        {
            Require(ShortcutIconService.RepairInstallation(longInstall, [longLinkPath]) == 0 && !File.Exists(longLinkPath),
                "Repair created a shortcut for an unrepresentable native shell target.");
            Console.WriteLine("INFO: Windows Shell rejects this >MAX_PATH target; canonical file resolution and safe skip passed.");
        }
    }

    private static void RetainIconAfterVersionReplacement()
    {
        var install = NewInstallation("update replacement install");
        var linkPath = LinkPath("Update Desktop", "Klipp.lnk");
        using (var link = CreateLink(linkPath, install)) link.Save();
        Require(ShortcutIconService.RepairInstallation(install, [linkPath]) == 1, "Initial update fixture was not repaired.");
        var previousPath = new NativeShellLink(linkPath);
        var stableIconPath = previousPath.IconPath;
        previousPath.Dispose();
        // Velopack replaces the current payload, then recreates its shortcuts.
        File.Copy(_iconSource, Icon(install), overwrite: true);
        WriteManifest(install, AppId, "Klipp.Desktop.exe", "99.0.1");
        using (var generated = CreateLink(linkPath, install))
        {
            generated.IconPath = Path.Combine(_root, "outdated install view", "Klipp.Desktop.exe");
            generated.Save();
        }
        Require(ShortcutIconService.RepairInstallation(install, [linkPath]) == 1,
            "Updater-created shortcut metadata was not repaired after replacing current files.");
        using var repaired = new NativeShellLink(linkPath);
        AssertIconMetadata(repaired, install);
        Require(SamePath(stableIconPath, repaired.IconPath), "The icon location was tied to a version-specific directory.");
        Require(NativeIcon.ReadPixels(repaired.IconPath).SequenceEqual(NativeIcon.ReadPixels(_iconSource)),
            "The updated shortcut no longer loads the Klipp icon.");
    }

    private static string NewInstallation(string name)
    {
        var install = Path.Combine(_root, "installations", name);
        Directory.CreateDirectory(Path.Combine(install, "current", "Assets"));
        // The service validates installation layout/manifest, not executable code.
        // Fixtures are never launched; real executable metadata is checked at packaging time.
        File.WriteAllText(Exe(install), "disposable desktop executable fixture");
        File.WriteAllText(Path.Combine(install, "Klipp.exe"), "disposable stable launcher fixture");
        File.WriteAllText(Path.Combine(install, "Update.exe"), "disposable updater fixture");
        File.Copy(_iconSource, Icon(install));
        WriteManifest(install, AppId, "Klipp.Desktop.exe");
        return install;
    }

    private static void WriteManifest(string install, string appId, string mainExe, string version = "99.0.0") =>
        File.WriteAllText(Path.Combine(install, "current", "sq.version"),
            $"<?xml version=\"1.0\"?><package xmlns=\"http://schemas.microsoft.com/packaging/2010/07/nuspec.xsd\"><metadata><id>{appId}</id><version>{version}</version><mainExe>{mainExe}</mainExe><shortcutAumid>{AppId}</shortcutAumid></metadata></package>");

    private static NativeShellLink CreateLink(string linkPath, string install)
    {
        var link = NativeShellLink.Create(linkPath);
        link.Target = Exe(install);
        link.WorkingDirectory = Path.Combine(install, "current");
        link.IconPath = Path.Combine(_root, "missing icon.ico");
        link.IconIndex = 0;
        link.AppUserModelId = AppId;
        return link;
    }

    private static void AssertIconMetadata(NativeShellLink link, string install, bool launcherTarget = false)
    {
        var expectedExe = CanonicalPath.TryResolveFile(Exe(install));
        var expectedTarget = launcherTarget ? CanonicalPath.TryResolveFile(Path.Combine(install, "Klipp.exe")) : expectedExe;
        var expectedIcon = CanonicalPath.TryResolveFile(Icon(install));
        Require(expectedExe is not null && expectedTarget is not null && expectedIcon is not null, "Fixture paths did not canonicalize.");
        Require(SamePath(link.Target, expectedTarget!), "The shell target does not preserve its canonical executable or launcher.");
        Require(SamePath(link.WorkingDirectory, Path.GetDirectoryName(expectedExe!)!), "The shell working directory is not canonical.");
        Require(SamePath(link.IconPath, expectedIcon!) && link.IconIndex == 0, "The shell icon does not point to the canonical ICO,0.");
        Require(link.AppUserModelId == AppId, "The shortcut's application identity changed.");
    }

    private static void AssertIndependentMetadata(string linkPath, string install, bool launcherTarget = false)
    {
        var expectedExe = CanonicalPath.TryResolveFile(Exe(install))!;
        var expectedTarget = launcherTarget ? CanonicalPath.TryResolveFile(Path.Combine(install, "Klipp.exe"))! : expectedExe;
        var expectedIcon = CanonicalPath.TryResolveFile(Icon(install))!;
        using var independent = new Velopack.Windows.ShellLink();
        independent.Open(linkPath, 0,
            Velopack.Windows.ShellLink.EShellLinkResolveFlags.SLR_NO_UI |
            Velopack.Windows.ShellLink.EShellLinkResolveFlags.SLR_NOUPDATE |
            Velopack.Windows.ShellLink.EShellLinkResolveFlags.SLR_NOSEARCH |
            Velopack.Windows.ShellLink.EShellLinkResolveFlags.SLR_NOTRACK |
            Velopack.Windows.ShellLink.EShellLinkResolveFlags.SLR_NOLINKINFO);
        Require(SamePath(independent.Target, expectedTarget), "Velopack's independent shell-link reader found an incorrect target.");
        Require(SamePath(independent.WorkingDirectory, Path.GetDirectoryName(expectedExe)!),
            "Velopack's independent shell-link reader found an incorrect working directory.");
        Require(SamePath(independent.IconPath, expectedIcon) && independent.IconIndex == 0,
            "Velopack's independent shell-link reader found an incorrect icon resource.");
        Require(IndependentShell.ReadAppUserModelId(linkPath) == AppId,
            "The independent Windows property-store reader found an incorrect application identity.");
    }

    private static string LinkPath(string directory, string name)
    {
        var folder = Path.Combine(_root, "shortcuts", directory);
        Directory.CreateDirectory(folder);
        return Path.Combine(folder, name);
    }

    private static string Exe(string install) => Path.Combine(install, "current", "Klipp.Desktop.exe");
    private static string Icon(string install) => Path.Combine(install, "current", "Assets", "Klipp.ico");
    private static bool SamePath(string first, string second) => string.Equals(Path.GetFullPath(first), Path.GetFullPath(second), StringComparison.OrdinalIgnoreCase);
    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
        _checks++;
    }

    private static string FindIcon()
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var icon = Path.Combine(directory.FullName, "src", "Klipp.Desktop", "Assets", "Klipp.ico");
            if (File.Exists(icon)) return icon;
        }
        throw new FileNotFoundException("The native shortcut harness requires the repository's Klipp.ico asset.");
    }

    private static class Junction
    {
        internal static void Create(string alias, string target)
        {
            var targetExe = CanonicalPath.TryResolveFile(Exe(target)) ?? throw new InvalidOperationException("Junction target is unavailable.");
            var physicalTarget = Path.GetDirectoryName(Path.GetDirectoryName(targetExe))!;
            var substitute = Encoding.Unicode.GetBytes(@"\??\" + physicalTarget);
            var printable = Encoding.Unicode.GetBytes(physicalTarget);
            var data = new byte[16 + substitute.Length + 2 + printable.Length + 2];
            using (var stream = new MemoryStream(data))
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write(0xA0000003u); // IO_REPARSE_TAG_MOUNT_POINT
                writer.Write(checked((ushort)(data.Length - 8)));
                writer.Write((ushort)0);
                writer.Write((ushort)0);
                writer.Write(checked((ushort)substitute.Length));
                writer.Write(checked((ushort)(substitute.Length + 2)));
                writer.Write(checked((ushort)printable.Length));
                writer.Write(substitute);
                writer.Write((ushort)0);
                writer.Write(printable);
                writer.Write((ushort)0);
            }
            Directory.CreateDirectory(alias);
            using var handle = CreateFile(alias, 0x40000000, 0, 0, 3, 0x02200000, 0);
            if (handle.IsInvalid || !DeviceIoControl(handle, 0x000900A4, data, data.Length, 0, 0, out _, 0))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not create the disposable path-redirection fixture.");
        }

        [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafeFileHandle CreateFile(string path, uint access, uint share, nint security, uint creation, uint flags, nint template);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DeviceIoControl(SafeFileHandle handle, uint code, byte[] input, int inputSize, nint output, int outputSize, out int returned, nint overlapped);
    }

    internal static class NativeIcon
    {
        internal static byte[] ReadPixels(string path)
        {
            var icon = LoadImage(0, path, 1, 32, 32, 0x0010);
            if (icon == 0) throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows could not load the shortcut icon.");
            return RenderAndDestroy(icon);
        }

        internal static byte[] ReadExecutablePixels(string path)
        {
            var large = new nint[1];
            var small = new nint[1];
            var count = ExtractIconEx(path, 0, large, small, 1);
            if (small[0] != 0) DestroyIcon(small[0]);
            if (count == 0 || count == uint.MaxValue || large[0] == 0)
            {
                if (large[0] != 0) DestroyIcon(large[0]);
                throw new InvalidOperationException($"Windows could not extract an application icon from {path}.");
            }
            return RenderAndDestroy(large[0]);
        }

        private static byte[] RenderAndDestroy(nint icon)
        {
            var dc = CreateCompatibleDC(0);
            var info = new BitmapInfo { Size = 40, Width = 32, Height = -32, Planes = 1, BitCount = 32 };
            var bitmap = CreateDIBSection(dc, ref info, 0, out var bits, 0, 0);
            if (dc == 0 || bitmap == 0)
            {
                DestroyIcon(icon);
                if (bitmap != 0) DeleteObject(bitmap);
                if (dc != 0) DeleteDC(dc);
                throw new InvalidOperationException("Could not allocate the disposable icon pixel surface.");
            }
            var previous = SelectObject(dc, bitmap);
            try
            {
                var pixels = new byte[4096];
                Marshal.Copy(pixels, 0, bits, pixels.Length);
                if (!DrawIconEx(dc, 0, 0, icon, 32, 32, 0, 0, 3)) throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not render the shortcut icon.");
                Marshal.Copy(bits, pixels, 0, pixels.Length);
                return pixels;
            }
            finally
            {
                SelectObject(dc, previous);
                DeleteObject(bitmap);
                DeleteDC(dc);
                DestroyIcon(icon);
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct BitmapInfo
        {
            public uint Size;
            public int Width, Height;
            public ushort Planes, BitCount;
            public uint Compression, SizeImage;
            public int XPelsPerMeter, YPelsPerMeter;
            public uint ClrUsed, ClrImportant;
        }

        [DllImport("user32.dll", EntryPoint = "LoadImageW", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern nint LoadImage(nint instance, string path, uint type, int width, int height, uint flags);
        [DllImport("shell32.dll", EntryPoint = "ExtractIconExW", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern uint ExtractIconEx(string path, int index, [Out] nint[] large, [Out] nint[] small, uint count);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DestroyIcon(nint icon);
        [DllImport("gdi32.dll")] private static extern nint CreateCompatibleDC(nint dc);
        [DllImport("gdi32.dll")] private static extern nint CreateDIBSection(nint dc, ref BitmapInfo info, uint usage, out nint bits, nint section, uint offset);
        [DllImport("gdi32.dll")] private static extern nint SelectObject(nint dc, nint value);
        [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DeleteObject(nint value);
        [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DeleteDC(nint dc);
        [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DrawIconEx(nint dc, int x, int y, nint icon, int width, int height, uint step, nint brush, uint flags);
    }

    private static class IndependentShell
    {
        internal static object ReadDirectAppUserModelId(NativeShellLink link)
        {
            var valueObject = typeof(NativeShellLink).GetField("_com", System.Reflection.BindingFlags.NonPublic |
                System.Reflection.BindingFlags.Instance)!.GetValue(link)!;
            var store = (IPropertyStore)valueObject;
            var key = new PropertyKey { FormatId = new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), Id = 5 };
            Marshal.ThrowExceptionForHR(store.GetValue(ref key, out var value));
            try
            {
                var text = value.Type switch
                {
                    31 => Marshal.PtrToStringUni(value.Pointer),
                    8 => Marshal.PtrToStringBSTR(value.Pointer),
                    0x101F when value.ElementCount == 1 && value.VectorArray != 0 =>
                        Marshal.PtrToStringUni(Marshal.ReadIntPtr(value.VectorArray)),
                    _ => null
                };
                return new { VariantType = value.Type, Text = text, Count = value.Type == 0x101F ? value.ElementCount : (uint?)null };
            }
            finally { PropVariantClear(ref value); }
        }

        internal static void WriteVectorPropertyBlock(string path, params string[] values)
        {
            if (!Path.GetFullPath(path).StartsWith(Path.GetFullPath(_root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Only disposable harness links may receive a native property-store fixture.");
            // Match MS-PROPSTORE/MS-SHLLINK on disk, rather than COM SetValue,
            // which normalizes a one-element vector to a scalar when saved.
            using var propertyValue = new MemoryStream();
            using (var writer = new BinaryWriter(propertyValue, Encoding.Unicode, leaveOpen: true))
            {
                writer.Write(0u); // Serialized Property Value size, filled below.
                writer.Write(5u); // System.AppUserModel.ID
                writer.Write((byte)0);
                writer.Write(0x101Fu); // VT_VECTOR | VT_LPWSTR, plus padding.
                writer.Write((uint)values.Length);
                foreach (var value in values)
                {
                    writer.Write((uint)(value.Length + 1));
                    var bytes = Encoding.Unicode.GetBytes(value + '\0');
                    writer.Write(bytes);
                    writer.Write(new byte[(4 - bytes.Length % 4) % 4]);
                }
                propertyValue.Position = 0;
                writer.Write(checked((uint)propertyValue.Length));
            }
            using var block = new MemoryStream();
            using (var writer = new BinaryWriter(block, Encoding.Unicode, leaveOpen: true))
            {
                writer.Write(checked((uint)(8 + 24 + propertyValue.Length + 4 + 4)));
                writer.Write(0xA0000009u); // PropertyStoreDataBlock
                writer.Write(checked((uint)(24 + propertyValue.Length + 4)));
                writer.Write(0x53505331u);
                writer.Write(new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3").ToByteArray());
                writer.Write(propertyValue.ToArray());
                writer.Write(0u); // Serialized Property Value terminal.
                writer.Write(0u); // Serialized Property Storage terminal.
            }

            var source = File.ReadAllBytes(path);
            if (source.Length < 76 || BinaryPrimitives.ReadUInt32LittleEndian(source) != 76)
                throw new InvalidOperationException("The fixture is not a Shell Link.");
            var flags = BinaryPrimitives.ReadUInt32LittleEndian(source.AsSpan(20));
            var extra = 76;
            if ((flags & 1) != 0) extra += 2 + BinaryPrimitives.ReadUInt16LittleEndian(source.AsSpan(extra));
            if ((flags & 2) != 0) extra += checked((int)BinaryPrimitives.ReadUInt32LittleEndian(source.AsSpan(extra)));
            foreach (var flag in new uint[] { 4, 8, 16, 32, 64 })
                if ((flags & flag) != 0)
                {
                    var characters = BinaryPrimitives.ReadUInt16LittleEndian(source.AsSpan(extra));
                    extra += 2 + characters * ((flags & 128) != 0 ? 2 : 1);
                }
            using var destination = new MemoryStream();
            destination.Write(source, 0, extra);
            while (extra + 4 <= source.Length)
            {
                var size = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(source.AsSpan(extra)));
                if (size == 0) break;
                if (size < 8 || extra + size > source.Length) throw new InvalidOperationException("Malformed fixture ExtraData block.");
                var signature = BinaryPrimitives.ReadUInt32LittleEndian(source.AsSpan(extra + 4));
                if (signature != 0xA0000009) destination.Write(source, extra, size);
                extra += size;
            }
            destination.Write(block.ToArray());
            destination.Write(new byte[4]);
            File.WriteAllBytes(path, destination.ToArray());
        }

        internal static string? ReadAppUserModelId(string linkPath)
        {
            var interfaceId = typeof(IPropertyStore).GUID;
            Marshal.ThrowExceptionForHR(SHGetPropertyStoreFromParsingName(linkPath, 0, 0, ref interfaceId, out var store));
            try
            {
                var key = new PropertyKey { FormatId = new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), Id = 5 };
                Marshal.ThrowExceptionForHR(store.GetValue(ref key, out var value));
                try { return value.Type == 31 ? Marshal.PtrToStringUni(value.Pointer) : null; }
                finally { PropVariantClear(ref value); }
            }
            finally { Marshal.FinalReleaseComObject(store); }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct PropertyKey { public Guid FormatId; public uint Id; }
        [StructLayout(LayoutKind.Explicit, Size = 24)]
        private struct PropVariant
        {
            [FieldOffset(0)] public ushort Type;
            [FieldOffset(8)] public nint Pointer;
            [FieldOffset(8)] public uint ElementCount;
            [FieldOffset(16)] public nint VectorArray;
        }
        [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IPropertyStore
        {
            [PreserveSig] int GetCount(out uint count);
            [PreserveSig] int GetAt(uint index, out PropertyKey key);
            [PreserveSig] int GetValue(ref PropertyKey key, out PropVariant value);
            [PreserveSig] int SetValue(ref PropertyKey key, ref PropVariant value);
            [PreserveSig] int Commit();
        }
        [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
        private static extern int SHGetPropertyStoreFromParsingName(string path, nint bindingContext, uint flags,
            ref Guid interfaceId, [MarshalAs(UnmanagedType.Interface)] out IPropertyStore store);
        [DllImport("ole32.dll")] private static extern int PropVariantClear(ref PropVariant value);
    }
}
