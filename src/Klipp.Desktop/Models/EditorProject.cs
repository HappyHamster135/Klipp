using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Serialization;
using Klipp.Core.Models;

namespace Klipp.Desktop.Models;

public sealed class EditorProject
{
    public const int CurrentVersion = 34;
    public const string DefaultPrimaryVideoTrackId = "video-1";
    public const string DefaultOverlayVideoTrackId = "video-2";
    public const string DefaultAudioTrackId = "audio-1";

    public int Version { get; set; } = CurrentVersion;
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "Untitled";
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
    public int CanvasWidth { get; set; } = 1280;
    public int CanvasHeight { get; set; } = 720;
    public List<EditorProjectClip> Clips { get; set; } = [];
    public List<EditorProjectTextLayer> ExtraTextLayers { get; set; } = [];
    public List<EditorProjectTextLayer> SubtitleLayers { get; set; } = [];
    public List<EditorProjectGraphicLayer> GraphicLayers { get; set; } = [];
    public List<EditorProjectEffectLayer> EffectLayers { get; set; } = [];
    public List<EditorProjectAudioTrack> AudioTracks { get; set; } = [];
    public List<EditorProjectMarker> Markers { get; set; } = [];
    public List<EditorTimelineTrack> Tracks { get; set; } = [];
    public EditorProjectSettings Settings { get; set; } = new();

    public IReadOnlyList<EditorProjectClip> PrimaryClips =>
        Clips.Where(clip => clip.Track == EditorClipTrack.Primary)
            .OrderBy(clip => clip.TimelineStartSeconds)
            .ThenBy(clip => clip.Order)
            .ToList();

    public IReadOnlyList<EditorProjectClip> OverlayClips =>
        Clips.Where(clip => clip.Track == EditorClipTrack.Overlay)
            .OrderBy(clip => clip.TimelineStartSeconds)
            .ToList();

    public double DurationSeconds
    {
        get
        {
            var videoEnd = (Clips ?? [])
                .Select(clip => clip.TimelineStartSeconds + clip.EffectiveDurationSeconds)
                .DefaultIfEmpty(0)
                .Max();
            var audioEnd = (AudioTracks ?? [])
                .Select(track => track.TimelineStartSeconds + track.EffectiveDurationSeconds)
                .DefaultIfEmpty(0)
                .Max();
            return Math.Max(0.1, Math.Max(audioEnd, videoEnd));
        }
    }

    [JsonIgnore]
    public int OutputWidth => Settings.ExportResolutionMode == "canvas"
        ? CanvasWidth
        : Settings.ExportWidth;

    [JsonIgnore]
    public int OutputHeight => Settings.ExportResolutionMode == "canvas"
        ? CanvasHeight
        : Settings.ExportHeight;

    public double GetTransitionOverlapSeconds(EditorProjectClip left, EditorProjectClip right)
    {
        if (string.Equals(GetTransitionStyle(right), "cut", StringComparison.OrdinalIgnoreCase))
            return 0;

        var availableOverlap = left.TimelineStartSeconds + left.EffectiveDurationSeconds - right.TimelineStartSeconds;
        if (right.TimelineStartSeconds < left.TimelineStartSeconds || availableOverlap <= 0.00001)
            return 0;
        return Math.Min(availableOverlap, GetRequestedTransitionOverlapSeconds(left, right));
    }

    public EditorProjectClip? GetTransitionPredecessor(EditorProjectClip incomingClip, bool includeTouching = false) =>
        GetTrackClips(incomingClip.TrackId).TakeWhile(clip => !string.Equals(clip.Id, incomingClip.Id, StringComparison.Ordinal))
            .LastOrDefault(clip => clip.TimelineStartSeconds <= incomingClip.TimelineStartSeconds &&
                (clip.TimelineStartSeconds + clip.EffectiveDurationSeconds > incomingClip.TimelineStartSeconds + 0.00001 ||
                 (includeTouching && Math.Abs(clip.TimelineStartSeconds + clip.EffectiveDurationSeconds - incomingClip.TimelineStartSeconds) < 0.001)));

    private double GetRequestedTransitionOverlapSeconds(EditorProjectClip left, EditorProjectClip right) =>
        string.Equals(GetTransitionStyle(right), "cut", StringComparison.OrdinalIgnoreCase) ? 0 : Math.Clamp(
            GetTransitionDurationSeconds(right), 0,
            Math.Min(left.EffectiveDurationSeconds / 2, right.EffectiveDurationSeconds / 2));

    public string GetTransitionStyle(EditorProjectClip incomingClip) =>
        string.Equals(incomingClip.TransitionInStyle, "inherit", StringComparison.OrdinalIgnoreCase)
            ? Settings.TransitionStyle
            : incomingClip.TransitionInStyle;

    public double GetTransitionDurationSeconds(EditorProjectClip incomingClip) =>
        string.Equals(incomingClip.TransitionInStyle, "inherit", StringComparison.OrdinalIgnoreCase)
            ? Settings.TransitionDurationSeconds
            : incomingClip.TransitionInDurationSeconds;

