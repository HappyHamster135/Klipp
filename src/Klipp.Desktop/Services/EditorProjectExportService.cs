using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Klipp.Core.Editing;
using Klipp.Desktop.Models;
using Klipp.Encoding.FFmpeg;

namespace Klipp.Desktop.Services;

public sealed class EditorProjectExportService
{
    private readonly FFmpegLocator _locator;
    private readonly ClipEditService _clipEditService;
    private readonly FFmpegEncoderService _encoderService;
    private readonly EditorTimelineRenderCacheService _renderCacheService;

    public EditorProjectExportService(
        FFmpegLocator? locator = null,
        EditorTimelineRenderCacheService? renderCacheService = null)
    {
        _locator = locator ?? new FFmpegLocator();
        _clipEditService = new ClipEditService(_locator);
        _encoderService = new FFmpegEncoderService(_locator);
        _renderCacheService = renderCacheService ?? new EditorTimelineRenderCacheService(
            mediaProbeService: new MediaProbeService(_locator));
    }

    public EditorRenderCacheRun LastRenderCacheRun { get; private set; } = EditorRenderCacheRun.Empty;

    public async Task ExportAsync(
        EditorProject project,
        string outputPath,
        CancellationToken cancellationToken = default)
    {
        await ExportAsync(project, outputPath, progress: null, cancellationToken).ConfigureAwait(false);
    }

