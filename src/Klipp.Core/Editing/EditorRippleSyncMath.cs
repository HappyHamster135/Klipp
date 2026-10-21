namespace Klipp.Core.Editing;

public sealed record EditorRippleTrackState(string Id, bool SyncLocked, bool Locked);
public sealed record EditorRippleSpan(string TrackId, double Start, double End);
public sealed record EditorRippleSourceSpan(string Id, string TrackId, string SourceClipId, bool IsVideo,
    double TimelineStart, double Duration, double TrimStart, double TrimEnd, double SourceDuration, double Speed);
public sealed record EditorRippleTrimPlan(IReadOnlySet<string> TrackIds, IReadOnlySet<string> TrimmedIds, double Delta);
public sealed record EditorRippleRemovalPlan(IReadOnlySet<string> TrackIds,
    IReadOnlyList<(double Start, double End)> Intervals)
{
    public double RemovedSeconds => Intervals.Sum(interval => interval.End - interval.Start);
}

/// <summary>One timeline time map for all participating tracks; never cuts or overlaps untouched media.</summary>
public static class EditorRippleSyncMath
{
    private const double Epsilon = .000001;

    public static IReadOnlySet<string> ParticipatingTracks(IEnumerable<EditorRippleTrackState> tracks,
        IEnumerable<string> editedTrackIds)
    {
        var edited = editedTrackIds.ToHashSet(StringComparer.Ordinal);
        return tracks.Where(track => !track.Locked && (track.SyncLocked || edited.Contains(track.Id)))
            .Select(track => track.Id).ToHashSet(StringComparer.Ordinal);
    }

    public static EditorRippleRemovalPlan PlanRemoval(IEnumerable<EditorRippleTrackState> tracks,
        IEnumerable<EditorRippleSpan> removed, IEnumerable<EditorRippleSpan> remaining, double frameRate)
    {
        var removals = removed.ToList();
        var participants = ParticipatingTracks(tracks, removals.Select(span => span.TrackId));
        var requested = removals.Where(span => participants.Contains(span.TrackId)).Select(span => (span.Start, span.End));
        var occupied = remaining.Where(span => participants.Contains(span.TrackId)).Select(span => (span.Start, span.End));
        var rate = SafeFrameRate(frameRate);
        var gaps = EditorAdvancedEditMath.SubtractCoveredIntervals(requested, occupied)
            // Round inward: fractional edges must never consume a neighbour's source frames.
            .Select(interval => (Start: Math.Ceiling(interval.Start * rate - Epsilon) / rate,
                End: Math.Floor(interval.End * rate + Epsilon) / rate))
            .Where(interval => interval.End - interval.Start > Epsilon).ToList();
        return new(participants, gaps);
    }

    public static double ClampTrimDelta(IEnumerable<EditorRippleTrackState> tracks, string editedTrackId,
        double originalEnd, double requestedDelta, IEnumerable<EditorRippleSpan> remaining, double frameRate)
    {
        if (!double.IsFinite(requestedDelta) || !double.IsFinite(originalEnd)) return 0;
        var participants = ParticipatingTracks(tracks, [editedTrackId]);
        if (!participants.Contains(editedTrackId)) return 0;
        var clips = remaining.Where(span => participants.Contains(span.TrackId) &&
            double.IsFinite(span.Start) && double.IsFinite(span.End) && span.End > span.Start).ToList();
        // A clip spanning the edit point cannot follow without an implicit cut.
        if (clips.Any(span => span.Start < originalEnd - Epsilon && span.End > originalEnd + Epsilon))
            return 0;
        var minimum = double.NegativeInfinity;
        foreach (var track in clips.GroupBy(span => span.TrackId))
        {
            var followingStart = track.Where(span => span.Start >= originalEnd - Epsilon)
                .Select(span => span.Start).DefaultIfEmpty(double.PositiveInfinity).Min();
            var stationaryEnd = track.Where(span => span.End <= originalEnd + Epsilon)
                .Select(span => span.End).DefaultIfEmpty(0).Max();
            minimum = Math.Max(minimum, Math.Min(0, stationaryEnd - followingStart));
        }
        var delta = Math.Max(requestedDelta, minimum);
        var rate = SafeFrameRate(frameRate);
        return delta < 0 ? Math.Ceiling(delta * rate - Epsilon) / rate : Math.Floor(delta * rate + Epsilon) / rate;
    }

    public static EditorRippleTrimPlan PlanSourceTrim(IEnumerable<EditorRippleTrackState> tracks, string editedId,
        double requestedDelta, bool leading, IEnumerable<EditorRippleSourceSpan> sourceSpans, double frameRate)
    {
        var rows = tracks.ToList();
        var spans = sourceSpans.ToList();
        var edited = spans.FirstOrDefault(span => span.Id == editedId);
        if (edited is null) return new(new HashSet<string>(), new HashSet<string>(), 0);
        var participants = ParticipatingTracks(rows, [edited.TrackId]);
        bool Aligned(EditorRippleSourceSpan other) =>
            Math.Abs(other.TimelineStart - edited.TimelineStart) < Epsilon &&
            Math.Abs(other.Duration - edited.Duration) < Epsilon &&
            Math.Abs(other.Speed - edited.Speed) < Epsilon &&
            Math.Abs(other.TrimStart - edited.TrimStart) < Epsilon &&
            Math.Abs(other.TrimEnd - edited.TrimEnd) < Epsilon;
        var video = edited.IsVideo ? edited : spans.FirstOrDefault(span => span.IsVideo && span.Id == edited.SourceClipId && Aligned(span));
        var family = spans.Where(span => participants.Contains(span.TrackId) &&
            (span.Id == edited.Id || video is not null && Aligned(span) &&
                (span.Id == video.Id || !span.IsVideo && span.SourceClipId == video.Id))).ToList();
        var ids = family.Select(span => span.Id).ToHashSet(StringComparer.Ordinal);
        var minimum = family.Select(span => .1 / Math.Max(.25, span.Speed) - span.Duration).DefaultIfEmpty(0).Max();
        var maximum = family.Select(span => leading ? span.TrimStart / Math.Max(.25, span.Speed)
            : (span.SourceDuration - span.TrimEnd) / Math.Max(.25, span.Speed)).DefaultIfEmpty(0).Min();
        var bounded = Math.Clamp(requestedDelta, Math.Min(0, minimum), Math.Max(0, maximum));
        var delta = ClampTrimDelta(rows, edited.TrackId, edited.TimelineStart + edited.Duration, bounded,
            spans.Where(span => !ids.Contains(span.Id)).Select(span =>
                new EditorRippleSpan(span.TrackId, span.TimelineStart, span.TimelineStart + span.Duration)), frameRate);
        return new(participants, ids, delta);
    }

    private static double SafeFrameRate(double rate) => double.IsFinite(rate) && rate > 0 ? rate : 30;
}
