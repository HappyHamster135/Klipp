using System.Runtime.InteropServices;
using Klipp.Capture.Models;
using Klipp.Capture.Video;
using Klipp.Core.Models;
using Klipp.Encoding.FFmpeg;
using Klipp.Encoding.Video;

namespace Klipp.SmokeTest;

internal static class Program
{
    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern nint FindWindow(string? lpClassName, string? lpWindowName);

    private const int CaptureSeconds = 10;

    private static async Task<int> Main()
    {
        Console.WriteLine("Klipp Smoke Test — FFmpeg encoder end-to-end");
        Console.WriteLine("=============================================");
        Console.WriteLine("  Capture (Notepad) -> FFmpegH264Encoder -> MP4-fil");
        Console.WriteLine();

        // Hitta Notepad
        var hwnd = FindWindow("Notepad", null);
        if (hwnd == nint.Zero)
        {
            Console.Error.WriteLine("FEL: Hittade inte Notepad. Starta Notepad och kor igen.");
            return 1;
        }

        Console.WriteLine($"Notepad hittad — HWND: 0x{hwnd:X}");
        Console.WriteLine();

        // Steg 1: Verifiera/ladda ner FFmpeg
        var locator = new FFmpegLocator();
        if (!locator.IsInstalled)
        {
            Console.WriteLine("FFmpeg ej installerat — laddar ner (~50 MB)...");
            var progress = new Progress<double>(p =>
            {
                Console.Write($"\r  {p:P0} klart");
            });
            await locator.GetFFmpegPathAsync(progress);
            Console.WriteLine();
            Console.WriteLine($"FFmpeg installerat i: {locator.BinDirectory}");
        }
        else
        {
            Console.WriteLine($"FFmpeg redan installerat i: {locator.BinDirectory}");
        }
        Console.WriteLine();

        // Steg 2: Bygg pipelinen
        var target = new CaptureTarget
        {
            Kind = CaptureTargetKind.Window,
            Handle = hwnd,
            DisplayName = "Notepad"
        };

        // 30 FPS för rimlig prestanda
        var settings = RecordingSettings.Default1080p60 with { FrameRate = 30 };

        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        var outputPath = Path.Combine(desktop, $"klipp_ffmpeg_{DateTime.Now:yyyyMMdd_HHmmss}.mp4");

        await using var encoder = new FFmpegH264Encoder(locator);
        encoder.SetOutputPath(outputPath);
        await encoder.InitializeAsync(settings);

        await using var captureSource = new WgcCaptureSource(target);
        await captureSource.StartAsync();

        Console.WriteLine($"Spelar in {CaptureSeconds} sekunder...");
        Console.WriteLine();

        var frameCount = 0;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(CaptureSeconds));

        try
        {
            await foreach (var frame in captureSource.ReadSamplesAsync(cts.Token))
            {
                frameCount++;

                // Pipea frame direkt till FFmpeg (EncodeAsync returnerar inga samples,
                // FFmpeg muxar direkt till filen)
                await foreach (var _ in encoder.EncodeAsync(frame, cts.Token))
                {
                    // Ingen output att samla
                }

                if (frameCount % 30 == 0)
                    Console.WriteLine($"  {frameCount} frames pipade till FFmpeg");
            }
        }
        catch (OperationCanceledException)
        {
            // Forvantat nar timeout gar ut
        }

        Console.WriteLine();
        Console.WriteLine("Avslutar inspelning...");
        await captureSource.StopAsync();

        // Flusha FFmpeg — stänger stdin och väntar på MP4-finalisering
        await foreach (var _ in encoder.FlushAsync())
        {
            // FFmpeg muxar fardigt
        }

        // Verifiera filen
        var fileInfo = new FileInfo(outputPath);
        Console.WriteLine();
        Console.WriteLine("OK!");
        Console.WriteLine($"  Output: {outputPath}");
        Console.WriteLine($"  Storlek: {fileInfo.Length / 1024.0 / 1024.0:F1} MB");
        Console.WriteLine($"  Frames pipade: {frameCount}");
        Console.WriteLine();
        Console.WriteLine("Dubbelklicka filen for att spela i Windows Media Player eller VLC.");

        return 0;
    }
}
