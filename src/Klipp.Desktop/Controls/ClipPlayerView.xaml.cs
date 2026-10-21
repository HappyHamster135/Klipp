using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using Klipp.Core.Playback;
using Klipp.Desktop.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.Media.Core;
using Windows.Media.Playback;

namespace Klipp.Desktop.Controls;

/// <summary>
/// Native Windows playback with Klipp's own transport chrome.
/// </summary>
public sealed partial class ClipPlayerView : UserControl
{
    private string? _currentFilePath;
    private MediaSource? _currentMediaSource;
    private int _mediaLoadVersion;
    private bool _restorePlaybackOnOpen;
    private TimeSpan _requestedStartPosition;
    private bool _requestedAutoPlay;
    private bool _requestedIsMuted;
    private double _requestedVolume;
    private PlaybackMediaIdentity? _playbackMediaIdentity;
    private bool _resumeRememberedPosition;
    private bool _mediaOpened;
    private DateTime _lastPositionSaveUtc;
    public string? CurrentFilePath => _currentFilePath;

    private readonly DispatcherTimer _hideChromeTimer;
    private readonly DispatcherTimer _positionTimer;
    private bool _canGoPrev;
    private bool _canGoNext;
    private bool _chromeVisible = true;
    private bool _hostFullscreen;
    private bool _syncingPlayerUi;
    private bool _isScrubbing;
    private bool _resumePlaybackAfterScrub;
    private int _videoTapToken;
    private DateTime _ignoreVideoTapUntilUtc = DateTime.MinValue;
    private double _headerChromeOpacity = double.NaN;
    private double _controlsChromeOpacity = double.NaN;
    private double _centerPlayOpacity = double.NaN;
    private double _prevButtonOpacity = double.NaN;
    private double _nextButtonOpacity = double.NaN;

    public event EventHandler? BackRequested;
    public event EventHandler<ClipDeleteEventArgs>? DeleteRequested;
    public event EventHandler<ClipRenameEventArgs>? RenameRequested;
    public event EventHandler<ClipTrimEventArgs>? TrimRequested;
    public event EventHandler? PrevRequested;
    public event EventHandler? NextRequested;
    public event EventHandler<PlayerFullscreenRequestEventArgs>? FullscreenRequested;