    public void Normalize()
    {
        var migratePackedPrimaryClips = Version < 31;
        var migrateTrackLanes = Version < 32;
        Version = CurrentVersion;
        CanvasWidth = Math.Clamp(CanvasWidth, 320, 7680);
        CanvasHeight = Math.Clamp(CanvasHeight, 180, 4320);
        Clips ??= [];
        ExtraTextLayers ??= [];
        SubtitleLayers ??= [];
        GraphicLayers ??= [];
        EffectLayers ??= [];
        AudioTracks ??= [];
        Markers ??= [];
        Tracks ??= [];
        Settings ??= new EditorProjectSettings();
        Settings.TransitionStyle = Settings.TransitionStyle switch
        {
            "dissolve" or "fadeblack" or "fadewhite" or "wipeleft" or "wiperight" or "wipeup" or "wipedown" or
            "slideleft" or "slideright" or "slideup" or "slidedown" or
            "circleopen" or "rectcrop" or "horzopen" or "vertopen" or "diagtl" => Settings.TransitionStyle,
            _ => "cut"
        };
        Settings.TransitionDurationSeconds = Math.Clamp(Settings.TransitionDurationSeconds, 0.1, 2);

        // Version 30 and earlier stored primary placement as an ordered sequence.
        // Materialize that sequence once; all subsequent edits own their positions.
        foreach (var clip in Clips.ToList())
        {
            clip.Normalize();
            if (string.IsNullOrWhiteSpace(clip.SourcePath))
                Clips.Remove(clip);
        }
        if (migratePackedPrimaryClips)
        {
            var packed = Clips.Where(clip => clip.Track == EditorClipTrack.Primary).OrderBy(clip => clip.Order).ToList();
            var end = 0d;
            for (var index = 0; index < packed.Count; index++)
            {
                var overlap = index == 0 ? 0 : GetRequestedTransitionOverlapSeconds(packed[index - 1], packed[index]);
                packed[index].TimelineStartSeconds = Math.Max(0, end - overlap);
                end = packed[index].TimelineStartSeconds + packed[index].EffectiveDurationSeconds;
            }
        }
        foreach (var track in AudioTracks.ToList())
        {
            track.Normalize();
            if (string.IsNullOrWhiteSpace(track.SourcePath))
                AudioTracks.Remove(track);
        }
        NormalizeTracks(migrateTrackLanes);
        var effectsById = new HashSet<string>(StringComparer.Ordinal);
        foreach (var effect in EffectLayers.ToArray())
        {
            var target = Clips.FirstOrDefault(clip => clip.Id == effect.TargetClipId);
            if (target is null)
            {
                EffectLayers.Remove(effect);
                continue;
            }
            effect.Normalize(target, DurationSeconds);
            if (effect.Style is null && effect.Filter == "none" || effect.EndSeconds <= effect.StartSeconds)
                EffectLayers.Remove(effect);
            else if (!effectsById.Add(effect.Id))
            {
                effect.Id = Guid.NewGuid().ToString("N");
                effectsById.Add(effect.Id);
            }
        }
        foreach (var lane in Clips.GroupBy(clip => clip.TrackId))
        {
            var order = 0;
            foreach (var clip in lane.OrderBy(clip => clip.TimelineStartSeconds).ThenBy(clip => clip.Order))
                clip.Order = order++;
        }
        Settings.ExportQuality = Settings.ExportQuality switch
        {
            "draft" or "max" => Settings.ExportQuality,
            _ => "high"
        };
        Settings.ExportFrameRate = Settings.ExportFrameRate is 30 or 60 ? Settings.ExportFrameRate : 60;
        Settings.ExportEncoder = Settings.ExportEncoder?.Trim().ToLowerInvariant() switch
        {
            "software" => "software",
            "nvenc" => "nvenc",
            "qsv" => "qsv",
            "amf" => "amf",
            _ => "auto"
        };
        Settings.ExportBitrateMbps = Math.Clamp(Settings.ExportBitrateMbps, 2, 120);
        var exportResolutionMode = Settings.ExportResolutionMode?.Trim().ToLowerInvariant();
        Settings.ExportResolutionMode = exportResolutionMode switch
        {
            "720p" or "1080p" or "1440p" or "2160p" or "custom" => exportResolutionMode,
            _ => "canvas"
        };
        Settings.ExportWidth = NormalizeEvenDimension(Settings.ExportWidth, 320, 7680);
        Settings.ExportHeight = NormalizeEvenDimension(Settings.ExportHeight, 180, 7680);
        Settings.ExportAudioBitrateKbps = Math.Clamp(Settings.ExportAudioBitrateKbps, 64, 320);
        Settings.PreviewQuality = PreviewProxyQuality.Normalize(Settings.PreviewQuality);
        if (Settings.ExportResolutionMode == "canvas")
        {
            Settings.ExportWidth = CanvasWidth;
            Settings.ExportHeight = CanvasHeight;
        }

        Settings.TextFontFamily = EditorTextTypography.NormalizeFontFamily(Settings.TextFontFamily);
        Settings.TextFontFilePath = EditorTextTypography.NormalizeFontFilePath(Settings.TextFontFilePath);
        Settings.TextFontFamilyName = EditorTextTypography.NormalizeCustomFontFamilyName(Settings.TextFontFamilyName);
        Settings.TextColor = EditorTextTypography.NormalizeColor(Settings.TextColor);
        Settings.SubtitleFontFamily = EditorTextTypography.NormalizeFontFamily(Settings.SubtitleFontFamily);
        Settings.SubtitleFontFilePath = EditorTextTypography.NormalizeFontFilePath(Settings.SubtitleFontFilePath);
        Settings.SubtitleFontFamilyName = EditorTextTypography.NormalizeCustomFontFamilyName(Settings.SubtitleFontFamilyName);
        Settings.SubtitleColor = EditorTextTypography.NormalizeColor(Settings.SubtitleColor);
        Settings.TextAnchorX = Math.Clamp(Settings.TextAnchorX, 0, 1);
        Settings.TextAnchorY = Math.Clamp(Settings.TextAnchorY, 0, 1);
        Settings.TextSize = Math.Clamp(Settings.TextSize, 12, 220);
        Settings.TextBoxWidth = Math.Clamp(Settings.TextBoxWidth, 96, 1280);
        Settings.TextOpacity = Math.Clamp(Settings.TextOpacity, 0, 1);
        Settings.TextStartSeconds = Math.Clamp(Settings.TextStartSeconds, 0, Math.Max(0, DurationSeconds - 0.1));
        Settings.TextEndSeconds = Math.Clamp(
            Settings.TextEndSeconds,
            Settings.TextStartSeconds + 0.1,
            Math.Max(0.1, DurationSeconds));
        Settings.TextKeyframes ??= [];
        foreach (var keyframe in Settings.TextKeyframes)
            keyframe.Normalize(Settings.TextStartSeconds, Settings.TextEndSeconds);
        Settings.TextKeyframes = Settings.TextKeyframes
            .OrderBy(keyframe => keyframe.TimeSeconds)
            .GroupBy(keyframe => Math.Round(keyframe.TimeSeconds * 120) / 120)
            .Select(group => group.Last())
            .ToList();

        foreach (var layer in ExtraTextLayers.ToList())
        {
            layer.Normalize(DurationSeconds);
            if (string.IsNullOrWhiteSpace(layer.Text))
                ExtraTextLayers.Remove(layer);
        }

        foreach (var layer in SubtitleLayers.ToList())
        {
            layer.Normalize(DurationSeconds);
            layer.Animation = "none";
            if (string.IsNullOrWhiteSpace(layer.Text))
                SubtitleLayers.Remove(layer);
        }

        foreach (var layer in GraphicLayers.ToList())
        {
            layer.Normalize(DurationSeconds, CanvasWidth, CanvasHeight);
            if (string.IsNullOrWhiteSpace(layer.SourcePath))
                GraphicLayers.Remove(layer);
        }

        foreach (var marker in Markers)
            marker.Normalize(DurationSeconds);
        Markers = Markers
            .OrderBy(marker => marker.TimeSeconds)
            .GroupBy(marker => marker.Id, StringComparer.Ordinal)
            .Select(group => group.Last())
            .ToList();
    }

    public EditorProjectClip? FindClip(string? id)
    {
        return string.IsNullOrWhiteSpace(id)
            ? null
            : Clips.FirstOrDefault(clip => string.Equals(clip.Id, id, StringComparison.Ordinal));
    }

    public EditorTimelineTrack? GetTrack(string? trackId) =>
        Tracks?.FirstOrDefault(track => string.Equals(track.Id, trackId, StringComparison.Ordinal));

    public IReadOnlyList<EditorProjectClip> GetTrackClips(string? trackId) =>
        Clips.Where(clip => string.Equals(clip.TrackId, trackId, StringComparison.Ordinal))
            .OrderBy(clip => clip.TimelineStartSeconds).ThenBy(clip => clip.Order).ToList();

    public bool IsTrackActive(string? trackId)
    {
        var track = GetTrack(trackId);
        return track is null || (!track.Muted &&
            (!Tracks.Any(candidate => candidate.Kind == track.Kind && candidate.Solo) || track.Solo));
    }

    private void NormalizeTracks(bool migrateLanes)
    {
        foreach (var track in Tracks)
            track.Normalize();
        Tracks = Tracks.GroupBy(track => track.Id, StringComparer.Ordinal).Select(group => group.First()).ToList();

        EditorTimelineTrack Ensure(string id, EditorTimelineTrackKind kind, string name, int order)
        {
            var existing = GetTrack(id);
            if (existing is not null && existing.Kind == kind)
                return existing;
            var track = new EditorTimelineTrack { Id = id, Kind = kind, Name = name, Order = order };
            if (existing is not null)
                track.Id = Guid.NewGuid().ToString("N");
            Tracks.Add(track);
            return track;
        }

        var video = Tracks.Where(track => track.Kind == EditorTimelineTrackKind.Video).OrderBy(track => track.Order).ToList();
        if (video.Count == 0)
            video.Add(Ensure(DefaultPrimaryVideoTrackId, EditorTimelineTrackKind.Video, "V1", 0));
        if (video.Count == 1)
            video.Add(Ensure(video[0].Id == DefaultOverlayVideoTrackId ? DefaultPrimaryVideoTrackId : DefaultOverlayVideoTrackId,
                EditorTimelineTrackKind.Video, "V2", video[0].Order + 1));
        var audio = Tracks.Where(track => track.Kind == EditorTimelineTrackKind.Audio).OrderBy(track => track.Order).ToList();
        if (audio.Count == 0)
            audio.Add(Ensure(DefaultAudioTrackId, EditorTimelineTrackKind.Audio, "A1", 0));

        if (migrateLanes)
        {
            foreach (var clip in Clips.Where(clip => clip.Track == EditorClipTrack.Primary))
                clip.TrackId = video[0].Id;
            var overlayEnds = new List<double> { 0 };
            foreach (var clip in Clips.Where(clip => clip.Track == EditorClipTrack.Overlay)
                         .OrderBy(clip => clip.TimelineStartSeconds).ThenBy(clip => clip.Order))
            {
                // Reuse only lanes above every still-active legacy overlay so
                // migration preserves the previous later-start stacking order.
                var highestActive = overlayEnds.FindLastIndex(end => end > clip.TimelineStartSeconds + 0.00001);
                var lane = highestActive + 1;
                while (lane >= overlayEnds.Count)
                    overlayEnds.Add(0);
                while (lane + 1 >= video.Count)
                    video.Add(Ensure($"video-{video.Count + 1}", EditorTimelineTrackKind.Video, $"V{video.Count + 1}", video.Count));
                clip.TrackId = video[lane + 1].Id;
                overlayEnds[lane] = clip.TimelineStartSeconds + clip.EffectiveDurationSeconds;
            }
            var audioEnds = new List<double> { 0 };
            foreach (var track in AudioTracks.OrderBy(track => track.TimelineStartSeconds))
            {
                var lane = audioEnds.FindIndex(end => end <= track.TimelineStartSeconds + 0.00001);
                if (lane < 0)
                {
                    lane = audioEnds.Count;
                    audioEnds.Add(0);
                }
                while (lane >= audio.Count)
                    audio.Add(Ensure($"audio-{audio.Count + 1}", EditorTimelineTrackKind.Audio, $"A{audio.Count + 1}", audio.Count));
                track.TrackId = audio[lane].Id;
                audioEnds[lane] = track.TimelineStartSeconds + track.EffectiveDurationSeconds;
            }
        }

        EditorTimelineTrack Resolve(string? id, EditorTimelineTrackKind kind, EditorTimelineTrack fallback)
        {
            if (string.IsNullOrWhiteSpace(id))
                return fallback;
            if (GetTrack(id) is { } existing)
                return existing.Kind == kind ? existing : fallback;
            var peers = Tracks.Where(track => track.Kind == kind).ToList();
            return Ensure(id, kind, $"{(kind == EditorTimelineTrackKind.Video ? "V" : "A")}{peers.Count + 1}",
                peers.Select(track => track.Order).DefaultIfEmpty(-1).Max() + 1);
        }
        foreach (var clip in Clips)
            clip.TrackId = Resolve(clip.TrackId, EditorTimelineTrackKind.Video,
                clip.Track == EditorClipTrack.Primary ? video[0] : video[1]).Id;
        foreach (var track in AudioTracks)
            track.TrackId = Resolve(track.TrackId, EditorTimelineTrackKind.Audio, audio[0]).Id;

        foreach (var kind in Enum.GetValues<EditorTimelineTrackKind>())
        {
            var order = 0;
            foreach (var track in Tracks.Where(track => track.Kind == kind).OrderBy(track => track.Order))
                track.Order = order++;
        }
        var primaryId = Tracks.Where(track => track.Kind == EditorTimelineTrackKind.Video).MinBy(track => track.Order)!.Id;
        foreach (var clip in Clips)
            clip.Track = clip.TrackId == primaryId ? EditorClipTrack.Primary : EditorClipTrack.Overlay;
    }

