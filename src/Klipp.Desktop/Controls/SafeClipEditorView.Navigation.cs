using System;
using Klipp.Core.Editing;
using Klipp.Desktop.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;

namespace Klipp.Desktop.Controls;

public sealed partial class SafeClipEditorView
{
    private bool _timecodeDialogOpen;

    private async void OnGoToTimecodeClicked(object sender, RoutedEventArgs e)
    {
        if (_timecodeDialogOpen || _project is null || XamlRoot is null) return;
        _timecodeDialogOpen = true;
        var origin = CurrentTimelinePosition.TotalSeconds;
        var duration = _duration.TotalSeconds;
        var lastFramePosition = Math.Floor((duration + .0000001) * _exportFrameRate) / _exportFrameRate;
        var loadVersion = _projectLoadVersion;
        var entry = new TextBox
        {
            Header = "Position",
            Text = EditorTimecodeMath.Format(Math.Clamp(origin, 0, lastFramePosition), _exportFrameRate),
            PlaceholderText = "00:00:00:00",
            MaxLength = 40,
            MinWidth = 280
        };
        AutomationProperties.SetName(entry, "Go to timecode position");
        var hint = new TextBlock
        {
            Text = $"HH:MM:SS:FF · {_exportFrameRate} fps\nYou can also enter minutes:seconds or seconds, such as 1:25 or 85.5.\nUse + or - to move relative to {EditorTimecodeMath.Format(origin, _exportFrameRate)}.",
            TextWrapping = TextWrapping.Wrap,
            Opacity = .75
        };
        var error = new TextBlock { TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
        AutomationProperties.SetLiveSetting(error, AutomationLiveSetting.Polite);
        var content = new StackPanel { Spacing = 12, Children = { entry, hint, error } };
        var dialog = AccessibleDialogFactory.Create(XamlRoot, "Go to timecode", content,
            primaryButtonText: "Go", closeButtonText: "Cancel", defaultButton: ContentDialogButton.Primary);
        var target = origin;
        void Validate()
        {
            var valid = EditorTimecodeMath.TryParsePosition(entry.Text, origin, duration,
                _exportFrameRate, out target, out var message);
            dialog.IsPrimaryButtonEnabled = valid;
            error.Text = message;
            error.Visibility = valid ? Visibility.Collapsed : Visibility.Visible;
        }
        entry.TextChanged += (_, _) => Validate();
        dialog.Opened += (_, _) => { entry.Focus(FocusState.Programmatic); entry.SelectAll(); Validate(); };
        dialog.PrimaryButtonClick += (_, args) =>
        {
            Validate();
            args.Cancel = !dialog.IsPrimaryButtonEnabled;
        };
        ContentDialogResult result;
        try { result = await dialog.ShowAsync(); }
        catch (Exception exception) when (exception is InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            ProjectStatusText.Text = "Close the current dialog before opening Go to timecode.";
            return;
        }
        finally { _timecodeDialogOpen = false; }
        if (result != ContentDialogResult.Primary || loadVersion != _projectLoadVersion)
            return;
        PausePreview();
        SeekTo(TimeSpan.FromSeconds(target));
        LayerTimeline.Focus(FocusState.Programmatic);
        ProjectStatusText.Text = $"Playhead at {EditorTimecodeMath.Format(target, _exportFrameRate)}";
    }
}
