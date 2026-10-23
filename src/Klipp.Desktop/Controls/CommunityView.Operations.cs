using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Klipp.Core.Community;
using Klipp.Desktop.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Media.Core;
using Windows.Media.Playback;

namespace Klipp.Desktop.Controls;

public sealed partial class CommunityView
{
    private enum AccountToolsPage { None, Usage, Help, Reports, Requests }
    private AccountToolsPage _operationsPage;
    private CommunityOperationsCapabilities _operationsCapabilities = CommunityOperationsCapabilities.Unavailable;
    private Guid? _operationsCheckedUser;
    private CommunityUsage? _usage;
    private readonly List<CommunitySupportRequest> _supportRequests = [];
    private readonly List<CommunityOperatorReport> _reports = [];
    private readonly Dictionary<Guid, string> _reviewDrafts = [];
    private readonly Dictionary<Guid, string> _replyDrafts = [];
    private readonly List<Control> _operationsControls = [];
    private readonly Dictionary<Control, Func<bool>> _operationsEnabled = [];
    private CommunityFeedCursor? _operationsCursor;
    private string _supportDraft = string.Empty;
    private string _supportCategory = "help";
    private bool _deletionPending;
    private bool _operationsLoaded;
    private ContentDialog? _operationsDialog;

    private void ResetOperationsIdentity()
    {
        _operationsPage = AccountToolsPage.None;
        _operationsCapabilities = CommunityOperationsCapabilities.Unavailable;
        _operationsCheckedUser = null;
        _usage = null;
        _supportRequests.Clear(); _reports.Clear();
        _reviewDrafts.Clear(); _replyDrafts.Clear();
        _supportDraft = string.Empty; _supportCategory = "help";
        _deletionPending = false; _operationsLoaded = false; _operationsCursor = null;
    }

    private async Task CheckOperationsCapabilitiesAsync()
    {
        if (!_active || _disposed || _busy || !_accounts.IsSignedIn || _operationsCheckedUser == _accounts.User?.Id) return;
        await RunAsync(async token =>
        {
            // An older backend simply keeps optional tools hidden. No token claim
            // or locally saved role grants access to these controls.
            try { _operationsCapabilities = await _accounts.GetOperationsCapabilitiesAsync(token); }
            catch (CommunityAccountException exception) when (exception.Code == "unavailable")
            { _operationsCapabilities = CommunityOperationsCapabilities.Unavailable; }
            token.ThrowIfCancellationRequested();
            _operationsCheckedUser = _accounts.User?.Id;
            if (!_operationsCapabilities.Enabled) _operationsPage = AccountToolsPage.None;
        });
        if (_active && !_operationsCapabilities.Enabled) await ActivateFeedAsync();
    }

