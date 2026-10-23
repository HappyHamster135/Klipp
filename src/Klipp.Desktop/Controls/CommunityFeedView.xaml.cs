using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Klipp.Core.Community;
using Klipp.Desktop.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.System;

namespace Klipp.Desktop.Controls;

/// <summary>Authenticated, bounded pages of community clips. Only an explicitly selected video is streamed.</summary>
public sealed partial class CommunityFeedView : UserControl, IDisposable
{
    private CommunityAccountService? _accounts;
    private CancellationTokenSource? _viewLifetime;
    private CancellationTokenSource? _pageLifetime;
    private CancellationTokenSource? _selectionLifetime;
    private CancellationTokenSource? _uploadLifetime;
    private readonly SemaphoreSlim _posterSlots = new(2, 2);
    private readonly List<CommunityFeedCursor?> _pageStarts = [null];
    private readonly HashSet<Guid> _likeOperations = [];
    private CommunityFeedCursor? _nextCursor;
    private CommunityFeedCursor? _nextCommentCursor;
    private CommunityFeedClipItem? _selected;
    private MediaPlayer? _player;
    private MediaSource? _source;
    private ContentDialog? _activeDialog;
    private XamlRoot? _layoutRoot;
    private Guid? _ownerId;
    private int _pageVersion;
    private int _selectionVersion;
    private int _pageIndex;
    private int _networkOperations;
    private bool _active;
    private bool _disposed;
    private bool _mine;
    private bool _feedLoading;
    private bool _commentsLoading;
    private bool _postingComment;
    private bool _uploading;
    private bool _feedFailed;
    private bool _expanded;
    private bool _playbackEnded;
    private bool _playbackLoading;
    private bool _managingBlocked;

    public ObservableCollection<CommunityFeedClipItem> Clips { get; } = [];
    public ObservableCollection<CommunityCommentItem> Comments { get; } = [];
    public event EventHandler? UploadRequested;
    public event EventHandler? ProfileRequired;
    public event EventHandler? IdentityChanged;
    public event EventHandler? BusyChanged;
    public bool IsBusy => _networkOperations > 0 || _player is not null || _uploading;
    public bool IsActive => _active;
    public bool IsUploading => _uploading;

    public CommunityFeedView()
    {
        InitializeComponent();
        Loaded += OnFeedLoaded;
        Unloaded += OnFeedUnloaded;
        UpdateControls();
    }

    public void Configure(CommunityAccountService accounts) => _accounts = accounts;

    public async Task ActivateAsync()
    {
        if (_disposed || _accounts?.IsSignedIn != true) return;
        if (_active && _ownerId == _accounts.User?.Id) return;
        Deactivate(clear: _ownerId != _accounts.User?.Id);
        _ownerId = _accounts.User!.Id;
        _active = true;
        _viewLifetime?.Dispose();
        _viewLifetime = new CancellationTokenSource();
        await LoadPageAsync(null, 0, resetHistory: true);
    }

    public void Deactivate(bool clear = false)
    {
        _active = false;
        _pageVersion++;
        _viewLifetime?.Cancel();
        _pageLifetime?.Cancel();
        _uploadLifetime?.Cancel();
        try { _activeDialog?.Hide(); } catch (Exception) { }
        StopWatching();
        _feedLoading = false;
        if (clear)
        {
            Clips.Clear();
            Comments.Clear();
            _ownerId = null;
            _pageStarts.Clear();
            _pageStarts.Add(null);
            _pageIndex = 0;
            _nextCursor = null;
            _mine = false;
            HideNotice();
        }
    }

    public async Task RefreshAsync()
    {
        if (CanUseMedia) await LoadPageAsync(null, 0, resetHistory: true);
    }

    public async Task<bool> UploadPreparedClipAsync(string videoPath, string thumbnailPath, string title,
        string description, double durationSeconds, CancellationToken cancellationToken = default)
    {
        if (!CanUseMedia || _uploading) return false;
        if (_accounts!.Profile?.IsComplete != true)
        {
            ProfileRequired?.Invoke(this, EventArgs.Empty);
            return false;
        }
        StopWatching();
        _pageLifetime?.Cancel();
        using var uploadCancellation = CancellationTokenSource.CreateLinkedTokenSource(_viewLifetime!.Token, cancellationToken);
        _uploadLifetime = uploadCancellation;
        _uploading = true;
        UploadProgressPanel.Visibility = Visibility.Visible;
        UploadProgressTitle.Text = title;
        UploadProgressBar.Value = 0;
        UploadProgressText.Text = "Preparing upload…";
        CancelUploadButton.Content = "Cancel";
        CancelUploadButton.IsEnabled = true;
        UpdateControls();
        RaiseBusyChanged();
        var progress = new Progress<CommunityUploadProgress>(value =>
        {
            if (!_active || _disposed || !ReferenceEquals(_uploadLifetime, uploadCancellation)) return;
            UploadProgressBar.Value = value.Fraction * 100;
            UploadProgressText.Text = $"{value.Status} · {FormatBytes(value.CompletedBytes)} / {FormatBytes(value.TotalBytes)}";
        });
        var uploaded = await RunMediaAsync(async token =>
        {
            await _accounts.UploadClipAsync(new CommunityUploadRequest(videoPath, thumbnailPath, title, description, durationSeconds), progress, token);
        }, uploadCancellation.Token);
        if (ReferenceEquals(_uploadLifetime, uploadCancellation)) _uploadLifetime = null;
        _uploading = false;
        UploadProgressPanel.Visibility = Visibility.Collapsed;
        UpdateControls();
        RaiseBusyChanged();
        if (uploaded && CanUseMedia)
        {
            _mine = true;
            await LoadPageAsync(null, 0, resetHistory: true);
            ShowNotice("Clip uploaded. It is now available in Media.", InfoBarSeverity.Success);
        }
        else if (uploadCancellation.IsCancellationRequested && CanUseMedia)
        {
            await LoadPageAsync(null, 0, resetHistory: true);
            ShowNotice("Upload canceled. Your local recording is still available.", InfoBarSeverity.Informational);
        }
        return uploaded;
    }

