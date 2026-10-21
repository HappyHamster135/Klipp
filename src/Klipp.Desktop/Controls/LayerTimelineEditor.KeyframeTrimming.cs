using System.Collections.Generic;
using Klipp.Core.Editing;
using Klipp.Desktop.Models;

namespace Klipp.Desktop.Controls;

public sealed partial class LayerTimelineEditor
{
    private static IReadOnlyList<EditorProjectClipTransformKeyframe> SamplePartialTransformCurve(
        IReadOnlyList<EditorProjectClipTransformKeyframe> source, EditorProjectClipTransformState fallback,
        double boundary, bool keepAfter) => EditorKeyframeTrimMath.SampleTruncatedCurve(source, boundary, keepAfter,
        key => key.TimeSeconds, key => key.Easing, time =>
        {
            var state = EvaluateTransformKeyframes(source, fallback, time);
            return new EditorProjectClipTransformKeyframe
            { TimeSeconds = time, Scale = state.Scale, OffsetX = state.OffsetX, OffsetY = state.OffsetY, Easing = "linear" };
        });

    private static IReadOnlyList<EditorProjectClipFilterKeyframe> SamplePartialFilterCurve(
        IReadOnlyList<EditorProjectClipFilterKeyframe> source, double fallback, double boundary, bool keepAfter) =>
        EditorKeyframeTrimMath.SampleTruncatedCurve(source, boundary, keepAfter, key => key.TimeSeconds, key => key.Easing,
            time => new EditorProjectClipFilterKeyframe
            { TimeSeconds = time, Strength = EvaluateFilterKeyframes(source, fallback, time), Easing = "linear" });
}
