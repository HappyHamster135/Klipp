using System;
using System.Collections.Generic;
using Klipp.Core.Editing;
using Klipp.Desktop.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace Klipp.Desktop.Controls;

public sealed partial class LayerTimelineEditor
{
    private readonly Dictionary<string, AudioWaveformSpan> _audioWaveformSpans = new(StringComparer.Ordinal);
    private readonly Dictionary<string, AudioWaveformVisual> _audioWaveformVisuals = new(StringComparer.Ordinal);

    private sealed record AudioWaveformSpan(string SourcePath, double Start, double End);
    private sealed record AudioWaveformVisual(Canvas Viewport, Image Image);

    private void DrawIndependentAudioWaveform(EditorProjectAudioTrack track, double width, double height, double railWidth)
    {
        if (!_audioWaveformVisuals.TryGetValue(track.Id, out var visual))
            return;
        var border = _selectedAudioTrackIds.Contains(track.Id) ? 2 : 1;
        var innerWidth = Math.Max(0, width - border * 2);
        var innerHeight = Math.Max(0, height - border * 2);
        visual.Viewport.Width = innerWidth;
        visual.Viewport.Height = innerHeight;
        visual.Viewport.Clip = new RectangleGeometry { Rect = new Rect(0, 0, innerWidth, innerHeight) };
        if (!_audioWaveformSpans.TryGetValue(track.Id, out var span) ||
            !string.Equals(span.SourcePath, track.SourcePath, StringComparison.OrdinalIgnoreCase) ||
            !_audioTrackWaveforms.TryGetValue(track.Id, out var source) || source is null ||
            !EditorWaveformMath.TryPlace(track.TimelineStartSeconds, track.TrimStartSeconds, track.TrimEndSeconds,
                track.PlaybackSpeed, span.Start, span.End, VisibleStart.TotalSeconds,
                VisibleDuration.TotalSeconds, railWidth, out var placement))
        {
            visual.Image.Visibility = Visibility.Collapsed;
            return;
        }
        visual.Image.Source = source;
        visual.Image.Visibility = Visibility.Visible;
        visual.Image.Width = placement.Width;
        visual.Image.Height = innerHeight;
        Canvas.SetLeft(visual.Image, placement.Left - border);
    }

    private bool CanAcceptAudioWaveform(EditorProjectAudioTrack track, string sourcePath, double start, double end)
    {
        if (!string.Equals(track.SourcePath, sourcePath, StringComparison.OrdinalIgnoreCase) ||
            !double.IsFinite(start) || !double.IsFinite(end) || end <= start)
            return false;
        if (Math.Abs(track.TrimStartSeconds - start) <= .000001 && Math.Abs(track.TrimEndSeconds - end) <= .000001)
            return true;
        // A job already running when trimming starts may still supply the original
        // source interval. Its geometry stays correct while the handles move.
        return _dragAudioTrackId == track.Id &&
            _dragMode is DragMode.AudioTrimStart or DragMode.AudioTrimEnd or DragMode.AudioSlip &&
            Math.Abs(_dragAudioTrimStart - start) <= .000001 && Math.Abs(_dragAudioTrimEnd - end) <= .000001;
    }
}
