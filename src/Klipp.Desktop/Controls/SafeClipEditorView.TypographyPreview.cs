using System;
using Klipp.Desktop.Models;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace Klipp.Desktop.Controls;

public sealed partial class SafeClipEditorView
{
    private void OnTypographyFontPickerLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is ComboBox picker)
            UpdateFontPickerSamples(picker, picker == SubtitleFontComboBox ? _subtitleStyle : _textStyle);
    }

    private static void UpdateFontPickerSamples(ComboBox picker, string style)
    {
        foreach (var value in picker.Items)
        {
            if (value is not ComboBoxItem item)
                continue;
            var fontId = item.Tag?.ToString() ?? EditorTextTypography.Automatic;
            if (item.Content is string label)
            {
                var row = new Grid { MinWidth = 190, Height = 32, ColumnSpacing = 10 };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(42) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.Children.Add(new TextBlock
                {
                    Text = "Aa",
                    FontSize = 24,
                    VerticalAlignment = VerticalAlignment.Center
                });
                var name = new TextBlock
                {
                    Text = label,
                    FontSize = 12,
                    FontFamily = new FontFamily("Segoe UI Variable"),
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    VerticalAlignment = VerticalAlignment.Center
                };
                Grid.SetColumn(name, 1);
                row.Children.Add(name);
                item.Content = row;
                item.HorizontalContentAlignment = HorizontalAlignment.Stretch;
                AutomationProperties.SetName(item, label);
            }
            if (item.Content is Grid { Children.Count: > 0 } sampleRow &&
                sampleRow.Children[0] is TextBlock sample)
            {
                sample.FontFamily = new FontFamily(EditorTextTypography.ResolvePreviewFontFamily(fontId, style));
            }
        }
    }

    private void OnTypographyStylePreviewLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is Border { Tag: string style } preview)
            UpdateTypographySample(preview, style, EditorTextTypography.Automatic, EditorTextTypography.Automatic);
    }

    private static void UpdateTypographySample(
        Border? preview,
        string style,
        string fontFamily,
        string color,
        string? fontFilePath = null,
        string? customFamilyName = null)
    {
        if (preview is null)
            return;
        // Native text stays crisp at every display scale and uses the same font resolution as the player.
        var family = new FontFamily(EditorTextTypography.ResolvePreviewFontFamily(
            fontFamily, style, fontFilePath, customFamilyName));
        var foreground = ParseEditorTextColor(EditorTextTypography.ResolveColor(color, style));
        var content = new Grid
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(4)
        };
        var text = style is "meme" or "impact" ? "AA" : style == "typewriter" ? "Aa|" : "Aa";
        TextBlock Sample(Color tint, double x = 0, double y = 0) => new()
        {
            Text = text,
            FontFamily = family,
            FontSize = 28,
            FontWeight = style is "bold" or "meme" or "outline" or "neon" or "shadow" or "impact" or "sticker"
                ? FontWeights.Bold : FontWeights.SemiBold,
            Foreground = new SolidColorBrush(tint),
            RenderTransform = new TranslateTransform { X = x, Y = y },
            IsHitTestVisible = false
        };

        if (style == "shadow")
            content.Children.Add(Sample(Color.FromArgb(210, 0, 0, 0), 3, 3));

        var stroke = style == "neon" ? Color.FromArgb(230, 184, 76, 255) : Color.FromArgb(235, 0, 0, 0);
        var strokeSize = style is "outline" or "impact" ? 2.0 : 1.0;
        if (style is "bold" or "meme" or "pixel" or "outline" or "neon" or "shadow" or "typewriter" or "impact")
        {
            foreach (var (x, y) in new[] { (-1, -1), (0, -1), (1, -1), (-1, 0), (1, 0), (-1, 1), (0, 1), (1, 1) })
                content.Children.Add(Sample(stroke, x * strokeSize, y * strokeSize));
        }
        content.Children.Add(Sample(foreground));
        preview.Child = new Border
        {
            Padding = new Thickness(style is "highlight" or "sticker" or "caption" or "subtitle" ? 8 : 2, 2, 8, 2),
            CornerRadius = new CornerRadius(4),
            Background = new SolidColorBrush(style switch
            {
                "highlight" => Color.FromArgb(235, 238, 255, 38),
                "sticker" => Color.FromArgb(230, 169, 112, 255),
                "caption" or "subtitle" => Color.FromArgb(170, 0, 0, 0),
                _ => Color.FromArgb(0, 0, 0, 0)
            }),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Child = content
        };
        AutomationProperties.SetName(preview, $"Typography sample: {style}");
    }
}