    private bool CanUseMedia => !_disposed && _active && _accounts?.IsSignedIn == true && _accounts.User?.Id == _ownerId;

    private bool IsCurrent(Guid? owner) => CanUseMedia && owner == _ownerId;

    private async Task<bool> RunMediaAsync(Func<CancellationToken, Task> action, CancellationToken cancellationToken,
        bool quiet = false)
    {
        if (!CanUseMedia || cancellationToken.IsCancellationRequested) return false;
        var owner = _ownerId;
        _networkOperations++;
        RaiseBusyChanged();
        try
        {
            await action(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            return IsCurrent(owner);
        }
        catch (OperationCanceledException) { return false; }
        catch (CommunityAccountException exception)
        {
            if (!_disposed && _active)
            {
                if (_accounts?.IsSignedIn != true || _accounts.User?.Id != _ownerId)
                    IdentityChanged?.Invoke(this, EventArgs.Empty);
                if (!quiet && IsCurrent(owner)) ShowNotice(exception.Message, InfoBarSeverity.Error);
            }
            return false;
        }
        catch (Exception)
        {
            if (!quiet && IsCurrent(owner))
                ShowNotice("Media could not finish that request. Check your connection and try again.", InfoBarSeverity.Error);
            return false;
        }
        finally
        {
            _networkOperations--;
            RaiseBusyChanged();
        }
    }

    private async Task LoadPageAsync(CommunityFeedCursor? cursor, int pageIndex, bool resetHistory = false)
    {
        if (!CanUseMedia || _uploading) return;
        var owner = _ownerId;
        var version = ++_pageVersion;
        _pageLifetime?.Cancel();
        _pageLifetime?.Dispose();
        _pageLifetime = CancellationTokenSource.CreateLinkedTokenSource(_viewLifetime!.Token);
        var token = _pageLifetime.Token;
        _feedLoading = true;
        _feedFailed = false;
        HideNotice();
        if (resetHistory) Clips.Clear();
        UpdateControls();
        var succeeded = await RunMediaAsync(async cancellation =>
        {
            var page = await _accounts!.GetMediaFeedAsync(cursor, _mine, cancellation);
            if (!IsCurrent(owner) || version != _pageVersion || cancellation.IsCancellationRequested) return;
            Clips.Clear();
            foreach (var clip in page.Items)
            {
                Clips.Add(new CommunityFeedClipItem(clip, owner!.Value) { IsSelected = _selected?.Clip.Id == clip.Id });
                if (_selected?.Clip.Id == clip.Id) _selected.Clip = clip;
            }
            if (resetHistory)
            {
                _pageStarts.Clear();
                _pageStarts.Add(null);
            }
            else if (pageIndex >= _pageStarts.Count) _pageStarts.Add(cursor);
            else _pageStarts[pageIndex] = cursor;
            _pageIndex = pageIndex;
            _nextCursor = page.NextCursor;
        }, token);
        if (version != _pageVersion || !IsCurrent(owner)) return;
        _feedLoading = false;
        _feedFailed = !succeeded;
        UpdateControls();
    }

    private void UpdateControls()
    {
        if (_disposed) return;
        var canNavigate = !_feedLoading && !_uploading;
        CommunityFeedButton.IsEnabled = canNavigate;
        MyUploadsButton.IsEnabled = canNavigate;
        RefreshFeedButton.IsEnabled = canNavigate;
        UploadButton.IsEnabled = !_uploading;
        BlockedUsersButton.IsEnabled = !_uploading && !_managingBlocked;
        NewerPageButton.IsEnabled = canNavigate && _pageIndex > 0;
        OlderPageButton.IsEnabled = canNavigate && _nextCursor is not null;
        FeedPageText.Text = $"Page {_pageIndex + 1}";
        FeedLoadingPanel.Visibility = _feedLoading ? Visibility.Visible : Visibility.Collapsed;
        FeedProgress.IsActive = _feedLoading;
        BrowseHeading.Text = _mine ? "Your shared clips" : "Latest clips";
        BrowseCount.Text = _feedLoading ? string.Empty : Clips.Count == 1 ? "1 clip" : $"{Clips.Count} clips";
        BrowseHeader.Visibility = _feedLoading || Clips.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        FeedEmptyPanel.Visibility = !_feedLoading && Clips.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        FeedEmptyTitle.Text = _feedFailed ? "Clips aren’t available right now" : _pageIndex > 0 ? "No more clips on this page" : _mine ? "No uploads yet" : "No clips shared yet";
        FeedEmptyDescription.Text = _feedFailed
            ? "Check your connection and choose Refresh to try again. Your local Library is still available."
            : _pageIndex > 0 ? "Choose Newer clips to return to the previous page."
            : _mine ? "Choose a highlight from your Library. Review it before uploading your share copy."
            : "Share a highlight from your Library to start the community.";
        FeedPagingPanel.Visibility = Clips.Count > 0 || _pageIndex > 0 ? Visibility.Visible : Visibility.Collapsed;
        foreach (var clip in Clips) clip.UploadBusy = _uploading;
        PostCommentButton.IsEnabled = !_postingComment && !_uploading && _selected is not null;
        OlderCommentsButton.IsEnabled = !_commentsLoading && !_uploading;
        OlderCommentsButton.Visibility = _nextCommentCursor is not null ? Visibility.Visible : Visibility.Collapsed;
        var selected = (Brush)Application.Current.Resources["KlippSurfaceElevatedBrush"];
        var border = (Brush)Application.Current.Resources["KlippBorderBrush"];
        var transparent = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        CommunityFeedButton.Background = _mine ? transparent : selected;
        CommunityFeedButton.BorderBrush = _mine ? transparent : border;
        MyUploadsButton.Background = _mine ? selected : transparent;
        MyUploadsButton.BorderBrush = _mine ? border : transparent;
        UpdateSelectedPresentation();
    }

    private async void OnCommunityClicked(object sender, RoutedEventArgs e)
    {
        if (!_mine) return;
        _mine = false;
        await LoadPageAsync(null, 0, resetHistory: true);
    }

    private async void OnMyUploadsClicked(object sender, RoutedEventArgs e)
    {
        if (_mine) return;
        _mine = true;
        await LoadPageAsync(null, 0, resetHistory: true);
    }

    private async void OnRefreshFeedClicked(object sender, RoutedEventArgs e) => await RefreshAsync();
    private async void OnOlderPageClicked(object sender, RoutedEventArgs e)
    {
        if (_nextCursor is not null) await LoadPageAsync(_nextCursor, _pageIndex + 1);
    }

    private async void OnNewerPageClicked(object sender, RoutedEventArgs e)
    {
        if (_pageIndex > 0) await LoadPageAsync(_pageStarts[_pageIndex - 1], _pageIndex - 1);
    }

    private void OnUploadClicked(object sender, RoutedEventArgs e)
    {
        if (!CanUseMedia || _uploading) return;
        if (_accounts!.Profile?.IsComplete != true) ProfileRequired?.Invoke(this, EventArgs.Empty);
        else UploadRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OnCancelUploadClicked(object sender, RoutedEventArgs e)
    {
        CancelUploadButton.IsEnabled = false;
        CancelUploadButton.Content = "Canceling…";
        UploadProgressText.Text = "Canceling the upload and removing its unfinished share copy…";
        _uploadLifetime?.Cancel();
    }

    private async void OnClipViewportChanged(FrameworkElement sender, EffectiveViewportChangedEventArgs args)
    {
        if (!CanUseMedia || _uploading || sender.Tag is not CommunityFeedClipItem item || item.PosterRequested) return;
        var viewport = args.EffectiveViewport;
        if (viewport.Width <= 0 || viewport.Height <= 0 || viewport.Right <= 0 || viewport.Bottom <= 0
            || viewport.X >= sender.ActualWidth || viewport.Y >= sender.ActualHeight) return;
        await LoadPosterAsync(item);
    }

    private async Task LoadPosterAsync(CommunityFeedClipItem item)
    {
        if (!CanUseMedia || _pageLifetime is null || item.PosterRequested || !item.Clip.IsPublished) return;
        item.PosterRequested = true;
        var token = _pageLifetime.Token;
        var version = _pageVersion;
        var owner = _ownerId;
        try
        {
            await _posterSlots.WaitAsync(token);
            try
            {
                await RunMediaAsync(async cancellation =>
                {
                    var uri = await _accounts!.GetClipPosterUriAsync(item.Clip.Id, cancellation);
                    if (!IsCurrent(owner) || version != _pageVersion || !Clips.Contains(item)) return;
                    item.Poster = new BitmapImage { DecodePixelWidth = 480, UriSource = uri };
                    if (_selected?.Clip.Id == item.Clip.Id && WatchPoster.Visibility == Visibility.Visible)
                        WatchPoster.Source = item.Poster;
                }, token, quiet: true);
            }
            finally { _posterSlots.Release(); }
        }
        catch (OperationCanceledException) { item.PosterRequested = false; }
    }

    private void OnPosterFailed(object sender, ExceptionRoutedEventArgs e)
    {
        if (sender is Image { DataContext: CommunityFeedClipItem item }) item.Poster = null;
    }

    private void OnClipPosterSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (sender is not FrameworkElement frame || e.NewSize.Width <= 0) return;
        var height = Math.Clamp(e.NewSize.Width * 9 / 16, 120, 240);
        if (Math.Abs(frame.Height - height) > 0.5) frame.Height = height;
    }

    private async void OnWatchClipClicked(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: CommunityFeedClipItem item }) await SelectClipAsync(item, play: true);
    }

    private async void OnCommentsClicked(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: CommunityFeedClipItem item }) return;
        if (_selected?.Clip.Id != item.Clip.Id) await SelectClipAsync(item, play: false);
        _expanded = false;
        UpdateWatchLayout();
        CommentInput.Focus(FocusState.Programmatic);
    }

    private async Task SelectClipAsync(CommunityFeedClipItem item, bool play)
    {
        if (!CanUseMedia || _uploading || !item.Clip.CanPlay) return;
        if (_selected?.Clip.Id == item.Clip.Id && _selectionLifetime is not null)
        {
            BringWatchIntoView(item.Clip.Id, _selectionVersion);
            if (play) await PlaySelectedAsync();
            return;
        }
        StopWatching(hidePanel: false);
        _selected = item;
        foreach (var card in Clips) card.IsSelected = card.Clip.Id == item.Clip.Id;
        _selectionLifetime = CancellationTokenSource.CreateLinkedTokenSource(_viewLifetime!.Token);
        var token = _selectionLifetime.Token;
        var version = _selectionVersion;
        WatchPoster.Source = item.Poster;
        WatchPoster.Visibility = Visibility.Visible;
        WatchPlayButton.Content = "Play clip";
        WatchPlayButton.Visibility = Visibility.Visible;
        WatchPanel.Visibility = Visibility.Visible;
        CommentsStateText.Text = "Loading comments…";
        CommentsStateText.Visibility = Visibility.Visible;
        if (!play) _expanded = false;
        UpdateWatchLayout();
        UpdateControls();
        BringWatchIntoView(item.Clip.Id, version);
        if (play) await StartSelectedPlaybackAsync(item, version, token);
        if (SelectionIsCurrent(item.Clip.Id, version)) await LoadCommentsAsync(older: false);
        if (SelectionIsCurrent(item.Clip.Id, version)) BringWatchIntoView(item.Clip.Id, version);
    }

    private void BringWatchIntoView(Guid clipId, int version)
    {
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            if (SelectionIsCurrent(clipId, version))
                OnlinePlayer.StartBringIntoView(new BringIntoViewOptions
                {
                    AnimationDesired = false, VerticalAlignmentRatio = 0, VerticalOffset = 12
                });
        });
    }

    private void UpdateSelectedPresentation()
    {
        if (_selected is null)
        {
            WatchLikeButton.IsEnabled = false;
            return;
        }
        WatchTitle.Text = _selected.Title;
        WatchAuthor.Text = _selected.Author;
        WatchDescription.Text = _selected.Clip.Description;
        WatchDescription.Visibility = string.IsNullOrWhiteSpace(_selected.Clip.Description) ? Visibility.Collapsed : Visibility.Visible;
        WatchLikeButton.Content = _selected.LikeLabel;
        WatchLikeButton.IsEnabled = !_uploading && !_likeOperations.Contains(_selected.Clip.Id) && _selected.Clip.CanPlay;
        CommentsHeading.Text = $"Comments · {_selected.Clip.CommentCount}";
    }

    private async Task StartSelectedPlaybackAsync(CommunityFeedClipItem item, int version, CancellationToken token)
    {
        if (_playbackLoading || !SelectionIsCurrent(item.Clip.Id, version)) return;
        _playbackLoading = true;
        WatchPlayButton.IsEnabled = false;
        var started = await RunMediaAsync(async cancellation =>
        {
            var uri = await _accounts!.GetClipPlaybackUriAsync(item.Clip.Id, cancellation);
            if (!SelectionIsCurrent(item.Clip.Id, version)) return;
            StopPlayer();
            _player = new MediaPlayer { AutoPlay = false, Volume = 0.8 };
            _player.CommandManager.IsEnabled = false;
            _player.MediaFailed += OnOnlineMediaFailed;
            _player.MediaEnded += OnOnlineMediaEnded;
            _player.PlaybackSession.PlaybackStateChanged += OnOnlinePlaybackStateChanged;
            _source = MediaSource.CreateFromUri(uri);
            OnlinePlayer.SetMediaPlayer(_player);
            _player.Source = _source;
            WatchPoster.Visibility = Visibility.Collapsed;
            WatchPlayButton.Visibility = Visibility.Collapsed;
            _player.Play();
            RaiseBusyChanged();
        }, token);
        if (SelectionIsCurrent(item.Clip.Id, version))
        {
            _playbackLoading = false;
            WatchPlayButton.IsEnabled = true;
            if (!started)
            {
                StopPlayer();
                WatchPoster.Visibility = Visibility.Visible;
                WatchPlayButton.Content = "Retry playback";
                WatchPlayButton.Visibility = Visibility.Visible;
            }
        }
    }

    private async void OnSelectedPlayClicked(object sender, RoutedEventArgs e) => await PlaySelectedAsync();

    private async Task PlaySelectedAsync()
    {
        if (CanUseMedia && _player is not null && !_uploading)
        {
            try
            {
                if (_playbackEnded) _player.PlaybackSession.Position = TimeSpan.Zero;
                _player.Play();
                _playbackEnded = false;
                WatchPlayButton.Visibility = Visibility.Collapsed;
                return;
            }
            catch (Exception) { StopPlayer(); }
        }
        if (_selected is not null && _selectionLifetime is not null && !_uploading)
            await StartSelectedPlaybackAsync(_selected, _selectionVersion, _selectionLifetime.Token);
    }

    private bool SelectionIsCurrent(Guid clipId, int version) => CanUseMedia && _selected?.Clip.Id == clipId
        && version == _selectionVersion && _selectionLifetime?.IsCancellationRequested == false;

    private void OnOnlineMediaEnded(MediaPlayer sender, object args)
    {
        var version = _selectionVersion;
        DispatcherQueue.TryEnqueue(() =>
        {
            if (!CanUseMedia || _selected is null || version != _selectionVersion || !ReferenceEquals(_player, sender)) return;
            _playbackEnded = true;
            WatchPlayButton.Content = "Replay clip";
            WatchPlayButton.IsEnabled = true;
            WatchPlayButton.Visibility = Visibility.Visible;
        });
    }

    private void OnOnlinePlaybackStateChanged(MediaPlaybackSession sender, object args)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            if (!CanUseMedia || _player is null || !ReferenceEquals(_player.PlaybackSession, sender)) return;
            if (_playbackEnded && sender.PlaybackState == MediaPlaybackState.Playing)
            {
                _playbackEnded = false;
                WatchPlayButton.Visibility = Visibility.Collapsed;
            }
        });
    }

    private void OnOnlineMediaFailed(MediaPlayer sender, MediaPlayerFailedEventArgs args)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            if (!_disposed && _active && ReferenceEquals(_player, sender))
            {
                StopPlayer();
                WatchPoster.Visibility = Visibility.Visible;
                WatchPlayButton.Content = "Retry playback";
                WatchPlayButton.IsEnabled = true;
                WatchPlayButton.Visibility = Visibility.Visible;
                ShowNotice("This video could not play. Close it and choose it again to get a fresh playback link.", InfoBarSeverity.Error);
            }
        });
    }

    private void StopPlayer()
    {
        try
        {
            if (_player is not null)
            {
                _player.MediaFailed -= OnOnlineMediaFailed;
                _player.MediaEnded -= OnOnlineMediaEnded;
                _player.PlaybackSession.PlaybackStateChanged -= OnOnlinePlaybackStateChanged;
                _player.Pause();
                _player.Source = null;
            }
            OnlinePlayer.SetMediaPlayer(null);
        }
        catch (Exception) { }
        try { _source?.Dispose(); } catch (Exception) { }
        _source = null;
        try { _player?.Dispose(); } catch (Exception) { }
        _player = null;
        _playbackEnded = false;
        RaiseBusyChanged();
    }

    private void StopWatching(bool hidePanel = true)
    {
        _selectionVersion++;
        _selectionLifetime?.Cancel();
        _selectionLifetime?.Dispose();
        _selectionLifetime = null;
        StopPlayer();
        _selected = null;
        foreach (var card in Clips) card.IsSelected = false;
        Comments.Clear();
        CommentInput.Text = string.Empty;
        _nextCommentCursor = null;
        _commentsLoading = false;
        _postingComment = false;
        _playbackLoading = false;
        if (hidePanel) WatchPanel.Visibility = Visibility.Collapsed;
        WatchPoster.Source = null;
        WatchPlayButton.IsEnabled = true;
        if (hidePanel) _expanded = false;
        UpdateWatchLayout();
    }

    private void OnCloseWatchClicked(object sender, RoutedEventArgs e)
    {
        StopWatching();
        UpdateControls();
    }

    private void OnPlayerDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (_player is null) return;
        ToggleExpandedVideo();
        e.Handled = true;
    }

    private void OnExpandVideoClicked(object sender, RoutedEventArgs e) => ToggleExpandedVideo();

    private void ToggleExpandedVideo()
    {
        _expanded = !_expanded;
        UpdateWatchLayout();
    }

    private void OnFeedKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Escape && _expanded)
        {
            _expanded = false;
            UpdateWatchLayout();
            e.Handled = true;
        }
    }

    private async Task LoadCommentsAsync(bool older)
    {
        if (!CanUseMedia || _selected is null || _selectionLifetime is null || _commentsLoading) return;
        var clipId = _selected.Clip.Id;
        var version = _selectionVersion;
        var token = _selectionLifetime.Token;
        _commentsLoading = true;
        UpdateControls();
        var succeeded = await RunMediaAsync(async cancellation =>
        {
            var page = await _accounts!.GetClipCommentsPageAsync(clipId, older ? _nextCommentCursor : null, cancellation);
            if (!SelectionIsCurrent(clipId, version)) return;
            if (!older) Comments.Clear();
            foreach (var comment in page.Items)
                if (Comments.All(existing => existing.Comment.Id != comment.Id)) Comments.Add(new CommunityCommentItem(comment, _ownerId!.Value));
            _nextCommentCursor = page.NextCursor;
            CommentsStateText.Text = Comments.Count == 0 ? "No comments yet. Start the conversation." : string.Empty;
            CommentsStateText.Visibility = Comments.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }, token);
        if (!SelectionIsCurrent(clipId, version)) return;
        _commentsLoading = false;
        if (!succeeded && Comments.Count == 0)
        {
            CommentsStateText.Text = "Comments couldn’t load. Reopen the clip to try again.";
            CommentsStateText.Visibility = Visibility.Visible;
        }
        UpdateControls();
    }

    private async void OnOlderCommentsClicked(object sender, RoutedEventArgs e)
    {
        if (_nextCommentCursor is not null) await LoadCommentsAsync(older: true);
    }

    private async void OnPostCommentClicked(object sender, RoutedEventArgs e)
    {
        if (!CanUseMedia || _selected is null || _selectionLifetime is null || _postingComment) return;
        var text = CommentInput.Text.Trim();
        if (string.IsNullOrWhiteSpace(text))
        {
            ShowNotice("Write a comment before posting.", InfoBarSeverity.Informational);
            CommentInput.Focus(FocusState.Programmatic);
            return;
        }
        var selected = _selected;
        var version = _selectionVersion;
        _postingComment = true;
        UpdateControls();
        await RunMediaAsync(async token =>
        {
            var comment = await _accounts!.AddClipCommentAsync(selected.Clip.Id, text, token);
            if (!SelectionIsCurrent(selected.Clip.Id, version)) return;
            Comments.Insert(0, new CommunityCommentItem(comment, _ownerId!.Value));
            CommentInput.Text = string.Empty;
            CommentsStateText.Visibility = Visibility.Collapsed;
            selected.Clip = selected.Clip with { CommentCount = selected.Clip.CommentCount + 1 };
            SyncVisibleClip(selected.Clip);
        }, _selectionLifetime.Token);
        if (SelectionIsCurrent(selected.Clip.Id, version))
        {
            _postingComment = false;
            UpdateControls();
        }
    }

    private async void OnDeleteCommentClicked(object sender, RoutedEventArgs e)
    {
        if (!CanUseMedia || _selected is null || _selectionLifetime is null
            || sender is not FrameworkElement { Tag: CommunityCommentItem item } || item.Comment.OwnerId != _ownerId) return;
        var clip = _selected;
        var version = _selectionVersion;
        var token = _selectionLifetime.Token;
        if (!await ConfirmAsync("Delete your comment?", "This removes your comment from the shared clip.", "Delete")) return;
        await RunMediaAsync(async cancellation =>
        {
            await _accounts!.DeleteClipCommentAsync(item.Comment.Id, cancellation);
            if (!SelectionIsCurrent(clip.Clip.Id, version)) return;
            Comments.Remove(item);
            clip.Clip = clip.Clip with { CommentCount = Math.Max(0, clip.Clip.CommentCount - 1) };
            SyncVisibleClip(clip.Clip);
            UpdateSelectedPresentation();
            if (Comments.Count == 0)
            {
                CommentsStateText.Text = "No comments yet. Start the conversation.";
                CommentsStateText.Visibility = Visibility.Visible;
            }
        }, token);
    }

    private async void OnLikeClicked(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: CommunityFeedClipItem item }) await LikeClipAsync(item);
    }

    private async void OnWatchLikeClicked(object sender, RoutedEventArgs e)
    {
        if (_selected is not null) await LikeClipAsync(_selected);
    }

    private async Task LikeClipAsync(CommunityFeedClipItem item)
    {
        if (!CanUseMedia || _uploading || _viewLifetime is null || item.IsBusy || !_likeOperations.Add(item.Clip.Id)) return;
        var owner = _ownerId;
        item.IsBusy = true;
        foreach (var card in Clips.Where(card => card.Clip.Id == item.Clip.Id)) card.IsBusy = true;
        UpdateSelectedPresentation();
        try
        {
            await RunMediaAsync(async token =>
            {
                var updated = await _accounts!.SetClipLikedAsync(item.Clip.Id, !item.Clip.IsLikedByMe, token);
                if (!IsCurrent(owner) || token.IsCancellationRequested) return;
                item.Clip = updated;
                SyncVisibleClip(updated);
                UpdateSelectedPresentation();
            }, _viewLifetime.Token);
        }
        finally
        {
            _likeOperations.Remove(item.Clip.Id);
            item.IsBusy = false;
            if (IsCurrent(owner))
            {
                foreach (var card in Clips.Where(card => card.Clip.Id == item.Clip.Id)) card.IsBusy = false;
                UpdateSelectedPresentation();
            }
        }
    }

    private void SyncVisibleClip(CommunityClip updated)
    {
        foreach (var card in Clips.Where(card => card.Clip.Id == updated.Id)) card.Clip = updated;
        if (_selected?.Clip.Id == updated.Id) _selected.Clip = updated;
    }

    private async void OnDeleteClipClicked(object sender, RoutedEventArgs e)
    {
        if (!CanUseMedia || _uploading || _pageLifetime is null || sender is not FrameworkElement { Tag: CommunityFeedClipItem item } || item.Clip.OwnerId != _ownerId) return;
        var token = _pageLifetime.Token;
        if (!await ConfirmAsync("Delete shared clip?", $"“{item.Title}” and its comments will be removed from Media. Your local recording remains in Library.", "Delete clip")) return;
        var deleted = await RunMediaAsync(async cancellation =>
        {
            await _accounts!.DeleteClipAsync(item.Clip.Id, cancellation);
            if (_selected?.Clip.Id == item.Clip.Id) StopWatching();
            Clips.Remove(item);
        }, token);
        if (deleted)
        {
            UpdateControls();
            ShowNotice("Shared clip deleted.", InfoBarSeverity.Success);
        }
    }

    private async void OnReportClipClicked(object sender, RoutedEventArgs e)
    {
        if (!CanUseMedia || _uploading || _pageLifetime is null || sender is not FrameworkElement { Tag: CommunityFeedClipItem item }) return;
        var token = _pageLifetime.Token;
        var reason = new TextBox { Header = "What’s wrong with this clip?", PlaceholderText = "Describe the issue", MaxLength = 500, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 96 };
        var dialog = AccessibleDialogFactory.Create(XamlRoot, "Report clip", reason,
            primaryButtonText: "Send report", closeButtonText: "Cancel", defaultButton: ContentDialogButton.Close);
        EnterDialog(dialog);
        ContentDialogResult result;
        try { result = await dialog.ShowAsync(); }
        catch (Exception)
        {
            if (CanUseMedia) ShowNotice("Close the current dialog before reporting a clip.", InfoBarSeverity.Informational);
            return;
        }
        finally { ExitDialog(dialog); }
        if (result != ContentDialogResult.Primary || !CanUseMedia || token.IsCancellationRequested) return;
        if (string.IsNullOrWhiteSpace(reason.Text))
        {
            ShowNotice("Add a reason so the report can be reviewed.", InfoBarSeverity.Informational);
            return;
        }
        if (await RunMediaAsync(ct => _accounts!.ReportClipAsync(item.Clip.Id, reason.Text.Trim(), ct), token))
            ShowNotice("Report sent for review.", InfoBarSeverity.Success);
    }

    private async void OnBlockUserClicked(object sender, RoutedEventArgs e)
    {
        if (!CanUseMedia || _uploading || _pageLifetime is null || sender is not FrameworkElement { Tag: CommunityFeedClipItem item } || item.Clip.OwnerId == _ownerId) return;
        var token = _pageLifetime.Token;
        if (!await ConfirmAsync($"Block {item.Author}?", "Their clips and comments will be hidden from you. You can unblock them from Blocked users.", "Block user")) return;
        var blocked = await RunMediaAsync(async cancellation =>
        {
            await _accounts!.BlockUserAsync(item.Clip.OwnerId, cancellation);
            if (_selected?.Clip.OwnerId == item.Clip.OwnerId) StopWatching();
            foreach (var clip in Clips.Where(clip => clip.Clip.OwnerId == item.Clip.OwnerId).ToArray()) Clips.Remove(clip);
            foreach (var comment in Comments.Where(comment => comment.Comment.OwnerId == item.Clip.OwnerId).ToArray()) Comments.Remove(comment);
        }, token);
        if (blocked)
        {
            UpdateControls();
            ShowNotice($"{item.Author} blocked. Manage this from Blocked users.", InfoBarSeverity.Success);
        }
    }

    private async void OnBlockedUsersClicked(object sender, RoutedEventArgs e)
    {
        if (!CanUseMedia || _uploading || _managingBlocked || _viewLifetime is null) return;
        var owner = _ownerId;
        var viewToken = _viewLifetime.Token;
        _managingBlocked = true;
        UpdateControls();
        try
        {
            IReadOnlyList<CommunityBlockedUser> blockedUsers = [];
            if (!await RunMediaAsync(async cancellation => blockedUsers = await _accounts!.GetBlockedUsersAsync(cancellation), viewToken)
                || !IsCurrent(owner)) return;

            var rows = new StackPanel { Spacing = 10 };
            var empty = new TextBlock
            {
                Text = "You haven’t blocked anyone.",
                TextWrapping = TextWrapping.Wrap,
                Visibility = blockedUsers.Count == 0 ? Visibility.Visible : Visibility.Collapsed
            };
            var status = new TextBlock { TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
            var content = new StackPanel { Spacing = 16, MinWidth = 320, MaxWidth = 440 };
            content.Children.Add(new TextBlock
            {
                Text = "Blocked users’ clips and comments are hidden from you. Unblock someone to see their posts again.",
                TextWrapping = TextWrapping.Wrap
            });
            content.Children.Add(empty);
            content.Children.Add(new ScrollViewer
            {
                Content = rows, MaxHeight = 320, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
            });
            content.Children.Add(status);
            var dialog = AccessibleDialogFactory.Create(XamlRoot, "Blocked users", content,
                closeButtonText: "Done", defaultButton: ContentDialogButton.Close);
            using var dialogLifetime = CancellationTokenSource.CreateLinkedTokenSource(viewToken);
            var dialogToken = dialogLifetime.Token;
            var open = true;
            var unblocking = false;
            dialog.Closed += (_, _) => { open = false; dialogLifetime.Cancel(); };
            foreach (var user in blockedUsers)
            {
                var row = new Grid { ColumnSpacing = 16 };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                var name = new StackPanel { Spacing = 3, VerticalAlignment = VerticalAlignment.Center };
                name.Children.Add(new TextBlock
                {
                    Text = string.IsNullOrWhiteSpace(user.DisplayName) ? "Community member" : user.DisplayName,
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis
                });
                if (!string.IsNullOrWhiteSpace(user.Username))
                    name.Children.Add(new TextBlock
                    {
                        Text = "@" + user.Username, FontSize = 12,
                        Foreground = (Brush)Application.Current.Resources["KlippTextSecondaryBrush"],
                        TextTrimming = TextTrimming.CharacterEllipsis
                    });
                var unblock = new Button { Content = "Unblock", Height = 32, VerticalAlignment = VerticalAlignment.Center };
                Grid.SetColumn(unblock, 1);
                row.Children.Add(name);
                row.Children.Add(unblock);
                rows.Children.Add(row);
                unblock.Click += async (_, _) =>
                {
                    if (!open || unblocking || !IsCurrent(owner) || dialogToken.IsCancellationRequested) return;
                    unblocking = true;
                    rows.IsHitTestVisible = false;
                    unblock.IsEnabled = false;
                    unblock.Content = "Unblocking…";
                    status.Visibility = Visibility.Collapsed;
                    var succeeded = await RunMediaAsync(ct => _accounts!.SetUserBlockedAsync(user.UserId, false, ct), dialogToken);
                    if (open && IsCurrent(owner))
                    {
                        if (succeeded)
                        {
                            rows.Children.Remove(row);
                            empty.Visibility = rows.Children.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
                        }
                        else if (!dialogToken.IsCancellationRequested)
                        {
                            unblock.IsEnabled = true;
                            unblock.Content = "Unblock";
                            status.Text = "Couldn’t unblock this user. Check your connection and try again.";
                            status.Visibility = Visibility.Visible;
                        }
                        rows.IsHitTestVisible = true;
                    }
                    unblocking = false;
                    // Also refresh after cancellation: the server may have applied the change before the dialog closed.
                    if (IsCurrent(owner) && !viewToken.IsCancellationRequested) await RefreshAsync();
                };
            }
            EnterDialog(dialog);
            try { await dialog.ShowAsync(); }
            catch (Exception)
            {
                if (IsCurrent(owner)) ShowNotice("Close the current dialog and try again.", InfoBarSeverity.Informational);
            }
            finally
            {
                open = false;
                dialogLifetime.Cancel();
                ExitDialog(dialog);
            }
        }
        finally
        {
            _managingBlocked = false;
            if (!_disposed) UpdateControls();
        }
    }

    private async Task<bool> ConfirmAsync(string title, string description, string primary)
    {
        if (!CanUseMedia) return false;
        var dialog = AccessibleDialogFactory.Create(XamlRoot, title, description,
            primaryButtonText: primary, closeButtonText: "Cancel", defaultButton: ContentDialogButton.Close);
        EnterDialog(dialog);
        try { return await dialog.ShowAsync() == ContentDialogResult.Primary && CanUseMedia; }
        catch (Exception)
        {
            if (CanUseMedia) ShowNotice("Close the current dialog and try again.", InfoBarSeverity.Informational);
            return false;
        }
        finally { ExitDialog(dialog); }
    }

    private void EnterDialog(ContentDialog dialog) { _activeDialog = dialog; _networkOperations++; RaiseBusyChanged(); }
    private void ExitDialog(ContentDialog dialog)
    {
        if (ReferenceEquals(_activeDialog, dialog)) _activeDialog = null;
        _networkOperations--;
        RaiseBusyChanged();
    }
    private void RaiseBusyChanged() => BusyChanged?.Invoke(this, EventArgs.Empty);

    private void OnWatchContentSizeChanged(object sender, SizeChangedEventArgs e) => UpdateWatchLayout();

    private void OnFeedLoaded(object sender, RoutedEventArgs e)
    {
        if (_layoutRoot is not null) _layoutRoot.Changed -= OnLayoutRootChanged;
        _layoutRoot = XamlRoot;
        if (_layoutRoot is not null) _layoutRoot.Changed += OnLayoutRootChanged;
        UpdateWatchLayout();
    }

    private void OnFeedUnloaded(object sender, RoutedEventArgs e)
    {
        if (_layoutRoot is not null) _layoutRoot.Changed -= OnLayoutRootChanged;
        _layoutRoot = null;
    }

    private void OnLayoutRootChanged(XamlRoot sender, XamlRootChangedEventArgs args) => UpdateWatchLayout();

    private void OnFeedToolbarSizeChanged(object sender, SizeChangedEventArgs e)
    {
        var narrow = e.NewSize.Width < 530;
        Grid.SetRow(FeedActionsPanel, narrow ? 1 : 0);
        Grid.SetColumn(FeedActionsPanel, narrow ? 0 : 1);
        Grid.SetColumnSpan(FeedActionsPanel, narrow ? 2 : 1);
        FeedActionsPanel.Margin = new Thickness(0, narrow ? 8 : 0, 0, 0);
        FeedActionsPanel.HorizontalAlignment = HorizontalAlignment.Left;
    }

    private void UpdateWatchLayout()
    {
        var width = WatchContent.ActualWidth;
        var narrow = width < 980;
        var videoWidth = narrow || _expanded ? width : width - 312;
        var narrowHeader = videoWidth < 600;
        Grid.SetRow(WatchActionsPanel, narrowHeader ? 1 : 0);
        Grid.SetColumn(WatchActionsPanel, narrowHeader ? 0 : 1);
        Grid.SetColumnSpan(WatchActionsPanel, narrowHeader ? 2 : 1);
        WatchActionsPanel.HorizontalAlignment = HorizontalAlignment.Left;
        WatchCommentsColumn.Width = narrow || _expanded ? new GridLength(0) : new GridLength(292);
        Grid.SetColumn(CommentsPanel, narrow ? 0 : 1);
        Grid.SetRow(CommentsPanel, narrow ? 1 : 0);
        CommentsPanel.Visibility = _expanded ? Visibility.Collapsed : Visibility.Visible;
        ExpandVideoButton.Content = _expanded ? "Reduce" : "Expand";
        var viewportMaximum = Math.Max(180, (XamlRoot?.Size.Height ?? 740) - 280);
        OnlinePlayer.Height = Math.Clamp(videoWidth * 9 / 16, 180, Math.Min(_expanded ? 680 : 540, viewportMaximum));
        CommentsScroll.MaxHeight = narrow ? 220 : Math.Clamp(OnlinePlayer.Height - 120, 180, 340);
    }

    private void ShowNotice(string message, InfoBarSeverity severity)
    {
        FeedNotice.Message = message;
        FeedNotice.Severity = severity;
        FeedNotice.Visibility = Visibility.Visible;
        FeedNotice.IsOpen = true;
    }
    private void HideNotice() { FeedNotice.IsOpen = false; FeedNotice.Visibility = Visibility.Collapsed; }
    private void OnFeedNoticeClosed(InfoBar sender, InfoBarClosedEventArgs args) => FeedNotice.Visibility = Visibility.Collapsed;
    private static string FormatBytes(long value) => $"{Math.Max(0, value) / (1024d * 1024):0.0} MB";

    public void Dispose()
    {
        if (_disposed) return;
        Deactivate(clear: true);
        OnFeedUnloaded(this, new RoutedEventArgs());
        _disposed = true;
        _viewLifetime?.Dispose();
        _pageLifetime?.Dispose();
        // A canceled upload owns and disposes its linked cancellation source after cleanup.
    }
}

