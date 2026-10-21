using System;
using System.Linq;
using Klipp.Desktop.Models;

namespace Klipp.Desktop.Services;

/// <summary>Applies timeline range edits without restarting an effect's region motion.</summary>
public static class EditorEffectRegionAnimationService
{
    public static void ApplyRange(EditorProjectEffectLayer effect, EditorProjectEffectLayer original,
        double newStart, double newEnd, bool move = false)
    {
        // Capture before assigning: callers may pass the same object for both.
        var source = original.Clone();
        effect.RegionX = source.RegionX;
        effect.RegionY = source.RegionY;
        effect.RegionWidth = source.RegionWidth;
        effect.RegionHeight = source.RegionHeight;
        effect.RegionKeyframes = source.RegionKeyframes.Select(key => key.Clone()).ToList();
        effect.StartSeconds = source.StartSeconds;
        effect.EndSeconds = source.EndSeconds;
        if (move)
        {
            effect.StartSeconds = newStart;
            effect.EndSeconds = newEnd;
        }
        else
            effect.TrimRegionAnimation(newStart, newEnd);
    }
}
