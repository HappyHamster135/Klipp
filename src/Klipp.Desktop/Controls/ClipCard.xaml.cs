using System;
using System.IO;

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace Klipp.Desktop.Controls;

/// <summary>
/// Återanvändbart kort som visar ett enskilt klipp i biblioteket.
/// Klickbart — exponerar Click-event som triggers när användaren klickar någonstans på kortet.
/// </summary>
public sealed partial class ClipCard : UserControl
{
    /// <summary>Triggas när användaren klickar på kortet.</summary>
    public event EventHandler<ClipCardClickEventArgs>? Clicked;

    /// <summary>
    /// Filsökväg som identifierar vilket klipp kortet representerar.
    /// Sätts via x:Bind från ClipViewModel.
    /// </summary>
    public string ClipFilePath { get; set; } = string.Empty;

    public ClipCard()
    {
        InitializeComponent();
    }

    public string ClipTitle
    {
        get => TitleText.Text;
        set => TitleText.Text = value;
    }

    public string ClipMeta
    {
        get => MetaText.Text;
        set => MetaText.Text = value;
    }

    public string Duration
    {
        get => DurationText.Text;
        set => DurationText.Text = value;
    }

    public bool IsNew
    {
        get => NewBadge.Visibility == Visibility.Visible;
        set => NewBadge.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
    }
    private string _thumbnailPath = string.Empty;

    /// <summary>
    /// Sökväg till thumbnail-bild. Laddas via stream (undviker packaged-app
    /// sandbox-problem med fil-URI:er).
    /// </summary>
    public string ThumbnailPath
    {
        get => _thumbnailPath;
        set
        {
            _thumbnailPath = value;
            _ = LoadThumbnailAsync(value);
        }
    }

    private async System.Threading.Tasks.Task LoadThumbnailAsync(string path)
    {
        if (string.IsNullOrEmpty(path) || !System.IO.File.Exists(path))
            return;

        try
        {
            var bitmap = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage();
            using var stream = System.IO.File.OpenRead(path);
            await bitmap.SetSourceAsync(stream.AsRandomAccessStream());

            ThumbnailImage.Source = bitmap;
            ThumbnailImage.Visibility = Visibility.Visible;
            PlaceholderIcon.Visibility = Visibility.Collapsed;
        }
        catch
        {
            // Behåll placeholder-ikonen om bilden inte kan laddas
        }
    }
    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        Clicked?.Invoke(this, new ClipCardClickEventArgs(ClipFilePath));
    }
}

/// <summary>
/// Event args för ClipCard.Clicked.
/// </summary>
public sealed class ClipCardClickEventArgs : EventArgs
{
    public string FilePath { get; }

    public ClipCardClickEventArgs(string filePath)
    {
        FilePath = filePath;
    }
}
