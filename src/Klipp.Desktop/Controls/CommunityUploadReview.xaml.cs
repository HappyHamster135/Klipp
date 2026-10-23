using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Klipp.Desktop.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Globalization.NumberFormatting;

namespace Klipp.Desktop.Controls;

/// <summary>A read-only source preview and explicit, bounded choice of what to share.</summary>
public sealed partial class CommunityUploadReview : UserControl, IDisposable
{
#if DEBUG
    private static readonly object PreviewTraceLock = new();
    private static int _previewTraceSequence;
    private readonly int _previewTraceId = System.Threading.Interlocked.Increment(ref _previewTraceSequence);
#endif
    private readonly string _sourcePath = string.Empty;
    private readonly MediaProbeInfo _info = new(TimeSpan.Zero, string.Empty, string.Empty, [], 0, 0);
    private readonly List<(int Index, string Name, CheckBox Control)> _audio = [];
    private readonly DispatcherTimer _rangeDebounce = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private readonly DispatcherTimer _previewOpenDeadline = new() { Interval = TimeSpan.FromSeconds(10) };
    private MediaPlayer? _player;
    private MediaSource? _source;
    private MediaPlaybackItem? _item;
    private XamlRoot? _layoutRoot;
    private bool _initialized;
    private bool _updating;
    private bool _disposed;
    private bool _mediaOpened;
    private volatile bool _wantsPlay;
    private bool _ended;
    private bool _previewFailed;
    private int _previewAudioIndex = -1;

    // The XAML type provider needs a parameterless activation path. A configured
    // review is created with the overload below; an unconfigured view stays inert.
    public CommunityUploadReview() => InitializeComponent();

    public CommunityUploadReview(string sourcePath, string initialTitle, MediaProbeInfo info) : this()
    {
        _sourcePath = Path.GetFullPath(sourcePath);
        _info = info;
        var seconds = new DecimalFormatter(["en-US"], "US")
        {
            IntegerDigits = 1, FractionDigits = 3, IsGrouped = false
        };
        StartInput.NumberFormatter = LengthInput.NumberFormatter = seconds;
        FileNameText.Text = Path.GetFileName(sourcePath);
        ToolTipService.SetToolTip(FileNameText, FileNameText.Text);
        var bytes = new FileInfo(sourcePath).Length;
        SourceSummaryText.Text = $"{FormatTime(info.Duration.TotalSeconds)} total · {info.Width} × {info.Height} · {bytes / (1024d * 1024):0.0} MB original";
        TitleInput.Text = initialTitle[..Math.Min(initialTitle.Length, 120)];
        var minimumSection = Math.Min(0.1, info.Duration.TotalSeconds);
        StartInput.Maximum = SectionStartSlider.Maximum = Math.Max(0, info.Duration.TotalSeconds - minimumSection);
        LengthInput.Minimum = minimumSection;
        StartInput.Value = 0;
        LengthInput.Value = Math.Min(60, info.Duration.TotalSeconds);
        var hasStems = info.AudioStreams.Any(stream => stream.CaptureRole == "game audio");
        var defaultAudio = info.AudioStreams.FirstOrDefault(stream => stream.CaptureRole == "game audio")?.Index
            ?? info.AudioStreams.FirstOrDefault(stream => stream.CaptureRole is not ("microphone" or "discord" or "music"))?.Index;
        foreach (var stream in info.AudioStreams)
        {
            if (hasStems && stream.CaptureRole == "playback mix") continue;
            var name = stream.CaptureRole switch
            {
                "game audio" => "Game audio", "microphone" => "Microphone", "discord" => "Discord",
                "music" => "Music", "playback mix" => "Original mix", _ =>
                    string.IsNullOrWhiteSpace(stream.Title) ? $"Audio track {stream.Index + 1}" : stream.Title
            };
            var check = new CheckBox { Content = name, IsChecked = stream.Index == defaultAudio, MinHeight = 32 };
            AutomationProperties.SetName(check, $"Include {name} in the upload");
            check.Checked += OnAudioSelectionChanged;
            check.Unchecked += OnAudioSelectionChanged;
            _audio.Add((stream.Index, name, check));
            AudioChoicesPanel.Children.Add(check);
        }
        _rangeDebounce.Tick += OnRangeDebounce;
        _previewOpenDeadline.Tick += OnPreviewOpenDeadline;
        _initialized = true;
        UpdateAudioChoices();
        UpdateRange(changedStart: true, reload: false);
    }

