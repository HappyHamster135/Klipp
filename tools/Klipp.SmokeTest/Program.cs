using System.Diagnostics;
using System.Runtime.InteropServices;
using Klipp.Capture.Models;
using Klipp.Capture.Video;
using Klipp.Core.Models;
using Klipp.Encoding.Audio;
using Klipp.Encoding.FFmpeg;
using Klipp.Encoding.Muxing;
using Klipp.Encoding.Video;

namespace Klipp.SmokeTest;

internal static class Program
{
    [DllImport("user32.dll")]
    private static extern nint MonitorFromPoint(POINT pt, uint dwFlags);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int x, y; }

    private const uint MONITOR_DEFAULTTOPRIMARY = 0x00000001;
    private const int CaptureSeconds = 8;
    private const int AudioWarmupMilliseconds = 150;

    private static async Task<int> Main()
    {
        Console.WriteLine("Klipp Smoke Test - A/V SYNK");
        Console.WriteLine("===========================");
        Console.WriteLine();
        Console.WriteLine("Spelar in video och ljud separat, muxar efter stopp, och kör ffprobe på exakt output-filen.");
        Console.WriteLine("SPELA NÅGOT LJUD under inspelningen om du vill verifiera audio-spåret.");
        Console.WriteLine();

        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        var outputPath = Path.Combine(desktop, $"klipp_sync_{DateTime.Now:yyyyMMdd_HHmmss}.mp4");

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var tempDirectory = Path.Combine(localAppData, "Klipp", "temp", "smoke", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        var tempVideoPath = Path.Combine(tempDirectory, "video.mp4");
        var tempAudioPath = Path.Combine(tempDirectory, "audio.wav");

        var locator = new FFmpegLocator();
        var ffprobePath = await locator.GetFFprobePathAsync();

        try
        {
            var hMonitor = MonitorFromPoint(new POINT { x = 0, y = 0 }, MONITOR_DEFAULTTOPRIMARY);
            var target = new CaptureTarget
            {
                Kind = CaptureTargetKind.Monitor,
                Handle = hMonitor,
                DisplayName = "Primary Monitor"
            };
            var settings = RecordingSettings.Default1080p60 with { FrameRate = 30 };

            using var audioRecorder = new WasapiLoopbackWavRecorder();
            await using var captureSource = new WgcCaptureSource(target);
            await using var encoder = new FFmpegH264Encoder(locator);

            encoder.SetOutputPath(tempVideoPath);
            await encoder.InitializeAsync(settings);

            audioRecorder.Start(tempAudioPath);
            await Task.Delay(AudioWarmupMilliseconds);

            await captureSource.StartAsync();

            long? videoStartTimestamp = null;
            var frameCount = 0;

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(CaptureSeconds));

            try
            {
                await foreach (var frame in captureSource.ReadSamplesAsync(cts.Token))
                {
                    videoStartTimestamp ??= Stopwatch.GetTimestamp();
                    if (frameCount == 0)
                        Console.WriteLine("  Första videoframe mottagen.");

                    await foreach (var _ in encoder.EncodeAsync(frame, cts.Token))
                    {
                        // FFmpeg encodar video-only.
                    }

                    frameCount++;
                }
            }
            catch (OperationCanceledException) { }

            await captureSource.StopAsync();
            await foreach (var _ in encoder.FlushAsync())
            {
                // FFmpeg skriver klart video-only MP4.
            }
            audioRecorder.Stop();

            if (!videoStartTimestamp.HasValue || frameCount == 0)
            {
                Console.WriteLine("Ingen videoframe fångades.");
                return 2;
            }

            var muxer = new FFmpegMp4Muxer(locator);
            var result = await muxer.MuxAsync(
                videoPath: tempVideoPath,
                audioPath: audioRecorder.HasAudio ? tempAudioPath : null,
                outputPath: outputPath,
                videoStartTimestamp: videoStartTimestamp.Value,
                audioStartTimestamp: audioRecorder.HasAudio ? audioRecorder.FirstAudioStartTimestamp : null);

            Console.WriteLine();
            Console.WriteLine("===== RESULTAT =====");
            Console.WriteLine($"  Output:      {result.OutputPath}");
            Console.WriteLine($"  Videoframes: {frameCount}");
            Console.WriteLine($"  Audio:       {(result.HasAudio ? "ja" : "nej")}");
            if (result.AudioOffsetSeconds.HasValue)
                Console.WriteLine($"  Audio start relativt video: {result.AudioOffsetSeconds.Value * 1000.0:F0} ms");

            Console.WriteLine();
            Console.WriteLine("===== FFPROBE =====");
            await PrintProbeAsync(ffprobePath, outputPath);

            return 0;
        }
        finally
        {
            try { Directory.Delete(tempDirectory, recursive: true); } catch { }
        }
    }

    private static async Task PrintProbeAsync(string ffprobePath, string outputPath)
    {
        var streamInfo = await RunFFprobeAsync(
            ffprobePath,
            "-v", "error",
            "-show_entries", "stream=index,codec_type,start_time,duration",
            "-of", "default=noprint_wrappers=1",
            outputPath);

        var formatInfo = await RunFFprobeAsync(
            ffprobePath,
            "-v", "error",
            "-show_entries", "format=duration",
            "-of", "default=noprint_wrappers=1",
            outputPath);

        Console.WriteLine(streamInfo.Trim());
        Console.WriteLine("---");
        Console.WriteLine(formatInfo.Trim());
    }

    private static async Task<string> RunFFprobeAsync(string ffprobePath, params string[] args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = ffprobePath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (var arg in args)
            psi.ArgumentList.Add(arg);

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Kunde inte starta ffprobe.exe");

        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        var stdout = await stdoutTask;
        var stderr = await stderrTask;

        if (process.ExitCode != 0)
            return stderr;

        return stdout;
    }
}
