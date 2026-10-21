using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using Klipp.Capture.Models;
using Microsoft.Win32.SafeHandles;

namespace Klipp.Capture.GameCapture;

/// <summary>Explicit, same-user x64 attachment. Access failures are never escalated.</summary>
internal sealed class GameHookAttachment : IDisposable
{
    private readonly SafeProcessHandle _process;
    public uint ProcessId { get; }

    private GameHookAttachment(SafeProcessHandle process, uint processId) { _process = process; ProcessId = processId; }

    public static GameHookAttachment Open(CaptureTarget target)
    {
        if (RuntimeInformation.ProcessArchitecture != Architecture.X64 || target.Kind != CaptureTargetKind.Window)
            throw new NotSupportedException("In-game notifications currently require an x64 D3D11 game window.");
        GetWindowThreadProcessId(target.Handle, out var pid);
        if (pid == 0 || pid == Environment.ProcessId || (target.ProcessId != 0 && target.ProcessId != pid))
            throw new InvalidOperationException("The selected game window is no longer available.");
        var process = OpenProcess(0x0002 | 0x0008 | 0x0010 | 0x0020 | 0x0040 | 0x1000, false, pid);
        if (process.IsInvalid) { process.Dispose(); throw new Win32Exception(Marshal.GetLastWin32Error(), "This game does not allow capture attachment. Windows capture will be used."); }
        try
        {
            if (!IsWow64Process2(process, out var machine, out var native) || machine != 0 || native != 0x8664)
                throw new NotSupportedException("Only native x64 D3D11 games are supported by in-game capture.");
            if (!GetProcessTimes(process, out var created, out _, out _, out _) || (target.ProcessCreated != 0 && target.ProcessCreated != created))
                throw new InvalidOperationException("The selected game process has changed.");
            if (!OpenProcessToken(process, 8, out var token)) throw new Win32Exception(Marshal.GetLastWin32Error());
            using (token)
            using (var identity = new WindowsIdentity(token.DangerousGetHandle()))
            using (var current = WindowsIdentity.GetCurrent())
            {
                if (identity.User is null || current.User is null || !identity.User.Equals(current.User))
                    throw new InvalidOperationException("Game capture attachment is limited to your own Windows session.");
                if (!GetTokenInformation(token, 20, out var elevated, 4, out _) || elevated != 0)
                    throw new InvalidOperationException("Elevated games are not attached. Windows capture will be used.");
            }
            if (!ProcessIdToSessionId(pid, out var session) || !ProcessIdToSessionId((uint)Environment.ProcessId, out var ownSession) || session != ownSession)
                throw new InvalidOperationException("The game is not running in this Windows session.");
            return new(process, pid);
        }
        catch { process.Dispose(); throw; }
    }

    public void Attach(string fullPath, byte[] startArguments)
    {
        if (!Path.IsPathFullyQualified(fullPath) || fullPath.StartsWith(@"\\", StringComparison.Ordinal) || !File.Exists(fullPath))
            throw new FileNotFoundException("The optional D3D11 game capture component is not installed.", fullPath);
        fullPath = Path.GetFullPath(fullPath);
        // Load only the PE image locally to calculate the exported entry point. DllMain is not run.
        var local = LoadLibraryEx(fullPath, 0, 1);
        if (local == 0) throw new Win32Exception(Marshal.GetLastWin32Error(), "The x64 game capture component could not be read.");
        try
        {
            var export = GetProcAddress(local, "KlippGameCaptureStart");
            if (export == 0) throw new InvalidDataException("The game capture component has an incompatible entry point.");
            var offset = export.ToInt64() - local.ToInt64();
            var remote = FindRemoteModule(fullPath);
            if (remote == 0)
            {
                var loadLibrary = GetProcAddress(GetModuleHandle("kernel32.dll"), "LoadLibraryW");
                var resolvedModule = Process.GetCurrentProcess().Modules.Cast<ProcessModule>().FirstOrDefault(module =>
                    loadLibrary.ToInt64() >= module.BaseAddress.ToInt64() && loadLibrary.ToInt64() < module.BaseAddress.ToInt64() + module.ModuleMemorySize)
                    ?? throw new InvalidOperationException("The Windows library loader could not be located.");
                using var game = Process.GetProcessById(checked((int)ProcessId));
                var remoteModule = game.Modules.Cast<ProcessModule>().FirstOrDefault(module => string.Equals(module.ModuleName, resolvedModule.ModuleName, StringComparison.OrdinalIgnoreCase))
                    ?? throw new InvalidOperationException("The game library loader could not be located.");
                var remoteLoader = new nint(remoteModule.BaseAddress.ToInt64() + loadLibrary.ToInt64() - resolvedModule.BaseAddress.ToInt64());
                RunRemote(remoteLoader, Encoding.Unicode.GetBytes(fullPath + '\0'));
                remote = FindRemoteModule(fullPath);
                if (remote == 0) throw new InvalidOperationException("The game did not load the capture component. Windows capture will be used.");
            }
            var result = RunRemote(new nint(remote.ToInt64() + offset), startArguments);
            // A previous session can be finishing its final acknowledged pipe write.
            for (var retry = 0; result == 1 && retry < 12; retry++)
            { Thread.Sleep(25); result = RunRemote(new nint(remote.ToInt64() + offset), startArguments); }
            if (result != 0) throw new InvalidOperationException($"The game's capture bridge could not start ({result}).");
        }
        finally { FreeLibrary(local); }
    }

