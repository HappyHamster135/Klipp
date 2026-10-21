using System;
using System.IO;
using System.Threading.Tasks;
using Klipp.Desktop.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.System;

namespace Klipp.Desktop.Controls;

public sealed partial class SafeClipEditorView
{
    private DispatcherTimer? _sourceViewerTimer;
    private string? _sourceViewerPath;
    private double _sourceViewerDuration;
    private double _sourceMarkIn;
    private double _sourceMarkOut;
    private bool _syncingSourceViewer;
    private bool _sourceViewerInitialized;
    private int _sourceViewerVersion;

    public event EventHandler? SourceMediaImportRequested;

    private void OnSourceViewerLoaded(object sender, RoutedEventArgs e)
    {
        if (_sourceViewerInitialized)
            return;
        _sourceViewerInitialized = true;
        _sourceViewerTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) };
        _sourceViewerTimer.Tick += (_, _) => SyncSourceViewerTime();
        SourcePreviewPlayer.MediaPlayer.MediaOpened += OnSourcePreviewOpened;
        SourcePreviewPlayer.MediaPlayer.MediaFailed += OnSourcePreviewFailed;
        SourcePreviewPlayer.MediaPlayer.MediaEnded += (_, _) => DispatcherQueue.TryEnqueue(SyncSourceViewerTime);
        if (_sourceViewerPath is not null)
            _sourceViewerTimer.Start();
    }

    private void OnSourceViewerUnloaded(object sender, RoutedEventArgs e) => StopSourceViewer();

    private void StopSourceViewer()
    {
        _sourceViewerVersion++;
        _sourceViewerTimer?.Stop();
        try { SourcePreviewPlayer.MediaPlayer.Pause(); SourcePreviewPlayer.Source = null; } catch { }
        _sourceViewerPath = null;
        if (SourceViewerCard is not null)
            SourceViewerCard.Visibility = Visibility.Collapsed;
    }

    private void PauseSourceViewer()
    {
        if (_sourceViewerPath is null)
            return;
        try { SourcePreviewPlayer.MediaPlayer.Pause(); } catch { }
        SyncSourceViewerTime();
    }

    public async Task OpenSourceMediaAsync(string filePath)
    {
        if (!File.Exists(filePath))
            throw new FileNotFoundException("The source video could not be found.", filePath);
        var version = ++_sourceViewerVersion;
        var loadVersion = _projectLoadVersion;
        var info = await _mediaProbeService.GetMediaInfoAsync(filePath);
        if (version != _sourceViewerVersion || loadVersion != _projectLoadVersion)
            return;
        if (info is null || info.Duration <= TimeSpan.Zero)
            throw new InvalidOperationException("The selected file could not be opened as video.");
        PausePreview();
        _sourceViewerPath = filePath;
        _sourceViewerDuration = info.Duration.TotalSeconds;
        _sourceMarkIn = 0;
        _sourceMarkOut = _sourceViewerDuration;
        SourceViewerTitleText.Text = Path.GetFileName(filePath);
        SourceViewerStatusText.Text = "Mark a range, then add it at the timeline playhead.";
        SourceViewerCard.Visibility = Visibility.Visible;
        SourceSeekSlider.Maximum = _sourceViewerDuration;
        SourcePreviewPlayer.Source = MediaSource.CreateFromUri(new Uri(Path.GetFullPath(filePath)));
        _sourceViewerTimer?.Start();
        UpdateSourceRangeLabels();
        SetEditorTool("library");
        DispatcherQueue.TryEnqueue(() =>
        {
            SourceViewerCard.Focus(FocusState.Programmatic);
            InspectorScrollViewer.ChangeView(null, 0, null, disableAnimation: true);
        });
    }

    private async void OnSourceLibraryItemClicked(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not EditorLibraryItem item)
            return;
        try { await OpenSourceMediaAsync(item.FilePath); }
        catch (Exception exception) { ProjectStatusText.Text = exception.Message; }
    }

    private async void OnSourceLibraryItemTapped(object sender, TappedRoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string path } element)
            return;
        var current = e.OriginalSource as DependencyObject;
        while (current is not null && !ReferenceEquals(current, element))
        {
            if (current is ButtonBase)
                return;
            current = VisualTreeHelper.GetParent(current);
        }
        e.Handled = true;
        try { await OpenSourceMediaAsync(path); }
        catch (Exception exception) { ProjectStatusText.Text = exception.Message; }
    }

    private void OnSourceLibraryTileSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (sender is not Grid tile || tile.RowDefinitions.Count == 0 || e.NewSize.Width <= 8)
            return;
        var height = (e.NewSize.Width - tile.Padding.Left - tile.Padding.Right) * 9.0 / 16;
        if (Math.Abs(tile.RowDefinitions[0].Height.Value - height) > 0.5)
            tile.RowDefinitions[0].Height = new GridLength(height);
    }

    private void OnChooseSourceMediaClicked(object sender, RoutedEventArgs e) =>
        SourceMediaImportRequested?.Invoke(this, EventArgs.Empty);

    private void OnCloseSourceViewerClicked(object sender, RoutedEventArgs e) => StopSourceViewer();

    private void OnSourcePreviewOpened(MediaPlayer player, object args) => DispatcherQueue.TryEnqueue(() =>
    {
        if (_sourceViewerPath is null)
            return;
        player.Pause();
        player.PlaybackSession.Position = TimeSpan.Zero;
        _sourceViewerTimer?.Start();
        SyncSourceViewerTime();
    });

    private void OnSourcePreviewFailed(MediaPlayer player, MediaPlayerFailedEventArgs args) => DispatcherQueue.TryEnqueue(() =>
    {
        _sourceViewerTimer?.Stop();
        SourceViewerStatusText.Text = "Source playback failed. Try another video or convert this file before editing.";
    });

    private void OnSourcePlayPauseClicked(object sender, RoutedEventArgs e)
    {
        if (_sourceViewerPath is null)
            return;
        PausePreview();
        var player = SourcePreviewPlayer.MediaPlayer;
        if (player.PlaybackSession.PlaybackState == MediaPlaybackState.Playing)
            player.Pause();
        else
        {
            if (player.PlaybackSession.Position.TotalSeconds >= _sourceViewerDuration - 0.01)
                player.PlaybackSession.Position = TimeSpan.FromSeconds(_sourceMarkIn);
            player.Play();
            _sourceViewerTimer?.Start();
        }
        SyncSourceViewerTime();
    }

    private void OnSourceSeekChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_syncingSourceViewer || _sourceViewerPath is null)
            return;
        try { SourcePreviewPlayer.MediaPlayer.PlaybackSession.Position = TimeSpan.FromSeconds(e.NewValue); } catch { }
    }

    private void SyncSourceViewerTime()
    {
        if (_sourceViewerPath is null)
            return;
        _syncingSourceViewer = true;
        try
        {
            var player = SourcePreviewPlayer.MediaPlayer;
            SourceSeekSlider.Value = Math.Clamp(player.PlaybackSession.Position.TotalSeconds, 0, _sourceViewerDuration);
            SourcePositionText.Text = FormatTimestamp(player.PlaybackSession.Position) + " / " +
                FormatTimestamp(TimeSpan.FromSeconds(_sourceViewerDuration));
            SourcePlayIcon.Glyph = player.PlaybackSession.PlaybackState == MediaPlaybackState.Playing ? "\uE769" : "\uE768";
        }
        catch { }
        finally { _syncingSourceViewer = false; }
    }

    private void OnSourceMarkInClicked(object sender, RoutedEventArgs e)
    {
        _sourceMarkIn = Math.Clamp(SourceSeekSlider.Value, 0, Math.Max(0, _sourceMarkOut - SourceMinimumRange));
        UpdateSourceRangeLabels();
    }

    private void OnSourceMarkOutClicked(object sender, RoutedEventArgs e)
    {
        _sourceMarkOut = Math.Clamp(SourceSeekSlider.Value, Math.Min(_sourceViewerDuration, _sourceMarkIn + SourceMinimumRange), _sourceViewerDuration);
        UpdateSourceRangeLabels();
    }

    private double SourceMinimumRange => Math.Min(0.1, _sourceViewerDuration);

    private void OnSourceResetMarksClicked(object sender, RoutedEventArgs e)
    {
        _sourceMarkIn = 0;
        _sourceMarkOut = _sourceViewerDuration;
        UpdateSourceRangeLabels();
    }

    private void UpdateSourceRangeLabels()
    {
        SourceInText.Text = "In " + FormatTimestamp(TimeSpan.FromSeconds(_sourceMarkIn));
        SourceOutText.Text = "Out " + FormatTimestamp(TimeSpan.FromSeconds(_sourceMarkOut));
        SourceRangeText.Text = FormatTimestamp(TimeSpan.FromSeconds(Math.Max(0, _sourceMarkOut - _sourceMarkIn))) + " selected";
    }

    private async void OnAddSourceVideoClicked(object sender, RoutedEventArgs e) =>
        await AddSourceRangeAsync(EditorClipTrack.Primary);

    private async void OnAddSourceBrollClicked(object sender, RoutedEventArgs e) =>
        await AddSourceRangeAsync(EditorClipTrack.Overlay);

    private async Task AddSourceRangeAsync(EditorClipTrack track)
    {
        if (_sourceViewerPath is null)
            return;
        try { SourcePreviewPlayer.MediaPlayer.Pause(); } catch { }
        SourceAddVideoButton.IsEnabled = SourceAddBrollButton.IsEnabled = false;
        try
        {
            var added = await TryAddLibraryClipAsync(_sourceViewerPath, track, CurrentTimelinePosition,
                _sourceMarkIn, _sourceMarkOut);
            if (added is not null)
                SourceViewerStatusText.Text = "Selected range added to " + (track == EditorClipTrack.Primary ? "Video" : "B-roll") + ".";
        }
        finally { SourceAddVideoButton.IsEnabled = SourceAddBrollButton.IsEnabled = true; }
    }

    private void OnSourceViewerKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.OriginalSource is TextBox or NumberBox)
            return;
        if (GetEditorShortcutModifiers() != 0)
            return;
        switch (e.Key)
        {
            case VirtualKey.I: OnSourceMarkInClicked(sender, e); break;
            case VirtualKey.O: OnSourceMarkOutClicked(sender, e); break;
            case VirtualKey.Space: OnSourcePlayPauseClicked(sender, e); break;
            default: return;
        }
        e.Handled = true;
    }
}
