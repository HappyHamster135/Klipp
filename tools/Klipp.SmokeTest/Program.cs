using Klipp.Core.Models;
using Klipp.Encoding.Video;

namespace Klipp.SmokeTest;

internal static class Program
{
    private static async Task<int> Main()
    {
        Console.WriteLine("Klipp Smoke Test — MediaFoundationH264Encoder (Fas 1 + 2)");
        Console.WriteLine("==========================================================");
        Console.WriteLine();

        // Standardinställningar för 1080p60 — encodern ska kunna acceptera dessa
        var settings = RecordingSettings.Default1080p60;

        Console.WriteLine($"Settings:");
        Console.WriteLine($"  Resolution: {settings.Width}x{settings.Height}");
        Console.WriteLine($"  Frame rate: {settings.FrameRate}");
        Console.WriteLine($"  Video bitrate: {settings.VideoBitrate / 1_000_000} Mbps");
        Console.WriteLine();

        await using var encoder = new MediaFoundationH264Encoder();

        try
        {
            Console.WriteLine("Initialiserar encoder...");
            Console.WriteLine("  [Fas 1] Söker efter bästa H.264-encoder...");
            Console.WriteLine("  [Fas 2] Konfigurerar media types...");
            Console.WriteLine();

            await encoder.InitializeAsync(settings);

            Console.WriteLine($"✓ OK!");
            Console.WriteLine();
            Console.WriteLine($"  Vald encoder: {encoder.SelectedEncoderName}");
            Console.WriteLine($"  Hårdvaru-accelererad: {(encoder.IsHardwareAccelerated ? "Ja" : "Nej (software)")}");
            Console.WriteLine();
            Console.WriteLine("Fas 1 + 2 fungerar. Encodern är redo för Fas 3 (color conversion).");

            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine();
            Console.Error.WriteLine($"✗ FEL: {ex.GetType().Name}");
            Console.Error.WriteLine($"  {ex.Message}");
            Console.Error.WriteLine();

            if (ex.Message.Contains("Ingen H.264-encoder"))
            {
                Console.Error.WriteLine("Detta är förväntat på maskiner som saknar registrerade MF-encoders.");
                Console.Error.WriteLine("På en normal Windows-installation ska Fas 1 hitta minst en encoder.");
            }

            return 1;
        }
    }
}
