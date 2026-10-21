namespace Klipp.Core.Editing;

public readonly record struct EditorAudioPreviewSegment(string Id, double StartSeconds, double DurationSeconds, bool Enabled = true);

public sealed record EditorAudioPreviewPlan(IReadOnlySet<string> RequiredIds, IReadOnlySet<string> RetainedIds);

/// <summary>Keeps audio decoders near the cursor instead of opening every clip in a project.</summary>
public static class EditorAudioPreviewPlanner
{
    public static EditorAudioPreviewPlan Plan(IEnumerable<EditorAudioPreviewSegment> clips, double positionSeconds,
        double prepareAheadSeconds = 1.5, double retainBehindSeconds = .35)
    {
        var required = new HashSet<string>(StringComparer.Ordinal);
        var retained = new HashSet<string>(StringComparer.Ordinal);
        if (!double.IsFinite(positionSeconds) || !double.IsFinite(prepareAheadSeconds) || prepareAheadSeconds < 0 ||
            !double.IsFinite(retainBehindSeconds) || retainBehindSeconds < 0)
            return new(required, retained);
        var position = Math.Max(0, positionSeconds);
        foreach (var clip in clips)
        {
            if (string.IsNullOrEmpty(clip.Id) || !double.IsFinite(clip.StartSeconds) ||
                !double.IsFinite(clip.DurationSeconds) || clip.StartSeconds < 0 || clip.DurationSeconds <= 0)
                continue;
            var end = clip.StartSeconds + clip.DurationSeconds;
            if (!double.IsFinite(end) || clip.StartSeconds > position + prepareAheadSeconds)
                continue;
            if (end > position - retainBehindSeconds)
                retained.Add(clip.Id);
            // Muting/soloing should not close a nearby decoder that is already
            // warm, but muted tracks do not allocate a new decoder.
            if (clip.Enabled && end > position)
                required.Add(clip.Id);
        }
        return new(required, retained);
    }
}