    private static int NormalizeEvenDimension(int value, int minimum, int maximum)
    {
        var normalized = Math.Clamp(value, minimum, maximum);
        return normalized - normalized % 2;
    }
}

public enum EditorTimelineTrackKind { Video, Audio }

public sealed class EditorTimelineTrack
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "Track";
    public EditorTimelineTrackKind Kind { get; set; }
    public int Order { get; set; }
    public double Height { get; set; } = 44;
    public bool Locked { get; set; }
    public bool SyncLocked { get; set; } = true;
    public bool Muted { get; set; }
    public bool Solo { get; set; }

    public void Normalize()
    {
        Id = string.IsNullOrWhiteSpace(Id) ? Guid.NewGuid().ToString("N") : Id.Trim();
        Name = string.IsNullOrWhiteSpace(Name) ? (Kind == EditorTimelineTrackKind.Audio ? "Audio" : "Video") : Name.Trim();
        if (Name.Length > 80) Name = Name[..80];
        Kind = Enum.IsDefined(Kind) ? Kind : EditorTimelineTrackKind.Video;
        Order = Math.Max(0, Order);
        Height = double.IsFinite(Height) ? Math.Clamp(Height, 32, 240) : 44;
    }

    public EditorTimelineTrack Clone() => new()
    {
        Id = Id, Name = Name, Kind = Kind, Order = Order, Height = Height,
        Locked = Locked, SyncLocked = SyncLocked, Muted = Muted, Solo = Solo
    };
}

public sealed class EditorProjectClipEffect
{
    public static IReadOnlyList<string> DefaultOrder { get; } =
        ["look", "shadows", "curve", "color", "temperature", "hue", "sharpen", "blur", "vignette", "lut"];

    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Kind { get; set; } = "look";
    public bool Enabled { get; set; } = true;

    public static List<EditorProjectClipEffect> CreateDefault() =>
        DefaultOrder.Select(kind => new EditorProjectClipEffect { Kind = kind }).ToList();

    public EditorProjectClipEffect Clone() => new() { Id = Id, Kind = Kind, Enabled = Enabled };
}

public sealed class EditorProjectVideoProcessingState
{
    public string OriginalSourcePath { get; set; } = string.Empty;
    public double OriginalSourceDurationSeconds { get; set; }
    public double OriginalTrimStartSeconds { get; set; }
    public double OriginalTrimEndSeconds { get; set; }
    public double OriginalPlaybackSpeed { get; set; } = 1;
    public string BakedSourcePath { get; set; } = string.Empty;
    public bool IsApplied { get; set; }
    public bool RampEnabled { get; set; }
    public double RampStartSpeed { get; set; } = 1;
    public double RampMiddleSpeed { get; set; } = 1;
    public double RampEndSpeed { get; set; } = 1;
    public double StabilizationStrength { get; set; }

    public EditorProjectVideoProcessingState Clone() => (EditorProjectVideoProcessingState)MemberwiseClone();
}

public sealed class EditorProjectMarker
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public double TimeSeconds { get; set; }
    public string Label { get; set; } = "Marker";
    public string Notes { get; set; } = string.Empty;
    public string ColorLabel { get; set; } = "yellow";
    public string Kind { get; set; } = "manual";

    [JsonIgnore]
    public string TimeLabel => TimeSpan.FromSeconds(Math.Max(0, TimeSeconds)).TotalHours >= 1
        ? TimeSpan.FromSeconds(Math.Max(0, TimeSeconds)).ToString(@"h\:mm\:ss")
        : TimeSpan.FromSeconds(Math.Max(0, TimeSeconds)).ToString(@"m\:ss\.f");

    public void Normalize(double projectDuration)
    {
        Id = string.IsNullOrWhiteSpace(Id) ? Guid.NewGuid().ToString("N") : Id;
        TimeSeconds = Math.Clamp(TimeSeconds, 0, Math.Max(0, projectDuration));
        Label = NormalizeText(Label, "Marker", 80);
        Notes = NormalizeText(Notes, string.Empty, 4000);
        ColorLabel = EditorProjectColorLabels.Normalize(ColorLabel, "yellow");
        Kind = string.Equals(Kind, "beat", StringComparison.OrdinalIgnoreCase)
            ? "beat"
            : "manual";
    }

    private static string NormalizeText(string? value, string fallback, int maxLength)
    {
        var normalized = value?.Trim() ?? string.Empty;
        if (normalized.Length == 0)
            return fallback;
        return normalized.Length <= maxLength ? normalized : normalized[..maxLength];
    }
}

public static class EditorProjectColorLabels
{
    public static string Normalize(string? value, string fallback = "none")
    {
        var normalized = value?.Trim().ToLowerInvariant();
        return normalized switch
        {
            "none" or "red" or "orange" or "yellow" or "green" or "teal" or "blue" or "purple" => normalized,
            _ => fallback
        };
    }
}

