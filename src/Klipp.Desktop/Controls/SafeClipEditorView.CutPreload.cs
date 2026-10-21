using System;
using System.IO;
using System.Linq;
using Klipp.Core.Editing;
using Klipp.Desktop.Models;
using Klipp.Desktop.Services;
using Microsoft.UI.Xaml;
using Windows.Media.Core;
using Windows.Media.Playback;

namespace Klipp.Desktop.Controls;

public sealed partial class SafeClipEditorView
{
    private PreparedPreviewCut? _preparedPreviewCut;
    private readonly EditorPreviewRefreshGate _cutPreparationGate = new(0.15);

    private sealed class PreparedPreviewCut : IDisposable
    {
        private readonly SafeClipEditorView _owner;
        private readonly EditorPreviewSeekState _seek = new();
        private readonly double _openedAt = PreviewNowSeconds;
        private bool _opening = true;
        private bool _failed;
        private bool _disposed;
        private bool _transferred;

        public string ClipId { get; }
        public string Path { get; }
        public double SourceSeconds { get; }
        public double BoundarySeconds { get; }
        public MediaPlayer Player { get; } = new() { AutoPlay = false, IsMuted = true, Volume = 0, IsVideoFrameServerEnabled = true };

        public PreparedPreviewCut(SafeClipEditorView owner, EditorProjectClip clip, string path, double boundary)
        {
            _owner = owner;
            ClipId = clip.Id;
            Path = path;
            BoundarySeconds = boundary;
            SourceSeconds = owner.GetSourcePosition(clip, TimeSpan.FromSeconds(boundary)).TotalSeconds;
            PreviewAudioGainEffect.SetGain(Player, 0);
            Player.PlaybackSession.PlaybackRate = clip.PlaybackSpeed;
            Player.MediaOpened += OnOpened;
            Player.MediaFailed += OnFailed;
            try { Player.Source = MediaSource.CreateFromUri(new Uri(path)); }
            catch { _failed = true; }
        }

        private void OnOpened(MediaPlayer sender, object args) => _owner.DispatcherQueue.TryEnqueue(() =>
        {
            if (_disposed || _transferred) return;
            _opening = false;
            try
            {
                Player.Pause();
                RequestDecoderSeek(Player, _seek, TimeSpan.FromSeconds(SourceSeconds), force: true);
            }
            catch { _failed = true; }
        });

        private void OnFailed(MediaPlayer sender, MediaPlayerFailedEventArgs args) =>
            _owner.DispatcherQueue.TryEnqueue(() => { if (!_disposed) _failed = true; });

        public bool IsReady
        {
            get
            {
                if (_disposed || _failed || _opening) return false;
                var session = Player.PlaybackSession;
                var pending = _seek.IsPending(session.Position.TotalSeconds, PreviewNowSeconds);
                if (_seek.TakeTimeout()) _failed = true;
                return !_failed && !pending && session.PlaybackState is
                    MediaPlaybackState.Paused or MediaPlaybackState.Playing;
            }
        }

        public bool HasExpired => _failed || _opening && PreviewNowSeconds - _openedAt > 4;
        // A bad file must not reopen a decoder on every preparation tick.
        public bool CanRetry => PreviewNowSeconds - _openedAt >= 0.75;

        public MediaPlayer Transfer()
        {
            _transferred = true;
            Player.MediaOpened -= OnOpened;
            Player.MediaFailed -= OnFailed;
            return Player;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Player.MediaOpened -= OnOpened;
            Player.MediaFailed -= OnFailed;
            if (_transferred) return;
            try { Player.Pause(); Player.Source = null; } catch { }
            PreviewAudioGainEffect.Release(Player);
            Player.Dispose();
        }
    }

    private static EditorPreviewCutSegment GetPreviewCutSegment(EditorProjectClip clip) => new(
        clip.TimelineStartSeconds, clip.TrimStartSeconds, clip.TrimEndSeconds, clip.PlaybackSpeed);

    private bool CanContinueSecondarySource(EditorProjectClip? previous, EditorProjectClip incoming,
        string? previousPath, string incomingPath, MediaPlayer player, EditorPreviewSeekState seekState,
        bool unavailable, bool explicitSeek) => previous is not null && !explicitSeek && !unavailable &&
        _isPreviewPlaying && !_wasPreviewBuffering && seekState.TargetSeconds is null &&
        string.Equals(previousPath, incomingPath, StringComparison.OrdinalIgnoreCase) &&
        player.PlaybackSession.PlaybackState == MediaPlaybackState.Playing &&
        EditorPreviewCutMath.CanContinueSource(GetPreviewCutSegment(previous), GetPreviewCutSegment(incoming),
            string.Equals(previous.SourcePath, incoming.SourcePath, StringComparison.OrdinalIgnoreCase));

    private bool CanContinuePrimarySource(EditorProjectClip? previous, EditorProjectClip incoming, string previewPath) =>
        previous is not null && _usingNativePreview && !_isPreviewOpening &&
        _primaryDecoderSeek.TargetSeconds is null &&
        NativePreviewPlayer.MediaPlayer.PlaybackSession.PlaybackState == MediaPlaybackState.Playing &&
        string.Equals(_nativePreviewMediaPath, previewPath, StringComparison.OrdinalIgnoreCase) &&
        EditorPreviewCutMath.CanContinueSource(GetPreviewCutSegment(previous), GetPreviewCutSegment(incoming),
            string.Equals(previous.SourcePath, incoming.SourcePath, StringComparison.OrdinalIgnoreCase));

