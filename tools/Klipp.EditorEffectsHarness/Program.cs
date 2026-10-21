using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Klipp.Desktop.Models;
using Klipp.Desktop.Services;
using Klipp.Encoding.FFmpeg;

// Integration checks against the actual editor services and FFmpeg, without WinUI.
// Run: dotnet run --project tools/Klipp.EditorEffectsHarness -- <temporary-directory>
CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
var smokeDirectory = Path.GetFullPath(args.Length >= 1
    ? args[0]
    : Path.Combine(Path.GetTempPath(), "Klipp", "EffectsSmoke", Guid.NewGuid().ToString("N")));
Directory.CreateDirectory(smokeDirectory);
var locator = new FFmpegLocator();
if (!locator.IsInstalled)
    throw new InvalidOperationException("Install the app's FFmpeg binaries before running the effects smoke checks.");
if (args.Contains("--look-thumbnails-only"))
{
    await LookThumbnailChecks.RunAsync(locator, smokeDirectory);
    return;
}
var edit = new ClipEditService(locator);
var probe = new MediaProbeService(locator);
var export = new EditorProjectExportService(locator,
    new EditorTimelineRenderCacheService(Path.Combine(smokeDirectory, "cache"), probe));
if (args.Contains("--audio-ducking-only"))
{
    await AudioDuckingChecks.RunAsync(export, locator, smokeDirectory);
    return;
}
if (args.Contains("--separate-capture-audio-only"))
{
    await SeparateCaptureAudioChecks.RunAsync(export, locator, smokeDirectory);
    return;
}
if (args.Contains("--effect-layers-only"))
{
    await EffectLayerChecks.RunAsync(export, locator, smokeDirectory);
    return;
}
if (args.Contains("--effect-styles-only"))
{
    await EffectStyleLayerChecks.RunAsync(export, locator, smokeDirectory);
    return;
}
if (args.Contains("--effect-region-isolation-only"))
{
    await EffectRegionIsolationChecks.RunAsync(export, locator, smokeDirectory);
    return;
}
if (args.Contains("--effect-region-animation-only"))
{
    await EffectRegionMotionChecks.RunAsync(export, locator, smokeDirectory);
    return;
}
if (args.Contains("--transition-editing-only"))
{
    await TransitionEditingChecks.RunAsync(export, locator, smokeDirectory);
    return;
}
if (args.Contains("--transition-families-only"))
{
    await TransitionFamilyChecks.RunAsync(smokeDirectory);
    return;
}
if (args.Contains("--video-processing-only"))
{
    await VideoProcessingChecks.RunAsync(export, locator, smokeDirectory);
    return;
}
if (args.Contains("--filter-parity-only") || args.Contains("--curves-only"))
{
    await FilterParityChecks.RunAsync(export, locator, smokeDirectory, curvesOnly: args.Contains("--curves-only"));
    return;
}
var redSource = Path.Combine(smokeDirectory, "red-24.mp4");
var blueSource = Path.Combine(smokeDirectory, "blue-60.mp4");
var graySource = Path.Combine(smokeDirectory, "gray.mp4");
var portraitSource = Path.Combine(smokeDirectory, "portrait-blue.mp4");
await SourceAsync(redSource, "red", 24, "mono");
await SourceAsync(blueSource, "blue", 60, "stereo");
await SourceAsync(graySource, "gray", 30, "stereo");
await SourceAsync(portraitSource, "blue", 30, "stereo", "180x320");
if (args.Contains("--tracks-only"))
{
    await TrackEffectChecks.RunAsync(export, locator, smokeDirectory, redSource, blueSource, graySource);
    return;
}
if (args.Contains("--free-timeline-only"))
{
    await FreeTimelineChecks.RunAsync(export, locator, smokeDirectory, redSource, blueSource, graySource);
    return;
}
var changingSource = Path.Combine(smokeDirectory, "red-then-blue.mp4");
await RunAsync(locator.FFmpegPath,
    ["-hide_banner", "-loglevel", "error", "-y", "-f", "lavfi", "-i",
     "color=c=red:s=320x180:r=30:d=2,drawbox=color=blue:t=fill:enable='gte(t,1)'",
     "-c:v", "libx264", "-g", "60", "-pix_fmt", "yuv420p", changingSource]);

