using System.Diagnostics;
using System.Security.Cryptography;
using Klipp.Core.Performance;
using Klipp.Desktop.Services;
using Klipp.Encoding.FFmpeg;

internal static class LookThumbnailChecks
{
    public static async Task RunAsync(FFmpegLocator locator, string directory)
    {
        var root = Path.Combine(directory, "look-thumbnails-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var source = Path.Combine(root, "color-chart.mp4");
        await FfmpegAsync(locator, ["-y", "-f", "lavfi", "-i", "testsrc2=s=640x360:r=10:d=2,eq=contrast=0.65:saturation=0.5:brightness=0.08",
            "-c:v", "libx264", "-g", "20", "-pix_fmt", "yuv420p", source]);
        var sourceHash = await HashAsync(source);
        await using var queue = new PriorityBackgroundWorkQueue(2);
        var service = new EditorLookThumbnailService(locator, queue, Path.Combine(root, "pictures"));
        var looks = new[] { "boost", "warm", "cool", "mono", "cinematic", "sharpen", "blur", "vintage", "vibrant", "noir",
            "dream", "film", "soft", "tealorange", "glitch", "vignette", "natural", "matte", "goldenhour", "steel" };
        var previews = await Task.WhenAll(looks.Select(look =>
            service.GetOrCreateAsync(source, 0.25, look, BackgroundWorkPriority.Visible, CancellationToken.None)));
        var frames = new Dictionary<string, byte[]>();
        for (var index = 0; index < looks.Length; index++)
        {
            var path = previews[index];
            Check(path is not null && File.Exists(path), $"No thumbnail was produced for {looks[index]}.");
            var frame = await RgbAsync(locator, path!);
            Check(frame.Length == 320 * 180 * 3, $"{looks[index]} was not a complete 320×180 picture.");
            frames.Add(looks[index], frame);
        }
        Check(Directory.GetFiles(service.CacheDirectory, "*-source.png").Length == 1,
            "Generating all looks decoded more than one source frame.");
        var mono = frames["mono"];
        Check(Enumerable.Range(0, mono.Length / 3).Max(index =>
                Math.Max(Math.Abs(mono[index * 3] - mono[index * 3 + 1]), Math.Abs(mono[index * 3] - mono[index * 3 + 2]))) <= 1,
            "Mono preview did not remove color.");
        var temperatureDifference = MeanDifference(frames["warm"], frames["cool"]);
        Check(temperatureDifference > 1,
            $"Warm and cool previews did not differ on the color chart ({temperatureDifference:0.###}).");
        Check(looks.All(look => look == "mono" || MeanDifference(frames[look], mono) > 1),
            "A color look unexpectedly displayed the mono picture.");
        Console.WriteLine("PASS: all 20 look previews decode to 320×180; real mono and warm/cool filtering; one shared source PNG");

        var first = previews[0]!;
        var written = File.GetLastWriteTimeUtc(first);
        var cached = await service.GetOrCreateAsync(source, 0.25, looks[0], BackgroundWorkPriority.Visible, CancellationToken.None);
        Check(cached == first && File.GetLastWriteTimeUtc(first) == written, "A cache hit rewrote the existing picture.");
        var later = await service.GetOrCreateAsync(source, 1.25, looks[0], BackgroundWorkPriority.Visible, CancellationToken.None);
        Check(later is not null && later != first, "Changing the source sample time reused the previous picture.");
        Check(MeanDifference(await RgbAsync(locator, later!), frames[looks[0]]) > 1,
            "A later source sample did not show the changing chart.");
        var originalWrite = File.GetLastWriteTimeUtc(source);
        try
        {
            File.SetLastWriteTimeUtc(source, originalWrite.AddSeconds(2));
            var revised = await service.GetOrCreateAsync(source, 0.25, looks[0], BackgroundWorkPriority.Visible, CancellationToken.None);
            Check(revised is not null && revised != first, "A changed source file signature reused a stale picture.");
        }
        finally { File.SetLastWriteTimeUtc(source, originalWrite); }
        Console.WriteLine("PASS: cache reuse keeps the picture timestamp; source time and file signature invalidate stale pictures");

        var duplicateService = new EditorLookThumbnailService(locator, queue, Path.Combine(root, "duplicate-pictures"));
        var duplicates = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => duplicateService.GetOrCreateAsync(
            source, 0.25, "film", BackgroundWorkPriority.Visible, CancellationToken.None)));
        Check(duplicates[0] is not null && duplicates.All(path => path == duplicates[0]),
            "Concurrent identical requests did not reuse the same picture.");
        Check(Directory.GetFiles(duplicateService.CacheDirectory, "*.png").Length == 2,
            "Concurrent identical requests left duplicate or partial files.");
        Check(await service.GetOrCreateAsync(Path.Combine(root, "missing.mp4"), 0, "film",
            BackgroundWorkPriority.Visible, CancellationToken.None) is null, "A missing source did not return null.");
        Console.WriteLine("PASS: concurrent requests share the result; missing media returns null");

