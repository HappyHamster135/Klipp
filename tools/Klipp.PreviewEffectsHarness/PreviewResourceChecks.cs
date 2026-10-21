using Klipp.Desktop.Models;
using Microsoft.Graphics.Canvas;
using Microsoft.UI.Xaml.Controls;
using Windows.Media.Playback;

namespace Klipp.Desktop.Controls;

public sealed partial class SafeClipEditorView
{
    internal static void VerifyNativeFallbackLifetime()
    {
        using var native = new MediaPlayer { IsVideoFrameServerEnabled = false };
        string? signature = "fallback|mono";
        Check(KeepNativePreviewFallback(native, "warm", ref signature),
            "Changing a look retried GPU output on an already native decoder.");
        Check(signature == "fallback|warm" && !native.IsVideoFrameServerEnabled,
            "Keeping native fallback changed decoder mode or retained an obsolete signature.");
        using var reopened = new MediaPlayer { IsVideoFrameServerEnabled = true };
        signature = null;
        Check(!KeepNativePreviewFallback(reopened, "warm", ref signature) && signature is null,
            "An early frame-server decoder could not retry GPU output after source reload.");
    }

    private sealed partial class ClipEffectPreviewResources
    {
        public void DrawForChecks() => DrawFrame();
        public object ImageForChecks => _image;
        public object? ImageSourceForChecks => _image.Source;
        public object? GraphForChecks => _effect;
        public int CopiesForChecks => _frameCopyCount;
        public EditorProjectEffectLayer LayerForChecks => _timelineBlends[0].Layer;
    }

    internal static void VerifyLiveRegionEditing(MediaPlayer player)
    {
        var host = new Grid();
        var clip = new EditorProjectClip { SourcePath = "synthetic.mp4" };
        var layer = new EditorProjectEffectLayer
        {
            Filter = "mono", StartSeconds = 0, EndSeconds = 2,
            RegionX = .2, RegionY = .2, RegionWidth = .3, RegionHeight = .3
        };
        using var preview = new ClipEffectPreviewResources(player, host, clip, 1, [layer], .75, false,
            "mono", Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread(), (_, error) => throw error);
        preview.DrawForChecks();
        var image = preview.ImageForChecks;
        var source = preview.ImageSourceForChecks;
        var graph = preview.GraphForChecks;
        var copies = preview.CopiesForChecks;
        Check(source is not null && copies > 0, "No initial frame for the live-region regression test.");
        for (var index = 0; index < 30; index++)
        {
            var updated = layer.Clone();
            updated.RegionX = .05 + index * .01;
            updated.RegionY = .1 + index * .005;
            updated.RegionFeather = index / 30d;
            updated.RegionShape = index % 2 == 0 ? "rectangle" : "ellipse";
            preview.Update(clip, 1, [updated], .75, false, "mono");
            Check(ReferenceEquals(preview.ImageSourceForChecks, source), "A mask edit removed the last good image.");
            preview.DrawForChecks();
            Check(ReferenceEquals(preview.ImageForChecks, image) && ReferenceEquals(preview.ImageSourceForChecks, source),
                "Moving a region replaced its image or drawing surface.");
            Check(ReferenceEquals(preview.GraphForChecks, graph), "Moving a region rebuilt the grade graph.");
            Check(ReferenceEquals(preview.LayerForChecks, updated), "The preview retained a stale layer snapshot.");
            Check(preview.CopiesForChecks == copies, "A paused mask edit copied another decoder frame.");
        }
        layer.UpsertRegionKeyframe(0, new(.05, .2, .3, .3));
        layer.UpsertRegionKeyframe(2, new(.65, .2, .3, .3));
        for (var index = 0; index < 10; index++)
        {
            preview.Update(clip, 1, [layer.Clone()], index / 5d, false, "mono");
            preview.DrawForChecks();
            Check(ReferenceEquals(preview.GraphForChecks, graph) && preview.CopiesForChecks == copies,
                "Animated coverage rebuilt a graph or copied an unchanged paused frame.");
        }
        var warm = layer.Clone(); warm.Filter = "warm";
        preview.Update(clip, 1, [warm], .75, false, "warm");
        Check(ReferenceEquals(preview.ImageSourceForChecks, source), "Changing a look removed the last good image before drawing.");
        preview.DrawForChecks();
        Check(ReferenceEquals(preview.ImageForChecks, image) && ReferenceEquals(preview.ImageSourceForChecks, source) &&
            preview.CopiesForChecks == copies, "Changing the FX graph replaced its stable image or recopied the decoder.");
    }
}
