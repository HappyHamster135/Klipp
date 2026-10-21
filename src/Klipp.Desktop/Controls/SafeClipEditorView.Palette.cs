using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.Foundation;
using Windows.System;

namespace Klipp.Desktop.Controls;

public sealed partial class SafeClipEditorView
{
    private string _activeLookPaletteCategory = "all";
    private string _activeTransitionPaletteCategory = "basic";
    private readonly Dictionary<Button, string> _presetPaletteFormats = new();
    private Button? _pressedPresetPaletteButton;
    private uint _presetPalettePointerId;
    private Point _presetPalettePressPoint;
    private bool _presetPalettePointerDragging;
    private bool _presetPalettePointerClickHandled;
    private DateTime _suppressPresetClickUntilUtc;

    private void OnLookPaletteLoaded(object sender, RoutedEventArgs e) => UpdateLookPaletteCategory();

    private void OnLookPaletteCategoryClicked(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: "all" or "color" or "finish" } element)
            return;
        _activeLookPaletteCategory = (string)element.Tag;
        UpdateLookPaletteCategory();
    }

    private void UpdateLookPaletteCategory()
    {
        SetOptionButtonState(AllLooksCategoryButton, _activeLookPaletteCategory == "all");
        SetOptionButtonState(ColorLooksCategoryButton, _activeLookPaletteCategory == "color");
        SetOptionButtonState(FinishLooksCategoryButton, _activeLookPaletteCategory == "finish");
        RefreshLookBrowser();
    }

    private void OnTransitionPaletteLoaded(object sender, RoutedEventArgs e) => UpdateTransitionPaletteCategory();

    private void OnTransitionPaletteCategoryClicked(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: "basic" or "motion" } element)
            return;
        _activeTransitionPaletteCategory = (string)element.Tag;
        UpdateTransitionPaletteCategory();
    }

    private void UpdateTransitionPaletteCategory()
    {
        var basic = _activeTransitionPaletteCategory == "basic";
        BasicTransitionsPalette.Visibility = basic ? Visibility.Visible : Visibility.Collapsed;
        MotionTransitionsPalette.Visibility = basic ? Visibility.Collapsed : Visibility.Visible;
        SetOptionButtonState(BasicTransitionsCategoryButton, basic);
        SetOptionButtonState(MotionTransitionsCategoryButton, !basic);
    }

    private void OnClipLookPaletteClicked(object sender, RoutedEventArgs e)
    {
        if (TryBeginPresetPaletteClick(sender) && sender is FrameworkElement { Tag: string look })
            ActivatePresetPaletteButton(LayerTimelineEditor.ClipLookDataFormat, look);
    }

    private bool TryBeginPresetPaletteClick(object sender)
    {
        if (_presetPalettePointerDragging || DateTime.UtcNow < _suppressPresetClickUntilUtc)
            return false;
        if (ReferenceEquals(sender, _pressedPresetPaletteButton))
        {
            if (_presetPalettePointerClickHandled) return false;
            _presetPalettePointerClickHandled = true;
        }
        return true;
    }

    private void ActivatePresetPaletteButton(string format, string value)
    {
        if (_isLoadingClip || _suppressProjectChanges) return;
        if (format == LayerTimelineEditor.TransitionDataFormat)
        {
            SelectTransitionAudition(ResolveTransitionFamilyStyle(value));
            return;
        }
        AddTimelineEffect(value);
    }

    private void OnTransitionPaletteClicked(object sender, RoutedEventArgs e)
    {
        if (TryBeginPresetPaletteClick(sender) && sender is FrameworkElement { Tag: string style })
            ActivatePresetPaletteButton(LayerTimelineEditor.TransitionDataFormat, style);
    }

    private void OnClipLookPaletteItemLoaded(object sender, RoutedEventArgs e) =>
        RegisterPresetPalettePointerHandlers(sender, LayerTimelineEditor.ClipLookDataFormat);

    private void OnTransitionPaletteItemLoaded(object sender, RoutedEventArgs e) =>
        RegisterPresetPalettePointerHandlers(sender, LayerTimelineEditor.TransitionDataFormat);

    private void RegisterPresetPalettePointerHandlers(object sender, string format)
    {
        if (sender is not Button button || !_presetPaletteFormats.TryAdd(button, format))
            return;

        // Button consumes the normal routed pointer events. Observe them even when
        // handled, then keep the gesture in-process so it also works when elevated.
        button.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler(OnPresetPalettePointerPressed), true);
        button.AddHandler(UIElement.PointerMovedEvent, new PointerEventHandler(OnPresetPalettePointerMoved), true);
        button.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler(OnPresetPalettePointerReleased), true);
        button.AddHandler(UIElement.PointerCanceledEvent, new PointerEventHandler(OnPresetPalettePointerCanceled), true);
        button.AddHandler(UIElement.PointerCaptureLostEvent, new PointerEventHandler(OnPresetPalettePointerCaptureLost), true);
        button.AddHandler(UIElement.KeyDownEvent, new KeyEventHandler(OnPresetPaletteKeyDown), true);
        button.Unloaded += OnPresetPaletteButtonUnloaded;
    }

    private void OnPresetPaletteButtonUnloaded(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button) return;
        if (ReferenceEquals(_pressedPresetPaletteButton, button)) CancelPresetPalettePointerDrag();
        _presetPaletteFormats.Remove(button);
        button.RemoveHandler(UIElement.PointerPressedEvent, new PointerEventHandler(OnPresetPalettePointerPressed));
        button.RemoveHandler(UIElement.PointerMovedEvent, new PointerEventHandler(OnPresetPalettePointerMoved));
        button.RemoveHandler(UIElement.PointerReleasedEvent, new PointerEventHandler(OnPresetPalettePointerReleased));
        button.RemoveHandler(UIElement.PointerCanceledEvent, new PointerEventHandler(OnPresetPalettePointerCanceled));
        button.RemoveHandler(UIElement.PointerCaptureLostEvent, new PointerEventHandler(OnPresetPalettePointerCaptureLost));
        button.RemoveHandler(UIElement.KeyDownEvent, new KeyEventHandler(OnPresetPaletteKeyDown));
        button.Unloaded -= OnPresetPaletteButtonUnloaded;
    }

    private void OnPresetPalettePointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_pressedPresetPaletteButton is not null || sender is not Button button ||
            !_presetPaletteFormats.ContainsKey(button) || _isLoadingClip)
        {
            return;
        }

        var point = e.GetCurrentPoint(button);
        if (!point.Properties.IsLeftButtonPressed)
            return;
        _suppressPresetClickUntilUtc = DateTime.MinValue;
        _pressedPresetPaletteButton = button;
        _presetPalettePointerId = e.Pointer.PointerId;
        _presetPalettePressPoint = point.Position;
        _presetPalettePointerDragging = false;
        _presetPalettePointerClickHandled = false;
        button.CapturePointer(e.Pointer);
    }

    private void OnPresetPalettePointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not Button button || !ReferenceEquals(button, _pressedPresetPaletteButton) ||
            e.Pointer.PointerId != _presetPalettePointerId)
        {
            return;
        }

        var point = e.GetCurrentPoint(button);
        if (!point.Properties.IsLeftButtonPressed)
        {
            CancelPresetPalettePointerDrag();
            return;
        }
        var deltaX = point.Position.X - _presetPalettePressPoint.X;
        var deltaY = point.Position.Y - _presetPalettePressPoint.Y;
        if (!_presetPalettePointerDragging)
        {
            if (deltaX * deltaX + deltaY * deltaY < 16)
                return;
            _presetPalettePointerDragging = true;
            button.Opacity = 0.65;
            DismissTransitionAudition();
            PausePreview();
            ProjectStatusText.Text = _presetPaletteFormats[button] == LayerTimelineEditor.TransitionDataFormat
                ? "Drag to a cut between clips"
                : "Drag onto a video clip";
        }

        LayerTimeline.PreviewPresetDrop(_presetPaletteFormats[button], e.GetCurrentPoint(LayerTimeline).Position);
        e.Handled = true;
    }

    private void OnPresetPalettePointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not Button button || !ReferenceEquals(button, _pressedPresetPaletteButton) ||
            e.Pointer.PointerId != _presetPalettePointerId)
        {
            return;
        }

        var dragging = _presetPalettePointerDragging;
        var clickHandled = _presetPalettePointerClickHandled;
        var localPoint = e.GetCurrentPoint(button).Position;
        var inside = localPoint.X >= 0 && localPoint.Y >= 0 &&
            localPoint.X <= button.ActualWidth && localPoint.Y <= button.ActualHeight;
        var point = e.GetCurrentPoint(LayerTimeline).Position;
        var format = _presetPaletteFormats[button];
        var value = button.Tag as string;
        if (format == LayerTimelineEditor.TransitionDataFormat && value is not null)
            value = ResolveTransitionFamilyStyle(value);
        // Applying can change the inspector and unload this button. Release all
        // gesture state first so capture loss cannot cancel a valid drop halfway.
        ClearPresetPalettePointerState();
        if (value is null)
            return;

        _suppressPresetClickUntilUtc = DateTime.UtcNow.AddMilliseconds(250);
        e.Handled = true;
        if (!dragging)
        {
            // Pointer capture can suppress Button.Click. Complete a released
            // click explicitly, while also accepting controls that raised it
            // before this routed release. Each gesture activates exactly once.
            if (inside && !clickHandled) ActivatePresetPaletteButton(format, value);
            return;
        }
        if (LayerTimeline.ApplyPresetDrop(format, value, point,
                format == LayerTimelineEditor.TransitionDataFormat ? _transitionAuditionDurationSeconds : null))
        {
            CommitPendingProjectChange();
            if (format == LayerTimelineEditor.ClipLookDataFormat) RevealSelectedTimelineEffect();
        }
        UpdatePresetPickers();
    }

    private void OnPresetPalettePointerCanceled(object sender, PointerRoutedEventArgs e)
    {
        if (ReferenceEquals(sender, _pressedPresetPaletteButton))
            CancelPresetPalettePointerDrag();
    }

    private void OnPresetPalettePointerCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        var button = _pressedPresetPaletteButton;
        if (!ReferenceEquals(sender, button))
            return;
        // Button releases capture in its class handler before the routed release
        // event reaches us. Let that release finish; unexpected loss still cancels.
        DispatcherQueue.TryEnqueue(() =>
        {
            if (ReferenceEquals(_pressedPresetPaletteButton, button))
                CancelPresetPalettePointerDrag();
        });
    }

    private void OnPresetPaletteKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Escape || _pressedPresetPaletteButton is null)
            return;
        CancelPresetPalettePointerDrag();
        e.Handled = true;
    }

    private void CancelPresetPalettePointerDrag()
    {
        var dragging = _presetPalettePointerDragging;
        ClearPresetPalettePointerState();
        _suppressPresetClickUntilUtc = DateTime.UtcNow.AddMilliseconds(250);
        if (!dragging)
            return;
        ProjectStatusText.Text = "Drag cancelled";
    }

    private void ClearPresetPalettePointerState()
    {
        var button = _pressedPresetPaletteButton;
        _pressedPresetPaletteButton = null;
        _presetPalettePointerId = 0;
        _presetPalettePointerDragging = false;
        _presetPalettePointerClickHandled = false;
        LayerTimeline.ClearPresetDropPreview();
        if (button is null)
            return;
        button.Opacity = 1;
        button.ReleasePointerCaptures();
    }
}
