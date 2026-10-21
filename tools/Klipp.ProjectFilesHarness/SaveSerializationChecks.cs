using Klipp.Desktop.Models;
using Klipp.Desktop.Services;
using System.Text.Json;

internal static class SaveSerializationChecks
{
    public static async Task<int> RunAsync(string root)
    {
        var directory = Path.Combine(root, "Concurrent saves");
        var firstService = new EditorProjectService(directory);
        var secondService = new EditorProjectService(directory);
        var source = Path.Combine(root, "concurrent-source.mp4");
        var path = firstService.GetAutosavePath(source);
        var project = new EditorProject { Name = "Old snapshot" };
        project.Clips = Enumerable.Range(0, 500).Select(index => new EditorProjectClip
        {
            Name = "Clip " + index, SourcePath = source, SourceDurationSeconds = 20, TrimEndSeconds = 20,
            TimelineStartSeconds = index * 20, Notes = new string('x', 512)
        }).ToList();
        var oldSave = firstService.SaveAutosaveAsync(EditorProjectService.Clone(project), source);
        project.Name = "Flushed latest snapshot";
        var flush = secondService.SaveAutosaveAsync(EditorProjectService.Clone(project), source);
        await Task.WhenAll(oldSave, flush);
        if ((await firstService.TryLoadAsync(path))?.Name != project.Name)
            throw new InvalidOperationException("A queued file-operation flush lost to an older in-flight save.");
        if (Directory.EnumerateFiles(directory, "*.tmp").Any())
            throw new InvalidOperationException("Concurrent saves left temporary files behind.");

        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        try
        {
            await firstService.SaveAutosaveAsync(new EditorProject { Name = "Cancelled overwrite" }, source, cancelled.Token);
            throw new InvalidOperationException("A cancelled save was not rejected.");
        }
        catch (OperationCanceledException) { }
        if ((await firstService.TryLoadAsync(path))?.Name != project.Name)
            throw new InvalidOperationException("A cancelled save changed the saved document.");
        await firstService.SaveAutosaveAsync(new EditorProject { Name = "Gate recovered" }, source);
        if ((await firstService.TryLoadAsync(path))?.Name != "Gate recovered")
            throw new InvalidOperationException("A cancelled save left the destination gate locked.");
        await using (var activeRead = EditorProjectService.OpenProjectReadStream(path))
        {
            await secondService.SaveAutosaveAsync(new EditorProject { Name = "Saved while reading" }, source);
            var readSnapshot = await JsonSerializer.DeserializeAsync<EditorProject>(activeRead);
            if (readSnapshot?.Name != "Gate recovered")
                throw new InvalidOperationException("Atomic replacement changed the contents of an already-open project read.");
            if ((await firstService.TryLoadAsync(path))?.Name != "Saved while reading")
                throw new InvalidOperationException("An open project reader blocked the concurrent save.");
        }
        return 6;
    }
}
