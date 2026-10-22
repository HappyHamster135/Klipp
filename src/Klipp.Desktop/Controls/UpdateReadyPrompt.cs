using System;
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
    private readonly Border _surface;
    private readonly Storyboard _entrance;
    private string? _version;

    public event EventHandler? RestartRequested;
    public event EventHandler? LaterRequested;

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
                <StackPanel Spacing="12">
                    <Grid ColumnSpacing="12">
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
                    <StackPanel Spacing="4">
                        <TextBlock x:Name="Detail" FontSize="13" TextWrapping="Wrap"
                                   Foreground="{ThemeResource KlippTextPrimaryBrush}" />
                        <TextBlock Text="Keep working. You can install it later in Settings → Updates."
                                   FontSize="12" TextWrapping="Wrap"
                                   Foreground="{ThemeResource KlippTextSecondaryBrush}" />
                    </StackPanel>
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
            </Border>
            """);
        _surface = layout;
        _detail = (TextBlock)layout.FindName("Detail");
        ((ContentControl)layout.FindName("BrandHost")).Content = new KlippBrandMark { Width = 24, Height = 24 };
        var later = (Button)layout.FindName("LaterButton");
        var restart = (Button)layout.FindName("RestartButton");
        later.Click += (_, _) => LaterRequested?.Invoke(this, EventArgs.Empty);
        restart.Click += (_, _) => RestartRequested?.Invoke(this, EventArgs.Empty);
        AutomationProperties.SetName(this, "Klipp update ready");
        AutomationProperties.SetName(later, "Later");
        AutomationProperties.SetName(restart, "Restart Klipp to install the update");
        AutomationProperties.SetLiveSetting(_detail, AutomationLiveSetting.Polite);
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
    public void Show(string version)
    {
        if (string.IsNullOrWhiteSpace(version))
        {
            Hide();
            return;
        }

        version = version.Trim();
        var wasVisible = Visibility == Visibility.Visible;
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

    public void Hide()
    {
        Visibility = Visibility.Collapsed;
        _entrance.Stop();
        _surface.Opacity = 1;
    }
}