    public event EventHandler? ChoiceChanged;
    public string ClipTitle => TitleInput.Text.Trim();
    public string Description => DescriptionInput.Text.Trim();
    public double StartSeconds => StartInput.Value;
    public double DurationSeconds => LengthInput.Value;
    public IReadOnlyList<int> SelectedAudioStreams => _audio.Where(item => item.Control.IsChecked == true).Select(item => item.Index).ToArray();
    public bool IsChoiceValid => !string.IsNullOrWhiteSpace(ClipTitle) && IsRangeValid;
    private bool IsRangeValid => double.IsFinite(StartSeconds)
        && double.IsFinite(DurationSeconds) && StartSeconds >= 0 && DurationSeconds > 0 && DurationSeconds <= 60
        && StartSeconds + DurationSeconds <= _info.Duration.TotalSeconds + 0.05;

    public void UpdateViewport(XamlRoot root)
    {
        if (_disposed) return;
        if (!ReferenceEquals(_layoutRoot, root))
        {
            if (_layoutRoot is not null) _layoutRoot.Changed -= OnLayoutRootChanged;
            _layoutRoot = root;
            root.Changed += OnLayoutRootChanged;
        }
        Width = MaxWidth = Math.Max(240, Math.Min(960, root.Size.Width - 96));
        MaxHeight = Math.Max(180, Math.Min(660, root.Size.Height - 210));
        UpdateColumns();
    }

    private void OnReviewLoaded(object sender, RoutedEventArgs e)
    {
        TracePreview("Loaded");
        if (_disposed || !_initialized) return;
        if (XamlRoot is { } root) UpdateViewport(root);
        if (_player is null && !_previewFailed) LoadPreview(play: false);
    }

    // ContentDialog can temporarily unload/reparent its content while arranging
    // its surface. MainWindow's Closing/finally owns permanent disposal.
    private void OnReviewUnloaded(object sender, RoutedEventArgs e) => TracePreview("Unloaded; owner retains lifetime");
    private void OnLayoutRootChanged(XamlRoot sender, XamlRootChangedEventArgs args) => UpdateViewport(sender);
    private void OnReviewSizeChanged(object sender, SizeChangedEventArgs e) => UpdateColumns();
    private void UpdateColumns()
    {
        var width = ActualWidth > 0 ? ActualWidth : Width;
        if (!double.IsFinite(width) || width <= 0) return;
        var narrow = width < 780;
        DetailsColumn.Width = narrow ? new GridLength(0) : new GridLength(320);
        Grid.SetColumn(DetailsPanel, narrow ? 0 : 1);
        Grid.SetRow(DetailsPanel, narrow ? 1 : 0);
        var previewWidth = narrow ? width : width - 344;
        PreviewFrame.Height = Math.Clamp(previewWidth * 9 / 16, 140,
            Math.Max(160, Math.Min(340, (_layoutRoot?.Size.Height ?? 740) - 320)));
    }

