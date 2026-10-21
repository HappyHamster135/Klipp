using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Klipp.Desktop.Services;

/// <summary>
/// Hanterar listan av sparade klipp. Läser MP4-filer från disk, exponerar dem
/// som ClipViewModel, och låter andra delar av appen lägga till nya klipp.
/// </summary>
public sealed class ClipLibraryService
{
    private const int FastMetadataCount = 18;
    private const int ActiveMetadataDelayMs = 160;
    private const int IdleMetadataDelayMs = 650;

    private readonly ThumbnailService _thumbnails = new();
    private readonly MediaProbeService _probe = new();
    private readonly MediaImportService _mediaImporter = new();
    private readonly ClipLibraryMetadataService _libraryMetadata = new();
    private CancellationTokenSource? _metadataCts;

    /// <summary>Mapp där klipp sparas. Skapas automatiskt om den inte finns.</summary>
    public string ClipsDirectory { get; private set; }

    /// <summary>Observerbar lista av klipp. UI binder mot denna.</summary>
    public ObservableCollection<ClipViewModel> Clips { get; } = new();

    public IReadOnlyList<string> Collections => _libraryMetadata.GetCollections();

    public ClipLibraryService()
    {
        ClipsDirectory = GetDefaultClipsDirectory();
        Directory.CreateDirectory(ClipsDirectory);
    }

    public static string GetDefaultClipsDirectory()
    {
        var videos = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
        return Path.Combine(videos, "Klipp");
    }

    public void SetClipsDirectory(string? directory)
    {
        var target = string.IsNullOrWhiteSpace(directory)
            ? GetDefaultClipsDirectory()
            : directory.Trim();

        target = Path.GetFullPath(Environment.ExpandEnvironmentVariables(target));
        Directory.CreateDirectory(target);
        ClipsDirectory = target;
    }

    /// <summary>
    /// Skannar klipp-mappen och laddar alla .mp4-filer som ClipViewModel-objekt.
    /// </summary>
    public Task LoadFromDiskAsync()
    {
        _metadataCts?.Cancel();
        var metadataCts = new CancellationTokenSource();
        _metadataCts = metadataCts;

        Clips.Clear();

        var files = Directory.EnumerateFiles(ClipsDirectory, "*.mp4")
                             .Select(path => new FileInfo(path))
                             .OrderByDescending(fi => fi.LastWriteTime)
                             .ToList();

        foreach (var file in files)
        {
            var vm = CreateViewModelFromFile(file, isNew: false);
            Clips.Add(vm);
        }

        _ = PopulateClipMetadataAsync(Clips.ToList(), metadataCts);
        return Task.CompletedTask;
    }

    public void PauseBackgroundMetadata()
    {
        _metadataCts?.Cancel();
    }

    public void ResumeBackgroundMetadata()
    {
        if (_metadataCts is not null)
            return;

        var pending = Clips
            .Where(clip => clip.Duration == "—" || string.IsNullOrWhiteSpace(clip.ThumbnailPath))
            .ToList();

        if (pending.Count == 0)
            return;

        var metadataCts = new CancellationTokenSource();
        _metadataCts = metadataCts;
        _ = PopulateClipMetadataAsync(pending, metadataCts);
    }

