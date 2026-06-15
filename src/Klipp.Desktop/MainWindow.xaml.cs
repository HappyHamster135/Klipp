using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Klipp.Desktop.Controls;
using Klipp.Desktop.Services;

namespace Klipp.Desktop;

public sealed partial class MainWindow : Window
{
    private readonly RecordingService _recording = new();
    public ClipLibraryService LibraryService { get; } = new();

    private readonly DispatcherTimer _statusTimer;

    public MainWindow()
    {
        InitializeComponent();

        _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _statusTimer.Tick += (_, _) => UpdateStatusUI();

        LibraryService.Clips.CollectionChanged += (_, _) => UpdateLibraryUI();

        _ = InitializeAsync();
    }

    private async Task InitializeAsync()
    {
        await LibraryService.LoadFromDiskAsync();
        UpdateLibraryUI();
        UpdateStatusUI();
    }

    private async void OnRecordClicked(object sender, RoutedEventArgs e)
    {
        RecordButton.IsEnabled = false;

        try
        {
            if (_recording.IsRecording)
            {
                StatusText.Text = "Sparar...";
                var outputPath = await _recording.StopAsync();
                _statusTimer.Stop();

                if (outputPath is not null)
                    LibraryService.AddNewClip(outputPath);
            }
            else
            {
                if (!_recording.IsFFmpegInstalled)
                {
                    StatusText.Text = "Laddar ner FFmpeg...";
                    await _recording.EnsureFFmpegInstalledAsync();
                }

                var outputPath = LibraryService.GenerateClipPath();
                await _recording.StartAsync(outputPath);
                _statusTimer.Start();
            }
        }
        catch (Exception ex)
        {
            await ShowErrorAsync("Fel vid inspelning", ex.Message);
        }
        finally
        {
            RecordButton.IsEnabled = true;
            UpdateStatusUI();
        }
    }

    private async void OnSaveClipClicked(object sender, RoutedEventArgs e)
    {
        OnRecordClicked(sender, e);
        await Task.CompletedTask;
    }

    private void OnClipCardClicked(object sender, ClipCardClickEventArgs e)
    {
        if (string.IsNullOrEmpty(e.FilePath) || !File.Exists(e.FilePath))
            return;

        ShowPlayer(e.FilePath);
    }

    private void OnPlayerBackRequested(object? sender, EventArgs e)
    {
        ShowLibrary();
    }

    private async void OnPlayerDeleteRequested(object? sender, ClipDeleteEventArgs e)
    {
        var confirm = await ShowConfirmAsync(
            "Ta bort klipp?",
            $"Vill du verkligen ta bort '{Path.GetFileName(e.FilePath)}'?");

        if (!confirm) return;

        try
        {
            File.Delete(e.FilePath);

            for (int i = LibraryService.Clips.Count - 1; i >= 0; i--)
            {
                if (LibraryService.Clips[i].FilePath == e.FilePath)
                {
                    LibraryService.Clips.RemoveAt(i);
                    break;
                }
            }

            ShowLibrary();
        }
        catch (Exception ex)
        {
            await ShowErrorAsync("Kunde inte ta bort filen", ex.Message);
        }
    }

    private void ShowPlayer(string filePath)
    {
        LibraryView.Visibility = Visibility.Collapsed;
        PlayerView.Visibility = Visibility.Visible;
        PlayerView.LoadClip(filePath);
        UpdatePlayerNavigationButtons(filePath);
    }

    /// <summary>
    /// Uppdaterar pilarnas enable-status baserat på var i listan vi är.
    /// </summary>
    private void UpdatePlayerNavigationButtons(string currentFilePath)
    {
        var index = FindClipIndex(currentFilePath);
        PlayerView.CanGoPrev = index > 0;
        PlayerView.CanGoNext = index >= 0 && index < LibraryService.Clips.Count - 1;
    }

    /// <summary>
    /// Hittar index för ett klipp baserat på filsökväg. Returnerar -1 om inte hittat.
    /// </summary>
    private int FindClipIndex(string filePath)
    {
        for (int i = 0; i < LibraryService.Clips.Count; i++)
        {
            if (LibraryService.Clips[i].FilePath == filePath)
                return i;
        }
        return -1;
    }

    private void OnPlayerPrevRequested(object? sender, EventArgs e)
    {
        NavigateToClip(-1);
    }

    private void OnPlayerNextRequested(object? sender, EventArgs e)
    {
        NavigateToClip(+1);
    }

    /// <summary>
    /// Navigerar i biblioteket. offset = -1 för föregående, +1 för nästa.
    /// </summary>
    private void NavigateToClip(int offset)
    {
        var currentPath = PlayerView.CurrentFilePath;
        if (string.IsNullOrEmpty(currentPath)) return;

        var currentIndex = FindClipIndex(currentPath);
        if (currentIndex < 0) return;

        var newIndex = currentIndex + offset;
        if (newIndex < 0 || newIndex >= LibraryService.Clips.Count) return;

        var newClip = LibraryService.Clips[newIndex];
        PlayerView.LoadClip(newClip.FilePath);
        UpdatePlayerNavigationButtons(newClip.FilePath);
    }

    private void ShowLibrary()
    {
        PlayerView.Stop();
        PlayerView.Visibility = Visibility.Collapsed;
        LibraryView.Visibility = Visibility.Visible;
    }

    private void UpdateStatusUI()
    {
        if (_recording.IsRecording)
        {
            StatusDot.Fill = (Brush)Application.Current.Resources["KlippDangerBrush"];
            StatusText.Text = $"Spelar in \u2022 {_recording.RecordedSeconds:F0}s";
            RecordButton.Content = "Spara klipp";
            RecordButton.Background = (Brush)Application.Current.Resources["KlippAccentBrush"];
            SaveClipButton.IsEnabled = false;
        }
        else
        {
            StatusDot.Fill = (Brush)Application.Current.Resources["KlippTextMutedBrush"];
            StatusText.Text = "Inte aktiv";
            RecordButton.Content = "Spela in";
            RecordButton.Background = (Brush)Application.Current.Resources["KlippAccentBrush"];
            SaveClipButton.IsEnabled = false;
        }
    }

    private void UpdateLibraryUI()
    {
        var count = LibraryService.Clips.Count;
        LibraryStatsText.Text = count == 1 ? "1 klipp" : $"{count} klipp";

        var hasClips = count > 0;
        EmptyState.Visibility = hasClips ? Visibility.Collapsed : Visibility.Visible;
        ClipsScroller.Visibility = hasClips ? Visibility.Visible : Visibility.Collapsed;
    }

    private async Task ShowErrorAsync(string title, string message)
    {
        var dialog = new ContentDialog
        {
            Title = title,
            Content = message,
            CloseButtonText = "OK",
            XamlRoot = this.Content.XamlRoot
        };
        await dialog.ShowAsync();
    }

    private async Task<bool> ShowConfirmAsync(string title, string message)
    {
        var dialog = new ContentDialog
        {
            Title = title,
            Content = message,
            PrimaryButtonText = "Ta bort",
            CloseButtonText = "Avbryt",
            XamlRoot = this.Content.XamlRoot,
            DefaultButton = ContentDialogButton.Close
        };

        var result = await dialog.ShowAsync();
        return result == ContentDialogResult.Primary;
    }
}