    public ClipPlayerView()
    {
        InitializeComponent();

        _hideChromeTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
        _hideChromeTimer.Tick += (_, _) => HideTransientChrome();

        _positionTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        _positionTimer.Tick += (_, _) => UpdatePlayerUi();

        NativePlayer.MediaPlayer.Volume = VolumeSlider.Value / 100.0;
        NativePlayer.MediaPlayer.MediaOpened += OnMediaOpened;
        NativePlayer.MediaPlayer.MediaEnded += OnMediaEnded;
        NativePlayer.MediaPlayer.MediaFailed += OnMediaFailed;
        NativePlayer.MediaPlayer.CurrentStateChanged += OnMediaStateChanged;

        ProgressSlider.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler(OnProgressPointerPressed), true);
        ProgressSlider.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler(OnProgressPointerReleased), true);
        ProgressSlider.AddHandler(UIElement.PointerCanceledEvent, new PointerEventHandler(OnProgressPointerCanceled), true);

        UpdatePlayerUi();

        Unloaded += (_, _) =>
        {
            _hideChromeTimer.Stop();
            _positionTimer.Stop();
            Stop();
        };
    }

    public bool CanGoPrev
    {
        get => _canGoPrev;
        set
        {
            _canGoPrev = value;
            UpdateNavigationChrome();
        }
    }

    public bool CanGoNext
    {
        get => _canGoNext;
        set
        {
            _canGoNext = value;
            UpdateNavigationChrome();
        }
    }

    public void LoadClip(string filePath, TimeSpan? startPosition = null, bool autoPlay = true,
        bool? isMuted = null, double? volume = null)
    {
        if (!File.Exists(filePath))
            return;

        Stop();
        _currentFilePath = filePath;
        _playbackMediaIdentity = PlaybackMediaIdentity.TryCreate(filePath);
        _resumeRememberedPosition = !startPosition.HasValue;
        _lastPositionSaveUtc = DateTime.UtcNow;
        var file = new FileInfo(filePath);
        TitleText.Text = file.Name.Replace(".mp4", "", StringComparison.OrdinalIgnoreCase)
                                  .Replace("klipp_", "", StringComparison.OrdinalIgnoreCase);

        try
        {
            var player = NativePlayer.MediaPlayer;
            _requestedStartPosition = startPosition.GetValueOrDefault() < TimeSpan.Zero
                ? TimeSpan.Zero : startPosition.GetValueOrDefault();
            _requestedAutoPlay = autoPlay;
            _requestedIsMuted = isMuted ?? player.IsMuted;
            _requestedVolume = volume is { } requestedVolume && double.IsFinite(requestedVolume)
                ? Math.Clamp(requestedVolume, 0, 1) : player.Volume;
            _restorePlaybackOnOpen = true;
            // Wait for MediaOpened before seeking and playing, so expanding a
            // preview never flashes or plays the beginning of the clip.
            NativePlayer.AutoPlay = false;
            player.AutoPlay = false;
            player.Volume = _requestedVolume;
            player.IsMuted = _requestedIsMuted;
            _currentMediaSource = MediaSource.CreateFromUri(new Uri(filePath));
            NativePlayer.Source = _currentMediaSource;
            ShowTransientChrome();
            UpdatePlayerUi();
        }
        catch (Exception ex)
        {
            Stop();
            Debug.WriteLine($"[Klipp] Native player could not load clip: {ex.Message}");
        }
    }

    public void Stop()
    {
        RememberPlaybackPosition();
        _mediaOpened = false;
        _playbackMediaIdentity = null;
        Interlocked.Increment(ref _mediaLoadVersion);
        _videoTapToken++;
        _restorePlaybackOnOpen = false;
        _isScrubbing = false;
        _resumePlaybackAfterScrub = false;
        var source = _currentMediaSource;
        _currentMediaSource = null;
        try
        {
            _hideChromeTimer.Stop();
            _positionTimer.Stop();
            NativePlayer.MediaPlayer.Pause();
            NativePlayer.Source = null;
            ResetPlayerUi();
        }
        catch
        {
            // MediaPlayer may already be shutting down during unload.
        }
        finally
        {
            try { source?.Dispose(); } catch { }
            PlayerRoot.SetCursorHidden(false);
        }

        _currentFilePath = null;
    }

    public void SetHostFullscreenState(bool isFullscreen)
    {
        _hostFullscreen = isFullscreen;
        UpdateNavigationChrome();
    }

    public void PausePlayback()
    {
        if (NativePlayer?.MediaPlayer is null)
            return;

        if (_restorePlaybackOnOpen) _requestedAutoPlay = false;
        NativePlayer.MediaPlayer.Pause();
        _positionTimer.Stop();
        UpdatePlayerUi();
        ShowTransientChrome();
    }

    public void SeekTo(TimeSpan position)
    {
        if (NativePlayer?.MediaPlayer is null)
            return;

        var session = NativePlayer.MediaPlayer.PlaybackSession;
        var duration = session.NaturalDuration;
        if (duration <= TimeSpan.Zero)
        {
            if (_restorePlaybackOnOpen)
            {
                _requestedStartPosition = position < TimeSpan.Zero ? TimeSpan.Zero : position;
                _resumeRememberedPosition = false;
            }
            return;
        }

        if (position < TimeSpan.Zero)
            position = TimeSpan.Zero;
        if (position > duration)
            position = duration;

        session.Position = position;
        UpdatePlayerUi();
        ShowTransientChrome();
    }

    private void OnMediaOpened(MediaPlayer sender, object args)
    {
        var version = Volatile.Read(ref _mediaLoadVersion);
        var source = Volatile.Read(ref _currentMediaSource);
        DispatcherQueue.TryEnqueue(() =>
        {
            if (!IsCurrentMediaEvent(sender, source, version) || source!.State != MediaSourceState.Opened)
                return;
            _mediaOpened = true;
            if (_restorePlaybackOnOpen)
            {
                _restorePlaybackOnOpen = false;
                sender.Volume = _requestedVolume;
                sender.IsMuted = _requestedIsMuted;
                var duration = sender.PlaybackSession.NaturalDuration;
                if (_resumeRememberedPosition)
                    _requestedStartPosition = TimeSpan.FromSeconds(PlaybackPositionHistory.Shared.GetPosition(
                        _playbackMediaIdentity, duration.TotalSeconds));
                sender.PlaybackSession.Position = duration > TimeSpan.Zero && _requestedStartPosition > duration
                    ? duration : _requestedStartPosition;
                if (_requestedAutoPlay) sender.Play();
                else sender.Pause();
            }
            if (IsPlaying()) _positionTimer.Start();
            else _positionTimer.Stop();
            UpdatePlayerUi();
            ShowTransientChrome();
        });
    }

    private void OnMediaEnded(MediaPlayer sender, object args)
    {
        var version = Volatile.Read(ref _mediaLoadVersion);
        var source = Volatile.Read(ref _currentMediaSource);
        DispatcherQueue.TryEnqueue(() =>
        {
            if (!IsCurrentMediaEvent(sender, source, version)) return;
            _positionTimer.Stop();
            PlaybackPositionHistory.Shared.Remember(_playbackMediaIdentity, sender.PlaybackSession.NaturalDuration.TotalSeconds,
                sender.PlaybackSession.NaturalDuration.TotalSeconds);
            UpdatePlayerUi();
            ShowTransientChrome();
        });
    }

    private void OnMediaFailed(MediaPlayer sender, MediaPlayerFailedEventArgs args)
    {
        var version = Volatile.Read(ref _mediaLoadVersion);
        var source = Volatile.Read(ref _currentMediaSource);
        DispatcherQueue.TryEnqueue(() =>
        {
            if (!IsCurrentMediaEvent(sender, source, version)) return;
            _restorePlaybackOnOpen = false;
            _positionTimer.Stop();
            Debug.WriteLine($"[Klipp] Native player media failed: {args.Error} {args.ErrorMessage}");
            UpdatePlayerUi();
            ShowTransientChrome();
        });
    }

    private void OnMediaStateChanged(MediaPlayer sender, object args)
    {
        var version = Volatile.Read(ref _mediaLoadVersion);
        var source = Volatile.Read(ref _currentMediaSource);
        DispatcherQueue.TryEnqueue(() =>
        {
            if (!IsCurrentMediaEvent(sender, source, version)) return;
            if (IsPlaying())
                _positionTimer.Start();
            else
            {
                _positionTimer.Stop();
                RememberPlaybackPosition();
            }

            UpdatePlayerUi();
            UpdateNavigationChrome();
        });
    }

    private bool IsCurrentMediaEvent(MediaPlayer player, MediaSource? source, int version) =>
        version == Volatile.Read(ref _mediaLoadVersion) && source is not null &&
        ReferenceEquals(source, _currentMediaSource) && ReferenceEquals(player, NativePlayer.MediaPlayer) &&
        !string.IsNullOrEmpty(_currentFilePath);

    private void OnBackClicked(object sender, RoutedEventArgs e)
    {
        Stop();
        BackRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OnPrevClicked(object sender, RoutedEventArgs e)
    {
        if (!_canGoPrev) return;
        PrevRequested?.Invoke(this, EventArgs.Empty);
        ShowTransientChrome();
    }

    private void OnNextClicked(object sender, RoutedEventArgs e)
    {
        if (!_canGoNext) return;
        NextRequested?.Invoke(this, EventArgs.Empty);
        ShowTransientChrome();
    }

    private void OnPlayerDoubleTapped(object sender, Microsoft.UI.Xaml.Input.DoubleTappedRoutedEventArgs e)
    {
        _videoTapToken++;
        _ignoreVideoTapUntilUtc = DateTime.UtcNow.AddMilliseconds(320);
        FullscreenRequested?.Invoke(this, new PlayerFullscreenRequestEventArgs(null));
        e.Handled = true;
    }

    private async void OnPlayerTapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e)
    {
        if (DateTime.UtcNow < _ignoreVideoTapUntilUtc)
        {
            e.Handled = true;
            return;
        }

        var tapToken = ++_videoTapToken;
        await System.Threading.Tasks.Task.Delay(220);

        if (tapToken != _videoTapToken || DateTime.UtcNow < _ignoreVideoTapUntilUtc)
            return;

        TogglePlayPause();
        ShowTransientChrome();
        e.Handled = true;
    }

    private void OnPlayerPointerMoved(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        ShowTransientChrome();
    }

    private void OnPlayPauseClicked(object sender, RoutedEventArgs e)
    {
        TogglePlayPause();
        ShowTransientChrome();
    }

    private void OnBack10Clicked(object sender, RoutedEventArgs e)
    {
        SeekBy(TimeSpan.FromSeconds(-10));
        ShowTransientChrome();
    }

    private void OnForward10Clicked(object sender, RoutedEventArgs e)
    {
        SeekBy(TimeSpan.FromSeconds(10));
        ShowTransientChrome();
    }

    private void OnMuteClicked(object sender, RoutedEventArgs e)
    {
        NativePlayer.MediaPlayer.IsMuted = !NativePlayer.MediaPlayer.IsMuted;
        if (_restorePlaybackOnOpen) _requestedIsMuted = NativePlayer.MediaPlayer.IsMuted;
        UpdatePlayerUi();
        ShowTransientChrome();
    }

    private void OnFullscreenClicked(object sender, RoutedEventArgs e)
    {
        FullscreenRequested?.Invoke(this, new PlayerFullscreenRequestEventArgs(null));
        ShowTransientChrome();
    }

    private void OnProgressValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_syncingPlayerUi || NativePlayer?.MediaPlayer is null)
            return;

        var duration = NativePlayer.MediaPlayer.PlaybackSession.NaturalDuration;
        if (duration <= TimeSpan.Zero)
            return;

        NativePlayer.MediaPlayer.PlaybackSession.Position = TimeSpan.FromSeconds(
            Math.Clamp(e.NewValue, 0, duration.TotalSeconds));

        if (_isScrubbing)
        {
            CurrentTimeText.Text = FormatTimestamp(TimeSpan.FromSeconds(e.NewValue));
            ShowTransientChrome();
            return;
        }

        UpdatePlayerUi();
    }

    private void OnProgressPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (NativePlayer?.MediaPlayer is null)
            return;

        _isScrubbing = true;
        _resumePlaybackAfterScrub = IsPlaying();

        if (_resumePlaybackAfterScrub)
        {
            NativePlayer.MediaPlayer.Pause();
            _positionTimer.Stop();
        }

        UpdatePlayerUi();
        ShowTransientChrome();
    }

    private void OnProgressPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        FinishProgressScrub();
    }

    private void OnProgressPointerCanceled(object sender, PointerRoutedEventArgs e)
    {
        FinishProgressScrub();
    }

    private void OnVolumeValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_syncingPlayerUi || NativePlayer?.MediaPlayer is null)
            return;

        NativePlayer.MediaPlayer.Volume = Math.Clamp(e.NewValue / 100.0, 0, 1);
        if (e.NewValue > 0)
            NativePlayer.MediaPlayer.IsMuted = false;
        if (_restorePlaybackOnOpen)
        {
            _requestedVolume = NativePlayer.MediaPlayer.Volume;
            _requestedIsMuted = NativePlayer.MediaPlayer.IsMuted;
        }

        UpdatePlayerUi();
    }

    private void TogglePlayPause()
    {
        if (string.IsNullOrEmpty(_currentFilePath))
            return;

        if (IsPlaying())
        {
            if (_restorePlaybackOnOpen) _requestedAutoPlay = false;
            NativePlayer.MediaPlayer.Pause();
            _positionTimer.Stop();
        }
        else
        {
            if (_restorePlaybackOnOpen) _requestedAutoPlay = true;
            var session = NativePlayer.MediaPlayer.PlaybackSession;
            if (session.NaturalDuration > TimeSpan.Zero &&
                session.Position >= session.NaturalDuration - TimeSpan.FromMilliseconds(100))
            {
                session.Position = TimeSpan.Zero;
            }

            NativePlayer.MediaPlayer.Play();
            _positionTimer.Start();
        }

        UpdatePlayerUi();
    }

    private void SeekBy(TimeSpan delta)
    {
        var session = NativePlayer.MediaPlayer.PlaybackSession;
        var duration = session.NaturalDuration;
        if (duration <= TimeSpan.Zero)
            return;

        SeekTo(session.Position + delta);
    }

    private void FinishProgressScrub()
    {
        if (!_isScrubbing || NativePlayer?.MediaPlayer is null)
            return;

        var session = NativePlayer.MediaPlayer.PlaybackSession;
        var duration = session.NaturalDuration;
        if (duration > TimeSpan.Zero)
        {
            session.Position = TimeSpan.FromSeconds(Math.Clamp(ProgressSlider.Value, 0, duration.TotalSeconds));
        }

        _isScrubbing = false;
        var shouldResume = _resumePlaybackAfterScrub;
        _resumePlaybackAfterScrub = false;

        if (shouldResume && !string.IsNullOrEmpty(_currentFilePath))
        {
            NativePlayer.MediaPlayer.Play();
            _positionTimer.Start();
        }

        UpdatePlayerUi();
        ShowTransientChrome();
    }

    private void UpdatePlayerUi()
    {
        if (NativePlayer?.MediaPlayer is null)
            return;

        var session = NativePlayer.MediaPlayer.PlaybackSession;
        var duration = session.NaturalDuration;
        if (_mediaOpened && DateTime.UtcNow - _lastPositionSaveUtc >= TimeSpan.FromSeconds(5))
            RememberPlaybackPosition();
        var position = session.Position;
        var hasDuration = duration > TimeSpan.Zero;
        var isPlaying = IsPlaying();
        var displayPosition = _isScrubbing && hasDuration
            ? TimeSpan.FromSeconds(Math.Clamp(ProgressSlider.Value, 0, duration.TotalSeconds))
            : position;

        _syncingPlayerUi = true;
        try
        {
            CurrentTimeText.Text = FormatTimestamp(displayPosition);
            TotalTimeText.Text = hasDuration ? FormatTimestamp(duration) : "0:00";
            ProgressSlider.Maximum = hasDuration ? Math.Max(1, duration.TotalSeconds) : 1;
            if (!_isScrubbing)
                ProgressSlider.Value = hasDuration ? Math.Clamp(position.TotalSeconds, 0, ProgressSlider.Maximum) : 0;
            PlayPauseIcon.Text = isPlaying ? "\uE769" : "\uE768";
            CenterPlayIcon.Text = "\uE768";
            MuteIcon.Text = NativePlayer.MediaPlayer.IsMuted || NativePlayer.MediaPlayer.Volume <= 0.001
                ? "\uE74F"
                : "\uE995";
            VolumeSlider.Value = NativePlayer.MediaPlayer.Volume * 100;
        }
        finally
        {
            _syncingPlayerUi = false;
        }

        var hideCenterPlay = isPlaying || _isScrubbing;
        var centerOpacity = hideCenterPlay ? 0 : 0.96;
        SetChromeOpacity(CenterPlayButton, ref _centerPlayOpacity, centerOpacity);
        CenterPlayButton.IsHitTestVisible = !hideCenterPlay;
    }

    private void RememberPlaybackPosition()
    {
        if (!_mediaOpened || NativePlayer?.MediaPlayer is null) return;
        var session = NativePlayer.MediaPlayer.PlaybackSession;
        PlaybackPositionHistory.Shared.Remember(_playbackMediaIdentity, session.Position.TotalSeconds,
            session.NaturalDuration.TotalSeconds);
        _lastPositionSaveUtc = DateTime.UtcNow;
    }

    private void ResetPlayerUi()
    {
        _syncingPlayerUi = true;
        try
        {
            CurrentTimeText.Text = "0:00";
            TotalTimeText.Text = "0:00";
            ProgressSlider.Maximum = 1;
            ProgressSlider.Value = 0;
            PlayPauseIcon.Text = "\uE768";
            CenterPlayIcon.Text = "\uE768";
        }
        finally
        {
            _syncingPlayerUi = false;
        }

        SetChromeOpacity(CenterPlayButton, ref _centerPlayOpacity, 0.96);
        CenterPlayButton.IsHitTestVisible = true;
        UpdateNavigationChrome();
    }

    private bool IsPlaying()
    {
        return NativePlayer.MediaPlayer.PlaybackSession.PlaybackState == MediaPlaybackState.Playing;
    }

    private static string FormatTimestamp(TimeSpan value)
    {
        if (value < TimeSpan.Zero)
            value = TimeSpan.Zero;

        return value.TotalHours >= 1
            ? $"{(int)value.TotalHours}:{value.Minutes:00}:{value.Seconds:00}"
            : $"{(int)value.TotalMinutes}:{value.Seconds:00}";
    }

    private void ShowTransientChrome()
    {
        _chromeVisible = true;
        UpdateNavigationChrome();

        _hideChromeTimer.Stop();
        _hideChromeTimer.Start();
    }

    private void HideTransientChrome()
    {
        _hideChromeTimer.Stop();
        _chromeVisible = false;
        UpdateNavigationChrome();
    }

    private void UpdateNavigationChrome()
    {
        var showChrome = _chromeVisible || !IsPlaying();
        // Repeated transport and keyboard actions update immediately; no visual movement
        // competes with decoding, and the same behavior respects reduced-motion settings.
        SetChromeOpacity(HeaderChrome, ref _headerChromeOpacity, showChrome ? 1 : 0);
        SetChromeOpacity(ControlsChrome, ref _controlsChromeOpacity, showChrome ? 1 : 0);
        HeaderChrome.IsHitTestVisible = showChrome;
        ControlsChrome.IsHitTestVisible = showChrome;
        PlayerRoot.SetCursorHidden(_hostFullscreen && !showChrome);
        UpdateButtonChrome(PrevButton, _canGoPrev, ref _prevButtonOpacity);
        UpdateButtonChrome(NextButton, _canGoNext, ref _nextButtonOpacity);
    }

    private void UpdateButtonChrome(Button button, bool enabled, ref double lastOpacity)
    {
        var showChrome = _chromeVisible || !IsPlaying();
        button.IsEnabled = enabled;
        button.IsHitTestVisible = enabled && showChrome;
        SetChromeOpacity(button, ref lastOpacity, showChrome ? (enabled ? 1 : 0.35) : 0);
    }

    private static void SetChromeOpacity(UIElement element, ref double lastOpacity, double to)
    {
        if (!double.IsNaN(lastOpacity) && Math.Abs(lastOpacity - to) < 0.001)
            return;
        lastOpacity = to;
        element.Opacity = to;
    }

    private void OnOpenFolderClicked(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_currentFilePath)) return;
        Process.Start("explorer.exe", $"/select,\"{_currentFilePath}\"");
    }

    private void OnRenameClicked(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_currentFilePath)) return;
        RenameRequested?.Invoke(this, new ClipRenameEventArgs(_currentFilePath));
    }

    private void OnTrimClicked(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_currentFilePath)) return;
        TrimRequested?.Invoke(this, new ClipTrimEventArgs(_currentFilePath));
    }

    private void OnDeleteClicked(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_currentFilePath)) return;
        var path = _currentFilePath;
        Stop();
        DeleteRequested?.Invoke(this, new ClipDeleteEventArgs(path));
    }
}

public sealed class ClipDeleteEventArgs : EventArgs
{
    public string FilePath { get; }
    public ClipDeleteEventArgs(string filePath) { FilePath = filePath; }
}

public sealed class ClipRenameEventArgs : EventArgs
{
    public string FilePath { get; }
    public ClipRenameEventArgs(string filePath) { FilePath = filePath; }
}

public sealed class ClipTrimEventArgs : EventArgs
{
    public string FilePath { get; }
    public ClipTrimEventArgs(string filePath) { FilePath = filePath; }
}

public sealed class PlayerFullscreenRequestEventArgs : EventArgs
{
    public bool? IsFullscreen { get; }
    public PlayerFullscreenRequestEventArgs(bool? isFullscreen) { IsFullscreen = isFullscreen; }
}