public sealed class EditorProjectGraphicLayer
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "Graphic";
    public string SourcePath { get; set; } = string.Empty;
    public double AnchorX { get; set; } = 0.5;
    public double AnchorY { get; set; } = 0.5;
    public double Width { get; set; } = 320;
    public double Height { get; set; } = 180;
    public double Opacity { get; set; } = 1;
    public string BlendMode { get; set; } = "normal";
    public string MaskShape { get; set; } = "none";
    public double MaskRoundness { get; set; } = 0.2;
    public double StartSeconds { get; set; }
    public double EndSeconds { get; set; } = 5;
    public List<EditorProjectGraphicKeyframe> Keyframes { get; set; } = [];

    [JsonIgnore]
    public string RangeLabel => $"{StartSeconds:0.0}s - {EndSeconds:0.0}s";

    [JsonIgnore]
    public bool IsMissing => !string.IsNullOrWhiteSpace(SourcePath) && !File.Exists(SourcePath);

    public void Normalize(double projectDuration, int canvasWidth, int canvasHeight)
    {
        Id = string.IsNullOrWhiteSpace(Id) ? Guid.NewGuid().ToString("N") : Id;
        Name = string.IsNullOrWhiteSpace(Name) ? Path.GetFileNameWithoutExtension(SourcePath) : Name.Trim();
        AnchorX = Math.Clamp(AnchorX, 0, 1);
        AnchorY = Math.Clamp(AnchorY, 0, 1);
        Width = Math.Clamp(Width, 16, Math.Max(16, canvasWidth * 2));
        Height = Math.Clamp(Height, 16, Math.Max(16, canvasHeight * 2));
        Opacity = Math.Clamp(Opacity, 0, 1);
        BlendMode = BlendMode switch
        {
            "multiply" or "screen" or "overlay" or "softlight" or "difference" => BlendMode,
            _ => "normal"
        };
        MaskShape = MaskShape switch
        {
            "rounded" or "ellipse" => MaskShape,
            _ => "none"
        };
        MaskRoundness = Math.Clamp(MaskRoundness, 0, 0.5);
        StartSeconds = Math.Clamp(StartSeconds, 0, Math.Max(0, projectDuration - 0.1));
        EndSeconds = Math.Clamp(EndSeconds, StartSeconds + 0.1, Math.Max(0.1, projectDuration));
        Keyframes ??= [];
        foreach (var keyframe in Keyframes)
            keyframe.Normalize(projectDuration, canvasWidth, canvasHeight);

        Keyframes = Keyframes
            .OrderBy(keyframe => keyframe.TimeSeconds)
            .GroupBy(keyframe => Math.Round(keyframe.TimeSeconds * 120) / 120)
            .Select(group => group.Last())
            .ToList();
    }

    public EditorProjectGraphicState Evaluate(double timelineSeconds)
    {
        if (Keyframes is null || Keyframes.Count == 0)
            return new EditorProjectGraphicState(AnchorX, AnchorY, Width, Height, Opacity);

        var ordered = Keyframes.OrderBy(keyframe => keyframe.TimeSeconds).ToList();
        if (timelineSeconds <= ordered[0].TimeSeconds)
            return ordered[0].ToState();
        if (timelineSeconds >= ordered[^1].TimeSeconds)
            return ordered[^1].ToState();

        for (var index = 1; index < ordered.Count; index++)
        {
            var right = ordered[index];
            if (timelineSeconds > right.TimeSeconds)
                continue;

            var left = ordered[index - 1];
            var duration = Math.Max(0.0001, right.TimeSeconds - left.TimeSeconds);
            var progress = Math.Clamp((timelineSeconds - left.TimeSeconds) / duration, 0, 1);
            progress = EditorProjectGraphicKeyframe.ApplyEasing(progress, right.Easing);
            return EditorProjectGraphicState.Lerp(left.ToState(), right.ToState(), progress);
        }

        return ordered[^1].ToState();
    }
}

public readonly record struct EditorProjectGraphicState(
    double AnchorX,
    double AnchorY,
    double Width,
    double Height,
    double Opacity)
{
    public static EditorProjectGraphicState Lerp(
        EditorProjectGraphicState from,
        EditorProjectGraphicState to,
        double progress)
    {
        progress = Math.Clamp(progress, 0, 1);
        return new EditorProjectGraphicState(
            from.AnchorX + (to.AnchorX - from.AnchorX) * progress,
            from.AnchorY + (to.AnchorY - from.AnchorY) * progress,
            from.Width + (to.Width - from.Width) * progress,
            from.Height + (to.Height - from.Height) * progress,
            from.Opacity + (to.Opacity - from.Opacity) * progress);
    }
}

public sealed class EditorProjectGraphicKeyframe
{
    public double TimeSeconds { get; set; }
    public double AnchorX { get; set; } = 0.5;
    public double AnchorY { get; set; } = 0.5;
    public double Width { get; set; } = 320;
    public double Height { get; set; } = 180;
    public double Opacity { get; set; } = 1;
    public string Easing { get; set; } = "linear";

    public EditorProjectGraphicState ToState() =>
        new(AnchorX, AnchorY, Width, Height, Opacity);

    public void Normalize(double projectDuration, int canvasWidth, int canvasHeight)
    {
        TimeSeconds = Math.Clamp(TimeSeconds, 0, Math.Max(0, projectDuration));
        AnchorX = Math.Clamp(AnchorX, 0, 1);
        AnchorY = Math.Clamp(AnchorY, 0, 1);
        Width = Math.Clamp(Width, 16, Math.Max(16, canvasWidth * 2));
        Height = Math.Clamp(Height, 16, Math.Max(16, canvasHeight * 2));
        Opacity = Math.Clamp(Opacity, 0, 1);
        Easing = Easing switch
        {
            "ease-in" or "ease-out" or "ease-in-out" or "hold" => Easing,
            _ => "linear"
        };
    }

    public static double ApplyEasing(double progress, string? easing)
    {
        progress = Math.Clamp(progress, 0, 1);
        return easing switch
        {
            "ease-in" => progress * progress,
            "ease-out" => 1 - Math.Pow(1 - progress, 2),
            "ease-in-out" => progress < 0.5
                ? 2 * progress * progress
                : 1 - Math.Pow(-2 * progress + 2, 2) / 2,
            "hold" => progress >= 1 ? 1 : 0,
            _ => progress
        };
    }

}

public sealed class EditorProjectAudioTrack
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "Audio";
    public string SourcePath { get; set; } = string.Empty;
    public string SourceClipId { get; set; } = string.Empty;
    public int SourceAudioStreamIndex { get; set; }
    public string SourceAudioRole { get; set; } = string.Empty;
    public string TrackId { get; set; } = string.Empty;
    public string GroupId { get; set; } = string.Empty;
    public double SourceDurationSeconds { get; set; } = 1;
    public double PlaybackSpeed { get; set; } = 1;
    public double TrimStartSeconds { get; set; }
    public double TrimEndSeconds { get; set; } = 1;
    public double TimelineStartSeconds { get; set; }
    public double Volume { get; set; } = 1;
    public List<EditorProjectClipVolumeKeyframe> VolumeKeyframes { get; set; } = [];
    public bool Muted { get; set; }
    public bool NormalizeAudio { get; set; }
    public double NormalizationGainDb { get; set; }
    public double? IntegratedLoudnessLufs { get; set; }
    public double LoudnessAnalysisTrimStartSeconds { get; set; }
    public double LoudnessAnalysisTrimEndSeconds { get; set; }
    public bool DuckingEnabled { get; set; }
    public double DuckingStrength { get; set; } = 0.65;
    public bool NoiseReductionEnabled { get; set; }
    public double NoiseReductionStrength { get; set; } = 0.45;
    public double FadeInSeconds { get; set; }
    public double FadeOutSeconds { get; set; }

    [JsonIgnore]
    public double EffectiveDurationSeconds => Math.Max(0.1, TrimEndSeconds - TrimStartSeconds) / Math.Max(0.25, PlaybackSpeed);

    [JsonIgnore]
    public string RangeLabel => $"{TimelineStartSeconds:0.0}s · {EffectiveDurationSeconds:0.0}s";

    [JsonIgnore]
    public bool IsMissing => !string.IsNullOrWhiteSpace(SourcePath) && !File.Exists(SourcePath);

    public double EvaluateVolume(double clipTimelineSeconds)
    {
        if (VolumeKeyframes is null || VolumeKeyframes.Count == 0)
            return Volume;
        var ordered = VolumeKeyframes.OrderBy(keyframe => keyframe.TimeSeconds).ToList();
        if (clipTimelineSeconds <= ordered[0].TimeSeconds)
            return ordered[0].Volume;
        if (clipTimelineSeconds >= ordered[^1].TimeSeconds)
            return ordered[^1].Volume;
        for (var index = 1; index < ordered.Count; index++)
        {
            var right = ordered[index];
            if (clipTimelineSeconds > right.TimeSeconds)
                continue;
            var left = ordered[index - 1];
            var progress = Math.Clamp((clipTimelineSeconds - left.TimeSeconds) /
                Math.Max(0.0001, right.TimeSeconds - left.TimeSeconds), 0, 1);
            progress = EditorProjectGraphicKeyframe.ApplyEasing(progress, right.Easing);
            return Math.Clamp(left.Volume + (right.Volume - left.Volume) * progress, 0, 2);
        }
        return ordered[^1].Volume;
    }

    public void Normalize()
    {
        Id = string.IsNullOrWhiteSpace(Id) ? Guid.NewGuid().ToString("N") : Id;
        SourceClipId = SourceClipId?.Trim() ?? string.Empty;
        SourceAudioStreamIndex = Math.Max(0, SourceAudioStreamIndex);
        SourceAudioRole = SourceAudioRole?.Trim().ToLowerInvariant() switch
        { "microphone" => "microphone", "system" => "system", "discord" => "discord", "music" => "music", _ => string.Empty };
        TrackId = TrackId?.Trim() ?? string.Empty;
        GroupId = GroupId?.Trim() ?? string.Empty;
        Name = string.IsNullOrWhiteSpace(Name) ? Path.GetFileNameWithoutExtension(SourcePath) : Name.Trim();
        SourceDurationSeconds = Math.Max(0.1, SourceDurationSeconds);
        PlaybackSpeed = double.IsFinite(PlaybackSpeed) ? Math.Clamp(PlaybackSpeed, 0.25, 4) : 1;
        TrimStartSeconds = Math.Clamp(TrimStartSeconds, 0, Math.Max(0, SourceDurationSeconds - 0.1));
        TrimEndSeconds = Math.Clamp(TrimEndSeconds, TrimStartSeconds + 0.1, SourceDurationSeconds);
        TimelineStartSeconds = Math.Max(0, TimelineStartSeconds);
        Volume = Math.Clamp(Volume, 0, 2);
        NormalizationGainDb = Math.Clamp(
            double.IsFinite(NormalizationGainDb) ? NormalizationGainDb : 0,
            -24,
            12);
        if (IntegratedLoudnessLufs is { } trackLufs && !double.IsFinite(trackLufs))
            IntegratedLoudnessLufs = null;
        if (IntegratedLoudnessLufs is not null &&
            (Math.Abs(LoudnessAnalysisTrimStartSeconds - TrimStartSeconds) > 0.001 ||
             Math.Abs(LoudnessAnalysisTrimEndSeconds - TrimEndSeconds) > 0.001))
        {
            NormalizeAudio = false;
            NormalizationGainDb = 0;
            IntegratedLoudnessLufs = null;
        }
        DuckingStrength = Math.Clamp(
            double.IsFinite(DuckingStrength) ? DuckingStrength : 0.65,
            0,
            1);
        NoiseReductionStrength = Math.Clamp(
            double.IsFinite(NoiseReductionStrength) ? NoiseReductionStrength : 0.45,
            0,
            1);
        FadeInSeconds = Math.Clamp(FadeInSeconds, 0, EffectiveDurationSeconds / 2);
        FadeOutSeconds = Math.Clamp(FadeOutSeconds, 0, EffectiveDurationSeconds / 2);
        VolumeKeyframes ??= [];
        foreach (var keyframe in VolumeKeyframes)
            keyframe.Normalize(EffectiveDurationSeconds);
        VolumeKeyframes = VolumeKeyframes.OrderBy(keyframe => keyframe.TimeSeconds)
            .GroupBy(keyframe => Math.Round(keyframe.TimeSeconds * 120) / 120)
            .Select(group => group.Last()).ToList();
    }
}

