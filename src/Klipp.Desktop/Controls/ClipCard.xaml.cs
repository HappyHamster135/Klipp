using Microsoft.UI.Xaml.Controls;

namespace Klipp.Desktop.Controls;

/// <summary>
/// Återanvändbart kort som visar ett enskilt klipp i biblioteket.
/// </summary>
public sealed partial class ClipCard : UserControl
{
    public ClipCard()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Sätter klippets titel (t.ex. "Triple kill - Haven").
    /// </summary>
    public string ClipTitle
    {
        get => TitleText.Text;
        set => TitleText.Text = value;
    }

    /// <summary>
    /// Sätter metadata-raden (t.ex. "Valorant • 5 min sedan").
    /// </summary>
    public string ClipMeta
    {
        get => MetaText.Text;
        set => MetaText.Text = value;
    }

    /// <summary>
    /// Sätter längden som visas i nedre högra hörnet (t.ex. "0:30").
    /// </summary>
    public string Duration
    {
        get => DurationText.Text;
        set => DurationText.Text = value;
    }

    /// <summary>
    /// Visar eller döljer "NY"-badge i övre vänstra hörnet.
    /// </summary>
    public bool IsNew
    {
        get => NewBadge.Visibility == Microsoft.UI.Xaml.Visibility.Visible;
        set => NewBadge.Visibility = value
            ? Microsoft.UI.Xaml.Visibility.Visible
            : Microsoft.UI.Xaml.Visibility.Collapsed;
    }
}
