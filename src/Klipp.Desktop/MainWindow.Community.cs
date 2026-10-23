using System;
using System.Threading.Tasks;
using Klipp.Desktop.Controls;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Klipp.Desktop;

public sealed partial class MainWindow
{
    private CommunityView? _communityView;
    private IDisposable? _communityUpdateWork;

    private CommunityView CommunityView
    {
        get
        {
            if (_communityView is not null) return _communityView;
            var view = new CommunityView();
            view.LibraryRequested += (_, _) => ShowLibrary();
            view.UploadRequested += async (_, _) => await BeginCommunityUploadAsync();
            view.AccountChanged += (_, _) => CommunityAccountLabel.Text = view.AccountLabel;
            view.BusyChanged += (_, _) =>
            {
                if (view.IsBusy)
                    _communityUpdateWork ??= BeginUpdateBlockingOperation();
                else
                {
                    _communityUpdateWork?.Dispose();
                    _communityUpdateWork = null;
                }
            };
            _communityView = view;
            MediaHost.Children.Add(view);
            return view;
        }
    }

    private void OnLibraryNavigationClicked(object sender, RoutedEventArgs e) => ShowLibrary();
    private async void OnMediaNavigationClicked(object sender, RoutedEventArgs e) => await ShowMediaAsync();
    private async void OnCommunityAccountClicked(object sender, RoutedEventArgs e) => await ShowMediaAsync(showProfile: true);

    private async Task ShowMediaAsync(bool showProfile = false)
    {
        if (_updateRestarting || _captureWindowClosing || _trayExitPending) return;
        ClipCard.StopActivePreview();
        HideSideMenu();
        SetPlayerFullscreen(false);
        StopPlayerIfCreated();
        StopEditorIfCreated();
        TopBar.Visibility = Visibility.Visible;
        SetViewVisibility(LibraryView, false);
        SetViewVisibility(PlayerHost, false);
        SetViewVisibility(EditorHost, false);
        SetViewVisibility(MediaHost, true);
        UpdateWorkspaceNavigation(media: true);
        RefreshUpdateUi();
        await CommunityView.ActivateAsync(showProfile);
    }

    private void LeaveMedia()
    {
        _communityView?.Deactivate();
        SetViewVisibility(MediaHost, false);
    }

    private void UpdateWorkspaceNavigation(bool media, bool visible = true)
    {
        WorkspaceNavigation.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        var selected = (Brush)Application.Current.Resources["KlippSurfaceElevatedBrush"];
        var border = (Brush)Application.Current.Resources["KlippBorderBrush"];
        var transparent = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        LibraryNavigationButton.Background = media ? transparent : selected;
        LibraryNavigationButton.BorderBrush = media ? transparent : border;
        MediaNavigationButton.Background = media ? selected : transparent;
        MediaNavigationButton.BorderBrush = media ? border : transparent;
    }

    private void DisposeCommunity()
    {
        _communityPreparation?.Cancel();
        _communityView?.Dispose();
        _communityUpdateWork?.Dispose();
        _communityUpdateWork = null;
    }
}
