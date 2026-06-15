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
    public Task LoadFromDiskAsync()
    {
        Clips.Clear();

        var files = Directory.GetFiles(ClipsDirectory, "*.mp4")
                             .Select(path => new FileInfo(path))
                             .OrderByDescending(fi => fi.LastWriteTime)
                             .ToList();

        foreach (var file in files)
        {
            Clips.Add(CreateViewModelFromFile(file, isNew: false));
        }

        return Task.CompletedTask;
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

        Clips.Insert(0, CreateViewModelFromFile(file, isNew: true));
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