    public async Task ExportAsync(
        EditorProject project,
        string outputPath,
        IProgress<double>? progress,
        CancellationToken cancellationToken = default)
    {
        progress?.Report(0);
        project.Normalize();
        if (project.Clips.Count == 0 && project.AudioTracks.Count == 0 && project.GraphicLayers.Count == 0)
            throw new InvalidOperationException("Add video or audio to the timeline before exporting.");

        var missingMedia = project.Clips
            .Where(clip => project.IsTrackActive(clip.TrackId) && clip.IsMissing)
            .Select(clip => clip.Name)
            .Concat(project.Clips
                .Where(clip => project.IsTrackActive(clip.TrackId) && HasClipLut(clip) &&
                               !File.Exists(clip.LutPath))
                .Select(clip => $"{clip.Name} LUT"))
            .Concat(project.GraphicLayers.Where(layer => layer.IsMissing).Select(layer => layer.Name))
            .Concat(ActiveEffectLayers(project).Where(layer => layer.Style is { } style && HasClipLut(style) &&
                    !File.Exists(style.LutPath)).Select(layer => $"{layer.Name} LUT"))
            .Concat(project.AudioTracks.Where(track => project.IsTrackActive(track.TrackId) && track.IsMissing).Select(track => track.Name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (missingMedia.Count > 0)
        {
            throw new InvalidOperationException(
                $"Project has missing media: {string.Join(", ", missingMedia)}. Relink media before export.");
        }

        var lutService = new CubeLutService();
        foreach (var lutPath in project.Clips.Where(clip => project.IsTrackActive(clip.TrackId) && HasClipLut(clip))
                     .Select(clip => clip.LutPath).Concat(ActiveEffectLayers(project)
                         .Where(layer => layer.Style is { } style && HasClipLut(style)).Select(layer => layer.Style!.LutPath))
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            await lutService.ValidateAsync(lutPath, cancellationToken).ConfigureAwait(false);
        }

        var encoder = await _encoderService.ResolveAsync(project.Settings.ExportEncoder, cancellationToken)
            .ConfigureAwait(false);

        try
        {
            await ExportWithEncoderAsync(project, outputPath, encoder.Codec, progress, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception) when (encoder.IsHardware && !cancellationToken.IsCancellationRequested)
        {
            try { File.Delete(outputPath); } catch { }
            await ExportWithEncoderAsync(
                    project,
                    outputPath,
                    FFmpegEncoderService.SoftwareCodec,
                    progress,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        progress?.Report(1);
    }

    private async Task ExportWithEncoderAsync(
        EditorProject project,
        string outputPath,
        string videoEncoder,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        var renderDirectory = Path.Combine(Path.GetTempPath(), "Klipp", "ProjectRenders");
        Directory.CreateDirectory(renderDirectory);
        var basePath = Path.Combine(renderDirectory, $"{project.Id}-{Guid.NewGuid():N}-base.mp4");

        try
        {
            await RenderTimelineAsync(
                    project,
                    basePath,
                    videoEncoder,
                    progress is null ? null : new MappedProgress(progress, 0.02, 0.68),
                    cancellationToken)
                .ConfigureAwait(false);
            progress?.Report(0.70);
            var settings = project.Settings;
            var options = new ClipEditOptions(
                audioVolume: settings.AudioVolume,
                muteAudio: settings.MuteAudio,
                videoFilter: settings.VideoFilter,
                textOverlay: settings.Text,
                textStyle: settings.TextStyle,
                textPosition: "custom",
                elementOverlay: settings.ElementOverlay,
                fadeInSeconds: settings.FadeInSeconds,
                fadeOutSeconds: settings.FadeOutSeconds,
                freezeFrameSeconds: settings.FreezeFrameSeconds,
                subtitleText: settings.SubtitleText,
                textEnabled: settings.TextEnabled,
                textStart: TimeSpan.FromSeconds(settings.TextStartSeconds),
                textEnd: TimeSpan.FromSeconds(settings.TextEndSeconds),
                vfxEnabled: settings.VfxEnabled,
                vfxStart: TimeSpan.FromSeconds(settings.VfxStartSeconds),
                vfxEnd: TimeSpan.FromSeconds(settings.VfxEndSeconds),
                subtitleEnabled: settings.SubtitleEnabled,
                subtitleStart: TimeSpan.FromSeconds(settings.SubtitleStartSeconds),
                subtitleEnd: TimeSpan.FromSeconds(settings.SubtitleEndSeconds),
                elementEnabled: settings.ElementEnabled,
                elementStart: TimeSpan.FromSeconds(settings.ElementStartSeconds),
                elementEnd: TimeSpan.FromSeconds(settings.ElementEndSeconds),
                textAnchorX: settings.TextAnchorX,
                textAnchorY: settings.TextAnchorY,
                textSize: settings.TextSize,
                subtitleStyle: settings.SubtitleStyle,
                subtitleAnchorX: settings.SubtitleAnchorX,
                subtitleAnchorY: settings.SubtitleAnchorY,
                subtitleSize: settings.SubtitleSize,
                videoScale: settings.VideoScale,
                videoOffsetX: settings.VideoOffsetX,
                videoOffsetY: settings.VideoOffsetY,
                // RenderTimelineAsync has already fitted every source to the project's canvas.
                // Applying the legacy 1280x720 fit here would overwrite vertical and square exports.
                fitToCanvas: false,
                textAnimation: settings.TextAnimation,
                textBoxWidth: settings.TextBoxWidth,
                subtitleBoxWidth: settings.SubtitleBoxWidth,
                extraTextLayers: project.ExtraTextLayers
                    .Select(layer => new ClipTextOverlay(
                        layer.Text,
                        layer.Style,
                        layer.Animation,
                        layer.AnchorX,
                        layer.AnchorY,
                        layer.Size,
                        layer.BoxWidth,
                        layer.StartSeconds,
                        layer.EndSeconds,
                        isSubtitle: false,
                        opacity: layer.Opacity,
                        keyframes: layer.Keyframes.Select(ToClipTextKeyframe).ToList(),
                        fontFamily: layer.FontFamily,
                        color: layer.Color,
                        fontFilePath: layer.FontFilePath,
                        fontFamilyName: layer.FontFamilyName))
                    .Concat(project.SubtitleLayers.Select(layer => new ClipTextOverlay(
                        layer.Text,
                        layer.Style,
                        "none",
                        layer.AnchorX,
                        layer.AnchorY,
                        layer.Size,
                        layer.BoxWidth,
                        layer.StartSeconds,
                        layer.EndSeconds,
                        isSubtitle: true,
                        opacity: layer.Opacity,
                        keyframes: layer.Keyframes.Select(ToClipTextKeyframe).ToList(),
                        fontFamily: layer.FontFamily,
                        color: layer.Color,
                        fontFilePath: layer.FontFilePath,
                        fontFamilyName: layer.FontFamilyName)))
                    .ToList(),
                videoCrf: GetVideoCrf(settings.ExportQuality),
                frameRate: settings.ExportFrameRate,
                videoEncoder: videoEncoder,
                videoBitrateMbps: settings.ExportBitrateMbps,
                exportQuality: settings.ExportQuality,
                outputWidth: project.OutputWidth == project.CanvasWidth && project.OutputHeight == project.CanvasHeight
                    ? 0
                    : project.OutputWidth,
                outputHeight: project.OutputWidth == project.CanvasWidth && project.OutputHeight == project.CanvasHeight
                    ? 0
                    : project.OutputHeight,
                audioBitrateKbps: settings.ExportAudioBitrateKbps,
                textOpacity: settings.TextOpacity,
                textKeyframes: settings.TextKeyframes.Select(ToClipTextKeyframe).ToList(),
                textFontFamily: settings.TextFontFamily,
                textColor: settings.TextColor,
                subtitleFontFamily: settings.SubtitleFontFamily,
                subtitleColor: settings.SubtitleColor,
                textFontFilePath: settings.TextFontFilePath,
                textFontFamilyName: settings.TextFontFamilyName,
                subtitleFontFilePath: settings.SubtitleFontFilePath,
                subtitleFontFamilyName: settings.SubtitleFontFamilyName);

            await _clipEditService.TrimAsync(
                basePath,
                outputPath,
                TimeSpan.Zero,
                TimeSpan.FromSeconds(project.DurationSeconds),
                options,
                progress is null ? null : new MappedProgress(progress, 0.70, 0.29),
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            try { File.Delete(basePath); } catch { }
        }
    }

    private async Task RenderTimelineAsync(
        EditorProject project,
        string outputPath,
        string videoEncoder,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        var segments = _renderCacheService.BuildSegments(project, videoEncoder);
        var segmentPaths = new List<string>(segments.Count);
        var cacheHits = 0;
        var cacheMisses = 0;
        for (var index = 0; index < segments.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var segment = segments[index];
            var segmentProgress = progress is null
                ? null
                : new MappedProgress(progress, index / (double)segments.Count, 1d / segments.Count);
            var entry = await _renderCacheService.GetOrCreateSegmentAsync(
                    project,
                    segment,
                    (temporaryPath, renderProgress, token) => RenderTimelineCoreAsync(
                        project,
                        temporaryPath,
                        videoEncoder,
                        segment.StartSeconds,
                        segment.EndSeconds,
                        renderProgress,
                        token),
                    segmentProgress,
                    cancellationToken)
                .ConfigureAwait(false);
            segmentPaths.Add(entry.Path);
            if (entry.CacheHit)
                cacheHits++;
            else
                cacheMisses++;
        }

        LastRenderCacheRun = new EditorRenderCacheRun(segments.Count, cacheHits, cacheMisses);
        await ConcatenateCachedSegmentsAsync(segmentPaths, outputPath, cancellationToken)
            .ConfigureAwait(false);
        progress?.Report(1);
    }

    private async Task RenderTimelineCoreAsync(
        EditorProject project,
        string outputPath,
        string videoEncoder,
        double rangeStartSeconds,
        double rangeEndSeconds,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        var ffmpegPath = await _locator.GetFFmpegPathAsync(cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        // Render persistent rows from bottom to top. Within one row, a later
        // starting clip covers an earlier clip until its own out-point.
        var primary = project.Clips.Where(clip => project.IsTrackActive(clip.TrackId))
            .OrderBy(clip => project.GetTrack(clip.TrackId)?.Order ?? 0)
            .ThenBy(clip => clip.TimelineStartSeconds).ThenBy(clip => clip.Order).ToList();
        var overlays = new List<EditorProjectClip>();
        var graphics = project.GraphicLayers
            .Where(layer => File.Exists(layer.SourcePath))
            .OrderBy(layer => layer.StartSeconds)
            .ToList();
        var audioTracks = new List<EditorProjectAudioTrack>();
        foreach (var track in project.AudioTracks)
        {
            if (!project.IsTrackActive(track.TrackId))
                continue;
            if (await HasAudioStreamAsync(ffmpegPath, track.SourcePath, cancellationToken, track.SourceAudioStreamIndex).ConfigureAwait(false))
                audioTracks.Add(track);
        }

        var hasAudio = new bool[primary.Count];

        for (var index = 0; index < primary.Count; index++)
            hasAudio[index] = !primary[index].SourceAudioSeparated &&
                await HasAudioStreamAsync(ffmpegPath, primary[index].SourcePath, cancellationToken).ConfigureAwait(false);

        var psi = new ProcessStartInfo
        {
            FileName = ffmpegPath,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        psi.ArgumentList.Add("-nostdin");
        psi.ArgumentList.Add("-hide_banner");
        psi.ArgumentList.Add("-loglevel");
        psi.ArgumentList.Add("error");
        foreach (var inputPath in primary.Select(clip => clip.SourcePath)
                     .Concat(overlays.Select(clip => clip.SourcePath)))
        {
            psi.ArgumentList.Add("-i");
            psi.ArgumentList.Add(inputPath);
        }
        foreach (var graphic in graphics)
        {
            psi.ArgumentList.Add("-loop");
            psi.ArgumentList.Add("1");
            psi.ArgumentList.Add("-framerate");
            psi.ArgumentList.Add(project.Settings.ExportFrameRate.ToString(CultureInfo.InvariantCulture));
            psi.ArgumentList.Add("-i");
            psi.ArgumentList.Add(graphic.SourcePath);
        }
        foreach (var track in audioTracks)
        {
            psi.ArgumentList.Add("-i");
            psi.ArgumentList.Add(track.SourcePath);
        }

        var filters = new List<string>();
        for (var index = 0; index < primary.Count; index++)
        {
            var clip = primary[index];
            var outputLabel = $"v{index}";
            var sourceLabel = $"vsource{index}";
            filters.Add(
                $"[{index}:v]trim=start={F(clip.TrimStartSeconds)}:end={F(clip.TrimEndSeconds)}," +
                $"setpts=(PTS-STARTPTS)/{F(clip.PlaybackSpeed)}," +
                $"fps={project.Settings.ExportFrameRate},settb=AVTB[{sourceLabel}]");
            AppendClipVideoFilters(
                filters,
                sourceLabel,
                outputLabel,
                $"primary{index}",
                project,
                clip,
                transparentBackground: clip.Track == EditorClipTrack.Overlay);

            if (hasAudio[index])
            {
                filters.Add(
                    $"[{index}:a]atrim=start={F(clip.TrimStartSeconds)}:end={F(clip.TrimEndSeconds)}," +
                    $"asetpts=PTS-STARTPTS{BuildAtempo(clip.PlaybackSpeed)}," +
                    $"{BuildNoiseReductionFilter(clip.NoiseReductionEnabled, clip.NoiseReductionStrength)}" +
                    $"{BuildClipVolumeFilter(clip)}," +
                    "aformat=sample_fmts=fltp:sample_rates=48000:channel_layouts=stereo," +
                    $"apad=whole_dur={F(clip.EffectiveDurationSeconds)}," +
                    $"atrim=duration={F(clip.EffectiveDurationSeconds)}[a{index}]");
            }
            else
            {
                filters.Add(
                    $"anullsrc=r=48000:cl=stereo,atrim=duration={F(clip.EffectiveDurationSeconds)}[a{index}]");
            }
        }

        // The video lane is an absolute timeline, just like independent audio.
        // Composite later-start clips over a full-length black clock so moving,
        // trimming or deleting a clip preserves both gaps and earlier material.
        filters.Add($"color=c=black:s={project.CanvasWidth}x{project.CanvasHeight}:" +
                    $"r={project.Settings.ExportFrameRate}:d={F(project.DurationSeconds)},settb=AVTB[primarycanvas]");
        var baseVideo = "primarycanvas";
        var baseAudio = "basea";
        filters.Add($"anullsrc=r=48000:cl=stereo,atrim=duration={F(project.DurationSeconds)}[primarysilence]");
        var primaryAudioInputs = new List<string> { "[primarysilence]" };
        for (var index = 0; index < primary.Count; index++)
        {
            var clip = primary[index];
            var start = clip.TimelineStartSeconds;
            var end = start + clip.EffectiveDurationSeconds;
            var predecessor = project.GetTransitionPredecessor(clip);
            var overlap = predecessor is null ? 0 : project.GetTransitionOverlapSeconds(predecessor, clip);
            var incomingVideo = $"v{index}";
            var background = baseVideo;
            if (overlap > 0.00001)
            {
                background = $"primarybackground{index}";
                var outgoing = $"primaryoutgoing{index}";
                var leftVideo = $"transitionleftv{index}";
                var rightVideo = $"transitionrightv{index}";
                filters.Add($"[{baseVideo}]split=2[{background}][{outgoing}]");
                // Keep the prefix on both split branches. Trimming the outgoing
                // input forward would make overlay wait for its first frame and
                // buffer the entire background before a late transition.
                filters.Add($"[{outgoing}]trim=end={F(start + overlap)},setpts=PTS-STARTPTS," +
                            $"fps={project.Settings.ExportFrameRate},settb=AVTB," +
                            $"scale=in_range=auto:out_range=pc,format=gbrap,setrange=full,premultiply=inplace=1[{leftVideo}]");
                filters.Add($"[v{index}]fps={project.Settings.ExportFrameRate}," +
                            "settb=AVTB," +
                            $"scale=in_range=auto:out_range=pc,format=gbrap,setrange=full,premultiply=inplace=1[{rightVideo}]");
                var style = project.GetTransitionStyle(clip);
                var transition = style switch
                {
                    "wipeleft" or "wiperight" or "wipeup" or "wipedown" or
                    "slideleft" or "slideright" or "slideup" or "slidedown" => style,
                    _ => "fade"
                };
                var options = $"transition={transition}";
                if (style is "fadeblack" or "fadewhite")
                {
                    var rgb = style == "fadewhite" ? 255 : 0;
                    var color = $"if(eq(PLANE,3),255,{rgb})";
                    options = "transition=custom:expr='" +
                        $"if(gte(P,0.5),A*(2*P-1)+({color})*(2-2*P)," +
                        $"B*(1-2*P)+({color})*2*P)'";
                }
                else if (style is "circleopen" or "rectcrop" or "horzopen" or "vertopen" or "diagtl")
                {
                    // xfade P goes from 1 to 0. Use the same pixel-center
                    // geometry as the live compositor, including non-square video.
                    const string x = "(X+0.5)";
                    const string y = "(Y+0.5)";
                    const string p = "(1-P)";
                    var reveal = style switch
                    {
                        "circleopen" => $"lte(pow({x}-W/2,2)+pow({y}-H/2,2),pow({p},2)*(W*W+H*H)/4)",
                        "rectcrop" => $"lte(abs({x}/W-0.5),{p}/2)*lte(abs({y}/H-0.5),{p}/2)",
                        "horzopen" => $"lte(abs({x}/W-0.5),{p}/2)",
                        "vertopen" => $"lte(abs({y}/H-0.5),{p}/2)",
                        _ => $"lte({x}/W+{y}/H,2*{p})"
                    };
                    options = $"transition=custom:expr='if(gt({p},0)*({reveal}),B,A)'";
                }
                incomingVideo = $"transitionv{index}";
                // xfade interpolates color planes and alpha independently. Keep
                // them premultiplied while blending so transparent upper-row
                // pixels cannot darken the lower composite when overlaid again.
                filters.Add($"[{leftVideo}][{rightVideo}]xfade={options}:duration={F(overlap)}:offset={F(start)}," +
                            $"unpremultiply=inplace=1[{incomingVideo}]");
            }
            var timedVideo = $"primarytimedv{index}";
            var nextVideo = $"primarycomposite{index}";
            var timing = overlap > 0.00001 ? "setpts=PTS-STARTPTS"
                : $"trim=duration={F(clip.EffectiveDurationSeconds)},setpts=PTS-STARTPTS+{F(start)}/TB";
            var primaryFormat = clip.Track == EditorClipTrack.Overlay || clip.MaskShape == "polygon" ? "rgba" : "yuv444p";
            filters.Add($"[{incomingVideo}]format={primaryFormat},{timing}[{timedVideo}]");
            var visibility = $"gte(t,{F(start)})*lt(t,{F(end)})";
            foreach (var later in primary.Skip(index + 1).Where(later => later.TrackId == clip.TrackId &&
                         later.TimelineStartSeconds < end && later.TimelineStartSeconds + later.EffectiveDurationSeconds > start))
            {
                var laterPredecessor = project.GetTransitionPredecessor(later);
                var transitionEnd = later.TimelineStartSeconds + (laterPredecessor is null ? 0 :
                    project.GetTransitionOverlapSeconds(laterPredecessor, later));
                // A row displays its newest active clip, including transparent
                // areas. Keep the outgoing picture until its transition ends,
                // then reveal lower rows rather than ghosting older same-row
                // footage. The older clip returns when the newer clip ends.
                visibility += $"*not(gte(t,{F(transitionEnd)})*lt(t,{F(later.TimelineStartSeconds + later.EffectiveDurationSeconds)}))";
            }
            filters.Add($"[{background}][{timedVideo}]overlay=x=0:y=0:eof_action=pass:repeatlast=0:format=auto:" +
                        $"enable='{visibility}'[{nextVideo}]");
            baseVideo = nextVideo;

            var audioEffects = overlap > 0.00001 ? $",afade=t=in:st=0:d={F(overlap)}" : string.Empty;
            var successor = primary.FirstOrDefault(next => project.GetTransitionPredecessor(next)?.Id == clip.Id &&
                project.GetTransitionOverlapSeconds(clip, next) > 0.00001);
            if (successor is not null)
            {
                var outgoingOverlap = project.GetTransitionOverlapSeconds(clip, successor);
                audioEffects += $",afade=t=out:st={F(successor.TimelineStartSeconds - start)}:d={F(outgoingOverlap)}";
            }
            var delay = Math.Max(0L, (long)Math.Round(start * 48000));
            filters.Add($"[a{index}]anull{audioEffects},adelay={delay}S:all=1[primarytimeda{index}]");
            primaryAudioInputs.Add($"[primarytimeda{index}]");
        }
        filters.Add($"{string.Concat(primaryAudioInputs)}amix=inputs={primaryAudioInputs.Count}:duration=first:normalize=0[{baseAudio}]");

        var previousVideo = baseVideo;
        var overlayTimelineStart = 0.0;
        for (var index = 0; index < overlays.Count; index++)
        {
            var clip = overlays[index];
            overlayTimelineStart = clip.TimelineStartSeconds;
            var inputIndex = primary.Count + index;
            var outputLabel = $"ovlocal{index}";
            var sourceLabel = $"ovsource{index}";
            filters.Add(
                $"[{inputIndex}:v]trim=start={F(clip.TrimStartSeconds)}:end={F(clip.TrimEndSeconds)}," +
                $"setpts=(PTS-STARTPTS)/{F(clip.PlaybackSpeed)}," +
                $"fps={project.Settings.ExportFrameRate},settb=AVTB[{sourceLabel}]");
            AppendClipVideoFilters(
                filters,
                sourceLabel,
                outputLabel,
                $"overlay{index}",
                project,
                clip,
                transparentBackground: true);
            filters.Add(
                $"[ovlocal{index}]setpts=PTS+{F(overlayTimelineStart)}/TB[ov{index}]");
            var nextVideo = $"mix{index}";
            var overlayEnd = overlayTimelineStart + clip.EffectiveDurationSeconds;
            filters.Add(
                $"[{previousVideo}][ov{index}]overlay=x=0:y=0:eof_action=pass:format=auto:" +
                $"enable='between(t,{F(overlayTimelineStart)},{F(overlayEnd)})'[{nextVideo}]");
            previousVideo = nextVideo;
        }

        for (var index = 0; index < graphics.Count; index++)
        {
            var graphic = graphics[index];
            var inputIndex = primary.Count + overlays.Count + index;
            var frameTime = $"on/{project.Settings.ExportFrameRate}";
            var widthExpression = BuildGraphicValueExpression(
                graphic, keyframe => keyframe.Width, graphic.Width, frameTime);
            var heightExpression = BuildGraphicValueExpression(
                graphic, keyframe => keyframe.Height, graphic.Height, frameTime);
            var anchorXExpression = BuildGraphicValueExpression(
                graphic, keyframe => keyframe.AnchorX, graphic.AnchorX, frameTime);
            var anchorYExpression = BuildGraphicValueExpression(
                graphic, keyframe => keyframe.AnchorY, graphic.AnchorY, frameTime);
            var opacityExpression = BuildGraphicValueExpression(
                graphic,
                keyframe => keyframe.Opacity,
                graphic.Opacity,
                timeVariable: "T");
            var alphaWidthExpression = BuildGraphicValueExpression(
                graphic, keyframe => keyframe.Width, graphic.Width, "T");
            var alphaHeightExpression = BuildGraphicValueExpression(
                graphic, keyframe => keyframe.Height, graphic.Height, "T");
            var alphaAnchorXExpression = BuildGraphicValueExpression(
                graphic, keyframe => keyframe.AnchorX, graphic.AnchorX, "T");
            var alphaAnchorYExpression = BuildGraphicValueExpression(
                graphic, keyframe => keyframe.AnchorY, graphic.AnchorY, "T");
            var alphaLeftExpression =
                $"({alphaAnchorXExpression})*{project.CanvasWidth}-({alphaWidthExpression})/2";
            var alphaTopExpression =
                $"({alphaAnchorYExpression})*{project.CanvasHeight}-({alphaHeightExpression})/2";
            var leftExpression = $"({anchorXExpression})*{project.CanvasWidth}-({widthExpression})/2";
            var topExpression = $"({anchorYExpression})*{project.CanvasHeight}-({heightExpression})/2";
            var rightExpression = $"({leftExpression})+({widthExpression})";
            var bottomExpression = $"({topExpression})+({heightExpression})";
            var nextVideo = $"graphicmix{index}";
            filters.Add(
                $"[{inputIndex}:v]format=rgba,trim=duration={F(project.DurationSeconds)}," +
                $"setpts=PTS-STARTPTS," +
                $"scale={project.CanvasWidth}:{project.CanvasHeight}," +
                $"perspective=x0='{leftExpression}':y0='{topExpression}':" +
                $"x1='{rightExpression}':y1='{topExpression}':" +
                $"x2='{leftExpression}':y2='{bottomExpression}':" +
                $"x3='{rightExpression}':y3='{bottomExpression}':" +
                $"sense=destination:eval=frame:interpolation=cubic," +
                BuildGraphicAlphaFilter(
                    graphic,
                    opacityExpression,
                    alphaLeftExpression,
                    alphaTopExpression,
                    alphaWidthExpression,
                    alphaHeightExpression) +
                $"format=rgba[graphic{index}]");
            if (string.Equals(graphic.BlendMode, "normal", StringComparison.OrdinalIgnoreCase))
            {
                filters.Add(
                    $"[{previousVideo}][graphic{index}]overlay=x=0:y=0:" +
                    $"eof_action=pass:format=auto:" +
                    $"enable='between(t,{F(graphic.StartSeconds)},{F(graphic.EndSeconds)})'" +
                    $"[{nextVideo}]");
            }
            else
            {
                var blendMode = GraphicBlendModeToFfmpeg(graphic.BlendMode);
                filters.Add($"[graphic{index}]split=2[graphiccolor{index}][graphicalphasource{index}]");
                filters.Add($"[graphicalphasource{index}]alphaextract[graphicmask{index}]");
                filters.Add($"[{previousVideo}]split=2[graphicbase{index}][graphicoriginal{index}]");
                filters.Add(
                    $"[graphiccolor{index}][graphicbase{index}]blend=all_mode={blendMode}[graphicresult{index}]");
                filters.Add(
                    $"[graphicresult{index}]format=rgb24[graphicresultcolor{index}]");
                filters.Add(
                    $"[graphicresultcolor{index}][graphicmask{index}]alphamerge[graphicresultalpha{index}]");
                filters.Add(
                    $"[graphicoriginal{index}][graphicresultalpha{index}]overlay=x=0:y=0:eof_action=pass:format=auto:" +
                    $"enable='between(t,{F(graphic.StartSeconds)},{F(graphic.EndSeconds)})'[{nextVideo}]");
            }
            previousVideo = nextVideo;
        }

        var audioOutput = baseAudio;
        if (audioTracks.Count > 0)
        {
            var microphonePresent = project.AudioTracks.Any(track => track.SourceAudioRole == "microphone");
            bool IsDuckingControl(EditorProjectAudioTrack track) => !track.Muted &&
                EditorAudioDuckingMath.IsControlSource(track.SourceAudioRole, microphonePresent, !string.IsNullOrEmpty(track.SourceClipId));
            var duckedTrackIndexes = Enumerable.Range(0, audioTracks.Count)
                .Where(index => audioTracks[index].DuckingEnabled && audioTracks[index].DuckingStrength > 0.001)
                .ToList();
            var baseMixLabel = baseAudio;
            if (duckedTrackIndexes.Count > 0)
            {
                if (microphonePresent)
                {
                    foreach (var index in duckedTrackIndexes)
                        filters.Add($"anullsrc=r=48000:cl=stereo,atrim=duration={F(project.DurationSeconds)}[duckcontrol{index}]");
                }
                else
                {
                    baseMixLabel = "basemix";
                    var splitOutputs = new List<string> { $"[{baseMixLabel}]" };
                    splitOutputs.AddRange(duckedTrackIndexes.Select(index => $"[duckcontrol{index}]"));
                    filters.Add($"[{baseAudio}]asplit={splitOutputs.Count}{string.Concat(splitOutputs)}");
                }
            }

            var rawTrackLabels = new string[audioTracks.Count];
            for (var index = 0; index < audioTracks.Count; index++)
            {
                var track = audioTracks[index];
                var inputIndex = primary.Count + overlays.Count + graphics.Count + index;
                var delaySamples = Math.Max(0L, (long)Math.Round(track.TimelineStartSeconds * 48000));
                var fadeFilters = string.Empty;
                if (track.FadeInSeconds > 0.001)
                    fadeFilters += $",afade=t=in:st=0:d={F(track.FadeInSeconds)}";
                if (track.FadeOutSeconds > 0.001)
                {
                    var fadeOutStart = Math.Max(0, track.EffectiveDurationSeconds - track.FadeOutSeconds);
                    fadeFilters += $",afade=t=out:st={F(fadeOutStart)}:d={F(track.FadeOutSeconds)}";
                }
                filters.Add(
                    $"[{inputIndex}:a:{track.SourceAudioStreamIndex}]atrim=start={F(track.TrimStartSeconds)}:end={F(track.TrimEndSeconds)}," +
                    $"asetpts=PTS-STARTPTS{BuildAtempo(track.PlaybackSpeed)}," +
                    "aformat=sample_fmts=fltp:sample_rates=48000:channel_layouts=stereo," +
                    $"apad=whole_dur={F(track.EffectiveDurationSeconds)}," +
                    $"atrim=duration={F(track.EffectiveDurationSeconds)}," +
                    $"{BuildNoiseReductionFilter(track.NoiseReductionEnabled, track.NoiseReductionStrength)}" +
                    $"{BuildAudioTrackVolumeFilter(track)}{fadeFilters}," +
                    $"adelay={delaySamples}S:all=1[mixaudioinitial{index}]");

                var controlTargets = IsDuckingControl(track)
                    ? duckedTrackIndexes.Where(target => target != index).ToList() : [];
                rawTrackLabels[index] = $"mixaudioinitial{index}";
                if (controlTargets.Count > 0)
                {
                    rawTrackLabels[index] = $"mixaudioraw{index}";
                    var outputs = $"[{rawTrackLabels[index]}]" + string.Concat(controlTargets.Select(target => $"[ducksource{index}for{target}]"));
                    filters.Add($"[mixaudioinitial{index}]asplit={controlTargets.Count + 1}{outputs}");
                }
            }

            var mixInputs = new List<string> { $"[{baseMixLabel}]" };
            for (var index = 0; index < audioTracks.Count; index++)
            {
                var track = audioTracks[index];
                var mixLabel = rawTrackLabels[index];
                if (track.DuckingEnabled && track.DuckingStrength > 0.001)
                {
                    var controlInputs = new List<string> { $"[duckcontrol{index}]" };
                    controlInputs.AddRange(Enumerable.Range(0, audioTracks.Count)
                        .Where(source => source != index && IsDuckingControl(audioTracks[source]))
                        .Select(source => $"[ducksource{source}for{index}]"));
                    var control = $"duckcontrol{index}";
                    if (controlInputs.Count > 1)
                    {
                        control = $"duckmixedcontrol{index}";
                        filters.Add($"{string.Concat(controlInputs)}amix=inputs={controlInputs.Count}:duration=first:normalize=0[{control}]");
                    }
                    mixLabel = $"mixaudioducked{index}";
                    var ratio = 1 + (15 * track.DuckingStrength);
                    filters.Add(
                        $"[{rawTrackLabels[index]}][{control}]" +
                        $"sidechaincompress=threshold=0.02:ratio={F(ratio)}:knee=1:attack=20:release=350[{mixLabel}]");
                }

                mixInputs.Add($"[{mixLabel}]");
            }

            filters.Add($"{string.Concat(mixInputs)}amix=inputs={mixInputs.Count}:duration=first:normalize=0[aout]");
            audioOutput = "aout";
        }

        var renderDuration = Math.Max(0.1, rangeEndSeconds - rangeStartSeconds);
        filters.Add(
            $"[{previousVideo}]trim=start={F(rangeStartSeconds)}:end={F(rangeEndSeconds)}," +
            "setpts=PTS-STARTPTS[vout]");
        filters.Add(
            $"[{audioOutput}]atrim=start={F(rangeStartSeconds)}:end={F(rangeEndSeconds)}," +
            "asetpts=PTS-STARTPTS[aouttrim]");
        var filterGraph = string.Join(";", filters);
        using var filterScript = filterGraph.Length > 6000 ? new TemporaryFilterScript(filterGraph) : null;
        psi.ArgumentList.Add(filterScript is null ? "-filter_complex" : "-filter_complex_script");
        psi.ArgumentList.Add(filterScript?.Path ?? filterGraph);
        psi.ArgumentList.Add("-map");
        psi.ArgumentList.Add("[vout]");
        psi.ArgumentList.Add("-map");
        psi.ArgumentList.Add("[aouttrim]");
        FFmpegVideoEncoding.AddArguments(
            psi,
            videoEncoder,
            project.Settings.ExportBitrateMbps,
            project.Settings.ExportQuality,
            GetVideoCrf(project.Settings.ExportQuality));
        psi.ArgumentList.Add("-r");
        psi.ArgumentList.Add(project.Settings.ExportFrameRate.ToString(CultureInfo.InvariantCulture));
        psi.ArgumentList.Add("-c:a");
        psi.ArgumentList.Add("aac");
        psi.ArgumentList.Add("-b:a");
        psi.ArgumentList.Add($"{project.Settings.ExportAudioBitrateKbps}k");
        psi.ArgumentList.Add("-movflags");
        psi.ArgumentList.Add("+faststart");
        if (progress is not null)
            FFmpegProgressReader.AddArguments(psi);
        psi.ArgumentList.Add("-y");
        psi.ArgumentList.Add(outputPath);

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("FFmpeg could not be started.");
        var progressTask = FFmpegProgressReader.ReadAsync(
            process.StandardOutput,
            renderDuration,
            progress,
            cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            FFmpegProgressReader.Terminate(process);
            try { await progressTask.ConfigureAwait(false); } catch { }
            try { await stderrTask.ConfigureAwait(false); } catch { }
            throw;
        }

        await progressTask.ConfigureAwait(false);
        var stderr = await stderrTask.ConfigureAwait(false);
        if (process.ExitCode != 0 || !File.Exists(outputPath))
            throw new InvalidOperationException(
                $"The project could not be rendered (FFmpeg exit {process.ExitCode}). {stderr}".Trim());
    }

    private async Task ConcatenateCachedSegmentsAsync(
        IReadOnlyList<string> segmentPaths,
        string outputPath,
        CancellationToken cancellationToken)
    {
        if (segmentPaths.Count == 0)
            throw new InvalidOperationException("Timeline render cache did not produce any segments.");
        if (segmentPaths.Count == 1)
        {
            File.Copy(segmentPaths[0], outputPath, overwrite: true);
            return;
        }

        var ffmpegPath = await _locator.GetFFmpegPathAsync(cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        var listPath = Path.Combine(
            Path.GetDirectoryName(outputPath) ?? Path.GetTempPath(),
            $".{Path.GetFileNameWithoutExtension(outputPath)}-{Guid.NewGuid():N}.concat.txt");
        try
        {
            var lines = segmentPaths.Select(path =>
                $"file '{Path.GetFullPath(path).Replace('\\', '/').Replace("'", "'\\''")}'");
            await File.WriteAllTextAsync(
                    listPath,
                    string.Join(Environment.NewLine, lines),
                    cancellationToken)
                .ConfigureAwait(false);

            var startInfo = new ProcessStartInfo
            {
                FileName = ffmpegPath,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            foreach (var argument in new[]
                     {
                         "-nostdin", "-hide_banner", "-loglevel", "error",
                         "-f", "concat", "-safe", "0", "-i", listPath,
                         "-map", "0:v:0", "-map", "0:a?", "-c", "copy",
                         "-movflags", "+faststart", "-y", outputPath
                     })
            {
                startInfo.ArgumentList.Add(argument);
            }

            using var process = Process.Start(startInfo)
                ?? throw new InvalidOperationException(
                    "FFmpeg could not concatenate cached timeline segments.");
            var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
            try
            {
                await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
                await outputTask.ConfigureAwait(false);
                var error = await errorTask.ConfigureAwait(false);
                if (process.ExitCode != 0)
                {
                    throw new InvalidOperationException(
                        string.IsNullOrWhiteSpace(error)
                            ? $"FFmpeg cache concat failed with exit code {process.ExitCode}."
                            : error.Trim());
                }
            }
            catch (OperationCanceledException)
            {
                FFmpegProgressReader.Terminate(process);
                try { await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false); } catch { }
                try { await outputTask.ConfigureAwait(false); } catch { }
                try { await errorTask.ConfigureAwait(false); } catch { }
                throw;
            }

            if (!File.Exists(outputPath) || new FileInfo(outputPath).Length <= 1024)
                throw new InvalidDataException("Concatenated timeline render cache output is empty.");
        }
        finally
        {
            try { File.Delete(listPath); } catch { }
        }
    }

    private sealed class MappedProgress : IProgress<double>
    {
        private readonly IProgress<double>? _target;
        private readonly double _start;
        private readonly double _span;

        public MappedProgress(IProgress<double>? target, double start, double span)
        {
            _target = target;
            _start = start;
            _span = span;
        }

        public void Report(double value)
        {
            _target?.Report(_start + Math.Clamp(value, 0, 1) * _span);
        }
    }

    private static async Task<bool> HasAudioStreamAsync(
        string ffmpegPath,
        string inputPath,
        CancellationToken cancellationToken, int audioStreamIndex = 0)
    {
        var psi = new ProcessStartInfo
        {
            FileName = ffmpegPath,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        psi.ArgumentList.Add("-v");
        psi.ArgumentList.Add("error");
        psi.ArgumentList.Add("-i");
        psi.ArgumentList.Add(inputPath);
        psi.ArgumentList.Add("-map");
        psi.ArgumentList.Add($"0:a:{Math.Max(0, audioStreamIndex)}");
        psi.ArgumentList.Add("-frames:a");
        psi.ArgumentList.Add("1");
        psi.ArgumentList.Add("-f");
        psi.ArgumentList.Add("null");
        psi.ArgumentList.Add("-");

        using var process = Process.Start(psi);
        if (process is null)
            return false;
        _ = process.StandardOutput.ReadToEndAsync(cancellationToken);
        _ = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        return process.ExitCode == 0;
    }

    private static string GraphicBlendModeToFfmpeg(string blendMode) => blendMode switch
    {
        "multiply" => "multiply",
        "screen" => "screen",
        "overlay" => "overlay",
        "softlight" => "softlight",
        "difference" => "difference",
        _ => "normal"
    };

    private static string BuildGraphicAlphaFilter(
        EditorProjectGraphicLayer graphic,
        string opacityExpression,
        string leftExpression,
        string topExpression,
        string widthExpression,
        string heightExpression)
    {
        var rightExpression = $"({leftExpression})+({widthExpression})";
        var bottomExpression = $"({topExpression})+({heightExpression})";
        var insideRectangle =
            $"gte(X,({leftExpression}))*lte(X,({rightExpression}))*" +
            $"gte(Y,({topExpression}))*lte(Y,({bottomExpression}))";
        var maskExpression = insideRectangle;
        if (string.Equals(graphic.MaskShape, "ellipse", StringComparison.OrdinalIgnoreCase))
        {
            var centerX = $"({leftExpression})+({widthExpression})/2";
            var centerY = $"({topExpression})+({heightExpression})/2";
            maskExpression =
                $"if(lte(pow((X-({centerX}))/(({widthExpression})/2),2)+" +
                $"pow((Y-({centerY}))/(({heightExpression})/2),2),1),1,0)";
        }
        else if (string.Equals(graphic.MaskShape, "rounded", StringComparison.OrdinalIgnoreCase))
        {
            var radius =
                $"min(({widthExpression}),({heightExpression}))*" +
                F(Math.Clamp(graphic.MaskRoundness, 0, 0.5));
            var localX = $"X-({leftExpression})";
            var localY = $"Y-({topExpression})";
            maskExpression =
                $"if(({insideRectangle})*lte(" +
                $"pow(max(({radius})-min(({localX}),({widthExpression})-({localX})),0),2)+" +
                $"pow(max(({radius})-min(({localY}),({heightExpression})-({localY})),0),2)," +
                $"pow(({radius}),2)),1,0)";
        }

        return $"geq=r='r(X,Y)':g='g(X,Y)':b='b(X,Y)':" +
               $"a='alpha(X,Y)*({maskExpression})*({opacityExpression})',";
    }

    private static string BuildGraphicValueExpression(
        EditorProjectGraphicLayer graphic,
        Func<EditorProjectGraphicKeyframe, double> selector,
        double fallback,
        string timeVariable = "t")
    {
        if (graphic.Keyframes.Count == 0)
            return F(fallback);

        var keyframes = graphic.Keyframes.OrderBy(keyframe => keyframe.TimeSeconds).ToList();
        var expression = F(selector(keyframes[^1]));
        for (var index = keyframes.Count - 1; index >= 1; index--)
        {
            var left = keyframes[index - 1];
            var right = keyframes[index];
            var duration = Math.Max(0.0001, right.TimeSeconds - left.TimeSeconds);
            var progress = $"max(0,min(1,({timeVariable}-{F(left.TimeSeconds)})/{F(duration)}))";
            var easedProgress = BuildGraphicEasingExpression(progress, right.Easing);
            var leftValue = F(selector(left));
            var rightValue = F(selector(right));
            var interpolated = $"({leftValue})+(({rightValue})-({leftValue}))*({easedProgress})";
            expression = $"if(lt({timeVariable},{F(right.TimeSeconds)}),{interpolated},{expression})";
        }

        return $"if(lt({timeVariable},{F(keyframes[0].TimeSeconds)}),{F(selector(keyframes[0]))},{expression})";
    }

    private static string BuildGraphicEasingExpression(string progress, string easing) => easing switch
    {
        "ease-in" => $"pow(({progress}),2)",
        "ease-out" => $"1-pow(1-({progress}),2)",
        "ease-in-out" =>
            $"if(lt(({progress}),0.5),2*pow(({progress}),2),1-pow(-2*({progress})+2,2)/2)",
        "hold" => "0",
        _ => progress
    };

    private static void AppendClipVideoFilters(
        ICollection<string> filters,
        string inputLabel,
        string outputLabel,
        string prefix,
        EditorProject project,
        EditorProjectClip clip,
        bool transparentBackground)
    {
        var hasEffectLayers = project.EffectLayers.Any(layer => layer.TargetClipId == clip.Id);
        if (hasEffectLayers)
        {
            // FX regions belong to decoded source pixels, before crop, fit or
            // motion. Grade the source first, matching the native preview, and
            // keep the original alpha around all RGB-only look filters.
            var sourceGrade = $"{prefix}sourcegrade";
            var sourceEffects = $"{prefix}sourceeffects";
            AppendClipFilterMix(filters, inputLabel, sourceGrade, $"{prefix}sourcefilter", clip, "rgba");
            AppendTimedClipEffectLayers(filters, sourceGrade, sourceEffects, $"{prefix}fx", project, clip);
            inputLabel = sourceEffects;
        }
        // Polygon points belong to the original source frame. Apply their matte
        // before cropping and fitting, so letterboxing never distorts the shape.
        if (clip.MaskShape == "polygon")
        {
            var polygonLabel = $"{prefix}polygon";
            var polygon = BuildPolygonMaskExpression(clip.MaskPolygon, "X", "Y", "W", "H");
            if (clip.MaskInverted) polygon = $"1-({polygon})";
            filters.Add($"[{inputLabel}]format=rgba,geq=r='r(X,Y)':g='g(X,Y)':b='b(X,Y)':" +
                $"a='alpha(X,Y)*({polygon})'[{polygonLabel}]");
            inputLabel = polygonLabel;
            transparentBackground = true;
        }
        var pixelFormat = transparentBackground ? "rgba" : "yuv420p";
        var canvasLabel = $"{prefix}canvas";
        var filteredLabel = clip.MaskShape is "none" or "polygon"
            ? outputLabel
            : $"{prefix}unmasked";
        if (clip.TransformKeyframes.Count == 0)
        {
            filters.Add(
                $"[{inputLabel}]" +
                BuildClipCanvasFilter(project, clip, transparentBackground) +
                $"[{canvasLabel}]");
            if (hasEffectLayers)
                filters.Add($"[{canvasLabel}]format={pixelFormat}[{filteredLabel}]");
            else
                AppendClipFilterMix(filters, canvasLabel, filteredLabel, $"{prefix}filter", clip, pixelFormat);
            AppendClipMaskFilter(filters, filteredLabel, outputLabel, project, clip, transparentBackground);
            return;
        }

        var frameRate = Math.Max(1, project.Settings.ExportFrameRate);
        var scaleExpression = BuildClipTransformValueExpression(
            clip,
            keyframe => keyframe.Scale,
            clip.Scale,
            $"n/{frameRate}");
        var offsetXExpression = BuildClipTransformValueExpression(
            clip,
            keyframe => keyframe.OffsetX,
            clip.OffsetX,
            "t");
        var offsetYExpression = BuildClipTransformValueExpression(
            clip,
            keyframe => keyframe.OffsetY,
            clip.OffsetY,
            "t");
        var background = transparentBackground ? "black@0.0" : "black";
        var fitLabel = $"{prefix}fit";
        var scaledLabel = $"{prefix}scaled";
        var backgroundLabel = $"{prefix}background";
        filters.Add(
            $"[{inputLabel}]" +
            BuildClipFitFilter(project, clip, transparentBackground) +
            $",fps={frameRate}[{fitLabel}]");
        filters.Add(
            $"[{fitLabel}]scale=" +
            $"w='max(2,trunc({project.CanvasWidth}*({scaleExpression})/2)*2)':" +
            $"h='max(2,trunc({project.CanvasHeight}*({scaleExpression})/2)*2)':" +
            $"eval=frame[{scaledLabel}]");
        filters.Add(
            $"color=c={background}:s={project.CanvasWidth}x{project.CanvasHeight}:" +
            $"r={frameRate}:d={F(clip.EffectiveDurationSeconds + 0.05)}," +
            $"format={pixelFormat},setpts=PTS-STARTPTS[{backgroundLabel}]");
        filters.Add(
            $"[{backgroundLabel}][{scaledLabel}]overlay=" +
            $"x='(main_w-overlay_w)*(({offsetXExpression})+1)/2':" +
            $"y='(main_h-overlay_h)*(({offsetYExpression})+1)/2':" +
            $"eval=frame:shortest=1:eof_action=pass:format=auto[{canvasLabel}]");
        if (hasEffectLayers)
            filters.Add($"[{canvasLabel}]format={pixelFormat}[{filteredLabel}]");
        else
            AppendClipFilterMix(filters, canvasLabel, filteredLabel, $"{prefix}filter", clip, pixelFormat);
        AppendClipMaskFilter(filters, filteredLabel, outputLabel, project, clip, transparentBackground);
    }

    private static void AppendTimedClipEffectLayers(ICollection<string> filters, string inputLabel,
        string outputLabel, string prefix, EditorProject project, EditorProjectClip clip)
    {
        var layers = project.EffectLayers.Where(layer => layer.TargetClipId == clip.Id &&
            layer.Enabled && layer.Amount > 0 && (layer.Style is not null || layer.Filter != "none")).ToList();
        if (layers.Count == 0)
        {
            filters.Add($"[{inputLabel}]null[{outputLabel}]");
            return;
        }

        filters.Add($"[{inputLabel}]format=rgba,split=2[{prefix}color][{prefix}alphasource]");
        filters.Add($"[{prefix}alphasource]alphaextract[{prefix}alpha]");
        var current = $"{prefix}rgb";
        filters.Add($"[{prefix}color]format=gbrp[{current}]");
        for (var index = 0; index < layers.Count; index++)
        {
            var layer = layers[index];
            var item = $"{prefix}{index}";
            var start = Math.Max(0, layer.StartSeconds - clip.TimelineStartSeconds);
            var end = Math.Min(clip.EffectiveDurationSeconds, layer.EndSeconds - clip.TimelineStartSeconds);
            var lookFilters = layer.Style is null ? ClipEditService.GetVfxFilters(layer.Filter).ToArray() : [];
            if (layer.Style is null && lookFilters.Length == 0 || end <= start)
                continue;
            filters.Add($"[{current}]split=2[{item}original][{item}source]");
            if (layer.Style is { } style)
            {
                AppendClipFilterMix(filters, $"{item}source", $"{item}style", $"{item}stylefilter", style, "rgb24");
                filters.Add($"[{item}style]format=gbrp[{item}graded]");
            }
            else
                filters.Add($"[{item}source]{string.Join(',', lookFilters)},format=gbrp[{item}graded]");
            var region = BuildEffectRegionCoverageExpression(layer, clip);
            var weight = $"{F(layer.Amount)}*gte(T,{F(start)})*lt(T,{F(end)})*ld(4)";
            var next = $"{item}mixed";
            filters.Add($"[{item}original][{item}graded]blend=all_expr='{region}A+(B-A)*({weight})':" +
                $"shortest=1[{next}]");
            current = next;
        }
        filters.Add($"[{current}]format=rgb24[{prefix}resultcolor]");
        filters.Add($"[{prefix}resultcolor][{prefix}alpha]alphamerge,format=rgba[{outputLabel}]");
    }

    private static string BuildEffectRegionCoverageExpression(EditorProjectEffectLayer layer, EditorProjectClip clip)
    {
        var localTime = $"(T-{F(layer.StartSeconds - clip.TimelineStartSeconds)})";
        // libav's expression registers avoid repeating an animated interpolation
        // tree for every edge calculation. They are local to each blend plane.
        var setup = $"st(0,{BuildEffectRegionValueExpression(layer, key => key.RegionX, layer.RegionX, localTime)});" +
            $"st(1,{BuildEffectRegionValueExpression(layer, key => key.RegionY, layer.RegionY, localTime)});" +
            $"st(2,{BuildEffectRegionValueExpression(layer, key => key.RegionWidth, layer.RegionWidth, localTime)});" +
            $"st(3,{BuildEffectRegionValueExpression(layer, key => key.RegionHeight, layer.RegionHeight, localTime)});";
        string coverage;
        if (layer.RegionShape == "ellipse")
        {
            var radial = "sqrt(pow(((X+0.5)/W-ld(0)-ld(2)/2)/(ld(2)/2),2)+" +
                "pow(((Y+0.5)/H-ld(1)-ld(3)/2)/(ld(3)/2),2))";
            coverage = layer.RegionFeather > 0
                ? $"max(0,min(1,(1-({radial}))/{F(layer.RegionFeather)}))"
                : $"lt({radial},1)";
        }
        else if (layer.RegionFeather > 0)
        {
            var inward = "min(min((X+0.5)-ld(0)*W,(ld(0)+ld(2))*W-(X+0.5))," +
                "min((Y+0.5)-ld(1)*H,(ld(1)+ld(3))*H-(Y+0.5)))";
            coverage = $"max(0,min(1,({inward})/(min(ld(2)*W,ld(3)*H)*0.5*{F(layer.RegionFeather)})))";
        }
        else
            coverage = "gte((X+0.5)/W,ld(0))*lt((X+0.5)/W,ld(0)+ld(2))*" +
                "gte((Y+0.5)/H,ld(1))*lt((Y+0.5)/H,ld(1)+ld(3))";
        return setup + $"st(4,{coverage});";
    }

    private static string BuildEffectRegionValueExpression(EditorProjectEffectLayer layer,
        Func<EditorProjectEffectRegionKeyframe, double> selector, double fallback, string localTime)
    {
        var keys = layer.RegionKeyframes;
        if (keys.Count == 0) return F(fallback);
        if (keys.Count == 1) return F(selector(keys[0]));
        string Interval(int index)
        {
            var left = keys[index - 1];
            var right = keys[index];
            var progress = $"max(0,min(1,({localTime}-{F(left.TimeSeconds)})/{F(Math.Max(.000001, right.TimeSeconds - left.TimeSeconds))}))";
            var eased = BuildGraphicEasingExpression(progress, right.Easing);
            return $"{F(selector(left))}+({F(selector(right) - selector(left))})*({eased})";
        }
        string DecisionTree(int first, int last)
        {
            if (first == last) return Interval(first);
            var middle = (first + last) / 2;
            return $"if(lt({localTime},{F(keys[middle].TimeSeconds)}),{DecisionTree(first, middle)}," +
                $"{DecisionTree(middle + 1, last)})";
        }
        // A trimmed eased path contains sampled keys. Bound parser depth by
        // choosing intervals with a balanced tree instead of a linear if chain.
        return $"if(lt({localTime},{F(keys[0].TimeSeconds)}),{F(selector(keys[0]))}," +
            $"if(gte({localTime},{F(keys[^1].TimeSeconds)}),{F(selector(keys[^1]))},{DecisionTree(1, keys.Count - 1)}))";
    }

    private static void AppendClipFilterMix(
        ICollection<string> filters,
        string inputLabel,
        string outputLabel,
        string prefix,
        EditorProjectClip clip,
        string pixelFormat)
    {
        var enabled = clip.Effects.Where(effect => effect.Enabled &&
            (effect.Kind == "lut" ? HasClipLut(clip) : !string.IsNullOrEmpty(BuildClipEffectFilter(clip, effect.Kind)))).ToList();
        if (enabled.Count == 0 ||
            (clip.FilterStrengthKeyframes.Count == 0 && clip.FilterStrength <= 0.001))
        {
            filters.Add($"[{inputLabel}]format={pixelFormat}[{outputLabel}]");
            return;
        }

        // Many color filters negotiate a format without alpha. Keep the matte
        // outside the grade so a filtered picture-in-picture retains its clear
        // canvas instead of covering the lower video with a black rectangle.
        if (string.Equals(pixelFormat, "rgba", StringComparison.Ordinal))
        {
            filters.Add($"[{inputLabel}]format=rgba,split=2[{prefix}color][{prefix}alphasource]");
            filters.Add($"[{prefix}alphasource]alphaextract[{prefix}alpha]");
            AppendClipFilterMix(filters, $"{prefix}color", $"{prefix}result", $"{prefix}rgb", clip, "rgb24");
            filters.Add($"[{prefix}result]format=rgb24[{prefix}resultcolor]");
            filters.Add($"[{prefix}resultcolor][{prefix}alpha]alphamerge,format=rgba[{outputLabel}]");
            return;
        }

        if (clip.FilterStrengthKeyframes.Count == 0 && clip.FilterStrength >= 0.999)
        {
            AppendClipEffectPipeline(filters, inputLabel, $"{prefix}graded", prefix, clip, enabled);
            filters.Add($"[{prefix}graded]format={pixelFormat}[{outputLabel}]");
            return;
        }

        var originalLabel = $"{prefix}original";
        var originalRgbLabel = $"{prefix}originalrgb";
        var sourceLabel = $"{prefix}source";
        var filteredLabel = $"{prefix}graded";
        filters.Add(
            $"[{inputLabel}]format=rgb24,split=2[{originalLabel}][{sourceLabel}]");
        filters.Add($"[{originalLabel}]format=gbrp[{originalRgbLabel}]");
        AppendClipEffectPipeline(filters, sourceLabel, filteredLabel, prefix, clip, enabled);
        var strengthExpression = BuildClipFilterStrengthValueExpression(clip, "T");
        filters.Add(
            $"[{originalRgbLabel}][{filteredLabel}]" +
            $"blend=all_expr='A*(1-({strengthExpression}))+B*({strengthExpression})':" +
            $"shortest=1,format={pixelFormat}[{outputLabel}]");
    }

    private static void AppendClipEffectPipeline(ICollection<string> filters, string inputLabel,
        string outputLabel, string prefix, EditorProjectClip clip, IReadOnlyList<EditorProjectClipEffect> effects)
    {
        var current = $"{prefix}stackinput";
        filters.Add($"[{inputLabel}]format=rgb24[{current}]");
        for (var index = 0; index < effects.Count; index++)
        {
            var next = $"{prefix}stack{index}";
            if (effects[index].Kind == "lut")
                AppendClipLutFilters(filters, current, next, $"{prefix}lut{index}", clip, false);
            else
                filters.Add($"[{current}]{BuildClipEffectFilter(clip, effects[index].Kind)}[{next}]");
            current = next;
        }
        filters.Add($"[{current}]format=gbrp[{outputLabel}]");
    }

    private static void AppendClipMaskFilter(
        ICollection<string> filters,
        string inputLabel,
        string outputLabel,
        EditorProject project,
        EditorProjectClip clip,
        bool transparentBackground)
    {
        if (clip.MaskShape is "none" or "polygon")
            return;

        var mask = BuildClipMaskExpression(project, clip);
        if (clip.MaskInverted)
            mask = $"1-({mask})";
        if (transparentBackground)
        {
            filters.Add(
                $"[{inputLabel}]format=rgba," +
                $"geq=r='r(X,Y)':g='g(X,Y)':b='b(X,Y)':a='alpha(X,Y)*({mask})'," +
                $"format=rgba[{outputLabel}]");
            return;
        }

        filters.Add(
            $"[{inputLabel}]format=rgba," +
            $"geq=r='r(X,Y)*({mask})':g='g(X,Y)*({mask})':b='b(X,Y)*({mask})':a='255'," +
            $"format=yuv420p[{outputLabel}]");
    }

    private static string BuildClipMaskExpression(EditorProject project, EditorProjectClip clip)
    {
        var scale = BuildClipTransformValueExpression(clip, keyframe => keyframe.Scale, clip.Scale, "T");
        var offsetX = BuildClipTransformValueExpression(clip, keyframe => keyframe.OffsetX, clip.OffsetX, "T");
        var offsetY = BuildClipTransformValueExpression(clip, keyframe => keyframe.OffsetY, clip.OffsetY, "T");
        var width = $"{project.CanvasWidth}*({scale})";
        var height = $"{project.CanvasHeight}*({scale})";
        var left = $"({project.CanvasWidth}-({width}))*(({offsetX})+1)/2";
        var top = $"({project.CanvasHeight}-({height}))*(({offsetY})+1)/2";
        var localX = $"X-({left})";
        var localY = $"Y-({top})";
        var inside =
            $"gte(X,({left}))*lte(X,({left})+({width}))*" +
            $"gte(Y,({top}))*lte(Y,({top})+({height}))";

        if (string.Equals(clip.MaskShape, "ellipse", StringComparison.Ordinal))
        {
            var centerX = $"({left})+({width})/2";
            var centerY = $"({top})+({height})/2";
            return
                $"if(lte(pow((X-({centerX}))/(({width})/2),2)+" +
                $"pow((Y-({centerY}))/(({height})/2),2),1),1,0)";
        }

        var radius = $"min(({width}),({height}))*{F(Math.Clamp(clip.MaskRoundness, 0, 0.5))}";
        return
            $"if(({inside})*lte(" +
            $"pow(max(({radius})-min(({localX}),({width})-({localX})),0),2)+" +
            $"pow(max(({radius})-min(({localY}),({height})-({localY})),0),2)," +
            $"pow(({radius}),2)),1,0)";
    }

    private static string BuildPolygonMaskExpression(IReadOnlyList<EditorProjectCurvePoint> points,
        string x, string y, string width, string height)
    {
        if (points.Count < 3) return "1";
        var crossings = new List<string>();
        for (var index = 0; index < points.Count; index++)
        {
            var a = points[index];
            var b = points[(index + 1) % points.Count];
            if (Math.Abs(a.Y - b.Y) < 1e-9) continue;
            var edge = $"({F(a.X)}+({F(b.X - a.X)})*(({y})/({height})-{F(a.Y)})/({F(b.Y - a.Y)}))*({width})";
            crossings.Add($"not(eq(gt(({y}),{F(a.Y)}*({height})),gt(({y}),{F(b.Y)}*({height}))))*lt(({x}),({edge}))");
        }
        return crossings.Count == 0 ? "1" : $"mod({string.Join('+', crossings)},2)";
    }

    private static bool HasClipLut(EditorProjectClip clip) =>
        clip.LutStrength > 0.001 &&
        !string.IsNullOrWhiteSpace(clip.LutPath) &&
        clip.Effects.Any(effect => effect.Kind == "lut" && effect.Enabled);

    private static IEnumerable<EditorProjectEffectLayer> ActiveEffectLayers(EditorProject project) =>
        project.EffectLayers.Where(layer => layer.Enabled && layer.Amount > 0 &&
            project.Clips.Any(clip => clip.Id == layer.TargetClipId && project.IsTrackActive(clip.TrackId)));

    private static void AppendClipLutFilters(
        ICollection<string> filters,
        string inputLabel,
        string outputLabel,
        string prefix,
        EditorProjectClip clip,
        bool preserveAlpha)
    {
        if (preserveAlpha)
        {
            filters.Add($"[{inputLabel}]format=rgba,split=2[{prefix}color][{prefix}alphasource]");
            filters.Add($"[{prefix}alphasource]alphaextract[{prefix}alpha]");
            AppendClipLutFilters(filters, $"{prefix}color", $"{prefix}result", $"{prefix}rgb", clip, false);
            filters.Add($"[{prefix}result]format=rgb24[{prefix}resultcolor]");
            filters.Add($"[{prefix}resultcolor][{prefix}alpha]alphamerge,format=rgba[{outputLabel}]");
            return;
        }

        var strength = Math.Clamp(clip.LutStrength, 0, 1);
        var escapedPath = ClipEditService.EscapeFilterPath(clip.LutPath);
        filters.Add($"[{inputLabel}]format=gbrp,split=2[{prefix}original][{prefix}source]");
        filters.Add($"[{prefix}source]lut3d=file={escapedPath}:interp=tetrahedral,format=gbrp[{prefix}graded]");
        filters.Add(
            $"[{prefix}original][{prefix}graded]" +
            $"blend=all_expr='A*(1-{F(strength)})+B*{F(strength)}':shortest=1[{outputLabel}]");
    }

    private static string BuildClipEffectFilter(EditorProjectClip clip, string kind)
    {
        var filters = kind == "look" ? ClipEditService.GetVfxFilters(clip.Filter).ToList() : [];

        if (kind == "shadows" && (Math.Abs(clip.Shadows) > 0.001 || Math.Abs(clip.Highlights) > 0.001))
        {
            var shadowPoint = Math.Clamp(0.25 + clip.Shadows * 0.20, 0.05, 0.45);
            var highlightPoint = Math.Clamp(0.75 + clip.Highlights * 0.20, 0.55, 0.95);
            filters.Add(
                $"curves=master='0/0 0.25/{F(shadowPoint)} 0.75/{F(highlightPoint)} 1/1'");
        }

        if (kind == "curve" && clip.ToneCurve is { Count: >= 2 } toneCurve &&
            toneCurve.Any(point => Math.Abs(point.Y - point.X) > 0.0005))
        {
            var points = string.Join(
                ' ',
                toneCurve.OrderBy(point => point.X).Select(point =>
                    $"{point.X.ToString("G17", CultureInfo.InvariantCulture)}/{F(point.Y)}"));
            filters.Add($"curves=master='{points}'");
        }

        if (kind == "color" && (Math.Abs(clip.Brightness) > 0.001 ||
            Math.Abs(clip.Contrast - 1) > 0.001 ||
            Math.Abs(clip.Saturation - 1) > 0.001 ||
            Math.Abs(clip.Gamma - 1) > 0.001))
        {
            filters.Add(
                $"eq=brightness={F(clip.Brightness)}:" +
                $"contrast={F(clip.Contrast)}:saturation={F(clip.Saturation)}:" +
                $"gamma={F(clip.Gamma)}");
        }

        if (kind == "temperature" && Math.Abs(clip.Temperature) > 0.001)
        {
            var shift = Math.Clamp(clip.Temperature, -1, 1) * 0.12;
            filters.Add($"colorbalance=rs={F(shift)}:bs={F(-shift)}");
        }

        if (kind == "hue" && Math.Abs(clip.HueDegrees) > 0.001)
            filters.Add($"hue=h={F(clip.HueDegrees)}");

        if (kind == "sharpen" && clip.Sharpen > 0.001)
            filters.Add($"unsharp=5:5:{F(clip.Sharpen)}:5:5:0");
        if (kind == "blur" && clip.Blur > 0.001)
            filters.Add($"gblur=sigma={F(clip.Blur)}");
        if (kind == "vignette" && clip.Vignette > 0.001)
            filters.Add($"vignette=angle={F(clip.Vignette * Math.PI / 3)}:mode=forward");

        return string.Join(',', filters);
    }

    private static string BuildClipCanvasFilter(
        EditorProject project,
        EditorProjectClip clip,
        bool transparentBackground)
    {
        var width = project.CanvasWidth;
        var height = project.CanvasHeight;
        var background = transparentBackground ? "black@0" : "black";
        var fit = BuildClipFitFilter(project, clip, transparentBackground);

        var scale = Math.Clamp(clip.Scale, 0.1, 4);
        var offsetX = Math.Clamp(clip.OffsetX, -1, 1);
        var offsetY = Math.Clamp(clip.OffsetY, -1, 1);
        if (Math.Abs(scale - 1) < 0.001 &&
            Math.Abs(offsetX) < 0.001 &&
            Math.Abs(offsetY) < 0.001)
        {
            return fit;
        }

        var scaledWidth = width * scale;
        var scaledHeight = height * scale;
        if (scale >= 1)
        {
            var cropX = (scaledWidth - width) * (offsetX + 1) / 2;
            var cropY = (scaledHeight - height) * (offsetY + 1) / 2;
            return fit + $",scale={F(scaledWidth)}:{F(scaledHeight)}:flags=lanczos," +
                   $"crop={width}:{height}:{F(cropX)}:{F(cropY)}";
        }

        var padX = (width - scaledWidth) * (offsetX + 1) / 2;
        var padY = (height - scaledHeight) * (offsetY + 1) / 2;
        return fit + $",scale={F(scaledWidth)}:{F(scaledHeight)}:flags=lanczos," +
               $"pad={width}:{height}:{F(padX)}:{F(padY)}:{background}";
    }

    private static string BuildClipFitFilter(
        EditorProject project,
        EditorProjectClip clip,
        bool transparentBackground)
    {
        var width = project.CanvasWidth;
        var height = project.CanvasHeight;
        var background = transparentBackground ? "black@0" : "black";
        var outputFormat = transparentBackground ? ",format=rgba" : string.Empty;
        var cropLeft = Math.Clamp(clip.CropLeft, 0, 0.9);
        var cropTop = Math.Clamp(clip.CropTop, 0, 0.9);
        var cropRight = Math.Clamp(clip.CropRight, 0, 0.9 - cropLeft);
        var cropBottom = Math.Clamp(clip.CropBottom, 0, 0.9 - cropTop);
        var crop = cropLeft + cropTop + cropRight + cropBottom > 0.001
            ? $"crop=iw*{F(1 - cropLeft - cropRight)}:ih*{F(1 - cropTop - cropBottom)}:" +
              $"iw*{F(cropLeft)}:ih*{F(cropTop)},"
            : string.Empty;
        return crop + $"scale={width}:{height}:force_original_aspect_ratio=decrease:flags=lanczos{outputFormat}," +
               $"pad={width}:{height}:(ow-iw)/2:(oh-ih)/2:{background},setsar=1{outputFormat}";
    }

    private static string BuildClipTransformValueExpression(
        EditorProjectClip clip,
        Func<EditorProjectClipTransformKeyframe, double> selector,
        double fallback,
        string timeVariable)
    {
        if (clip.TransformKeyframes.Count == 0)
            return F(fallback);

        var keyframes = clip.TransformKeyframes.OrderBy(keyframe => keyframe.TimeSeconds).ToList();
        var expression = F(selector(keyframes[^1]));
        for (var index = keyframes.Count - 1; index >= 1; index--)
        {
            var left = keyframes[index - 1];
            var right = keyframes[index];
            var duration = Math.Max(0.0001, right.TimeSeconds - left.TimeSeconds);
            var progress = $"max(0,min(1,({timeVariable}-{F(left.TimeSeconds)})/{F(duration)}))";
            var easedProgress = BuildGraphicEasingExpression(progress, right.Easing);
            var leftValue = F(selector(left));
            var rightValue = F(selector(right));
            var interpolated = $"({leftValue})+(({rightValue})-({leftValue}))*({easedProgress})";
            expression = $"if(lt({timeVariable},{F(right.TimeSeconds)}),{interpolated},{expression})";
        }

        return $"if(lt({timeVariable},{F(keyframes[0].TimeSeconds)})," +
               $"{F(selector(keyframes[0]))},{expression})";
    }

    private static string BuildClipVolumeFilter(EditorProjectClip clip)
        => BuildVolumeFilter(clip.Muted, clip.Volume, clip.NormalizeAudio, clip.NormalizationGainDb, clip.VolumeKeyframes);

    private static string BuildAudioTrackVolumeFilter(EditorProjectAudioTrack track)
        => BuildVolumeFilter(track.Muted, track.Volume, track.NormalizeAudio, track.NormalizationGainDb, track.VolumeKeyframes);

    private static string BuildVolumeFilter(
        bool muted,
        double volume,
        bool normalize,
        double gainDb,
        IReadOnlyCollection<EditorProjectClipVolumeKeyframe> keyframes)
    {
        if (muted)
            return "volume=0";
        var normalizationGain = GetNormalizationMultiplier(normalize, gainDb);
        if (keyframes.Count == 0)
            return $"volume={F(volume * normalizationGain)}";

        var expression = BuildVolumeValueExpression(keyframes, volume, "t");
        if (Math.Abs(normalizationGain - 1) > 0.0001)
            expression = $"({expression})*{F(normalizationGain)}";
        return $"volume='{expression}':eval=frame";
    }

    private static string BuildNoiseReductionFilter(bool enabled, double strength)
    {
        if (!enabled)
            return string.Empty;

        var reductionDb = 6 + Math.Clamp(strength, 0, 1) * 24;
        return $"afftdn=nr={F(reductionDb)}:nf=-45:tn=1,";
    }

    private static double GetNormalizationMultiplier(bool enabled, double gainDb) =>
        enabled ? Math.Pow(10, Math.Clamp(gainDb, -24, 12) / 20) : 1;

    private static string BuildVolumeValueExpression(
        IEnumerable<EditorProjectClipVolumeKeyframe> sourceKeyframes,
        double fallback,
        string timeVariable)
    {
        var keyframes = sourceKeyframes.OrderBy(keyframe => keyframe.TimeSeconds).ToList();
        if (keyframes.Count == 0)
            return F(fallback);
        if (keyframes.Count == 1)
            return F(keyframes[0].Volume);

        string BuildInterval(int rightIndex)
        {
            var left = keyframes[rightIndex - 1];
            var right = keyframes[rightIndex];
            var duration = Math.Max(0.0001, right.TimeSeconds - left.TimeSeconds);
            var progress = $"max(0,min(1,({timeVariable}-{F(left.TimeSeconds)})/{F(duration)}))";
            var easedProgress = BuildGraphicEasingExpression(progress, right.Easing);
            var leftValue = F(left.Volume);
            var rightValue = F(right.Volume);
            return $"({leftValue})+(({rightValue})-({leftValue}))*({easedProgress})";
        }

        string BuildDecisionTree(int firstInterval, int lastInterval)
        {
            if (firstInterval == lastInterval)
                return BuildInterval(firstInterval);
            var middle = (firstInterval + lastInterval) / 2;
            return $"if(lt({timeVariable},{F(keyframes[middle].TimeSeconds)})," +
                   $"{BuildDecisionTree(firstInterval, middle)}," +
                   $"{BuildDecisionTree(middle + 1, lastInterval)})";
        }

        // Splitting an eased curve can produce hundreds of sampled keys. A
        // linear chain of nested ifs exhausts FFmpeg's expression parser depth;
        // this tree keeps the same intervals at logarithmic nesting depth.
        var expression = BuildDecisionTree(1, keyframes.Count - 1);
        return $"if(lt({timeVariable},{F(keyframes[0].TimeSeconds)})," +
               $"{F(keyframes[0].Volume)}," +
               $"if(gte({timeVariable},{F(keyframes[^1].TimeSeconds)}),{F(keyframes[^1].Volume)},{expression}))";
    }

    private static string BuildClipFilterStrengthValueExpression(
        EditorProjectClip clip,
        string timeVariable)
    {
        var keyframes = clip.FilterStrengthKeyframes
            .OrderBy(keyframe => keyframe.TimeSeconds)
            .ToList();
        if (keyframes.Count == 0)
            return F(clip.FilterStrength);

        var expression = F(keyframes[^1].Strength);
        for (var index = keyframes.Count - 1; index >= 1; index--)
        {
            var left = keyframes[index - 1];
            var right = keyframes[index];
            var duration = Math.Max(0.0001, right.TimeSeconds - left.TimeSeconds);
            var progress = $"max(0,min(1,({timeVariable}-{F(left.TimeSeconds)})/{F(duration)}))";
            var easedProgress = BuildGraphicEasingExpression(progress, right.Easing);
            var leftValue = F(left.Strength);
            var rightValue = F(right.Strength);
            var interpolated = $"({leftValue})+(({rightValue})-({leftValue}))*({easedProgress})";
            expression = $"if(lt({timeVariable},{F(right.TimeSeconds)}),{interpolated},{expression})";
        }

        return $"if(lt({timeVariable},{F(keyframes[0].TimeSeconds)})," +
               $"{F(keyframes[0].Strength)},{expression})";
    }

    private static string BuildAtempo(double speed)
    {
        speed = Math.Clamp(speed, 0.25, 4);
        if (speed < 0.5)
            return $",atempo=0.5,atempo={F(speed / 0.5)}";
        if (speed > 2)
            return $",atempo=2,atempo={F(speed / 2)}";
        return Math.Abs(speed - 1) < 0.001 ? string.Empty : $",atempo={F(speed)}";
    }

    private static string F(double value) =>
        value.ToString("0.######", CultureInfo.InvariantCulture);

    private static ClipTextKeyframe ToClipTextKeyframe(EditorProjectTextKeyframe keyframe) => new(
        keyframe.TimeSeconds,
        keyframe.AnchorX,
        keyframe.AnchorY,
        keyframe.Size,
        keyframe.Opacity,
        keyframe.Easing);

    private static int GetVideoCrf(string quality)
    {
        return quality switch
        {
            "draft" => 28,
            "max" => 16,
            _ => 20
        };
    }

    private sealed class TemporaryFilterScript : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), $"klipp-timeline-filter-{Guid.NewGuid():N}.txt");

        public TemporaryFilterScript(string filterGraph)
        {
            try
            {
                File.WriteAllText(Path, filterGraph, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        public void Dispose()
        {
            try { File.Delete(Path); } catch { }
        }
    }
}
