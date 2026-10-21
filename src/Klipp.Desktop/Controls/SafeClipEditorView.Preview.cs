using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Klipp.Desktop.Models;
using Microsoft.UI.Composition;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.UI;

namespace Klipp.Desktop.Controls;

public sealed partial class SafeClipEditorView
{
    private interface IPreviewVisual
    {
        Border Frame { get; }
    }

    private sealed record GraphicPreviewVisual(string Signature, Border Frame, Grid MediaHost) : IPreviewVisual;
    private sealed record TextPreviewVisual(string Signature, Border Frame) : IPreviewVisual;

    private readonly Dictionary<string, GraphicPreviewVisual> _graphicPreviewVisuals = new(StringComparer.Ordinal);
    private readonly Dictionary<string, TextPreviewVisual> _textPreviewVisuals = new(StringComparer.Ordinal);
    private readonly Dictionary<Grid, ClipEffectPreviewResources> _clipEffectPreviews = [];
    private readonly Dictionary<Border, SolidColorBrush> _previewOverlayBrushes = [];

    private void SetFallbackPreviewColor(Border overlay, Color color)
    {
        if (!_previewOverlayBrushes.TryGetValue(overlay, out var brush))
        {
            brush = new SolidColorBrush(color);
            _previewOverlayBrushes.Add(overlay, brush);
        }
        else if (!brush.Color.Equals(color)) brush.Color = color;
        if (!ReferenceEquals(overlay.Background, brush)) overlay.Background = brush;
    }

    private void ClearClipEffectPreview(Grid host)
    {
        ElementCompositionPreview.SetElementChildVisual(host, null);
        if (_clipEffectPreviews.Remove(host, out var resources))
            resources.Dispose();
        host.Children.Clear();
    }

    private void ClearPreviewVisualCaches()
    {
        DisposeAdditionalVideoTrackPreviews();
        GraphicOverlayCanvas.Children.Clear();
        ExtraTextOverlayCanvas.Children.Clear();
        _graphicPreviewVisuals.Clear();
        _textPreviewVisuals.Clear();
        foreach (var host in _clipEffectPreviews.Keys.ToArray())
            ClearClipEffectPreview(host);
        _primaryEffectPreviewSignature = null;
        _transitionEffectPreviewSignature = null;
        _overlayEffectPreviewSignature = null;
    }

    private static void PlacePreviewVisual(Canvas canvas, Border visual, int index)
    {
        var current = canvas.Children.IndexOf(visual);
        if (current == index)
            return;
        if (current >= 0)
            canvas.Children.RemoveAt(current);
        canvas.Children.Insert(index, visual);
    }

    private static void RemoveInactivePreviewVisuals<T>(Canvas canvas, Dictionary<string, T> cache, HashSet<string> visibleIds)
        where T : IPreviewVisual
    {
        foreach (var id in cache.Keys.Where(id => !visibleIds.Contains(id)).ToArray())
        {
            canvas.Children.Remove(cache[id].Frame);
            cache.Remove(id);
        }
    }

    private void UpdateGraphicPreviewVisual(GraphicPreviewVisual visual, EditorProjectGraphicLayer layer, EditorProjectGraphicState state)
    {
        if (visual.Frame.Width != state.Width || visual.Frame.Height != state.Height)
        {
            visual.Frame.Width = state.Width;
            visual.Frame.Height = state.Height;
            ApplyGraphicMaskPreview(visual.MediaHost, layer, state);
            if (visual.MediaHost.Children.FirstOrDefault() is Grid blendHost &&
                ElementCompositionPreview.GetElementChildVisual(blendHost) is SpriteVisual blendVisual)
            {
                blendVisual.Size = new System.Numerics.Vector2((float)state.Width, (float)state.Height);
            }
        }
        visual.MediaHost.Opacity = state.Opacity;
        Canvas.SetLeft(visual.Frame, state.AnchorX * _project!.CanvasWidth - state.Width / 2);
        Canvas.SetTop(visual.Frame, state.AnchorY * _project.CanvasHeight - state.Height / 2);
    }

    private Border GetTextPreviewVisual(EditorProjectTextLayer layer, bool isSubtitle)
    {
        var signature = JsonSerializer.Serialize(new
        {
            layer.Style, layer.FontFamily, layer.FontFilePath,
            layer.FontFamilyName, layer.Color, isSubtitle
        });
        if (_textPreviewVisuals.TryGetValue(layer.Id, out var cached) && cached.Signature == signature)
            return cached.Frame;
        if (cached is not null)
            ExtraTextOverlayCanvas.Children.Remove(cached.Frame);

        var outline = layer.Style is "outline" or "neon" or "sticker";
        var textBlock = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center,
            FontFamily = new FontFamily(EditorTextTypography.ResolvePreviewFontFamily(
                layer.FontFamily, layer.Style, layer.FontFilePath, layer.FontFamilyName)),
            FontWeight = layer.Style is "bold" or "meme" or "outline" or "neon" or "shadow" or "impact" or "sticker"
                ? FontWeights.Bold : FontWeights.SemiBold,
            Foreground = new SolidColorBrush(ParseEditorTextColor(EditorTextTypography.ResolveColor(layer.Color, layer.Style)))
        };
        var frame = new Border
        {
            Padding = new Thickness(12, 6, 12, 6),
            CornerRadius = new CornerRadius(8),
            Background = new SolidColorBrush(layer.Style == "highlight"
                ? Color.FromArgb(230, 238, 255, 38)
                : layer.Style == "sticker" ? Color.FromArgb(232, 169, 112, 255)
                : isSubtitle || layer.Style is "caption" or "shadow"
                    ? Color.FromArgb(160, 0, 0, 0) : Color.FromArgb(0, 0, 0, 0)),
            BorderBrush = new SolidColorBrush(outline ? Color.FromArgb(255, 255, 255, 255) : Color.FromArgb(0, 0, 0, 0)),
            BorderThickness = new Thickness(outline ? 2 : 0),
            Child = textBlock,
            IsHitTestVisible = false,
            RenderTransformOrigin = new Point(0.5, 0.5),
            RenderTransform = new CompositeTransform()
        };
        _textPreviewVisuals[layer.Id] = new TextPreviewVisual(signature, frame);
        return frame;
    }

    private void OnPreviousFrameClicked(object sender, RoutedEventArgs e) => StepPreviewFrame(-1);
    private void OnNextFrameClicked(object sender, RoutedEventArgs e) => StepPreviewFrame(1);

    private void StepPreviewFrame(int direction)
    {
        PausePreview();
        var frameRate = Math.Max(1, _exportFrameRate);
        var frame = Math.Round(_previewPosition.TotalSeconds * frameRate, MidpointRounding.AwayFromZero);
        SeekTo(TimeSpan.FromSeconds(Math.Clamp((frame + direction) / frameRate, 0, _duration.TotalSeconds)));
    }

    private void OnLoopPreviewClicked(object sender, RoutedEventArgs e)
    {
        _loopPreview = !_loopPreview;
        SetOptionButtonState(LoopPreviewButton, _loopPreview);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(
            LoopPreviewButton, _loopPreview ? "Loop playback on" : "Loop playback off");
    }
}
