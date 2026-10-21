namespace Klipp.Core.Editing;

public readonly record struct EditorSelectionTrimRange(
    double TimelineStartSeconds,
    double TrimStartSeconds,
    double TrimEndSeconds,
    double SourceDurationSeconds,
    double PlaybackSpeed);

/// <summary>Bounds a precision trim by every selected clip so their shared edit delta stays intact.</summary>
public static class EditorSelectionEditMath
{
    public static (double Delta, double? Guide) SnapGroupMove(double requestedDelta,
        IEnumerable<(double Start, double End)> selection, IEnumerable<double> candidates, double threshold)
    {
        var ranges = selection.Where(range => double.IsFinite(range.Start) && double.IsFinite(range.End) &&
            range.Start >= 0 && range.End >= range.Start).ToList();
        if (ranges.Count == 0 || !double.IsFinite(requestedDelta)) return (0, null);
        var minimum = -ranges.Min(range => range.Start);
        var delta = Math.Max(minimum, requestedDelta);
        var best = Math.Max(0, double.IsFinite(threshold) ? threshold : 0);
        double? guide = null;
        var adjustment = 0d;
        var targets = candidates.Where(candidate => double.IsFinite(candidate) && candidate >= 0)
            .Distinct().Order().ToArray();
        void TryEdge(double edge)
        {
            // Only the nearest target on either side can win. Large grouped
            // edits must not allocate or scan every edge/target pair per frame.
            var index = Array.BinarySearch(targets, edge + delta);
            if (index < 0) index = ~index;
            if (index > 0) TryCandidate(targets[index - 1], edge);
            if (index < targets.Length) TryCandidate(targets[index], edge);
        }
        void TryCandidate(double candidate, double edge)
        {
            var shift = candidate - (edge + delta);
            var distance = Math.Abs(shift);
            if (candidate < 0 || delta + shift < minimum - .0000001 || distance > best ||
                guide.HasValue && distance >= best) return;
            best = distance;
            guide = candidate;
            adjustment = shift;
        }
        foreach (var range in ranges) { TryEdge(range.Start); TryEdge(range.End); }
        return (Math.Max(minimum, delta + adjustment), guide);
    }

    public static double ClampTrimDelta(double requestedDelta, bool trimStart,
        IEnumerable<EditorSelectionTrimRange> selection, double minimumSourceDuration = .1)
    {
        if (!double.IsFinite(requestedDelta) || !double.IsFinite(minimumSourceDuration) || minimumSourceDuration <= 0)
            return 0;
        var minimum = double.NegativeInfinity;
        var maximum = double.PositiveInfinity;
        var count = 0;
        foreach (var clip in selection)
        {
            if (!double.IsFinite(clip.TimelineStartSeconds) || clip.TimelineStartSeconds < 0 ||
                !double.IsFinite(clip.TrimStartSeconds) || clip.TrimStartSeconds < 0 ||
                !double.IsFinite(clip.TrimEndSeconds) ||
                !double.IsFinite(clip.SourceDurationSeconds) || clip.TrimEndSeconds > clip.SourceDurationSeconds + .000001 ||
                !double.IsFinite(clip.PlaybackSpeed) || clip.PlaybackSpeed <= 0 ||
                clip.TrimEndSeconds - clip.TrimStartSeconds < minimumSourceDuration - .000001)
                return 0;
            count++;
            if (trimStart)
            {
                minimum = Math.Max(minimum, Math.Max(-clip.TimelineStartSeconds, -clip.TrimStartSeconds / clip.PlaybackSpeed));
                maximum = Math.Min(maximum,
                    (clip.TrimEndSeconds - clip.TrimStartSeconds - minimumSourceDuration) / clip.PlaybackSpeed);
            }
            else
            {
                minimum = Math.Max(minimum,
                    -(clip.TrimEndSeconds - clip.TrimStartSeconds - minimumSourceDuration) / clip.PlaybackSpeed);
                maximum = Math.Min(maximum, (clip.SourceDurationSeconds - clip.TrimEndSeconds) / clip.PlaybackSpeed);
            }
        }
        // Floating point source bounds can differ by a few ticks at the minimum clip length.
        if (count == 0 || minimum > maximum + .000001)
            return 0;
        return Math.Clamp(requestedDelta, Math.Min(0, minimum), Math.Max(0, maximum));
    }
}
