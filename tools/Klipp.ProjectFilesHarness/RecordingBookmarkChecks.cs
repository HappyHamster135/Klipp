using Klipp.Core.Models;
using Klipp.Desktop.Services;

internal static class RecordingBookmarkChecks
{
    public static async Task<int> RunAsync(string root)
    {
        var directory = Path.Combine(Path.GetFullPath(root), "Recording bookmark checks");
        Directory.CreateDirectory(directory);
        var checks = 0;
        void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
            checks++;
        }

        async Task<(string Source, string Ledger, RecordingBookmarkService Service)> Fixture(string name)
        {
            var folder = Path.Combine(directory, name);
            Directory.CreateDirectory(folder);
            var source = Path.Combine(folder, "recording.mp4");
            await File.WriteAllTextAsync(source, "synthetic recording payload");
            var ledger = Path.Combine(folder, "Bookmarks");
            return (source, ledger, new RecordingBookmarkService(ledger));
        }

        var normal = await Fixture("Save and load");
        await normal.Service.SaveAsync(normal.Source,
        [
            new("late", 20, " Later "), new("middle", 3, " First\r\nline "),
            new("start", -2, "  "), new("middle", 9, "Duplicate"),
            new("invalid", double.NaN, "Invalid")
        ], 12);
        var saved = await new RecordingBookmarkService(normal.Ledger).LoadAsync(normal.Source);
        Check(saved.Select(marker => marker.Id).SequenceEqual(["start", "middle", "late"]),
            "A newly opened bookmark service lost marker order, IDs or duplicate filtering.");
        Check(saved[0].TimeSeconds == 0 && saved[2].TimeSeconds == 12,
            "Saved recording highlights were not limited to the recording duration.");
        Check(saved[0].Label == "Highlight" && saved[1].Label == "First  line" && saved[2].Label == "Later",
            "Saved highlight labels were not cleaned for display.");
        Check(!Directory.EnumerateFiles(normal.Ledger, "*.tmp").Any() &&
              await File.ReadAllTextAsync(normal.Source) == "synthetic recording payload",
            "Saving highlights left partial metadata or changed the original media.");

        var changedTime = await Fixture("Replaced media timestamp");
        await changedTime.Service.SaveAsync(changedTime.Source, [new("old", 1, "Old recording")], 3);
        var previousWrite = File.GetLastWriteTimeUtc(changedTime.Source);
        File.SetLastWriteTimeUtc(changedTime.Source, previousWrite.AddSeconds(5));
        Check((await changedTime.Service.LoadAsync(changedTime.Source)).Count == 0,
            "Highlights for a previous recording were applied after its modification time changed.");

        var changedLength = await Fixture("Replaced media length");
        await changedLength.Service.SaveAsync(changedLength.Source, [new("old", 1, "Old recording")], 3);
        previousWrite = File.GetLastWriteTimeUtc(changedLength.Source);
        await File.AppendAllTextAsync(changedLength.Source, "replacement");
        File.SetLastWriteTimeUtc(changedLength.Source, previousWrite);
        Check((await changedLength.Service.LoadAsync(changedLength.Source)).Count == 0,
            "Highlights for a previous recording were applied after its length changed.");

        var rename = await Fixture("Renamed media");
        await rename.Service.SaveAsync(rename.Source, [new("rename", 2, "Keep this highlight")], 5);
        var renamedPath = Path.Combine(Path.GetDirectoryName(rename.Source)!, "renamed recording.mp4");
        File.Move(rename.Source, renamedPath);
        rename.Service.Move(rename.Source, renamedPath);
        var renamedMarkers = await new RecordingBookmarkService(rename.Ledger).LoadAsync(renamedPath);
        Check(renamedMarkers.Count == 1 && renamedMarkers[0] == new RecordingSessionMarker("rename", 2, "Keep this highlight"),
            "Renaming a recording lost its durable highlights.");
        Check((await rename.Service.LoadAsync(rename.Source)).Count == 0 && Directory.GetFiles(rename.Ledger, "*.json").Length == 1,
            "Renaming a recording left duplicate metadata at the old path.");