    private void OnTitleChanged(object sender, TextChangedEventArgs e) { if (_initialized) ValidateChoice(); }
    private void OnSectionSliderChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (!_initialized || _updating) return;
        _updating = true;
        StartInput.Value = e.NewValue;
        _updating = false;
        UpdateRange(changedStart: true);
    }
    private void OnSectionInputChanged(NumberBox sender, NumberBoxValueChangedEventArgs e)
    {
        if (!_initialized || _updating) return;
        UpdateRange(ReferenceEquals(sender, StartInput));
    }
    private void UpdateRange(bool changedStart, bool reload = true)
    {
        _updating = true;
        if (double.IsFinite(StartInput.Value))
        {
            SectionStartSlider.Value = Math.Clamp(StartInput.Value, 0, SectionStartSlider.Maximum);
            LengthInput.Maximum = Math.Min(60, Math.Max(LengthInput.Minimum, _info.Duration.TotalSeconds - StartInput.Value));
            if (changedStart && double.IsFinite(LengthInput.Value) && LengthInput.Value > LengthInput.Maximum)
                LengthInput.Value = LengthInput.Maximum;
        }
        _updating = false;
        SectionSummaryText.Text = IsFiniteRange ? $"{FormatTime(StartSeconds)} – {FormatTime(StartSeconds + DurationSeconds)}" : "Choose a section";
        ValidateChoice();
        if (!reload) return;
        PausePreview();
        _rangeDebounce.Stop();
        PlayPreviewButton.IsEnabled = ReplayPreviewButton.IsEnabled = false;
        if (IsRangeValid)
        {
            PreviewStateText.Text = "Updating section…";
            _rangeDebounce.Start();
        }
        else PreviewStateText.Text = "Choose a valid section to preview.";
    }
    private bool IsFiniteRange => double.IsFinite(StartSeconds) && double.IsFinite(DurationSeconds) && DurationSeconds > 0;
    private void ValidateChoice()
    {
        ValidationText.Text = !IsFiniteRange || StartSeconds < 0 || DurationSeconds > 60 || StartSeconds + DurationSeconds > _info.Duration.TotalSeconds + 0.05
            ? "Choose a section inside the video, up to 60 seconds long."
            : string.IsNullOrWhiteSpace(ClipTitle) ? "Add a title before uploading."
            : $"{DurationSeconds:0.#} seconds selected · up to 5 shared clips per account";
        ChoiceChanged?.Invoke(this, EventArgs.Empty);
    }
    private void OnRangeDebounce(object? sender, object e) { _rangeDebounce.Stop(); LoadPreview(play: false); }

    private void OnAudioSelectionChanged(object sender, RoutedEventArgs e)
    {
        if (!_initialized) return;
        PausePreview();
        UpdateAudioChoices();
        SelectPreviewAudio();
        ChoiceChanged?.Invoke(this, EventArgs.Empty);
    }
    private void UpdateAudioChoices()
    {
        _updating = true;
        var selected = _audio.Where(item => item.Control.IsChecked == true).ToArray();
        PreviewAudioChoice.Items.Clear();
        foreach (var audio in selected) PreviewAudioChoice.Items.Add(new ComboBoxItem { Content = audio.Name, Tag = audio.Index });
        PreviewAudioChoice.SelectedItem = PreviewAudioChoice.Items.OfType<ComboBoxItem>().FirstOrDefault(item => item.Tag is int index && index == _previewAudioIndex)
            ?? PreviewAudioChoice.Items.OfType<ComboBoxItem>().FirstOrDefault();
        _previewAudioIndex = PreviewAudioChoice.SelectedItem is ComboBoxItem { Tag: int selectedIndex } ? selectedIndex : -1;
        PreviewAudioChoice.IsEnabled = selected.Length > 0;
        AudioSummaryText.Text = selected.Length == 0 ? "Silent clip · no audio will be uploaded."
            : "Included: " + string.Join(", ", selected.Select(item => item.Name));
        PreviewAudioHelpText.Text = selected.Length > 1
            ? "Listen to one track at a time here. All selected tracks will be mixed together in the upload."
            : selected.Length == 1 ? "The preview uses the selected audio track. Nothing plays until you press Play."
            : _audio.Count == 0 ? "This source video has no audio tracks." : "Select a track to include sound, or keep the upload silent.";
        _updating = false;
    }
    private void OnPreviewAudioChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_initialized || _updating) return;
        PausePreview();
        _previewAudioIndex = PreviewAudioChoice.SelectedItem is ComboBoxItem { Tag: int selectedIndex } ? selectedIndex : -1;
        SelectPreviewAudio();
    }

    private void LoadPreview(bool play)
    {
        if (_disposed || !IsRangeValid) return;
        TracePreview(play ? "LoadPreview; manual play requested" : "LoadPreview; paused first frame requested");
        ReleasePlayer();
        _mediaOpened = false;
        _wantsPlay = play;
        _ended = false;
        _previewFailed = false;
        PreviewPlaceholder.Visibility = Visibility.Visible;
        PreviewStateText.Text = "Loading selected section…";
        PlayPreviewButton.Content = "Loading…";
        PlayPreviewButton.IsEnabled = ReplayPreviewButton.IsEnabled = false;
        try
        {
            _player = new MediaPlayer { AutoPlay = false, IsMuted = true, Volume = 0.7 };
            _player.CommandManager.IsEnabled = false;
            _player.MediaOpened += OnMediaOpened;
            _player.MediaEnded += OnMediaEnded;
            _player.MediaFailed += OnMediaFailed;
            VideoPreview.SetMediaPlayer(_player);
            // Native duration limiting keeps playback inside the chosen range even
            // if the UI thread is busy. A MediaSource belongs to only one item.
            _source = MediaSource.CreateFromUri(new Uri(_sourcePath));
            _item = new MediaPlaybackItem(_source, TimeSpan.FromSeconds(StartSeconds), TimeSpan.FromSeconds(DurationSeconds));
            _item.AudioTracksChanged += OnAudioTracksChanged;
            _player.Source = _item;
            _previewOpenDeadline.Start();
            // Some local MediaPlaybackItems open lazily. Prime the decoder while
            // muted, then pause at MediaOpened unless the user explicitly played.
            // Sound is enabled only after opening and a manual play request.
            _player.Play();
        }
        catch { ShowPreviewFailure(); }
    }
    private void OnPlayPreviewClicked(object sender, RoutedEventArgs e)
    {
        if (_disposed || !IsRangeValid) return;
        _rangeDebounce.Stop();
        if (_previewFailed) { LoadPreview(play: false); return; }
        if (_wantsPlay) { PausePreview(); return; }
        if (_player is null || _ended) { LoadPreview(play: true); return; }
        _wantsPlay = true;
        StartWhenReady();
    }
    private void OnReplayPreviewClicked(object sender, RoutedEventArgs e) { _rangeDebounce.Stop(); LoadPreview(play: true); }
    private void OnMediaOpened(MediaPlayer sender, object args)
    {
        TracePreview("MediaOpened");
        // Do not wait for a busy UI dispatcher to stop muted priming playback.
        if (!_wantsPlay) { try { sender.Pause(); } catch { } }
        DispatchPlayer(sender, () =>
        {
            _previewOpenDeadline.Stop();
            _mediaOpened = true;
            PreviewPlaceholder.Visibility = Visibility.Collapsed;
            PlayPreviewButton.IsEnabled = ReplayPreviewButton.IsEnabled = IsRangeValid;
            StartWhenReady();
        });
    }
    private void OnPreviewOpenDeadline(object? sender, object e)
    {
        TracePreview("OpenDeadline");
        _previewOpenDeadline.Stop();
        if (!_disposed && !_mediaOpened)
            ShowPreviewFailure("The preview did not open. Retry, or create a share copy if this format is not supported here.");
    }
    private void OnAudioTracksChanged(MediaPlaybackItem sender, object args) => DispatcherQueue.TryEnqueue(() =>
    {
        if (!_disposed && ReferenceEquals(_item, sender)) StartWhenReady();
    });
    private void OnMediaEnded(MediaPlayer sender, object args) => DispatchPlayer(sender, () =>
    {
        _ended = true;
        PausePreview();
        PlayPreviewButton.Content = "Replay section";
        PreviewStateText.Text = "End of selected section";
    });
    private void OnMediaFailed(MediaPlayer sender, MediaPlayerFailedEventArgs args)
    {
        TracePreview("MediaFailed");
        DispatchPlayer(sender, ShowPreviewFailure);
    }
    private void DispatchPlayer(MediaPlayer player, Action action) => DispatcherQueue.TryEnqueue(() =>
    {
        if (!_disposed && ReferenceEquals(_player, player)) action();
    });
    private bool SelectPreviewAudio()
    {
        if (_player is null) return false;
        _player.IsMuted = true;
        if (_previewAudioIndex < 0) return true;
        if (_item is null || _previewAudioIndex >= _item.AudioTracks.Count) return false;
        try { _item.AudioTracks.SelectedIndex = _previewAudioIndex; _player.IsMuted = !_mediaOpened || !_wantsPlay; return true; }
        catch { return false; }
    }
    private void StartWhenReady()
    {
        if (_player is null || !_mediaOpened) return;
        var audioReady = SelectPreviewAudio();
        try { if (_wantsPlay) _player.Play(); else _player.Pause(); }
        catch { ShowPreviewFailure(); return; }
        PlayPreviewButton.Content = _wantsPlay ? "Pause" : "Play section";
        AutomationProperties.SetName(PlayPreviewButton, _wantsPlay ? "Pause selected section" : "Play selected section");
        PreviewStateText.Text = !audioReady ? "Preview sound unavailable · video only"
            : _wantsPlay ? "Playing selected section" : "Preview ready";
    }
    private void PausePreview()
    {
        _wantsPlay = false;
        try
        {
            if (_player is { } player)
            {
                player.IsMuted = true;
                if (_mediaOpened) player.Pause();
            }
        }
        catch { }
        PlayPreviewButton.Content = "Play section";
        AutomationProperties.SetName(PlayPreviewButton, "Play selected section");
        if (_mediaOpened) PreviewStateText.Text = "Paused";
    }
    private void ShowPreviewFailure() => ShowPreviewFailure("Preview unavailable for this format. Retry, or create a share copy to upload.");
    private void ShowPreviewFailure(string message)
    {
        ReleasePlayer();
        _previewFailed = true;
        _mediaOpened = false;
        _wantsPlay = false;
        PreviewStateText.Text = message;
        PreviewPlaceholder.Visibility = Visibility.Visible;
        PlayPreviewButton.Content = "Retry preview";
        AutomationProperties.SetName(PlayPreviewButton, "Retry opening the selected section preview");
        PlayPreviewButton.IsEnabled = IsRangeValid;
        ReplayPreviewButton.IsEnabled = false;
    }
    private void ReleasePlayer()
    {
        TracePreview("ReleasePlayer");
        _previewOpenDeadline.Stop();
        var player = _player;
        _player = null;
        if (_item is not null) _item.AudioTracksChanged -= OnAudioTracksChanged;
        _item = null;
        if (player is not null)
        {
            player.MediaOpened -= OnMediaOpened;
            player.MediaEnded -= OnMediaEnded;
            player.MediaFailed -= OnMediaFailed;
            try { player.Pause(); player.Source = null; } catch { }
            try { VideoPreview.SetMediaPlayer(null); } catch { }
            try { player.Dispose(); } catch { }
        }
        try { _source?.Dispose(); } catch { }
        _source = null;
    }
    private static string FormatTime(double seconds) => TimeSpan.FromSeconds(Math.Max(0, seconds))
        .ToString(seconds >= 3600 ? @"h\:mm\:ss" : @"m\:ss", CultureInfo.InvariantCulture);

    [System.Diagnostics.Conditional("DEBUG")]
    private void TracePreview(string eventName)
    {
#if DEBUG
        // Fixed lifecycle labels only: never file paths, titles, emails, URLs,
        // tokens, exceptions, or media metadata. Release builds omit all calls.
        try
        {
            lock (PreviewTraceLock)
                File.AppendAllText(Path.Combine(Path.GetTempPath(), "KlippCommunityPreview.log"),
                    $"{DateTime.UtcNow:o} review:{_previewTraceId} {eventName}{Environment.NewLine}");
        }
        catch { }
#endif
    }

    public void Dispose()
    {
        if (_disposed) return;
        TracePreview("Dispose");
        _disposed = true;
        _rangeDebounce.Stop();
        _rangeDebounce.Tick -= OnRangeDebounce;
        _previewOpenDeadline.Stop();
        _previewOpenDeadline.Tick -= OnPreviewOpenDeadline;
        if (_layoutRoot is not null) _layoutRoot.Changed -= OnLayoutRootChanged;
        _layoutRoot = null;
        ReleasePlayer();
    }
}