public sealed class EditorProjectTextLayer
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "Text overlay";
    public string Text { get; set; } = string.Empty;
    public string Style { get; set; } = "plain";
    public string Animation { get; set; } = "none";
    public string Origin { get; set; } = "manual";
    public string SourceClipId { get; set; } = string.Empty;
    public string FontFamily { get; set; } = EditorTextTypography.Automatic;
    public string FontFilePath { get; set; } = string.Empty;
    public string FontFamilyName { get; set; } = string.Empty;
    public string Color { get; set; } = EditorTextTypography.Automatic;
    public double AnchorX { get; set; } = 0.5;
    public double AnchorY { get; set; } = 0.84;
    public double Size { get; set; } = 64;
    public double BoxWidth { get; set; } = 420;
    public double Opacity { get; set; } = 1;
    public double StartSeconds { get; set; }
    public double EndSeconds { get; set; } = 5;
    public List<EditorProjectTextKeyframe> Keyframes { get; set; } = [];

    [JsonIgnore]
    public string RangeLabel => $"{StartSeconds:0.0}s - {EndSeconds:0.0}s";

    public void Normalize(double projectDuration)
    {
        Id = string.IsNullOrWhiteSpace(Id) ? Guid.NewGuid().ToString("N") : Id;
        Text = Text?.Trim() ?? string.Empty;
        Name = string.IsNullOrWhiteSpace(Name)
            ? (Text.Length > 24 ? $"{Text[..24]}..." : Text)
            : Name.Trim();
        Style = string.IsNullOrWhiteSpace(Style) ? "plain" : Style.Trim().ToLowerInvariant();
        Animation = string.IsNullOrWhiteSpace(Animation) ? "none" : Animation.Trim().ToLowerInvariant();
        Origin = string.Equals(Origin, "transcription", StringComparison.OrdinalIgnoreCase)
            ? "transcription"
            : "manual";
        SourceClipId = Origin == "transcription" ? SourceClipId?.Trim() ?? string.Empty : string.Empty;
        FontFamily = EditorTextTypography.NormalizeFontFamily(FontFamily);
        FontFilePath = EditorTextTypography.NormalizeFontFilePath(FontFilePath);
        FontFamilyName = EditorTextTypography.NormalizeCustomFontFamilyName(FontFamilyName);
        Color = EditorTextTypography.NormalizeColor(Color);
        AnchorX = Math.Clamp(AnchorX, 0, 1);
        AnchorY = Math.Clamp(AnchorY, 0, 1);
        Size = Math.Clamp(Size, 12, 220);
        BoxWidth = Math.Clamp(BoxWidth, 96, 1280);
        Opacity = Math.Clamp(Opacity, 0, 1);
        StartSeconds = Math.Clamp(StartSeconds, 0, Math.Max(0, projectDuration - 0.1));
        EndSeconds = Math.Clamp(EndSeconds, StartSeconds + 0.1, Math.Max(0.1, projectDuration));
        Keyframes ??= [];
        foreach (var keyframe in Keyframes)
            keyframe.Normalize(StartSeconds, EndSeconds);
        Keyframes = Keyframes
            .OrderBy(keyframe => keyframe.TimeSeconds)
            .GroupBy(keyframe => Math.Round(keyframe.TimeSeconds * 120) / 120)
            .Select(group => group.Last())
            .ToList();
    }

    public EditorProjectTextState Evaluate(double timelineSeconds)
    {
        return EditorProjectTextKeyframe.Evaluate(
            Keyframes,
            new EditorProjectTextState(AnchorX, AnchorY, Size, Opacity),
            timelineSeconds);
    }
}

public readonly record struct EditorProjectTextState(
    double AnchorX,
    double AnchorY,
    double Size,
    double Opacity)
{
    public static EditorProjectTextState Lerp(
        EditorProjectTextState from,
        EditorProjectTextState to,
        double progress)
    {
        progress = Math.Clamp(progress, 0, 1);
        return new EditorProjectTextState(
            from.AnchorX + (to.AnchorX - from.AnchorX) * progress,
            from.AnchorY + (to.AnchorY - from.AnchorY) * progress,
            from.Size + (to.Size - from.Size) * progress,
            from.Opacity + (to.Opacity - from.Opacity) * progress);
    }
}

public sealed class EditorProjectTextKeyframe
{
    public double TimeSeconds { get; set; }
    public double AnchorX { get; set; } = 0.5;
    public double AnchorY { get; set; } = 0.84;
    public double Size { get; set; } = 64;
    public double Opacity { get; set; } = 1;
    public string Easing { get; set; } = "linear";

    public EditorProjectTextState ToState() => new(AnchorX, AnchorY, Size, Opacity);

    public EditorProjectTextKeyframe Clone() => new()
    {
        TimeSeconds = TimeSeconds,
        AnchorX = AnchorX,
        AnchorY = AnchorY,
        Size = Size,
        Opacity = Opacity,
        Easing = Easing
    };

    public void Normalize(double startSeconds, double endSeconds)
    {
        TimeSeconds = Math.Clamp(TimeSeconds, Math.Max(0, startSeconds), Math.Max(startSeconds, endSeconds));
        AnchorX = Math.Clamp(AnchorX, 0, 1);
        AnchorY = Math.Clamp(AnchorY, 0, 1);
        Size = Math.Clamp(Size, 12, 220);
        Opacity = Math.Clamp(Opacity, 0, 1);
        Easing = Easing switch
        {
            "ease-in" or "ease-out" or "ease-in-out" or "hold" => Easing,
            _ => "linear"
        };
    }

