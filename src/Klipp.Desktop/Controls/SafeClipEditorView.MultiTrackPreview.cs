using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Klipp.Desktop.Models;
using Klipp.Core.Editing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.Media.Core;
using Windows.Media.Playback;

namespace Klipp.Desktop.Controls;

public sealed partial class SafeClipEditorView
{
    private readonly Dictionary<string, AdditionalVideoRowPreview> _additionalVideoRows = new(StringComparer.Ordinal);
    private AdditionalVideoClipPreview? _highestOverlayOutgoing;

    private sealed class AdditionalVideoClipPreview : IDisposable
    {
        private readonly SafeClipEditorView _owner;
        public Grid Host { get; } = new() { RenderTransformOrigin = new Point(0.5, 0.5), IsHitTestVisible = false };
        public Grid MotionHost { get; } = new() { IsHitTestVisible = false };
        public Grid RevealHost { get; } = new() { IsHitTestVisible = false };
        public Grid ShapeHost { get; } = new() { IsHitTestVisible = false };
        public CompositeTransform Motion { get; } = new();
        private readonly CompositeTransform _transform = new();
        public RectangleGeometry Reveal { get; } = new();
        private readonly RectangleGeometry _crop = new();
        private readonly Grid _effectHost = new() { IsHitTestVisible = false };
        private readonly MediaPlayerElement _element = new() { AutoPlay = false, AreTransportControlsEnabled = false, Stretch = Stretch.Uniform, IsHitTestVisible = false };
        public MediaPlayer Player { get; } = new() { AutoPlay = false, IsMuted = true, Volume = 0, IsVideoFrameServerEnabled = true };
        public EditorProjectClip? Clip { get; private set; }
        private TimeSpan _pendingPosition;
        private string? _sourcePath;
        private string? _effectSignature;
        private ClipEffectPreviewResources? _effects;
        private bool _disposed;
        private bool _opening;
        private bool _failed;
        private double _openingAt;
        private long _sourceVersion;
        private readonly EditorPreviewSeekState _seek = new();

        public bool IsDecoderBlocked
        {
            get
            {
                if (_disposed || _failed) return false;
                var session = Player.PlaybackSession;
                var pending = _seek.IsPending(session.Position.TotalSeconds, PreviewNowSeconds);
                if (_seek.TakeTimeout() || _opening && PreviewNowSeconds - _openingAt >= 3)
                {
                    FailPreview();
                    return false;
                }
                return _opening || session.PlaybackState is MediaPlaybackState.Opening or MediaPlaybackState.Buffering || pending;
            }
        }

        public AdditionalVideoClipPreview(SafeClipEditorView owner)
        {
            _owner = owner;
            _element.SetMediaPlayer(Player);
            Host.RenderTransform = _transform;
            var cropHost = new Grid { Clip = _crop, IsHitTestVisible = false };
            cropHost.Children.Add(_element);
            Host.Children.Add(cropHost);
            Host.Children.Add(_effectHost);
            MotionHost.RenderTransform = Motion;
            MotionHost.Children.Add(Host);
            RevealHost.Clip = Reveal;
            ShapeHost.Children.Add(MotionHost);
            RevealHost.Children.Add(ShapeHost);
            Player.MediaOpened += OnOpened;
            Player.MediaFailed += OnFailed;
            Player.PlaybackSession.SeekCompleted += OnSeekCompleted;
        }

        public void Synchronize(EditorProjectClip clip, TimeSpan position, bool seek)
        {
            var previous = Clip;
            var changed = previous?.Id != clip.Id;
            var path = _owner.ResolvePreviewMediaPath(clip.SourcePath);
            var continuous = changed && _owner.CanContinueSecondarySource(previous, clip, _sourcePath, path,
                Player, _seek, _opening || _failed, seek);
            Clip = clip;
            _pendingPosition = _owner.GetSourcePosition(clip, position);
            if (!string.Equals(path, _sourcePath, StringComparison.OrdinalIgnoreCase))
            {
                ClearEffects();
                _sourceVersion++;
                _seek.Reset();
                _opening = true;
                _failed = false;
                _openingAt = PreviewNowSeconds;
                Host.Visibility = Visibility.Visible;
                _sourcePath = path;
                Player.Pause();
                Player.IsVideoFrameServerEnabled = true;
                Player.Source = MediaSource.CreateFromUri(new Uri(path));
            }
            else if (!_opening && !_failed)
                ApplyPlayback(seek || changed && !continuous);
        }

