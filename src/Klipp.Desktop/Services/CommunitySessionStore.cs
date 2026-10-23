using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Klipp.Core.Community;

namespace Klipp.Desktop.Services;

/// <summary>Windows current-user DPAPI credentials, isolated by backend project; never ordinary settings JSON.</summary>
public sealed class CommunitySessionStore : ICommunitySessionStore
{
    private readonly string _path;
    private readonly string _signOutMarkerPath;
    private readonly byte[] _entropy;
    private const uint CryptprotectUiForbidden = 0x1;

    public CommunitySessionStore(CommunityConfiguration configuration, string? directory = null)
    {
        _entropy = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes("Klipp.Community.v1:" + configuration.ProjectUrl.ToLowerInvariant()));
        directory ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Klipp", "Community");
        _path = Path.Combine(directory, Convert.ToHexString(_entropy) + ".session");
        _signOutMarkerPath = _path + ".signed-out";
    }

    public async Task<CommunitySession?> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (File.Exists(_signOutMarkerPath))
        {
            // A previous sign-out may have been unable to delete a locked file. Never restore that credential.
            try { await ClearAsync(cancellationToken); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            return null;
        }
        if (!File.Exists(_path)) return null;
        if (new FileInfo(_path).Length > 128 * 1024)
        {
            await ClearAsync(cancellationToken);
            return null;
        }
        var encrypted = await File.ReadAllBytesAsync(_path, cancellationToken);
        byte[]? plain = null;
        try
        {
            plain = Protect(encrypted, protect: false);
            return JsonSerializer.Deserialize<CommunitySession>(plain);
        }
        catch (Exception ex) when (ex is CryptographicException or JsonException)
        {
            // A corrupt or foreign Windows-user credential is treated as signed out.
            await ClearAsync(cancellationToken);
            return null;
        }
        finally
        {
            if (plain is not null) CryptographicOperations.ZeroMemory(plain);
        }
    }

    public async Task SaveAsync(CommunitySession session, CancellationToken cancellationToken = default)
    {
        var plain = JsonSerializer.SerializeToUtf8Bytes(session);
        byte[] encrypted;
        try { encrypted = Protect(plain, protect: true); }
        finally { CryptographicOperations.ZeroMemory(plain); }
        cancellationToken.ThrowIfCancellationRequested();
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllBytesAsync(temporary, encrypted, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, _path, overwrite: true);
            File.Delete(_signOutMarkerPath);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    public Task ClearAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!File.Exists(_path) && !File.Exists(_signOutMarkerPath)) return Task.CompletedTask;
        // Persist intent first. A failed deletion must not silently sign the account back in next launch.
        if (!File.Exists(_signOutMarkerPath)) File.WriteAllText(_signOutMarkerPath, "signed-out");
        File.Delete(_path);
        File.Delete(_signOutMarkerPath);
        return Task.CompletedTask;
    }

    private byte[] Protect(byte[] input, bool protect)
    {
        var data = new DataBlob();
        var entropy = new DataBlob();
        var output = new DataBlob();
        try
        {
            data.Length = input.Length;
            data.Data = Marshal.AllocHGlobal(input.Length);
            Marshal.Copy(input, 0, data.Data, input.Length);
            entropy.Length = _entropy.Length;
            entropy.Data = Marshal.AllocHGlobal(_entropy.Length);
            Marshal.Copy(_entropy, 0, entropy.Data, _entropy.Length);
            var success = protect
                ? CryptProtectData(ref data, "Klipp account", ref entropy, IntPtr.Zero, IntPtr.Zero, CryptprotectUiForbidden, out output)
                : CryptUnprotectData(ref data, IntPtr.Zero, ref entropy, IntPtr.Zero, IntPtr.Zero, CryptprotectUiForbidden, out output);
            if (!success) throw new CryptographicException("Windows could not protect this account credential.");
            var result = new byte[output.Length];
            Marshal.Copy(output.Data, result, 0, output.Length);
            return result;
        }
        finally
        {
            if (data.Data != IntPtr.Zero)
            {
                for (var i = 0; i < data.Length; i++) Marshal.WriteByte(data.Data, i, 0);
                Marshal.FreeHGlobal(data.Data);
            }
            if (entropy.Data != IntPtr.Zero) Marshal.FreeHGlobal(entropy.Data);
            if (output.Data != IntPtr.Zero)
            {
                if (!protect)
                    for (var i = 0; i < output.Length; i++) Marshal.WriteByte(output.Data, i, 0);
                LocalFree(output.Data);
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob { public int Length; public IntPtr Data; }

    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(ref DataBlob input, string description, ref DataBlob entropy,
        IntPtr reserved, IntPtr prompt, uint flags, out DataBlob output);

    [DllImport("crypt32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(ref DataBlob input, IntPtr description, ref DataBlob entropy,
        IntPtr reserved, IntPtr prompt, uint flags, out DataBlob output);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);
}
