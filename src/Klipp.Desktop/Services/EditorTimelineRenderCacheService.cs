using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Klipp.Desktop.Models;

namespace Klipp.Desktop.Services;

public sealed record EditorRenderCacheSegment(
    double StartSeconds,
    double EndSeconds,
    string CacheKey,
    string CachePath)
{
    public double DurationSeconds => Math.Max(0.1, EndSeconds - StartSeconds);
}

public sealed record EditorRenderCacheEntry(string Path, bool CacheHit);

public sealed record EditorRenderCacheRun(int SegmentCount, int CacheHits, int CacheMisses)
{
    public static EditorRenderCacheRun Empty { get; } = new(0, 0, 0);
}

public sealed class EditorTimelineRenderCacheService
{
    private const string CacheFormatVersion = "timeline-render-v10";
    private const string WorkCategory = "Render cache";
    private const double SegmentDurationSeconds = 8;

    private static readonly JsonSerializerOptions FingerprintJsonOptions = new()
    {
        WriteIndented = false
    };

    private readonly MediaCacheGenerationGate _generationGate = new();
    private readonly MediaProbeService _mediaProbeService;
    private readonly Klipp.Core.Performance.PriorityBackgroundWorkQueue _workQueue;

    public EditorTimelineRenderCacheService(
        string? cacheDirectory = null,
        MediaProbeService? mediaProbeService = null,
        Klipp.Core.Performance.PriorityBackgroundWorkQueue? workQueue = null)
    {
        CacheDirectory = cacheDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Klipp",
            "render-cache");
        _mediaProbeService = mediaProbeService ?? new MediaProbeService();
        _workQueue = workQueue ?? MediaPreviewWorkQueue.Shared;
        Directory.CreateDirectory(CacheDirectory);
    }

    public string CacheDirectory { get; }

    public IReadOnlyList<EditorRenderCacheSegment> BuildSegments(
        EditorProject project,
        string videoEncoder)
    {
        ArgumentNullException.ThrowIfNull(project);
        project.Normalize();

        var duration = project.DurationSeconds;
        var segmentCount = Math.Max(1, (int)Math.Ceiling(duration / SegmentDurationSeconds));
        var result = new List<EditorRenderCacheSegment>(segmentCount);
        var primarySpans = BuildPrimarySpans(project);
        var fileSignatures = new Dictionary<string, SourceFileSignature>(StringComparer.OrdinalIgnoreCase);
        SourceFileSignature GetFileSignature(string? path)
        {
            var key = path ?? string.Empty;
            if (!fileSignatures.TryGetValue(key, out var signature))
            {
                signature = CreateFileSignature(path);
                fileSignatures.Add(key, signature);
            }
            return signature;
        }

        for (var index = 0; index < segmentCount; index++)
        {
            var start = index * SegmentDurationSeconds;
            var end = Math.Min(duration, start + SegmentDurationSeconds);
            var key = BuildCacheKey(project, videoEncoder, start, end, primarySpans, GetFileSignature);
            result.Add(new EditorRenderCacheSegment(
                start,
                end,
                key,
                Path.Combine(CacheDirectory, $"{key}.mp4")));
        }

        return result;
    }

    public bool TryGetExistingSegment(EditorRenderCacheSegment segment, out string path)
    {
        path = segment.CachePath;
        try
        {
            var exists = File.Exists(path) && new FileInfo(path).Length > 1024;
            if (exists)
                TryTouch(path);
            return exists;
        }
        catch
        {
            return false;
        }
    }

    public async Task<EditorRenderCacheEntry> GetOrCreateSegmentAsync(
        EditorProject project,
        EditorRenderCacheSegment segment,
        Func<string, IProgress<double>?, CancellationToken, Task> renderer,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(segment);
        ArgumentNullException.ThrowIfNull(renderer);
        cancellationToken.ThrowIfCancellationRequested();
        if (TryGetExistingSegment(segment, out var existingPath))
        {
            _workQueue.RecordCacheHit(WorkCategory);
            progress?.Report(1);
            return new EditorRenderCacheEntry(existingPath, true);
        }

        using var lease = await _generationGate.EnterAsync(segment.CachePath, cancellationToken)
            .ConfigureAwait(false);
        if (TryGetExistingSegment(segment, out existingPath))
        {
            _workQueue.RecordCacheHit(WorkCategory);
            progress?.Report(1);
            return new EditorRenderCacheEntry(existingPath, true);
        }

        _workQueue.RecordCacheMiss(WorkCategory);
        var temporaryPath = Path.Combine(
            CacheDirectory,
            $".{segment.CacheKey}-{Guid.NewGuid():N}.tmp.mp4");
        try
        {
            await renderer(temporaryPath, progress, cancellationToken).ConfigureAwait(false);
            await ValidateSegmentAsync(project, segment, temporaryPath, cancellationToken)
                .ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporaryPath, segment.CachePath, overwrite: true);
            TryTouch(segment.CachePath);
            progress?.Report(1);
            return new EditorRenderCacheEntry(segment.CachePath, false);
        }
        finally
        {
            try { File.Delete(temporaryPath); } catch { }
        }
    }

    private async Task ValidateSegmentAsync(
        EditorProject project,
        EditorRenderCacheSegment segment,
        string path,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(path) || new FileInfo(path).Length <= 1024)
            throw new InvalidDataException("Rendered timeline cache segment is empty.");

        var info = await _mediaProbeService.GetMediaInfoAsync(path, cancellationToken)
            .ConfigureAwait(false) ?? throw new InvalidDataException(
                "Rendered timeline cache segment could not be inspected.");
        var durationTolerance = Math.Max(0.35, 2d / project.Settings.ExportFrameRate);
        if (!info.IsMp4Container ||
            info.Width != project.CanvasWidth ||
            info.Height != project.CanvasHeight ||
            Math.Abs(info.Duration.TotalSeconds - segment.DurationSeconds) > durationTolerance)
        {
            throw new InvalidDataException(
                $"Rendered timeline cache segment is invalid ({info.Width}x{info.Height}, " +
                $"{info.Duration.TotalSeconds:0.###}s)." );
        }
    }

    private static string BuildCacheKey(
        EditorProject project,
        string videoEncoder,
        double startSeconds,
        double endSeconds,
        IReadOnlyList<PrimaryClipSpan> primarySpans,
        Func<string?, SourceFileSignature> getFileSignature)
    {
        var framePadding = 1d / Math.Max(1, project.Settings.ExportFrameRate);
        var primary = primarySpans
            .Where(span => Intersects(span.StartSeconds, span.EndSeconds, startSeconds, endSeconds, framePadding))
            .Select(span => new
            {
                span.StartSeconds,
                span.EndSeconds,
                Clip = span.Clip,
                SourceGradeBeforeTransform = project.EffectLayers.Any(layer => layer.TargetClipId == span.Clip.Id),
                TransitionStyle = project.GetTransitionStyle(span.Clip),
                TransitionDuration = project.GetTransitionDurationSeconds(span.Clip),
                Source = getFileSignature(span.Clip.SourcePath),
                Lut = getFileSignature(span.Clip.LutPath)
            })
            .ToList();
        var overlays = project.OverlayClips
            .Where(clip => Intersects(
                clip.TimelineStartSeconds,
                clip.TimelineStartSeconds + clip.EffectiveDurationSeconds,
                startSeconds,
                endSeconds,
                framePadding))
            .Select(clip => new
            {
                Clip = clip,
                SourceGradeBeforeTransform = project.EffectLayers.Any(layer => layer.TargetClipId == clip.Id),
                Source = getFileSignature(clip.SourcePath),
                Lut = getFileSignature(clip.LutPath)
            })
            .ToList();
        var graphics = project.GraphicLayers
            .Where(layer => Intersects(
                layer.StartSeconds,
                layer.EndSeconds,
                startSeconds,
                endSeconds,
                framePadding))
            .OrderBy(layer => layer.StartSeconds)
            .Select(layer => new
            {
                Layer = layer,
                Source = getFileSignature(layer.SourcePath)
            })
            .ToList();
        var audioTracks = project.AudioTracks
            .Where(track => Intersects(
                track.TimelineStartSeconds,
                track.TimelineStartSeconds + track.EffectiveDurationSeconds,
                startSeconds,
                endSeconds,
                framePadding))
            .OrderBy(track => track.TimelineStartSeconds)
            .Select(track => new
            {
                Track = track,
                Source = getFileSignature(track.SourcePath)
            })
            .ToList();

        var fingerprint = new
        {
            Version = CacheFormatVersion,
            StartSeconds = startSeconds,
            EndSeconds = endSeconds,
            project.CanvasWidth,
            project.CanvasHeight,
            FrameRate = project.Settings.ExportFrameRate,
            VideoEncoder = videoEncoder?.Trim().ToLowerInvariant() ?? "libx264",
            VideoBitrateMbps = project.Settings.ExportBitrateMbps,
            VideoQuality = project.Settings.ExportQuality,
            AudioBitrateKbps = project.Settings.ExportAudioBitrateKbps,
            AudioDuckingUsesMicrophone = project.AudioTracks.Any(track => track.SourceAudioRole == "microphone"),
            TransitionStyle = project.Settings.TransitionStyle,
            TransitionDuration = project.Settings.TransitionDurationSeconds,
            Tracks = project.Tracks.OrderBy(track => track.Kind).ThenBy(track => track.Order)
                .Select(track => new { track.Id, track.Kind, track.Order, track.Muted, track.Solo }),
            Primary = primary,
            Overlays = overlays,
            Graphics = graphics,
            EffectLayers = project.EffectLayers.Where(layer => Intersects(layer.StartSeconds, layer.EndSeconds,
                startSeconds, endSeconds, framePadding)).Select(layer => new
                {
                    Layer = layer,
                    Lut = getFileSignature(layer.Style?.LutPath)
                }).ToList(),
            AudioTracks = audioTracks
        };
        var json = JsonSerializer.Serialize(fingerprint, FingerprintJsonOptions);
        return Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(json)))
            .ToLowerInvariant();
    }

    private static IReadOnlyList<PrimaryClipSpan> BuildPrimarySpans(EditorProject project)
    {
        var clips = project.PrimaryClips;
        var spans = new List<PrimaryClipSpan>(clips.Count);
        for (var index = 0; index < clips.Count; index++)
        {
            var clip = clips[index];
            var start = clip.TimelineStartSeconds;
            var end = start + clip.EffectiveDurationSeconds;
            spans.Add(new PrimaryClipSpan(clip, start, end));
        }

        return spans;
    }

    private static bool Intersects(
        double itemStart,
        double itemEnd,
        double segmentStart,
        double segmentEnd,
        double padding) =>
        itemEnd > segmentStart - padding && itemStart < segmentEnd + padding;

    private static SourceFileSignature CreateFileSignature(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return new SourceFileSignature(string.Empty, -1, 0);
        try
        {
            var info = new FileInfo(Path.GetFullPath(path));
            return new SourceFileSignature(
                info.FullName.ToUpperInvariant(),
                info.Exists ? info.Length : -1,
                info.Exists ? info.LastWriteTimeUtc.Ticks : 0);
        }
        catch
        {
            return new SourceFileSignature(path.Trim().ToUpperInvariant(), -1, 0);
        }
    }

    private static void TryTouch(string path)
    {
        try { File.SetLastAccessTimeUtc(path, DateTime.UtcNow); } catch { }
    }

    private sealed record PrimaryClipSpan(
        EditorProjectClip Clip,
        double StartSeconds,
        double EndSeconds);

    private sealed record SourceFileSignature(string Path, long Length, long LastWriteTicks);
}