    public static EditorProjectTextState Evaluate(
        IEnumerable<EditorProjectTextKeyframe>? keyframes,
        EditorProjectTextState fallback,
        double timelineSeconds)
    {
        var ordered = keyframes?.OrderBy(keyframe => keyframe.TimeSeconds).ToList() ?? [];
        if (ordered.Count == 0)
            return fallback;
        if (timelineSeconds <= ordered[0].TimeSeconds)
            return ordered[0].ToState();
        if (timelineSeconds >= ordered[^1].TimeSeconds)
            return ordered[^1].ToState();

        for (var index = 1; index < ordered.Count; index++)
        {
            var right = ordered[index];
            if (timelineSeconds > right.TimeSeconds)
                continue;

            var left = ordered[index - 1];
            var duration = Math.Max(0.0001, right.TimeSeconds - left.TimeSeconds);
            var progress = Math.Clamp((timelineSeconds - left.TimeSeconds) / duration, 0, 1);
            progress = EditorProjectGraphicKeyframe.ApplyEasing(progress, right.Easing);
            return EditorProjectTextState.Lerp(left.ToState(), right.ToState(), progress);
        }

        return ordered[^1].ToState();
    }
}

public sealed class EditorProjectClip
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string SourcePath { get; set; } = string.Empty;
    public string Name { get; set; } = "Clip";
    public string Notes { get; set; } = string.Empty;
    public string ColorLabel { get; set; } = "none";
    public EditorClipTrack Track { get; set; }
    public string TrackId { get; set; } = string.Empty;
    public string GroupId { get; set; } = string.Empty;
    public int Order { get; set; }
    public double SourceDurationSeconds { get; set; } = 1;
    public double TrimStartSeconds { get; set; }
    public double TrimEndSeconds { get; set; } = 1;
    public double TimelineStartSeconds { get; set; }
    public double PlaybackSpeed { get; set; } = 1;
    public double Volume { get; set; } = 1;
    public bool Muted { get; set; }
    public bool SourceAudioSeparated { get; set; }
    public bool NormalizeAudio { get; set; }
    public double NormalizationGainDb { get; set; }
    public double? IntegratedLoudnessLufs { get; set; }
    public double LoudnessAnalysisTrimStartSeconds { get; set; }
    public double LoudnessAnalysisTrimEndSeconds { get; set; }
    public bool NoiseReductionEnabled { get; set; }
    public double NoiseReductionStrength { get; set; } = 0.45;
    public List<EditorProjectClipVolumeKeyframe> VolumeKeyframes { get; set; } = [];
    public double Scale { get; set; } = 1;
    public double OffsetX { get; set; }
    public double OffsetY { get; set; }
    public List<EditorProjectClipTransformKeyframe> TransformKeyframes { get; set; } = [];
    public double CropLeft { get; set; }
    public double CropTop { get; set; }
    public double CropRight { get; set; }
    public double CropBottom { get; set; }
    public string MaskShape { get; set; } = "none";
    public double MaskRoundness { get; set; } = 0.16;
    public List<EditorProjectCurvePoint> MaskPolygon { get; set; } = [];
    public bool MaskInverted { get; set; }
    public EditorProjectVideoProcessingState? VideoProcessing { get; set; }
    public string Filter { get; set; } = "none";
    public List<EditorProjectClipEffect> Effects { get; set; } = EditorProjectClipEffect.CreateDefault();
    public double FilterStrength { get; set; } = 1;
    public List<EditorProjectClipFilterKeyframe> FilterStrengthKeyframes { get; set; } = [];
    public double Brightness { get; set; }
    public double Contrast { get; set; } = 1;
    public double Saturation { get; set; } = 1;
    public double Temperature { get; set; }
    public double Shadows { get; set; }
    public double Highlights { get; set; }
    public double Gamma { get; set; } = 1;
    public double HueDegrees { get; set; }
    public List<EditorProjectCurvePoint> ToneCurve { get; set; } = [];
    public double Sharpen { get; set; }
    public double Blur { get; set; }
    public double Vignette { get; set; }
    public string LutPath { get; set; } = string.Empty;
    public double LutStrength { get; set; } = 1;
    public string TransitionInStyle { get; set; } = "inherit";
    public double TransitionInDurationSeconds { get; set; } = 0.5;

    [JsonIgnore]
    public bool IsMissing => !string.IsNullOrWhiteSpace(SourcePath) && !File.Exists(SourcePath);

    public double EffectiveDurationSeconds =>
        Math.Max(0.1, TrimEndSeconds - TrimStartSeconds) / Math.Clamp(PlaybackSpeed, 0.25, 4);

    public void Normalize()
    {
        Id = string.IsNullOrWhiteSpace(Id) ? Guid.NewGuid().ToString("N") : Id;
        Name = string.IsNullOrWhiteSpace(Name) ? Path.GetFileNameWithoutExtension(SourcePath) : Name;
        Notes = Notes?.Trim() ?? string.Empty;
        if (Notes.Length > 4000)
            Notes = Notes[..4000];
        ColorLabel = EditorProjectColorLabels.Normalize(ColorLabel);
        TrackId = TrackId?.Trim() ?? string.Empty;
        GroupId = GroupId?.Trim() ?? string.Empty;
        SourceDurationSeconds = Math.Max(0.1, SourceDurationSeconds);
        TrimStartSeconds = Math.Clamp(TrimStartSeconds, 0, Math.Max(0, SourceDurationSeconds - 0.1));
        TrimEndSeconds = Math.Clamp(TrimEndSeconds, TrimStartSeconds + 0.1, SourceDurationSeconds);
        TimelineStartSeconds = Math.Max(0, TimelineStartSeconds);
        PlaybackSpeed = Math.Clamp(PlaybackSpeed, 0.25, 4);
        Volume = Math.Clamp(Volume, 0, 2);
        NormalizationGainDb = Math.Clamp(
            double.IsFinite(NormalizationGainDb) ? NormalizationGainDb : 0,
            -24,
            12);
        if (IntegratedLoudnessLufs is { } clipLufs && !double.IsFinite(clipLufs))
            IntegratedLoudnessLufs = null;
        if (IntegratedLoudnessLufs is not null &&
            (Math.Abs(LoudnessAnalysisTrimStartSeconds - TrimStartSeconds) > 0.001 ||
             Math.Abs(LoudnessAnalysisTrimEndSeconds - TrimEndSeconds) > 0.001))
        {
            NormalizeAudio = false;
            NormalizationGainDb = 0;
            IntegratedLoudnessLufs = null;
        }
        NoiseReductionStrength = Math.Clamp(
            double.IsFinite(NoiseReductionStrength) ? NoiseReductionStrength : 0.45,
            0,
            1);
        VolumeKeyframes ??= [];
        foreach (var keyframe in VolumeKeyframes)
            keyframe.Normalize(EffectiveDurationSeconds);
        VolumeKeyframes = VolumeKeyframes
            .OrderBy(keyframe => keyframe.TimeSeconds)
            .GroupBy(keyframe => Math.Round(keyframe.TimeSeconds * 120) / 120)
            .Select(group => group.Last())
            .ToList();
        Scale = Math.Clamp(Scale, 0.1, 4);
        OffsetX = Math.Clamp(OffsetX, -1, 1);
        OffsetY = Math.Clamp(OffsetY, -1, 1);
        TransformKeyframes ??= [];
        foreach (var keyframe in TransformKeyframes)
            keyframe.Normalize(EffectiveDurationSeconds);
        TransformKeyframes = TransformKeyframes
            .OrderBy(keyframe => keyframe.TimeSeconds)
            .GroupBy(keyframe => Math.Round(keyframe.TimeSeconds * 120) / 120)
            .Select(group => group.Last())
            .ToList();
        CropLeft = Math.Clamp(CropLeft, 0, 0.9);
        CropTop = Math.Clamp(CropTop, 0, 0.9);
        CropRight = Math.Clamp(CropRight, 0, 0.9 - CropLeft);
        CropBottom = Math.Clamp(CropBottom, 0, 0.9 - CropTop);
        MaskShape = MaskShape switch
        {
            "rounded" or "ellipse" or "polygon" => MaskShape,
            _ => "none"
        };
        MaskPolygon ??= [];
        MaskPolygon = MaskPolygon.Where(point => double.IsFinite(point.X) && double.IsFinite(point.Y))
            .Take(32).Select(point => new EditorProjectCurvePoint
            { X = Math.Clamp(point.X, 0, 1), Y = Math.Clamp(point.Y, 0, 1) }).ToList();
        MaskRoundness = Math.Clamp(MaskRoundness, 0, 0.5);
        Filter = string.IsNullOrWhiteSpace(Filter) ? "none" : Filter;
        Effects ??= [];
        Effects = Effects.Where(effect => effect is not null && !string.IsNullOrWhiteSpace(effect.Kind))
            .GroupBy(effect => effect.Kind.Trim().ToLowerInvariant(), StringComparer.Ordinal)
            .Select(group =>
            {
                var effect = group.First();
                effect.Kind = group.Key;
                effect.Id = string.IsNullOrWhiteSpace(effect.Id) ? Guid.NewGuid().ToString("N") : effect.Id.Trim();
                return effect;
            }).ToList();
        foreach (var kind in EditorProjectClipEffect.DefaultOrder)
            if (!Effects.Any(effect => effect.Kind == kind))
                Effects.Add(new EditorProjectClipEffect { Kind = kind });
        FilterStrength = Math.Clamp(FilterStrength, 0, 1);
        FilterStrengthKeyframes ??= [];
        foreach (var keyframe in FilterStrengthKeyframes)
            keyframe.Normalize(EffectiveDurationSeconds);
        FilterStrengthKeyframes = FilterStrengthKeyframes
            .OrderBy(keyframe => keyframe.TimeSeconds)
            .GroupBy(keyframe => Math.Round(keyframe.TimeSeconds * 120) / 120)
            .Select(group => group.Last())
            .ToList();
        Brightness = Math.Clamp(Brightness, -1, 1);
        Contrast = Math.Clamp(Contrast, 0.5, 2);
        Saturation = Math.Clamp(Saturation, 0, 3);
        Temperature = Math.Clamp(Temperature, -1, 1);
        Shadows = Math.Clamp(Shadows, -1, 1);
        Highlights = Math.Clamp(Highlights, -1, 1);
        Gamma = Math.Clamp(Gamma <= 0 ? 1 : Gamma, 0.25, 4);
        HueDegrees = Math.Clamp(HueDegrees, -180, 180);
        ToneCurve ??= [];
        ToneCurve = NaturalCubicCurve.NormalizePoints(ToneCurve
                .Select(point => new PiecewiseLinearCurvePoint(point.X, point.Y)))
            .Take(16)
            .Select(point => new EditorProjectCurvePoint { X = point.X, Y = point.Y })
            .ToList();
        if (ToneCurve.Count is > 0 and < 2)
            ToneCurve.Clear();
        if (ToneCurve.Count >= 2)
        {
            if (ToneCurve[0].X > 0.0001)
                ToneCurve.Insert(0, new EditorProjectCurvePoint { X = 0, Y = 0 });
            else
                ToneCurve[0].X = 0;
            if (ToneCurve[^1].X < 0.9999)
                ToneCurve.Add(new EditorProjectCurvePoint { X = 1, Y = 1 });
            else
                ToneCurve[^1].X = 1;
        }
        Sharpen = Math.Clamp(Sharpen, 0, 2);
        Blur = Math.Clamp(Blur, 0, 10);
        Vignette = Math.Clamp(Vignette, 0, 1);
        LutPath = LutPath?.Trim() ?? string.Empty;
        LutStrength = Math.Clamp(LutStrength, 0, 1);
        TransitionInStyle = TransitionInStyle switch
        {
            "inherit" or "cut" or "dissolve" or "fadeblack" or "fadewhite" or "wipeleft" or "wiperight" or
            "wipeup" or "wipedown" or "slideleft" or "slideright" or "slideup" or "slidedown" or
            "circleopen" or "rectcrop" or "horzopen" or "vertopen" or "diagtl" => TransitionInStyle,
            _ => "inherit"
        };
        TransitionInDurationSeconds = Math.Clamp(TransitionInDurationSeconds, 0.1, 2);
    }

    public EditorProjectClip CloneWithNewId()
    {
        return new EditorProjectClip
        {
            SourcePath = SourcePath,
            Name = Name,
            Notes = Notes,
            ColorLabel = ColorLabel,
            Track = Track,
            TrackId = TrackId,
            GroupId = GroupId,
            Order = Order,
            SourceDurationSeconds = SourceDurationSeconds,
            TrimStartSeconds = TrimStartSeconds,
            TrimEndSeconds = TrimEndSeconds,
            TimelineStartSeconds = TimelineStartSeconds,
            PlaybackSpeed = PlaybackSpeed,
            Volume = Volume,
            Muted = Muted,
            SourceAudioSeparated = SourceAudioSeparated,
            NormalizeAudio = NormalizeAudio,
            NormalizationGainDb = NormalizationGainDb,
            IntegratedLoudnessLufs = IntegratedLoudnessLufs,
            LoudnessAnalysisTrimStartSeconds = LoudnessAnalysisTrimStartSeconds,
            LoudnessAnalysisTrimEndSeconds = LoudnessAnalysisTrimEndSeconds,
            NoiseReductionEnabled = NoiseReductionEnabled,
            NoiseReductionStrength = NoiseReductionStrength,
            VolumeKeyframes = VolumeKeyframes.Select(keyframe => keyframe.Clone()).ToList(),
            Scale = Scale,
            OffsetX = OffsetX,
            OffsetY = OffsetY,
            TransformKeyframes = TransformKeyframes.Select(keyframe => keyframe.Clone()).ToList(),
            CropLeft = CropLeft,
            CropTop = CropTop,
            CropRight = CropRight,
            CropBottom = CropBottom,
            MaskShape = MaskShape,
            MaskRoundness = MaskRoundness,
            MaskPolygon = MaskPolygon.Select(point => new EditorProjectCurvePoint { X = point.X, Y = point.Y }).ToList(),
            MaskInverted = MaskInverted,
            VideoProcessing = VideoProcessing?.Clone(),
            Filter = Filter,
            Effects = Effects.Select(effect => effect.Clone()).ToList(),
            FilterStrength = FilterStrength,
            FilterStrengthKeyframes = FilterStrengthKeyframes.Select(keyframe => keyframe.Clone()).ToList(),
            Brightness = Brightness,
            Contrast = Contrast,
            Saturation = Saturation,
            Temperature = Temperature,
            Shadows = Shadows,
            Highlights = Highlights,
            Gamma = Gamma,
            HueDegrees = HueDegrees,
            ToneCurve = ToneCurve.Select(point => new EditorProjectCurvePoint
            {
                X = point.X,
                Y = point.Y
            }).ToList(),
            Sharpen = Sharpen,
            Blur = Blur,
            Vignette = Vignette,
            LutPath = LutPath,
            LutStrength = LutStrength,
            TransitionInStyle = TransitionInStyle,
            TransitionInDurationSeconds = TransitionInDurationSeconds
        };
    }

    public EditorProjectClipTransformState EvaluateTransform(double clipTimelineSeconds)
    {
        if (TransformKeyframes is null || TransformKeyframes.Count == 0)
            return new EditorProjectClipTransformState(Scale, OffsetX, OffsetY);

        var ordered = TransformKeyframes.OrderBy(keyframe => keyframe.TimeSeconds).ToList();
        if (clipTimelineSeconds <= ordered[0].TimeSeconds)
            return ordered[0].ToState();
        if (clipTimelineSeconds >= ordered[^1].TimeSeconds)
            return ordered[^1].ToState();

        for (var index = 1; index < ordered.Count; index++)
        {
            var right = ordered[index];
            if (clipTimelineSeconds > right.TimeSeconds)
                continue;

            var left = ordered[index - 1];
            var duration = Math.Max(0.0001, right.TimeSeconds - left.TimeSeconds);
            var progress = Math.Clamp((clipTimelineSeconds - left.TimeSeconds) / duration, 0, 1);
            progress = EditorProjectGraphicKeyframe.ApplyEasing(progress, right.Easing);
            return EditorProjectClipTransformState.Lerp(left.ToState(), right.ToState(), progress);
        }

        return ordered[^1].ToState();
    }

    public double EvaluateVolume(double clipTimelineSeconds)
    {
        if (VolumeKeyframes is null || VolumeKeyframes.Count == 0)
            return Volume;

        var ordered = VolumeKeyframes.OrderBy(keyframe => keyframe.TimeSeconds).ToList();
        if (clipTimelineSeconds <= ordered[0].TimeSeconds)
            return ordered[0].Volume;
        if (clipTimelineSeconds >= ordered[^1].TimeSeconds)
            return ordered[^1].Volume;

        for (var index = 1; index < ordered.Count; index++)
        {
            var right = ordered[index];
            if (clipTimelineSeconds > right.TimeSeconds)
                continue;

            var left = ordered[index - 1];
            var duration = Math.Max(0.0001, right.TimeSeconds - left.TimeSeconds);
            var progress = Math.Clamp((clipTimelineSeconds - left.TimeSeconds) / duration, 0, 1);
            progress = EditorProjectGraphicKeyframe.ApplyEasing(progress, right.Easing);
            return Math.Clamp(left.Volume + (right.Volume - left.Volume) * progress, 0, 2);
        }

        return ordered[^1].Volume;
    }

    public double EvaluateFilterStrength(double clipTimelineSeconds)
    {
        if (FilterStrengthKeyframes is null || FilterStrengthKeyframes.Count == 0)
            return FilterStrength;

        var ordered = FilterStrengthKeyframes.OrderBy(keyframe => keyframe.TimeSeconds).ToList();
        if (clipTimelineSeconds <= ordered[0].TimeSeconds)
            return ordered[0].Strength;
        if (clipTimelineSeconds >= ordered[^1].TimeSeconds)
            return ordered[^1].Strength;

        for (var index = 1; index < ordered.Count; index++)
        {
            var right = ordered[index];
            if (clipTimelineSeconds > right.TimeSeconds)
                continue;

            var left = ordered[index - 1];
            var duration = Math.Max(0.0001, right.TimeSeconds - left.TimeSeconds);
            var progress = Math.Clamp((clipTimelineSeconds - left.TimeSeconds) / duration, 0, 1);
            progress = EditorProjectGraphicKeyframe.ApplyEasing(progress, right.Easing);
            return Math.Clamp(left.Strength + (right.Strength - left.Strength) * progress, 0, 1);
        }

        return ordered[^1].Strength;
    }
}

