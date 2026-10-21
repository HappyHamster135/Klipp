using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Graphics.Canvas;
using Windows.Graphics.DirectX;
using Windows.Media.Core;
using Windows.Media.Playback;

internal static class MediaCopyChecks
{
    public static void RunRangeChecks()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"Klipp-preview-range-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        foreach (var range in new[] { "tv", "pc", "unknown" })
        {
            var source = Path.Combine(directory, $"ramp-{range}.mp4");
            var values = range == "pc" ? "255*X/W" : "16+219*X/W";
            var start = NewFfmpeg();
            foreach (var argument in new[]
            {
                "-v", "error", "-y", "-f", "lavfi", "-i",
                $"nullsrc=s=128x72:r=12:d=2,geq=lum='{values}':cb=128:cr=128",
                "-c:v", "libx264", "-pix_fmt", "yuv420p", "-color_range", range, source
            }) start.ArgumentList.Add(argument);
            using (var encoding = Process.Start(start)!)
            {
                var error = encoding.StandardError.ReadToEnd();
                encoding.WaitForExit();
                if (encoding.ExitCode != 0) throw new InvalidOperationException(error);
            }
            Run(source, attachElement: true, verifyStatic: true, seekSeconds: .75);
        }
    }

    public static void Run(string path, bool attachElement = false, bool verifyStatic = false, double seekSeconds = 10)
    {
        using var player = new MediaPlayer { AutoPlay = false, IsMuted = true, IsVideoFrameServerEnabled = true };
        var element = attachElement ? new Microsoft.UI.Xaml.Controls.MediaPlayerElement() : null;
        element?.SetMediaPlayer(player);
        using var opened = new ManualResetEventSlim();
        using var seeked = new ManualResetEventSlim();
        using var available = new AutoResetEvent(false);
        string? failure = null;
        player.MediaOpened += (_, _) => opened.Set();
        player.MediaFailed += (_, args) => { failure = args.ErrorMessage; opened.Set(); available.Set(); };
        player.VideoFrameAvailable += (_, _) => available.Set();
        player.PlaybackSession.SeekCompleted += (_, _) => seeked.Set();
        player.Source = MediaSource.CreateFromUri(new Uri(Path.GetFullPath(path)));
        if (!opened.Wait(TimeSpan.FromSeconds(10))) throw new InvalidOperationException("MediaOpened timeout.");
        if (failure is not null) throw new InvalidOperationException(failure);
        player.Play();
        Thread.Sleep(200);
        player.Pause();
        available.Reset();
        player.PlaybackSession.Position = TimeSpan.FromSeconds(seekSeconds);
        if (!seeked.Wait(TimeSpan.FromSeconds(5))) throw new InvalidOperationException("Paused seek timeout.");
        var width = (int)player.PlaybackSession.NaturalVideoWidth;
        var height = (int)player.PlaybackSession.NaturalVideoHeight;
        using var device = CanvasDevice.GetSharedDevice();
        using var frame = new CanvasRenderTarget(device, width, height, 96,
            DirectXPixelFormat.B8G8R8A8UIntNormalized, CanvasAlphaMode.Premultiplied);
        var copied = false;
        for (var attempt = 0; attempt < 10; attempt++)
        {
            if (!available.WaitOne(TimeSpan.FromSeconds(2))) continue;
            try
            {
                player.CopyFrameToVideoSurface(frame);
                if (frame.GetPixelBytes().Where((_, index) => index % 4 == 3).Max() == 0) continue;
                copied = true;
                break;
            }
            catch (COMException) { }
        }
        player.Pause();
        if (!copied) throw new InvalidOperationException("No copied frame.");
        if (attachElement)
        {
            Klipp.Desktop.Controls.SafeClipEditorView.VerifyFrameServerModeAfterGraphChanges(player);
            Klipp.Desktop.Controls.SafeClipEditorView.VerifyLiveRegionEditing(player);
        }
        var media = frame.GetPixelBytes();
        using var bitmap = CanvasBitmap.CreateFromBytes(device, new byte[width * height * 4], width, height,
            DirectXPixelFormat.B8G8R8A8UIntNormalized, 96, CanvasAlphaMode.Premultiplied);
        player.CopyFrameToVideoSurface(bitmap);
        var copiedBitmap = bitmap.GetPixelBytes();
        player.CopyFrameToVideoSurface(frame, new Windows.Foundation.Rect(0, 0, width, height));
        var copiedRect = frame.GetPixelBytes();
        var rendered = Klipp.Desktop.Controls.SafeClipEditorView.RenderCopiedVideoForChecks(frame, width, height);
        var start = NewFfmpeg();
        foreach (var arg in new[] { "-v", "error", "-i", path, "-frames:v", "1", "-pix_fmt", "bgra", "-f", "rawvideo", "pipe:1" }) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        using var output = new MemoryStream();
        var error = process.StandardError.ReadToEndAsync();
        process.StandardOutput.BaseStream.CopyTo(output);
        process.WaitForExit();
        if (process.ExitCode != 0) throw new InvalidOperationException(error.Result);
        var reference = output.ToArray();
        if (!verifyStatic) Console.WriteLine($"MediaPlayer CopyFrame {width}x{height}, position {player.PlaybackSession.Position}");
        foreach (var point in new[] { (0.02, 0.02), (0.05, 0.25), (0.1, 0.5), (0.25, 0.1), (0.5, 0.5), (0.75, 0.5), (0.95, 0.5) })
        {
            var x = Math.Min(width - 1, (int)(point.Item1 * width));
            var y = Math.Min(height - 1, (int)(point.Item2 * height));
            var offset = (y * width + x) * 4;
            if (!verifyStatic)
                Console.WriteLine($"{x},{y}: FFmpeg {string.Join(',', reference.Skip(offset).Take(4))}; Target {string.Join(',', media.Skip(offset).Take(4))}; Bitmap {string.Join(',', copiedBitmap.Skip(offset).Take(4))}; Rect {string.Join(',', copiedRect.Skip(offset).Take(4))}; FX {string.Join(',', rendered.Skip(offset).Take(4))}");
            if (verifyStatic)
            {
                for (var channel = 0; channel < 3; channel++)
                {
                    if (Math.Abs(reference[offset + channel] - copiedRect[offset + channel]) > 3)
                        throw new InvalidOperationException("The paused frame-server copy changed the source color range.");
                    if (point.Item1 < .25 || point.Item1 >= .75)
                        if (Math.Abs(copiedRect[offset + channel] - rendered[offset + channel]) > 1)
                            throw new InvalidOperationException("Regional FX changed colors outside the selected ellipse.");
                }
            }
        }
        if (verifyStatic) Console.WriteLine($"PASS: {Path.GetFileName(path)} paused seek, FX rebuilds, and outside-region pixels");
    }

    private static ProcessStartInfo NewFfmpeg() => new(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Klipp", "bin", "ffmpeg.exe"))
    {
        UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true
    };
}