    public async Task<bool> CheckUploadCapacityAsync(CancellationToken cancellationToken = default)
    {
        if (!await ConfirmDiscardProfileAsync()) return false;
        if (_editingProfile) { _editingProfile = false; Render(); }
        if (!_operationsCapabilities.Enabled) return true;
        var allowed = false;
        await RunAsync(async token =>
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, cancellationToken);
            _usage = await _accounts.GetUsageAsync(linked.Token);
            allowed = !_usage.UploadsFull;
            if (!allowed) _operationsPage = AccountToolsPage.Usage;
        });
        if (_usage?.UploadWarning is { Length: > 0 } warning && _active)
            ShowNotice(warning, InfoBarSeverity.Warning);
        return allowed;
    }

    private async void OnUsageClicked(object sender, RoutedEventArgs e) => await OpenAccountToolsAsync(AccountToolsPage.Usage);
    private async void OnHelpClicked(object sender, RoutedEventArgs e) => await OpenAccountToolsAsync(AccountToolsPage.Help);

    private async Task OpenAccountToolsAsync(AccountToolsPage page)
    {
        if (!_active || _disposed || _busy || _feedView?.IsUploading == true || !_operationsCapabilities.Enabled || !_accounts.IsSignedIn) return;
        if (page is AccountToolsPage.Reports or AccountToolsPage.Requests && _usage?.IsOperator != true) return;
        if (!await ConfirmDiscardProfileAsync()) return;
        AccountMenuButton.Flyout?.Hide();
        _feedView?.Deactivate();
        _editingProfile = false;
        _operationsPage = page; _operationsLoaded = false; _operationsCursor = null;
        _supportRequests.Clear(); _reports.Clear();
        Render();
        await LoadAccountToolsAsync();
    }

    private async Task LoadAccountToolsAsync(bool older = false)
    {
        var page = _operationsPage;
        if (page == AccountToolsPage.None) return;
        await RunAsync(async token =>
        {
            if (page == AccountToolsPage.Usage)
            {
                _usage = await _accounts.GetUsageAsync(token);
            }
            else if (page == AccountToolsPage.Reports)
            {
                var result = await _accounts.GetOperatorReportsPageAsync(older ? _operationsCursor : null, token);
                token.ThrowIfCancellationRequested();
                if (!older) _reports.Clear();
                _reports.AddRange(result.Items.Where(row => _reports.All(existing => existing.Id != row.Id)));
                _operationsCursor = result.NextCursor;
            }
            else
            {
                var result = await _accounts.GetSupportRequestsPageAsync(older ? _operationsCursor : null,
                    isOperator: page == AccountToolsPage.Requests, cancellationToken: token);
                token.ThrowIfCancellationRequested();
                if (!older) _supportRequests.Clear();
                _supportRequests.AddRange(result.Items.Where(row => _supportRequests.All(existing => existing.Id != row.Id)));
                _operationsCursor = result.NextCursor;
            }
            _operationsLoaded = true;
        });
    }

    private void RenderOperations()
    {
        if (OperationsHost is null) return;
        _operationsControls.Clear();
        _operationsEnabled.Clear();
        OperationsHost.Children.Clear();
        if (!_accounts.IsSignedIn || _operationsPage == AccountToolsPage.None) return;
        var navigation = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        navigation.Children.Add(ToolButton("Back to Media", async () =>
        {
            _operationsPage = AccountToolsPage.None; HideNotice(); Render(); await ActivateFeedAsync();
        }));
        navigation.Children.Add(ToolButton("Refresh", () => LoadAccountToolsAsync()));
        OperationsHost.Children.Add(navigation);
        OperationsHost.Children.Add(ToolText(_operationsPage switch
        {
            AccountToolsPage.Usage => "Storage & usage", AccountToolsPage.Help => "Help & requests",
            AccountToolsPage.Reports => "Reported clips", _ => "Private requests"
        }, heading: true));
        if (_operationsPage == AccountToolsPage.Usage) RenderUsage();
        else if (_operationsPage == AccountToolsPage.Help) RenderHelp();
        else if (_operationsPage == AccountToolsPage.Reports) RenderReports();
        else RenderSupportRequests(isOperator: true);
        if (_operationsCursor is not null && _operationsPage != AccountToolsPage.Usage)
            OperationsHost.Children.Add(ToolButton("Load older", () => LoadAccountToolsAsync(older: true)));
        if (!_operationsLoaded)
            OperationsHost.Children.Add(ToolText("Choose Refresh to retry if account tools could not be loaded.", secondary: true));
        SetOperationsBusy(_busy);
    }

    private void RenderUsage()
    {
        OperationsHost.Children.Add(ToolText("Your shared clips and the free beta’s current capacity.", secondary: true));
        if (_usage is not { } usage) return;
        var own = ToolSection("Your clips");
        own.Children.Add(ToolText($"{usage.Me.SlotsUsed} of {usage.Me.SlotsLimit} upload slots in use"));
        own.Children.Add(UsageBar(usage.Me.SlotsUsed, usage.Me.SlotsLimit, "Your clip slots"));
        own.Children.Add(ToolText($"{usage.Me.Published} shared · {usage.Me.Uploading} uploading · {usage.Me.Deleting} awaiting removal", secondary: true));
        own.Children.Add(ToolText($"{ByteLabel(usage.Me.StoredBytes)} tracked media. Originals stay in your local Library.", secondary: true));
        AddToolSection(own);
        var beta = ToolSection("Beta capacity");
        beta.Children.Add(ToolText($"{usage.Beta.SlotsUsed} of {usage.Beta.SlotsLimit} clip slots reserved"));
        beta.Children.Add(UsageBar(usage.Beta.SlotsUsed, usage.Beta.SlotsLimit, "Beta clip slots"));
        beta.Children.Add(ToolText($"{ByteLabel(usage.Beta.StorageBytes)} tracked media · {ByteLabel(usage.Beta.ReservedBytes)} reserved", secondary: true));
        if (usage.Beta.UnknownStorageObjects > 0)
            beta.Children.Add(ToolNotice("Some legacy file sizes are unavailable. Tracked media totals may be incomplete."));
        beta.Children.Add(ToolText($"The app’s {ByteLabel(usage.Beta.StorageLimitBytes)} media budget follows clip reservations. It is not a measurement of free space on the server’s SSD.", secondary: true));
        beta.Children.Add(ToolText($"Uploads: up to {usage.MaximumDurationSeconds:0} seconds / {ByteLabel(usage.MaximumVideoBytes)} per video. Unfinished uploads retain their slot until cleanup finishes.", secondary: true));
        if (usage.UploadWarning.Length > 0) beta.Children.Add(ToolNotice(usage.UploadWarning));
        beta.Children.Add(ToolText($"Checked {usage.CheckedAt.ToLocalTime():g}", secondary: true));
        AddToolSection(beta);
        if (usage.IsOperator && usage.Operations is { } operations)
        {
            var administration = ToolSection("Administration");
            administration.Children.Add(ToolText($"{operations.RegisteredUsers} accounts · {operations.OpenReports} open reports · {operations.OpenSupport} open requests"));
            administration.Children.Add(ToolText($"Database: {ByteLabel(operations.DatabaseBytes)} of {ByteLabel(operations.DatabaseLimitBytes)}", secondary: true));
            administration.Children.Add(UsageBar(operations.DatabaseBytes, operations.DatabaseLimitBytes, "Database capacity"));
            if (operations.DatabaseBytes * 100m >= operations.DatabaseLimitBytes * usage.WarningPercent)
                administration.Children.Add(ToolNotice("The database is nearing its free allowance. Review provider usage before inviting more members."));
            var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            actions.Children.Add(ToolButton("Review reports", () => OpenAccountToolsAsync(AccountToolsPage.Reports)));
            actions.Children.Add(ToolButton("Private requests", () => OpenAccountToolsAsync(AccountToolsPage.Requests)));
            administration.Children.Add(actions);
            foreach (var (name, metric) in operations.ProviderUsage)
            {
                var label = name switch
                {
                    "cached_egress" => "Cached delivery", "uncached_egress" => "Uncached delivery", "monthly_active_users" => "Monthly active users",
                    "email_daily" => "Daily emails", "email_monthly" => "Monthly emails", _ => name
                };
                var current = metric.IsCurrent(DateTimeOffset.UtcNow);
                var unitBytes = name.EndsWith("egress", StringComparison.Ordinal);
                var measured = current ? $"{(unitBytes ? ByteLabel(metric.Used!.Value) : metric.Used!.Value.ToString("N0", CultureInfo.CurrentCulture))} of {(unitBytes ? ByteLabel(metric.Limit) : metric.Limit.ToString("N0", CultureInfo.CurrentCulture))}" : "Not measured recently";
                administration.Children.Add(ToolText($"{label}: {measured}", secondary: true));
                if (current && metric.Used!.Value * 100m >= metric.Limit * usage.WarningPercent)
                    administration.Children.Add(ToolNotice($"{label} is nearing its free allowance. Review provider usage before inviting more members."));
            }
            administration.Children.Add(ToolText("Clip slots do not measure email or provider traffic. Unknown measurements are never treated as zero.", secondary: true));
            AddToolSection(administration);
        }
    }

    private async Task PreviewReportedClipAsync(CommunityOperatorReport report)
    {
        if (report.Hidden || _usage?.IsOperator != true || _operationsDialog is not null) return;
        await RunAsync(async token =>
        {
            // Review uses ordinary visibility checks and a short-lived capability;
            // there is deliberately no moderator bypass for hidden media.
            var uri = await _accounts.GetClipPlaybackUriAsync(report.ClipId, token);
            token.ThrowIfCancellationRequested();
            using var source = MediaSource.CreateFromUri(uri);
            using var player = new MediaPlayer { Source = source, AutoPlay = false };
            var element = new MediaPlayerElement { AreTransportControlsEnabled = true, Height = 310, MinWidth = 400, Stretch = Stretch.Uniform };
            element.SetMediaPlayer(player);
            var dialog = AccessibleDialogFactory.Create(XamlRoot, report.Title, element, closeButtonText: "Done");
            _operationsDialog = dialog;
            BusyChanged?.Invoke(this, EventArgs.Empty);
            try { player.Play(); await dialog.ShowAsync(); }
            finally
            {
                player.Pause(); element.SetMediaPlayer(null); _operationsDialog = null;
                BusyChanged?.Invoke(this, EventArgs.Empty);
            }
        });
    }

    private void RenderHelp()
    {
        var compose = ToolSection("Send a private request");
        compose.Children.Add(ToolText("Only you and Klipp’s moderators can read these messages. Don’t include passwords or sign-in codes.", secondary: true));
        var category = Track(new ComboBox { Header = "Topic", HorizontalAlignment = HorizontalAlignment.Stretch });
        foreach (var (tag, label) in new[] { ("help", "Help"), ("privacy", "Privacy"), ("deletion", "Deletion request") })
            category.Items.Add(new ComboBoxItem { Content = label, Tag = tag });
        category.SelectedIndex = _supportCategory == "privacy" ? 1 : _supportCategory == "deletion" ? 2 : 0;
        category.SelectionChanged += (_, _) => _supportCategory = (string)((ComboBoxItem)category.SelectedItem).Tag;
        compose.Children.Add(category);
        var message = ToolInput("Message", _supportDraft, 2000);
        message.TextChanged += (_, _) => _supportDraft = message.Text;
        compose.Children.Add(message);
        var send = ToolButton("Send private request", async () =>
        {
            var body = _supportDraft; var topic = _supportCategory;
            await RunAsync(async token =>
            {
                var saved = await _accounts.SubmitSupportRequestAsync(topic, body, token);
                token.ThrowIfCancellationRequested();
                _supportRequests.Insert(0, saved); _supportDraft = string.Empty;
            }, "Your private request was sent.");
        }, primary: true);
        send.IsEnabled = !_busy && _supportDraft.Trim().Length >= 10;
        _operationsEnabled[send] = () => message.Text.Trim().Length >= 10;
        message.TextChanged += (_, _) => send.IsEnabled = !_busy && message.Text.Trim().Length >= 10;
        compose.Children.Add(send);
        AddToolSection(compose);
        RenderSupportRequests(isOperator: false);
        if (_operationsCapabilities.AccountDeletionEnabled)
        {
            var deletion = ToolSection(_deletionPending ? "Account cleanup in progress" : "Delete account");
            deletion.Children.Add(ToolText(_deletionPending
                ? "Your account is awaiting cleanup. Retry to finish removing shared files and the account. Your sign-in is kept until the server confirms completion."
                : "Permanently remove your account, shared clips and interactions. Original recordings and projects on your devices stay there.", secondary: true));
            deletion.Children.Add(ToolButton(_deletionPending ? "Retry account deletion" : "Delete my account", ConfirmAccountDeletionAsync, danger: true));
            AddToolSection(deletion);
        }
    }

    private void RenderSupportRequests(bool isOperator)
    {
        if (_supportRequests.Count == 0 && _operationsLoaded)
            OperationsHost.Children.Add(ToolText(isOperator ? "No private requests to review." : "No requests yet.", secondary: true));
        foreach (var request in _supportRequests)
        {
            var card = ToolSection($"{(isOperator ? "@" + (request.Username ?? "member") + " · " : "")}{request.Category} · {request.Status}");
            card.Children.Add(ToolText(request.Body));
            card.Children.Add(ToolText(request.CreatedAt.ToLocalTime().ToString("g"), secondary: true));
            if (!string.IsNullOrEmpty(request.Reply)) card.Children.Add(ToolText("Reply: " + request.Reply));
            else if (!isOperator) card.Children.Add(ToolText("Awaiting a reply", secondary: true));
            if (isOperator)
            {
                var reply = ToolInput("Private reply", _replyDrafts.GetValueOrDefault(request.Id, string.Empty), 2000);
                reply.TextChanged += (_, _) => _replyDrafts[request.Id] = reply.Text;
                card.Children.Add(reply);
                var resolve = ToolButton("Reply & resolve", async () =>
                {
                    await RunAsync(async token =>
                    {
                        var updated = await _accounts.ReplyToSupportAsync(request.Id, reply.Text, resolved: true, token);
                        token.ThrowIfCancellationRequested();
                        var index = _supportRequests.FindIndex(row => row.Id == request.Id);
                        if (index >= 0) _supportRequests[index] = updated;
                        _replyDrafts.Remove(request.Id);
                    }, "Private reply saved.");
                });
                resolve.IsEnabled = !_busy && reply.Text.Trim().Length > 0;
                _operationsEnabled[resolve] = () => reply.Text.Trim().Length > 0;
                reply.TextChanged += (_, _) => resolve.IsEnabled = !_busy && reply.Text.Trim().Length > 0;
                card.Children.Add(resolve);
            }
            AddToolSection(card);
        }
    }

    private void RenderReports()
    {
        OperationsHost.Children.Add(ToolText("Review the report and leave a note before keeping or hiding a clip. Moderation is checked by the server.", secondary: true));
        if (_reports.Count == 0 && _operationsLoaded) OperationsHost.Children.Add(ToolText("No reports to review.", secondary: true));
        foreach (var report in _reports)
        {
            var card = ToolSection(report.Title);
            card.Children.Add(ToolText($"@{report.OwnerUsername} · {report.Status}{(report.Hidden ? " · hidden" : "")}", secondary: true));
            card.Children.Add(ToolText(report.Reason));
            card.Children.Add(ToolText($"Reported by @{report.ReporterUsername} · {report.CreatedAt.ToLocalTime():g}", secondary: true));
            if (report.ReviewNote.Length > 0) card.Children.Add(ToolText("Review: " + report.ReviewNote, secondary: true));
            var note = ToolInput("Review note", _reviewDrafts.GetValueOrDefault(report.Id, string.Empty), 1000);
            note.TextChanged += (_, _) => _reviewDrafts[report.Id] = note.Text;
            card.Children.Add(note);
            var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            var view = ToolButton("View clip", () => PreviewReportedClipAsync(report));
            _operationsEnabled[view] = () => !report.Hidden;
            actions.Children.Add(view);
            if (report.Hidden) card.Children.Add(ToolText("Hidden clips cannot be played here.", secondary: true));
            foreach (var hide in new[] { false, true })
            {
                var review = ToolButton(hide ? "Hide clip" : "Keep clip", async () =>
                {
                    if (hide && !await ConfirmAccountActionAsync("Hide this clip?",
                        $"{report.Title}\n\nThis clip will be hidden from Media. Your review note is saved with the decision.", "Hide clip")) return;
                    var complete = false;
                    await RunAsync(async token =>
                    {
                        await _accounts.ReviewReportedClipAsync(report.ClipId, hide, note.Text, token);
                        token.ThrowIfCancellationRequested();
                        _reviewDrafts.Remove(report.Id); complete = true;
                    }, hide ? "Clip hidden from Media." : "Report reviewed; clip kept.");
                    if (complete) await LoadAccountToolsAsync();
                }, danger: hide);
                review.IsEnabled = !_busy && note.Text.Trim().Length > 0;
                _operationsEnabled[review] = () => note.Text.Trim().Length > 0;
                note.TextChanged += (_, _) => review.IsEnabled = !_busy && note.Text.Trim().Length > 0;
                actions.Children.Add(review);
            }
            card.Children.Add(actions);
            AddToolSection(card);
        }
    }

    private (string Username, string DisplayName, string Bio) CurrentProfileDraft() =>
        (UsernameInput.Text.Trim().ToLowerInvariant(), DisplayNameInput.Text.Trim(), BioInput.Text.Trim());

    private Task<bool> ConfirmDiscardProfileAsync()
    {
        if (!_active || _disposed || _busy || _operationsDialog is not null) return Task.FromResult(false);
        if (!_editingProfile || CurrentProfileDraft() == _profileBaseline) return Task.FromResult(true);
        return ConfirmAccountActionAsync("Discard profile changes?", "Your profile changes haven’t been saved. Discard them to continue.", "Discard changes", "Keep editing");
    }

    private async Task<bool> ConfirmAccountActionAsync(string title, string message, string primary, string close = "Cancel")
    {
        if (!_active || _disposed || _busy || _operationsDialog is not null) return false;
        var owner = _accounts.User?.Id;
        AccountMenuButton.Flyout?.Hide();
        var dialog = AccessibleDialogFactory.Create(XamlRoot, title, ToolText(message), primaryButtonText: primary,
            closeButtonText: close, defaultButton: ContentDialogButton.Close);
        _operationsDialog = dialog;
        BusyChanged?.Invoke(this, EventArgs.Empty);
        try
        {
            return await dialog.ShowAsync() == ContentDialogResult.Primary && _active && !_disposed && _accounts.User?.Id == owner;
        }
        finally { _operationsDialog = null; BusyChanged?.Invoke(this, EventArgs.Empty); }
    }

    private async Task ConfirmAccountDeletionAsync()
    {
        if (_busy || _operationsDialog is not null || !_operationsCapabilities.AccountDeletionEnabled || !_accounts.IsSignedIn) return;
        var expected = string.IsNullOrWhiteSpace(_accounts.Profile?.Username) ? "DELETE" : _accounts.Profile.Username;
        var content = new StackPanel { Spacing = 14 };
        content.Children.Add(ToolText("This permanently removes your account and shared content. Original recordings and editor projects remain on your devices."));
        var instruction = ToolText($"Type {expected} exactly to confirm.", secondary: true);
        content.Children.Add(instruction);
        var input = new TextBox { Header = "Current username", IsSpellCheckEnabled = false, IsTextPredictionEnabled = false, MaxLength = 24 };
        content.Children.Add(input);
        var error = ToolText(string.Empty, secondary: true); content.Children.Add(error);
        var dialog = AccessibleDialogFactory.Create(XamlRoot, _deletionPending ? "Finish deleting your account?" : "Delete your Klipp account?", content,
            primaryButtonText: "Delete account", closeButtonText: "Cancel", defaultButton: ContentDialogButton.Close);
        dialog.IsPrimaryButtonEnabled = false;
        dialog.Closing += (_, args) => args.Cancel = _busy && _active;
        input.TextChanged += (_, _) => dialog.IsPrimaryButtonEnabled = !_busy && input.Text == expected;
        dialog.PrimaryButtonClick += async (_, args) =>
        {
            var deferral = args.GetDeferral(); args.Cancel = true;
            input.IsEnabled = false; dialog.IsPrimaryButtonEnabled = false;
            var acknowledged = false;
            try
            {
                await RunAsync(async token =>
                {
                    try { await _accounts.DeleteAccountAsync(input.Text, token); acknowledged = true; }
                    catch (CommunityAccountException exception)
                    {
                        if (exception.Code == "deletion_pending") _deletionPending = true;
                        if (exception.Code == "confirmation_required")
                        {
                            expected = string.IsNullOrWhiteSpace(_accounts.Profile?.Username) ? "DELETE" : _accounts.Profile.Username;
                            instruction.Text = $"Type {expected} exactly to confirm.";
                            input.Text = string.Empty;
                        }
                        error.Text = exception.Message;
                        throw;
                    }
                });
                if (acknowledged && !_accounts.IsSignedIn && !_accounts.HasPendingSignOut)
                {
                    args.Cancel = false;
                    ShowNotice("Your Klipp account and shared content were deleted. Your local recordings and projects are still here.", InfoBarSeverity.Success);
                }
                else if (!_accounts.IsSignedIn) args.Cancel = false;
            }
            finally
            {
                input.IsEnabled = true;
                dialog.IsPrimaryButtonEnabled = input.Text == expected; deferral.Complete();
            }
        };
        _operationsDialog = dialog;
        BusyChanged?.Invoke(this, EventArgs.Empty);
        try { await dialog.ShowAsync(); }
        finally { _operationsDialog = null; BusyChanged?.Invoke(this, EventArgs.Empty); }
    }

    private static string ByteLabel(long bytes) => bytes >= 1024L * 1024 * 1024
        ? $"{bytes / (1024d * 1024 * 1024):0.##} GiB" : $"{bytes / (1024d * 1024):0.##} MiB";
    private static TextBlock ToolText(string text, bool secondary = false, bool heading = false) => new()
    {
        Text = text, TextWrapping = TextWrapping.Wrap, FontSize = heading ? 20 : 13,
        FontWeight = heading ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal,
        Foreground = (Brush)Application.Current.Resources[secondary ? "KlippTextSecondaryBrush" : "KlippTextPrimaryBrush"]
    };
    private static StackPanel ToolSection(string title)
    {
        var section = new StackPanel { Spacing = 10 };
        var heading = ToolText(title); heading.FontSize = 16; heading.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
        section.Children.Add(heading); return section;
    }
    private void AddToolSection(StackPanel content) => OperationsHost.Children.Add(new Border
    {
        Background = (Brush)Application.Current.Resources["KlippSurfaceBrush"], BorderBrush = (Brush)Application.Current.Resources["KlippBorderSubtleBrush"],
        BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), Padding = new Thickness(20), Child = content
    });
    private static InfoBar ToolNotice(string message) => new() { IsOpen = true, IsClosable = false, Severity = InfoBarSeverity.Warning, Message = message };
    private static ProgressBar UsageBar(long used, long limit, string name)
    {
        var bar = new ProgressBar { Minimum = 0, Maximum = limit, Value = used, HorizontalAlignment = HorizontalAlignment.Stretch };
        AutomationProperties.SetName(bar, name); return bar;
    }
    private T Track<T>(T control) where T : Control { _operationsControls.Add(control); return control; }
    private TextBox ToolInput(string header, string value, int maximum) => Track(new TextBox
    {
        Header = header, Text = value, MaxLength = maximum, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap,
        MinHeight = 88, HorizontalAlignment = HorizontalAlignment.Stretch
    });
    private Button ToolButton(string label, Func<Task> action, bool primary = false, bool danger = false)
    {
        var button = Track(new Button { Content = label, MinHeight = 32, HorizontalAlignment = HorizontalAlignment.Left });
        if (primary) button.Style = (Style)Application.Current.Resources["KlippPrimaryButton"];
        if (danger) button.Foreground = (Brush)Application.Current.Resources["KlippDangerBrush"];
        button.Click += async (_, _) => { if (!_busy && _active && !_disposed) await action(); };
        return button;
    }
    private void SetOperationsBusy(bool busy)
    {
        foreach (var control in _operationsControls)
            control.IsEnabled = !busy && (!_operationsEnabled.TryGetValue(control, out var allowed) || allowed());
        UsageMenuButton.IsEnabled = !busy && _feedView?.IsUploading != true;
        HelpMenuButton.IsEnabled = UsageMenuButton.IsEnabled;
    }
}
