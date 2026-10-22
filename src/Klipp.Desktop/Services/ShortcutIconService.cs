using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Runtime.Versioning;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using Microsoft.Win32.SafeHandles;
using Velopack.Locators;

namespace Klipp.Desktop.Services;

/// <summary>Keep shell icons resolvable outside the installer's filesystem context.</summary>
[SupportedOSPlatform("windows")]
internal static class ShortcutIconService
{
    internal static void RepairCurrentInstallation()
    {
        try
        {
            var locator = VelopackLocator.Current;
            if (locator.IsPortable || locator.AppId != AppUpdateService.ApplicationId || locator.RootAppDir is null) return;
            var files = FindShortcuts().ToArray();
            var repaired = RepairInstallation(locator.RootAppDir, files);
            locator.Log.Log(Velopack.Logging.VelopackLogLevel.Information,
                $"Shortcut icons: repaired {repaired} of {files.Length} existing shell entries.", null);
        }
        catch (Exception exception)
        {
            // Shell integration must not prevent installation, updates or capture.
            LogFailure("Shortcut icon repair could not finish.", exception);
        }
    }

    internal static int RepairInstallation(string rootDirectory, IEnumerable<string> shortcutFiles,
        Action<string>? notifyChanged = null, bool isPortable = false)
    {
        if (isPortable) return 0;
        var content = Path.Combine(rootDirectory, "current");
        var executable = CanonicalPath.TryResolveFile(Path.Combine(content, "Klipp.Desktop.exe"));
        var icon = CanonicalPath.TryResolveFile(Path.Combine(content, "Assets", "Klipp.ico"));
        if (executable is null || icon is null || !File.Exists(Path.Combine(rootDirectory, "Update.exe")) ||
            !IsKlippManifest(Path.Combine(content, "sq.version"))) return 0;

        var launcher = CanonicalPath.TryResolveFile(Path.Combine(rootDirectory, "Klipp.exe"));
        var workingDirectory = Path.GetDirectoryName(executable)!;
        var repaired = 0;
        foreach (var file in shortcutFiles.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                // Loading without Resolve avoids Windows searching for another
                // installation when a link is broken or current is being replaced.
                if (!File.Exists(file)) continue;
                using var link = new NativeShellLink(file);
                if (link.AppUserModelId != AppUpdateService.ApplicationId) continue;
                var target = link.Target;
                var physicalTarget = CanonicalPath.TryResolveFile(target);
                var ownsTarget = Same(physicalTarget, executable) || (launcher is not null && Same(physicalTarget, launcher));
                if (!ownsTarget) continue;

                // Preserve launcher targets and every custom argument, hotkey,
                // description and show state. Only paths and icon index change.
                var desiredTarget = launcher is not null && Same(physicalTarget, launcher) ? launcher : executable;
                if (Same(target, desiredTarget) && Same(link.WorkingDirectory, workingDirectory) &&
                    Same(link.IconPath, icon) && link.IconIndex == 0) continue;
                link.Target = desiredTarget;
                link.WorkingDirectory = workingDirectory;
                link.IconPath = icon;
                link.IconIndex = 0;
                link.Save();
                repaired++;
                (notifyChanged ?? NotifyChanged)(CanonicalPath.TryResolveFile(file) ?? file);
            }
            catch (Exception exception)
            {
                // One stale, read-only or damaged shortcut must not block others.
                LogFailure($"Could not repair shortcut {file}.", exception);
            }
        }
        return repaired;
    }

    private static bool IsKlippManifest(string file)
    {
        try
        {
            using var reader = XmlReader.Create(file, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit });
            var metadata = XDocument.Load(reader).Root?.Elements().FirstOrDefault(element => element.Name.LocalName == "metadata");
            return metadata?.Elements().FirstOrDefault(element => element.Name.LocalName == "id")?.Value == AppUpdateService.ApplicationId &&
                metadata.Elements().FirstOrDefault(element => element.Name.LocalName == "mainExe")?.Value == "Klipp.Desktop.exe";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or XmlException) { return false; }
    }

    private static bool Same(string? left, string? right) => left is not null && right is not null &&
        string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    private static void LogFailure(string message, Exception exception)
    {
        Debug.WriteLine($"[Klipp] {message} {exception.Message}");
        try
        {
            if (VelopackLocator.IsCurrentSet)
                VelopackLocator.Current.Log.Log(Velopack.Logging.VelopackLogLevel.Warning, message, exception);
        }
        catch { /* A shell/logging failure must never stop the application. */ }
    }

    private static IEnumerable<string> FindShortcuts()
    {
        var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var roots = new[]
        {
            (Path: Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), Recurse: false),
            (Path: Environment.GetFolderPath(Environment.SpecialFolder.Programs), Recurse: true),
            (Path: Environment.GetFolderPath(Environment.SpecialFolder.Startup), Recurse: true),
            (Path: Path.Combine(roaming, "Microsoft", "Internet Explorer", "Quick Launch", "User Pinned"), Recurse: true)
        };
        foreach (var root in roots)
        {
            var directory = root.Path;
            if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory)) continue;
            // Include OneDrive placeholder shortcut files, but do not follow
            // directory junctions out of the shell folders (or recurse Desktop).
            var pending = new Stack<string>();
            pending.Push(directory);
            while (pending.TryPop(out var current))
            {
                string[] files;
                try
                {
                    files = Directory.GetFiles(current, "*.lnk", new EnumerationOptions
                    { IgnoreInaccessible = true, AttributesToSkip = 0 });
                    if (root.Recurse)
                        foreach (var child in Directory.GetDirectories(current, "*", new EnumerationOptions
                        { IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint })) pending.Push(child);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { continue; }
                foreach (var file in files) yield return file;
            }
        }
    }

    private static void NotifyChanged(string file) => SHChangeNotify(0x2000, 0x2005, file, 0);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern void SHChangeNotify(uint eventId, uint flags, string item1, nint item2);
}