        var missing = await Fixture("Missing and malformed metadata");
        Check((await missing.Service.LoadAsync(missing.Source)).Count == 0,
            "A recording without a highlight ledger could not be opened.");
        await missing.Service.SaveAsync(missing.Source, [new("one", 1, "Highlight")], 5);
        var ledgerPath = Directory.GetFiles(missing.Ledger, "*.json").Single();
        await File.WriteAllTextAsync(ledgerPath, "{ malformed metadata");
        Check((await missing.Service.LoadAsync(missing.Source)).Count == 0,
            "Malformed highlight metadata prevented a recording from opening.");
        await missing.Service.SaveAsync(missing.Source, [new("one", 1, "Highlight")], 5);
        var ledgerJson = await File.ReadAllTextAsync(ledgerPath);
        await File.WriteAllTextAsync(ledgerPath, ledgerJson.Replace("\"Version\": 1", "\"Version\": 99", StringComparison.Ordinal));
        Check((await missing.Service.LoadAsync(missing.Source)).Count == 0,
            "An unsupported highlight ledger version was applied.");
        await missing.Service.SaveAsync(missing.Source, [new("one", 1, "Highlight")], 5);
        File.Delete(missing.Source);
        Check((await missing.Service.LoadAsync(missing.Source)).Count == 0,
            "A missing recording returned orphaned highlight metadata.");

        var editor = await Fixture("Editor marker import");
        await editor.Service.SaveAsync(editor.Source,
            [new("first", 2, "Opening highlight"), new("second", 7, "Later highlight")], 10);
        var projects = new EditorProjectService(Path.Combine(directory, "Projects"), editor.Service);
        var firstOpen = await projects.LoadOrCreateAsync(editor.Source, TimeSpan.FromSeconds(10));
        Check(firstOpen.Markers.Select(marker => marker.Id).SequenceEqual(["first", "second"]) &&
              firstOpen.Markers[0].TimeSeconds == 2 && firstOpen.Markers[0].Label == "Opening highlight" &&
              firstOpen.Markers.All(marker => marker.ColorLabel == "yellow" && marker.Kind == "manual"),
            "First opening a recording did not import its saved highlights as editable timeline markers.");
        firstOpen.Markers[0].TimeSeconds = 1.5;
        firstOpen.Markers[0].Label = "Edited in timeline";
        firstOpen.Markers.RemoveAt(1);
        await projects.SaveAutosaveAsync(firstOpen, editor.Source);
        // New capture metadata must not overwrite an editor document after the
        // user has moved or deleted its imported timeline markers.
        await editor.Service.SaveAsync(editor.Source,
            [new("first", 2, "Opening highlight"), new("second", 7, "Later highlight"), new("third", 9, "New metadata")], 10);
        var reopened = await new EditorProjectService(Path.Combine(directory, "Projects"), editor.Service)
            .LoadOrCreateAsync(editor.Source, TimeSpan.FromSeconds(10));
        Check(reopened.Markers.Count == 1 && reopened.Markers[0].Id == "first" &&
              reopened.Markers[0].TimeSeconds == 1.5 && reopened.Markers[0].Label == "Edited in timeline",
            "Reopening an autosaved edit replaced its modified or deleted markers with recording highlights.");
        reopened.Markers.Clear();
        await projects.SaveAutosaveAsync(reopened, editor.Source);
        Check((await projects.LoadOrCreateAsync(editor.Source, TimeSpan.FromSeconds(10))).Markers.Count == 0,
            "Deleting every imported marker caused capture highlights to return on the next open.");
        Check((await editor.Service.LoadAsync(editor.Source)).Count == 3,
            "Editing timeline markers changed the recording's independent highlight metadata.");
        return checks;
    }
}
