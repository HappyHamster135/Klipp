using System.Text.Json;
using Klipp.Core.Editing;
using Klipp.Desktop.Models;
using Klipp.Desktop.Services;

internal static class EffectRegionAnimationChecks
{
    public static int Run()
    {
        var checks = 0;
        void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
            checks++;
        }
        static EditorProjectEffectLayer Animated(string easing = "linear") => new()
        {
            Id = "animated-fx", TargetClipId = "target", Name = "Moving blur", Filter = "blur",
            StartSeconds = 10, EndSeconds = 18, RegionX = .1, RegionY = .1, RegionWidth = .2, RegionHeight = .3,
            RegionFeather = .25,
            RegionKeyframes =
            [
                new() { TimeSeconds = 0, RegionX = .1, RegionY = .1, RegionWidth = .2, RegionHeight = .3 },
                new() { TimeSeconds = 4, RegionX = .3, RegionY = .2, RegionWidth = .3, RegionHeight = .25, Easing = easing },
                new() { TimeSeconds = 8, RegionX = .6, RegionY = .5, RegionWidth = .25, RegionHeight = .3, Easing = easing }
            ]
        };
        static bool Near(EditorEffectRegion a, EditorEffectRegion b, double tolerance = .00004) =>
            Math.Abs(a.X - b.X) <= tolerance && Math.Abs(a.Y - b.Y) <= tolerance &&
            Math.Abs(a.Width - b.Width) <= tolerance && Math.Abs(a.Height - b.Height) <= tolerance;

        foreach (var easing in new[] { "linear", "ease-in", "ease-out", "ease-in-out", "hold" })
        {
            var original = Animated(easing);
            var serialized = JsonSerializer.Serialize(original);
            var trimmed = original.Clone();
            EditorEffectRegionAnimationService.ApplyRange(trimmed, original, 11.25, 16.75);
            Check(trimmed.StartSeconds == 11.25 && trimmed.EndSeconds == 16.75 &&
                trimmed.RegionKeyframes[0].TimeSeconds == 0 && trimmed.RegionKeyframes[^1].TimeSeconds == 5.5,
                $"{easing}: trimmed FX keys were not rebased to its new range.");
            Check(Enumerable.Range(0, 111).All(index =>
            {
                var time = 11.25 + 5.5 * index / 110;
                return Near(original.EvaluateRegion(time), trimmed.EvaluateRegion(time));
            }), $"{easing}: trimming changed retained FX region motion.");
            Check(JsonSerializer.Serialize(original) == serialized && trimmed.RegionFeather == .25,
                $"{easing}: range editing mutated the source FX or its feather.");

            var left = original.Clone();
            var right = original.Clone();
            EditorEffectRegionAnimationService.ApplyRange(left, original, 10, 13.25);
            EditorEffectRegionAnimationService.ApplyRange(right, original, 13.25, 18);
            Check(Enumerable.Range(0, 161).All(index =>
            {
                var time = 10 + 8d * index / 160;
                return Near(original.EvaluateRegion(time), (time <= 13.25 ? left : right).EvaluateRegion(time));
            }), $"{easing}: splitting restarted or changed the FX motion path.");
            Check(Near(left.EvaluateRegion(13.25), right.EvaluateRegion(13.25)),
                $"{easing}: split FX boundaries do not meet.");

            var exact = original.Clone();
            EditorEffectRegionAnimationService.ApplyRange(exact, original, 10, 14);
            Check(Enumerable.Range(0, 81).All(index => Near(original.EvaluateRegion(10 + index / 20d), exact.EvaluateRegion(10 + index / 20d))),
                $"{easing}: trimming at an existing key changed its incoming easing.");
        }

