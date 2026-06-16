using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace Klipp.Desktop.Services;

/// <summary>
/// Hanterar listan av sparade klipp. Läser MP4-filer från disk, exponerar dem
/// som ClipViewModel, och låter andra delar av appen lägga till nya klipp.
/// </summary>
public sealed class ClipLibraryService
{
    private readonly ThumbnailService _thumbnails = new();
    private readonly MediaProbeService _probe = new();  

    /// <summary>Mapp där klipp sparas. Skapas automatiskt om den inte finns.</summary>
    public string ClipsDirectory { get; }

    /// <summary>Observerbar lista av klipp. UI binder mot denna.</summary>
    public ObservableCollection<ClipViewModel> Clips { get; } = new();

    public ClipLibraryService()
    {
        var videos = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
        ClipsDirectory = Path.Combine(videos, "Klipp");
        Directory.CreateDirectory(ClipsDirectory);
    }

    /// <summary>
    /// Skannar klipp-mappen och laddar alla .mp4-filer som ClipViewModel-objekt.
    /// </summary>
    public async Task LoadFromDiskAsync()
    {
        Clips.Clear();

        var files = Directory.GetFiles(ClipsDirectory, "*.mp4")
                             .Select(path => new FileInfo(path))
                             .OrderByDescending(fi => fi.LastWriteTime)
                             .ToList();

        foreach (var file in files)
        {
            var vm = CreateViewModelFromFile(file, isNew: false);
            Clips.Add(vm);
        }

        // Generera thumbnails i bakgrunden — blockerar inte UI:t.
        // Korten uppdateras automatiskt via INotifyPropertyChanged när de blir klara.
        foreach (var vm in Clips)
        {
            await GenerateThumbnailAsync(vm);
            await GenerateDurationAsync(vm);
        }
    }

    /// <summary>Genererar en thumbnail för ett klipp och uppdaterar dess ViewModel.</summary>
    private async Task GenerateThumbnailAsync(ClipViewModel vm)
    {
        try
        {
            var thumbPath = await _thumbnails.GetOrCreateThumbnailAsync(vm.FilePath);
            if (!string.IsNullOrEmpty(thumbPath))
                vm.ThumbnailPath = thumbPath;
        }
        catch { /* fallback-ikon visas om det misslyckas */ }
    }

    /// <summary>Läser klippets varaktighet och uppdaterar dess ViewModel.</summary>
    private async Task GenerateDurationAsync(ClipViewModel vm)
    {
        try
        {
            var duration = await _probe.GetDurationAsync(vm.FilePath);
            if (duration is not null)
                vm.Duration = FormatDuration(duration.Value);
        }
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

    private static ClipViewModel CreateViewModelFromFile(FileInfo file, bool isNew)
    {
        var sizeMb = file.Length / 1024.0 / 1024.0;
        var ageText = FormatAge(file.LastWriteTime);

        return new ClipViewModel
        {
            Title = file.Name.Replace(".mp4", "").Replace("klipp_", ""),
            Meta = $"{ageText} \u2022 {sizeMb:F1} MB",
            Duration = "—",
            FilePath = file.FullName,
            ThumbnailPath = string.Empty,
            IsNew = isNew
        };
    }

    private static string FormatAge(DateTime when)
    {
        var diff = DateTime.Now - when;
        if (diff.TotalMinutes < 1) return "Just nu";
        if (diff.TotalMinutes < 60) return $"{(int)diff.TotalMinutes} min sedan";
        if (diff.TotalHours < 24) return $"{(int)diff.TotalHours} timmar sedan";
        if (diff.TotalDays < 7) return $"{(int)diff.TotalDays} dagar sedan";
        return when.ToString("d MMMM");
    }
}