        private void OnOpened(MediaPlayer sender, object args)
        {
            var version = _sourceVersion;
            _owner.DispatcherQueue.TryEnqueue(() =>
            {
                if (_disposed || _failed || version != _sourceVersion || Player.Source is null) return;
                _opening = false;
                ApplyPlayback(true);
                UpdateTransform();
                UpdateEffects();
                _owner.UpdateTimelineFxPreviewAdorner();
            });
        }

        private void OnSeekCompleted(MediaPlaybackSession session, object args)
        {
            var version = _sourceVersion;
            _owner.DispatcherQueue.TryEnqueue(() =>
            {
                if (_disposed || _failed || _opening || version != _sourceVersion || Player.Source is null) return;
                _seek.IsPending(session.Position.TotalSeconds, PreviewNowSeconds, seekCompleted: true);
                UpdateTransform();
                UpdateEffects();
                _owner.UpdateTimelineFxPreviewAdorner();
            });
        }

        private void OnFailed(MediaPlayer sender, MediaPlayerFailedEventArgs args)
        {
            var version = _sourceVersion;
            _owner.DispatcherQueue.TryEnqueue(() => { if (!_disposed && version == _sourceVersion) FailPreview(); });
        }

        private void FailPreview()
        {
            _opening = false; _failed = true; _seek.Reset();
            Host.Visibility = Visibility.Collapsed;
            Pause(); ClearEffects();
            _owner.ProjectStatusText.Text = "A video layer could not be previewed. Other tracks will continue.";
        }

        private void ApplyPlayback(bool seek)
        {
            try
            {
                Player.IsMuted = true;
                Player.PlaybackSession.PlaybackRate = Clip?.PlaybackSpeed ?? 1;
                if (seek || Math.Abs((Player.PlaybackSession.Position - _pendingPosition).TotalSeconds) > 0.18)
                    RequestDecoderSeek(Player, _seek, _pendingPosition, force: seek);
                if (_owner._isPreviewPlaying && !_owner._wasPreviewBuffering)
                {
                    if (Player.PlaybackSession.PlaybackState != MediaPlaybackState.Playing) Player.Play();
                }
                else Player.Pause();
            }
            catch { /* The next opened/seek notification retries decoder state. */ }
        }

        public void UpdateTransform()
        {
            if (Clip is not null) Clip = _owner.LayerTimeline.FindMediaClipById(Clip.Id) ?? Clip;
            _owner.ApplyClipPreviewTransform(Host, _transform, _crop, Clip);
            Reveal.Rect = new Rect(0, 0, Math.Max(1, _owner.PreviewSurface.ActualWidth), Math.Max(1, _owner.PreviewSurface.ActualHeight));
            if (_effectHost.Clip is not RectangleGeometry geometry || geometry.Rect != _crop.Rect)
                _effectHost.Clip = new RectangleGeometry { Rect = _crop.Rect };
        }

