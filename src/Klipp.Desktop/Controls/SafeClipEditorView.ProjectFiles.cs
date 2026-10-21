using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Klipp.Desktop.Models;
using Klipp.Desktop.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Klipp.Desktop.Controls;

public sealed partial class SafeClipEditorView
{
    private readonly EditorProjectFilesService _projectFilesService = new();
    private CancellationTokenSource? _projectCollectionCts;
    public ObservableCollection<EditorProjectBackupInfo> ProjectVersions { get; } = [];
    public event EventHandler? ProjectFileOpenRequested;
    public event EventHandler? ProjectMediaCollectRequested;

    private void OnOpenProjectFileClicked(object sender, RoutedEventArgs e) =>
        ProjectFileOpenRequested?.Invoke(this, EventArgs.Empty);

    private void OnCollectProjectMediaClicked(object sender, RoutedEventArgs e)
    {
        if (_project is not null && _projectCollectionCts is null)
            ProjectMediaCollectRequested?.Invoke(this, EventArgs.Empty);
    }

    private async Task FlushProjectForFileOperationAsync()
    {
        CommitPendingProjectChange();
        if (_project is not null && !string.IsNullOrWhiteSpace(_projectSourcePath))
            await _projectService.SaveAutosaveAsync(EditorProjectService.Clone(_project), _projectSourcePath);
    }

    public async Task OpenProjectFileAsync(string projectPath)
    {
        var loadVersion = ++_projectLoadVersion;
        _previewProxyCts?.Cancel();
        _previewProxyCts = null;
        _isBuildingPreviewProxies = false;
        _previewProxyBuildQueued = false;
        _isLoadingClip = false;
        _suppressProjectChanges = false;
        var project = await _projectService.TryLoadAsync(projectPath) ??
            throw new InvalidDataException("This file could not be opened as a Klipp project.");
        if (loadVersion != _projectLoadVersion)
            return;
        await FlushProjectForFileOperationAsync();
        if (loadVersion != _projectLoadVersion)
            return;
        PausePreview();
        StopSourceViewer();
        var recovered = await _projectService.TryLoadAsync(_projectService.GetAutosavePath(projectPath));
        if (loadVersion != _projectLoadVersion)
            return;
        if (recovered?.Id == project.Id && recovered.UpdatedUtc > project.UpdatedUtc)
            project = recovered;
        await SeparateProjectSourceAudioAsync(project);
        if (loadVersion != _projectLoadVersion)
            return;
        _projectSourcePath = Path.GetFullPath(projectPath);
        _previewPosition = TimeSpan.Zero;
        _projectUndoHistory.Clear();
        _projectRedoHistory.Clear();
        RestoreProjectSnapshot(project);
        _syncingProjectName = true;
        try { TitleText.Text = project.Name; }
        finally { _syncingProjectName = false; }
        UpdatePreviewCanvas();
        RefreshPreviewAfterTimelineEdit();
        ProjectStatusText.Text = MissingMediaItems.Count > 0 ? "Project opened — relink missing media" : "Project opened";
        await RefreshProjectVersionsAsync();
    }

    public async Task CollectProjectMediaAsync(string destinationDirectory)
    {
        if (_project is null || _projectCollectionCts is not null)
            return;
        var document = _project;
        var cancellation = new CancellationTokenSource();
        _projectCollectionCts = cancellation;
        CollectProjectMediaButton.IsEnabled = false;
        CollectProjectMediaProgress.Value = 0;
        CollectProjectMediaProgress.Visibility = CancelCollectProjectMediaButton.Visibility = Visibility.Visible;
        OpenCollectedProjectFolderButton.Visibility = Visibility.Collapsed;
        ProjectFilesStatusText.Text = "Collecting project files…";
        try
        {
            await FlushProjectForFileOperationAsync();
            if (!ReferenceEquals(document, _project))
                return;
            var snapshot = EditorProjectService.Clone(document);
            var progress = new Progress<EditorProjectCollectionProgress>(value =>
            {
                if (!ReferenceEquals(_projectCollectionCts, cancellation))
                    return;
                CollectProjectMediaProgress.Value = value.Fraction * 100;
                ProjectFilesStatusText.Text = $"{value.CompletedFiles}/{value.TotalFiles} files · {value.FileName}";
            });
            var result = await _projectFilesService.CollectAsync(snapshot, destinationDirectory, progress, cancellation.Token);
            ProjectFilesStatusText.Text = $"Portable project created · {result.FileCount} files copied.";
            OpenCollectedProjectFolderButton.Tag = Path.GetDirectoryName(result.ProjectPath);
            OpenCollectedProjectFolderButton.Visibility = Visibility.Visible;
            ProjectStatusText.Text = "Portable project collected";
        }
        catch (OperationCanceledException) { ProjectFilesStatusText.Text = "Collection cancelled. Original files are unchanged."; }
        catch (Exception exception) { ProjectFilesStatusText.Text = exception.Message; }
        finally
        {
            if (ReferenceEquals(_projectCollectionCts, cancellation))
                _projectCollectionCts = null;
            cancellation.Dispose();
            CollectProjectMediaButton.IsEnabled = true;
            CollectProjectMediaProgress.Visibility = CancelCollectProjectMediaButton.Visibility = Visibility.Collapsed;
        }
    }