        var source = Animated("ease-in-out");
        var originalKeys = JsonSerializer.Serialize(source.RegionKeyframes);
        var moved = source.Clone();
        EditorEffectRegionAnimationService.ApplyRange(moved, source, 15, 23, move: true);
        Check(JsonSerializer.Serialize(moved.RegionKeyframes) == originalKeys &&
            Enumerable.Range(0, 81).All(index => Near(source.EvaluateRegion(10 + index / 10d), moved.EvaluateRegion(15 + index / 10d))),
            "Moving an FX changed its local keys or motion relative to the clip.");
        var noOp = source.Clone();
        var before = JsonSerializer.Serialize(noOp);
        EditorEffectRegionAnimationService.ApplyRange(noOp, noOp, 10, 18);
        Check(JsonSerializer.Serialize(noOp) == before, "Applying an unchanged FX range altered keyframes or easing.");
        var recovered = source.Clone();
        EditorEffectRegionAnimationService.ApplyRange(recovered, source, 13, 14);
        EditorEffectRegionAnimationService.ApplyRange(recovered, source, 10, 18);
        Check(JsonSerializer.Serialize(recovered.RegionKeyframes) == originalKeys,
            "Trimming inward and back out within a gesture did not recover the original region animation.");
        moved.RegionKeyframes[0].RegionX = .75;
        Check(JsonSerializer.Serialize(source.RegionKeyframes) == originalKeys,
            "Copied FX animation shared mutable keyframes with the original.");
        var target = new EditorProjectClip { Id = "target", TimelineStartSeconds = 12,
            SourceDurationSeconds = 20, TrimStartSeconds = 2, TrimEndSeconds = 7 };
        var clamped = source.Clone();
        clamped.Normalize(target, 30);
        Check(clamped.StartSeconds == 12 && clamped.EndSeconds == 17 &&
            Enumerable.Range(0, 101).All(index => Near(source.EvaluateRegion(12 + index / 20d), clamped.EvaluateRegion(12 + index / 20d))),
            "Trimming a video boundary changed the attached FX path over the retained source frames.");
        var still = new EditorProjectEffectLayer { StartSeconds = 2, EndSeconds = 9, RegionX = .2, RegionY = .3, RegionWidth = .4, RegionHeight = .2 };
        EditorEffectRegionAnimationService.ApplyRange(still, still, 4, 7);
        Check(still.RegionKeyframes.Count == 0 && Near(still.EvaluateRegion(5), new(.2, .3, .4, .2)),
            "Trimming a static FX unexpectedly enabled animation or changed its region.");
        var tracks = new[] { new EditorTimelineTrack { Id = EditorProject.DefaultPrimaryVideoTrackId, Kind = EditorTimelineTrackKind.Video } };
        var video = new EditorProjectClip { Id = "target", TimelineStartSeconds = 10, SourcePath = "animation-source.mp4",
            SourceDurationSeconds = 8, TrimEndSeconds = 8, TrackId = EditorProject.DefaultPrimaryVideoTrackId };
        var clipboard = new EditorTimelineClipboardService();
        clipboard.Capture([video], [], tracks, [source]);
        Check(clipboard.TryCreatePaste(tracks, 0, 30, out var paste, out _) && paste!.EffectLayers.Count == 1 &&
            paste.EffectLayers[0].StartSeconds == 0 && paste.EffectLayers[0].EndSeconds == 8 &&
            JsonSerializer.Serialize(paste.EffectLayers[0].RegionKeyframes) == originalKeys &&
            Enumerable.Range(0, 81).All(index => Near(source.EvaluateRegion(10 + index / 10d), paste.EffectLayers[0].EvaluateRegion(index / 10d))),
            "Copy/paste changed local FX animation timing or lost region keys.");
        paste!.EffectLayers[0].RegionKeyframes[0].RegionX = .75;
        clipboard.TryCreatePaste(tracks, 1, 30, out var secondPaste, out _);
        Check(JsonSerializer.Serialize(source.RegionKeyframes) == originalKeys &&
            JsonSerializer.Serialize(secondPaste!.EffectLayers[0].RegionKeyframes) == originalKeys,
            "Pasted FX animation shared keys with the original or reusable clipboard.");
        return checks;
    }
}