        public void UpdateEffects()
        {
            if (Clip is not null) Clip = _owner.LayerTimeline.FindMediaClipById(Clip.Id) ?? Clip;
            if (_disposed || _opening || _failed || Clip is not { } clip) return;
            var local = _owner.LayerTimeline.GetMediaClipLocalTime(clip.Id, _owner.CurrentTimelinePosition);
            var strength = _owner.IsShowingOriginal(clip) ? 0 : clip.EvaluateFilterStrength(local);
            if (!_owner.HasPreviewEffects(clip))
            {
                ClearEffects();
                return;
            }
            var signature = _owner.BuildPreviewEffectSignature(clip);
            if (KeepNativePreviewFallback(Player, signature, ref _effectSignature)) return;
            if (_effectSignature == "fallback|" + signature) return;
            if (_effects is not null)
            {
                _effects.Update(clip, strength, _owner.GetPreviewTimelineEffectLayers(clip),
                    _owner.CurrentTimelinePosition.TotalSeconds, _owner.IsShowingOriginal(clip), signature);
                _effectSignature = signature;
                return;
            }
            ClearEffects();
            try
            {
                _effects = new ClipEffectPreviewResources(Player, _effectHost, clip, strength,
                    _owner.GetPreviewTimelineEffectLayers(clip), _owner.CurrentTimelinePosition.TotalSeconds,
                    _owner.IsShowingOriginal(clip), signature, _owner.DispatcherQueue,
                    (failedSignature, exception) =>
                    {
                        ClearEffects(); _effectSignature = "fallback|" + failedSignature;
                        RestoreNativePreviewAfterFrameFailure(Player);
                        _owner.WarnTimelineEffectPreviewUnavailable(clip);
                    });
                _effectSignature = signature;
            }
            catch
            {
                ClearEffects(); _effectSignature = "fallback|" + signature;
                RestoreNativePreviewAfterFrameFailure(Player);
                _owner.WarnTimelineEffectPreviewUnavailable(clip);
            }
        }

