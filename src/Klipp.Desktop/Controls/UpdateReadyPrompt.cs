using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.UI.ViewManagement;

namespace Klipp.Desktop.Controls;

/// <summary>An in-app update card. Its owner decides when showing it is safe.</summary>
public sealed class UpdateReadyPrompt : UserControl
{
    private readonly TextBlock _detail;
    private readonly TextBlock _error;
    private readonly TextBlock[] _highlights;
    private readonly Grid[] _highlightRows;
    private readonly TextBlock _noHighlights;
    private readonly Border _surface;
    private readonly Storyboard _entrance;
    private string? _version;

    public event EventHandler? RestartRequested;
    public event EventHandler? LaterRequested;
    public event EventHandler? ReleaseNotesRequested;

    public UpdateReadyPrompt()
    {
        Visibility = Visibility.Collapsed;
        IsTabStop = false;
        MaxWidth = 360;
        var layout = (Border)XamlReader.Load("""
            <Border xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                    Background="{ThemeResource KlippSurfaceElevatedBrush}"
                    BorderBrush="{ThemeResource KlippBorderBrush}" BorderThickness="1"
                    CornerRadius="6" Padding="16">
                <Grid>
                    <Grid.RowDefinitions>
                        <RowDefinition Height="Auto" />
                        <RowDefinition Height="*" />
                        <RowDefinition Height="Auto" />
                        <RowDefinition Height="Auto" />
                    </Grid.RowDefinitions>
                    <Grid Grid.Row="0" ColumnSpacing="12" Margin="0,0,0,12">
                        <Grid.ColumnDefinitions>
                            <ColumnDefinition Width="24" />
                            <ColumnDefinition Width="*" />
                        </Grid.ColumnDefinitions>
                        <ContentControl x:Name="BrandHost" IsTabStop="False" Width="24" Height="24"
                                        HorizontalContentAlignment="Stretch" VerticalContentAlignment="Stretch" />
                        <TextBlock Grid.Column="1" Text="Update ready" FontSize="14" FontWeight="SemiBold"
                                   VerticalAlignment="Center" TextWrapping="Wrap"
                                   Foreground="{ThemeResource KlippTextPrimaryBrush}" />
                    </Grid>
                    <ScrollViewer x:Name="NotesViewport" Grid.Row="1" VerticalScrollBarVisibility="Auto"
                                  HorizontalScrollBarVisibility="Disabled" ZoomMode="Disabled"
                                  HorizontalContentAlignment="Stretch" IsTabStop="False" Margin="0,0,0,12">
                        <StackPanel Spacing="12">
                            <StackPanel Spacing="4">
                                <TextBlock x:Name="Detail" FontSize="13" TextWrapping="Wrap"
                                           Foreground="{ThemeResource KlippTextPrimaryBrush}" />
                                <TextBlock Text="Keep working. You can install it later in Settings → Updates."
                                           FontSize="12" TextWrapping="Wrap"
                                           Foreground="{ThemeResource KlippTextSecondaryBrush}" />
                            </StackPanel>
                            <StackPanel Spacing="6">
                                <TextBlock Text="What's new" FontSize="12" FontWeight="SemiBold"
                                           Foreground="{ThemeResource KlippTextPrimaryBrush}" />
                                <Grid x:Name="HighlightRow0" ColumnSpacing="8" Visibility="Collapsed">
                                    <Grid.ColumnDefinitions><ColumnDefinition Width="Auto" /><ColumnDefinition Width="*" /></Grid.ColumnDefinitions>
                                    <TextBlock Text="•" FontSize="12" Foreground="{ThemeResource KlippTextSecondaryBrush}" />
                                    <TextBlock x:Name="Highlight0" Grid.Column="1" FontSize="12" TextWrapping="Wrap"
                                               Foreground="{ThemeResource KlippTextSecondaryBrush}" />
                                </Grid>
                                <Grid x:Name="HighlightRow1" ColumnSpacing="8" Visibility="Collapsed">
                                    <Grid.ColumnDefinitions><ColumnDefinition Width="Auto" /><ColumnDefinition Width="*" /></Grid.ColumnDefinitions>
                                    <TextBlock Text="•" FontSize="12" Foreground="{ThemeResource KlippTextSecondaryBrush}" />
                                    <TextBlock x:Name="Highlight1" Grid.Column="1" FontSize="12" TextWrapping="Wrap"
                                               Foreground="{ThemeResource KlippTextSecondaryBrush}" />
                                </Grid>
                                <Grid x:Name="HighlightRow2" ColumnSpacing="8" Visibility="Collapsed">
                                    <Grid.ColumnDefinitions><ColumnDefinition Width="Auto" /><ColumnDefinition Width="*" /></Grid.ColumnDefinitions>
                                    <TextBlock Text="•" FontSize="12" Foreground="{ThemeResource KlippTextSecondaryBrush}" />
                                    <TextBlock x:Name="Highlight2" Grid.Column="1" FontSize="12" TextWrapping="Wrap"
                                               Foreground="{ThemeResource KlippTextSecondaryBrush}" />
                                </Grid>
                                <TextBlock x:Name="NoHighlights" Text="See the full release notes for details."
                                           FontSize="12" TextWrapping="Wrap"
                                           Foreground="{ThemeResource KlippTextSecondaryBrush}" />
                            </StackPanel>
                        </StackPanel>
                    </ScrollViewer>
                    <TextBlock x:Name="Error" Grid.Row="2" FontSize="12" TextWrapping="Wrap" Visibility="Collapsed" Margin="0,0,0,12"
                               MaxLines="4" TextTrimming="WordEllipsis" Foreground="{ThemeResource KlippWarningBrush}" />
                    <StackPanel Grid.Row="3" Spacing="8">
                        <HyperlinkButton x:Name="ReleaseNotesButton" Content="Full release notes" Padding="0"
                                         MinHeight="24" HorizontalAlignment="Left" FontSize="12" />
                        <Grid ColumnSpacing="8">
                            <Grid.ColumnDefinitions>
                                <ColumnDefinition Width="Auto" />
                                <ColumnDefinition Width="*" />
                            </Grid.ColumnDefinitions>
                            <Button x:Name="LaterButton" Content="Later" MinWidth="0" MinHeight="32" Height="Auto"
                                    Style="{StaticResource KlippToolbarButton}" />
                            <Button x:Name="RestartButton" Grid.Column="1" MinWidth="0" MinHeight="32" Height="Auto"
                                    HorizontalAlignment="Stretch" Style="{StaticResource KlippPrimaryButton}">
                                <TextBlock Text="Restart to update" TextWrapping="Wrap" TextAlignment="Center"
                                           Foreground="{Binding Foreground, ElementName=RestartButton}" />
                            </Button>
                        </Grid>
                    </StackPanel>
                </Grid>
            </Border>
            """);
        _surface = layout;
        _detail = (TextBlock)layout.FindName("Detail");
        _error = (TextBlock)layout.FindName("Error");
        _highlights = new TextBlock[3];
        _highlightRows = new Grid[3];
        for (var index = 0; index < _highlights.Length; index++)
        {
            _highlights[index] = (TextBlock)layout.FindName($"Highlight{index}");
            _highlightRows[index] = (Grid)layout.FindName($"HighlightRow{index}");
        }
        _noHighlights = (TextBlock)layout.FindName("NoHighlights");
        ((ContentControl)layout.FindName("BrandHost")).Content = new KlippBrandMark { Width = 24, Height = 24 };
        var later = (Button)layout.FindName("LaterButton");
        var restart = (Button)layout.FindName("RestartButton");
        var releaseNotes = (HyperlinkButton)layout.FindName("ReleaseNotesButton");
        later.Click += (_, _) => LaterRequested?.Invoke(this, EventArgs.Empty);
        restart.Click += (_, _) => RestartRequested?.Invoke(this, EventArgs.Empty);
        releaseNotes.Click += (_, _) => ReleaseNotesRequested?.Invoke(this, EventArgs.Empty);
        AutomationProperties.SetName(this, "Klipp update ready");
        AutomationProperties.SetName(later, "Later");
        AutomationProperties.SetName(restart, "Restart Klipp to install the update");
        AutomationProperties.SetName(releaseNotes, "Full release notes for this update");
        AutomationProperties.SetLiveSetting(_detail, AutomationLiveSetting.Polite);
        AutomationProperties.SetLiveSetting(_error, AutomationLiveSetting.Polite);
        Content = layout;

        var fade = new DoubleAnimation
        {
            From = 0, To = 1, Duration = new Duration(TimeSpan.FromMilliseconds(150)),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        Storyboard.SetTarget(fade, _surface);
        Storyboard.SetTargetProperty(fade, "Opacity");
        _entrance = new Storyboard();
        _entrance.Children.Add(fade);
        Unloaded += (_, _) => _entrance.Stop();
    }

    /// <summary>Reveal without activating the window or moving keyboard focus.</summary>
    public void Show(string version, string? errorMessage = null, IReadOnlyList<string>? highlights = null)
    {
        if (string.IsNullOrWhiteSpace(version))
        {
            Hide();
            return;
        }

        version = version.Trim();
        var wasVisible = Visibility == Visibility.Visible;
        var error = string.IsNullOrWhiteSpace(errorMessage) ? string.Empty : errorMessage.Trim();
        if (_error.Text != error)
        {
            _error.Text = error;
            ToolTipService.SetToolTip(_error, error.Length == 0 ? null : error);
        }
        _error.Visibility = error.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        UpdateHighlights(highlights);
        if (_version == version && wasVisible) return;
        _version = version;
        _detail.Text = $"Klipp {version} is ready to install.";
        Visibility = Visibility.Visible;
        if (wasVisible) return;

        _entrance.Stop();
        _surface.Opacity = 1;
        // No delayed hide: recording, playback or a dialog may begin while
        // the card is fading in, and must remove it immediately.
        try
        {
            if (new UISettings().AnimationsEnabled) _entrance.Begin();
        }
        catch (COMException) { }
    }

    private void UpdateHighlights(IReadOnlyList<string>? highlights)
    {
        var visibleCount = 0;
        for (var index = 0; index < _highlights.Length; index++)
        {
            var text = highlights is not null && index < highlights.Count ? highlights[index]?.Trim() ?? string.Empty : string.Empty;
            // The owner supplies bounded summaries; retain a small defensive
            // limit because this card must remain usable in narrow windows.
            text = text.Replace('\r', ' ').Replace('\n', ' ');
            if (text.Length > 110)
            {
                var length = char.IsHighSurrogate(text[108]) ? 108 : 109;
                text = text[..length].TrimEnd() + "…";
            }
            if (_highlights[index].Text != text) _highlights[index].Text = text;
            _highlightRows[index].Visibility = text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
            if (text.Length != 0) visibleCount++;
        }
        _noHighlights.Visibility = visibleCount == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    public void Hide()
    {
        Visibility = Visibility.Collapsed;
        _entrance.Stop();
        _surface.Opacity = 1;
    }
}