public readonly record struct EditorProjectClipTransformState(double Scale, double OffsetX, double OffsetY)
{
    public static EditorProjectClipTransformState Lerp(
        EditorProjectClipTransformState from,
        EditorProjectClipTransformState to,
        double progress)
    {
        progress = Math.Clamp(progress, 0, 1);
        return new EditorProjectClipTransformState(
            from.Scale + (to.Scale - from.Scale) * progress,
            from.OffsetX + (to.OffsetX - from.OffsetX) * progress,
            from.OffsetY + (to.OffsetY - from.OffsetY) * progress);
    }
}

public sealed class EditorProjectClipTransformKeyframe
{
    public double TimeSeconds { get; set; }
    public double Scale { get; set; } = 1;
    public double OffsetX { get; set; }
    public double OffsetY { get; set; }
    public string Easing { get; set; } = "linear";

    public EditorProjectClipTransformState ToState() => new(Scale, OffsetX, OffsetY);

    public EditorProjectClipTransformKeyframe Clone() => new()
    {
        TimeSeconds = TimeSeconds,
        Scale = Scale,
        OffsetX = OffsetX,
        OffsetY = OffsetY,
        Easing = Easing
    };

    public void Normalize(double clipDuration)
    {
        TimeSeconds = Math.Clamp(TimeSeconds, 0, Math.Max(0, clipDuration));
        Scale = Math.Clamp(Scale, 0.1, 4);
        OffsetX = Math.Clamp(OffsetX, -1, 1);
        OffsetY = Math.Clamp(OffsetY, -1, 1);
        Easing = Easing switch
        {
            "ease-in" or "ease-out" or "ease-in-out" or "hold" => Easing,
            _ => "linear"
        };
    }
}

