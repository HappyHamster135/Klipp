using System.Runtime.InteropServices;
using Klipp.Capture.Models;
using Klipp.Capture.Video;
using Klipp.Core.Abstractions;
using Klipp.Core.Models;
using Klipp.Encoding.Muxing;
using Klipp.Encoding.Video;
using Klipp.Storage.RingBuffer;

namespace Klipp.SmokeTest;

internal static class Program
{
    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern nint FindWindow(string? lpClassName, string? lpWindowName);

    private const int CaptureSeconds = 5;
    private const int FlushLastSeconds = 3;

    private static async Task<int> Main()
    {
        Console.WriteLine("Klipp Smoke Test — end-to-end pipeline");
        Console.WriteLine("========================================");
        Console.WriteLine("  Capture → RawEncoder → RingBuffer → RawFile");
        Console.WriteLine();

        // Hitta Notepad
        var hwnd = FindWindow("Notepad", null);
        if (hwnd == nint.Zero)
        {
            Console.Error.WriteLine("FEL: Hittade inte Notepad. Starta Notepad och kör igen.");
            return 1;
        }

        Console.WriteLine($"Notepad hittad — HWND: 0x{hwnd:X}");

        var target = new CaptureTarget
        {
            Kind = CaptureTargetKind.Window,
            Handle = hwnd,
            DisplayName = "Notepad"
        };

        // Bygg pipelinen
        // Settings — 30 FPS för att inte överbelasta disk med raw data
        var settings = RecordingSettings.Default1080p60 with { FrameRate = 30, RingBufferSeconds = 10 };

        await using var encoder = new RawSampleEncoder();
        await encoder.InitializeAsync(settings);

        // Ring buffer behöver en factory som skapar en ny RawFileWriter per flush
        await using var buffer = new RingBufferClipBuffer(() => new RawFileWriter(), settings);

        await using var captureSource = new WgcCaptureSource(target);

        // Output-fil
        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        var outputPath = Path.Combine(desktop, $"klipp_pipeline_{DateTime.Now:yyyyMMdd_HHmmss}.raw");

        Console.WriteLine($"Capturing {CaptureSeconds}s in 30 FPS (notera: filen blir stor!)");
        Console.WriteLine();

        // Starta capture
        await captureSource.StartAsync();

        var frameCount = 0;
        var encodedCount = 0;

        // Kör capture-loop i 5 sekunder
        using var captureCts = new CancellationTokenSource(TimeSpan.FromSeconds(CaptureSeconds));

        try
        {
            await foreach (var frame in captureSource.ReadSamplesAsync(captureCts.Token))
            {
                frameCount++;

                // Koda (passthrough) → buffer
                await foreach (var sample in encoder.EncodeAsync(frame))
                {
                    await buffer.AppendAsync(sample);
                    encodedCount++;
                }

                if (frameCount % 30 == 0)
                {
                    Console.WriteLine($"  {frameCount} frames captured, {encodedCount} samples in buffer ({buffer.BufferedSeconds:F1}s)");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Förväntat när timeout går ut
        }

        await captureSource.StopAsync();

        Console.WriteLine();
        Console.WriteLine($"Capture klar. Total: {frameCount} frames, buffer har {buffer.BufferedSeconds:F1}s");
        Console.WriteLine();

        // Flusha senaste N sekunder
        Console.WriteLine($"Flushar senaste {FlushLastSeconds} sekunder till disk...");
        var actualDuration = await buffer.FlushLastSecondsAsync(FlushLastSeconds, outputPath);

        // Verifiera filen
        var fileInfo = new FileInfo(outputPath);
        Console.WriteLine();
        Console.WriteLine("OK!");
        Console.WriteLine($"  Output: {outputPath}");
        Console.WriteLine($"  Storlek: {fileInfo.Length / 1024.0 / 1024.0:F1} MB");
        Console.WriteLine($"  Sparad varaktighet: {actualDuration:F2}s");

        return 0;
    }
}