if (args.Contains("--dense-envelope-only"))
{
    await AssertDenseVolumeExportAsync();
    return;
}

foreach (var (speed, freeze, expected) in new[] { (0.5, 0d, 2d), (2d, 0d, 0.5), (1d, 0.75, 1.75) })
{
    var path = Path.Combine(smokeDirectory, $"timing-{speed}-{freeze}.mp4");
    await edit.TrimAsync(redSource, path, TimeSpan.FromSeconds(0.25), TimeSpan.FromSeconds(1.25),
        new ClipEditOptions(playbackSpeed: speed, freezeFrameSeconds: freeze, frameRate: 30));
    await AssertDurationAsync(path, expected);
}
var preciseTrim = Path.Combine(smokeDirectory, "precise-trim.mp4");
await edit.TrimAsync(changingSource, preciseTrim, TimeSpan.FromSeconds(1.25), TimeSpan.FromSeconds(1.75));
var firstFrame = await PixelAsync(preciseTrim, 0, 160, 90);
Assert(firstFrame.B > 160 && firstFrame.R < 35, "A non-keyframe trim included video before its selected in-point.");
var freezeTrim = Path.Combine(smokeDirectory, "freeze-selected-frame.mp4");
await edit.TrimAsync(changingSource, freezeTrim, TimeSpan.Zero, TimeSpan.FromSeconds(0.75),
    new ClipEditOptions(freezeFrameSeconds: 0.5, frameRate: 30));
var frozenFrame = await PixelAsync(freezeTrim, 1.1, 160, 90);
Assert(frozenFrame.R > 160 && frozenFrame.B < 35, "The freeze read beyond the selected out-point.");
Console.WriteLine("PASS: accurate trims, slow/fast motion, freeze tail durations");

var transitionProject = Project(Clip(redSource), Clip(blueSource));
transitionProject.Clips[1].TransitionInStyle = "dissolve";
transitionProject.Clips[1].TransitionInDurationSeconds = 0.4;
var transitionPath = Path.Combine(smokeDirectory, "mixed-fps-transition.mp4");
await export.ExportAsync(transitionProject, transitionPath);
await AssertDurationAsync(transitionPath, 3.6);
var middle = await PixelAsync(transitionPath, 1.8, 160, 90);
Assert(middle.R > 35 && middle.B > 35, "Mixed-fps dissolve did not blend the clips.");
Console.WriteLine("PASS: mixed 24/60 fps dissolve and mono/stereo audio");

var transitions = new[]
{
    "dissolve", "fadeblack", "fadewhite", "wipeleft", "wiperight", "wipeup", "wipedown",
    "slideleft", "slideright", "slideup", "slidedown"
};
foreach (var style in transitions)
{
    var project = Project(Clip(redSource), Clip(blueSource));
    project.Clips[1].TransitionInStyle = style;
    project.Clips[1].TransitionInDurationSeconds = 0.4;
    var path = Path.Combine(smokeDirectory, $"transition-{style}.mp4");
    await export.ExportAsync(project, path);
    Assert(project.Clips[1].TransitionInStyle == style, $"Normalization discarded {style}.");
    await AssertDurationAsync(path, 3.6);
    var frame = await FrameAsync(path, 1.8);
    var center = Sample(frame, 160, 90);
    if (style == "fadeblack")
        Assert(center.R < 45 && center.G < 45 && center.B < 45, $"Dip to black never reached black: {center}.");
    else if (style == "fadewhite")
        Assert(center.R > 220 && center.G > 220 && center.B > 220, "Dip to white never reached white.");
    else if (style == "dissolve")
        Assert(center.R > 35 && center.B > 35, "Dissolve did not blend both clips.");
    else
    {
        var vertical = style.EndsWith("up") || style.EndsWith("down");
        var first = Sample(frame, vertical ? 160 : 80, vertical ? 45 : 90);
        var second = Sample(frame, vertical ? 160 : 240, vertical ? 135 : 90);
        var incomingFromStart = style.EndsWith("right") || style.EndsWith("down");
        Assert(incomingFromStart
                ? first.B > 180 && second.R > 180
                : first.R > 180 && second.B > 180,
            $"{style} did not reveal both clips in the correct direction.");
    }
}
Console.WriteLine("PASS: all 11 transitions render with mixed frame rates, exact overlap and visible transition");