    private async Task PopulateClipMetadataAsync(IReadOnlyList<ClipViewModel> clips, CancellationTokenSource metadataCts)
    {
        var cancellationToken = metadataCts.Token;
        try
        {
            await Task.Yield();

            // Generera metadata löpande efter att biblioteket visats. En liten paus mellan
            // klippen hindrar ffmpeg/ffprobe från att konkurrera för hårt med capture.
            for (var index = 0; index < clips.Count; index++)
            {
                var vm = clips[index];
                cancellationToken.ThrowIfCancellationRequested();
                if (vm.Duration == "—")
                    await GenerateDurationAsync(vm, cancellationToken);

                cancellationToken.ThrowIfCancellationRequested();
                if (string.IsNullOrWhiteSpace(vm.ThumbnailPath))
                    await GenerateThumbnailAsync(vm, cancellationToken);

                var delay = index < FastMetadataCount ? ActiveMetadataDelayMs : IdleMetadataDelayMs;
                await Task.Delay(delay, cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            // En ny biblioteksladdning ersatte detta metadata-pass.
        }
        finally
        {
            if (ReferenceEquals(_metadataCts, metadataCts))
                _metadataCts = null;
            metadataCts.Dispose();
        }
    }

    /// <summary>Genererar en thumbnail för ett klipp och uppdaterar dess ViewModel.</summary>
    private async Task GenerateThumbnailAsync(ClipViewModel vm, CancellationToken cancellationToken = default)
    {
        try
        {
            var thumbPath = await _thumbnails.GetOrCreateThumbnailAsync(vm.FilePath, cancellationToken);
            if (!string.IsNullOrEmpty(thumbPath))
                vm.ThumbnailPath = thumbPath;
        }
        catch (OperationCanceledException) { throw; }
        catch { /* fallback-ikon visas om det misslyckas */ }
    }

    /// <summary>Läser klippets varaktighet och uppdaterar dess ViewModel.</summary>
    private async Task GenerateDurationAsync(ClipViewModel vm, CancellationToken cancellationToken = default)
    {
        try
        {
            var duration = await _probe.GetDurationAsync(vm.FilePath, cancellationToken);
            if (duration is not null)
                vm.Duration = FormatDuration(duration.Value);
        }
        catch (OperationCanceledException) { throw; }
        catch { /* behåll "—" om det misslyckas */ }
    }

    private static string FormatDuration(TimeSpan d)
    {
        var totalSeconds = (int)Math.Round(d.TotalSeconds);
        var minutes = totalSeconds / 60;
        var seconds = totalSeconds % 60;
        return $"{minutes}:{seconds:D2}";
    }

    /// <summary>
    /// Genererar en unik sökväg för en ny inspelning.
    /// </summary>
    public string GenerateClipPath()
    {
        var fileName = $"klipp_{DateTime.Now:yyyyMMdd_HHmmss}.mp4";
        return Path.Combine(ClipsDirectory, fileName);
    }

    public string GenerateEditedClipPath(string sourcePath, string suffix)
    {
        var directory = Path.GetDirectoryName(sourcePath);
        if (string.IsNullOrWhiteSpace(directory))
            directory = ClipsDirectory;

        var sourceName = Path.GetFileNameWithoutExtension(sourcePath);
        if (string.IsNullOrWhiteSpace(sourceName))
            sourceName = $"klipp_{DateTime.Now:yyyyMMdd_HHmmss}";

        var safeSuffix = SanitizeClipTitle(suffix);
        return GetUniqueClipPath(directory, $"{sourceName}_{safeSuffix}", currentPath: string.Empty);
    }

    /// <summary>
    /// Lägger till ett nytt klipp i listan efter att inspelning sparats.
    /// </summary>
    public void AddNewClip(string filePath)
    {
        if (!File.Exists(filePath)) return;

        var file = new FileInfo(filePath);

        // Avmarkera "NY" på alla existerande klipp
        foreach (var clip in Clips)
            clip.IsNew = false;

        var vm = CreateViewModelFromFile(file, isNew: true);
        Clips.Insert(0, vm);

        // Generera thumbnail + läs varaktighet i bakgrunden
        _ = GenerateThumbnailAsync(vm);
        _ = GenerateDurationAsync(vm);
    }

    public async Task<string> ImportClipAsync(string sourcePath, CancellationToken cancellationToken = default)
    {
        var result = await ImportClipWithDetailsAsync(sourcePath, progress: null, cancellationToken);
        return result.FilePath;
    }

    public async Task<ClipImportResult> ImportClipWithDetailsAsync(
        string sourcePath,
        IProgress<ClipImportProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var result = await _mediaImporter.ImportAsync(
                sourcePath,
                ClipsDirectory,
                progress,
                cancellationToken);
        if (!Clips.Any(clip =>
                string.Equals(clip.FilePath, result.FilePath, StringComparison.OrdinalIgnoreCase)))
        {
            AddNewClip(result.FilePath);
        }

        return result;
    }

    public string RenameClip(string currentPath, string title)
    {
        if (!File.Exists(currentPath))
            throw new FileNotFoundException("The clip no longer exists.", currentPath);

        var safeTitle = SanitizeClipTitle(title);
        var directory = Path.GetDirectoryName(currentPath) ?? ClipsDirectory;
        var targetPath = GetUniqueClipPath(directory, safeTitle, currentPath);

        if (string.Equals(currentPath, targetPath, StringComparison.OrdinalIgnoreCase))
            return currentPath;

        File.Move(currentPath, targetPath);
        _libraryMetadata.Move(currentPath, targetPath);
        try { new RecordingBookmarkService().Move(currentPath, targetPath); }
        catch (IOException exception) { System.Diagnostics.Debug.WriteLine($"[Klipp] Could not move highlight metadata: {exception.Message}"); }
        catch (UnauthorizedAccessException exception) { System.Diagnostics.Debug.WriteLine($"[Klipp] Could not move highlight metadata: {exception.Message}"); }

        var file = new FileInfo(targetPath);
        var vm = Clips.FirstOrDefault(clip =>
            string.Equals(clip.FilePath, currentPath, StringComparison.OrdinalIgnoreCase));
        if (vm is not null)
        {
            vm.FilePath = targetPath;
            vm.Title = GetDisplayTitle(file);
            vm.Meta = CreateMeta(file);
            vm.FileSizeBytes = file.Length;
            vm.LastWriteTime = file.LastWriteTime;
            ApplyMetadata(vm, _libraryMetadata.Get(targetPath));
        }

        return targetPath;
    }

    public ClipLibraryMetadata GetClipMetadata(string filePath) =>
        _libraryMetadata.Get(filePath);

    public ClipLibraryMetadata UpdateClipMetadata(
        string filePath,
        bool isFavorite,
        IEnumerable<string>? tags,
        string? collection)
    {
        var metadata = _libraryMetadata.Update(filePath, isFavorite, tags, collection);
        var clip = Clips.FirstOrDefault(item =>
            string.Equals(item.FilePath, filePath, StringComparison.OrdinalIgnoreCase));
        if (clip is not null)
            ApplyMetadata(clip, metadata);
        return metadata;
    }

    public ClipLibraryMetadata ToggleClipFavorite(string filePath)
    {
        var metadata = _libraryMetadata.ToggleFavorite(filePath);
        var clip = Clips.FirstOrDefault(item =>
            string.Equals(item.FilePath, filePath, StringComparison.OrdinalIgnoreCase));
        if (clip is not null)
            ApplyMetadata(clip, metadata);
        return metadata;
    }

    public void RemoveClipMetadata(string filePath) =>
        _libraryMetadata.Remove(filePath);

    private ClipViewModel CreateViewModelFromFile(FileInfo file, bool isNew)
    {
        var viewModel = new ClipViewModel
        {
            Title = GetDisplayTitle(file),
            Meta = CreateMeta(file),
            Duration = "—",
            FilePath = file.FullName,
            ThumbnailPath = string.Empty,
            FileSizeBytes = file.Length,
            LastWriteTime = file.LastWriteTime,
            IsNew = isNew
        };
        ApplyMetadata(viewModel, _libraryMetadata.Get(file.FullName));
        return viewModel;
    }

    private static void ApplyMetadata(ClipViewModel clip, ClipLibraryMetadata metadata)
    {
        clip.IsFavorite = metadata.IsFavorite;
        clip.TagsText = string.Join(", ", metadata.Tags);
        clip.Collection = metadata.Collection;
    }

    private static string GetDisplayTitle(FileInfo file)
    {
        var title = Path.GetFileNameWithoutExtension(file.Name);
        return title.StartsWith("klipp_", StringComparison.OrdinalIgnoreCase)
            ? title["klipp_".Length..]
            : title;
    }

    private static string CreateMeta(FileInfo file)
    {
        var sizeMb = file.Length / 1024.0 / 1024.0;
        var ageText = FormatAge(file.LastWriteTime);
        return $"{ageText} \u2022 {sizeMb:F1} MB";
    }

    private static string GetUniqueClipPath(string directory, string title, string currentPath)
    {
        var candidate = Path.Combine(directory, $"{title}.mp4");
        if (string.Equals(candidate, currentPath, StringComparison.OrdinalIgnoreCase))
            return currentPath;

        var suffix = 2;
        while (File.Exists(candidate))
        {
            candidate = Path.Combine(directory, $"{title} ({suffix}).mp4");
            suffix++;
        }

        return candidate;
    }

    private static string SanitizeClipTitle(string title)
    {
        var name = title.Trim();
        if (name.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase))
            name = name[..^4];

        var invalidChars = Path.GetInvalidFileNameChars();
        var chars = name
            .Select(ch => invalidChars.Contains(ch) || char.IsControl(ch) ? ' ' : ch)
            .ToArray();

        name = string.Join(" ", new string(chars)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .Trim('.', ' ');

        if (name.Length > 100)
            name = name[..100].Trim('.', ' ');

        if (string.IsNullOrWhiteSpace(name))
            throw new InvalidOperationException("Enter a name for the clip.");

        var reserved = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "CON", "PRN", "AUX", "NUL",
            "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
            "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
        };
        if (reserved.Contains(name))
            name = $"Klipp {name}";

        return name;
    }

    private static string FormatAge(DateTime when)
    {
        var diff = DateTime.Now - when;
        if (diff.TotalMinutes < 1) return "Just now";
        if (diff.TotalMinutes < 60) return $"{(int)diff.TotalMinutes} min ago";
        if (diff.TotalHours < 24) return $"{(int)diff.TotalHours} hr ago";
        if (diff.TotalDays < 7) return $"{(int)diff.TotalDays} day{((int)diff.TotalDays == 1 ? string.Empty : "s")} ago";
        return when.ToString("d MMMM", CultureInfo.InvariantCulture);
    }
}
