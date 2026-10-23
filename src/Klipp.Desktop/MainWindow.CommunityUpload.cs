using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Klipp.Desktop.Controls;
using Klipp.Desktop.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;

namespace Klipp.Desktop;

public sealed partial class MainWindow
{
    private bool _communityUploadActive;
    private CancellationTokenSource? _communityPreparation;

    private async void OnClipCardUploadRequested(object sender, ClipCardClickEventArgs e) =>
        await BeginCommunityUploadAsync(e.FilePath);

    private async Task BeginCommunityUploadAsync(string? sourcePath = null)
    {
        if (_communityUploadActive || _updateRestarting || _captureWindowClosing) return;
        _communityUploadActive = true;
        using var updateWork = BeginUpdateBlockingOperation();
        using var cancellation = new CancellationTokenSource();
        _communityPreparation = cancellation;
        PreparedCommunityClip? prepared = null;
        try
        {
            await ShowMediaAsync();
            var community = CommunityView;
            if (!community.IsSignedIn) return;
            if (!community.HasCompleteProfile)
            {
                await ShowMediaAsync(showProfile: true);
                return;
            }
            if (!await community.CheckUploadCapacityAsync(cancellation.Token)) return;
            if (sourcePath is null) sourcePath = await ChooseCommunityLibraryClipAsync();
            if (sourcePath is null || _captureWindowClosing) return;
            using var probeDeadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation.Token);
            probeDeadline.CancelAfter(TimeSpan.FromSeconds(30));
            MediaProbeInfo? info;
            try { info = await _mediaProbe.GetMediaInfoAsync(sourcePath, probeDeadline.Token); }
            catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
            {
                throw new InvalidOperationException("Reading this clip took too long. Try copying it to a local folder or exporting it from the editor first.");
            }
            if (info is null || info.Duration.TotalSeconds <= 0)
            {
                await ShowErrorAsync("Could not read clip", "Try exporting this clip from the editor first.");
                return;
            }
            var libraryClip = LibraryService.Clips.FirstOrDefault(clip =>
                string.Equals(clip.FilePath, sourcePath, StringComparison.OrdinalIgnoreCase));
            var choice = await ReviewCommunityClipAsync(sourcePath, libraryClip?.Title ?? Path.GetFileNameWithoutExtension(sourcePath), info);
            if (choice is null || _captureWindowClosing) return;
            var label = new TextBlock { Text = "Creating a smaller share copy on this device…", TextWrapping = TextWrapping.Wrap };
            var bar = new ProgressBar { Minimum = 0, Maximum = 100, Value = 0, MinWidth = 360 };
            AutomationProperties.SetName(bar, "Share copy preparation progress");
            var panel = new StackPanel { Spacing = 14 };
            panel.Children.Add(label);
            panel.Children.Add(bar);
            panel.Children.Add(new TextBlock { Text = "Your original clip stays unchanged.", TextWrapping = TextWrapping.Wrap });
            var progressDialog = AccessibleDialogFactory.Create(Content.XamlRoot, "Preparing your clip", panel,
                closeButtonText: "Cancel");
            var finishingPreparation = false;
            progressDialog.Closing += (_, _) =>
            {
                if (!finishingPreparation) cancellation.Cancel();
            };
            var shown = progressDialog.ShowAsync();
            try
            {
                var progress = new Progress<double>(value => bar.Value = Math.Clamp(value * 100, 0, 100));
                prepared = await new CommunityClipPreparationService().PrepareAsync(sourcePath,
                    choice.StartSeconds, choice.DurationSeconds, choice.AudioStreams, progress, cancellation.Token);
            }
            finally
            {
                finishingPreparation = true;
                progressDialog.Hide();
                await shown;
            }
            cancellation.Token.ThrowIfCancellationRequested();
            if (_captureWindowClosing) return;
            await community.UploadPreparedClipAsync(prepared.VideoPath, prepared.ThumbnailPath,
                choice.Title, choice.Description, prepared.DurationSeconds, cancellation.Token);
        }
        catch (OperationCanceledException) { }
        catch (Exception exception)
        {
            if (!_captureWindowClosing)
                await ShowErrorAsync("Could not upload clip", exception is InvalidOperationException
                    ? exception.Message : "The clip could not be prepared. Check your connection and try again.");
        }
        finally
        {
            prepared?.Dispose();
            _communityPreparation = null;
            _communityUploadActive = false;
        }
    }

    private async Task<string?> ChooseCommunityLibraryClipAsync()
    {
        var items = LibraryService.Clips.Where(clip => File.Exists(clip.FilePath)).ToArray();
        if (items.Length == 0)
        {
            await ShowErrorAsync("Your Library is empty", "Record or import a clip first, then choose Upload to Media from its menu.");
            return null;
        }
        var search = new TextBox { PlaceholderText = "Search your clips", MinWidth = 400 };
        AutomationProperties.SetName(search, "Search library clips to upload");
        var list = new ListView
        {
            ItemsSource = items, SelectionMode = ListViewSelectionMode.Single, MaxHeight = 300,
            ItemTemplate = (DataTemplate)XamlReader.Load("""
                <DataTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation">
                  <Grid ColumnSpacing="12" Padding="4,6">
                    <Grid.ColumnDefinitions><ColumnDefinition Width="88"/><ColumnDefinition Width="*"/></Grid.ColumnDefinitions>
                    <Image Source="{Binding ThumbnailPath}" Width="88" Height="50" Stretch="UniformToFill"/>
                    <StackPanel Grid.Column="1" VerticalAlignment="Center" Spacing="4">
                      <TextBlock Text="{Binding Title}" TextTrimming="CharacterEllipsis" MaxWidth="360"/>
                      <TextBlock Text="{Binding Duration}" FontSize="12" Opacity="0.7"/>
                    </StackPanel>
                  </Grid>
                </DataTemplate>
                """)
        };
        AutomationProperties.SetName(list, "Library clips to upload");
        var content = new StackPanel { Spacing = 12 };
        content.Children.Add(search);
        content.Children.Add(list);
        var dialog = AccessibleDialogFactory.Create(Content.XamlRoot, "Upload from Library", content,
            primaryButtonText: "Continue", closeButtonText: "Cancel", defaultButton: ContentDialogButton.Primary);
        dialog.IsPrimaryButtonEnabled = false;
        list.SelectionChanged += (_, _) => dialog.IsPrimaryButtonEnabled = list.SelectedItem is ClipViewModel;
        search.TextChanged += (_, _) => list.ItemsSource = items.Where(clip =>
            clip.Title.Contains(search.Text.Trim(), StringComparison.OrdinalIgnoreCase)).ToArray();
        return await dialog.ShowAsync() == ContentDialogResult.Primary && list.SelectedItem is ClipViewModel chosen
            ? chosen.FilePath : null;
    }

    private async Task<CommunityShareChoice?> ReviewCommunityClipAsync(string sourcePath, string initialTitle, MediaProbeInfo info)
    {
        using var review = new CommunityUploadReview(sourcePath, initialTitle, info);
        var dialog = AccessibleDialogFactory.Create(Content.XamlRoot, "Share a clip", review,
            primaryButtonText: "Create and upload", closeButtonText: "Cancel", defaultButton: ContentDialogButton.None,
            automationHelpText: "Review the selected video section, title, description, and audio before uploading a share copy.");
        dialog.Resources["ContentDialogMaxWidth"] = 1040d;
        review.UpdateViewport(Content.XamlRoot);
        dialog.IsPrimaryButtonEnabled = review.IsChoiceValid;
        review.ChoiceChanged += (_, _) => dialog.IsPrimaryButtonEnabled = review.IsChoiceValid;
        // Closing (including Escape) releases the decoder before preparation can
        // begin. The outer preparation token and temporary-file cleanup are unchanged.
        dialog.Closing += (_, _) => review.Dispose();
        return await dialog.ShowAsync() == ContentDialogResult.Primary && review.IsChoiceValid
            ? new(review.ClipTitle, review.Description, review.StartSeconds, review.DurationSeconds,
                review.SelectedAudioStreams) : null;
    }
    private sealed record CommunityShareChoice(string Title, string Description, double StartSeconds,
        double DurationSeconds, IReadOnlyList<int> AudioStreams);
}