var fullRangeSource = Path.Combine(smokeDirectory, "red-full-range.mp4");
await RunAsync(locator.FFmpegPath,
    ["-hide_banner", "-loglevel", "error", "-y", "-i", redSource, "-vf", "scale=in_range=tv:out_range=pc",
     "-color_range", "pc", "-c:v", "libx264", "-pix_fmt", "yuv420p", "-c:a", "copy", fullRangeSource]);
foreach (var style in new[] { "fadeblack", "fadewhite" })
{
    var project = Project(Clip(fullRangeSource), Clip(fullRangeSource));
    project.Clips[1].TransitionInStyle = style;
    project.Clips[1].TransitionInDurationSeconds = 0.4;
    var path = Path.Combine(smokeDirectory, $"full-range-{style}.mp4");
    await export.ExportAsync(project, path);
    var center = await PixelAsync(path, 1.8, 160, 90);
    Assert(style == "fadeblack"
            ? center.R < 6 && center.G < 6 && center.B < 6
            : center.R > 248 && center.G > 248 && center.B > 248,
        $"{style} produced the wrong dip color for full-range media: {center}.");
}
Console.WriteLine("PASS: black/white dip colors remain correct with full-range video");

// A hard cut followed by an animated transition exercises concat -> xfade clock negotiation.
var chainedProject = Project(Clip(redSource), Clip(blueSource), Clip(redSource));
chainedProject.Clips[1].TransitionInStyle = "cut";
chainedProject.Clips[2].TransitionInStyle = "slideright";
chainedProject.Clips[2].TransitionInDurationSeconds = 0.4;
var chainedPath = Path.Combine(smokeDirectory, "cut-then-slide.mp4");
await export.ExportAsync(chainedProject, chainedPath);
await AssertDurationAsync(chainedPath, 5.6);
Console.WriteLine("PASS: mixed hard cuts and animated transition chain");

var toneSource = Path.Combine(smokeDirectory, "two-tones.wav");
await RunAsync(locator.FFmpegPath,
    ["-hide_banner", "-loglevel", "error", "-y", "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=48000:duration=1",
     "-f", "lavfi", "-i", "sine=frequency=880:sample_rate=48000:duration=1",
     "-filter_complex", "[0:a][1:a]concat=n=2:v=0:a=1[a]", "-map", "[a]", "-c:a", "pcm_s16le", toneSource]);
var audioProject = Project(Clip(redSource));
audioProject.Clips[0].Muted = true;
audioProject.AudioTracks =
[
    new() { SourcePath = toneSource, SourceDurationSeconds = 2, TrimStartSeconds = 0.5, TrimEndSeconds = 1,
        TimelineStartSeconds = 0.25, PlaybackSpeed = 0.5 },
    new() { SourcePath = toneSource, SourceDurationSeconds = 2, TrimStartSeconds = 1, TrimEndSeconds = 1.5,
        TimelineStartSeconds = 1.25, PlaybackSpeed = 0.5,
        VolumeKeyframes =
        [
            new() { TimeSeconds = 0, Volume = 0.15 },
            new() { TimeSeconds = 0.4, Volume = 0.15 },
            new() { TimeSeconds = 0.6, Volume = 1, Easing = "ease-in-out" },
            new() { TimeSeconds = 1, Volume = 1 }
        ] }
];
var audioPath = Path.Combine(smokeDirectory, "detached-split-slow-audio.mp4");
await export.ExportAsync(audioProject, audioPath);
await AssertDurationAsync(audioPath, 2.25);
var pcmBytes = await RunAsync(locator.FFmpegPath,
    ["-hide_banner", "-loglevel", "error", "-i", audioPath, "-vn", "-ac", "1", "-ar", "48000", "-f", "f32le", "pipe:1"]);
var samples = new float[pcmBytes.Length / sizeof(float)];
Buffer.BlockCopy(pcmBytes, 0, samples, 0, pcmBytes.Length);
Assert(AudioRms(samples, 0.05, 0.15) < 0.001, "Detached audio started before its timeline position.");
Assert(Math.Abs(AudioFrequency(samples, 0.4, 0.8) - 440) < 20, "First audio cut used the wrong source region or changed pitch.");
Assert(Math.Abs(AudioFrequency(samples, 1.5, 1.9) - 880) < 20, "Second audio cut used the wrong source region or playback speed.");
Assert(AudioRms(samples, 1.22, 1.28) > 0.025, "Splitting detached audio introduced a silent gap.");
Assert(AudioRms(samples, 2.1, 2.2) > 0.025, "Slowed detached audio was cut off at source duration.");
Assert(AudioRms(samples, 2, 2.15) > AudioRms(samples, 1.4, 1.55) * 4,
    "Detached audio did not preserve its local volume envelope.");
