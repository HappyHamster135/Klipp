using System;
using System.Collections.Generic;
using System.Linq;
using Klipp.Core.Input;
using Klipp.Desktop.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Klipp.Desktop.Controls;

public sealed partial class SafeClipEditorView
{
    private bool _editorShortcutsOpen;

    private void OnCopyTimelineSelectionClicked(object sender, RoutedEventArgs e)
    {
        LayerTimeline.CopySelectedClips();
        LayerTimeline.Focus(FocusState.Programmatic);
    }

    private void OnCutTimelineSelectionClicked(object sender, RoutedEventArgs e)
    {
        RunTimelineSelectionCommand(LayerTimeline.CutSelectedClips);
        LayerTimeline.Focus(FocusState.Programmatic);
    }

    private void OnPasteTimelineSelectionClicked(object sender, RoutedEventArgs e)
    {
        RunTimelineSelectionCommand(LayerTimeline.PasteClipsAtPlayhead);
        LayerTimeline.Focus(FocusState.Programmatic);
    }

    private void OnSelectAllTimelineClipsClicked(object sender, RoutedEventArgs e)
    {
        LayerTimeline.SelectAllClips();
        LayerTimeline.Focus(FocusState.Programmatic);
    }

    private void OnTimelineEditMenuOpening(object sender, object e)
    {
        CopyTimelineMenuItem.IsEnabled = LayerTimeline.CanCopySelectedClips;
        CutTimelineMenuItem.IsEnabled = LayerTimeline.CanCutSelectedClips;
        PasteTimelineMenuItem.IsEnabled = LayerTimeline.HasCopiedClips;
        PrecisionTimelineMenuItem.IsEnabled = LayerTimeline.TryGetSelectedRange(out _, out _, out _);
        SetMenuGesture(CopyTimelineMenuItem, EditorShortcutActions.Copy);
        SetMenuGesture(CutTimelineMenuItem, EditorShortcutActions.Cut);
        SetMenuGesture(PasteTimelineMenuItem, EditorShortcutActions.Paste);
        SetMenuGesture(RippleDeleteTimelineMenuItem, EditorShortcutActions.RippleDelete);
        SetMenuGesture(GoToTimecodeMenuItem, EditorShortcutActions.GoToTimecode);
    }

    private void SetMenuGesture(MenuFlyoutItem item, string action)
    {
        var binding = _shortcuts.FirstOrDefault(shortcut => shortcut.Action == action);
        item.KeyboardAcceleratorTextOverride = binding is null ? string.Empty : EditorShortcutCatalog.FormatGesture(binding);
    }

    private async void OnEditorShortcutsClicked(object sender, RoutedEventArgs e)
    {
        if (_editorShortcutsOpen || XamlRoot is null)
            return;
        _editorShortcutsOpen = true;
        try
        {
            var content = new StackPanel { Spacing = 8 };
            content.Children.Add(new TextBlock
            {
                Text = "Current shortcuts. Change them in Settings → Keyboard shortcuts.",
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 8)
            });
            foreach (var definition in EditorShortcutCatalog.Definitions)
            {
                var binding = _shortcuts.FirstOrDefault(shortcut => shortcut.Action == definition.Action);
                if (binding is not null)
                    content.Children.Add(CreateShortcutHelpRow(definition.Label, EditorShortcutCatalog.FormatGesture(binding)));
            }
            content.Children.Add(new TextBlock
            {
                Text = "Timeline gestures",
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Margin = new Thickness(0, 16, 0, 4)
            });
            foreach (var (description, gesture) in TimelineGestureHelp)
                content.Children.Add(CreateShortcutHelpRow(description, gesture));
            var scroll = new ScrollViewer
            {
                Content = content,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                MinWidth = 540,
                MaxHeight = 460,
                Padding = new Thickness(0, 0, 12, 0)
            };
            AutomationProperties.SetName(scroll, "Editor commands and timeline gestures");
            var dialog = AccessibleDialogFactory.Create(XamlRoot, "Editor shortcuts", scroll,
                closeButtonText: "Close", defaultButton: ContentDialogButton.Close);
            await dialog.ShowAsync();
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            ProjectStatusText.Text = "Close the current dialog before opening the shortcut guide.";
        }
        finally { _editorShortcutsOpen = false; }
    }

    private static readonly IReadOnlyList<(string Description, string Gesture)> TimelineGestureHelp =
    [
        ("Select all unlocked video and audio clips", "Ctrl + A"),
        ("Move selected clips, or step the playhead", "Left / Right"),
        ("Move or step ten frames", "Shift + Left / Right"),
        ("Trim selection by one frame", "Edit → Move and trim by frame"),
        ("Previous / next clip edge on the selected track", "Up / Down"),
        ("Go to playback range start / end", "Home / End"),
        ("Set playback range start / end", "I / O"),
        ("Split video or audio where you click", "Double-click clip"),
        ("Add an audio volume point", "Alt + double-click audio"),
        ("Add or remove a clip from selection", "Ctrl + click"),
        ("Select several clips", "Drag empty track space"),
        ("Temporarily bypass snapping", "Hold Alt while dragging"),
        ("Zoom at the pointer", "Ctrl + mouse wheel"),
        ("Pan the timeline", "Mouse wheel"),
        ("Fade selected audio in or out", "Drag its upper corners"),
        ("Cancel an active drag", "Esc")
    ];

    private static Grid CreateShortcutHelpRow(string description, string gesture)
    {
        var row = new Grid { ColumnSpacing = 20, MinHeight = 24 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var label = new TextBlock
        {
            Text = description,
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center
        };
        var key = new TextBlock
        {
            Text = gesture,
            FontFamily = new FontFamily("Consolas"),
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(key, 1);
        row.Children.Add(label);
        row.Children.Add(key);
        AutomationProperties.SetName(row, $"{description}: {gesture}");
        return row;
    }
}