        await CheckRunningCancellationAsync(locator, queue, root);
        Check(await HashAsync(source) == sourceHash, "Thumbnail generation modified its source media.");
        Console.WriteLine($"PASS: look thumbnail checks; isolated artifacts: {root}");
    }

    private static async Task CheckRunningCancellationAsync(FFmpegLocator locator, PriorityBackgroundWorkQueue queue, string root)
    {
        // A lossless 4K frame gives the cancellation check time to observe the actual decoder process.
        var source = Path.Combine(root, "cancellation-source.mkv");
        await FfmpegAsync(locator, ["-y", "-f", "lavfi", "-i", "testsrc2=s=4096x2160:r=1:d=1",
            "-frames:v", "1", "-c:v", "ffv1", "-level", "3", "-threads", "1", source]);
        var sourceHash = await HashAsync(source);
        var service = new EditorLookThumbnailService(locator, queue, Path.Combine(root, "cancellation-pictures"));
        var before = Process.GetProcessesByName("ffmpeg").Select(process => { using (process) return process.Id; }).ToHashSet();
        using var cancellation = new CancellationTokenSource();
        var request = service.GetOrCreateAsync(source, 0, "film", BackgroundWorkPriority.Interactive, cancellation.Token);
        Process? decoder = null;
        var deadline = Stopwatch.StartNew();
        while (decoder is null && !request.IsCompleted && deadline.Elapsed < TimeSpan.FromSeconds(5))
        {
            foreach (var process in Process.GetProcessesByName("ffmpeg"))
            {
                if (!before.Contains(process.Id) && decoder is null)
                    decoder = process;
                else
                    process.Dispose();
            }
            if (decoder is null)
                await Task.Delay(2);
        }
        Check(decoder is not null && !request.IsCompleted, "Could not observe a running thumbnail decoder for cancellation.");
        using (decoder)
        {
            cancellation.Cancel();
            try
            {
                await request;
                throw new InvalidOperationException("The canceled thumbnail request completed successfully.");
            }
            catch (OperationCanceledException) { }
            await decoder!.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
        }
        Check(!Directory.EnumerateFiles(service.CacheDirectory).Any(path => Path.GetFileName(path).Contains(".tmp", StringComparison.Ordinal)),
            "Cancellation left a partial thumbnail file.");
        Check(await service.GetOrCreateAsync(source, 0, "film", BackgroundWorkPriority.Visible, CancellationToken.None) is { } retry && File.Exists(retry),
            "A canceled request prevented a subsequent request for the same cache key.");
        Check(await HashAsync(source) == sourceHash, "Cancellation changed its source media.");
        Console.WriteLine("PASS: running decoder cancellation exits the process, removes temporary files, and permits a same-key retry");
    }

    private static Task<byte[]> RgbAsync(FFmpegLocator locator, string path) =>
        FfmpegAsync(locator, ["-i", path, "-frames:v", "1", "-f", "rawvideo", "-pix_fmt", "rgb24", "pipe:1"]);

    private static async Task<byte[]> FfmpegAsync(FFmpegLocator locator, string[] arguments)
    {
        var start = new ProcessStartInfo(locator.FFmpegPath)
        {
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true
        };
        foreach (var argument in new[] { "-hide_banner", "-loglevel", "error", "-threads", "1", "-filter_threads", "1" }.Concat(arguments))
            start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("FFmpeg did not start.");
        var error = process.StandardError.ReadToEndAsync();
        using var output = new MemoryStream();
        var reading = process.StandardOutput.BaseStream.CopyToAsync(output);
        await process.WaitForExitAsync();
        await reading;
        Check(process.ExitCode == 0, await error);
        return output.ToArray();
    }

    private static async Task<string> HashAsync(string path)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream));
    }

    private static double MeanDifference(byte[] first, byte[] second) =>
        first.Zip(second, (left, right) => Math.Abs(left - right)).Average();

    private static void Check(bool valid, string message)
    {
        if (!valid) throw new InvalidOperationException(message);
    }
}
