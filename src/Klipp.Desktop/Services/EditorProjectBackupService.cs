using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Klipp.Desktop.Models;

namespace Klipp.Desktop.Services;

public sealed class EditorProjectBackupInfo(string projectPath, DateTime createdUtc, string projectName)
{
    public string ProjectPath { get; set; } = projectPath;
    public DateTime CreatedUtc { get; set; } = createdUtc;
    public string ProjectName { get; set; } = projectName;
    public string Label => CreatedUtc.ToLocalTime().ToString("dd MMM yyyy · HH:mm:ss");
    public string Summary => ProjectName;
}

public sealed class EditorProjectBackupService
{
    public const int MaximumVersions = 30;
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true, WriteIndented = true };
    private readonly string _root;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public EditorProjectBackupService(string root) => _root = Path.GetFullPath(root);

    public async Task SavePreviousAsync(string projectPath, bool force = false, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(projectPath))
            return;
        EditorProject? previous;
        try
        {
            previous = JsonSerializer.Deserialize<EditorProject>(
                await File.ReadAllTextAsync(projectPath, cancellationToken).ConfigureAwait(false), JsonOptions);
        }
        catch (JsonException) { return; }
        if (previous is null)
            return;
        EditorProjectFilesService.ResolveReferences(previous, projectPath);
        await CreateSnapshotAsync(previous, force, cancellationToken).ConfigureAwait(false);
    }

    public async Task<EditorProjectBackupInfo?> CreateSnapshotAsync(EditorProject project,
        bool force = true, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var snapshot = EditorProjectService.Clone(project);
            snapshot.Normalize();
            var directory = GetDirectory(snapshot.Id);
            var latest = OwnedFiles(directory).OrderByDescending(path => path, StringComparer.Ordinal).FirstOrDefault();
            var updated = snapshot.UpdatedUtc;
            snapshot.UpdatedUtc = DateTime.MinValue;
            var hash = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(snapshot, JsonOptions)))[..12];
            snapshot.UpdatedUtc = updated;
            if (latest is not null && (Path.GetFileNameWithoutExtension(latest).EndsWith(hash, StringComparison.Ordinal) ||
                !force && DateTime.UtcNow - File.GetLastWriteTimeUtc(latest) < TimeSpan.FromMinutes(1)))
                return null;

            Directory.CreateDirectory(directory);
            var created = DateTime.UtcNow;
            var path = Path.Combine(directory, $"{created:yyyyMMdd-HHmmss-fffffff}-{hash}.klippbackup");
            var temporary = path + ".tmp";
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(snapshot, JsonOptions), System.Text.Encoding.UTF8, cancellationToken)
                .ConfigureAwait(false);
            File.Move(temporary, path);

            foreach (var expired in OwnedFiles(directory).OrderByDescending(file => file, StringComparer.Ordinal).Skip(MaximumVersions))
                File.Delete(expired);
            return new EditorProjectBackupInfo(path, created, snapshot.Name);
        }
        finally { _gate.Release(); }
    }

    public async Task<IReadOnlyList<EditorProjectBackupInfo>> ListAsync(string projectId,
        CancellationToken cancellationToken = default)
    {
        var versions = new List<EditorProjectBackupInfo>();
        foreach (var path in OwnedFiles(GetDirectory(projectId)).OrderByDescending(path => path, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var project = JsonSerializer.Deserialize<EditorProject>(
                    await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false), JsonOptions);
                if (project is not null)
                    versions.Add(new EditorProjectBackupInfo(path, File.GetLastWriteTimeUtc(path), project.Name));
            }
            catch (JsonException) { }
            catch (IOException) { }
        }
        return versions;
    }

    public IEnumerable<string> EnumerateBackupPaths() => Directory.Exists(_root)
        ? Directory.EnumerateDirectories(_root).SelectMany(OwnedFiles)
        : [];

    private string GetDirectory(string projectId) => Path.Combine(_root,
        Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(projectId)))[..24]);

    private static IEnumerable<string> OwnedFiles(string directory) => Directory.Exists(directory)
        ? Directory.EnumerateFiles(directory, "*.klippbackup", SearchOption.TopDirectoryOnly)
            .Where(path => System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileName(path),
                @"^\d{8}-\d{6}-\d{7}-[A-F0-9]{12}\.klippbackup$"))
        : [];
}
