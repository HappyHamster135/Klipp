using System;
using System.Collections.Generic;
using System.Linq;
using Klipp.Core.Editing;

namespace Klipp.Desktop.Models;

/// <summary>A timed effect on a region of a clip's original source frame.</summary>
public sealed class EditorProjectEffectLayer
{
    public const double MinimumRegionSize = 0.02;
    private static readonly HashSet<string> SupportedFilters = new(StringComparer.Ordinal)
    {
        "natural", "film", "matte", "goldenhour", "steel", "boost", "cinematic", "tealorange",
        "warm", "cool", "mono", "noir", "vintage", "vibrant", "soft", "dream", "sharpen",
        "blur", "vignette", "glitch"
    };

    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string TargetClipId { get; set; } = string.Empty;
    public string Name { get; set; } = "Effect";
    public string Filter { get; set; } = "none";
    public EditorProjectClip? Style { get; set; }
    public double StartSeconds { get; set; }
    public double EndSeconds { get; set; } = 5;
    public double Amount { get; set; } = 1;
    public bool Enabled { get; set; } = true;
    public double RegionX { get; set; }
    public double RegionY { get; set; }
    public double RegionWidth { get; set; } = 1;
    public double RegionHeight { get; set; } = 1;
    public string RegionShape { get; set; } = "rectangle";
    public double RegionFeather { get; set; }
    public List<EditorProjectEffectRegionKeyframe> RegionKeyframes { get; set; } = [];

    public EditorProjectEffectLayer Clone()
    {
        var copy = (EditorProjectEffectLayer)MemberwiseClone();
        copy.RegionKeyframes = (RegionKeyframes ?? []).Where(key => key is not null).Select(key => key.Clone()).ToList();
        if (Style is not null)
        {
            copy.Style = Style.CloneWithNewId();
            copy.Style.Id = Style.Id;
        }
        return copy;
    }

    public EditorEffectRegion EvaluateRegion(double absoluteTimelineSeconds)
    {
        var fallback = EditorEffectRegionMath.Normalize(new(RegionX, RegionY, RegionWidth, RegionHeight));
        if (RegionKeyframes is not { Count: > 0 }) return fallback;
        var time = Finite(absoluteTimelineSeconds - StartSeconds, 0);
        // Normalize/Upsert keep this persistent list sorted; evaluation is a
        // read-only operation used by every preview frame.
        if (time <= RegionKeyframes[0].TimeSeconds) return RegionKeyframes[0].ToRegion();
        for (var index = 1; index < RegionKeyframes.Count; index++)
        {
            var right = RegionKeyframes[index];
            if (time > right.TimeSeconds) continue;
            var left = RegionKeyframes[index - 1];
            var progress = (time - left.TimeSeconds) / Math.Max(.000001, right.TimeSeconds - left.TimeSeconds);
            return EditorEffectRegionMath.Interpolate(left.ToRegion(), right.ToRegion(), progress, right.Easing);
        }
        return RegionKeyframes[^1].ToRegion();
    }

    public void UpsertRegionKeyframe(double absoluteTimelineSeconds, EditorEffectRegion region)
    {
        RegionKeyframes ??= [];
        var time = Math.Clamp(Finite(absoluteTimelineSeconds - StartSeconds, 0), 0, Math.Max(0, EndSeconds - StartSeconds));
        if (RegionKeyframes.Count == 0 && time > .0001)
            RegionKeyframes.Add(RegionKeyframe(0, new(RegionX, RegionY, RegionWidth, RegionHeight)));
        var existing = RegionKeyframes.FirstOrDefault(key => Math.Abs(key.TimeSeconds - time) <= .0001);
        var updated = RegionKeyframe(existing?.TimeSeconds ?? time, region, existing?.Easing ?? "linear");
        if (existing is not null) RegionKeyframes.Remove(existing);
        RegionKeyframes.Add(updated);
        RegionKeyframes = RegionKeyframes.OrderBy(key => key.TimeSeconds).ToList();
    }

    /// <summary>Trim absolute time bounds while keeping the original region path over the retained interval.</summary>
    public void TrimRegionAnimation(double newStartSeconds, double newEndSeconds)
    {
        var start = Finite(newStartSeconds, StartSeconds);
        var end = Math.Max(start, Finite(newEndSeconds, EndSeconds));
        if (start == StartSeconds && end == EndSeconds) return;
        if (RegionKeyframes is { Count: > 0 })
        {
            var originalStart = StartSeconds;
            var snapshot = Clone();
            var localStart = start - originalStart;
            var localEnd = end - originalStart;
            EditorProjectEffectRegionKeyframe Sample(double local) => RegionKeyframe(local,
                snapshot.EvaluateRegion(originalStart + local));
            var sampled = EditorKeyframeTrimMath.SampleTruncatedCurve(snapshot.RegionKeyframes, localStart, true,
                key => key.TimeSeconds, key => key.Easing, Sample);
            sampled = EditorKeyframeTrimMath.SampleTruncatedCurve(sampled, localEnd, false,
                key => key.TimeSeconds, key => key.Easing, Sample);
            var keys = sampled.Where(key => key.TimeSeconds > localStart + .0001 && key.TimeSeconds < localEnd - .0001)
                .Select(key => key.Clone()).ToList();
            var first = Sample(localStart);
            var last = Sample(localEnd);
            if (end > start)
            {
                // A hold segment must remain a hold at a newly sampled out-point.
                last.Easing = sampled.FirstOrDefault(key => Math.Abs(key.TimeSeconds - localEnd) <= .0001)?.Easing ??
                    (snapshot.RegionKeyframes.FirstOrDefault(key => key.TimeSeconds >= localEnd - .0001)?.Easing == "hold"
                        ? "hold" : "linear");
                keys.Add(last);
            }
            keys.Add(first);
            foreach (var key in keys) key.TimeSeconds = Math.Max(0, key.TimeSeconds - localStart);
            RegionKeyframes = keys.OrderBy(key => key.TimeSeconds).ToList();
        }
        StartSeconds = start;
        EndSeconds = end;
    }