        public void Pause() { try { Player.Pause(); } catch { } }
        private void ClearEffects()
        {
            _effects?.Dispose();
            _effects = null;
            _effectSignature = null;
            _effectHost.Children.Clear();
        }
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Player.MediaOpened -= OnOpened;
            Player.MediaFailed -= OnFailed;
            Player.PlaybackSession.SeekCompleted -= OnSeekCompleted;
            ClearTransitionShapeMask(ShapeHost);
            ClearEffects();
            try { Player.Pause(); Player.Source = null; _element.SetMediaPlayer(null); } catch { }
            Player.Dispose();
        }
    }

    private sealed class AdditionalVideoRowPreview : IDisposable
    {
        public Grid Host { get; } = new() { IsHitTestVisible = false };
        public AdditionalVideoClipPreview Incoming { get; }
        public AdditionalVideoClipPreview? Outgoing { get; private set; }
        public Border Dip { get; } = new() { Visibility = Visibility.Collapsed, IsHitTestVisible = false };
        private readonly SafeClipEditorView _owner;
        public AdditionalVideoRowPreview(SafeClipEditorView owner)
        {
            _owner = owner;
            Incoming = new(owner);
            Host.Children.Add(Incoming.RevealHost);
            Host.Children.Add(Dip);
        }
        public void Synchronize(EditorProjectClip incoming, EditorProjectClip? outgoing, PreviewTransition? transition, TimeSpan position, bool seek)
        {
            Incoming.Synchronize(incoming, position, seek);
            if (outgoing is null)
            {
                if (Outgoing is not null) { Host.Children.Remove(Outgoing.RevealHost); Outgoing.Dispose(); Outgoing = null; }
            }
            else
            {
                if (Outgoing is null) { Outgoing = new(_owner); Host.Children.Insert(0, Outgoing.RevealHost); }
                Outgoing.Synchronize(outgoing, position, seek);
            }
            _owner.ApplyAdditionalTransition(Incoming.Host, Incoming.ShapeHost, Incoming.Reveal, Incoming.Motion,
                Outgoing?.Host, Outgoing?.Motion, Dip, transition);
        }
        public void Dispose() { Incoming.Dispose(); Outgoing?.Dispose(); Host.Children.Clear(); }
    }

    private PreviewTransition? GetTrackPreviewTransition(EditorProjectClip incoming, TimeSpan position)
    {
        if (_project?.GetTransitionPredecessor(incoming) is not { } outgoing || !File.Exists(outgoing.SourcePath)) return null;
        var duration = _project.GetTransitionOverlapSeconds(outgoing, incoming);
        var local = position.TotalSeconds - incoming.TimelineStartSeconds;
        return duration > 0.001 && local >= 0 && local < duration
            ? new PreviewTransition(outgoing, incoming, _project.GetTransitionStyle(incoming), local / duration) : null;
    }

    private void UpdateMultiTrackOverlayPreview(TimeSpan position, bool seekExisting)
    {
        if (_project is null) { DisposeAdditionalVideoTrackPreviews(); return; }
        var clips = _project.Clips;
        var fixedOverlayTrack = _project.Tracks.Where(track => track.Kind == EditorTimelineTrackKind.Video &&
            track.Order > 0 && _project.IsTrackActive(track.Id)).OrderBy(track => track.Order).LastOrDefault();
        var rows = _project.Tracks.Where(track => track.Kind == EditorTimelineTrackKind.Video &&
                track.Order > 0 && _project.IsTrackActive(track.Id)).OrderBy(track => track.Order)
            .Select(track => (Track: track, Clip: clips.Where(clip => clip.TrackId == track.Id &&
                position.TotalSeconds >= clip.TimelineStartSeconds &&
                position.TotalSeconds < clip.TimelineStartSeconds + clip.EffectiveDurationSeconds)
                .OrderBy(clip => clip.TimelineStartSeconds).ThenBy(clip => clip.Order).LastOrDefault()))
            .Where(row => row.Clip is not null && File.Exists(row.Clip.SourcePath)).ToList();
        var highest = rows.FirstOrDefault(row => row.Track.Id == fixedOverlayTrack?.Id).Clip;
        SynchronizeHighestOverlay(highest, position, seekExisting);

        var visible = new HashSet<string>(StringComparer.Ordinal);
        var visualOrder = 0;
        foreach (var row in rows.Where(row => row.Track.Id != fixedOverlayTrack?.Id))
        {
            visible.Add(row.Track.Id);
            if (!_additionalVideoRows.TryGetValue(row.Track.Id, out var state))
            {
                state = new AdditionalVideoRowPreview(this);
                _additionalVideoRows.Add(row.Track.Id, state);
            }
            PlaceVideoPreviewVisual(state.Host, visualOrder++);
            var transition = GetTrackPreviewTransition(row.Clip!, position);
            state.Synchronize(row.Clip!, transition?.OutgoingClip, transition, position, seekExisting);
        }
        foreach (var id in _additionalVideoRows.Keys.Where(id => !visible.Contains(id)).ToList())
        {
            var state = _additionalVideoRows[id];
            AdditionalVideoTrackPreviewCanvas.Children.Remove(state.Host);
            state.Dispose();
            _additionalVideoRows.Remove(id);
        }
        var highestTransition = highest is null ? null : GetTrackPreviewTransition(highest, position);
        if (highestTransition is not null)
        {
            _highestOverlayOutgoing ??= new AdditionalVideoClipPreview(this);
            PlaceVideoPreviewVisual(_highestOverlayOutgoing.RevealHost, visualOrder);
            _highestOverlayOutgoing.Synchronize(highestTransition.OutgoingClip, position, seekExisting);
        }
        else if (_highestOverlayOutgoing is not null)
        {
            AdditionalVideoTrackPreviewCanvas.Children.Remove(_highestOverlayOutgoing.RevealHost);
            _highestOverlayOutgoing.Dispose();
            _highestOverlayOutgoing = null;
        }
        ApplyAdditionalTransition(OverlayPreviewHost, OverlayTrackShapeHost, OverlayTrackRevealGeometry, OverlayTrackMotionTransform,
            _highestOverlayOutgoing?.Host, _highestOverlayOutgoing?.Motion, OverlayTrackDipOverlay, highestTransition);
    }

    private void PlaceVideoPreviewVisual(UIElement visual, int index)
    {
        var children = AdditionalVideoTrackPreviewCanvas.Children;
        var current = children.IndexOf(visual);
        if (current == index) return;
        if (current >= 0) children.RemoveAt(current);
        children.Insert(Math.Min(index, children.Count), visual);
    }

    private void SynchronizeHighestOverlay(EditorProjectClip? overlay, TimeSpan position, bool seek)
    {
        OverlayPreviewHost.Visibility = overlay is null || _overlayPreviewFailed ? Visibility.Collapsed : Visibility.Visible;
        if (overlay is null)
        {
            if (_activeOverlayPreviewClipId is not null) { try { OverlayPreviewPlayer.Source = null; } catch { } }
            _activeOverlayPreviewClipId = _overlayPreviewSourcePath = null;
            _overlaySourceVersion++;
            _overlayPreviewOpening = _overlayPreviewFailed = false;
            _overlayDecoderSeek.Reset();
            ClearClipEffectPreview(OverlayEffectPreviewHost);
            _overlayEffectPreviewSignature = null;
            return;
        }
        var path = ResolvePreviewMediaPath(overlay.SourcePath);
        var clipChanged = _activeOverlayPreviewClipId != overlay.Id;
        var changed = !string.Equals(path, _overlayPreviewSourcePath, StringComparison.OrdinalIgnoreCase);
        var continuous = clipChanged && CanContinueSecondarySource(LayerTimeline.FindMediaClipById(_activeOverlayPreviewClipId),
            overlay, _overlayPreviewSourcePath, path, OverlayPreviewPlayer.MediaPlayer,
            _overlayDecoderSeek, _overlayPreviewOpening || _overlayPreviewFailed, seek);
        _activeOverlayPreviewClipId = overlay.Id;
        _overlayPreviewSourcePath = path;
        _pendingOverlaySourcePosition = GetSourcePosition(overlay, position);
        if (changed)
        {
            EnsureOverlayFailureEvents();
            _overlaySourceVersion++;
            _overlayPreviewOpening = true;
            _overlayPreviewFailed = false;
            _overlayOpeningAt = PreviewNowSeconds;
            _overlayDecoderSeek.Reset();
            OverlayPreviewHost.Visibility = Visibility.Visible;
            ClearClipEffectPreview(OverlayEffectPreviewHost);
            _overlayEffectPreviewSignature = null;
            try
            {
                OverlayPreviewPlayer.MediaPlayer.Pause();
                OverlayPreviewPlayer.MediaPlayer.IsVideoFrameServerEnabled = true;
                OverlayPreviewPlayer.Source = MediaSource.CreateFromUri(new Uri(path));
            }
            catch { _overlayPreviewOpening = false; _overlayPreviewFailed = true; OverlayPreviewHost.Visibility = Visibility.Collapsed; }
            return;
        }
        if (_overlayPreviewOpening || _overlayPreviewFailed) return;
        try
        {
            var player = OverlayPreviewPlayer.MediaPlayer;
            player.IsMuted = true;
            var session = player.PlaybackSession;
            session.PlaybackRate = overlay.PlaybackSpeed;
            var needsCutSeek = clipChanged && !continuous;
            if (seek || needsCutSeek || Math.Abs((session.Position - _pendingOverlaySourcePosition).TotalSeconds) > 0.18)
                RequestDecoderSeek(player, _overlayDecoderSeek, _pendingOverlaySourcePosition, force: seek || needsCutSeek);
            if (_isPreviewPlaying && !_wasPreviewBuffering)
            {
                if (session.PlaybackState != MediaPlaybackState.Playing) player.Play();
            }
            else player.Pause();
        }
        catch { /* MediaOpened applies the pending absolute source position. */ }
    }

    private void ApplyAdditionalTransition(Grid incoming, Grid revealHost, RectangleGeometry reveal, CompositeTransform incomingMotion,
        Grid? outgoing, CompositeTransform? outgoingMotion, Border dip, PreviewTransition? transition)
    {
        var width = Math.Max(1, PreviewSurface.ActualWidth > 0 ? PreviewSurface.ActualWidth : PreviewSurface.Width);
        var height = Math.Max(1, PreviewSurface.ActualHeight > 0 ? PreviewSurface.ActualHeight : PreviewSurface.Height);
        reveal.Rect = new Rect(0, 0, width, height);
        incomingMotion.TranslateX = incomingMotion.TranslateY = 0;
        if (outgoingMotion is not null) outgoingMotion.TranslateX = outgoingMotion.TranslateY = 0;
        incoming.Opacity = 1;
        if (outgoing is not null) outgoing.Opacity = 1;
        dip.Visibility = Visibility.Collapsed;
        if (transition is null || outgoing is null)
        {
            ClearTransitionShapeMask(revealHost);
            return;
        }
        var p = Math.Clamp(transition.Progress, 0, 1);
        switch (transition.Style)
        {
            case "fadeblack": case "fadewhite":
                outgoing.Opacity = p < 0.5 ? 1 : 0;
                incoming.Opacity = p < 0.5 ? 0 : 1;
                dip.Background = new SolidColorBrush(transition.Style == "fadewhite" ? Microsoft.UI.Colors.White : Microsoft.UI.Colors.Black);
                dip.Opacity = p < 0.5 ? p * 2 : (1 - p) * 2;
                dip.Visibility = Visibility.Visible;
                break;
            case "wipeleft": reveal.Rect = new Rect(width * (1-p), 0, width*p, height); break;
            case "wiperight": reveal.Rect = new Rect(0, 0, width*p, height); break;
            case "wipeup": reveal.Rect = new Rect(0, height * (1-p), width, height*p); break;
            case "wipedown": reveal.Rect = new Rect(0, 0, width, height*p); break;
            case "slideleft": case "slideright":
                var dx = transition.Style == "slideleft" ? -1 : 1;
                incomingMotion.TranslateX = -dx * width * (1-p);
                if (outgoingMotion is not null) outgoingMotion.TranslateX = dx * width*p;
                break;
            case "slideup": case "slidedown":
                var dy = transition.Style == "slideup" ? -1 : 1;
                incomingMotion.TranslateY = -dy * height * (1-p);
                if (outgoingMotion is not null) outgoingMotion.TranslateY = dy * height*p;
                break;
            case "circleopen": case "rectcrop": case "horzopen": case "vertopen": case "diagtl":
                var frame = EditorTransitionAuditionMath.Evaluate(transition.Style, p);
                incoming.Opacity = frame.IncomingOpacity;
                reveal.Rect = new Rect(frame.RevealX * width, frame.RevealY * height,
                    frame.RevealWidth * width, frame.RevealHeight * height);
                break;
            default: incoming.Opacity = p; break;
        }
        ApplyTransitionShapeMask(revealHost, EditorTransitionAuditionMath.Evaluate(transition.Style, p), width, height);
    }

    private void UpdateAdditionalVideoTrackTransforms()
    {
        foreach (var row in _additionalVideoRows.Values) { row.Incoming.UpdateTransform(); row.Outgoing?.UpdateTransform(); }
        _highestOverlayOutgoing?.UpdateTransform();
    }
    private void UpdateAdditionalVideoTrackEffects()
    {
        foreach (var row in _additionalVideoRows.Values) { row.Incoming.UpdateEffects(); row.Outgoing?.UpdateEffects(); }
        _highestOverlayOutgoing?.UpdateEffects();
    }
    private void PauseAdditionalVideoTrackPreviews()
    {
        foreach (var row in _additionalVideoRows.Values) { row.Incoming.Pause(); row.Outgoing?.Pause(); }
        _highestOverlayOutgoing?.Pause();
    }
    private void DisposeAdditionalVideoTrackPreviews()
    {
        foreach (var row in _additionalVideoRows.Values) row.Dispose();
        _additionalVideoRows.Clear();
        _highestOverlayOutgoing?.Dispose();
        _highestOverlayOutgoing = null;
        AdditionalVideoTrackPreviewCanvas?.Children.Clear();
        if (OverlayTrackDipOverlay is not null) OverlayTrackDipOverlay.Visibility = Visibility.Collapsed;
        if (OverlayTrackShapeHost is not null) ClearTransitionShapeMask(OverlayTrackShapeHost);
    }
}
