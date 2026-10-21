using System;
using Klipp.Core.Editing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;

namespace Klipp.Desktop.Controls;

public sealed partial class SafeClipEditorView
{
    private void OnTransitionPaletteCardLoaded(object sender, RoutedEventArgs e)
    {
        OnTransitionPaletteItemLoaded(sender, e);
        if (sender is not Button { Tag: string style } button) return;
        var name = AutomationProperties.GetName(button);
        var concreteStyle = ResolveTransitionFamilyStyle(style);
        var sample = CreateTransitionCardSample(concreteStyle);
        sample.IsHitTestVisible = false;
        AutomationProperties.SetAccessibilityView(sample, AccessibilityView.Raw);
        var image = new Viewbox
        {
            Height = 52,
            Stretch = Stretch.Uniform,
            HorizontalAlignment = HorizontalAlignment.Center,
            Child = sample,
            IsHitTestVisible = false
        };
        var caption = new Grid { ColumnSpacing = 4 };
        caption.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        caption.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        caption.Children.Add(new TextBlock
        {
            Text = name,
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis
        });
        var play = new FontIcon { Glyph = "\uE768", FontSize = 10, Opacity = 0.75, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(play, 1);
        caption.Children.Add(play);
        var content = new StackPanel { Spacing = 5 };
        content.Children.Add(image);
        content.Children.Add(caption);
        button.Content = content;
        AutomationProperties.SetHelpText(button, $"{name}. Click to play a popup sample and choose its direction, or drag to a join between clips.");
        ToolTipService.SetToolTip(button, $"Preview {GetTransitionDisplayName(concreteStyle)} · drag to a join to apply");
    }

    private static Grid CreateTransitionCardSample(string style)
    {
        const double width = 256, height = 144;
        // A still from the same A/B sample makes the preset recognizable before
        // playing it. Only the opened popup animates, keeping the gallery quiet.
        var progress = style is "fadeblack" or "fadewhite" ? 0.35 : 0.5;
        var frame = EditorTransitionAuditionMath.Evaluate(style, progress);
        var scene = new Grid
        {
            Width = width,
            Height = height,
            Background = TransitionSampleBrush(0xFF000000),
            Clip = new RectangleGeometry { Rect = new Rect(0, 0, width, height) }
        };
        var outgoing = CreateTransitionSampleScene(incoming: false);
        outgoing.Opacity = frame.OutgoingOpacity;
        outgoing.RenderTransform = new TranslateTransform { X = frame.OutgoingX * width, Y = frame.OutgoingY * height };
        scene.Children.Add(outgoing);
        var incoming = CreateTransitionSampleScene(incoming: true);
        incoming.Opacity = frame.IncomingOpacity;
        incoming.RenderTransform = new TranslateTransform { X = frame.IncomingX * width, Y = frame.IncomingY * height };
        var reveal = new Grid
        {
            Clip = new RectangleGeometry
            {
                Rect = new Rect(frame.RevealX * width, frame.RevealY * height, frame.RevealWidth * width, frame.RevealHeight * height)
            }
        };
        var shapeHost = new Grid();
        shapeHost.Children.Add(incoming);
        reveal.Children.Add(shapeHost);
        ApplyTransitionShapeMask(shapeHost, frame, width, height);
        scene.Children.Add(reveal);
        scene.Children.Add(new Rectangle { Fill = TransitionSampleBrush(0xFFFFFFFF), Opacity = frame.WhiteOpacity });
        return scene;
    }

    private static Grid CreateTransitionSampleScene(bool incoming)
    {
        var scene = new Grid { Background = TransitionSampleBrush(incoming ? 0xFF4A3B33 : 0xFF19313C) };
        var canvas = new Canvas();
        var sun = new Ellipse
        {
            Width = incoming ? 36 : 29,
            Height = incoming ? 36 : 29,
            Fill = TransitionSampleBrush(incoming ? 0xFFE0C39B : 0xFFD9BC86)
        };
        Canvas.SetLeft(sun, incoming ? 49 : 176);
        Canvas.SetTop(sun, incoming ? 32 : 25);
        canvas.Children.Add(sun);
        canvas.Children.Add(new Polygon
        {
            Points = incoming
                ? new PointCollection { new(0, 119), new(84, 79), new(169, 144), new(0, 144) }
                : new PointCollection { new(0, 115), new(65, 51), new(149, 144), new(0, 144) },
            Fill = TransitionSampleBrush(incoming ? 0xFF8E7560 : 0xFF3D6570)
        });
        canvas.Children.Add(new Polygon
        {
            Points = incoming
                ? new PointCollection { new(94, 144), new(188, 62), new(256, 113), new(256, 144) }
                : new PointCollection { new(70, 144), new(164, 68), new(256, 131), new(256, 144) },
            Fill = TransitionSampleBrush(incoming ? 0xFF6B5547 : 0xFF294D58)
        });
        var ground = new Rectangle { Width = 256, Height = 19, Fill = TransitionSampleBrush(incoming ? 0xFF352C28 : 0xFF142731) };
        Canvas.SetTop(ground, 125);
        canvas.Children.Add(ground);
        scene.Children.Add(canvas);
        return scene;
    }

    private static SolidColorBrush TransitionSampleBrush(uint color) => new(Windows.UI.Color.FromArgb(
        (byte)(color >> 24), (byte)(color >> 16), (byte)(color >> 8), (byte)color));
}
