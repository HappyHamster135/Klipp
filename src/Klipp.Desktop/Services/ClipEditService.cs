using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Klipp.Core.Models;
using Klipp.Desktop.Models;
using Klipp.Encoding.FFmpeg;

namespace Klipp.Desktop.Services;

public sealed class ClipEditService
{
    private readonly FFmpegLocator _locator;

    public ClipEditService(FFmpegLocator? locator = null)
    {
        _locator = locator ?? new FFmpegLocator();
    }

    public async Task TrimAsync(
        string inputPath,
        string outputPath,
        TimeSpan start,
        TimeSpan end,
        CancellationToken cancellationToken = default)
    {
        await TrimAsync(
            inputPath,
            outputPath,
            start,
            end,
            ClipEditOptions.Default,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task TrimAsync(
        string inputPath,
        string outputPath,
        TimeSpan start,
        TimeSpan end,
        ClipEditOptions? options,
        CancellationToken cancellationToken = default)
    {
        await TrimAsync(inputPath, outputPath, start, end, options, progress: null, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task TrimAsync(
        string inputPath,
        string outputPath,
        TimeSpan start,
        TimeSpan end,
        ClipEditOptions? options,
        IProgress<double>? progress,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(inputPath))
            throw new FileNotFoundException("The clip no longer exists.", inputPath);
        if (end <= start)
            throw new ArgumentException("The end time must be after the start time.");
        if (start < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(start));

        options ??= ClipEditOptions.Default;

        var outputDirectory = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrWhiteSpace(outputDirectory))
            Directory.CreateDirectory(outputDirectory);

        var ffmpegPath = await _locator.GetFFmpegPathAsync(cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        var duration = end - start;

        var includeAudio = !options.MuteAudio;
        if (includeAudio && options.RequiresAudioFilter)
            includeAudio = await HasAudioStreamAsync(inputPath, cancellationToken).ConfigureAwait(false);

        // A copied stream can start only at a keyframe. Decode non-zero trims so
        // the exported in-point is the frame the editor actually selected.
        if (!options.RequiresReencode && start == TimeSpan.Zero)
        {
            var copyResult = await RunFfmpegAsync(
                ffmpegPath,
                inputPath,
                outputPath,
                start,
                duration,
                options,
                includeAudio: true,
                reencode: false,
                progress,
                cancellationToken).ConfigureAwait(false);

            if (copyResult)
            {
                progress?.Report(1);
                return;
            }

            try { File.Delete(outputPath); } catch { }
        }

        try { File.Delete(outputPath); } catch { }

        var reencodeResult = await RunFfmpegAsync(
            ffmpegPath,
            inputPath,
            outputPath,
            start,
            duration,
            options,
            includeAudio,
            reencode: true,
            progress,
            cancellationToken).ConfigureAwait(false);

        if (!reencodeResult || !File.Exists(outputPath))
            throw new InvalidOperationException("FFmpeg could not export the clip.");

        progress?.Report(1);
    }

    private static async Task<bool> RunFfmpegAsync(
        string ffmpegPath,
        string inputPath,
        string outputPath,
        TimeSpan start,
        TimeSpan duration,
        ClipEditOptions options,
        bool includeAudio,
        bool reencode,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        var tempFiles = new List<string>();

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = ffmpegPath,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            psi.ArgumentList.Add("-nostdin");
            psi.ArgumentList.Add("-hide_banner");
            psi.ArgumentList.Add("-loglevel");
            psi.ArgumentList.Add("error");
            psi.ArgumentList.Add("-ss");
            psi.ArgumentList.Add(FormatSeconds(start));
            // Limit the source before time-changing filters. An output-only -t
            // would truncate slow motion and freeze frames, or read past a trim
            // when speeding a clip up.
            if (reencode)
            {
                psi.ArgumentList.Add("-t");
                psi.ArgumentList.Add(FormatSeconds(duration));
            }
            psi.ArgumentList.Add("-i");
            psi.ArgumentList.Add(inputPath);
            psi.ArgumentList.Add("-t");
            psi.ArgumentList.Add(reencode
                ? FormatFilterNumber(GetOutputDurationSeconds(duration, options))
                : FormatSeconds(duration));
            psi.ArgumentList.Add("-map");
            psi.ArgumentList.Add("0:v:0");
            if (includeAudio)
            {
                psi.ArgumentList.Add("-map");
                psi.ArgumentList.Add("0:a?");
            }

            if (reencode)
            {
                AddVideoFilters(psi, options, start, duration, tempFiles);
                AddAudioFilters(psi, options, duration, includeAudio);

                FFmpegVideoEncoding.AddArguments(
                    psi,
                    options.VideoEncoder,
                    options.VideoBitrateMbps,
                    options.ExportQuality,
                    options.VideoCrf);
                psi.ArgumentList.Add("-r");
                psi.ArgumentList.Add(options.FrameRate.ToString(CultureInfo.InvariantCulture));

                if (includeAudio)
                {
                    psi.ArgumentList.Add("-c:a");
                    psi.ArgumentList.Add("aac");
                    psi.ArgumentList.Add("-b:a");
                    psi.ArgumentList.Add($"{options.AudioBitrateKbps}k");
                }
                else
                {
                    psi.ArgumentList.Add("-an");
                }
            }
            else
            {
                psi.ArgumentList.Add("-c");
                psi.ArgumentList.Add("copy");
                psi.ArgumentList.Add("-avoid_negative_ts");
                psi.ArgumentList.Add("make_zero");
            }

            psi.ArgumentList.Add("-movflags");
            psi.ArgumentList.Add("+faststart");
            if (progress is not null)
                FFmpegProgressReader.AddArguments(psi);
            psi.ArgumentList.Add("-y");
            psi.ArgumentList.Add(outputPath);

            using var process = Process.Start(psi);
            if (process is null)
                return false;

            try { process.PriorityClass = ProcessPriorityClass.BelowNormal; } catch { }

            var outputDuration = GetOutputDurationSeconds(duration, options);
            var progressTask = FFmpegProgressReader.ReadAsync(
                process.StandardOutput,
                outputDuration,
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

            if (process.ExitCode != 0)
            {
                Debug.WriteLine($"[Klipp] Edit {(reencode ? "reencode" : "copy")} failed: {stderr}");
                if (reencode)
                {
                    var detail = string.IsNullOrWhiteSpace(stderr)
                        ? $"Unknown FFmpeg error. Arguments: {string.Join(' ', psi.ArgumentList)}"
                        : stderr.Trim();
                    throw new InvalidOperationException($"FFmpeg export failed: {detail}");
                }
            }

            return process.ExitCode == 0 && File.Exists(outputPath);
        }
        finally
        {
            foreach (var tempFile in tempFiles)
            {
                try { File.Delete(tempFile); } catch { }
            }
        }
    }

    private static void AddVideoFilters(
        ProcessStartInfo psi,
        ClipEditOptions options,
        TimeSpan trimStart,
        TimeSpan sourceDuration,
        List<string> tempFiles)
    {
        var filters = new List<string>();

        filters.Add($"setpts=(PTS-STARTPTS)/{FormatFilterNumber(options.PlaybackSpeed)}");

        if (options.FitToCanvas)
            filters.Add(BuildFitToCanvasFilter());

        if (options.RequiresVideoTransform)
            filters.Add(BuildVideoTransformFilter(options));

        var vfxEnable = BuildEnableClause(options.VfxEnabled, options.VfxStart, options.VfxEnd, trimStart, sourceDuration, options);
        if (vfxEnable is not null)
            filters.AddRange(GetVfxFilters(options.VideoFilter, vfxEnable));

        var elementEnable = BuildEnableClause(options.ElementEnabled, options.ElementStart, options.ElementEnd, trimStart, sourceDuration, options);
        if (elementEnable is not null)
            filters.AddRange(GetElementFilters(options.ElementOverlay, elementEnable));

        var textEnable = BuildEnableClause(options.TextEnabled, options.TextStart, options.TextEnd, trimStart, sourceDuration, options);
        if (textEnable is not null && !string.IsNullOrWhiteSpace(options.TextOverlay))
        {
            var textStart = GetLayerStartSeconds(options.TextStart, trimStart, options);
            var textEnd = GetLayerEndSeconds(options.TextEnd, trimStart, sourceDuration, options);
            filters.AddRange(BuildDrawTextFilters(options.TextOverlay, options.TextStyle, options.TextPosition, options.TextAnchorX, options.TextAnchorY, options.TextSize, false, textEnable, tempFiles, options.TextAnimation, textStart, textEnd, options.TextBoxWidth, options.TextOpacity, options.TextKeyframes, options.TextFontFamily, options.TextFontFilePath, options.TextColor));
        }

        var subtitleEnable = BuildEnableClause(options.SubtitleEnabled, options.SubtitleStart, options.SubtitleEnd, trimStart, sourceDuration, options);
        if (subtitleEnable is not null && !string.IsNullOrWhiteSpace(options.SubtitleText))
        {
            var subtitleStart = GetLayerStartSeconds(options.SubtitleStart, trimStart, options);
            var subtitleEnd = GetLayerEndSeconds(options.SubtitleEnd, trimStart, sourceDuration, options);
            filters.AddRange(BuildDrawTextFilters(options.SubtitleText, options.SubtitleStyle, "custom", options.SubtitleAnchorX, options.SubtitleAnchorY, options.SubtitleSize, true, subtitleEnable, tempFiles, "none", subtitleStart, subtitleEnd, options.SubtitleBoxWidth, 1, [], options.SubtitleFontFamily, options.SubtitleFontFilePath, options.SubtitleColor));
        }

        foreach (var layer in options.ExtraTextLayers)
        {
            if (string.IsNullOrWhiteSpace(layer.Text))
                continue;

            var start = TimeSpan.FromSeconds(layer.StartSeconds);
            var end = TimeSpan.FromSeconds(layer.EndSeconds);
            var enable = BuildEnableClause(true, start, end, trimStart, sourceDuration, options);
            if (enable is null)
                continue;

            var animationStart = GetLayerStartSeconds(start, trimStart, options);
            var layerEnd = GetLayerEndSeconds(end, trimStart, sourceDuration, options);
            filters.AddRange(BuildDrawTextFilters(
                layer.Text,
                layer.Style,
                "custom",
                layer.AnchorX,
                layer.AnchorY,
                layer.Size,
                layer.IsSubtitle,
                enable,
                tempFiles,
                layer.Animation,
                animationStart,
                layerEnd,
                layer.BoxWidth,
                layer.Opacity,
                layer.Keyframes,
                layer.FontFamily,
                layer.FontFilePath,
                layer.Color));
        }

        if (options.FreezeFrameSeconds > 0.001)
            filters.Add($"tpad=stop_mode=clone:stop_duration={FormatFilterNumber(options.FreezeFrameSeconds)}");

        if (options.FadeInSeconds > 0.001)
            filters.Add($"fade=t=in:st=0:d={FormatFilterNumber(ClampEffectDuration(options.FadeInSeconds, sourceDuration, options))}");

        if (options.FadeOutSeconds > 0.001)
        {
            var outputDuration = GetOutputDurationSeconds(sourceDuration, options);
            var fadeDuration = Math.Min(options.FadeOutSeconds, outputDuration);
            var start = Math.Max(0, outputDuration - fadeDuration);
            filters.Add($"fade=t=out:st={FormatFilterNumber(start)}:d={FormatFilterNumber(fadeDuration)}");
        }

        if (options.RequiresOutputResize)
            filters.Add(BuildOutputCanvasFilter(options.OutputWidth, options.OutputHeight));

        if (filters.Count == 0)
            return;

        var filterGraph = string.Join(",", filters);
        if (filterGraph.Length > 6000)
        {
            var filterScriptPath = Path.Combine(Path.GetTempPath(), $"klipp-filter-{Guid.NewGuid():N}.txt");
            File.WriteAllText(filterScriptPath, filterGraph, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            tempFiles.Add(filterScriptPath);
            psi.ArgumentList.Add("-filter_script:v");
            psi.ArgumentList.Add(filterScriptPath);
        }
        else
        {
            psi.ArgumentList.Add("-vf");
            psi.ArgumentList.Add(filterGraph);
        }
    }

    private static void AddAudioFilters(
        ProcessStartInfo psi,
        ClipEditOptions options,
        TimeSpan sourceDuration,
        bool includeAudio)
    {
        if (!includeAudio || !options.RequiresAudioFilter)
            return;

        var filters = new List<string> { "asetpts=PTS-STARTPTS" };
        if (Math.Abs(options.PlaybackSpeed - 1.0) > 0.001)
            filters.Add($"atempo={FormatFilterNumber(options.PlaybackSpeed)}");
        if (Math.Abs(options.AudioVolume - 1.0) > 0.001)
            filters.Add($"volume={FormatFilterNumber(options.AudioVolume)}");
        if (options.FreezeFrameSeconds > 0.001)
            filters.Add($"apad=pad_dur={FormatFilterNumber(options.FreezeFrameSeconds)}");
        if (options.FadeInSeconds > 0.001)
            filters.Add($"afade=t=in:st=0:d={FormatFilterNumber(ClampEffectDuration(options.FadeInSeconds, sourceDuration, options))}");
        if (options.FadeOutSeconds > 0.001)
        {
            var outputDuration = GetOutputDurationSeconds(sourceDuration, options);
            var fadeDuration = Math.Min(options.FadeOutSeconds, outputDuration);
            var start = Math.Max(0, outputDuration - fadeDuration);
            filters.Add($"afade=t=out:st={FormatFilterNumber(start)}:d={FormatFilterNumber(fadeDuration)}");
        }

        if (filters.Count == 0)
            return;

        psi.ArgumentList.Add("-af");
        psi.ArgumentList.Add(string.Join(",", filters));
    }

    internal static IEnumerable<string> GetVfxFilters(string filter, string? enable = null)
    {
        var curves = EditorLookCurves.Find(filter);
        static string Points(IReadOnlyList<PiecewiseLinearCurvePoint> points) =>
            string.Join(' ', points.Select(point =>
                $"{point.X.ToString("G17", CultureInfo.InvariantCulture)}/{point.Y.ToString("G17", CultureInfo.InvariantCulture)}"));
        var filters = curves is not null
            ? new[] { $"curves=red='{Points(curves.Red)}':green='{Points(curves.Green)}':blue='{Points(curves.Blue)}'" }
            : filter switch
        {
            "boost" => new[] { "eq=contrast=1.07:saturation=1.12:brightness=0.008" },
            "warm" => ["colorbalance=rs=0.045:gs=0.012:bs=-0.035:rm=0.025:bm=-0.018:pl=1", "eq=saturation=1.04:contrast=1.015"],
            "cool" => ["colorbalance=rs=-0.025:gs=0.008:bs=0.045:rm=-0.012:bm=0.022:pl=1", "eq=saturation=0.98:contrast=1.015"],
            "mono" => ["hue=s=0"],
            "cinematic" => ["curves=master='0/0.005 0.2/0.16 0.5/0.5 0.8/0.84 1/0.985'", "colorbalance=rs=-0.018:gs=0.008:bs=0.025:rh=0.022:bh=-0.012:pl=1", "eq=saturation=0.9"],
            "sharpen" => ["unsharp=5:5:0.55:5:5:0.0"],
            "blur" => ["gblur=sigma=2"],
            "vintage" => ["curves=master='0/0.035 0.25/0.27 0.75/0.73 1/0.95'", "colorbalance=rs=0.035:gs=0.015:bs=-0.025:rh=0.02:bh=-0.025:pl=1", "eq=saturation=0.8"],
            "vibrant" => ["eq=contrast=1.06:saturation=1.22:brightness=0.006"],
            "noir" => ["hue=s=0", "curves=master='0/0 0.2/0.12 0.5/0.51 0.8/0.9 1/1'"],
            "dream" => ["gblur=sigma=0.4", "eq=saturation=0.96:brightness=0.018:gamma=1.025", "colorbalance=rs=0.018:bs=0.025:pl=1"],
            "film" => ["curves=master='0/0.025 0.2/0.18 0.5/0.51 0.8/0.82 1/0.97'", "colorbalance=rs=0.016:bs=-0.012:rh=0.02:bh=-0.018:pl=1", "eq=saturation=0.94"],
            "soft" => ["curves=master='0/0.015 0.25/0.27 0.75/0.74 1/0.985'", "eq=saturation=0.94:gamma=1.025", "unsharp=5:5:-0.15:5:5:0"],
            "tealorange" => ["colorbalance=rs=-0.035:gs=0.018:bs=0.05:rm=0.018:bm=-0.018:rh=0.045:gh=0.012:bh=-0.04:pl=1", "eq=saturation=1.06:contrast=1.04"],
            "glitch" => ["chromashift=cbh=8:crh=-8", "eq=contrast=1.18:saturation=1.25"],
            "vignette" => ["vignette=angle=PI/5:mode=forward"],
            _ => Array.Empty<string>()
        };
        return string.IsNullOrWhiteSpace(enable)
            ? filters
            : filters.Select(value => $"{value}:{enable}");
    }

    private static IEnumerable<string> GetElementFilters(string element, string enable)
    {
        return element switch
        {
            "focus" =>
            [
                $"drawbox=x=iw*0.10:y=ih*0.12:w=iw*0.80:h=ih*0.76:color=white@0.82:t=4:{enable}"
            ],
            "letterbox" =>
            [
                $"drawbox=x=0:y=0:w=iw:h=ih*0.08:color=black@1:t=fill:{enable}",
                $"drawbox=x=0:y=ih-ih*0.08:w=iw:h=ih*0.08:color=black@1:t=fill:{enable}"
            ],
            "center" =>
            [
                $"drawbox=x=iw/2-28:y=ih/2-2:w=56:h=4:color=white@0.78:t=fill:{enable}",
                $"drawbox=x=iw/2-2:y=ih/2-28:w=4:h=56:color=white@0.78:t=fill:{enable}"
            ],
            _ => []
        };
    }

    private static string BuildVideoTransformFilter(ClipEditOptions options)
    {
        var scale = FormatFilterNumber(options.VideoScale);
        var offsetX = FormatFilterNumber(options.VideoOffsetX);
        var offsetY = FormatFilterNumber(options.VideoOffsetY);
        var padWidth = $"if(gte({scale}\\,1)\\,iw\\,trunc(iw/{scale}/2)*2)";
        var padHeight = $"if(gte({scale}\\,1)\\,ih\\,trunc(ih/{scale}/2)*2)";
        var padX = $"if(gte({scale}\\,1)\\,0\\,min(max(0\\,(ow-iw)/2+{offsetX}*ow)\\,ow-iw))";
        var padY = $"if(gte({scale}\\,1)\\,0\\,min(max(0\\,(oh-ih)/2+{offsetY}*oh)\\,oh-ih))";
        var cropWidth = $"if(gte({scale}\\,1)\\,trunc(iw/{scale}/2)*2\\,iw)";
        var cropHeight = $"if(gte({scale}\\,1)\\,trunc(ih/{scale}/2)*2\\,ih)";
        var cropX = $"if(gte({scale}\\,1)\\,min(max(0\\,(iw-out_w)/2-{offsetX}*out_w)\\,iw-out_w)\\,0)";
        var cropY = $"if(gte({scale}\\,1)\\,min(max(0\\,(ih-out_h)/2-{offsetY}*out_h)\\,ih-out_h)\\,0)";

        return $"scale=w=trunc(iw*{scale}/2)*2:h=trunc(ih*{scale}/2)*2,"
            + $"pad=w={padWidth}:h={padHeight}:x={padX}:y={padY}:color=black,"
            + $"crop=w={cropWidth}:h={cropHeight}:x={cropX}:y={cropY}";
    }

    private static string BuildFitToCanvasFilter()
    {
        return "scale=1280:720:force_original_aspect_ratio=decrease:flags=lanczos,"
            + "pad=1280:720:(ow-iw)/2:(oh-ih)/2:color=black,"
            + "setsar=1";
    }

    private static string BuildOutputCanvasFilter(int width, int height)
    {
        return $"scale={width}:{height}:force_original_aspect_ratio=decrease:flags=lanczos," +
               $"pad={width}:{height}:(ow-iw)/2:(oh-ih)/2:color=black,setsar=1";
    }

    private static IEnumerable<string> BuildDrawTextFilters(
        string text,
        string style,
        string position,
        double anchorX,
        double anchorY,
        double fontSize,
        bool subtitle,
        string enable,
        List<string> tempFiles,
        string animation,
        double animationStart,
        double layerEnd,
        double boxWidth,
        double opacity,
        IReadOnlyList<ClipTextKeyframe> keyframes,
        string fontFamily,
        string fontFilePath,
        string color)
    {
        var sizeChanges = keyframes.Count > 1 &&
                          keyframes.Any(keyframe => Math.Abs(keyframe.Size - keyframes[0].Size) > 0.01);
        if (!sizeChanges || layerEnd <= animationStart + 0.01)
        {
            return
            [
                BuildDrawTextFilter(
                    text,
                    style,
                    position,
                    anchorX,
                    anchorY,
                    fontSize,
                    subtitle,
                    enable,
                    tempFiles,
                    animation,
                    animationStart,
                    boxWidth,
                    opacity,
                    keyframes,
                    fontFamily,
                    fontFilePath,
                    color,
                    dynamicFontSize: false)
            ];
        }

        var duration = layerEnd - animationStart;
        var sampleCount = Math.Clamp((int)Math.Ceiling(duration * 10), 2, 120);
        var filters = new List<string>(sampleCount);
        for (var index = 0; index < sampleCount; index++)
        {
            var sliceStart = animationStart + duration * index / sampleCount;
            var sliceEnd = animationStart + duration * (index + 1) / sampleCount;
            var sampleTime = (sliceStart + sliceEnd) / 2;
            var sampledSize = EvaluateTextKeyframeValue(
                keyframes,
                keyframe => keyframe.Size,
                fontSize,
                sampleTime);
            var sliceEnable = CombineTextEnable(enable, sliceStart, sliceEnd);
            filters.Add(BuildDrawTextFilter(
                text,
                style,
                position,
                anchorX,
                anchorY,
                sampledSize,
                subtitle,
                sliceEnable,
                tempFiles,
                animation,
                animationStart,
                boxWidth,
                opacity,
                keyframes,
                fontFamily,
                fontFilePath,
                color,
                dynamicFontSize: false));
        }

        return filters;
    }

    private static string BuildDrawTextFilter(
        string text,
        string style,
        string position,
        double anchorX,
        double anchorY,
        double fontSize,
        bool subtitle,
        string enable,
        List<string> tempFiles,
        string animation,
        double animationStart,
        double boxWidth,
        double opacity,
        IReadOnlyList<ClipTextKeyframe> keyframes,
        string fontFamily,
        string fontFilePath,
        string color,
        bool dynamicFontSize)
    {
        var maximumFontSize = keyframes.Count == 0
            ? fontSize
            : Math.Max(fontSize, keyframes.Max(keyframe => keyframe.Size));
        var exportText = style is "meme" or "impact"
            ? text.Trim().ToUpperInvariant()
            : text.Trim();
        exportText = WrapTextForExport(exportText, maximumFontSize, boxWidth, subtitle);
        var textFile = CreateTempTextFile(exportText, tempFiles);
        var fontFile = GetFontFile(fontFamily, style, fontFilePath);
        var anchorXExpression = BuildTextValueExpression(keyframes, keyframe => keyframe.AnchorX, anchorX);
        var anchorYExpression = BuildTextValueExpression(keyframes, keyframe => keyframe.AnchorY, anchorY);
        var fontSizeExpression = dynamicFontSize
            ? BuildTextValueExpression(keyframes, keyframe => keyframe.Size, fontSize)
            : FormatFilterNumber(fontSize);
        var opacityExpression = BuildTextValueExpression(keyframes, keyframe => keyframe.Opacity, opacity);
        var x = GetDrawTextX(position, subtitle, anchorXExpression, animation, animationStart);
        var y = GetDrawTextY(position, subtitle, anchorYExpression, animation, animationStart);
        var styling = GetDrawTextStyle(style, subtitle, fontSizeExpression, animation, animationStart, opacityExpression, color);

        return "drawtext="
            + $"fontfile={EscapeFilterPath(fontFile)}:"
            + $"textfile={EscapeFilterPath(textFile)}:"
            + $"x={x}:y={y}:"
            + styling
            + $":{enable}";
    }

    private static string CombineTextEnable(string enable, double start, double end)
    {
        var baseExpression = enable.StartsWith("enable='", StringComparison.Ordinal) && enable.EndsWith('\'')
            ? enable[8..^1]
            : "1";
        return $"enable='({baseExpression})*gte(t,{FormatFilterNumber(start)})*lt(t,{FormatFilterNumber(end)})'";
    }

    private static double EvaluateTextKeyframeValue(
        IReadOnlyList<ClipTextKeyframe> keyframes,
        Func<ClipTextKeyframe, double> selector,
        double fallback,
        double timelineSeconds)
    {
        if (keyframes.Count == 0)
            return fallback;

        var ordered = keyframes.OrderBy(keyframe => keyframe.TimeSeconds).ToList();
        if (timelineSeconds <= ordered[0].TimeSeconds)
            return selector(ordered[0]);
        if (timelineSeconds >= ordered[^1].TimeSeconds)
            return selector(ordered[^1]);

        for (var index = 1; index < ordered.Count; index++)
        {
            var right = ordered[index];
            if (timelineSeconds > right.TimeSeconds)
                continue;

            var left = ordered[index - 1];
            var duration = Math.Max(0.0001, right.TimeSeconds - left.TimeSeconds);
            var progress = Math.Clamp((timelineSeconds - left.TimeSeconds) / duration, 0, 1);
            progress = right.Easing switch
            {
                "ease-in" => progress * progress,
                "ease-out" => 1 - Math.Pow(1 - progress, 2),
                "ease-in-out" => progress * progress * (3 - 2 * progress),
                "hold" => progress >= 1 ? 1 : 0,
                _ => progress
            };
            return selector(left) + (selector(right) - selector(left)) * progress;
        }

        return selector(ordered[^1]);
    }

    private static string WrapTextForExport(string text, double fontSize, double boxWidth, bool subtitle)
    {
        if (string.IsNullOrWhiteSpace(text) || boxWidth <= 0)
            return text;

        var estimatedCharWidth = Math.Max(7, fontSize * (subtitle ? 0.52 : 0.56));
        var maxChars = Math.Clamp((int)Math.Floor(boxWidth / estimatedCharWidth), subtitle ? 12 : 6, 72);
        var result = new StringBuilder();
        var sourceLines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');

        for (var lineIndex = 0; lineIndex < sourceLines.Length; lineIndex++)
        {
            if (lineIndex > 0)
                result.Append('\n');

            var words = sourceLines[lineIndex].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (words.Length == 0)
                continue;

            var currentLength = 0;
            for (var wordIndex = 0; wordIndex < words.Length; wordIndex++)
            {
                var word = words[wordIndex];
                var separator = currentLength == 0 ? 0 : 1;

                if (currentLength > 0 && currentLength + separator + word.Length > maxChars)
                {
                    result.Append('\n');
                    currentLength = 0;
                }
                else if (currentLength > 0)
                {
                    result.Append(' ');
                    currentLength++;
                }

                result.Append(word);
                currentLength += word.Length;
            }
        }

        return result.ToString();
    }

    private static string GetDrawTextStyle(
        string style,
        bool subtitle,
        string fontSizeExpression,
        string animation,
        double animationStart,
        string opacityExpression,
        string color)
    {
        var fontSize = $"'{fontSizeExpression}'";
        var alpha = GetTextAlphaOption(animation, animationStart, opacityExpression);
        var fontColor = ToFfmpegColor(EditorTextTypography.ResolveColor(color, style));

        if (subtitle)
        {
            return style switch
            {
                "bold" => $"fontcolor={fontColor}:fontsize={fontSize}:borderw=4:bordercolor=black@0.92:box=1:boxcolor=black@0.44:boxborderw=10:line_spacing=8{alpha}",
                "highlight" => $"fontcolor={fontColor}:fontsize={fontSize}:box=1:boxcolor=yellow@0.92:boxborderw=10:line_spacing=8{alpha}",
                "pixel" => $"fontcolor={fontColor}:fontsize={fontSize}:borderw=3:bordercolor=black@0.92:box=1:boxcolor=black@0.50:boxborderw=10:line_spacing=8{alpha}",
                _ => $"fontcolor={fontColor}:fontsize={fontSize}:borderw=3:bordercolor=black@0.90:box=1:boxcolor=black@0.42:boxborderw=10:line_spacing=8{alpha}"
            };
        }

        return style switch
        {
            "bold" => $"fontcolor={fontColor}:fontsize={fontSize}:borderw=4:bordercolor=black@0.85:line_spacing=8{alpha}",
            "meme" => $"fontcolor={fontColor}:fontsize={fontSize}:borderw=5:bordercolor=black@0.95:line_spacing=8{alpha}",
            "highlight" => $"fontcolor={fontColor}:fontsize={fontSize}:box=1:boxcolor=yellow@0.92:boxborderw=12:line_spacing=8{alpha}",
            "pixel" => $"fontcolor={fontColor}:fontsize={fontSize}:borderw=3:bordercolor=black@0.85:line_spacing=8{alpha}",
            "outline" => $"fontcolor={fontColor}:fontsize={fontSize}:borderw=6:bordercolor=black@0.95:line_spacing=8{alpha}",
            "neon" => $"fontcolor={fontColor}:fontsize={fontSize}:borderw=4:bordercolor=0xB84CFF@0.90:shadowcolor={fontColor}@0.55:shadowx=0:shadowy=0:line_spacing=8{alpha}",
            "shadow" => $"fontcolor={fontColor}:fontsize={fontSize}:borderw=2:bordercolor=black@0.45:shadowcolor=black@0.75:shadowx=6:shadowy=6:line_spacing=8{alpha}",
            "typewriter" => $"fontcolor={fontColor}:fontsize={fontSize}:borderw=2:bordercolor=black@0.80:line_spacing=8{alpha}",
            "impact" => $"fontcolor={fontColor}:fontsize={fontSize}:borderw=6:bordercolor=black@0.95:line_spacing=8{alpha}",
            "sticker" => $"fontcolor={fontColor}:fontsize={fontSize}:borderw=3:bordercolor=black@0.70:box=1:boxcolor=0xA970FF@0.90:boxborderw=14:line_spacing=8{alpha}",
            "caption" => $"fontcolor={fontColor}:fontsize={fontSize}:box=1:boxcolor=black@0.66:boxborderw=16:line_spacing=8{alpha}",
            _ => $"fontcolor={fontColor}:fontsize={fontSize}:borderw=3:bordercolor=black@0.85:line_spacing=8{alpha}"
        };
    }

    private static string ToFfmpegColor(string color) =>
        color.StartsWith('#') ? $"0x{color[1..]}" : "white";

    private static string GetDrawTextX(string position, bool subtitle, string anchorX, string animation, double animationStart)
    {
        var slide = string.Equals(animation, "slide-left", StringComparison.OrdinalIgnoreCase)
            ? $"+(1-min(max((t-{FormatFilterNumber(animationStart)})/0.45\\,0)\\,1))*96"
            : string.Empty;

        if (!string.Equals(position, "custom", StringComparison.OrdinalIgnoreCase))
            return $"(w-text_w)/2{slide}";

        return $"min(max(0\\,w*({anchorX})-text_w/2)\\,w-text_w){slide}";
    }

    private static string GetDrawTextY(string position, bool subtitle, string anchorY, string animation, double animationStart)
    {
        var slide = string.Equals(animation, "slide-up", StringComparison.OrdinalIgnoreCase)
            ? $"+(1-min(max((t-{FormatFilterNumber(animationStart)})/0.45\\,0)\\,1))*72"
            : string.Empty;

        if (string.Equals(position, "custom", StringComparison.OrdinalIgnoreCase))
            return $"min(max(0\\,h*({anchorY})-text_h/2)\\,h-text_h){slide}";

        var baseY = position switch
        {
            "top" => "h*0.08",
            "center" => "(h-text_h)/2",
            _ => "h-text_h-h*0.10"
        };
        return $"{baseY}{slide}";
    }

    private static string GetTextAlphaOption(string animation, double animationStart, string opacityExpression)
    {
        var start = FormatFilterNumber(animationStart);
        var progress = $"min(max((t-{start})/0.45\\,0)\\,1)";
        var animationExpression = animation switch
        {
            "pop" => progress,
            "type" => progress,
            "fade" => progress,
            "slide-up" => progress,
            "slide-left" => progress,
            _ => "1"
        };

        return $":alpha='({opacityExpression})*({animationExpression})'";
    }

    private static string BuildTextValueExpression(
        IReadOnlyList<ClipTextKeyframe> keyframes,
        Func<ClipTextKeyframe, double> selector,
        double fallback)
    {
        if (keyframes.Count == 0)
            return FormatFilterNumber(fallback);

        var ordered = keyframes.OrderBy(keyframe => keyframe.TimeSeconds).ToList();
        if (ordered.Count == 1)
            return FormatFilterNumber(selector(ordered[0]));

        string Segment(int rightIndex)
        {
            var left = ordered[rightIndex - 1];
            var right = ordered[rightIndex];
            var duration = Math.Max(0.0001, right.TimeSeconds - left.TimeSeconds);
            var progress = $"max(0\\,min(1\\,(t-{FormatFilterNumber(left.TimeSeconds)})/{FormatFilterNumber(duration)}))";
            var eased = BuildTextEasingExpression(progress, right.Easing);
            var leftValue = FormatFilterNumber(selector(left));
            var rightValue = FormatFilterNumber(selector(right));
            return $"lerp({leftValue}\\,{rightValue}\\,{eased})";
        }

        var expression = Segment(ordered.Count - 1);
        for (var rightIndex = ordered.Count - 2; rightIndex >= 1; rightIndex--)
        {
            expression = $"if(lt(t\\,{FormatFilterNumber(ordered[rightIndex].TimeSeconds)})\\," +
                         $"{Segment(rightIndex)}\\,{expression})";
        }

        return expression;
    }

    private static string BuildTextEasingExpression(string progress, string easing) => easing switch
    {
        "ease-in" => $"pow(({progress})\\,2)",
        "ease-out" => $"1-pow(1-({progress})\\,2)",
        "ease-in-out" => $"({progress})*({progress})*(3-2*({progress}))",
        "hold" => "0",
        _ => progress
    };

    public static string ResolveFontFileForExport(
        string? fontFamily,
        string? style,
        string? customFontFilePath)
    {
        return GetFontFile(
            EditorTextTypography.NormalizeFontFamily(fontFamily),
            string.IsNullOrWhiteSpace(style) ? "plain" : style.Trim().ToLowerInvariant(),
            customFontFilePath);
    }

    private static string GetFontFile(string fontFamily, string style, string? customFontFilePath)
    {
        var customPath = EditorTextTypography.NormalizeFontFilePath(customFontFilePath);
        if (!string.IsNullOrWhiteSpace(customPath) && File.Exists(customPath))
            return customPath;

        var fontsDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            "Fonts");

        var boldStyle = style is "bold" or "meme" or "outline" or "neon" or "shadow" or "impact" or "sticker";
        var normalizedFont = EditorTextTypography.NormalizeFontFamily(fontFamily);
        string[] candidates = normalizedFont switch
        {
            "segoeui" => boldStyle ? ["segoeuib.ttf", "segoeui.ttf"] : ["segoeui.ttf"],
            "arial" => boldStyle ? ["arialbd.ttf", "arial.ttf"] : ["arial.ttf"],
            "georgia" => boldStyle ? ["georgiab.ttf", "georgia.ttf"] : ["georgia.ttf"],
            "times" => boldStyle ? ["timesbd.ttf", "times.ttf"] : ["times.ttf"],
            "consolas" => boldStyle ? ["consolab.ttf", "consola.ttf"] : ["consola.ttf"],
            "impact" => ["impact.ttf", "arialbd.ttf"],
            "trebuchet" => boldStyle ? ["trebucbd.ttf", "trebuc.ttf"] : ["trebuc.ttf"],
            _ => style switch
            {
                "bold" or "meme" or "sticker" => ["arialbd.ttf", "segoeuib.ttf", "segoeui.ttf"],
                "impact" => ["impact.ttf", "arialbd.ttf", "segoeuib.ttf", "segoeui.ttf"],
                "pixel" or "typewriter" => ["consola.ttf", "cour.ttf", "segoeui.ttf"],
                _ => ["segoeui.ttf", "arial.ttf"]
            }
        };

        foreach (var candidate in candidates)
        {
            var path = Path.Combine(fontsDirectory, candidate);
            if (File.Exists(path))
                return path;
        }

        return Path.Combine(fontsDirectory, "segoeui.ttf");
    }

    private static string CreateTempTextFile(string text, List<string> tempFiles)
    {
        var path = Path.Combine(Path.GetTempPath(), $"klipp-text-{Guid.NewGuid():N}.txt");
        File.WriteAllText(path, text, System.Text.Encoding.UTF8);
        tempFiles.Add(path);
        return path;
    }

    private async Task<bool> HasAudioStreamAsync(string inputPath, CancellationToken cancellationToken)
    {
        try
        {
            var ffprobePath = await _locator.GetFFprobePathAsync(cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            var psi = new ProcessStartInfo
            {
                FileName = ffprobePath,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            psi.ArgumentList.Add("-v");
            psi.ArgumentList.Add("error");
            psi.ArgumentList.Add("-select_streams");
            psi.ArgumentList.Add("a:0");
            psi.ArgumentList.Add("-show_entries");
            psi.ArgumentList.Add("stream=codec_type");
            psi.ArgumentList.Add("-of");
            psi.ArgumentList.Add("csv=p=0");
            psi.ArgumentList.Add(inputPath);

            using var process = Process.Start(psi);
            if (process is null)
                return false;

            var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            _ = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            var output = await outputTask.ConfigureAwait(false);

            return process.ExitCode == 0
                && output.Contains("audio", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Debug.WriteLine($"[Klipp] Kunde inte läsa audio-stream inför export: {ex.Message}");
            return false;
        }
    }

    private static double ClampEffectDuration(double seconds, TimeSpan sourceDuration, ClipEditOptions options)
    {
        return Math.Clamp(seconds, 0, GetOutputDurationSeconds(sourceDuration, options));
    }

    private static double GetOutputDurationSeconds(TimeSpan sourceDuration, ClipEditOptions options)
    {
        return Math.Max(0.01, sourceDuration.TotalSeconds / options.PlaybackSpeed + options.FreezeFrameSeconds);
    }

    private static string? BuildEnableClause(
        bool enabled,
        TimeSpan layerStart,
        TimeSpan layerEnd,
        TimeSpan trimStart,
        TimeSpan trimDuration,
        ClipEditOptions options)
    {
        if (!enabled || layerEnd <= layerStart)
            return null;

        var trimEnd = trimStart + trimDuration;
        var clippedStart = layerStart > trimStart ? layerStart : trimStart;
        var clippedEnd = layerEnd < trimEnd ? layerEnd : trimEnd;
        if (clippedEnd <= clippedStart)
            return null;

        var startSeconds = Math.Max(0, (clippedStart - trimStart).TotalSeconds / options.PlaybackSpeed);
        var endSeconds = Math.Max(startSeconds, (clippedEnd - trimStart).TotalSeconds / options.PlaybackSpeed);
        if (endSeconds - startSeconds <= 0.01)
            return null;

        return $"enable='between(t,{FormatFilterNumber(startSeconds)},{FormatFilterNumber(endSeconds)})'";
    }

    private static double GetLayerStartSeconds(TimeSpan layerStart, TimeSpan trimStart, ClipEditOptions options)
    {
        var clippedStart = layerStart > trimStart ? layerStart : trimStart;
        return Math.Max(0, (clippedStart - trimStart).TotalSeconds / options.PlaybackSpeed);
    }

    private static double GetLayerEndSeconds(
        TimeSpan layerEnd,
        TimeSpan trimStart,
        TimeSpan trimDuration,
        ClipEditOptions options)
    {
        var trimEnd = trimStart + trimDuration;
        var clippedEnd = layerEnd < trimEnd ? layerEnd : trimEnd;
        return Math.Max(0, (clippedEnd - trimStart).TotalSeconds / options.PlaybackSpeed);
    }

    internal static string EscapeFilterPath(string value)
    {
        // FFmpeg parses option values and the surrounding graph separately.
        // ArgumentList bypasses the shell, so exactly these two levels apply.
        static string Escape(string text, string specialCharacters)
        {
            var result = new StringBuilder(text.Length);
            foreach (var character in text)
            {
                if (specialCharacters.Contains(character))
                    result.Append('\\');
                result.Append(character);
            }
            return result.ToString();
        }

        var normalized = value.Replace("\\", "/", StringComparison.Ordinal);
        return Escape(Escape(normalized, "\\':"), "\\'[],;");
    }

    private static string FormatSeconds(TimeSpan value)
    {
        return Math.Max(0, value.TotalSeconds).ToString("0.###", CultureInfo.InvariantCulture);
    }

    private static string FormatFilterNumber(double value)
    {
        return value.ToString("0.######", CultureInfo.InvariantCulture);
    }
}

public sealed class ClipTextOverlay
{
    public ClipTextOverlay(
        string text,
        string? style,
        string? animation,
        double anchorX,
        double anchorY,
        double size,
        double boxWidth,
        double startSeconds,
        double endSeconds,
        bool isSubtitle = false,
        double opacity = 1,
        IReadOnlyList<ClipTextKeyframe>? keyframes = null,
        string? fontFamily = null,
        string? color = null,
        string? fontFilePath = null,
        string? fontFamilyName = null)
    {
        Text = text?.Trim() ?? string.Empty;
        Style = string.IsNullOrWhiteSpace(style) ? "plain" : style.Trim().ToLowerInvariant();
        Animation = string.IsNullOrWhiteSpace(animation) ? "none" : animation.Trim().ToLowerInvariant();
        FontFamily = EditorTextTypography.NormalizeFontFamily(fontFamily);
        FontFilePath = EditorTextTypography.NormalizeFontFilePath(fontFilePath);
        FontFamilyName = EditorTextTypography.NormalizeCustomFontFamilyName(fontFamilyName);
        Color = EditorTextTypography.NormalizeColor(color);
        AnchorX = Math.Clamp(anchorX, 0, 1);
        AnchorY = Math.Clamp(anchorY, 0, 1);
        IsSubtitle = isSubtitle;
        Size = Math.Clamp(size, isSubtitle ? 18 : 12, isSubtitle ? 160 : 220);
        BoxWidth = Math.Clamp(boxWidth, isSubtitle ? 220 : 96, 1280);
        Opacity = Math.Clamp(opacity, 0, 1);
        StartSeconds = Math.Max(0, startSeconds);
        EndSeconds = Math.Max(StartSeconds + 0.1, endSeconds);
        Keyframes = keyframes ?? Array.Empty<ClipTextKeyframe>();
    }

    public string Text { get; }
    public string Style { get; }
    public string Animation { get; }
    public string FontFamily { get; }
    public string FontFilePath { get; }
    public string FontFamilyName { get; }
    public string Color { get; }
    public double AnchorX { get; }
    public double AnchorY { get; }
    public double Size { get; }
    public double BoxWidth { get; }
    public double Opacity { get; }
    public double StartSeconds { get; }
    public double EndSeconds { get; }
    public bool IsSubtitle { get; }
    public IReadOnlyList<ClipTextKeyframe> Keyframes { get; }
}

public sealed record ClipTextKeyframe(
    double TimeSeconds,
    double AnchorX,
    double AnchorY,
    double Size,
    double Opacity,
    string Easing);

public sealed class ClipEditOptions
{
    public static ClipEditOptions Default { get; } = new();

    public ClipEditOptions(
        double playbackSpeed = 1.0,
        double audioVolume = 1.0,
        bool muteAudio = false,
        string? videoFilter = null,
        string? textOverlay = null,
        string? textStyle = null,
        string? textPosition = null,
        string? elementOverlay = null,
        double fadeInSeconds = 0,
        double fadeOutSeconds = 0,
        double freezeFrameSeconds = 0,
        string? subtitleText = null,
        bool textEnabled = false,
        TimeSpan textStart = default,
        TimeSpan textEnd = default,
        bool vfxEnabled = false,
        TimeSpan vfxStart = default,
        TimeSpan vfxEnd = default,
        bool subtitleEnabled = false,
        TimeSpan subtitleStart = default,
        TimeSpan subtitleEnd = default,
        bool elementEnabled = false,
        TimeSpan elementStart = default,
        TimeSpan elementEnd = default,
        double textAnchorX = 0.5,
        double textAnchorY = 0.84,
        double textSize = 64,
        string? subtitleStyle = null,
        double subtitleAnchorX = 0.5,
        double subtitleAnchorY = 0.88,
        double subtitleSize = 42,
        double videoScale = 1.0,
        double videoOffsetX = 0,
        double videoOffsetY = 0,
        bool fitToCanvas = false,
        string? textAnimation = null,
        double textBoxWidth = 420,
        double subtitleBoxWidth = 760,
        IReadOnlyList<ClipTextOverlay>? extraTextLayers = null,
        int videoCrf = 20,
        int frameRate = 60,
        string? videoEncoder = null,
        int videoBitrateMbps = 16,
        string? exportQuality = null,
        int outputWidth = 0,
        int outputHeight = 0,
        int audioBitrateKbps = 192,
        double textOpacity = 1,
        IReadOnlyList<ClipTextKeyframe>? textKeyframes = null,
        string? textFontFamily = null,
        string? textColor = null,
        string? subtitleFontFamily = null,
        string? subtitleColor = null,
        string? textFontFilePath = null,
        string? textFontFamilyName = null,
        string? subtitleFontFilePath = null,
        string? subtitleFontFamilyName = null)
    {
        PlaybackSpeed = Math.Clamp(playbackSpeed, 0.5, 2.0);
        AudioVolume = Math.Clamp(audioVolume, 0, 2.0);
        MuteAudio = muteAudio;
        VideoFilter = Normalize(videoFilter, "none");
        TextOverlay = textOverlay?.Trim() ?? string.Empty;
        TextStyle = Normalize(textStyle, "plain");
        TextAnimation = Normalize(textAnimation, "none");
        TextFontFamily = EditorTextTypography.NormalizeFontFamily(textFontFamily);
        TextFontFilePath = EditorTextTypography.NormalizeFontFilePath(textFontFilePath);
        TextFontFamilyName = EditorTextTypography.NormalizeCustomFontFamilyName(textFontFamilyName);
        TextColor = EditorTextTypography.NormalizeColor(textColor);
        TextPosition = Normalize(textPosition, "bottom");
        TextAnchorX = Math.Clamp(textAnchorX, 0, 1);
        TextAnchorY = Math.Clamp(textAnchorY, 0, 1);
        TextSize = Math.Clamp(textSize, 12, 220);
        TextBoxWidth = Math.Clamp(textBoxWidth, 96, 1280);
        TextOpacity = Math.Clamp(textOpacity, 0, 1);
        TextKeyframes = textKeyframes ?? Array.Empty<ClipTextKeyframe>();
        SubtitleStyle = Normalize(subtitleStyle, "subtitle");
        SubtitleFontFamily = EditorTextTypography.NormalizeFontFamily(subtitleFontFamily);
        SubtitleFontFilePath = EditorTextTypography.NormalizeFontFilePath(subtitleFontFilePath);
        SubtitleFontFamilyName = EditorTextTypography.NormalizeCustomFontFamilyName(subtitleFontFamilyName);
        SubtitleColor = EditorTextTypography.NormalizeColor(subtitleColor);
        SubtitleAnchorX = Math.Clamp(subtitleAnchorX, 0, 1);
        SubtitleAnchorY = Math.Clamp(subtitleAnchorY, 0, 1);
        SubtitleSize = Math.Clamp(subtitleSize, 18, 160);
        SubtitleBoxWidth = Math.Clamp(subtitleBoxWidth, 220, 1280);
        ElementOverlay = Normalize(elementOverlay, "none");
        FadeInSeconds = Math.Clamp(fadeInSeconds, 0, 5);
        FadeOutSeconds = Math.Clamp(fadeOutSeconds, 0, 5);
        FreezeFrameSeconds = Math.Clamp(freezeFrameSeconds, 0, 5);
        VideoScale = Math.Clamp(videoScale, 0.25, 3.0);
        VideoOffsetX = Math.Clamp(videoOffsetX, -0.5, 0.5);
        VideoOffsetY = Math.Clamp(videoOffsetY, -0.5, 0.5);
        FitToCanvas = fitToCanvas;
        SubtitleText = subtitleText?.Trim() ?? string.Empty;
        TextEnabled = textEnabled;
        TextStart = textStart;
        TextEnd = textEnd;
        VfxEnabled = vfxEnabled;
        VfxStart = vfxStart;
        VfxEnd = vfxEnd;
        SubtitleEnabled = subtitleEnabled;
        SubtitleStart = subtitleStart;
        SubtitleEnd = subtitleEnd;
        ElementEnabled = elementEnabled;
        ElementStart = elementStart;
        ElementEnd = elementEnd;
        ExtraTextLayers = extraTextLayers ?? Array.Empty<ClipTextOverlay>();
        VideoCrf = Math.Clamp(videoCrf, 12, 35);
        FrameRate = frameRate is 30 or 60 ? frameRate : 60;
        VideoEncoder = FFmpegVideoEncoding.NormalizeCodec(videoEncoder);
        VideoBitrateMbps = Math.Clamp(videoBitrateMbps, 2, 120);
        if (outputWidth > 0 && outputHeight > 0)
        {
            OutputWidth = NormalizeEvenDimension(outputWidth, 320, 7680);
            OutputHeight = NormalizeEvenDimension(outputHeight, 180, 7680);
        }
        AudioBitrateKbps = Math.Clamp(audioBitrateKbps, 64, 320);
        ExportQuality = exportQuality?.Trim().ToLowerInvariant() switch
        {
            "draft" => "draft",
            "max" => "max",
            _ => "high"
        };
    }

    public double PlaybackSpeed { get; }
    public double AudioVolume { get; }
    public bool MuteAudio { get; }
    public string VideoFilter { get; }
    public string TextOverlay { get; }
    public string TextStyle { get; }
    public string TextAnimation { get; }
    public string TextFontFamily { get; }
    public string TextFontFilePath { get; }
    public string TextFontFamilyName { get; }
    public string TextColor { get; }
    public string TextPosition { get; }
    public double TextAnchorX { get; }
    public double TextAnchorY { get; }
    public double TextSize { get; }
    public double TextBoxWidth { get; }
    public double TextOpacity { get; }
    public IReadOnlyList<ClipTextKeyframe> TextKeyframes { get; }
    public string SubtitleStyle { get; }
    public string SubtitleFontFamily { get; }
    public string SubtitleFontFilePath { get; }
    public string SubtitleFontFamilyName { get; }
    public string SubtitleColor { get; }
    public double SubtitleAnchorX { get; }
    public double SubtitleAnchorY { get; }
    public double SubtitleSize { get; }
    public double SubtitleBoxWidth { get; }
    public string ElementOverlay { get; }
    public double FadeInSeconds { get; }
    public double FadeOutSeconds { get; }
    public double FreezeFrameSeconds { get; }
    public double VideoScale { get; }
    public double VideoOffsetX { get; }
    public double VideoOffsetY { get; }
    public bool FitToCanvas { get; }
    public string SubtitleText { get; }
    public bool TextEnabled { get; }
    public TimeSpan TextStart { get; }
    public TimeSpan TextEnd { get; }
    public bool VfxEnabled { get; }
    public TimeSpan VfxStart { get; }
    public TimeSpan VfxEnd { get; }
    public bool SubtitleEnabled { get; }
    public TimeSpan SubtitleStart { get; }
    public TimeSpan SubtitleEnd { get; }
    public bool ElementEnabled { get; }
    public TimeSpan ElementStart { get; }
    public TimeSpan ElementEnd { get; }
    public IReadOnlyList<ClipTextOverlay> ExtraTextLayers { get; }
    public int VideoCrf { get; }
    public int FrameRate { get; }
    public string VideoEncoder { get; }
    public int VideoBitrateMbps { get; }
    public string ExportQuality { get; }
    public int OutputWidth { get; }
    public int OutputHeight { get; }
    public int AudioBitrateKbps { get; }

    public bool RequiresReencode =>
        MuteAudio
        || Math.Abs(PlaybackSpeed - 1.0) > 0.001
        || Math.Abs(AudioVolume - 1.0) > 0.001
        || RequiresVideoFilter
        || RequiresAudioFilter;

    public bool RequiresVideoFilter =>
        FitToCanvas
        || RequiresOutputResize
        || RequiresVideoTransform
        || (VfxEnabled && !IsNone(VideoFilter))
        || (ElementEnabled && !IsNone(ElementOverlay))
        || (TextEnabled && !string.IsNullOrWhiteSpace(TextOverlay))
        || (SubtitleEnabled && !string.IsNullOrWhiteSpace(SubtitleText))
        || ExtraTextLayers.Count > 0
        || FadeInSeconds > 0.001
        || FadeOutSeconds > 0.001
        || FreezeFrameSeconds > 0.001;

    public bool RequiresVideoTransform =>
        Math.Abs(VideoScale - 1.0) > 0.001
        || Math.Abs(VideoOffsetX) > 0.001
        || Math.Abs(VideoOffsetY) > 0.001;

    public bool RequiresOutputResize => OutputWidth > 0 && OutputHeight > 0;

    public bool RequiresAudioFilter =>
        Math.Abs(PlaybackSpeed - 1.0) > 0.001
        || Math.Abs(AudioVolume - 1.0) > 0.001
        || FadeInSeconds > 0.001
        || FadeOutSeconds > 0.001
        || FreezeFrameSeconds > 0.001;

    private static string Normalize(string? value, string fallback)
    {
        return string.IsNullOrWhiteSpace(value)
            ? fallback
            : value.Trim().ToLowerInvariant();
    }

    private static int NormalizeEvenDimension(int value, int minimum, int maximum)
    {
        var normalized = Math.Clamp(value, minimum, maximum);
        return normalized - normalized % 2;
    }

    private static bool IsNone(string value)
    {
        return string.IsNullOrWhiteSpace(value)
            || string.Equals(value, "none", StringComparison.OrdinalIgnoreCase);
    }
}
