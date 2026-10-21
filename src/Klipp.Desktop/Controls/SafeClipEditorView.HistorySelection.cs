using System;
using System.Collections.Generic;
using System.Linq;
using Klipp.Desktop.Models;

namespace Klipp.Desktop.Controls;

public sealed partial class SafeClipEditorView
{
    private sealed record HistoryClipSelection(string Id, string SourcePath, string TrackId,
        double SourceStart, double SourceEnd, double TimelineStart)
    {
        public static HistoryClipSelection FromVideo(EditorProjectClip clip) =>
            new(clip.Id, clip.SourcePath, clip.TrackId, clip.TrimStartSeconds, clip.TrimEndSeconds, clip.TimelineStartSeconds);

        public static HistoryClipSelection FromAudio(EditorProjectAudioTrack clip) =>
            new(clip.Id, clip.SourcePath, clip.TrackId, clip.TrimStartSeconds, clip.TrimEndSeconds, clip.TimelineStartSeconds);
    }

    private static string? ResolveHistoryClipSelection(HistoryClipSelection? selected,
        IReadOnlyList<HistoryClipSelection> restored)
    {
        if (selected is null) return null;
        if (restored.Any(clip => clip.Id == selected.Id)) return selected.Id;
        // Undoing a split removes the selected right-half ID. Keep its original
        // unsplit parent selected instead of dropping the user's selection.
        return restored.Where(clip => clip.TrackId == selected.TrackId &&
                string.Equals(clip.SourcePath, selected.SourcePath, StringComparison.OrdinalIgnoreCase) &&
                clip.SourceStart <= selected.SourceStart + 0.001 && clip.SourceEnd >= selected.SourceEnd - 0.001)
            .OrderBy(clip => Math.Abs(clip.TimelineStart - selected.TimelineStart))
            .ThenBy(clip => clip.SourceEnd - clip.SourceStart)
            .Select(clip => clip.Id).FirstOrDefault();
    }
}
