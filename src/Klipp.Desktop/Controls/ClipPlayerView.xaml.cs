using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Klipp.Desktop.Controls;

/// <summary>
/// Spelarvy med inbyggd HTML5 video via WebView2. Custom kontroller i Klipp-stil
/// renderade i player.html. Inkluderar bläddra-pilar för att navigera mellan klipp
/// utan att gå tillbaka till biblioteket (Medal-style).
/// </summary>
public sealed partial class ClipPlayerView : UserControl
{
    private string? _currentFilePath;
    /// <summary>Filsökväg till klippet som spelas just nu. Null om inget är laddat.</summary>
    public string? CurrentFilePath => _currentFilePath;
    private bool _webViewInitialized;
    private string? _pendingClipPath;

    public event EventHandler? BackRequested;
    public event EventHandler<ClipDeleteEventArgs>? DeleteRequested;
    public event EventHandler? PrevRequested;
    public event EventHandler? NextRequested;

    public ClipPlayerView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    /// <summary>
    /// Slår på/av föregående-pilen. Anropas av MainWindow när användaren
    /// är på första klippet.
    /// </summary>
    public bool CanGoPrev
    {
        get => PrevButton.IsEnabled;
        set
        {
            PrevButton.IsEnabled = value;
            PrevButton.Opacity = value ? 1.0 : 0.3;
        }
    }

    /// <summary>
    /// Slår på/av nästa-pilen. Anropas av MainWindow när användaren
    /// är på sista klippet.
    /// </summary>
    public bool CanGoNext
    {
        get => NextButton.IsEnabled;
        set
        {
            NextButton.IsEnabled = value;
            NextButton.Opacity = value ? 1.0 : 0.3;
        }
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_webViewInitialized) return;

        try
        {
            await PlayerWebView.EnsureCoreWebView2Async();

            var videosDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
            PlayerWebView.CoreWebView2.SetVirtualHostNameToFolderMapping(
                "klipp-videos.local",
                videosDirectory,
                Microsoft.Web.WebView2.Core.CoreWebView2HostResourceAccessKind.Allow);

            var appDir = AppContext.BaseDirectory;
            var webPlayerDir = Path.Combine(appDir, "Resources", "WebPlayer");
            PlayerWebView.CoreWebView2.SetVirtualHostNameToFolderMapping(
                "klipp-player.local",
                webPlayerDir,
                Microsoft.Web.WebView2.Core.CoreWebView2HostResourceAccessKind.Allow);

            PlayerWebView.CoreWebView2.Navigate("https://klipp-player.local/player.html");

            _webViewInitialized = true;

            if (_pendingClipPath is not null)
            {
                var path = _pendingClipPath;
                _pendingClipPath = null;
                await LoadClipInWebViewAsync(path);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Klipp] WebView2 init misslyckades: {ex.Message}");
        }
    }

    public async void LoadClip(string filePath)
    {
        if (!File.Exists(filePath))
            return;

        _currentFilePath = filePath;
        var file = new FileInfo(filePath);

        TitleText.Text = file.Name.Replace(".mp4", "").Replace("klipp_", "");

        if (!_webViewInitialized)
        {
            _pendingClipPath = filePath;
            return;
        }

        await LoadClipInWebViewAsync(filePath);
    }

    private async Task LoadClipInWebViewAsync(string filePath)
    {
        try
        {
            var videosDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);

            if (!filePath.StartsWith(videosDirectory, StringComparison.OrdinalIgnoreCase))
            {
                Debug.WriteLine($"[Klipp] Filen ligger utanfor Videos-mappen: {filePath}");
                return;
            }

            var relativePath = filePath.Substring(videosDirectory.Length).Replace('\\', '/');
            if (relativePath.StartsWith("/"))
                relativePath = relativePath.Substring(1);

            var virtualUrl = $"https://klipp-videos.local/{relativePath}";

            var escapedUrl = virtualUrl.Replace("'", "\\'");
            await PlayerWebView.CoreWebView2.ExecuteScriptAsync(
                $"window.loadClip('{escapedUrl}');");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Klipp] LoadClipInWebView misslyckades: {ex.Message}");
        }
    }

    public async void Stop()
    {
        try
        {
            if (_webViewInitialized && PlayerWebView.CoreWebView2 is not null)
            {
                await PlayerWebView.CoreWebView2.ExecuteScriptAsync("window.stopClip();");
            }
        }
        catch { /* webView kanske inte redo */ }

        _currentFilePath = null;
        _pendingClipPath = null;
    }

    private void OnBackClicked(object sender, RoutedEventArgs e)
    {
        Stop();
        BackRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OnPrevClicked(object sender, RoutedEventArgs e)
    {
        PrevRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OnNextClicked(object sender, RoutedEventArgs e)
    {
        NextRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OnOpenFolderClicked(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_currentFilePath)) return;
        Process.Start("explorer.exe", $"/select,\"{_currentFilePath}\"");
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
