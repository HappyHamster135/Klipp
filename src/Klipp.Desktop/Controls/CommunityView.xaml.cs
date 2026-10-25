using System;
using System.Globalization;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Klipp.Core.Community;
using Klipp.Desktop.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace Klipp.Desktop.Controls;

/// <summary>The optional online workspace. Nothing here is initialized by capture or editing.</summary>
public sealed partial class CommunityView : UserControl, IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly CommunityAccountService _accounts;
    private CommunityFeedView? _feedView;
    private readonly DispatcherTimer _resendTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private CancellationTokenSource? _request;
    private TaskCompletionSource? _idle;
    private int _activationVersion;
    private bool _active;
    private bool _busy;
    private bool _disposed;
    private bool _loadedAccount;
    private bool _creatingAccount;
    private bool _enteringCode;
    private bool _editingProfile;
    private (string Username, string DisplayName, string Bio) _profileBaseline = (string.Empty, string.Empty, string.Empty);
    private bool _openProfileWhenReady;
    private string _pendingEmail = string.Empty;
    private Guid? _renderedUserId;
    private DateTimeOffset _lastCodeSent;

    public event EventHandler? LibraryRequested;
    public event EventHandler? AccountChanged;
    public event EventHandler? BusyChanged;
    public event EventHandler? UploadRequested;
    public event EventHandler<PlayerFullscreenRequestEventArgs>? FullscreenRequested;
    public event EventHandler<CommunityPlaybackHostEventArgs>? PlaybackHostChanged;
    public event EventHandler? PlaybackLayoutChanged;
    public bool IsBusy => _busy || _operationsDialog is not null || (_feedView?.IsBusy ?? false);
    public bool IsSignedIn => _accounts.IsSignedIn;
    public bool HasCompleteProfile => _accounts.IsSignedIn && _accounts.Profile?.IsComplete == true;
    public string AccountLabel => _accounts.HasPendingSignOut ? "Finish signing out" : _accounts.IsSignedIn
        ? _accounts.Profile?.DisplayName ?? "My account"
        : "Sign in";

    public bool HandlePlaybackKey(VirtualKey key, bool repeated = false) => _active && !_disposed && _feedView?.HandlePlaybackKey(key, repeated) == true;
    public void ExitFullscreen() => _feedView?.ExitFullscreen();
    public bool ScrollMediaWheel(int delta, uint lines)
    {
        if (!_active || _disposed) return false;
        var distance = lines == uint.MaxValue ? Math.Max(16, CommunityScroller.ViewportHeight) : lines * 16d;
        var offset = Math.Clamp(CommunityScroller.VerticalOffset - delta / 120d * distance, 0, CommunityScroller.ScrollableHeight);
        return CommunityScroller.ChangeView(null, offset, null, disableAnimation: true);
    }

    public CommunityView()
    {
        var configuration = CommunityConfigurationService.Load();
        _httpClient = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = Timeout.InfiniteTimeSpan };
        _accounts = new CommunityAccountService(configuration, _httpClient, new CommunitySessionStore(configuration));
        InitializeComponent();
        CommunityScroller.ViewChanged += (_, _) => PlaybackLayoutChanged?.Invoke(this, EventArgs.Empty);
        _resendTimer.Tick += (_, _) => UpdateResendButton();
        Render();
    }

    public async Task ActivateAsync(bool showProfile = false)
    {
        if (_disposed) return;
        var activation = ++_activationVersion;
        _active = true;
        _openProfileWhenReady |= showProfile;
        Render();
        if (_busy && _idle is { } pending) await pending.Task;
        if (!_active || _disposed || activation != _activationVersion) return;
        if (_accounts.IsConfigured && !_loadedAccount && !_busy)
            await RunAsync(async token =>
            {
                await _accounts.RestoreSessionAsync(token);
                if (_accounts.IsSignedIn) await _accounts.GetProfileAsync(token);
                token.ThrowIfCancellationRequested();
                _loadedAccount = true;
            });
        if (!_active || _disposed || activation != _activationVersion) return;
        ShowRequestedProfile();
        await CheckOperationsCapabilitiesAsync();
        if (_enteringCode) _resendTimer.Start();
        await ActivateFeedAsync();
    }

    public void Deactivate()
    {
        _active = false;
        _activationVersion++;
        _request?.Cancel();
        _feedView?.Deactivate();
        _operationsDialog?.Hide();
        AccountMenuButton.Flyout?.Hide();
        _resendTimer.Stop();
        // Verification codes are short-lived credentials and never leave this view.
        CodeInput.Text = string.Empty;
    }

    private async Task RunAsync(Func<CancellationToken, Task> action, string? successMessage = null)
    {
        if (_busy || _disposed || !_active) return;
        using var cancellation = new CancellationTokenSource();
        _idle = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _request = cancellation;
        SetBusy(true);
        HideNotice();
        try
        {
            await action(cancellation.Token);
            if (!_active || cancellation.IsCancellationRequested || _disposed) return;
            Render();
            ShowRequestedProfile();
            if (successMessage is not null) ShowNotice(successMessage, InfoBarSeverity.Success);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (CommunityAccountException exception)
        {
            if (_active && !_disposed)
            {
                Render();
                ShowNotice(exception.Message, InfoBarSeverity.Error);
            }
        }
        catch (Exception)
        {
            if (_active && !_disposed)
                ShowNotice("Could not connect to online media. Check your connection and try again.", InfoBarSeverity.Error);
        }
        finally
        {
            if (ReferenceEquals(_request, cancellation)) _request = null;
            SetBusy(false);
            _idle?.TrySetResult();
            if (_active && !_disposed) ShowRequestedProfile();
        }
    }

    private void SetBusy(bool value)
    {
        _busy = value;
        if (!_disposed)
        {
            AccountProgress.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
            AccountProgress.IsActive = value;
            foreach (var control in new Control[]
                     { EmailInput, CodeInput, SendCodeButton, VerifyCodeButton, SwitchAuthButton,
                         ChangeEmailButton, DisplayNameInput, UsernameInput, BioInput,
                         SaveProfileButton, CancelProfileButton, EditProfileButton,
                         SignOutButton, RefreshAccountButton })
                control.IsEnabled = !value;
            UpdateResendButton();
            SyncAccountMediaControls();
            SetOperationsBusy(value);
        }
        BusyChanged?.Invoke(this, EventArgs.Empty);
    }

    private void Render()
    {
        var configured = _accounts.IsConfigured;
        var signedIn = configured && _accounts.IsSignedIn;
        var currentUserId = _accounts.User?.Id;
        if (currentUserId != _renderedUserId)
        {
            ResetProfileDraft();
            _feedView?.Deactivate(clear: true);
            ResetOperationsIdentity();
            _renderedUserId = currentUserId;
        }
        var pendingSignOut = _accounts.HasPendingSignOut;
        AccountMenuButton.Visibility = signedIn || pendingSignOut ? Visibility.Visible : Visibility.Collapsed;
        EditProfileButton.Visibility = signedIn ? Visibility.Visible : Visibility.Collapsed;
        SetupPanel.Visibility = configured ? Visibility.Collapsed : Visibility.Visible;
        SignInPanel.Visibility = configured && !signedIn && !pendingSignOut ? Visibility.Visible : Visibility.Collapsed;
        ConnectedPanel.Visibility = signedIn ? Visibility.Visible : Visibility.Collapsed;
        ProfileEditorPanel.Visibility = signedIn && _editingProfile ? Visibility.Visible : Visibility.Collapsed;
        FeedHost.Visibility = _editingProfile || _operationsPage != AccountToolsPage.None ? Visibility.Collapsed : Visibility.Visible;
        OperationsHost.Visibility = signedIn && _operationsPage != AccountToolsPage.None ? Visibility.Visible : Visibility.Collapsed;
        UsageMenuButton.Visibility = signedIn && _operationsCapabilities.Enabled ? Visibility.Visible : Visibility.Collapsed;
        HelpMenuButton.Visibility = UsageMenuButton.Visibility;
        RefreshAccountButton.Visibility = configured ? Visibility.Visible : Visibility.Collapsed;
        SignOutButton.Visibility = signedIn || pendingSignOut ? Visibility.Visible : Visibility.Collapsed;
        SignOutButton.Content = pendingSignOut ? "Finish signing out" : "Sign out";
        SignOutRecoveryText.Visibility = pendingSignOut ? Visibility.Visible : Visibility.Collapsed;
        EmailStep.Visibility = _enteringCode ? Visibility.Collapsed : Visibility.Visible;
        CodeStep.Visibility = _enteringCode ? Visibility.Visible : Visibility.Collapsed;
        AuthHeading.Text = _creatingAccount ? "Create your Klipp account" : "Sign in to Klipp";
        AuthDescription.Text = _creatingAccount
            ? "Start with your email. We’ll send a code to confirm it belongs to you."
            : "Use an email code to access your profile and online media.";
        SendCodeButton.Content = _creatingAccount ? "Send verification code" : "Send sign-in code";
        SwitchAuthButton.Content = _creatingAccount ? "Already have an account? Sign in" : "New to Klipp? Create an account";
        if (signedIn)
        {
            var profile = _accounts.Profile;
            ProfileName.Text = profile?.DisplayName ?? "Your profile";
            ProfileInitials.Text = string.IsNullOrWhiteSpace(profile?.DisplayName) ? "K" : StringInfo.GetNextTextElement(profile.DisplayName).ToUpperInvariant();
            ProfileUsername.Text = string.IsNullOrWhiteSpace(profile?.Username) ? "Choose your username" : "@" + profile.Username;
            ProfileBio.Text = profile?.Bio ?? string.Empty;
            ProfileBio.Visibility = string.IsNullOrWhiteSpace(ProfileBio.Text) ? Visibility.Collapsed : Visibility.Visible;
        }
        else if (pendingSignOut)
        {
            ProfileName.Text = "Account";
            ProfileUsername.Text = "Finish signing out";
            ProfileInitials.Text = "K";
            ProfileBio.Text = string.Empty;
            ProfileBio.Visibility = Visibility.Collapsed;
        }
        UpdateResendButton();
        RenderOperations();
        AccountChanged?.Invoke(this, EventArgs.Empty);
    }

    private void ShowRequestedProfile()
    {
        if (!_active || _busy || !_openProfileWhenReady || !_accounts.IsSignedIn) return;
        _openProfileWhenReady = false;
        OpenProfileEditor();
    }

    private void OpenProfileEditor()
    {
        if (_busy || !_accounts.IsSignedIn) return;
        AccountMenuButton.Flyout?.Hide();
        if (_editingProfile)
        {
            DisplayNameInput.Focus(FocusState.Programmatic);
            return;
        }
        _editingProfile = true;
        _operationsPage = AccountToolsPage.None;
        _feedView?.Deactivate();
        DisplayNameInput.Text = _accounts.Profile?.DisplayName ?? string.Empty;
        UsernameInput.Text = _accounts.Profile?.Username ?? string.Empty;
        BioInput.Text = _accounts.Profile?.Bio ?? string.Empty;
        _profileBaseline = CurrentProfileDraft();
        HideNotice();
        Render();
        DisplayNameInput.Focus(FocusState.Programmatic);
    }

    private async Task SendCodeAsync()
    {
        var email = _enteringCode ? _pendingEmail : EmailInput.Text.Trim();
        await RunAsync(async token =>
        {
            await _accounts.SendEmailCodeAsync(email, _creatingAccount, token);
            token.ThrowIfCancellationRequested();
            _pendingEmail = email;
            _lastCodeSent = DateTimeOffset.UtcNow;
            _enteringCode = true;
            CodeInput.Text = string.Empty;
            CodeDestination.Text = "If this email can be used, a code will arrive at " + email + ". Check your spam folder too.";
            _resendTimer.Start();
        });
        if (_active && _enteringCode && !_busy) CodeInput.Focus(FocusState.Programmatic);
    }

    private async Task VerifyCodeAsync()
    {
        var code = CodeInput.Text.Trim();
        await RunAsync(async token =>
        {
            await _accounts.VerifyEmailCodeAsync(_pendingEmail, code, token);
            token.ThrowIfCancellationRequested();
            CodeInput.Text = string.Empty;
            _enteringCode = false;
            _resendTimer.Stop();
            _loadedAccount = false;
            ResetProfileDraft();
            // The account remains signed in if profile loading needs a retry.
            _openProfileWhenReady |= _creatingAccount;
            _creatingAccount = false;
            await _accounts.GetProfileAsync(token);
            token.ThrowIfCancellationRequested();
            _loadedAccount = true;
            _openProfileWhenReady |= string.IsNullOrWhiteSpace(_accounts.Profile?.Username);
        });
        await ActivateFeedAsync();
        await CheckOperationsCapabilitiesAsync();
    }

    private void UpdateResendButton()
    {
        var remaining = _lastCodeSent.AddSeconds(60) - DateTimeOffset.UtcNow;
        var seconds = remaining <= TimeSpan.Zero ? 0 : (int)Math.Ceiling(remaining.TotalSeconds);
        ResendCodeButton.IsEnabled = !_busy && seconds == 0;
        ResendCodeButton.Content = seconds == 0 ? "Resend code" : $"Resend in {seconds}s";
    }

    private async void OnSendCodeClicked(object sender, RoutedEventArgs e) => await SendCodeAsync();
    private async void OnVerifyCodeClicked(object sender, RoutedEventArgs e) => await VerifyCodeAsync();
    private async void OnResendCodeClicked(object sender, RoutedEventArgs e)
    {
        if (DateTimeOffset.UtcNow - _lastCodeSent >= TimeSpan.FromSeconds(60)) await SendCodeAsync();
    }

    private void OnSwitchAuthClicked(object sender, RoutedEventArgs e)
    {
        _creatingAccount = !_creatingAccount;
        HideNotice();
        Render();
        EmailInput.Focus(FocusState.Programmatic);
    }

    private void OnChangeEmailClicked(object sender, RoutedEventArgs e)
    {
        _enteringCode = false;
        CodeInput.Text = string.Empty;
        _resendTimer.Stop();
        HideNotice();
        Render();
        EmailInput.Focus(FocusState.Programmatic);
    }

    private async void OnRefreshClicked(object sender, RoutedEventArgs e)
    {
        AccountMenuButton.Flyout?.Hide();
        await RunAsync(async token =>
        {
            await _accounts.RestoreSessionAsync(token);
            if (_accounts.IsSignedIn) await _accounts.GetProfileAsync(token);
            token.ThrowIfCancellationRequested();
            _loadedAccount = true;
        });
        if (_active && !_editingProfile && _accounts.IsSignedIn && _feedView?.IsActive == true) await _feedView.RefreshAsync();
        else await ActivateFeedAsync();
        _operationsCheckedUser = null;
        await CheckOperationsCapabilitiesAsync();
    }

    private async void OnSignOutClicked(object sender, RoutedEventArgs e)
    {
        if (!await ConfirmDiscardProfileAsync()) return;
        await RunAsync(async token =>
        {
            AccountMenuButton.Flyout?.Hide();
            _feedView?.Deactivate(clear: true);
            ResetProfileDraft();
            _openProfileWhenReady = false;
            _enteringCode = false;
            _creatingAccount = false;
            _pendingEmail = string.Empty;
            EmailInput.Text = string.Empty;
            CodeInput.Text = string.Empty;
            _resendTimer.Stop();
            await _accounts.SignOutAsync(token);
        }, "Signed out. Your local clips are still here.");
    }

    private void ResetProfileDraft()
    {
        _editingProfile = false;
        DisplayNameInput.Text = string.Empty;
        UsernameInput.Text = string.Empty;
        BioInput.Text = string.Empty;
        _profileBaseline = (string.Empty, string.Empty, string.Empty);
    }

    private void OnEditProfileClicked(object sender, RoutedEventArgs e) => OpenProfileEditor();
    private async void OnCancelProfileClicked(object sender, RoutedEventArgs e)
    {
        if (!await ConfirmDiscardProfileAsync()) return;
        _editingProfile = false;
        HideNotice();
        Render();
        await ActivateFeedAsync();
    }

    private async void OnSaveProfileClicked(object sender, RoutedEventArgs e)
    {
        var username = UsernameInput.Text;
        var displayName = DisplayNameInput.Text;
        var bio = BioInput.Text;
        await RunAsync(async token =>
        {
            await _accounts.SaveProfileAsync(username, displayName, bio, token);
            token.ThrowIfCancellationRequested();
            _editingProfile = false;
        }, "Profile saved.");
        await ActivateFeedAsync();
    }

    private async void OnEmailKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter || _busy) return;
        e.Handled = true;
        await SendCodeAsync();
    }

    private async void OnCodeKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter || _busy) return;
        e.Handled = true;
        await VerifyCodeAsync();
    }

    private void OnLibraryClicked(object sender, RoutedEventArgs e) => LibraryRequested?.Invoke(this, EventArgs.Empty);
    private void OnNoticeClosed(InfoBar sender, InfoBarClosedEventArgs args) => AccountNotice.Visibility = Visibility.Collapsed;
    private void ShowNotice(string message, InfoBarSeverity severity)
    {
        AccountNotice.Title = string.Empty;
        AccountNotice.Message = message;
        AccountNotice.Severity = severity;
        AccountNotice.Visibility = Visibility.Visible;
        AccountNotice.IsOpen = true;
    }
    private void HideNotice()
    {
        AccountNotice.IsOpen = false;
        AccountNotice.Visibility = Visibility.Collapsed;
    }

    private async Task ActivateFeedAsync()
    {
        if (_disposed || !_active || _busy || _editingProfile || _operationsPage != AccountToolsPage.None || !_accounts.IsSignedIn) return;
        if (_feedView is null)
        {
            _feedView = new CommunityFeedView();
            _feedView.Configure(_accounts);
            _feedView.UploadRequested += (_, _) => UploadRequested?.Invoke(this, EventArgs.Empty);
            _feedView.ProfileRequired += (_, _) =>
            {
                OpenProfileEditor();
                ShowNotice("Choose a username and save your profile before sharing a clip.", InfoBarSeverity.Informational);
            };
            _feedView.IdentityChanged += (_, _) => Render();
            _feedView.BusyChanged += (_, _) =>
            {
                SyncAccountMediaControls();
                BusyChanged?.Invoke(this, EventArgs.Empty);
            };
            _feedView.FullscreenRequested += (_, args) => FullscreenRequested?.Invoke(this, args);
            _feedView.PlaybackHostChanged += (_, args) => PlaybackHostChanged?.Invoke(this, args);
            FeedHost.Children.Add(_feedView);
        }
        await _feedView.ActivateAsync();
    }

    private void SyncAccountMediaControls()
    {
        if (_disposed) return;
        RefreshAccountButton.IsEnabled = !_busy && _feedView?.IsUploading != true;
        EditProfileButton.IsEnabled = !_busy && _feedView?.IsUploading != true;
        UsageMenuButton.IsEnabled = EditProfileButton.IsEnabled;
        HelpMenuButton.IsEnabled = EditProfileButton.IsEnabled;
    }

    public async Task<bool> UploadPreparedClipAsync(string videoPath, string thumbnailPath, string title,
        string description, double durationSeconds, CancellationToken cancellationToken = default)
    {
        if (!_active || !_accounts.IsSignedIn)
        {
            ShowNotice("Sign in before uploading a clip. Your local recording stays in Library.", InfoBarSeverity.Informational);
            return false;
        }
        if (!HasCompleteProfile)
        {
            OpenProfileEditor();
            ShowNotice("Choose a username and save your profile before sharing a clip.", InfoBarSeverity.Informational);
            return false;
        }
        _operationsPage = AccountToolsPage.None;
        Render();
        await ActivateFeedAsync();
        return _feedView is not null && await _feedView.UploadPreparedClipAsync(videoPath, thumbnailPath,
            title, description, durationSeconds, cancellationToken);
    }

    public void Dispose()
    {
        if (_disposed) return;
        Deactivate();
        _disposed = true;
        _resendTimer.Stop();
        _feedView?.Dispose();
        _accounts.Dispose();
        _httpClient.Dispose();
    }
}
