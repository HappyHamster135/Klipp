using System;
using System.Linq;
using Klipp.Core.Editing;
using Klipp.Desktop.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;

namespace Klipp.Desktop.Controls;

public sealed partial class SafeClipEditorView
{
    private double TimelineFxEditTime(EditorProjectEffectLayer effect) => Math.Clamp(
        EditorTimelineMath.SnapToFrame(CurrentTimelinePosition.TotalSeconds, _exportFrameRate),
        effect.StartSeconds, effect.EndSeconds);

    private EditorProjectEffectRegionKeyframe? CurrentTimelineFxRegionKeyframe(EditorProjectEffectLayer effect)
    {
        var time = TimelineFxEditTime(effect) - effect.StartSeconds;
        var tolerance = .5 / Math.Max(1, _exportFrameRate) + .000001;
        return effect.RegionKeyframes.OrderBy(key => Math.Abs(key.TimeSeconds - time))
            .FirstOrDefault(key => Math.Abs(key.TimeSeconds - time) <= tolerance);
    }

    private void SetTimelineFxRegion(EditorProjectEffectLayer effect, EditorEffectRegion region)
    {
        region = EditorEffectRegionMath.Normalize(region);
        if (effect.RegionKeyframes.Count > 0)
        {
            var key = CurrentTimelineFxRegionKeyframe(effect);
            var time = key is null ? TimelineFxEditTime(effect) : effect.StartSeconds + key.TimeSeconds;
            effect.UpsertRegionKeyframe(time, region);
        }
        else
        {
            effect.RegionX = region.X;
            effect.RegionY = region.Y;
            effect.RegionWidth = region.Width;
            effect.RegionHeight = region.Height;
        }
    }

    private void ApplyTimelineFxRegionEdit(Func<EditorEffectRegion, EditorEffectRegion> mutate,
        bool commit = true, bool syncControls = true) => ApplyTimelineFxEdit(effect =>
            SetTimelineFxRegion(effect, mutate(effect.EvaluateRegion(TimelineFxEditTime(effect)))), commit, syncControls);

    private void OnTimelineFxFeatherChanged(object sender, RangeBaseValueChangedEventArgs e) =>
        ApplyTimelineFxEdit(effect => effect.RegionFeather = e.NewValue / 100, commit: !_timelineFxAmountGesture);

    private void OnTimelineFxAnimateChanged(object sender, RoutedEventArgs e)
    {
        if (_syncingTimelineFx) return;
        ApplyTimelineFxEdit(effect =>
        {
            var time = TimelineFxEditTime(effect);
            var region = effect.EvaluateRegion(time);
            if (TimelineFxAnimateCheckBox.IsChecked == true)
            {
                effect.UpsertRegionKeyframe(effect.StartSeconds, region);
                if (time > effect.StartSeconds + .000001) effect.UpsertRegionKeyframe(time, region);
            }
            else
            {
                effect.RegionKeyframes.Clear();
                SetTimelineFxRegion(effect, region);
            }
        });
    }

    private void OnTimelineFxAddKeyframeClicked(object sender, RoutedEventArgs e) =>
        ApplyTimelineFxEdit(effect =>
        {
            var time = TimelineFxEditTime(effect);
            var key = CurrentTimelineFxRegionKeyframe(effect);
            effect.UpsertRegionKeyframe(key is null ? time : effect.StartSeconds + key.TimeSeconds,
                effect.EvaluateRegion(time));
        });

    private void OnTimelineFxRemoveKeyframeClicked(object sender, RoutedEventArgs e) =>
        ApplyTimelineFxEdit(effect =>
        {
            var key = CurrentTimelineFxRegionKeyframe(effect);
            if (key is null) return;
            var region = effect.EvaluateRegion(TimelineFxEditTime(effect));
            effect.RegionKeyframes.Remove(key);
            if (effect.RegionKeyframes.Count == 0) SetTimelineFxRegion(effect, region);
        });

