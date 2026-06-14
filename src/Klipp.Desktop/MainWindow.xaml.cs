using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Klipp.Desktop.Services;

namespace Klipp.Desktop;

/// <summary>
/// Klipp huvudfönster — visar klippbiblioteket och styr inspelning.
/// </summary>
public sealed partial class MainWindow : Window
{
    private const int SaveLastSeconds = 30;

    private readonly RecordingService _recording = new();
    public ClipLibraryService LibraryService { get; } = new();

    private readonly DispatcherTimer _statusTimer;

    public MainWindow()
    {
        InitializeComponent();

        // Timer som uppdaterar status-pill (sekunder buffrat) en gång per sekund
        _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _statusTimer.Tick += (_, _) => UpdateStatusUI();

        // Ladda existerande klipp + observera ändringar i listan
        LibraryService.Clips.CollectionChanged += (_, _) => UpdateLibraryUI();

        _ = InitializeAsync();
    }

    private async System.Threading.Tasks.Task InitializeAsync()
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
                await _recording.StopAsync();
                _statusTimer.Stop();
            }
            else
            {
                await _recording.StartAsync();
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
        if (!_recording.IsRecording) return;

        SaveClipButton.IsEnabled = false;

        try
        {
            var outputPath = LibraryService.GenerateClipPath();
            await _recording.SaveLastSecondsAsync(SaveLastSeconds, outputPath);
            LibraryService.AddNewClip(outputPath);
        }
        catch (Exception ex)
        {
            await ShowErrorAsync("Kunde inte spara klipp", ex.Message);
        }
        finally
        {
            SaveClipButton.IsEnabled = _recording.IsRecording;
        }
    }

    private void UpdateStatusUI()
    {
        if (_recording.IsRecording)
        {
            StatusDot.Fill = (Brush)Application.Current.Resources["KlippDangerBrush"];
            StatusText.Text = $"Spelar in \u2022 {_recording.BufferedSeconds:F0}s buffrat";
            RecordButton.Content = "Stoppa";
            RecordButton.Background = (Brush)Application.Current.Resources["KlippSurfaceElevatedBrush"];
            SaveClipButton.IsEnabled = true;
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

    private async System.Threading.Tasks.Task ShowErrorAsync(string title, string message)
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
}