Console.WriteLine("PASS: independent audio cuts preserve slow-motion timing, pitch, delay, volume envelope and gapless join");

var shortAudio = new EditorProjectAudioTrack
{
    SourcePath = toneSource, SourceDurationSeconds = 2,
    TrimStartSeconds = 0.5, TrimEndSeconds = 0.6,
    TimelineStartSeconds = 0.25, PlaybackSpeed = 4
};
shortAudio.Normalize();
var shortVideo = Clip(redSource);
shortVideo.TrimStartSeconds = 0.5;
shortVideo.TrimEndSeconds = 0.6;
shortVideo.PlaybackSpeed = 4;
shortVideo.Normalize();
Assert(Math.Abs(shortAudio.EffectiveDurationSeconds - 0.025) < 0.000001 &&
       Math.Abs(shortAudio.EffectiveDurationSeconds - shortVideo.EffectiveDurationSeconds) < 0.000001,
    "A short sped-up audio cut does not match its video duration.");
var shortProject = Project(Clip(redSource));
shortProject.Clips[0].Muted = true;
shortProject.AudioTracks = [shortAudio];
var shortPath = Path.Combine(smokeDirectory, "detached-short-fast-audio.mp4");
await export.ExportAsync(shortProject, shortPath);
var shortBytes = await RunAsync(locator.FFmpegPath,
    ["-hide_banner", "-loglevel", "error", "-i", shortPath, "-vn", "-ac", "1", "-ar", "48000", "-f", "f32le", "pipe:1"]);
var shortSamples = new float[shortBytes.Length / sizeof(float)];
Buffer.BlockCopy(shortBytes, 0, shortSamples, 0, shortBytes.Length);
Assert(AudioRms(shortSamples, 0.251, 0.272) > 0.002, "The short sped-up audio cut disappeared during export.");
Assert(AudioRms(shortSamples, 0.33, 0.4) < 0.002,
    "The short sped-up audio cut played beyond its selected source range (allowing AAC packet padding).");
Console.WriteLine("PASS: a 0.1s source cut at 4x stays 0.025s in both audio and video models and exports audibly");
await AssertDenseVolumeExportAsync();

var chartSource = Path.Combine(smokeDirectory, "effect-chart.mp4");
await RunAsync(locator.FFmpegPath,
    ["-hide_banner", "-loglevel", "error", "-y", "-f", "lavfi", "-i",
     "testsrc2=s=320x180:r=30:d=2", "-c:v", "libx264", "-pix_fmt", "yuv420p", chartSource]);
var chartBaselinePath = Path.Combine(smokeDirectory, "grade-baseline.mp4");
await export.ExportAsync(Project(Clip(chartSource)), chartBaselinePath);
var baselineFrame = await FrameAsync(chartBaselinePath, 0.5);
foreach (var style in new[] { "boost", "warm", "cool", "mono", "cinematic", "sharpen", "blur", "vintage", "vibrant", "noir", "dream", "film", "soft", "tealorange", "glitch", "vignette" })
{
    var project = Project(Clip(chartSource));
    project.Clips[0].Filter = style;
    project.Clips[0].FilterStrength = 0.65;
    var path = Path.Combine(smokeDirectory, $"grade-{style}.mp4");
    await export.ExportAsync(project, path);
    await AssertDurationAsync(path, 2);
    var frame = await FrameAsync(path, 0.5);
    Assert(frame.Length == 320 * 180 * 3, $"{style} did not produce a complete video frame.");
    var meanChange = frame.Zip(baselineFrame, (actual, original) => Math.Abs(actual - original)).Average();
    Assert(meanChange > 0.5, $"{style} did not visibly change the exported picture.");
    if (style is "film" or "soft")
    {
        // Lifted blacks and rolled-off whites are deliberate, subtle tonal changes.
        var shadow = Sample(frame, 300, 150);
        Assert(shadow.R < 250 || shadow.G < 250 || shadow.B < 250, $"{style} clipped the frame to white.");
    }
}
Console.WriteLine("PASS: all 16 effect presets export at partial strength");

