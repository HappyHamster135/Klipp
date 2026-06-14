using System.Runtime.InteropServices;

namespace Klipp.Encoding;

/// <summary>
/// Hanterar Media Foundation:s globala startup/shutdown. MF kräver att <c>MFStartup</c>
/// anropas en gång per process innan något annat MF-anrop fungerar.
/// </summary>
public static class MediaFoundationStartup
{
    private const int MF_VERSION = 0x00020070;
    private const int MFSTARTUP_FULL = 0;

    [DllImport("mfplat.dll", ExactSpelling = true, PreserveSig = false)]
    private static extern void MFStartup(int version, int flags);

    [DllImport("mfplat.dll", ExactSpelling = true, PreserveSig = false)]
    private static extern void MFShutdown();

    private static readonly Lazy<bool> _initialized = new(() =>
    {
        Console.WriteLine($"[debug] Calling MFStartup(version=0x{MF_VERSION:X8}, flags={MFSTARTUP_FULL})...");
        try
        {
            MFStartup(MF_VERSION, MFSTARTUP_FULL);
            Console.WriteLine("[debug] MFStartup returned successfully");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[debug] MFStartup FAILED: {ex.GetType().Name}: {ex.Message}");
            throw;
        }

        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            try { MFShutdown(); } catch { }
        };
        return true;
    });

    /// <summary>
    /// Säkerställer att Media Foundation är initialiserat. Idempotent — säker att anropa flera gånger.
    /// </summary>
    public static void EnsureInitialized() => _ = _initialized.Value;
}
