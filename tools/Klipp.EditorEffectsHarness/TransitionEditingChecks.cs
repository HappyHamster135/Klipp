using System.Diagnostics;
using System.Globalization;
using Klipp.Desktop.Models;
using Klipp.Desktop.Services;
using Klipp.Encoding.FFmpeg;

internal static class TransitionEditingChecks
{
    public static async Task RunAsync(EditorProjectExportService export, FFmpegLocator locator, string directory)
    {
        var green = Path.Combine(directory, "handles-green.mp4");
        var changing = Path.Combine(directory, "handles-red-blue.mp4");
        var red = Path.Combine(directory, "alpha-red.mp4");
        var blue = Path.Combine(directory, "alpha-blue.mp4");
        var gray = Path.Combine(directory, "alpha-gray.mp4");
        await Source(green, "color=c=green:s=320x180:r=30:d=8", 440);
        await Source(changing, "color=c=red:s=320x180:r=30:d=8,drawbox=color=blue:t=fill:enable='gte(t,4)'", 880);
        await Source(red, "color=c=red:s=320x180:r=24:d=2", 440);
        await Source(blue, "color=c=blue:s=320x180:r=60:d=2", 440);
        await Source(gray, "color=c=gray:s=320x180:r=30:d=2", 440);
        var first = Clip(green, "video-1", 0, 0, 4.25, 8);
        var incoming = Clip(changing, "video-1", 3.75, 3.75, 8, 8);
        incoming.TransitionInStyle = "dissolve";
        incoming.TransitionInDurationSeconds = 0.5;
        var project = Project(first, incoming);
        project.AudioTracks = [Audio(green, 0, 0, 4), Audio(changing, 4, 4, 8)];
        project.Normalize();
        Check(Math.Abs(project.DurationSeconds - 8) < 1e-8 && Math.Abs(project.GetTransitionOverlapSeconds(first, incoming) - 0.5) < 1e-8,
            "Source handles changed program duration or transition duration.");
        var touching = Clip(changing, "video-2", 4, 4, 8, 8);
        var previous = Clip(green, "video-2", 0, 0, 4, 8);
        var joined = Project(previous, touching);
        joined.Normalize();
        Check(joined.GetTransitionPredecessor(touching) is null && joined.GetTransitionPredecessor(touching, true)?.Id == previous.Id,
            "A touching join did not resolve for transition editing on an upper row.");
        touching.TimelineStartSeconds = 4.2;
        Check(joined.GetTransitionPredecessor(touching, true) is null, "A transition editor treated a gap as a join.");
        var path = Path.Combine(directory, "transition-source-handles.mp4");
        await export.ExportAsync(project, path);
        var info = await new MediaProbeService(locator).GetMediaInfoAsync(path);
        Check(info is not null && Math.Abs(info.Duration.TotalSeconds - 8) < 0.08, "A handle transition shortened the program.");
        var lead = await Pixel(path, 3.9, 160, 90);
        var tail = await Pixel(path, 4.1, 160, 90);
        Check(lead[0] > 35 && lead[1] > 35 && lead[2] < 10,
            "Incoming leading handles advanced its original blue source frames before the edit point.");
        Check(tail[2] > 140 && tail[0] < 15 && (await Pixel(path, 7.8, 160, 90))[2] > 220,
            "Incoming source timing or original program end moved during the handle transition.");
        Check(await Amplitude(path, 3.6, 440) > 0.07 && await Amplitude(path, 3.6, 880) < 0.003 &&
            await Amplitude(path, 4.1, 880) > 0.07 && await Amplitude(path, 4.1, 440) < 0.003,
            "A video handle transition shifted or crossfaded independent audio.");
        Console.WriteLine("PASS: source-handle transition preserves program/source timing and independent audio; touching/gap upper-row joins");

        var upper = Project(Clip(red, "video-1", 0, 0, 2, 2), Clip(blue, "video-2", 0, 0, 1, 2), Clip(gray, "video-2", 0.6, 0, 1, 2));
        upper.Clips[1].Scale = upper.Clips[2].Scale = 0.5;
        upper.Clips[2].TransitionInStyle = "dissolve";
        upper.Clips[2].TransitionInDurationSeconds = 0.4;
        var upperPath = Path.Combine(directory, "transition-alpha-unaffected-background.mp4");
        await export.ExportAsync(upper, upperPath);
        var corner = await Pixel(upperPath, 0.8, 8, 8);
        Check(corner[0] > 240 && corner[1] < 10 && corner[2] < 10,
            $"A transparent upper-row dissolve darkened its unaffected lower row: {string.Join(',', corner.Take(3))}.");
        Console.WriteLine("PASS: an upper-row dissolve preserves lower-row pixels outside the clip matte");

        var replacement = Project(Clip(red, "video-1", 0, 0, 2, 2), Clip(blue, "video-2", 0, 0, 2, 2), Clip(gray, "video-2", 0.5, 0, 0.8, 2));
        replacement.Clips[1].Scale = 0.5;
        replacement.Clips[2].Scale = 0.25;
        foreach (var transition in new[] { false, true })
        {
            replacement.Clips[2].TransitionInStyle = transition ? "dissolve" : "cut";
            replacement.Clips[2].TransitionInDurationSeconds = 0.2;
            var destination = Path.Combine(directory, $"transition-row-replacement-{transition}.mp4");
            await export.ExportAsync(replacement, destination);
            var beforeReplacement = await Pixel(destination, 0.3, 210, 90);
            var duringReplacement = await Pixel(destination, 0.9, 210, 90);
            var afterReplacement = await Pixel(destination, 1.5, 210, 90);
            Check(beforeReplacement[2] > 230 && duringReplacement[0] > 230 && duringReplacement[2] < 10 && afterReplacement[2] > 230,
                "A transparent newer clip did not hide/restore older footage on its own row.");
            Check((await Pixel(destination, 0.9, 160, 90))[1] is > 110 and < 145,
                "Replacing a transparent same-row clip lost the incoming picture.");
            if (transition)
            {
                var transitionCenter = await Pixel(destination, 0.6, 160, 90);
                var transitionEdge = await Pixel(destination, 0.6, 210, 90);
                var unaffectedLower = await Pixel(destination, 0.6, 8, 8);
                Check(transitionCenter[0] > 30 && transitionCenter[2] > transitionCenter[0] + 40 && transitionEdge[2] > 230 && unaffectedLower[0] > 240,
                    "Same-row visibility exclusion removed the outgoing picture during its transition.");
            }
        }
        Console.WriteLine("PASS: transparent newer clips hide older same-row footage after cuts/transitions and restore it afterward");

        foreach (var style in new[] { "dissolve", "fadeblack", "fadewhite", "wipeleft", "wiperight", "wipeup", "wipedown", "slideleft", "slideright", "slideup", "slidedown" })
        {
            var transitions = Project(Clip(red, "video-1", 0, 0, 1, 2), Clip(blue, "video-1", 0.6, 0, 1, 2));
            transitions.Clips[1].TransitionInStyle = style;
            transitions.Clips[1].TransitionInDurationSeconds = 0.4;
            var destination = Path.Combine(directory, $"transition-alpha-{style}.mp4");
            await export.ExportAsync(transitions, destination);
            var center = await Pixel(destination, 0.8, 160, 90);
            if (style == "dissolve") Check(center[0] > 100 && center[2] > 100, "RGB dissolve lost its blend.");
            else if (style == "fadeblack") Check(center.Take(3).All(value => value < 10), "RGB dip did not reach black.");
            else if (style == "fadewhite") Check(center.Take(3).All(value => value > 245), "RGB dip did not reach white.");
            else
            {
                var vertical = style.EndsWith("up") || style.EndsWith("down");
                var near = await Pixel(destination, 0.8, vertical ? 160 : 80, vertical ? 45 : 90);
                var far = await Pixel(destination, 0.8, vertical ? 160 : 240, vertical ? 135 : 90);
                Check(style.EndsWith("right") || style.EndsWith("down") ? near[2] > 180 && far[0] > 180 : near[0] > 180 && far[2] > 180,
                    $"RGB {style} changed its reveal direction.");
            }
        }
        var fullRange = Path.Combine(directory, "transition-full-range-red.mp4");
        await Run(["-i", red, "-vf", "scale=in_range=tv:out_range=pc", "-color_range", "pc", "-c:v", "libx264", "-pix_fmt", "yuv420p", "-an", fullRange]);
        foreach (var style in new[] { "fadeblack", "fadewhite" })
        {
            var dips = Project(Clip(fullRange, "video-1", 0, 0, 1, 2), Clip(fullRange, "video-1", 0.6, 0, 1, 2));
            dips.Clips[1].TransitionInStyle = style; dips.Clips[1].TransitionInDurationSeconds = 0.4;
            var destination = Path.Combine(directory, $"transition-full-range-{style}.mp4");
            await export.ExportAsync(dips, destination);
            var pixel = await Pixel(destination, 0.8, 160, 90);
            Check(style == "fadeblack" ? pixel.Take(3).All(value => value < 8) : pixel.Take(3).All(value => value > 247),
                "RGB dip color changed for full-range source media.");
        }
        Console.WriteLine("PASS: all11 mixed-frame-rate RGB/alpha transitions, directions, and full-range black/white dips");

        async Task Source(string destination, string filter, int frequency) => await Run(["-f", "lavfi", "-i", filter,
            "-f", "lavfi", "-i", $"sine=frequency={frequency}:sample_rate=48000:duration=8", "-map", "0:v:0", "-map", "1:a:0",
            "-shortest", "-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p", "-c:a", "aac", destination]);
        async Task<byte[]> Pixel(string source, double seconds, int x, int y) => await Run(["-ss", F(seconds), "-i", source,
            "-frames:v", "1", "-vf", $"crop=2:2:{x}:{y}", "-pix_fmt", "rgb24", "-f", "rawvideo", "pipe:1"]);
        async Task<double> Amplitude(string source, double start, double frequency)
        {
            var bytes = await Run(["-ss", F(start), "-i", source, "-t", "0.15", "-vn", "-ac", "1", "-ar", "48000", "-f", "f32le", "pipe:1"]);
            var real = 0d; var imaginary = 0d; var count = bytes.Length / 4;
            for (var index = 0; index < count; index++)
            {
                var sample = BitConverter.ToSingle(bytes, index * 4);
                var phase = 2 * Math.PI * frequency * index / 48000;
                real += sample * Math.Cos(phase); imaginary += sample * Math.Sin(phase);
            }
            return 2 * Math.Sqrt(real * real + imaginary * imaginary) / Math.Max(1, count);
        }
        async Task<byte[]> Run(string[] arguments)
        {
            var start = new ProcessStartInfo(locator.FFmpegPath) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
            foreach (var argument in new[] { "-hide_banner", "-loglevel", "error", "-y" }.Concat(arguments)) start.ArgumentList.Add(argument);
            using var process = Process.Start(start)!; using var data = new MemoryStream();
            var reading = process.StandardOutput.BaseStream.CopyToAsync(data); var errors = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync(); await reading; Check(process.ExitCode == 0, await errors); return data.ToArray();
        }
    }
    private static EditorProject Project(params EditorProjectClip[] clips) => new() { CanvasWidth = 320, CanvasHeight = 180, Clips = clips.ToList(),
        Settings = new() { ExportEncoder = "software", ExportFrameRate = 30, ExportQuality = "draft" } };
    private static EditorProjectClip Clip(string source, string row, double start, double trimStart, double trimEnd, double duration) => new()
    { SourcePath = source, TrackId = row, TimelineStartSeconds = start, TrimStartSeconds = trimStart, TrimEndSeconds = trimEnd,
        SourceDurationSeconds = duration, SourceAudioSeparated = true };
    private static EditorProjectAudioTrack Audio(string source, double start, double trimStart, double trimEnd) => new()
    { SourcePath = source, TimelineStartSeconds = start, TrimStartSeconds = trimStart, TrimEndSeconds = trimEnd, SourceDurationSeconds = 8 };
    private static string F(double value) => value.ToString("0.#########", CultureInfo.InvariantCulture);
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
