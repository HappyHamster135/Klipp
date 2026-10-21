using System;
using System.Linq;
using System.Text.Json;
using Klipp.Desktop.Models;

namespace Klipp.Desktop.Services;

/// <summary>Stages effect edits without changing a live timeline clip or its media identity.</summary>
public static class EditorClipEffectWorkflowService
{
    public static bool TryCreateMutation(EditorProjectClip source, Action<EditorProjectClip> mutate,
        out EditorProjectClip updated)
    {
        var original = JsonSerializer.Serialize(source);
        updated = JsonSerializer.Deserialize<EditorProjectClip>(original)!;
        mutate(updated);
        updated.Normalize();
        return !string.Equals(original, JsonSerializer.Serialize(updated), StringComparison.Ordinal);
    }

    public static void ApplyLook(EditorProjectClip clip, string filter)
    {
        clip.Filter = filter;
        if (!string.Equals(filter, "none", StringComparison.OrdinalIgnoreCase))
            SetStageEnabled(clip, "look", true);
    }

    public static void SetStageEnabled(EditorProjectClip clip, string kind, bool enabled)
    {
        var stage = clip.Effects.FirstOrDefault(item => item.Kind == kind);
        if (stage is not null)
            stage.Enabled = enabled;
    }

    public static void MoveStage(EditorProjectClip clip, string kind, int delta)
    {
        var position = clip.Effects.FindIndex(item => item.Kind == kind);
        if (position < 0 || delta is not (-1 or 1) || position + delta < 0 || position + delta >= clip.Effects.Count)
            return;
        var item = clip.Effects[position];
        clip.Effects.RemoveAt(position);
        clip.Effects.Insert(position + delta, item);
    }

    public static void ResetAllEffects(EditorProjectClip clip)
    {
        clip.Filter = "none";
        clip.FilterStrength = 1;
        clip.FilterStrengthKeyframes.Clear();
        clip.Brightness = clip.Temperature = clip.Shadows = clip.Highlights = clip.HueDegrees = 0;
        clip.Contrast = clip.Saturation = clip.Gamma = 1;
        clip.Sharpen = clip.Blur = clip.Vignette = 0;
        clip.ToneCurve.Clear();
        clip.LutPath = string.Empty;
        clip.LutStrength = 1;
        // Reuse node identities so resetting an already neutral clip is a no-op.
        clip.Effects = EditorProjectClipEffect.DefaultOrder.Select(kind =>
        {
            var stage = clip.Effects.FirstOrDefault(item => item.Kind == kind) ?? new EditorProjectClipEffect { Kind = kind };
            stage.Enabled = true;
            return stage;
        }).ToList();
    }
}