    private void PrepareUpcomingPreviewCut()
    {
        if (!_isPreviewPlaying || _project is null ||
            !_cutPreparationGate.TryEnter(PreviewNowSeconds, immediate: false)) return;
        var now = _previewPosition.TotalSeconds;
        var horizon = Math.Min(LayerTimeline.End.TotalSeconds, now + 1.5);
        var clips = LayerTimeline.GetPrimaryClipsForPreview();
        EditorProjectClip? candidate = null;
        var boundary = 0.0;
        // Both starts and ends matter: overlaps can restore an older clip, and a
        // transition keeps its outgoing decoder until the overlap finishes.
        foreach (var edge in clips.SelectMany(clip => new[]
                 { clip.TimelineStartSeconds, clip.TimelineStartSeconds + clip.EffectiveDurationSeconds })
                     .Where(edge => edge > now + 0.001 && edge <= horizon).Distinct().Order())
        {
            var atEdge = ResolvePrimaryPreviewClipAt(TimeSpan.FromSeconds(edge + 0.001));
            if (atEdge is null || atEdge.Id == _activePreviewClipId) continue;
            if (CanContinuePrimarySource(GetActivePreviewClip(), atEdge, ResolvePreviewMediaPath(atEdge.SourcePath)))
                continue;
            candidate = atEdge;
            boundary = edge;
            break;
        }
        if (candidate is null)
        {
            // Keep a prepared frame while the playhead lands exactly on its cut.
            if (_preparedPreviewCut is { } prepared && now <= prepared.BoundarySeconds + 0.15 &&
                now >= prepared.BoundarySeconds - 1.5 && !prepared.HasExpired) return;
            ClearPreparedPreviewCut();
            return;
        }
        var path = ResolvePreviewMediaPath(candidate.SourcePath);
        if (_preparedPreviewCut is { } existing && existing.ClipId == candidate.Id &&
            string.Equals(existing.Path, path, StringComparison.OrdinalIgnoreCase) &&
            Math.Abs(existing.BoundarySeconds - boundary) < 0.001 &&
            Math.Abs(existing.SourceSeconds - GetSourcePosition(candidate, TimeSpan.FromSeconds(boundary)).TotalSeconds) < 0.001)
        {
            if (!existing.HasExpired || !existing.CanRetry) return;
        }
        ClearPreparedPreviewCut();
        if (!File.Exists(path)) return;
        try { _preparedPreviewCut = new PreparedPreviewCut(this, candidate, path, boundary); }
        catch { ClearPreparedPreviewCut(); } // Warming must never prevent ordinary playback.
    }

    private bool TryUsePreparedPreviewCut(EditorProjectClip clip, TimeSpan position, string path)
    {
        if (_preparedPreviewCut is not { } prepared || prepared.ClipId != clip.Id ||
            !string.Equals(prepared.Path, path, StringComparison.OrdinalIgnoreCase) || !prepared.IsReady ||
            !EditorPreviewCutMath.CanUsePreparedFrame(prepared.Player.PlaybackSession.Position.TotalSeconds,
                GetSourcePosition(clip, position).TotalSeconds, clip.PlaybackSpeed, _exportFrameRate))
            return false;

        var previous = NativePreviewPlayer.MediaPlayer;
        // Effect resources subscribe to a particular decoder. Detach them before
        // replacing it, then let the usual effect refresh attach to the ready frame.
        ClearClipEffectPreview(PrimaryEffectPreviewHost);
        _primaryEffectPreviewSignature = null;
        var player = prepared.Transfer();
        _preparedPreviewCut = null;
        prepared.Dispose();
        _primarySourceVersion++;
        _primaryDecoderSeek.Reset();
        _isPreviewOpening = false;
        _usingNativePreview = true;
        _filePath = clip.SourcePath;
        _nativePreviewMediaPath = path;
        DetachPrimaryPreviewEvents();
        NativePreviewPlayer.SetMediaPlayer(player);
        EnsurePrimarySeekEvents();
        PreviewFallback.Visibility = Visibility.Collapsed;
        PreviewImage.Opacity = 0;
        try
        {
            player.PlaybackSession.PlaybackRate = clip.PlaybackSpeed;
            PreviewAudioGainEffect.SetGain(player, LivePreviewVolume * EvaluatePreviewClipVolume(clip, position));
            player.IsMuted = (MuteAudioToggle?.IsOn ?? false) || clip.Muted || clip.SourceAudioSeparated;
            if (_isPreviewPlaying) player.Play();
        }
        catch { }
        try { previous.Pause(); previous.Source = null; PreviewAudioGainEffect.Release(previous); previous.Dispose(); } catch { }
        return true;
    }

    private void ClearPreparedPreviewCut()
    {
        _preparedPreviewCut?.Dispose();
        _preparedPreviewCut = null;
    }
}
