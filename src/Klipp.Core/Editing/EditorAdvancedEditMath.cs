namespace Klipp.Core.Editing;

public static class EditorAdvancedEditMath
{
    public static (double LeftExtension, double RightExtension, double Overlap) PlanAudioCrossfadeHandles(
        double requestedOverlap, double leftTailSeconds, double rightHeadSeconds, double existingOverlap = 0)
    {
        requestedOverlap = double.IsFinite(requestedOverlap) ? Math.Max(0, requestedOverlap) : 0;
        leftTailSeconds = double.IsFinite(leftTailSeconds) ? Math.Max(0, leftTailSeconds) : 0;
        rightHeadSeconds = double.IsFinite(rightHeadSeconds) ? Math.Max(0, rightHeadSeconds) : 0;
        existingOverlap = double.IsFinite(existingOverlap) ? Math.Max(0, existingOverlap) : 0;
        var needed = Math.Max(0, requestedOverlap - existingOverlap);
        var left = Math.Min(needed / 2, leftTailSeconds);
        var right = Math.Min(needed / 2, rightHeadSeconds);
        var extraLeft = Math.Min(Math.Max(0, needed - left - right), leftTailSeconds - left);
        left += extraLeft;
        right += Math.Min(Math.Max(0, needed - left - right), rightHeadSeconds - right);
        return (left, right, existingOverlap + left + right);
    }

    public static IReadOnlyList<int> SimplifyLinearSamples(IReadOnlyList<(double Time, double Value)> samples,
        IReadOnlySet<int>? protectedIndices = null, double tolerance = 1e-9)
    {
        var kept = new List<int>();
        for (var index = 0; index < samples.Count; index++)
        {
            while (kept.Count >= 2)
            {
                var middle = kept[^1];
                if (protectedIndices?.Contains(middle) == true)
                    break;
                var left = samples[kept[^2]];
                var point = samples[middle];
                var right = samples[index];
                var length = right.Time - left.Time;
                if (length <= 0 || Math.Abs(point.Value - (left.Value + (right.Value - left.Value) *
                        (point.Time - left.Time) / length)) > tolerance)
                    break;
                kept.RemoveAt(kept.Count - 1);
            }
            kept.Add(index);
        }
        return kept;
    }

    public static double ClampGroupMove(double requestedDelta, IEnumerable<double> starts) =>
        Math.Max(-starts.DefaultIfEmpty(0).Min(), requestedDelta);

    public static int ClampGroupTrackMove(int requestedDelta, IReadOnlyList<int> selectedIndices,
        IReadOnlyList<bool> lockedTracks)
    {
        var indices = selectedIndices.Where(index => index >= 0 && index < lockedTracks.Count).ToList();
        if (indices.Count == 0 || indices.Any(index => lockedTracks[index]))
            return 0;
        var delta = Math.Clamp(requestedDelta, -indices.Min(), lockedTracks.Count - 1 - indices.Max());
        return indices.Any(index => lockedTracks[index + delta]) ? 0 : delta;
    }

    public static double RippleTrimFollowerStart(double originalStart, double affectedOriginalEnd, double durationDelta) =>
        originalStart >= affectedOriginalEnd - .0001 ? Math.Max(0, originalStart + durationDelta) : originalStart;

    public static (double Start, double End) SlipSourceRange(double trimStart, double trimEnd,
        double sourceDuration, double playbackSpeed, double pointerDelta)
    {
        var range = trimEnd - trimStart;
        var start = ClampSlipSourceStart(trimStart - pointerDelta * playbackSpeed, range, sourceDuration);
        return (start, start + range);
    }

    public static double ClampRollingDelta(double requestedDelta,
        double leftStart, double leftEnd, double leftSourceDuration, double leftSpeed,
        double rightStart, double rightEnd, double rightSpeed, double minimumSourceDuration = .1)
    {
        var minimum = Math.Max(-(leftEnd - leftStart - minimumSourceDuration) / leftSpeed, -rightStart / rightSpeed);
        var maximum = Math.Min((leftSourceDuration - leftEnd) / leftSpeed,
            (rightEnd - rightStart - minimumSourceDuration) / rightSpeed);
        return minimum > maximum ? 0 : Math.Clamp(requestedDelta, minimum, maximum);
    }

    public static double ClampSlipSourceStart(double requestedStart, double sourceRange, double sourceDuration) =>
        Math.Clamp(requestedStart, 0, Math.Max(0, sourceDuration - sourceRange));

    public static IReadOnlyList<(double Start, double End)> MergeIntervals(IEnumerable<(double Start, double End)> intervals)
    {
        var merged = new List<(double Start, double End)>();
        foreach (var interval in intervals.Where(interval => interval.End > interval.Start).OrderBy(interval => interval.Start))
        {
            if (merged.Count == 0 || interval.Start > merged[^1].End + .000001)
                merged.Add(interval);
            else
                merged[^1] = (merged[^1].Start, Math.Max(merged[^1].End, interval.End));
        }
        return merged;
    }

    public static double RippleStart(double originalStart, IEnumerable<(double Start, double End)> removed) =>
        Math.Max(0, originalStart - MergeIntervals(removed).Where(interval => interval.End <= originalStart + .000001)
            .Sum(interval => interval.End - interval.Start));

    public static IReadOnlyList<(double Start, double End)> SubtractCoveredIntervals(
        IEnumerable<(double Start, double End)> removed, IEnumerable<(double Start, double End)> remaining)
    {
        var covered = MergeIntervals(remaining);
        var empty = new List<(double Start, double End)>();
        foreach (var interval in MergeIntervals(removed))
        {
            var cursor = interval.Start;
            foreach (var occupied in covered)
            {
                if (occupied.End <= cursor)
                    continue;
                if (occupied.Start >= interval.End)
                    break;
                if (occupied.Start > cursor)
                    empty.Add((cursor, Math.Min(occupied.Start, interval.End)));
                cursor = Math.Max(cursor, Math.Min(interval.End, occupied.End));
                if (cursor >= interval.End)
                    break;
            }
            if (cursor < interval.End)
                empty.Add((cursor, interval.End));
        }
        return empty;
    }
}