var lutPath = Path.Combine(smokeDirectory, "identity grade's [1],test.cube");
const string colors = "0 0 0\n1 0 0\n0 1 0\n1 1 0\n0 0 1\n1 0 1\n0 1 1\n1 1 1\n";
await File.WriteAllTextAsync(lutPath, "TITLE \"Identity #1\"\nLUT_3D_SIZE 2\n" + colors);
var lutService = new CubeLutService();
Assert((await lutService.ValidateAsync(lutPath)).Name == "Identity #1", "A # in a quoted LUT title was treated as a comment.");
foreach (var malformed in new[]
{
    "LUT_3D_SIZE 2\nDOMAIN_MIN 1 0 0\nDOMAIN_MAX 0 1 1\n" + colors,
    "LUT_3D_SIZE 2\nDOMAIN_MIN NaN 0 0\n" + colors,
    "LUT_3D_SIZE 2\nLUT_3D_SIZE 2\n" + colors,
    "LUT_3D_SIZE 2\n0 0 0 1\n" + colors,
    "LUT_3D_SIZE 2\nDOMAIN_OTHER 0 0 0\n" + colors
})
{
    var invalidPath = Path.Combine(smokeDirectory, "invalid.cube");
    await File.WriteAllTextAsync(invalidPath, malformed);
    try
    {
        await lutService.ValidateAsync(invalidPath);
        throw new Exception("An invalid LUT was accepted.");
    }
    catch (InvalidOperationException) { }
}
Console.WriteLine("PASS: LUT title parsing and malformed domain/data rejection");

foreach (var strength in new[] { 1d, 0.5 })
{
    var overlayProject = Project(Clip(redSource));
    var overlay = Clip(portraitSource);
    overlay.Track = EditorClipTrack.Overlay;
    overlay.Scale = 0.5;
    overlay.Filter = "mono";
    overlay.FilterStrength = strength;
    overlay.LutPath = lutPath;
    overlay.LutStrength = 0.5;
    if (strength < 1)
    {
        overlay.TransformKeyframes =
        [
            new() { TimeSeconds = 0, Scale = 0.5 },
            new() { TimeSeconds = 2, Scale = 0.75 }
        ];
    }
    overlayProject.Clips.Add(overlay);
    var path = Path.Combine(smokeDirectory, $"graded-overlay-{strength}.mp4");
    await export.ExportAsync(overlayProject, path);
    var corner = await PixelAsync(path, 0.75, 10, 10);
    Assert(corner.R > 180 && corner.B < 35, "Grading/LUT destroyed an overlay's transparent canvas.");
    var pillarbox = await PixelAsync(path, 0.75, 110, 90);
    Assert(pillarbox.R > 180 && pillarbox.B < 35, "An overlay's aspect-ratio padding became opaque.");
    var center = await PixelAsync(path, 0.75, 160, 90);
    Assert(center.R < 100, "The overlay was missing from the center of the canvas.");
}
Console.WriteLine("PASS: overlay transparency survives full/partial grades and LUTs");

var tailProject = Project(Clip(redSource));
var tailOverlay = Clip(blueSource);
tailOverlay.Track = EditorClipTrack.Overlay;
tailOverlay.TimelineStartSeconds = 1.5;
tailOverlay.Scale = 0.5;
tailProject.Clips.Add(tailOverlay);
var tailPath = Path.Combine(smokeDirectory, "overlay-tail.mp4");
await export.ExportAsync(tailProject, tailPath);
await AssertDurationAsync(tailPath, 3.5);
var tailCenter = await PixelAsync(tailPath, 3, 160, 90);
var tailCorner = await PixelAsync(tailPath, 3, 10, 10);
Assert(tailCenter.B > 160 && tailCorner.R < 25 && tailCorner.B < 25,
    "An overlay extending beyond the primary track did not render over the empty canvas.");
Console.WriteLine("PASS: overlays extending the primary track preserve project duration");