    private void OnTimelineFxPreviousKeyframeClicked(object sender, RoutedEventArgs e) => NavigateTimelineFxKeyframe(false);
    private void OnTimelineFxNextKeyframeClicked(object sender, RoutedEventArgs e) => NavigateTimelineFxKeyframe(true);

    private void NavigateTimelineFxKeyframe(bool next)
    {
        var effect = LayerTimeline.GetSelectedEffectLayer();
        if (effect is null) return;
        var position = CurrentTimelinePosition.TotalSeconds;
        var tolerance = .5 / Math.Max(1, _exportFrameRate);
        var times = effect.RegionKeyframes.Select(key => effect.StartSeconds + key.TimeSeconds);
        var time = next ? times.Where(time => time > position + tolerance).OrderBy(time => time).Select(time => (double?)time).FirstOrDefault()
            : times.Where(time => time < position - tolerance).OrderByDescending(time => time).Select(time => (double?)time).FirstOrDefault();
        if (time is null) return;
        CommitPendingProjectChange();
        PausePreview();
        SeekTo(TimeSpan.FromSeconds(time.Value));
        SyncTimelineFxInspector();
    }

    private void SyncTimelineFxMotionControls(EditorProjectEffectLayer? effect = null)
    {
        if (TimelineFxAnimateCheckBox is null || LayerTimeline is null || _fxPreviewDragId is not null) return;
        effect ??= LayerTimeline.GetSelectedEffectLayer();
        var clip = effect is null ? null : LayerTimeline.FindMediaClipById(effect.TargetClipId);
        var editable = effect is not null && clip is not null && _project?.GetTrack(clip.TrackId)?.Locked != true;
        var animating = effect?.RegionKeyframes.Count > 0;
        var within = effect is not null && CurrentTimelinePosition.TotalSeconds >= effect.StartSeconds - .000001 &&
            CurrentTimelinePosition.TotalSeconds <= effect.EndSeconds + .000001;
        var previousSync = _syncingTimelineFx;
        _syncingTimelineFx = true;
        try
        {
            TimelineFxAnimateCheckBox.IsEnabled = editable;
            TimelineFxAnimateCheckBox.IsChecked = animating;
            TimelineFxMotionControls.Visibility = animating ? Visibility.Visible : Visibility.Collapsed;
            var current = effect is null ? null : CurrentTimelineFxRegionKeyframe(effect);
            TimelineFxAddKeyframeText.Text = current is null ? "Add keyframe" : "Update keyframe";
            TimelineFxAddKeyframeButton.IsEnabled = editable && within;
            TimelineFxRemoveKeyframeButton.IsEnabled = editable && within && current is not null;
            var tolerance = .5 / Math.Max(1, _exportFrameRate);
            TimelineFxPreviousKeyframeButton.IsEnabled = editable && effect!.RegionKeyframes.Any(key =>
                effect.StartSeconds + key.TimeSeconds < CurrentTimelinePosition.TotalSeconds - tolerance);
            TimelineFxNextKeyframeButton.IsEnabled = editable && effect!.RegionKeyframes.Any(key =>
                effect.StartSeconds + key.TimeSeconds > CurrentTimelinePosition.TotalSeconds + tolerance);
            var count = effect?.RegionKeyframes.Count ?? 0;
            TimelineFxKeyframeStatusText.Text = $"{count} {(count == 1 ? "keyframe" : "keyframes")}" +
                (!within && animating ? " · place the playhead inside the FX" : current is not null ? " · at keyframe" : "");
            if (effect is not null && !_isPreviewPlaying)
            {
                var region = effect.EvaluateRegion(TimelineFxEditTime(effect));
                TimelineFxXBox.Value = region.X * 100;
                TimelineFxYBox.Value = region.Y * 100;
                TimelineFxWidthBox.Value = region.Width * 100;
                TimelineFxHeightBox.Value = region.Height * 100;
            }
        }
        finally { _syncingTimelineFx = previousSync; }
    }
}