[SupportedOSPlatform("windows")]
internal static class CanonicalPath
{
    internal static string? TryResolveFile(string file)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(file)) return null;
            using var handle = File.OpenHandle(Environment.ExpandEnvironmentVariables(file), FileMode.Open,
                FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var buffer = new StringBuilder(512);
            var length = GetFinalPathNameByHandle(handle, buffer, (uint)buffer.Capacity, 0);
            if (length == 0) return null;
            if (length >= buffer.Capacity)
            {
                buffer.EnsureCapacity(checked((int)length + 1));
                length = GetFinalPathNameByHandle(handle, buffer, (uint)buffer.Capacity, 0);
                if (length == 0 || length >= buffer.Capacity) return null;
            }
            var path = buffer.ToString();
            if (path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)) return @"\\" + path[8..];
            return path.StartsWith(@"\\?\", StringComparison.Ordinal) ? path[4..] : path;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        { return null; }
    }

    [DllImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandle(SafeFileHandle file, StringBuilder path, uint size, uint flags);
}

/// <summary>Load/save the existing Shell Link without resolving or retargeting it.</summary>
[SupportedOSPlatform("windows")]
internal sealed class NativeShellLink : IDisposable
{
    private readonly object _com;
    private readonly IShellLink _link;
    private readonly string _file;
    internal NativeShellLink(string file) : this(file, load: true) { }
    private NativeShellLink(string file, bool load)
    {
        _file = file;
        _com = Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("00021401-0000-0000-C000-000000000046"), throwOnError: true)!)!;
        _link = (IShellLink)_com;
        try { if (load) ((IPersistFile)_com).Load(file, 0); }
        catch { Marshal.FinalReleaseComObject(_com); throw; }
    }
    internal static NativeShellLink Create(string file) => new(file, load: false);
    private static StringBuilder Buffer() => new(32768);
    internal string Target { get { var b = Buffer(); _link.GetPath(b, b.Capacity, 0, 4); return b.ToString(); } set => _link.SetPath(value); }
    internal string WorkingDirectory { get { var b = Buffer(); _link.GetWorkingDirectory(b, b.Capacity); return b.ToString(); } set => _link.SetWorkingDirectory(value); }
    internal string IconPath { get { var b = Buffer(); _link.GetIconLocation(b, b.Capacity, out _); return b.ToString(); } set => _link.SetIconLocation(value, IconIndex); }
    internal int IconIndex { get { var b = Buffer(); _link.GetIconLocation(b, b.Capacity, out var index); return index; } set => _link.SetIconLocation(IconPath, value); }
    internal string Arguments { get { var b = Buffer(); _link.GetArguments(b, b.Capacity); return b.ToString(); } set => _link.SetArguments(value); }
    internal string Description { get { var b = Buffer(); _link.GetDescription(b, b.Capacity); return b.ToString(); } set => _link.SetDescription(value); }
    internal int ShowCommand { get { _link.GetShowCmd(out var value); return value; } set => _link.SetShowCmd(value); }
    internal short HotKey { get { _link.GetHotkey(out var value); return value; } set => _link.SetHotkey(value); }
    internal string? AppUserModelId
    {
        get
        {
            var key = AppIdKey;
            Marshal.ThrowExceptionForHR(((IPropertyStore)_com).GetValue(ref key, out var value));
            try
            {
                // Windows/Velopack may persist a single-value string vector;
                // the shell's parsed property reader normalizes it to a string.
                // Read both forms without rewriting the shortcut's identity.
                return value.Type switch
                {
                    31 => Marshal.PtrToStringUni(value.Pointer),
                    8 => value.Pointer == 0 ? null : Marshal.PtrToStringBSTR(value.Pointer),
                    0x101F when value.Count == 1 && value.Values != 0 =>
                        Marshal.PtrToStringUni(Marshal.ReadIntPtr(value.Values)),
                    _ => null
                };
            }
            finally { PropVariantClear(ref value); }
        }
        set
        {
            var key = AppIdKey;
            var variant = new PropVariant { Type = 31, Pointer = Marshal.StringToCoTaskMemUni(value ?? "") };
            try
            {
                var store = (IPropertyStore)_com;
                Marshal.ThrowExceptionForHR(store.SetValue(ref key, ref variant));
                Marshal.ThrowExceptionForHR(store.Commit());
            }
            finally { PropVariantClear(ref variant); }
        }
    }
    internal void Save() => ((IPersistFile)_com).Save(_file, true);
    public void Dispose() => Marshal.FinalReleaseComObject(_com);

    private static PropertyKey AppIdKey => new() { FormatId = new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), Id = 5 };
    [StructLayout(LayoutKind.Sequential)] private struct PropertyKey { public Guid FormatId; public uint Id; }
    [StructLayout(LayoutKind.Explicit, Size = 24)] private struct PropVariant
    {
        [FieldOffset(0)] public ushort Type;
        [FieldOffset(8)] public nint Pointer;
        [FieldOffset(8)] public uint Count;
        [FieldOffset(16)] public nint Values;
    }
    [DllImport("ole32.dll")] private static extern int PropVariantClear(ref PropVariant value);
    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int GetAt(uint index, out PropertyKey key);
        [PreserveSig] int GetValue(ref PropertyKey key, out PropVariant value);
        [PreserveSig] int SetValue(ref PropertyKey key, ref PropVariant value);
        [PreserveSig] int Commit();
    }
    [ComImport, Guid("000214F9-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellLink
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder path, int size, nint findData, uint flags);
        void GetIDList(out nint list);
        void SetIDList(nint list);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder text, int size);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string text);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder path, int size);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string path);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder text, int size);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string text);
        void GetHotkey(out short hotkey);
        void SetHotkey(short hotkey);
        void GetShowCmd(out int command);
        void SetShowCmd(int command);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder path, int size, out int index);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string path, int index);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string path, uint reserved);
        void Resolve(nint window, uint flags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string path);
    }
}
