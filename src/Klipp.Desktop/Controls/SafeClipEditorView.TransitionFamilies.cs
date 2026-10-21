using System;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Klipp.Desktop.Controls;

public sealed partial class SafeClipEditorView
{
    private string _wipeTransitionVariant = "wipeleft";
    private string _slideTransitionVariant = "slideleft";
    private string _barnTransitionVariant = "horzopen";
    private bool _syncingTransitionDirections;

    private static string GetTransitionFamily(string style) => style switch
    {
        "wipeleft" or "wiperight" or "wipeup" or "wipedown" => "wipe",
        "slideleft" or "slideright" or "slideup" or "slidedown" => "slide",
        "horzopen" or "vertopen" => "barn",
        _ => style
    };

    private string ResolveTransitionFamilyStyle(string family) => family switch
    {
        "wipe" => _wipeTransitionVariant,
        "slide" => _slideTransitionVariant,
        "barn" => _barnTransitionVariant,
        _ => family
    };

    private void RememberTransitionVariant(string style)
    {
        switch (GetTransitionFamily(style))
        {
            case "wipe": _wipeTransitionVariant = style; break;
            case "slide": _slideTransitionVariant = style; break;
            case "barn": _barnTransitionVariant = style; break;
        }
    }

    private static string GetTransitionDisplayName(string style) => style switch
    {
        "cut" => "No transition", "dissolve" => "Cross dissolve",
        "fadeblack" => "Dip to black", "fadewhite" => "Dip to white",
        "wipeleft" => "Wipe · Left", "wiperight" => "Wipe · Right",
        "wipeup" => "Wipe · Up", "wipedown" => "Wipe · Down",
        "slideleft" => "Slide · Left", "slideright" => "Slide · Right",
        "slideup" => "Slide · Up", "slidedown" => "Slide · Down",
        "circleopen" => "Iris", "rectcrop" => "Box reveal",
        "horzopen" => "Barn doors · Horizontal", "vertopen" => "Barn doors · Vertical",
        "diagtl" => "Diagonal", _ => "Cross dissolve"
    };

    private void SyncTransitionDirectionPicker(ComboBox picker, string style, bool enabled = true)
    {
        _syncingTransitionDirections = true;
        try
        {
            var family = GetTransitionFamily(style);
            picker.Header = family == "barn" ? "Orientation" : "Direction";
            var choices = family switch
            {
                "wipe" => new[] { ("wipeleft", "Left"), ("wiperight", "Right"), ("wipeup", "Up"), ("wipedown", "Down") },
                "slide" => new[] { ("slideleft", "Left"), ("slideright", "Right"), ("slideup", "Up"), ("slidedown", "Down") },
                "barn" => new[] { ("horzopen", "Horizontal"), ("vertopen", "Vertical") },
                _ => Array.Empty<(string, string)>()
            };
            picker.Visibility = choices.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
            picker.IsEnabled = enabled;
            // Preserve the same item objects during playback/selection refreshes.
            if (!picker.Items.OfType<ComboBoxItem>().Select(item => item.Tag as string)
                    .SequenceEqual(choices.Select(choice => choice.Item1)))
            {
                picker.Items.Clear();
                foreach (var choice in choices)
                    picker.Items.Add(new ComboBoxItem { Tag = choice.Item1, Content = choice.Item2 });
            }
            picker.SelectedItem = picker.Items.OfType<ComboBoxItem>().FirstOrDefault(item => item.Tag as string == style);
        }
        finally { _syncingTransitionDirections = false; }
    }

    private void OnTransitionDirectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingTransitionDirections || _syncingPresetPickers || _isLoadingClip || _suppressProjectChanges ||
            TransitionDirectionComboBox.SelectedItem is not ComboBoxItem { Tag: string style }) return;
        RememberTransitionVariant(style);
        RefreshTransitionFamilySamples();
        if (!LayerTimeline.TryGetSelectedMediaClipTransition(out var current, out _, out _) || current == style) return;
        CommitPendingProjectChange();
        PausePreview();
        SetClipTransition(style);
        UpdatePresetPickers();
    }

    private void OnTransitionAuditionDirectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingTransitionDirections || _syncingTransitionAudition ||
            TransitionAuditionDirectionComboBox.SelectedItem is not ComboBoxItem { Tag: string style }) return;
        RememberTransitionVariant(style);
        RefreshTransitionFamilySamples();
        SelectTransitionAudition(style);
    }

    private void RefreshTransitionFamilySamples()
    {
        foreach (var button in BasicTransitionsPalette.Children.OfType<Button>().Concat(MotionTransitionsPalette.Children.OfType<Button>()))
            if (button.IsLoaded) OnTransitionPaletteCardLoaded(button, new RoutedEventArgs());
    }
}