public sealed class CommunityFeedClipItem : INotifyPropertyChanged
{
    private CommunityClip _clip;
    private ImageSource? _poster;
    private bool _isBusy;
    private bool _uploadBusy;
    private bool _isSelected;
    private readonly Guid _viewer;
    public CommunityFeedClipItem(CommunityClip clip, Guid viewer) { _clip = clip; _viewer = viewer; }
    public CommunityClip Clip { get => _clip; set { _clip = value; Changed(); } }
    public ImageSource? Poster { get => _poster; set { _poster = value; Changed(nameof(Poster)); } }
    public bool PosterRequested { get; set; }
    public bool IsBusy { get => _isBusy; set { _isBusy = value; Changed(); } }
    public bool UploadBusy { get => _uploadBusy; set { _uploadBusy = value; Changed(); } }
    public bool IsSelected { get => _isSelected; set { if (_isSelected == value) return; _isSelected = value; Changed(); } }
    public string Title => Clip.Title;
    public string Author => string.IsNullOrWhiteSpace(Clip.ProfileUsername) ? Clip.ProfileDisplayName : "@" + Clip.ProfileUsername;
    public string CreatedLabel => Clip.CreatedAt.ToLocalTime().ToString("d MMM", CultureInfo.CurrentCulture);
    public string DurationLabel => $"{(int)Clip.DurationSeconds / 60}:{(int)Clip.DurationSeconds % 60:00}";
    public string WatchLabel => "Watch " + Clip.Title;
    public bool CanPlay => Clip.CanPlay && !IsBusy && !UploadBusy;
    public string LikeLabel => $"{(Clip.IsLikedByMe ? "Liked" : "Like")} · {Clip.LikeCount}";
    public string CommentsLabel => $"Comments · {Clip.CommentCount}";
    public string StateLabel => Clip.Status switch { "uploading" => "Upload incomplete — delete it to free the slot.", "processing" => "Processing…", "failed" => "Upload failed — delete it to try again.", "deleting" => "Deletion incomplete — choose Delete shared clip again to finish cleanup.", _ => string.Empty };
    public Visibility StateVisibility => Clip.IsPublished ? Visibility.Collapsed : Visibility.Visible;
    public Visibility OwnVisibility => Clip.OwnerId == _viewer ? Visibility.Visible : Visibility.Collapsed;
    public Visibility OtherVisibility => Clip.OwnerId != _viewer ? Visibility.Visible : Visibility.Collapsed;
    public Visibility SelectionVisibility => IsSelected ? Visibility.Visible : Visibility.Collapsed;
    public Brush CardBorderBrush => (Brush)Application.Current.Resources[IsSelected ? "KlippAccentBrush" : "KlippBorderSubtleBrush"];
    public Brush CardBackground => (Brush)Application.Current.Resources[IsSelected ? "KlippAccentFaintBrush" : "KlippSurfaceBrush"];
    public event PropertyChangedEventHandler? PropertyChanged;
    private void Changed(string? property = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
}

public sealed class CommunityCommentItem
{
    public CommunityCommentItem(CommunityComment comment, Guid viewer) { Comment = comment; DeleteVisibility = comment.OwnerId == viewer ? Visibility.Visible : Visibility.Collapsed; }
    public CommunityComment Comment { get; }
    public string Author => string.IsNullOrWhiteSpace(Comment.ProfileUsername) ? Comment.ProfileDisplayName : "@" + Comment.ProfileUsername;
    public string Body => Comment.Body;
    public string CreatedLabel => Comment.CreatedAt.ToLocalTime().ToString("g", CultureInfo.CurrentCulture);
    public Visibility DeleteVisibility { get; }
}