    private void OnCancelCollectProjectMediaClicked(object sender, RoutedEventArgs e) => _projectCollectionCts?.Cancel();
    private void OnProjectFilesUnloaded(object sender, RoutedEventArgs e) => _projectCollectionCts?.Cancel();

    private void OnOpenCollectedProjectFolderClicked(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string folder } || !Directory.Exists(folder))
            return;
        var start = new ProcessStartInfo("explorer.exe") { UseShellExecute = false };
        start.ArgumentList.Add(folder);
        Process.Start(start);
    }

    private async void OnProjectVersionsExpanding(Expander sender, ExpanderExpandingEventArgs args) =>
        await RefreshProjectVersionsAsync();

    private async void OnRefreshProjectVersionsClicked(object sender, RoutedEventArgs e) =>
        await RefreshProjectVersionsAsync();

    private async Task RefreshProjectVersionsAsync()
    {
        var projectId = _project?.Id;
        if (projectId is null)
            return;
        try
        {
            var versions = await _projectService.ListBackupsAsync(projectId);
            if (_project?.Id != projectId)
                return;
            ProjectVersions.Clear();
            foreach (var version in versions)
                ProjectVersions.Add(version);
            ProjectVersionsEmptyText.Visibility = versions.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        catch (Exception exception) { ProjectFilesStatusText.Text = exception.Message; }
    }

    private async void OnSaveProjectVersionClicked(object sender, RoutedEventArgs e)
    {
        if (_project is null)
            return;
        try
        {
            CommitPendingProjectChange();
            CaptureProjectFromUi();
            await _projectService.SaveVersionAsync(EditorProjectService.Clone(_project));
            ProjectFilesStatusText.Text = "Current version saved.";
            await RefreshProjectVersionsAsync();
        }
        catch (Exception exception) { ProjectFilesStatusText.Text = exception.Message; }
    }

    private async void OnRestoreProjectVersionClicked(object sender, RoutedEventArgs e)
    {
        if (_project is null || sender is not FrameworkElement { Tag: string path })
            return;
        var document = _project;
        var loadVersion = _projectLoadVersion;
        try
        {
            var previous = await _projectService.TryLoadAsync(path) ??
                throw new InvalidDataException("This saved version could not be read.");
            if (!ReferenceEquals(document, _project) || loadVersion != _projectLoadVersion)
                return;
            if (previous.Id != document.Id)
                throw new InvalidOperationException("This saved version belongs to a different project.");
            await FlushProjectForFileOperationAsync();
            if (!ReferenceEquals(document, _project) || loadVersion != _projectLoadVersion)
                return;
            var current = EditorProjectService.Clone(document);
            await _projectService.SaveVersionAsync(current);
            if (!ReferenceEquals(document, _project) || loadVersion != _projectLoadVersion)
                return;
            _projectUndoHistory.Add(current);
            _projectRedoHistory.Clear();
            PausePreview();
            RestoreProjectSnapshot(previous);
            _syncingProjectName = true;
            try { TitleText.Text = previous.Name; }
            finally { _syncingProjectName = false; }
            UpdatePreviewCanvas();
            ProjectFilesStatusText.Text = "Version restored. Undo returns to your previous edits.";
            ProjectStatusText.Text = "Saved version restored";
            await RefreshProjectVersionsAsync();
        }
        catch (Exception exception) { ProjectFilesStatusText.Text = exception.Message; }
    }
}