    private static EditorProjectEffectRegionKeyframe RegionKeyframe(double time, EditorEffectRegion region, string easing = "linear")
    {
        region = EditorEffectRegionMath.Normalize(region);
        return new() { TimeSeconds = time, RegionX = region.X, RegionY = region.Y,
            RegionWidth = region.Width, RegionHeight = region.Height, Easing = easing };
    }

    public bool IsActiveAt(double timelineSeconds) => Enabled && Amount > 0 && (Style is not null || Filter != "none") &&
        timelineSeconds >= StartSeconds && timelineSeconds < EndSeconds;

    public void Normalize(EditorProjectClip target, double projectDuration)
    {
        if (string.IsNullOrWhiteSpace(Id)) Id = Guid.NewGuid().ToString("N");
        TargetClipId = target.Id;
        Name = string.IsNullOrWhiteSpace(Name) ? "Effect" : Name.Trim();
        Filter = Filter?.Trim().ToLowerInvariant() ?? "none";
        if (!SupportedFilters.Contains(Filter)) Filter = "none";
        if (Style is not null)
        {
            Style.Normalize();
            Style.FilterStrength = 1;
            Style.FilterStrengthKeyframes.Clear();
            Style.TransformKeyframes.Clear();
            Style.VolumeKeyframes.Clear();
            Style.SourcePath = string.Empty;
            Style.VideoProcessing = null;
        }
        var targetStart = Math.Max(0, target.TimelineStartSeconds);
        var targetEnd = Math.Max(targetStart, Math.Min(projectDuration, targetStart + target.EffectiveDurationSeconds));
        // A trim can leave less than the normal gesture minimum. Preserve that
        // exact intersection; never stretch the FX onto previously clean frames.
        var previousStart = Finite(StartSeconds, targetStart);
        var previousEnd = Math.Max(previousStart, Finite(EndSeconds, targetEnd));
        StartSeconds = previousStart;
        EndSeconds = previousEnd;
        RegionKeyframes ??= [];
        var normalizedKeys = RegionKeyframes.Where(key => key is not null && double.IsFinite(key.TimeSeconds))
            .Select(key => { key.Normalize(Math.Max(0, previousEnd - previousStart)); return key; })
            .OrderBy(key => key.TimeSeconds).ToList();
        RegionKeyframes = [];
        foreach (var key in normalizedKeys)
        {
            if (RegionKeyframes.Count > 0 && Math.Abs(RegionKeyframes[^1].TimeSeconds - key.TimeSeconds) <= .0001)
                RegionKeyframes[^1] = key;
            else
                RegionKeyframes.Add(key);
        }
        var newStart = Math.Clamp(previousStart, targetStart, targetEnd);
        var newEnd = Math.Clamp(previousEnd, newStart, targetEnd);
        if (newStart != previousStart || newEnd != previousEnd) TrimRegionAnimation(newStart, newEnd);
        Amount = Math.Clamp(Finite(Amount, 1), 0, 1);
        RegionWidth = Math.Clamp(Finite(RegionWidth, 1), MinimumRegionSize, 1);
        RegionHeight = Math.Clamp(Finite(RegionHeight, 1), MinimumRegionSize, 1);
        RegionX = Math.Clamp(Finite(RegionX, 0), 0, 1 - RegionWidth);
        RegionY = Math.Clamp(Finite(RegionY, 0), 0, 1 - RegionHeight);
        RegionShape = RegionShape == "ellipse" ? "ellipse" : "rectangle";
        RegionFeather = Math.Clamp(Finite(RegionFeather, 0), 0, 1);
    }

    private static double Finite(double value, double fallback) => double.IsFinite(value) ? value : fallback;
}

public sealed class EditorProjectEffectRegionKeyframe
{
    public double TimeSeconds { get; set; }
    public double RegionX { get; set; }
    public double RegionY { get; set; }
    public double RegionWidth { get; set; } = 1;
    public double RegionHeight { get; set; } = 1;
    public string Easing { get; set; } = "linear";

    public EditorProjectEffectRegionKeyframe Clone() => (EditorProjectEffectRegionKeyframe)MemberwiseClone();
    public EditorEffectRegion ToRegion() => EditorEffectRegionMath.Normalize(new(RegionX, RegionY, RegionWidth, RegionHeight));
    public void Normalize(double duration)
    {
        TimeSeconds = Math.Clamp(double.IsFinite(TimeSeconds) ? TimeSeconds : 0, 0, Math.Max(0, duration));
        var region = ToRegion();
        RegionX = region.X; RegionY = region.Y; RegionWidth = region.Width; RegionHeight = region.Height;
        Easing = Easing is "ease-in" or "ease-out" or "ease-in-out" or "hold" ? Easing : "linear";
    }
}
