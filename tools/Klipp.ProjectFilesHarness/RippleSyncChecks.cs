using System.Text.Json;
using Klipp.Desktop.Models;
using Klipp.Desktop.Services;

internal static class RippleSyncChecks
{
    public static void Run()
    {
        static void Check(bool result, string message)
        {
            if (!result) throw new InvalidOperationException(message);
        }

        var legacy = JsonSerializer.Deserialize<EditorTimelineTrack>("{\"Id\":\"legacy\",\"Name\":\"A1\",\"Kind\":1}")!;
        Check(legacy.SyncLocked, "Legacy rows should join synchronized ripple edits by default.");
        legacy.SyncLocked = false;
        var before = legacy.Clone();
        legacy.SyncLocked = true;
        var redo = legacy.Clone();
        var restored = before.Clone();
        Check(!restored.SyncLocked && redo.SyncLocked, "Track snapshots lost the independent Sync lock state.");
        Check(!JsonSerializer.Deserialize<EditorTimelineTrack>(JsonSerializer.Serialize(restored))!.SyncLocked,
            "A disabled Sync lock did not survive saving and reopening.");
        Check(JsonSerializer.Deserialize<EditorTimelineTrack>(JsonSerializer.Serialize(redo))!.SyncLocked,
            "An enabled Sync lock did not survive saving and reopening.");

        var clipboard = new EditorTimelineClipboardService();
        var audio = new EditorProjectAudioTrack
        {
            Id = "source-audio", TrackId = legacy.Id, SourcePath = "voice.wav",
            SourceDurationSeconds = 4, TrimEndSeconds = 4
        };
        legacy.SyncLocked = false;
        Check(clipboard.Capture([], [audio], [legacy]), "Could not copy the source row.");
        Check(clipboard.TryCreatePaste([], 0, 60, out var plan, out var error), error);
        Check(plan!.AddedTracks.Count == 1 && !plan.AddedTracks[0].SyncLocked,
            "Pasting into a new row lost the source row's Sync lock setting.");
        var destination = new EditorTimelineTrack { Id = "destination", Kind = EditorTimelineTrackKind.Audio, SyncLocked = true };
        Check(clipboard.TryCreatePaste([destination], 0, 60, out plan, out error), error);
        Check(destination.SyncLocked && plan!.AddedTracks.Count == 0,
            "Pasting clips overwrote an existing row's Sync lock setting.");
        Console.WriteLine("PASS: legacy Sync lock defaults, independent snapshots, save/reopen, and clipboard row creation/reuse");
    }
}
