using System;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using Klipp.Core.Editing;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Windows.Foundation;
using VirtualKey = Windows.System.VirtualKey;

namespace Klipp.Desktop.Controls;

public sealed partial class SafeClipEditorView
{
    private string _transitionAuditionStyle = "dissolve";
    private double _transitionAuditionDurationSeconds = 0.5;
    private double _transitionAuditionElapsed;
    private double _transitionAuditionStartedAt;
    private bool _transitionAuditionPlaying;
    private bool _syncingTransitionAudition;
    private bool _transitionAuditionInitialized;
    private DispatcherQueueTimer? _transitionAuditionTimer;

    private static double TransitionAuditionNow => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;

    private void OnTransitionAuditionLoaded(object sender, RoutedEventArgs e)
    {
        if (!_transitionAuditionInitialized)
        {
            _transitionAuditionInitialized = true;
            _transitionAuditionTimer = DispatcherQueue.CreateTimer();
            _transitionAuditionTimer.Interval = TimeSpan.FromMilliseconds(16);
            _transitionAuditionTimer.Tick += OnTransitionAuditionTick;
            AddHandler(UIElement.KeyDownEvent, new KeyEventHandler(OnTransitionAuditionKeyDown), true);
            SizeChanged += (_, _) =>
            {
                if (TransitionAuditionPopup.IsOpen) PositionTransitionAuditionPopup();
            };
            TransitionsPanel.RegisterPropertyChangedCallback(VisibilityProperty, (_, _) =>
            {
                if (TransitionsPanel.Visibility != Visibility.Visible)
                    DismissTransitionAudition();
            });
        }
        SyncTransitionAuditionControls();
        RenderTransitionAudition();
    }

    private void OnTransitionAuditionUnloaded(object sender, RoutedEventArgs e) => DismissTransitionAudition();

    private void OnOpenTransitionAuditionClicked(object sender, RoutedEventArgs e)
    {
        SelectTransitionAudition(_transitionAuditionStyle);
        if (OpenTransitionAuditionButton.FocusState == FocusState.Keyboard)
            DispatcherQueue.TryEnqueue(() =>
            {
                if (TransitionAuditionPopup.IsOpen) TransitionAuditionPlayButton.Focus(FocusState.Keyboard);
            });
    }

    private void OnCloseTransitionAuditionClicked(object sender, RoutedEventArgs e)
    {
        DismissTransitionAudition();
        OpenTransitionAuditionButton.Focus(FocusState.Programmatic);
    }