foreach (var amount in new[] { 0.25, 1d })
{
    var project = Project(Clip(graySource));
    project.Clips[0].Vignette = amount;
    var path = Path.Combine(smokeDirectory, $"vignette-{amount}.mp4");
    await export.ExportAsync(project, path);
    var center = await PixelAsync(path, 0.75, 160, 90);
    var corner = await PixelAsync(path, 0.75, 10, 10);
    Assert(corner.R < center.R - 2, "Vignette did not darken the edges.");
    if (amount == 1)
    {
        var weaker = await PixelAsync(Path.Combine(smokeDirectory, "vignette-0.25.mp4"), 0.75, 10, 10);
        Assert(corner.R < weaker.R - 5, "Increasing vignette strength made the vignette weaker.");
    }
}
Console.WriteLine("PASS: vignette darkens edges and increases monotonically");
await FreeTimelineChecks.RunAsync(export, locator, smokeDirectory, redSource, blueSource, graySource);
await TrackEffectChecks.RunAsync(export, locator, smokeDirectory, redSource, blueSource, graySource);
Console.WriteLine(JsonSerializer.Serialize(new { status = "passed", smokeDirectory }));

EditorProject Project(params EditorProjectClip[] clips) => new()
{
    Version = 30, // Existing sequence checks also exercise the one-time position migration.
    CanvasWidth = 320,
    CanvasHeight = 180,
    Clips = clips.Select((clip, index) => { clip.Order = index; return clip; }).ToList(),
    Settings = new EditorProjectSettings { ExportEncoder = "software", ExportFrameRate = 30, ExportQuality = "draft" }
};

static EditorProjectClip Clip(string path) => new()
{
    SourcePath = path,
    SourceDurationSeconds = 2,
    TrimEndSeconds = 2,
    Track = EditorClipTrack.Primary
};

async Task SourceAsync(string path, string color, int frameRate, string channels, string dimensions = "320x180")
{
    await RunAsync(locator.FFmpegPath,
        ["-hide_banner", "-loglevel", "error", "-y", "-f", "lavfi", "-i", $"color=c={color}:s={dimensions}:r={frameRate}:d=2",
         "-f", "lavfi", "-i", $"anullsrc=r=48000:cl={channels}", "-t", "2", "-c:v", "libx264", "-pix_fmt", "yuv420p",
         "-c:a", "aac", path]);
}

async Task AssertDurationAsync(string path, double expected)
{
    var actual = (await probe.GetDurationAsync(path))?.TotalSeconds ?? -1;
    Assert(Math.Abs(actual - expected) < 0.09, $"Expected {expected:0.###}s, got {actual:0.###}s in {Path.GetFileName(path)}.");
}

async Task AssertDenseVolumeExportAsync()
{
    var scriptsBefore = Directory.EnumerateFiles(Path.GetTempPath(), "klipp-timeline-filter-*.txt")
        .ToHashSet(StringComparer.OrdinalIgnoreCase);
    var denseSource = Path.Combine(smokeDirectory, "dense-volume-source.wav");
    await RunAsync(locator.FFmpegPath,
        ["-hide_banner", "-loglevel", "error", "-y", "-f", "lavfi", "-i",
         "sine=frequency=440:sample_rate=48000:duration=4.2", "-c:a", "pcm_s16le", denseSource]);
    EditorProject? lastProject = null;
    foreach (var count in new[] { 241, 500 })
    {
        var duration = (count - 1) / 120d;
        var project = Project(Clip(redSource));
        project.Clips[0].Muted = true;
        project.AudioTracks =
        [
            new()
            {
                SourcePath = denseSource, SourceDurationSeconds = 4.2, TrimEndSeconds = duration,
                VolumeKeyframes = Enumerable.Range(0, count)
                    .Select(index => new EditorProjectClipVolumeKeyframe
                    {
                        TimeSeconds = index / 120d,
                        Volume = 0.5 + Math.Sin(index / 30d) * 0.25
                    }).ToList()
            }
        ];
        var path = Path.Combine(smokeDirectory, $"dense-volume-envelope-{count}.mp4");
        await export.ExportAsync(project, path);
        Assert(project.AudioTracks[0].VolumeKeyframes.Count == count, "Normalization removed dense-envelope test keys.");
        await AssertDurationAsync(path, duration);
        var pcmBytes = await RunAsync(locator.FFmpegPath,
            ["-hide_banner", "-loglevel", "error", "-i", path, "-vn", "-ac", "1", "-ar", "48000", "-f", "f32le", "pipe:1"]);
        var samples = new float[pcmBytes.Length / sizeof(float)];
        Buffer.BlockCopy(pcmBytes, 0, samples, 0, pcmBytes.Length);
        Assert(AudioRms(samples, 0.3, 0.4) > AudioRms(samples, 1.1, 1.2) * 2,
            "The dense decision tree selected the wrong gain intervals.");
        lastProject = project;
    }
    Assert(!Directory.EnumerateFiles(Path.GetTempPath(), "klipp-timeline-filter-*.txt")
        .Any(path => !scriptsBefore.Contains(path)), "A successful export left its temporary filter script behind.");

    // A bad input fails after graph preparation, exercising disposal on failure.
    var invalidSource = Path.Combine(smokeDirectory, "invalid-source.mp4");
    await File.WriteAllTextAsync(invalidSource, "Not a valid video.");
    lastProject!.Clips[0].SourcePath = invalidSource;
    var failed = false;
    try
    {
        await export.ExportAsync(lastProject, Path.Combine(smokeDirectory, "expected-failure.mp4"));
    }
    catch (InvalidOperationException)
    {
        failed = true;
    }
    Assert(failed, "The intentionally invalid video unexpectedly exported.");
    Assert(!Directory.EnumerateFiles(Path.GetTempPath(), "klipp-timeline-filter-*.txt")
        .Any(path => !scriptsBefore.Contains(path)), "A failed export left its temporary filter script behind.");
    Console.WriteLine("PASS: 241/500-key audio gain envelopes export correctly and temporary scripts are removed on success/failure");
}

