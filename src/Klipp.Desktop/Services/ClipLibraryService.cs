using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace Klipp.Desktop.Services;

/// <summary>
/// Hanterar listan av sparade klipp. Läser klipp-filer från disk, exponerar dem
/// som <see cref="ClipViewModel"/>, och låter andra delar av appen lägga till nya
/// klipp efterhand.
/// </summary>
/// <remarks>
/// Eftersom listan är observerbar (ObservableCollection) uppdateras UI automatiskt
/// när vi lägger till nya klipp — det är så ItemsRepeater fungerar med x:Bind.
/// </remarks>
public sealed class ClipLibraryService
{
    /// <summary>Mapp där klipp sparas. Skapas automatiskt om den inte finns.</summary>
    public string ClipsDirectory { get; }

    /// <summary>Observerbar lista av klipp. UI binder mot denna.</summary>
    public ObservableCollection<ClipViewModel> Clips { get; } = new();

    public ClipLibraryService()
    {
        // Spara till %USERPROFILE%\Videos\Klipp\
        var videos = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
        ClipsDirectory = Path.Combine(videos, "Klipp");
        Directory.CreateDirectory(ClipsDirectory);
    }

    /// <summary>
    /// Skannar klipp-mappen och laddar alla .raw-filer som ClipViewModel-objekt.
    /// Anropas vid app-uppstart.
    /// </summary>
    public Task LoadFromDiskAsync()
    {
        Clips.Clear();

        var files = Directory.GetFiles(ClipsDirectory, "*.raw")
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
    /// Genererar en unik sökväg för en ny inspelning, t.ex.
    /// "C:\Users\jon\Videos\Klipp\klipp_20260614_184530.raw".
    /// </summary>
    public string GenerateClipPath()
    {
        var fileName = $"klipp_{DateTime.Now:yyyyMMdd_HHmmss}.raw";
        return Path.Combine(ClipsDirectory, fileName);
    }

    /// <summary>
    /// Lägger till ett nytt klipp i listan (efter att inspelning sparats).
    /// Klippet markeras som "NY" och visas högst upp i bibliotek-grid:en.
    /// </summary>
    public void AddNewClip(string filePath)
    {
        if (!File.Exists(filePath)) return;

        var file = new FileInfo(filePath);

        // Avmarkera "NY" på alla existerande klipp först — bara senaste ska vara markerad
        foreach (var clip in Clips)
            clip.IsNew = false;

        // Lägg till nya klippet längst fram
        Clips.Insert(0, CreateViewModelFromFile(file, isNew: true));
    }

    private static ClipViewModel CreateViewModelFromFile(FileInfo file, bool isNew)
    {
        var sizeMb = file.Length / 1024.0 / 1024.0;
        var ageText = FormatAge(file.LastWriteTime);
        var durationText = TryReadDuration(file.FullName);

        return new ClipViewModel
        {
            Title = file.Name.Replace(".raw", "").Replace("klipp_", ""),
            Meta = $"{ageText} \u2022 {sizeMb:F1} MB",
            Duration = durationText,
            IsNew = isNew
        };
    }

    /// <summary>
    /// Försöker läsa varaktighet från filens KRAW-header. Returnerar "?:??" om filen
    /// inte är giltig eller om läsningen misslyckas — vi vill inte krascha hela
    /// biblioteket bara för en korrupt fil.
    /// </summary>
    private static string TryReadDuration(string filePath)
    {
        try
        {
            var metadata = Klipp.Encoding.Muxing.RawFileReader.ReadMetadata(filePath);
            return metadata.DurationText;
        }
        catch
        {
            return "?:??";
        }
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
