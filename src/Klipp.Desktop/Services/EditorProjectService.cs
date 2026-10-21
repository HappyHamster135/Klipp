using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Klipp.Desktop.Models;

namespace Klipp.Desktop.Services;

public sealed class EditorProjectService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    private readonly string _projectsDirectory;
    private readonly EditorProjectBackupService _backups;
    private readonly RecordingBookmarkService _recordingBookmarks;
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> SaveGates = new(StringComparer.OrdinalIgnoreCase);

    public EditorProjectService(string? projectsDirectory = null, RecordingBookmarkService? recordingBookmarks = null)
    {
        _projectsDirectory = projectsDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Klipp",
            "Projects");
        _backups = new EditorProjectBackupService(Path.Combine(_projectsDirectory, "Backups"));
        _recordingBookmarks = recordingBookmarks ?? new RecordingBookmarkService();
    }

    public string GetAutosavePath(string sourcePath)
    {
        var normalized = Path.GetFullPath(sourcePath).ToUpperInvariant();
        var hash = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(normalized)))[..16];
        var safeName = MakeSafeFileName(Path.GetFileNameWithoutExtension(sourcePath));
        return Path.Combine(_projectsDirectory, $"{safeName}-{hash}.klippproject");
    }

    public async Task<EditorProject> LoadOrCreateAsync(
        string sourcePath,
        TimeSpan sourceDuration,
        CancellationToken cancellationToken = default)
    {
        var autosavePath = GetAutosavePath(sourcePath);
        var existing = await TryLoadAsync(autosavePath, cancellationToken).ConfigureAwait(false);
        if (existing is not null)
            return existing;

        var durationSeconds = Math.Max(0.1, sourceDuration.TotalSeconds);
        var project = new EditorProject
        {
            Name = Path.GetFileNameWithoutExtension(sourcePath),
            Settings = new EditorProjectSettings { PreviewQuality = "720p" },
            Clips =
            [
                new EditorProjectClip
                {
                    SourcePath = sourcePath,
                    Name = Path.GetFileNameWithoutExtension(sourcePath),
                    SourceDurationSeconds = durationSeconds,
                    TrimEndSeconds = durationSeconds,
                    Track = EditorClipTrack.Primary
                }
            ]
        };
        var highlights = await _recordingBookmarks.LoadAsync(sourcePath, cancellationToken).ConfigureAwait(false);
        project.Markers = highlights.Select(marker => new EditorProjectMarker
        {
            Id = marker.Id, TimeSeconds = Math.Clamp(marker.TimeSeconds, 0, durationSeconds),
            Label = marker.Label, ColorLabel = "yellow", Kind = "manual"
        }).ToList();
        return project;
    }

    public async Task<EditorProject?> TryLoadAsync(
        string projectPath,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(projectPath))
            return null;

        try
        {
            await using var stream = OpenProjectReadStream(projectPath);
            var project = await JsonSerializer.DeserializeAsync<EditorProject>(
                stream,
                JsonOptions,
                cancellationToken).ConfigureAwait(false);
            if (project is not null)
            {
                EditorProjectFilesService.ResolveReferences(project, projectPath);
                project.Normalize();
            }
            return project;
        }
        catch (JsonException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    public async Task SaveAutosaveAsync(
        EditorProject project,
        string sourcePath,
        CancellationToken cancellationToken = default)
    {
        await SaveAsync(project, GetAutosavePath(sourcePath), cancellationToken).ConfigureAwait(false);
    }

    public async Task<string> SaveNamedProjectAsync(
        EditorProject project,
        CancellationToken cancellationToken = default)
    {
        var path = Path.Combine(_projectsDirectory, $"{project.Id}.saved.klippproject");
        await SaveAsync(project, path, cancellationToken).ConfigureAwait(false);
        return path;
    }

    public async Task<IReadOnlyList<SavedEditorProjectInfo>> ListNamedProjectsAsync(
        CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(_projectsDirectory))
            return Array.Empty<SavedEditorProjectInfo>();

        var projects = new List<SavedEditorProjectInfo>();
        foreach (var path in Directory.EnumerateFiles(_projectsDirectory, "*.saved.klippproject"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var project = await TryLoadAsync(path, cancellationToken).ConfigureAwait(false);
            if (project is null)
                continue;

            projects.Add(new SavedEditorProjectInfo
            {
                ProjectPath = path,
                Name = project.Name,
                UpdatedUtc = project.UpdatedUtc,
                ClipCount = project.Clips.Count
            });
        }

        return projects.OrderByDescending(project => project.UpdatedUtc).ToList();
    }

    public async Task<IReadOnlySet<string>> GetReferencedMediaPathsAsync(
        CancellationToken cancellationToken = default)
    {
        var referencedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!Directory.Exists(_projectsDirectory))
            return referencedPaths;

        foreach (var projectPath in Directory.EnumerateFiles(
                     _projectsDirectory,
                     "*.klippproject",
                     SearchOption.TopDirectoryOnly).Concat(_backups.EnumerateBackupPaths()))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var project = await TryLoadAsync(projectPath, cancellationToken).ConfigureAwait(false);
            if (project is null)
                continue;

            foreach (var clip in project.Clips)
            {
                AddReferencedPath(referencedPaths, clip.SourcePath);
                if (clip.VideoProcessing is { } processing)
                {
                    AddReferencedPath(referencedPaths, processing.OriginalSourcePath);
                    AddReferencedPath(referencedPaths, processing.BakedSourcePath);
                }
                AddReferencedPath(referencedPaths, clip.LutPath);
            }
            foreach (var layer in project.GraphicLayers)
                AddReferencedPath(referencedPaths, layer.SourcePath);
            foreach (var effect in project.EffectLayers)
                if (effect.Style is { } style)
                    AddReferencedPath(referencedPaths, style.LutPath);
            foreach (var track in project.AudioTracks)
                AddReferencedPath(referencedPaths, track.SourcePath);
            foreach (var layer in project.ExtraTextLayers.Concat(project.SubtitleLayers))
                AddReferencedPath(referencedPaths, layer.FontFilePath);
            AddReferencedPath(referencedPaths, project.Settings.TextFontFilePath);
            AddReferencedPath(referencedPaths, project.Settings.SubtitleFontFilePath);
        }

        return referencedPaths;
    }

    public async Task SaveAsync(
        EditorProject project,
        string projectPath,
        CancellationToken cancellationToken = default)
    {
        projectPath = Path.GetFullPath(projectPath);
        var saveGate = SaveGates.GetOrAdd(projectPath, _ => new SemaphoreSlim(1, 1));
        await saveGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        string? tempPath = null;
        try
        {
            await _backups.SavePreviousAsync(projectPath,
                force: projectPath.EndsWith(".saved.klippproject", StringComparison.OrdinalIgnoreCase), cancellationToken)
                .ConfigureAwait(false);
            project.Normalize();
            project.UpdatedUtc = DateTime.UtcNow;
            Directory.CreateDirectory(Path.GetDirectoryName(projectPath)!);

            tempPath = projectPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            await using (var stream = new FileStream(
                tempPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                16 * 1024,
                FileOptions.Asynchronous))
            {
                await JsonSerializer.SerializeAsync(stream, project, JsonOptions, cancellationToken)
                    .ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            if (File.Exists(projectPath))
                File.Replace(tempPath, projectPath, destinationBackupFileName: null);
            else
                File.Move(tempPath, projectPath);
            tempPath = null;
        }
        finally
        {
            try { if (tempPath is not null && File.Exists(tempPath)) File.Delete(tempPath); }
            finally { saveGate.Release(); }
        }
    }

    public Task<IReadOnlyList<EditorProjectBackupInfo>> ListBackupsAsync(string projectId,
        CancellationToken cancellationToken = default) => _backups.ListAsync(projectId, cancellationToken);

    public Task<EditorProjectBackupInfo?> SaveVersionAsync(EditorProject project,
        CancellationToken cancellationToken = default) => _backups.CreateSnapshotAsync(project, force: true, cancellationToken);

    public static EditorProject Clone(EditorProject project)
    {
        var json = JsonSerializer.Serialize(project, JsonOptions);
        return JsonSerializer.Deserialize<EditorProject>(json, JsonOptions)
               ?? throw new InvalidOperationException("The project could not be copied.");
    }

    internal static FileStream OpenProjectReadStream(string projectPath) => new(projectPath,
        FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 16 * 1024,
        FileOptions.Asynchronous | FileOptions.SequentialScan);

    private static string MakeSafeFileName(string value)
    {
        foreach (var invalid in Path.GetInvalidFileNameChars())
            value = value.Replace(invalid, '_');
        return string.IsNullOrWhiteSpace(value) ? "project" : value;
    }

    private static void AddReferencedPath(ISet<string> paths, string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return;

        try
        {
            paths.Add(Path.GetFullPath(path));
        }
        catch
        {
            // Invalid legacy references are ignored instead of blocking retention inspection.
        }
    }
}
