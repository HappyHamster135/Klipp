using System.Diagnostics;
using Klipp.Core.Editing;
using Klipp.Desktop.Services;
using Klipp.Encoding.FFmpeg;

internal static class MotionTrackingChecks
{
    public static async Task<int> RunAsync(string root)
    {
        var executable = new FFmpegLocator().FFmpegPath;
        if (!File.Exists(executable)) throw new FileNotFoundException("Install FFmpeg before running the real motion tracking checks.", executable);
        const int width = 128, height = 80, frameCount = 48;
        var raw = Path.Combine(root, "moving-subject.gray");
        await using (var stream = File.Create(raw))
        {
            for (var index = 0; index < frameCount; index++)
            {
                var frame = Enumerable.Repeat((byte)20, width * height).ToArray();
                var random = new Random(1947);
                for (var y = 0; y < 18; y++)
                    for (var x = 0; x < 20; x++)
                        frame[(24 + y) * width + 16 + index + x] = (byte)random.Next(50, 220);
                await stream.WriteAsync(frame);
            }
        }
        var video = Path.Combine(root, "moving-subject.mkv");
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (var argument in new[] { "-nostdin", "-hide_banner", "-loglevel", "error", "-threads", "1",
                     "-f", "rawvideo", "-pix_fmt", "gray", "-s", $"{width}x{height}", "-r", "12", "-i", raw,
                     "-an", "-c:v", "ffv1", "-y", video }) start.ArgumentList.Add(argument);
        using (var process = Process.Start(start)!)
        {
            var errors = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            if (process.ExitCode != 0) throw new IOException(await errors);
        }
        var service = new EditorMotionTrackingService();
        var reference = await service.GetReferenceFrameAsync(video, 0);
        if (reference.Png.Length < 100 || reference.Width != 384 || reference.Height != 240)
            throw new InvalidOperationException("FFmpeg reference frame did not preserve the source aspect and expected image size.");
        var result = await service.TrackAsync(video, 0, 4, reference.Width, reference.Height,
            new(16.0 / width, 24.0 / height, 20.0 / width, 18.0 / height));
        if (result.LostSubject || result.Samples.Count < 45)
            throw new InvalidOperationException($"FFmpeg subject tracking stopped early ({result.Samples.Count} samples).");
        var first = result.Samples[0];
        var last = result.Samples[^1];
        var expected = (result.Samples.Count - 1) / (double)width;
        if (Math.Abs(last.CenterX - first.CenterX - expected) > 2.0 / reference.Width ||
            Math.Abs(last.CenterY - first.CenterY) > 1.0 / reference.Height)
            throw new InvalidOperationException("Decoded-frame motion tracking did not follow the known moving subject.");
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        try
        {
            await service.TrackAsync(video, 0, 4, reference.Width, reference.Height, new(0.1, 0.1, 0.2, 0.2),
                cancellationToken: cancelled.Token);
            throw new InvalidOperationException("Cancelled tracking continued decoding.");
        }
        catch (OperationCanceledException) { }
        using var midRun = new CancellationTokenSource();
        try
        {
            var progress = new InlineProgress<EditorMotionTrackingProgress>(value =>
            {
                if (value.SourceOffsetSeconds >= 0.2) midRun.Cancel();
            });
            await service.TrackAsync(video, 0, 4, reference.Width, reference.Height,
                new(16.0 / width, 24.0 / height, 20.0 / width, 18.0 / height), progress, midRun.Token);
            throw new InvalidOperationException("Cancelling a running track did not stop its decoder.");
        }
        catch (OperationCanceledException) { }
        return 5;
    }
}
