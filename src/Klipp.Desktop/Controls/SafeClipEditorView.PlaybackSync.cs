using System;
using System.Diagnostics;
using Klipp.Core.Editing;
using Klipp.Desktop.Models;
using Windows.Media.Playback;

namespace Klipp.Desktop.Controls;

public sealed partial class SafeClipEditorView
{
    private readonly EditorPreviewSeekState _primaryDecoderSeek = new();
    private readonly EditorPreviewSeekState _overlayDecoderSeek = new();
    private long _primarySourceVersion;
    private long _overlaySourceVersion;
    private MediaPlayer? _primaryEventsPlayer;
    private MediaPlaybackSession? _primaryEventsSession;
    private bool _overlayPreviewOpening;
    private bool _overlayPreviewFailed;
    private bool _overlayFailureEventsInitialized;
    private double _overlayOpeningAt;
    private double _primaryOpeningAt;
    private bool _pausedPrimaryForSecondaryBuffering;

    private static double PreviewNowSeconds => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;

    private static void RequestDecoderSeek(MediaPlayer player, EditorPreviewSeekState state, TimeSpan target, bool force)
    {
        var session = player.PlaybackSession;
        if (!state.Request(target.TotalSeconds, session.Position.TotalSeconds, PreviewNowSeconds, force)) return;
        try { session.Position = target; }
        catch { state.Reset(); throw; }
    }

    private void RequestPrimaryPreviewSeek(EditorProjectClip clip, TimeSpan projectPosition)
    {
        _pendingPreviewSourcePosition = GetSourcePosition(clip, projectPosition);
        var player = NativePreviewPlayer.MediaPlayer;
        player.PlaybackSession.PlaybackRate = clip.PlaybackSpeed;
        RequestDecoderSeek(player, _primaryDecoderSeek, _pendingPreviewSourcePosition, force: true);
        if (_isPreviewPlaying && player.PlaybackSession.PlaybackState != MediaPlaybackState.Playing)
            player.Play();
    }

    private void EnsurePrimarySeekEvents()
    {
        var player = NativePreviewPlayer.MediaPlayer;
        if (ReferenceEquals(_primaryEventsPlayer, player)) return;
        DetachPrimaryPreviewEvents();
        _primaryEventsPlayer = player;
        _primaryEventsSession = player.PlaybackSession;
        _primaryEventsSession.SeekCompleted += OnPrimaryPreviewSeekCompleted;
        player.MediaOpened += OnPreviewMediaOpened;
        player.MediaFailed += OnPreviewMediaFailed;
        player.MediaEnded += OnPreviewMediaEnded;
        player.CurrentStateChanged += OnPreviewStateChanged;
    }

    private void DetachPrimaryPreviewEvents()
    {
        var player = _primaryEventsPlayer;
        var session = _primaryEventsSession;
        _primaryEventsPlayer = null;
        _primaryEventsSession = null;
        // MediaPlayerElement closes its internally owned player when replacing
        // it. Unsubscribe while that player is alive, using the session we bound.
        if (session is not null)
            try { session.SeekCompleted -= OnPrimaryPreviewSeekCompleted; } catch { }
        if (player is null) return;
        try { player.MediaOpened -= OnPreviewMediaOpened; } catch { }
        try { player.MediaFailed -= OnPreviewMediaFailed; } catch { }
        try { player.MediaEnded -= OnPreviewMediaEnded; } catch { }
        try { player.CurrentStateChanged -= OnPreviewStateChanged; } catch { }
    }

    private void OnPrimaryPreviewSeekCompleted(MediaPlaybackSession session, object args)
    {
        var player = _primaryEventsPlayer;
        var version = _primarySourceVersion;
        var loadVersion = _projectLoadVersion;
        DispatcherQueue.TryEnqueue(() =>
        {
            if (!ReferenceEquals(player, NativePreviewPlayer.MediaPlayer) ||
                !ReferenceEquals(session, _primaryEventsSession) ||
                version != _primarySourceVersion || loadVersion != _projectLoadVersion || !_usingNativePreview) return;
            _primaryDecoderSeek.IsPending(session.Position.TotalSeconds, PreviewNowSeconds, seekCompleted: true);
            _lastPreviewTick = 0;
        });
    }

    private bool IsPrimaryPreviewDecoderBlocked()
    {
        if (_isPreviewOpening)
        {
            if (PreviewNowSeconds - _primaryOpeningAt >= 8)
            {
                _primarySourceVersion++;
                _isPreviewOpening = false;
                _nativePreviewMediaPath = null;
                try { NativePreviewPlayer.MediaPlayer.Source = null; } catch { }
                PausePreview();
                TimelinePlayIcon.Text = "\uE768";
                ProjectStatusText.Text = "Preview could not open in time. Try a lower preview quality.";
            }
            return true;
        }
        if (!_usingNativePreview) return false;
        var session = NativePreviewPlayer.MediaPlayer.PlaybackSession;
        var pending = _primaryDecoderSeek.IsPending(session.Position.TotalSeconds, PreviewNowSeconds);
        if (_primaryDecoderSeek.TakeTimeout())
        {
            PausePreview();
            TimelinePlayIcon.Text = "\uE768";
            ProjectStatusText.Text = "Preview seek timed out. Seek again or choose a lower preview quality.";
            return true;
        }
        return session.PlaybackState is MediaPlaybackState.Opening or MediaPlaybackState.Buffering || pending;
    }

    private bool IsOverlayPreviewDecoderBlocked()
    {
        if (_activeOverlayPreviewClipId is not null && !_overlayPreviewFailed)
        {
            var session = OverlayPreviewPlayer.MediaPlayer.PlaybackSession;
            var pending = _overlayDecoderSeek.IsPending(session.Position.TotalSeconds, PreviewNowSeconds);
            if (_overlayDecoderSeek.TakeTimeout() || _overlayPreviewOpening && PreviewNowSeconds - _overlayOpeningAt >= 3)
            {
                _overlayPreviewFailed = true;
                OverlayPreviewHost.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed;
                ProjectStatusText.Text = "A video layer could not reach the requested preview frame.";
                return false;
            }
            if (_overlayPreviewOpening || session.PlaybackState is MediaPlaybackState.Opening or MediaPlaybackState.Buffering ||
                pending) return true;
        }
        foreach (var row in _additionalVideoRows.Values)
            if (row.Incoming.IsDecoderBlocked || row.Outgoing?.IsDecoderBlocked == true) return true;
        return _highestOverlayOutgoing?.IsDecoderBlocked == true;
    }

    private void EnsureOverlayFailureEvents()
    {
        if (_overlayFailureEventsInitialized) return;
        _overlayFailureEventsInitialized = true;
        OverlayPreviewPlayer.MediaPlayer.MediaFailed += (_, _) =>
        {
            var version = _overlaySourceVersion;
            DispatcherQueue.TryEnqueue(() =>
            {
                if (version != _overlaySourceVersion || _activeOverlayPreviewClipId is null) return;
                _overlayPreviewOpening = false;
                _overlayPreviewFailed = true;
                _overlayDecoderSeek.Reset();
                OverlayPreviewHost.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed;
                ClearClipEffectPreview(OverlayEffectPreviewHost);
                _overlayEffectPreviewSignature = null;
                ProjectStatusText.Text = "A video layer could not be previewed. Other tracks will continue.";
            });
        };
    }
}
