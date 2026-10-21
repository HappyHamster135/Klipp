using System;
using System.Diagnostics;
using Klipp.Core.Playback;
using Microsoft.UI.Xaml;

namespace Klipp.Desktop.Controls;

public sealed partial class SafeClipEditorView
{
    private DispatcherTimer? _reviewShuttleTimer;
    private double _reviewShuttleRate;
    private double _reviewShuttleOrigin;
    private long _reviewShuttleStarted;

    private void StartReviewShuttle(int direction)
    {
        if (_project is null || _duration <= TimeSpan.Zero) return;
        PauseSourceViewer();
        var previous = _reviewShuttleRate != 0 ? _reviewShuttleRate : _isPreviewPlaying ? 1 : 0;
        var rate = PlaybackReviewMath.NextRate(previous, direction);
        PausePreview();
        if (rate == 1)
        {
            // Ordinary forward review retains full-rate video, sound and transitions.
            TogglePlayPause();
            _reviewShuttleRate = 1;
            return;
        }

        _reviewShuttleRate = rate;
        _reviewShuttleOrigin = Math.Clamp(_previewPosition.TotalSeconds,
            LayerTimeline.Start.TotalSeconds, LayerTimeline.End.TotalSeconds);
        _reviewShuttleStarted = Stopwatch.GetTimestamp();
        _reviewShuttleTimer ??= CreateReviewShuttleTimer();
        _reviewShuttleTimer.Start();
        UpdatePlaybackUi();
    }

    private DispatcherTimer CreateReviewShuttleTimer()
    {
        // Review seeking is silent and bounded to one outstanding primary decoder seek.
        // It works across cuts, gaps and clip speeds without changing the edit itself.
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(80) };
        timer.Tick += (_, _) =>
        {
            if (_project is null || _reviewShuttleRate == 0)
            {
                StopReviewShuttle();
                return;
            }
            if (IsPrimaryPreviewDecoderBlocked()) return;
            var elapsed = (Stopwatch.GetTimestamp() - _reviewShuttleStarted) / (double)Stopwatch.Frequency;
            var start = LayerTimeline.Start.TotalSeconds;
            var end = LayerTimeline.End.TotalSeconds;
            var position = PlaybackReviewMath.Advance(_reviewShuttleOrigin, elapsed, _reviewShuttleRate, start, end);
            SeekTo(TimeSpan.FromSeconds(position));
            if (_reviewShuttleRate < 0 && position <= start || _reviewShuttleRate > 0 && position >= end)
            {
                StopReviewShuttle();
                UpdatePlaybackUi();
            }
        };
        return timer;
    }

    private void StopReviewShuttle()
    {
        _reviewShuttleTimer?.Stop();
        _reviewShuttleRate = 0;
    }
}
