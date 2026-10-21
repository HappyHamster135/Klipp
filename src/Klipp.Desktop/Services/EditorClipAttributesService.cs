using System.Linq;
using Klipp.Desktop.Models;

namespace Klipp.Desktop.Services;

public static class EditorClipAttributesService
{
    public static void CopyLook(EditorProjectClip source, EditorProjectClip target)
    {
        target.Filter = source.Filter; target.FilterStrength = source.FilterStrength;
        target.Brightness = source.Brightness; target.Contrast = source.Contrast; target.Saturation = source.Saturation;
        target.Temperature = source.Temperature; target.Shadows = source.Shadows; target.Highlights = source.Highlights;
        target.Gamma = source.Gamma; target.HueDegrees = source.HueDegrees; target.Sharpen = source.Sharpen;
        target.Blur = source.Blur; target.Vignette = source.Vignette;
        target.ToneCurve = source.ToneCurve.Select(point => new EditorProjectCurvePoint { X = point.X, Y = point.Y }).ToList();
        target.LutPath = source.LutPath; target.LutStrength = source.LutStrength;
        target.Effects = source.Effects.Select(node => node.Clone()).ToList();
        target.Normalize();
    }

    public static void CopyTransform(EditorProjectClip source, EditorProjectClip target)
    {
        target.Scale = source.Scale; target.OffsetX = source.OffsetX; target.OffsetY = source.OffsetY;
        target.CropLeft = source.CropLeft; target.CropTop = source.CropTop;
        target.CropRight = source.CropRight; target.CropBottom = source.CropBottom;
        target.MaskShape = source.MaskShape; target.MaskRoundness = source.MaskRoundness;
        target.MaskInverted = source.MaskInverted;
        target.MaskPolygon = source.MaskPolygon.Select(point => new EditorProjectCurvePoint { X = point.X, Y = point.Y }).ToList();
        target.TransformKeyframes = source.TransformKeyframes.Select(keyframe => keyframe.Clone()).ToList();
        target.Normalize();
    }

    public static void CopyAudio(EditorProjectAudioTrack source, EditorProjectAudioTrack target)
    {
        target.Volume = source.Volume; target.Muted = source.Muted;
        target.FadeInSeconds = source.FadeInSeconds; target.FadeOutSeconds = source.FadeOutSeconds;
        target.NoiseReductionEnabled = source.NoiseReductionEnabled; target.NoiseReductionStrength = source.NoiseReductionStrength;
        target.DuckingEnabled = source.DuckingEnabled; target.DuckingStrength = source.DuckingStrength;
        target.VolumeKeyframes = source.VolumeKeyframes.Select(keyframe => keyframe.Clone()).ToList();
        // A loudness measurement belongs to the target's media and trim. Keep
        // its existing analysis instead of copying a gain measured elsewhere.
        target.Normalize();
    }
}
