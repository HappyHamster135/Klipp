using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Klipp.Desktop.Models;

namespace Klipp.Desktop.Services;

public sealed record EditorProjectCollectionProgress(int CompletedFiles, int TotalFiles,
    long CopiedBytes, long TotalBytes, string FileName)
{
    public double Fraction => TotalBytes > 0 ? Math.Clamp((double)CopiedBytes / TotalBytes, 0, 1) :
        TotalFiles > 0 ? (double)CompletedFiles / TotalFiles : 1;
}

public sealed record EditorProjectCollectionResult(string ProjectPath, int FileCount, long TotalBytes);

public sealed class EditorProjectFilesService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public async Task<EditorProjectCollectionResult> CollectAsync(EditorProject project, string destinationDirectory,
        IProgress<EditorProjectCollectionProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        var snapshot = EditorProjectService.Clone(project);
        snapshot.Normalize();
        var references = References(snapshot).Where(item => !string.IsNullOrWhiteSpace(item.Path))
            .Select(item => (Path: Path.GetFullPath(item.Path), item.Folder))
            .DistinctBy(item => item.Path, StringComparer.OrdinalIgnoreCase).ToList();
        var missing = references.Where(item => !File.Exists(item.Path)).ToList();
        if (missing.Count > 0)
            throw new FileNotFoundException($"Relink {missing.Count} missing file(s) before collecting this project. " +
                string.Join(", ", missing.Take(3).Select(item => Path.GetFileName(item.Path))));

        var parent = Path.GetFullPath(destinationDirectory);
        if (!Directory.Exists(parent))
            throw new DirectoryNotFoundException("Choose an existing folder for the portable project.");
        var folderName = SafeName(snapshot.Name) + " Portable";
        var destination = Path.Combine(parent, folderName);
        for (var suffix = 2; Directory.Exists(destination) || File.Exists(destination); suffix++)
            destination = Path.Combine(parent, $"{folderName} ({suffix})");
        Directory.CreateDirectory(destination);
        var createdFiles = new List<string>();
        try
        {
            var replacements = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var totalBytes = references.Sum(item => new FileInfo(item.Path).Length);
            var copiedBytes = 0L;
            var completed = 0;
            foreach (var item in references)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var hash = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(item.Path)))[..8];
                var relative = Path.Combine("Media", item.Folder, hash + "-" + SafeName(Path.GetFileName(item.Path)));
                var target = Path.Combine(destination, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                createdFiles.Add(target);
                var copiedBeforeFile = copiedBytes;
                var timer = Stopwatch.StartNew();
                await using (var source = new FileStream(item.Path, FileMode.Open, FileAccess.Read,
                                 FileShare.ReadWrite | FileShare.Delete, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
                await using (var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write,
                                 FileShare.None, 128 * 1024, FileOptions.Asynchronous))
                {
                    var buffer = new byte[128 * 1024];
                    int read;
                    while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                    {
                        await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                        copiedBytes += read;
                        if (timer.ElapsedMilliseconds >= 100)
                        {
                            progress?.Report(new(completed, references.Count, copiedBytes, totalBytes, Path.GetFileName(item.Path)));
                            timer.Restart();
                        }
                    }
                }
                if (copiedBytes - copiedBeforeFile != new FileInfo(target).Length)
                    throw new IOException("A collected media file could not be verified.");
                replacements[item.Path] = relative.Replace('\\', '/');
                progress?.Report(new(++completed, references.Count, copiedBytes, totalBytes, Path.GetFileName(item.Path)));
            }

            RewriteReferences(snapshot, path => string.IsNullOrWhiteSpace(path) ? path : replacements[Path.GetFullPath(path)]);
            snapshot.Id = Guid.NewGuid().ToString("N");
            snapshot.UpdatedUtc = DateTime.UtcNow;
            var projectPath = Path.Combine(destination, SafeName(snapshot.Name) + ".klippproject");
            createdFiles.Add(projectPath);
            await File.WriteAllTextAsync(projectPath, JsonSerializer.Serialize(snapshot, JsonOptions), System.Text.Encoding.UTF8, cancellationToken)
                .ConfigureAwait(false);
            return new(projectPath, references.Count, copiedBytes);
        }
        catch
        {
            // Only files created by this operation are removed; source media is never moved.
            foreach (var path in createdFiles)
                try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            foreach (var directory in Directory.EnumerateDirectories(destination, "*", SearchOption.AllDirectories)
                         .OrderByDescending(path => path.Length))
                try { Directory.Delete(directory, recursive: false); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            try { Directory.Delete(destination, recursive: false); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            throw;
        }
    }

    public static void ResolveReferences(EditorProject project, string projectPath)
    {
        var root = Path.GetDirectoryName(Path.GetFullPath(projectPath))!;
        RewriteReferences(project, path => string.IsNullOrWhiteSpace(path) || Path.IsPathRooted(path)
            ? path : Path.GetFullPath(Path.Combine(root, path.Replace('/', Path.DirectorySeparatorChar))));
    }

    private static IEnumerable<(string Path, string Folder)> References(EditorProject project)
    {
        foreach (var clip in project.Clips ?? [])
        {
            yield return (clip.SourcePath, "Video");
            if (clip.VideoProcessing is { } processing)
            {
                yield return (processing.OriginalSourcePath, "Video");
                yield return (processing.BakedSourcePath, "Video");
            }
            yield return (clip.LutPath, "LUTs");
        }
        foreach (var audio in project.AudioTracks ?? [])
            yield return (audio.SourcePath, "Audio");
        foreach (var effect in project.EffectLayers ?? [])
            if (effect.Style is { } style)
                yield return (style.LutPath, "LUTs");
        foreach (var graphic in project.GraphicLayers ?? [])
            yield return (graphic.SourcePath, "Images");
        foreach (var text in (project.ExtraTextLayers ?? []).Concat(project.SubtitleLayers ?? []))
            yield return (text.FontFilePath, "Fonts");
        yield return (project.Settings.TextFontFilePath, "Fonts");
        yield return (project.Settings.SubtitleFontFilePath, "Fonts");
    }

    private static void RewriteReferences(EditorProject project, Func<string, string> map)
    {
        foreach (var clip in project.Clips ?? [])
        {
            clip.SourcePath = map(clip.SourcePath);
            if (clip.VideoProcessing is { } processing)
            {
                processing.OriginalSourcePath = map(processing.OriginalSourcePath);
                processing.BakedSourcePath = map(processing.BakedSourcePath);
            }
            clip.LutPath = map(clip.LutPath);
        }
        foreach (var audio in project.AudioTracks ?? [])
            audio.SourcePath = map(audio.SourcePath);
        foreach (var effect in project.EffectLayers ?? [])
            if (effect.Style is { } style)
                style.LutPath = map(style.LutPath);
        foreach (var graphic in project.GraphicLayers ?? [])
            graphic.SourcePath = map(graphic.SourcePath);
        foreach (var text in (project.ExtraTextLayers ?? []).Concat(project.SubtitleLayers ?? []))
            text.FontFilePath = map(text.FontFilePath);
        project.Settings ??= new EditorProjectSettings();
        project.Settings.TextFontFilePath = map(project.Settings.TextFontFilePath);
        project.Settings.SubtitleFontFilePath = map(project.Settings.SubtitleFontFilePath);
    }

    private static string SafeName(string value)
    {
        foreach (var invalid in Path.GetInvalidFileNameChars())
            value = value.Replace(invalid, '_');
        value = value.Trim().TrimEnd('.', ' ');
        var stem = value.Split('.')[0].ToUpperInvariant();
        if (stem is "CON" or "PRN" or "AUX" or "NUL" ||
            stem.Length == 4 && (stem.StartsWith("COM", StringComparison.Ordinal) || stem.StartsWith("LPT", StringComparison.Ordinal)) &&
            stem[3] is >= '1' and <= '9')
            value = "Project-" + value;
        return string.IsNullOrWhiteSpace(value) ? "Project" : value.Length > 120 ? value[..120] : value;
    }
}
