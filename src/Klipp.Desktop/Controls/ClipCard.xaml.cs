using System;
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