public sealed class EditorProjectClipVolumeKeyframe
{
    public double TimeSeconds { get; set; }
    public double Volume { get; set; } = 1;
    public string Easing { get; set; } = "linear";

    public EditorProjectClipVolumeKeyframe Clone() => new()
    {
        TimeSeconds = TimeSeconds,
        Volume = Volume,
        Easing = Easing
    };

    public void Normalize(double clipDuration)
    {
        TimeSeconds = Math.Clamp(TimeSeconds, 0, Math.Max(0, clipDuration));
        Volume = Math.Clamp(Volume, 0, 2);
        Easing = Easing switch
        {
            "ease-in" or "ease-out" or "ease-in-out" or "hold" => Easing,
            _ => "linear"
        };
    }
}

public sealed class EditorProjectClipFilterKeyframe
{
    public double TimeSeconds { get; set; }
    public double Strength { get; set; } = 1;
    public string Easing { get; set; } = "linear";

    public EditorProjectClipFilterKeyframe Clone() => new()
    {
        TimeSeconds = TimeSeconds,
        Strength = Strength,
        Easing = Easing
    };

    public void Normalize(double clipDuration)
    {
        TimeSeconds = Math.Clamp(TimeSeconds, 0, Math.Max(0, clipDuration));
        Strength = Math.Clamp(Strength, 0, 1);
        Easing = Easing switch
        {
            "ease-in" or "ease-out" or "ease-in-out" or "hold" => Easing,
            _ => "linear"
        };
    }
}

public sealed class EditorProjectCurvePoint
{
    public double X { get; set; }
    public double Y { get; set; }
}

public enum EditorClipTrack
{
    Primary,
    Overlay
}

public sealed class EditorProjectSettings
{
    public string PreviewQuality { get; set; } = "original";
    public string ExportQuality { get; set; } = "high";
    public int ExportFrameRate { get; set; } = 60;
    public string ExportEncoder { get; set; } = "auto";
    public int ExportBitrateMbps { get; set; } = 16;
    public string ExportResolutionMode { get; set; } = "canvas";
    public int ExportWidth { get; set; } = 1280;
    public int ExportHeight { get; set; } = 720;
    public bool ExportAspectLocked { get; set; } = true;
    public int ExportAudioBitrateKbps { get; set; } = 192;
    public bool ShowSafeAreaGuides { get; set; }
    public string TransitionStyle { get; set; } = "cut";
    public double TransitionDurationSeconds { get; set; } = 0.5;
    public string Text { get; set; } = string.Empty;
    public string TextStyle { get; set; } = "plain";
    public string TextAnimation { get; set; } = "none";
    public string TextFontFamily { get; set; } = EditorTextTypography.Automatic;
    public string TextFontFilePath { get; set; } = string.Empty;
    public string TextFontFamilyName { get; set; } = string.Empty;
    public string TextColor { get; set; } = EditorTextTypography.Automatic;
    public double TextAnchorX { get; set; } = 0.5;
    public double TextAnchorY { get; set; } = 0.84;
    public double TextSize { get; set; } = 64;
    public double TextBoxWidth { get; set; } = 420;
    public double TextOpacity { get; set; } = 1;
    public List<EditorProjectTextKeyframe> TextKeyframes { get; set; } = [];
    public bool TextEnabled { get; set; }
    public double TextStartSeconds { get; set; }
    public double TextEndSeconds { get; set; } = 5;
    public string SubtitleText { get; set; } = string.Empty;
    public string SubtitleStyle { get; set; } = "subtitle";
    public string SubtitleFontFamily { get; set; } = EditorTextTypography.Automatic;
    public string SubtitleFontFilePath { get; set; } = string.Empty;
    public string SubtitleFontFamilyName { get; set; } = string.Empty;
    public string SubtitleColor { get; set; } = EditorTextTypography.Automatic;
    public double SubtitleAnchorX { get; set; } = 0.5;
    public double SubtitleAnchorY { get; set; } = 0.88;
    public double SubtitleSize { get; set; } = 42;
    public double SubtitleBoxWidth { get; set; } = 760;
    public bool SubtitleEnabled { get; set; }
    public double SubtitleStartSeconds { get; set; }
    public double SubtitleEndSeconds { get; set; } = 5;
    public string VideoFilter { get; set; } = "none";
    public bool VfxEnabled { get; set; }
    public double VfxStartSeconds { get; set; }
    public double VfxEndSeconds { get; set; } = 5;
    public string ElementOverlay { get; set; } = "none";
    public bool ElementEnabled { get; set; }
    public double ElementStartSeconds { get; set; }
    public double ElementEndSeconds { get; set; } = 5;
    public double FadeInSeconds { get; set; }
    public double FadeOutSeconds { get; set; }
    public double FreezeFrameSeconds { get; set; }
    public double AudioVolume { get; set; } = 1;
    public bool MuteAudio { get; set; }
    public double VideoScale { get; set; } = 1;
    public double VideoOffsetX { get; set; }
    public double VideoOffsetY { get; set; }
}

public static class PreviewProxyQuality
{
    public static string Normalize(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "720p" => "720p",
        "540p" => "540p",
        _ => "original"
    };
}