    private void OnTransitionAuditionKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Handled || e.Key != VirtualKey.Escape || TransitionAuditionPopup?.IsOpen != true) return;
        DismissTransitionAudition();
        OpenTransitionAuditionButton.Focus(FocusState.Keyboard);
        e.Handled = true;
    }

    private void OnTransitionAuditionPopupClosed(object sender, object args) => PauseTransitionAudition();

    private void DismissTransitionAudition()
    {
        PauseTransitionAudition();
        if (TransitionAuditionPopup is not null) TransitionAuditionPopup.IsOpen = false;
    }

    private void OpenTransitionAuditionPopup()
    {
        if (!_transitionAuditionInitialized || XamlRoot is null || TransitionsPanel.Visibility != Visibility.Visible) return;
        TransitionAuditionPopup.XamlRoot = XamlRoot;
        PositionTransitionAuditionPopup();
        TransitionAuditionPopup.IsOpen = true;
        // The first measure can precede the popup's control templates. Re-clamp
        // after their layout without moving focus away from the preset gallery.
        DispatcherQueue.TryEnqueue(() =>
        {
            if (TransitionAuditionPopup.IsOpen) PositionTransitionAuditionPopup();
        });
    }

    private void PositionTransitionAuditionPopup()
    {
        if (XamlRoot?.Content is not UIElement root || ActualWidth <= 0 || ActualHeight <= 0) return;
        const double inset = 12;
        var width = Math.Min(280, Math.Max(120, ActualWidth - inset * 2));
        TransitionAuditionCard.Width = width;
        TransitionAuditionCard.MaxHeight = Math.Max(120, ActualHeight - inset * 2);
        TransitionAuditionCard.Measure(new Size(width, TransitionAuditionCard.MaxHeight));
        var height = Math.Max(1, TransitionAuditionCard.DesiredSize.Height);
        var anchor = TransitionsPanel.TransformToVisual(this).TransformPoint(new Point());
        var left = Math.Clamp(anchor.X - width - inset, inset, Math.Max(inset, ActualWidth - width - inset));
        var top = Math.Clamp(anchor.Y, inset, Math.Max(inset, ActualHeight - height - inset));
        var position = TransformToVisual(root).TransformPoint(new Point(left, top));
        TransitionAuditionPopup.HorizontalOffset = position.X;
        TransitionAuditionPopup.VerticalOffset = position.Y;
    }

    // Palette clicks audition only. In particular, they must never select a
    // different clip, seek a decoder, move the playhead or create Undo history.
    private void SelectTransitionAudition(string style, bool play = true)
    {
        if (style is not ("cut" or "dissolve" or "fadeblack" or "fadewhite" or
            "wipeleft" or "wiperight" or "wipeup" or "wipedown" or
            "slideleft" or "slideright" or "slideup" or "slidedown" or
            "circleopen" or "rectcrop" or "horzopen" or "vertopen" or "diagtl"))
            return;
        PauseTransitionAudition();
        _transitionAuditionStyle = style;
        RememberTransitionVariant(style);
        _transitionAuditionElapsed = 0;
        SyncTransitionAuditionControls();
        OpenTransitionAuditionPopup();
        RenderTransitionAudition();
        if (play) StartTransitionAudition();
    }

    private void StartTransitionAudition()
    {
        if (!_transitionAuditionInitialized || _transitionAuditionPlaying ||
            TransitionAuditionPopup.IsOpen != true || TransitionsPanel.Visibility != Visibility.Visible || !TransitionsPanel.IsLoaded)
            return;
        if (_transitionAuditionElapsed >= EditorTransitionAuditionMath.CycleSeconds(_transitionAuditionDurationSeconds))
            _transitionAuditionElapsed = 0;
        _transitionAuditionStartedAt = TransitionAuditionNow;
        _transitionAuditionPlaying = true;
        _transitionAuditionTimer?.Start();
        SyncTransitionAuditionPlayButton();
    }

    private void PauseTransitionAudition()
    {
        if (_transitionAuditionPlaying)
            _transitionAuditionElapsed += Math.Max(0, TransitionAuditionNow - _transitionAuditionStartedAt);
        _transitionAuditionPlaying = false;
        _transitionAuditionTimer?.Stop();
        SyncTransitionAuditionPlayButton();
    }

    private void OnTransitionAuditionTick(DispatcherQueueTimer sender, object args)
    {
        if (!_transitionAuditionPlaying) return;
        if (!TransitionAuditionPopup.IsOpen || TransitionsPanel.Visibility != Visibility.Visible || !TransitionsPanel.IsLoaded)
        {
            PauseTransitionAudition();
            return;
        }
        var cycle = EditorTransitionAuditionMath.CycleSeconds(_transitionAuditionDurationSeconds);
        var elapsed = _transitionAuditionElapsed + Math.Max(0, TransitionAuditionNow - _transitionAuditionStartedAt);
        if (elapsed >= cycle)
        {
            if (TransitionAuditionLoopButton.IsChecked == true)
            {
                _transitionAuditionElapsed = elapsed % cycle;
                _transitionAuditionStartedAt = TransitionAuditionNow;
                elapsed = _transitionAuditionElapsed;
            }
            else
            {
                PauseTransitionAudition();
                _transitionAuditionElapsed = elapsed = cycle;
            }
        }
        RenderTransitionAudition(elapsed);
    }

    private void OnTransitionAuditionPlayClicked(object sender, RoutedEventArgs e)
    {
        if (_transitionAuditionPlaying) PauseTransitionAudition();
        else StartTransitionAudition();
    }

    private void OnTransitionAuditionProgressChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_syncingTransitionAudition || !_transitionAuditionInitialized) return;
        PauseTransitionAudition();
        _transitionAuditionElapsed = EditorTransitionAuditionMath.HoldSeconds +
            Math.Clamp(e.NewValue / 100, 0, 1) * _transitionAuditionDurationSeconds;
        RenderTransitionAudition();
    }

    private void OnTransitionAuditionDurationChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingTransitionAudition || TransitionAuditionDurationComboBox?.SelectedItem is not ComboBoxItem { Tag: string value } ||
            !double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var duration))
            return;
        var playing = _transitionAuditionPlaying;
        PauseTransitionAudition();
        _transitionAuditionDurationSeconds = Math.Clamp(duration, 0.1, 2);
        _transitionAuditionElapsed = 0;
        RenderTransitionAudition();
        if (playing) StartTransitionAudition();
    }

    private void RenderTransitionAudition(double? elapsedSeconds = null)
    {
        if (!_transitionAuditionInitialized || TransitionAuditionOutgoing is null) return;
        const double width = 256, height = 144;
        var progress = EditorTransitionAuditionMath.ProgressAt(elapsedSeconds ?? _transitionAuditionElapsed, _transitionAuditionDurationSeconds);
        var frame = EditorTransitionAuditionMath.Evaluate(_transitionAuditionStyle, progress);
        TransitionAuditionOutgoing.Opacity = frame.OutgoingOpacity;
        TransitionAuditionIncoming.Opacity = frame.IncomingOpacity;
        TransitionAuditionWhiteOverlay.Opacity = frame.WhiteOpacity;
        TransitionAuditionOutgoingTransform.X = frame.OutgoingX * width;
        TransitionAuditionOutgoingTransform.Y = frame.OutgoingY * height;
        TransitionAuditionIncomingTransform.X = frame.IncomingX * width;
        TransitionAuditionIncomingTransform.Y = frame.IncomingY * height;
        TransitionAuditionRevealGeometry.Rect = new Rect(frame.RevealX * width, frame.RevealY * height,
            frame.RevealWidth * width, frame.RevealHeight * height);
        ApplyTransitionShapeMask(TransitionAuditionShapeHost, frame, width, height);
        _syncingTransitionAudition = true;
        try { TransitionAuditionProgressSlider.Value = progress * 100; }
        finally { _syncingTransitionAudition = false; }
    }

    private void SyncTransitionAuditionPlayButton()
    {
        if (TransitionAuditionPlayButton is null) return;
        TransitionAuditionPlayText.Text = _transitionAuditionPlaying ? "Pause" : "Play";
        TransitionAuditionPlayIcon.Glyph = _transitionAuditionPlaying ? "\uE769" : "\uE768";
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(TransitionAuditionPlayButton,
            _transitionAuditionPlaying ? "Pause transition sample" : "Play transition sample");
    }

    private void SyncTransitionAuditionControls()
    {
        if (TransitionAuditionNameText is null || BasicTransitionsPalette is null || LayerTimeline is null) return;
        var name = GetTransitionDisplayName(_transitionAuditionStyle);
        TransitionAuditionNameText.Text = name;
        OpenTransitionAuditionText.Text = $"Preview {name}";
        TransitionAuditionDurationComboBox.IsEnabled = _transitionAuditionStyle != "cut";
        SyncTransitionDirectionPicker(TransitionAuditionDirectionComboBox, _transitionAuditionStyle);
        foreach (var button in BasicTransitionsPalette.Children.OfType<Button>().Concat(MotionTransitionsPalette.Children.OfType<Button>()))
            SetOptionButtonState(button, button.Tag as string == GetTransitionFamily(_transitionAuditionStyle));
        var available = TryGetSelectedTransitionContext(out var incoming, out var outgoing, out _);
        var locked = available && (_project?.GetTrack(incoming.TrackId)?.Locked == true || _project?.GetTrack(outgoing.TrackId)?.Locked == true);
        ApplyTransitionAuditionButton.IsEnabled = available && !locked && !_isLoadingClip;
        TransitionAuditionApplyHintText.Text = locked
            ? "Unlock the video track to apply this transition."
            : available ? $"Applies before {incoming.Name}." : "Select a video clip after a join to apply.";
        SyncTransitionAuditionPlayButton();
    }

    private void OnApplyTransitionAuditionClicked(object sender, RoutedEventArgs e)
    {
        if (_isLoadingClip || _suppressProjectChanges) return;
        if (!TryGetSelectedTransitionContext(out var incoming, out var outgoing, out var overlap))
        {
            ProjectStatusText.Text = GetTransitionSelectionPrompt();
            SyncTransitionAuditionControls();
            return;
        }
        if (_project?.GetTrack(incoming.TrackId)?.Locked == true || _project?.GetTrack(outgoing.TrackId)?.Locked == true)
        {
            ProjectStatusText.Text = "Unlock the video track to apply this transition";
            SyncTransitionAuditionControls();
            return;
        }
        // A real no-op must leave running playback and pending history alone.
        // Inherited styles still become explicit when the user presses Apply;
        // a touching/partly overlapped join still needs its missing handles.
        if (LayerTimeline.TryGetSelectedMediaClipTransition(out var currentStyle, out var currentDuration, out var inherited) &&
            !inherited && string.Equals(currentStyle, _transitionAuditionStyle, StringComparison.Ordinal) &&
            (_transitionAuditionStyle == "cut" ||
             Math.Abs(currentDuration - _transitionAuditionDurationSeconds) < 0.001 &&
             Math.Abs(overlap - _transitionAuditionDurationSeconds) < 0.001))
        {
            ProjectStatusText.Text = "This transition is already applied to the selected join";
            return;
        }
        CommitPendingProjectChange();
        PausePreview();
        if (LayerTimeline.SetSelectedMediaClipTransition(_transitionAuditionStyle, _transitionAuditionDurationSeconds))
        {
            _duration = LayerTimeline.Duration;
            ProjectStatusText.Text = "Transition applied to selected join";
            QueueProjectChange();
            CommitPendingProjectChange();
            RefreshTransitionEditPreview();
            DismissTransitionAudition();
        }
        else
            ProjectStatusText.Text = !string.IsNullOrWhiteSpace(LayerTimeline.LastEditStatus)
                ? LayerTimeline.LastEditStatus : "This transition could not be applied to the selected join";
        SyncTransitionAuditionControls();
    }
}