    private nint FindRemoteModule(string path)
    {
        using var process = Process.GetProcessById(checked((int)ProcessId));
        return process.Modules.Cast<ProcessModule>().FirstOrDefault(module => string.Equals(module.FileName, path, StringComparison.OrdinalIgnoreCase))?.BaseAddress ?? 0;
    }

    private uint RunRemote(nint entry, byte[] data)
    {
        var memory = VirtualAllocEx(_process, 0, (nuint)data.Length, 0x3000, 4);
        if (memory == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
        var completed = true;
        try
        {
            if (!WriteProcessMemory(_process, memory, data, (nuint)data.Length, out var written) || written != (nuint)data.Length)
                throw new Win32Exception(Marshal.GetLastWin32Error());
            using var thread = CreateRemoteThread(_process, 0, 0, entry, memory, 0, out _);
            if (thread.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error(), "The game declined capture attachment.");
            if (WaitForSingleObject(thread, 5000) != 0)
            {
                // The remote call may still reference its input. Do not free it underneath that thread.
                completed = false;
                throw new TimeoutException("The game did not respond to capture attachment.");
            }
            if (!GetExitCodeThread(thread, out var code)) throw new Win32Exception(Marshal.GetLastWin32Error());
            return code;
        }
        finally { if (completed) VirtualFreeEx(_process, memory, 0, 0x8000); }
    }

    public SafeFileHandle DuplicateTexture(ulong remoteHandle)
    {
        if (!DuplicateHandle(_process, new nint(unchecked((long)remoteHandle)), GetCurrentProcess(), out var local, 0, false, 2))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "A private capture texture could not be opened.");
        return new(local, ownsHandle: true);
    }

    public void Dispose() => _process.Dispose();

    [DllImport("kernel32.dll", SetLastError = true)] private static extern SafeProcessHandle OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool IsWow64Process2(SafeProcessHandle process, out ushort machine, out ushort native);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetProcessTimes(SafeProcessHandle process, out long created, out long exited, out long kernel, out long user);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool ProcessIdToSessionId(uint pid, out uint session);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool OpenProcessToken(SafeProcessHandle process, uint access, out SafeAccessTokenHandle token);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool GetTokenInformation(SafeAccessTokenHandle token, int info, out int value, int size, out int required);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint pid);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern nint LoadLibraryEx(string path, nint file, uint flags);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern nint GetModuleHandle(string name);
    [DllImport("kernel32.dll", CharSet = CharSet.Ansi, ExactSpelling = true)] private static extern nint GetProcAddress(nint module, string name);
    [DllImport("kernel32.dll")] private static extern bool FreeLibrary(nint module);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern nint VirtualAllocEx(SafeProcessHandle process, nint address, nuint size, uint allocation, uint protection);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool VirtualFreeEx(SafeProcessHandle process, nint address, nuint size, uint freeType);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool WriteProcessMemory(SafeProcessHandle process, nint address, byte[] data, nuint size, out nuint written);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern SafeWaitHandle CreateRemoteThread(SafeProcessHandle process, nint attributes, nuint stackSize, nint entry, nint parameter, uint flags, out uint id);
    [DllImport("kernel32.dll")] private static extern uint WaitForSingleObject(SafeWaitHandle handle, uint milliseconds);
    [DllImport("kernel32.dll")] private static extern bool GetExitCodeThread(SafeWaitHandle handle, out uint result);
    [DllImport("kernel32.dll")] private static extern nint GetCurrentProcess();
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool DuplicateHandle(SafeProcessHandle source, nint handle, nint target, out nint copy, uint access, bool inherit, uint options);
}
