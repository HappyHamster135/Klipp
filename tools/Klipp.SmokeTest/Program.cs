using System.Runtime.InteropServices;
using Klipp.Capture.Models;
using Klipp.Capture.Video;
using Klipp.Core.Models;
using Klipp.Encoding.Clipping;
using Klipp.Encoding.FFmpeg;
using Klipp.Encoding.Video;

namespace Klipp.SmokeTest;

internal static class Program
{
    [DllImport("user32.dll")]
    private static extern nint MonitorFromPoint(POINT pt, uint dwFlags);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int x, y; }

    private const uint MONITOR_DEFAULTTOPRIMARY = 0x00000001;

    private static async Task<int> Main()
    {
        Console.WriteLine("Klipp Smoke Test — Segment recorder + Clip extractor");
        Console.WriteLine("====================================================");
        Console.WriteLine();

        // Mappar
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var segmentDir = Path.Combine(localAppData, "Klipp", "segments");
        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        var outputPath = Path.Combine(desktop, $"klipp_extracted_{DateTime.Now:yyyyMMdd_HHmmss}.mp4");

        Console.WriteLine($"Segment-mapp: {segmentDir}");
        Console.WriteLine($"Output-klipp: {outputPath}");
        Console.WriteLine();

        // Capture-mål: hela primary monitor
        var hMonitor = MonitorFromPoint(new POINT { x = 0, y = 0 }, MONITOR_DEFAULTTOPRIMARY);
        var target = new CaptureTarget
        {
            Kind = CaptureTargetKind.Monitor,
            Handle = hMonitor,
            DisplayName = "Primary Monitor"
        };

        var settings = RecordingSettings.Default1080p60 with { FrameRate = 30 };
        var locator = new FFmpegLocator();

        // === DEL 1: Starta segment-inspelning ===
        var recorder = new FFmpegSegmentRecorder(locator)
        {
            SegmentDurationSeconds = 5,   // 5s per segment (för snabbare test)
            MaxSegments = 12               // 60s buffer
        };
        recorder.SetSegmentDirectory(segmentDir);
        await recorder.InitializeAsync(settings);

        await using var _ = recorder;

        var captureSource = new WgcCaptureSource(target);
        await using var __ = captureSource;
        await captureSource.StartAsync();

        Console.WriteLine("Spelar in i 25 sekunder (~5 segment a 5s)...");
        Console.WriteLine();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        var frameCount = 0;

        try
        {
            await foreach (var frame in captureSource.ReadSamplesAsync(cts.Token))
            {
                frameCount++;
                await foreach (var ___ in recorder.EncodeAsync(frame, cts.Token))
                {
                    // FFmpeg muxar direkt
                }

                if (frameCount % 30 == 0)
                {
                    var segCount = Directory.GetFiles(segmentDir, "seg_*.mp4").Length;
                    Console.WriteLine($"  {frameCount} frames, {segCount} segment(s) p\u00e5 disk");
                }
            }
        }
        catch (OperationCanceledException) { /* timeout */ }

        Console.WriteLine();
        Console.WriteLine($"Inspelning klar. Stoppar capture...");
        await captureSource.StopAsync();

        Console.WriteLine($"Flushar FFmpeg (sista segmentet skrivs)...");
        await foreach (var ___ in recorder.FlushAsync())
        {
            // FFmpeg avslutar sista segmentet
        }

        // === DEL 2: Visa vad vi har ===
        var allSegments = new DirectoryInfo(segmentDir).GetFiles("seg_*.mp4")
            .OrderBy(f => f.LastWriteTimeUtc).ToList();

        Console.WriteLine();
        Console.WriteLine($"Segment p\u00e5 disk efter inspelning:");
        foreach (var seg in allSegments)
        {
            Console.WriteLine($"  {seg.Name} - {seg.Length / 1024.0:F0} KB");
        }
        Console.WriteLine();

        // === DEL 3: Extrahera senaste 10 sekunder ===
        Console.WriteLine("Extraherar senaste 10 sekunder till en klipp-MP4...");
        var extractor = new ClipExtractor(locator);
        var result = await extractor.SaveLastSecondsAsync(
            segmentDirectory: segmentDir,
            outputPath: outputPath,
            secondsToCapture: 10,
            segmentDurationSeconds: 5);

        Console.WriteLine();
        Console.WriteLine($"OK!");
        Console.WriteLine($"  Output: {result.OutputPath}");
        Console.WriteLine($"  Segments anvanda: {result.SegmentsUsed}");
        Console.WriteLine($"  Ungefarlig langd: {result.ApproximateDurationSeconds}s");
        Console.WriteLine($"  Filstorlek: {result.FileSizeBytes / 1024.0 / 1024.0:F2} MB");
        Console.WriteLine();
        Console.WriteLine("Dubbelklicka klippet for att spela.");

        return 0;
    }
}