async Task<(byte R, byte G, byte B)> PixelAsync(string path, double seconds, int x, int y)
{
    var bytes = await RunAsync(locator.FFmpegPath,
        ["-hide_banner", "-loglevel", "error", "-ss", seconds.ToString(CultureInfo.InvariantCulture), "-i", path,
         "-vf", $"format=rgb24,crop=1:1:{x}:{y}", "-frames:v", "1", "-f", "rawvideo", "-pix_fmt", "rgb24", "pipe:1"]);
    Assert(bytes.Length == 3, "The exported video did not contain the sampled frame.");
    return (bytes[0], bytes[1], bytes[2]);
}

async Task<byte[]> FrameAsync(string path, double seconds) => await RunAsync(locator.FFmpegPath,
    ["-hide_banner", "-loglevel", "error", "-ss", seconds.ToString(CultureInfo.InvariantCulture), "-i", path,
     "-frames:v", "1", "-f", "rawvideo", "-pix_fmt", "rgb24", "pipe:1"]);

static (byte R, byte G, byte B) Sample(byte[] frame, int x, int y)
{
    var offset = (y * 320 + x) * 3;
    return (frame[offset], frame[offset + 1], frame[offset + 2]);
}

static double AudioRms(float[] samples, double start, double end)
{
    var from = (int)(start * 48000);
    var to = Math.Min(samples.Length, (int)(end * 48000));
    Assert(to > from, "The exported audio did not contain the sampled range.");
    return Math.Sqrt(samples.Skip(from).Take(to - from).Average(sample => (double)sample * sample));
}

static double AudioFrequency(float[] samples, double start, double end)
{
    var from = (int)(start * 48000);
    var to = Math.Min(samples.Length, (int)(end * 48000));
    var crossings = 0;
    for (var i = Math.Max(1, from); i < to; i++)
        if (samples[i - 1] < 0 && samples[i] >= 0) crossings++;
    return crossings * 48000d / (to - from);
}

static async Task<byte[]> RunAsync(string executable, string[] arguments)
{
    var start = new ProcessStartInfo(executable)
    {
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
        CreateNoWindow = true
    };
    foreach (var argument in arguments) start.ArgumentList.Add(argument);
    using var process = Process.Start(start) ?? throw new Exception("FFmpeg did not start.");
    var errors = process.StandardError.ReadToEndAsync();
    using var output = new MemoryStream();
    var reading = process.StandardOutput.BaseStream.CopyToAsync(output);
    await process.WaitForExitAsync();
    await reading;
    var detail = await errors;
    Assert(process.ExitCode == 0, detail);
    return output.ToArray();
}

static void Assert(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}
